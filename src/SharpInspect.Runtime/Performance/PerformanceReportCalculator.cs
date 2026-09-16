using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Performance;

/// <summary>
/// Pure V1 recomputation of one sealed baseline capture against its frozen contract. The
/// calculator performs no IO, mutates no shared state and grants no production qualification.
/// Rules are exactly the declared V1 ones: nearest-rank over successful complete durations,
/// observed maximum-minus-minimum jitter, explicit failure/Unknown/NotApplicable counts, null
/// empty percentiles, warm-up ordinals retained but excluded from statistics, expected failures
/// remaining inside failure counts and maximum-failure gates, and identity reconciliation between
/// the verified durable ledger and the raw capture.
/// </summary>
public static class PerformanceReportCalculator
{
    /// <summary>Recomputes one scenario report. The captured raw evidence is never modified.</summary>
    public static PerformanceScenarioReport Calculate(PerformanceRawCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return new ScenarioCalculation(capture).Run();
    }

    /// <summary>
    /// Aggregates one report per frozen scenario id. Every frozen scenario must appear exactly
    /// once: missing scenarios are NotRun, duplicates fail without cherry-picking a passing run,
    /// and any single-run failure is retained.
    /// </summary>
    public static PerformanceAggregateReport Aggregate(PerformanceContract contract,
        IEnumerable<PerformanceScenarioReport> reports)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return ScenarioCalculation.Aggregate(contract, reports);
    }

    private sealed class FailureCollector
    {
        private readonly List<PerformanceFailure> _details = new();
        private readonly int _maximumDetails;

        internal FailureCollector(int maximumDetails) => _maximumDetails = maximumDetails;
        internal int Total { get; private set; }
        internal bool Truncated => Total > _details.Count;

        internal void Add(string code, string detail, int? ordinal = null, PerformanceSpan? span = null,
            PerformanceResource? resource = null)
        {
            Total++;
            if (_details.Count >= _maximumDetails) return;
            _details.Add(new(code, SanitizeDetail(detail), ordinal, span, resource));
        }

        internal ReadOnlyCollection<PerformanceFailure> Snapshot() => _details.AsReadOnly();

        private static string SanitizeDetail(string detail)
        {
            var builder = new StringBuilder(Math.Min(detail.Length, 480));
            foreach (var character in detail)
            {
                if (builder.Length >= 480) break;
                if (char.IsControl(character) || char.IsSurrogate(character)) continue;
                builder.Append(character);
            }

            return builder.Length == 0 ? "DetailUnavailable" : builder.ToString();
        }
    }

    private sealed class SpanAccumulator
    {
        internal List<double> Durations { get; } = new();
        internal int FailureCount { get; set; }
        internal int UnknownCount { get; set; }
        internal int NotApplicableCount { get; set; }
        internal int DefectCount { get; set; }
    }

    private sealed class ResourceAccumulator
    {
        internal int ObservedCount { get; set; }
        internal int UnknownCount { get; set; }
        internal int UnavailableCount { get; set; }
        internal int MissingCount { get; set; }
        internal int InvalidCount { get; set; }
        internal double? First { get; set; }
        internal double? Last { get; set; }
        internal double? Maximum { get; set; }
        internal bool ResetObserved { get; set; }
    }

    private sealed class CycleContext
    {
        internal int Ordinal { get; set; }
        internal bool Warmup { get; set; }
        internal Guid Correlation { get; set; }
        internal Guid InspectionId { get; set; }
        internal PerformanceDurableFact? Core { get; set; }
        internal uint ControllerEpoch { get; set; }
        internal uint ControllerSequence { get; set; }
        internal long Position { get; set; }
        internal long AdmittedTimestamp { get; set; }
        internal string? TargetFingerprint { get; set; }
        internal string? ExpectedFrameHash { get; set; }
        internal PerformanceExpectedFailure? Expected { get; set; }
        internal PerformanceEvent? TriggerObserved { get; set; }
        internal PerformanceEvent? TriggerAccepted { get; set; }
        internal PerformanceEvent? Completed { get; set; }
        internal PerformanceFrameObservation? Frame { get; set; }
        internal bool ExecutionFailed { get; set; }
        internal List<string> FailureReasons { get; } = new();
        internal Dictionary<PerformanceEventKind, List<PerformanceEvent>> Events { get; } = new();
    }

    private sealed class ScenarioCalculation
    {
        private const int MaximumFailureDetails = 1024;
        private const int MaximumReports = 4096;
        private const string InvalidIdentityKey = "PerformanceEventIdentityInvalid";

        private static readonly List<PerformanceEvent> EmptyEvents = new();

        private readonly PerformanceRawCapture _capture;
        private readonly PerformanceContract _contract;
        private readonly PerformanceRunHeader _header;
        private readonly PerformanceScenario? _scenario;
        private readonly long _frequency;
        private readonly ILookup<Guid, PerformanceDurableFact> _factsByCorrelation;
        private readonly FailureCollector _failures = new(MaximumFailureDetails);
        private readonly SpanAccumulator[] _spanAccumulators = new SpanAccumulator[Enum.GetValues<PerformanceSpan>().Length];
        private readonly ResourceAccumulator[] _resourceAccumulators = new ResourceAccumulator[Enum.GetValues<PerformanceResource>().Length];
        private readonly Dictionary<Guid, List<PerformanceEvent>> _cycleEvents = new();
        private readonly Dictionary<Guid, List<PerformanceFrameObservation>> _frames = new();
        private readonly Dictionary<Guid, List<PerformanceCycleBinding>> _bindings = new();
        private readonly List<PerformanceEvent> _ready = new();
        private readonly List<CycleContext> _cycles = new();
        private PerformanceEvent[] _events = Array.Empty<PerformanceEvent>();
        private PerformanceResourceSample[] _samples = Array.Empty<PerformanceResourceSample>();
        private PerformanceIdentityReconciliation[] _identities = Array.Empty<PerformanceIdentityReconciliation>();
        private long _stopCompleted;
        private long _recoveryCompleted;
        private double _captureDurationMilliseconds;
        private long _windowStart = -1;

        internal ScenarioCalculation(PerformanceRawCapture capture)
        {
            _capture = capture;
            _contract = capture.Contract;
            _header = capture.Header;
            _factsByCorrelation = capture.DurableFacts.ToLookup(item => item.CorrelationId);
            _frequency = _header.MonotonicFrequency > 0 ? _header.MonotonicFrequency : 1;
            _scenario = _contract.Scenarios.FirstOrDefault(value =>
                string.Equals(value.Id, _header.ScenarioId, StringComparison.Ordinal));
            for (var index = 0; index < _spanAccumulators.Length; index++) _spanAccumulators[index] = new SpanAccumulator();
            for (var index = 0; index < _resourceAccumulators.Length; index++) _resourceAccumulators[index] = new ResourceAccumulator();
        }

        internal PerformanceScenarioReport Run()
        {
            ValidateHeaderIdentity();
            ValidateCaptureEvidence();
            PrepareEvents();
            PrepareSamples();
            PrepareDurableFacts();
            PrepareFrames();
            PrepareBindings();
            Reconcile();
            EvaluateResources();
            if (_scenario is not null)
            {
                EvaluatePlan(_scenario);
                BuildCycles(_scenario);
                EvaluateSpans();
                ValidateFrames();
            }

            return BuildReport();
        }

        private void ValidateHeaderIdentity()
        {
            if (_header.RunId == Guid.Empty || _header.RuntimeEpoch == Guid.Empty ||
                _header.MonotonicFrequency <= 0 || _header.StartedAt < 0 ||
                _header.StartedAtUtc == default || _header.StartedAtUtc.Offset != TimeSpan.Zero)
                _failures.Add("PerformanceRunIdentityInvalid", "RunId/Epoch/Frequency/StartedAt");
            if (!HashEquals(_header.ContractHash, _contract.ContentHash))
                _failures.Add("PerformanceContractHashMismatch",
                    "Header=" + SanitizeIdentifier(_header.ContractHash, "Unavailable") + " Contract=" + _contract.ContentHash);
            if (!TryNormalizeHash(_header.FrameworkAssemblyHash, out _) || !TryNormalizeHash(_header.AbstractionsAssemblyHash, out _))
                _failures.Add("PerformanceAssemblyIdentityUnavailable", "Actual assembly hashes required");
            if (_scenario is null)
                _failures.Add("PerformanceScenarioUnknown", "ScenarioId=" + SanitizeIdentifier(_header.ScenarioId, "Unavailable"));
            else if (!HashEquals(_header.ScenarioHash, _scenario.ContentHash))
                _failures.Add("PerformanceScenarioHashMismatch", "ScenarioId=" + _scenario.Id);
            if (!string.Equals(_header.CollectorId, _contract.CollectorId, StringComparison.Ordinal) ||
                !string.Equals(_header.CollectorVersion, _contract.CollectorVersion, StringComparison.Ordinal) ||
                !HashEquals(_header.CollectorHash, _contract.CollectorHash))
                _failures.Add("PerformanceCollectorMismatch", "Collector=" + SanitizeIdentifier(_header.CollectorId, "Unavailable"));
            if (!string.Equals(_header.BuildConfiguration, _contract.BuildConfiguration, StringComparison.Ordinal))
                _failures.Add("PerformanceBuildConfigurationMismatch", "Build=" + SanitizeIdentifier(_header.BuildConfiguration, "Unavailable"));
            if (!string.Equals(_header.ProcessArchitecture, _contract.ProcessArchitecture, StringComparison.Ordinal))
                _failures.Add("PerformanceArchitectureMismatch", "Architecture=" + SanitizeIdentifier(_header.ProcessArchitecture, "Unavailable"));
            if (!HashEquals(_header.PowerProfileHash, _contract.PowerProfileHash))
                _failures.Add("PerformancePowerProfileMismatch", "PowerProfile=Declared");
            if (_header.DebuggerPresent != _contract.DebuggerPresent)
                _failures.Add("PerformanceDebuggerMismatch", "Debugger=" + _header.DebuggerPresent);
            if (_header.ProfilerPresent != _contract.ProfilerPresent)
                _failures.Add("PerformanceProfilerMismatch", "Profiler=" + _header.ProfilerPresent);
            if (!HashEquals(_header.DiagnosticProfileHash, _contract.DiagnosticProfileHash))
                _failures.Add("PerformanceDiagnosticProfileMismatch", "DiagnosticProfile=Declared");
            _captureDurationMilliseconds = (_capture.EndedAt - _header.StartedAt) * 1000d / _frequency;
        }

        private void ValidateCaptureEvidence()
        {
            if (!_capture.LedgerAvailable)
                _failures.Add("PerformanceLedgerUnavailable", "Reason=" + SanitizeIdentifier(_capture.LedgerReasonCode, "Unavailable"));
            if (_capture.State != PerformanceEvidenceState.Sealed)
                _failures.Add("PerformanceCaptureNotSealed", "State=" + _capture.State);
            if (_capture.ObservationDrops != 0)
                _failures.Add("PerformanceObservationDrops", "Drops=" + Number(_capture.ObservationDrops));
            if (_capture.FirstMissingSequence != 0)
                _failures.Add("PerformanceFirstMissingSequence", "First=" + Number(_capture.FirstMissingSequence));
        }

        private void PrepareEvents()
        {
            _events = _capture.Events.OrderBy(item => item.Sequence).ToArray();
            if (_events.Length > 0 && (_events[0].Sequence != 1 || _events[^1].Sequence != _events.Length ||
                    _events.Select(item => item.Sequence).Distinct().Count() != _events.Length))
                _failures.Add("PerformanceEventSequenceInvalid", "Count=" + Number(_events.Length) +
                    " First=" + Number(_events[0].Sequence) + " Last=" + Number(_events[^1].Sequence));
            long outside = 0, foreignEpoch = 0, invalidIdentity = 0;
            foreach (var item in _events)
            {
                if (item.Timestamp < _header.StartedAt || item.Timestamp > _capture.EndedAt) outside++;
                if (item.RuntimeEpoch != _header.RuntimeEpoch) foreignEpoch++;
                if (item.Kind == PerformanceEventKind.ReadyAsserted && item.RuntimeEpoch == _header.RuntimeEpoch) _ready.Add(item);
                if (item.Kind == PerformanceEventKind.StopCompleted) _stopCompleted++;
                if (item.Kind == PerformanceEventKind.RecoveryCompleted) _recoveryCompleted++;
                var requiresIdentity = item.Kind is PerformanceEventKind.TriggerAccepted or
                    PerformanceEventKind.CoreCommitCompleted or PerformanceEventKind.AcknowledgementReset or
                    PerformanceEventKind.CycleFaulted;
                var production = item.Correlation is { Kind: ExecutionKind.Production } correlation &&
                    correlation.Value != Guid.Empty;
                if (requiresIdentity && !production) invalidIdentity++;
                if (production && item.Correlation is { } identity)
                {
                    if (!_cycleEvents.TryGetValue(identity.Value, out var list))
                    {
                        list = new List<PerformanceEvent>();
                        _cycleEvents.Add(identity.Value, list);
                    }

                    list.Add(item);
                }
            }

            if (outside != 0) _failures.Add("PerformanceEventTimestampOutsideCapture", "Count=" + Number(outside));
            if (foreignEpoch != 0) _failures.Add("PerformanceEventEpochMismatch", "Count=" + Number(foreignEpoch));
            if (invalidIdentity != 0) _failures.Add("PerformanceEventIdentityInvalid", "Count=" + Number(invalidIdentity));
            _ready.Sort((left, right) => left.Timestamp != right.Timestamp
                ? left.Timestamp.CompareTo(right.Timestamp)
                : left.Sequence.CompareTo(right.Sequence));
        }

        private void PrepareDurableFacts()
        {
            var admissions = AdmittedFacts();
            var byCorrelation = admissions.ToLookup(item => item.CorrelationId);
            if (admissions.Select(item => item.CorrelationId).Distinct().Count() != admissions.Length ||
                admissions.Select(item => item.InspectionId).Distinct().Count() != admissions.Length ||
                admissions.Select(item => (item.ControllerEpoch, item.ControllerSequence)).Distinct().Count() != admissions.Length)
                _failures.Add("PerformanceBusinessIdentityReused", "Admission identity must be unique on each business key");
            foreach (var fact in _capture.DurableFacts.Where(item => item.Kind != ProductionInspectionEventKind.Admitted))
            {
                var admitted = byCorrelation[fact.CorrelationId].ToArray();
                if (admitted.Length != 1 || admitted[0].InspectionId != fact.InspectionId ||
                    admitted[0].ControllerEpoch != fact.ControllerEpoch || admitted[0].ControllerSequence != fact.ControllerSequence)
                    _failures.Add("PerformanceDurableIdentityMismatch", "Position=" + Number(fact.Position));
            }
            foreach (var item in _events.Where(item => item.Correlation?.Kind == ExecutionKind.Production))
            {
                var admitted = byCorrelation[item.Correlation!.Value].ToArray();
                if (admitted.Length != 1 || item.ControllerEpoch is { } epoch && epoch != admitted[0].ControllerEpoch ||
                    item.ControllerSequence is { } sequence && sequence != admitted[0].ControllerSequence)
                    _failures.Add("PerformanceRawIdentityMismatch", "Sequence=" + Number(item.Sequence));
            }
            long foreignEpoch = 0, outside = 0, invalidIdentity = 0;
            foreach (var fact in _capture.DurableFacts)
            {
                if (fact.RuntimeEpoch != _header.RuntimeEpoch) foreignEpoch++;
                if (fact.Timestamp < _header.StartedAt || fact.Timestamp > _capture.EndedAt) outside++;
                if (fact.CorrelationId == Guid.Empty || fact.InspectionId == Guid.Empty) invalidIdentity++;
            }

            if (foreignEpoch != 0) _failures.Add("PerformanceDurableEpochMismatch", "Count=" + Number(foreignEpoch));
            if (outside != 0) _failures.Add("PerformanceDurableTimestampOutsideCapture", "Count=" + Number(outside));
            if (invalidIdentity != 0)
                _failures.Add("PerformanceDurableIdentityInvalid", "Count=" + Number(invalidIdentity));
        }

        private void PrepareSamples()
        {
            _samples = _capture.ResourceSamples.OrderBy(item => item.Sequence).ToArray();
            if (_samples.Length > 0 && (_samples[0].Sequence != 1 || _samples[^1].Sequence != _samples.Length ||
                    _samples.Select(item => item.Sequence).Distinct().Count() != _samples.Length))
                _failures.Add("PerformanceResourceSequenceInvalid", "Count=" + Number(_samples.Length) +
                    " First=" + Number(_samples[0].Sequence) + " Last=" + Number(_samples[^1].Sequence));
            var outside = _samples.Count(item =>
                item.Timestamp < _header.StartedAt || item.Timestamp > _capture.EndedAt);
            if (outside != 0)
                _failures.Add("PerformanceResourceTimestampOutsideCapture", "Count=" + Number(outside));
        }

        private void PrepareFrames()
        {
            long invalid = 0, outside = 0;
            foreach (var frame in _capture.Frames)
            {
                if (frame.Correlation.Kind != ExecutionKind.Production || frame.Correlation.Value == Guid.Empty)
                {
                    invalid++;
                    continue;
                }

                if (frame.Timestamp < _header.StartedAt || frame.Timestamp > _capture.EndedAt) outside++;
                if (!_frames.TryGetValue(frame.Correlation.Value, out var list))
                {
                    list = new List<PerformanceFrameObservation>();
                    _frames.Add(frame.Correlation.Value, list);
                }

                list.Add(frame);
            }

            if (invalid != 0) _failures.Add("PerformanceFrameIdentityInvalid", "Count=" + Number(invalid));
            if (outside != 0) _failures.Add("PerformanceFrameTimestampOutsideCapture", "Count=" + Number(outside));
        }

        private void PrepareBindings()
        {
            long invalid = 0;
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in _capture.CycleBindings)
            {
                if (binding.Correlation.Kind != ExecutionKind.Production || binding.Correlation.Value == Guid.Empty)
                {
                    invalid++;
                    continue;
                }

                if (!_bindings.TryGetValue(binding.Correlation.Value, out var list))
                {
                    list = new List<PerformanceCycleBinding>();
                    _bindings.Add(binding.Correlation.Value, list);
                }

                list.Add(binding);
                if (binding.TargetConfigurationFingerprint is { } fingerprint && TryNormalizeHash(fingerprint, out var normalized))
                    fingerprints.Add(normalized);
                else _failures.Add("PerformanceTargetFingerprintUnavailable", "A complete actual target hash is required");
            }

            if (invalid != 0) _failures.Add("PerformanceCycleBindingIdentityInvalid", "Count=" + Number(invalid));
            if (fingerprints.Count > 1)
                _failures.Add("PerformanceTargetFingerprintMismatch", "Distinct=" + Number(fingerprints.Count));
        }

        private void Reconcile()
        {
            var results = new List<PerformanceIdentityReconciliation>(4);
            AddReconciliation(results, PerformanceIdentityKind.Accepted, ProductionInspectionEventKind.Admitted,
                fact => Key(fact.CorrelationId, fact.ControllerEpoch, fact.ControllerSequence),
                fact => Key(fact.CorrelationId, fact.InspectionId, fact.ControllerEpoch, fact.ControllerSequence),
                PerformanceEventKind.TriggerAccepted,
                item => RawControllerKey(item));
            AddReconciliation(results, PerformanceIdentityKind.CoreCommit, ProductionInspectionEventKind.CoreCommitted,
                fact => Key(fact.CorrelationId, null, null),
                fact => Key(fact.CorrelationId, fact.ControllerEpoch, fact.ControllerSequence),
                PerformanceEventKind.CoreCommitCompleted,
                item => RawCorrelationKey(item));
            AddReconciliation(results, PerformanceIdentityKind.AcknowledgementReset,
                ProductionInspectionEventKind.AcknowledgementReset,
                fact => Key(fact.CorrelationId, fact.ControllerEpoch, fact.ControllerSequence),
                fact => Key(fact.CorrelationId, fact.InspectionId, fact.ControllerEpoch, fact.ControllerSequence),
                PerformanceEventKind.AcknowledgementReset,
                item => RawControllerKey(item));
            AddReconciliation(results, PerformanceIdentityKind.FaultTerminated,
                ProductionInspectionEventKind.FaultTerminated,
                fact => Key(fact.CorrelationId, fact.ControllerEpoch, fact.ControllerSequence),
                fact => Key(fact.CorrelationId, fact.InspectionId, fact.ControllerEpoch, fact.ControllerSequence),
                PerformanceEventKind.CycleFaulted,
                item => RawControllerKey(item));
            _identities = results.ToArray();
        }

        private void AddReconciliation(List<PerformanceIdentityReconciliation> results, PerformanceIdentityKind kind,
            ProductionInspectionEventKind ledgerKind, Func<PerformanceDurableFact, string> ledgerKey,
            Func<PerformanceDurableFact, string> ledgerDuplicateKey, PerformanceEventKind rawKind,
            Func<PerformanceEvent, string> rawKey)
        {
            var ledgerKeys = new HashSet<string>(StringComparer.Ordinal);
            var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
            var businessDuplicate = 0;
            foreach (var fact in _capture.DurableFacts)
            {
                if (fact.Kind != ledgerKind || fact.RuntimeEpoch != _header.RuntimeEpoch) continue;
                ledgerKeys.Add(ledgerKey(fact));
                if (!duplicateKeys.Add(ledgerDuplicateKey(fact))) businessDuplicate++;
            }

            var rawCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var observationDuplicate = 0;
            foreach (var item in _events)
            {
                if (item.Kind != rawKind) continue;
                var key = rawKey(item);
                if (!rawCounts.TryAdd(key, 1)) observationDuplicate++;
            }

            var businessLoss = rawCounts.Keys.Count(key => !ledgerKeys.Contains(key));
            var observationMissing = ledgerKeys.Count(key => !rawCounts.ContainsKey(key));
            results.Add(new PerformanceIdentityReconciliation(kind, businessLoss, businessDuplicate,
                observationMissing, observationDuplicate));
            if (businessLoss != 0)
                _failures.Add("PerformanceBusinessLoss", "Kind=" + kind + " Count=" + Number(businessLoss));
            if (businessDuplicate != 0)
                _failures.Add("PerformanceBusinessDuplicate", "Kind=" + kind + " Count=" + Number(businessDuplicate));
            if (observationMissing != 0)
                _failures.Add("PerformanceObservationMissing", "Kind=" + kind + " Count=" + Number(observationMissing));
            if (observationDuplicate != 0)
                _failures.Add("PerformanceObservationDuplicate", "Kind=" + kind + " Count=" + Number(observationDuplicate));
        }

        private static string RawControllerKey(PerformanceEvent item) =>
            item.Correlation is { Kind: ExecutionKind.Production } correlation
                ? Key(correlation.Value, item.ControllerEpoch, item.ControllerSequence)
                : InvalidIdentityKey;

        private static string RawCorrelationKey(PerformanceEvent item) =>
            item.Correlation is { Kind: ExecutionKind.Production } correlation
                ? Key(correlation.Value, null, null)
                : InvalidIdentityKey;

        private void EvaluateResources()
        {
            if (_scenario is not null)
            {
                var admitted = AdmittedFacts();
                if (admitted.Length > _scenario.WarmupCycles) _windowStart = admitted[_scenario.WarmupCycles].Timestamp;
            }

            foreach (var budget in _contract.Resources) AccumulateResource(budget);
        }

        private void AccumulateResource(PerformanceResourceBudget budget)
        {
            var accumulator = _resourceAccumulators[(int)budget.Resource];
            double? previous = null;
            foreach (var sample in _samples)
            {
                if (sample.Timestamp < _header.StartedAt || sample.Timestamp > _capture.EndedAt) continue;
                if (_windowStart < 0 || sample.Timestamp < _windowStart) continue;
                PerformanceResourceValue? value = null;
                foreach (var candidate in sample.Values)
                    if (candidate.Resource == budget.Resource)
                    {
                        value = candidate;
                        break;
                    }

                if (value is null)
                {
                    accumulator.MissingCount++;
                    continue;
                }

                if (value.Value is not { } number)
                {
                    accumulator.UnknownCount++;
                    continue;
                }

                if (!double.IsFinite(number) || number < 0)
                {
                    accumulator.InvalidCount++;
                    continue;
                }

                if (value.Outcome != PerformanceObservationOutcome.Observed)
                {
                    accumulator.UnavailableCount++;
                    continue;
                }

                accumulator.ObservedCount++;
                accumulator.First ??= number;
                accumulator.Last = number;
                if (accumulator.Maximum is not { } maximum || number > maximum) accumulator.Maximum = number;
                if (IsCumulative(budget.Resource) && previous is { } prior && number < prior)
                    accumulator.ResetObserved = true;
                previous = number;
            }
        }

        private void EvaluatePlan(PerformanceScenario scenario)
        {
            if (_captureDurationMilliseconds < scenario.MinimumDuration.TotalMilliseconds)
                _failures.Add("PerformanceCaptureDurationBelowMinimum", "Observed=" + Invariant(_captureDurationMilliseconds) +
                    " Minimum=" + Invariant(scenario.MinimumDuration.TotalMilliseconds));
            if (_captureDurationMilliseconds > scenario.MaximumDuration.TotalMilliseconds)
                _failures.Add("PerformanceCaptureDurationAboveMaximum", "Observed=" + Invariant(_captureDurationMilliseconds) +
                    " Maximum=" + Invariant(scenario.MaximumDuration.TotalMilliseconds));
            if (_stopCompleted < scenario.RequiredStops)
                _failures.Add("PerformanceRequiredStopsMissing", "Required=" + Number(scenario.RequiredStops) +
                    " Observed=" + Number(_stopCompleted));
            if (_recoveryCompleted < scenario.RequiredRecoveries)
                _failures.Add("PerformanceRequiredRecoveriesMissing", "Required=" + Number(scenario.RequiredRecoveries) +
                    " Observed=" + Number(_recoveryCompleted));
        }

        private void BuildCycles(PerformanceScenario scenario)
        {
            var admitted = AdmittedFacts();
            var expectedTotal = scenario.MeasuredCycles + scenario.WarmupCycles;
            if (admitted.Length != expectedTotal)
                _failures.Add("PerformanceAcceptedCountMismatch", "Expected=" + Number(expectedTotal) +
                    " Accepted=" + Number(admitted.Length));
            if (admitted.Select(fact => fact.Position).Distinct().Count() != admitted.Length)
                _failures.Add("PerformanceAcceptedPositionDuplicate", "Accepted=" + Number(admitted.Length));
            var expectedFailures = new Dictionary<int, PerformanceExpectedFailure>();
            foreach (var expected in scenario.ExpectedFailures)
            {
                if (expected.CycleOrdinal > admitted.Length)
                {
                    _failures.Add("PerformanceExpectedFailureOrdinalMissing",
                        "Ordinal=" + Number(expected.CycleOrdinal) + " Reason=" + expected.ReasonCode);
                    continue;
                }

                expectedFailures[expected.CycleOrdinal] = expected;
            }

            long controllerMismatch = 0;
            for (var index = 0; index < admitted.Length; index++)
            {
                var ordinal = index + 1;
                var fact = admitted[index];
                var cycle = new CycleContext
                {
                    Ordinal = ordinal,
                    Warmup = ordinal <= scenario.WarmupCycles,
                    Correlation = fact.CorrelationId,
                    InspectionId = fact.InspectionId,
                    ControllerEpoch = fact.ControllerEpoch,
                    ControllerSequence = fact.ControllerSequence,
                    Position = fact.Position,
                    AdmittedTimestamp = fact.Timestamp,
                    Expected = expectedFailures.TryGetValue(ordinal, out var expected) ? expected : null
                };
                if (_bindings.TryGetValue(fact.CorrelationId, out var bindings) && bindings.Count > 0)
                {
                    if (bindings.Count > 1)
                        _failures.Add("PerformanceCycleBindingDuplicate",
                            "Ordinal=" + Number(ordinal) + " Count=" + Number(bindings.Count), ordinal);
                    var binding = bindings[0];
                    if (binding.Correlation.Kind != ExecutionKind.Production ||
                        binding.Correlation.Value != fact.CorrelationId)
                        _failures.Add("PerformanceCycleBindingMismatch", "Ordinal=" + Number(ordinal), ordinal);
                    else
                        cycle.TargetFingerprint = SanitizeText(binding.TargetConfigurationFingerprint, 256);
                }
                else
                    _failures.Add("PerformanceCycleBindingMissing", "Ordinal=" + Number(ordinal), ordinal);

                if (_cycleEvents.TryGetValue(fact.CorrelationId, out var events))
                    foreach (var item in events)
                    {
                        if (item.ControllerEpoch is { } controllerEpoch &&
                            item.ControllerSequence is { } controllerSequence &&
                            (controllerEpoch != fact.ControllerEpoch || controllerSequence != fact.ControllerSequence))
                        {
                            controllerMismatch++;
                            continue;
                        }

                        if (!cycle.Events.TryGetValue(item.Kind, out var list))
                        {
                            list = new List<PerformanceEvent>();
                            cycle.Events.Add(item.Kind, list);
                        }

                        list.Add(item);
                    }

                foreach (var list in cycle.Events.Values)
                    list.Sort((left, right) => left.Timestamp != right.Timestamp
                        ? left.Timestamp.CompareTo(right.Timestamp)
                        : left.Sequence.CompareTo(right.Sequence));
                cycle.TriggerObserved = First(cycle, PerformanceEventKind.TriggerObserved);
                cycle.TriggerAccepted = First(cycle, PerformanceEventKind.TriggerAccepted);
                var completed = First(cycle, PerformanceEventKind.CycleCompleted);
                var faulted = First(cycle, PerformanceEventKind.CycleFaulted);
                if (completed is not null && faulted is not null)
                    _failures.Add("PerformanceCycleCompletionDuplicate", "Ordinal=" + Number(ordinal), ordinal);
                cycle.Completed = completed is not null && (faulted is null || completed.Timestamp <= faulted.Timestamp)
                    ? completed
                    : faulted;

                var faultedEvents = EventsOfKind(cycle, PerformanceEventKind.CycleFaulted);
                var outcomes = EventsOfKind(cycle, PerformanceEventKind.AlgorithmOutcomeFixed);
                var failedOutcomes = outcomes.Where(item => item.Outcome != PerformanceObservationOutcome.Observed).ToArray();
                cycle.ExecutionFailed = faultedEvents.Count > 0 || failedOutcomes.Length > 0;
                foreach (var item in faultedEvents)
                    if (!string.IsNullOrEmpty(item.ReasonCode) && !cycle.FailureReasons.Contains(item.ReasonCode))
                        cycle.FailureReasons.Add(item.ReasonCode);
                foreach (var item in failedOutcomes)
                    if (!string.IsNullOrEmpty(item.ReasonCode) && !cycle.FailureReasons.Contains(item.ReasonCode))
                        cycle.FailureReasons.Add(item.ReasonCode);

                cycle.Core = _factsByCorrelation[fact.CorrelationId]
                    .Where(item => item.Kind == ProductionInspectionEventKind.CoreCommitted &&
                        item.RuntimeEpoch == _header.RuntimeEpoch && item.CorrelationId == fact.CorrelationId &&
                        item.ControllerEpoch == fact.ControllerEpoch && item.ControllerSequence == fact.ControllerSequence)
                    .OrderBy(item => item.Position)
                    .FirstOrDefault();
                if (cycle.Core is { } core)
                {
                    if (core.ExecutionStatus is null || core.Decision is null || core.CoreHash is null || core.PayloadHash is null)
                        _failures.Add("PerformanceCoreIncomplete", "Ordinal=" + Number(ordinal), ordinal);
                    if (core.ExecutionStatus != ExecutionStatus.Success)
                    {
                        cycle.ExecutionFailed = true;
                        if (!string.IsNullOrWhiteSpace(core.ReasonCode) && !cycle.FailureReasons.Contains(core.ReasonCode))
                            cycle.FailureReasons.Add(core.ReasonCode);
                    }
                    if (outcomes.Any(item => (item.Outcome == PerformanceObservationOutcome.Observed) !=
                            (core.ExecutionStatus == ExecutionStatus.Success)))
                        _failures.Add("PerformanceCoreOutcomeMismatch", "Ordinal=" + Number(ordinal), ordinal);
                }
                else if (faultedEvents.Count == 0)
                    _failures.Add("PerformanceCoreMissing", "Ordinal=" + Number(ordinal), ordinal);
                ValidateCycleOutcome(cycle);
                if (scenario.ExpectedFrameHashes.Count >= ordinal)
                    cycle.ExpectedFrameHash = scenario.ExpectedFrameHashes[ordinal - 1];
                if (_frames.TryGetValue(fact.CorrelationId, out var frames))
                {
                    if (frames.Count > 1)
                        _failures.Add("PerformanceFrameDuplicateObservation",
                            "Ordinal=" + Number(ordinal) + " Count=" + Number(frames.Count), ordinal);
                    cycle.Frame = frames[0];
                }

                _cycles.Add(cycle);
            }

            if (controllerMismatch != 0)
                _failures.Add("PerformanceControllerCycleMismatch", "Count=" + Number(controllerMismatch));
        }

        private void ValidateCycleOutcome(CycleContext cycle)
        {
            if (cycle.Expected is { } expected)
            {
                if (!cycle.FailureReasons.Contains(expected.ReasonCode))
                    _failures.Add("PerformanceExpectedFailureNotObserved", "Ordinal=" + Number(cycle.Ordinal) +
                        " Expected=" + expected.ReasonCode + " Observed=" +
                        (cycle.FailureReasons.Count == 0 ? "None" : string.Join(",", cycle.FailureReasons)), cycle.Ordinal);
                foreach (var reason in cycle.FailureReasons)
                    if (!string.Equals(reason, expected.ReasonCode, StringComparison.Ordinal))
                        _failures.Add("PerformanceUnexpectedFailureReason",
                            "Ordinal=" + Number(cycle.Ordinal) + " Reason=" + reason, cycle.Ordinal);
            }
            else if (cycle.ExecutionFailed)
                _failures.Add("PerformanceUnexpectedExecutionFailure",
                    "Ordinal=" + Number(cycle.Ordinal) + " Reason=" + (cycle.FailureReasons.FirstOrDefault() ?? "Unknown"), cycle.Ordinal);
        }

        private void EvaluateSpans()
        {
            foreach (var cycle in _cycles)
            {
                if (cycle.Warmup) continue;
                foreach (var definition in PerformanceSpanDefinitions.All)
                    RecordSpan(definition.Span, definition.Start, definition.End, cycle);
            }
        }

        private void RecordSpan(PerformanceSpan span, PerformanceEventKind startKind, PerformanceEventKind endKind,
            CycleContext cycle)
        {
            List<PerformanceEvent> startEvents;
            List<PerformanceEvent> endEvents;
            if (span == PerformanceSpan.ReadyToReady)
            {
                var boundary = cycle.TriggerObserved?.Timestamp ?? cycle.TriggerAccepted?.Timestamp;
                var start = boundary is { } trigger ? ReadyBefore(trigger) : null;
                var end = cycle.Completed is { } completed ? ReadyAfter(completed.Timestamp) : null;
                startEvents = start is null ? EmptyEvents : new List<PerformanceEvent> { start };
                endEvents = end is null ? EmptyEvents : new List<PerformanceEvent> { end };
            }
            else if (span == PerformanceSpan.AcknowledgementResetToReady)
            {
                startEvents = EventsOfKind(cycle, startKind);
                var end = cycle.Completed is { } completed ? ReadyAfter(completed.Timestamp) : null;
                endEvents = end is null ? EmptyEvents : new List<PerformanceEvent> { end };
            }
            else
            {
                startEvents = EventsOfKind(cycle, startKind);
                endEvents = EventsOfKind(cycle, endKind);
            }

            ClassifySpan(span, cycle, startEvents, endEvents);
        }

        private void ClassifySpan(PerformanceSpan span, CycleContext cycle, List<PerformanceEvent> startEvents,
            List<PerformanceEvent> endEvents)
        {
            var accumulator = _spanAccumulators[(int)span];
            var present = new List<PerformanceEvent>(startEvents.Count + endEvents.Count);
            present.AddRange(startEvents);
            present.AddRange(endEvents);
            if (startEvents.Count > 1 || endEvents.Count > 1)
            {
                RecordDefect("PerformanceSpanDuplicatePhase", span, cycle);
                accumulator.FailureCount++;
                return;
            }

            if (present.Any(item => item.RuntimeEpoch != _header.RuntimeEpoch))
            {
                RecordDefect("PerformanceSpanEpochMismatch", span, cycle);
                accumulator.FailureCount++;
                return;
            }

            if (present.Any(item => item.Outcome == PerformanceObservationOutcome.NotApplicable))
            {
                accumulator.NotApplicableCount++;
                if (_contract.Spans[(int)span].Applicable || present.Any(item => IsFailing(item.Outcome)))
                    RecordDefect("PerformanceSpanApplicabilityMismatch", span, cycle);
                return;
            }

            if (startEvents.Count == 0 || endEvents.Count == 0)
            {
                accumulator.FailureCount++;
                if (_contract.Spans[(int)span].Applicable && !cycle.ExecutionFailed)
                    RecordDefect("PerformanceSpanEndpointMissing", span, cycle);
                return;
            }

            var start = startEvents[0];
            var end = endEvents[0];
            if (end.Timestamp < start.Timestamp)
            {
                RecordDefect("PerformanceSpanNegativeDuration", span, cycle);
                accumulator.FailureCount++;
                return;
            }

            if (IsFailing(start.Outcome) || IsFailing(end.Outcome))
            {
                accumulator.FailureCount++;
                return;
            }

            if (start.Outcome == PerformanceObservationOutcome.Unknown ||
                end.Outcome == PerformanceObservationOutcome.Unknown)
            {
                accumulator.UnknownCount++;
                return;
            }

            accumulator.Durations.Add(Duration(start.Timestamp, end.Timestamp));
        }

        private void ValidateFrames()
        {
            foreach (var cycle in _cycles)
            {
                var expected = cycle.ExpectedFrameHash;
                var frame = cycle.Frame;
                var ready = EventsOfKind(cycle, PerformanceEventKind.FrameReady);
                if (expected is null)
                {
                    if (frame is not null || ready.Count > 0)
                        _failures.Add("PerformanceUnexpectedFrameObserved",
                            "Ordinal=" + Number(cycle.Ordinal) + " Observed=" + (frame is null ? "FrameReady" : "Frame"),
                            cycle.Ordinal);
                    continue;
                }

                if (frame is null)
                {
                    _failures.Add(ready.Count > 0 ? "PerformanceFrameObservationMissing" : "PerformanceFrameMissing",
                        "Ordinal=" + Number(cycle.Ordinal) + " Expected=" + expected, cycle.Ordinal);
                    continue;
                }

                if (ready.Count == 0)
                    _failures.Add("PerformanceFrameReadyMissing", "Ordinal=" + Number(cycle.Ordinal), cycle.Ordinal);
                if (frame.Outcome != PerformanceObservationOutcome.Observed || frame.ValidPixelHash is null)
                {
                    _failures.Add("PerformanceFrameHashUnavailable", "Ordinal=" + Number(cycle.Ordinal) +
                        " Outcome=" + frame.Outcome + " Reason=" + SanitizeIdentifier(frame.ReasonCode, "Unavailable"),
                        cycle.Ordinal);
                    continue;
                }

                if (!TryNormalizeHash(frame.ValidPixelHash, out var observed))
                {
                    _failures.Add("PerformanceFrameHashInvalid", "Ordinal=" + Number(cycle.Ordinal), cycle.Ordinal);
                    continue;
                }

                if (!string.Equals(observed, expected, StringComparison.Ordinal))
                    _failures.Add("PerformanceFrameHashMismatch", "Ordinal=" + Number(cycle.Ordinal) +
                        " Expected=" + expected + " Observed=" + observed, cycle.Ordinal);
            }
        }

        private void RecordDefect(string code, PerformanceSpan span, CycleContext cycle)
        {
            _spanAccumulators[(int)span].DefectCount++;
            _failures.Add(code, "Span=" + span + " Ordinal=" + Number(cycle.Ordinal), cycle.Ordinal, span);
        }

        private PerformanceEvent? ReadyBefore(long timestamp)
        {
            var low = 0; var high = _ready.Count;
            while (low < high)
            { var middle = low + (high - low) / 2; if (_ready[middle].Timestamp <= timestamp) low = middle + 1; else high = middle; }
            return low == 0 ? null : _ready[low - 1];
        }

        private PerformanceEvent? ReadyAfter(long timestamp)
        {
            var low = 0; var high = _ready.Count;
            while (low < high)
            { var middle = low + (high - low) / 2; if (_ready[middle].Timestamp < timestamp) low = middle + 1; else high = middle; }
            return low == _ready.Count ? null : _ready[low];
        }

        private PerformanceScenarioReport BuildReport()
        {
            var spanReports = BuildSpanReports();
            var resourceReports = BuildResourceReports();
            var cycleReports = _cycles.Select(ToReport).ToArray();
            var cadence = BuildCadence();
            var evidence = new PerformanceEvidenceReconciliation(_capture.LedgerAvailable, _capture.LedgerReasonCode,
                _identities);
            var failures = _failures.Snapshot();
            var passed = cadence.State == PerformanceCadenceState.Satisfied && _failures.Total == 0 && spanReports.All(item => item.Passed) &&
                resourceReports.All(item => item.Passed);
            return new PerformanceScenarioReport(PerformanceReportPurpose.FrameworkBaseline, _header.RunId,
                SanitizeIdentifier(_header.ContractHash, "ContractHashUnavailable"),
                SanitizeIdentifier(_header.ScenarioId, "ScenarioUnknown"),
                SanitizeIdentifier(_header.ScenarioHash, "ScenarioHashUnavailable"),
                passed, _capture.State == PerformanceEvidenceState.Sealed, _capture.State, _capture.ObservationDrops,
                _scenario?.MeasuredCycles ?? 0, _scenario?.WarmupCycles ?? 0, _cycles.Count,
                _captureDurationMilliseconds, cadence, evidence, _failures.Total, _failures.Truncated, failures,
                spanReports, resourceReports, cycleReports);
        }

        private List<PerformanceSpanReport> BuildSpanReports()
        {
            var reports = new List<PerformanceSpanReport>(_contract.Spans.Count);
            foreach (var definition in PerformanceSpanDefinitions.All)
            {
                var span = definition.Span;
                var budget = _contract.Spans[(int)span];
                var accumulator = _spanAccumulators[(int)span];
                var durations = accumulator.Durations;
                durations.Sort();
                double? p50 = null, p95 = null, p99 = null, observedMaximum = null, jitter = null;
                if (durations.Count > 0)
                {
                    p50 = NearestRank(durations, 0.50);
                    p95 = NearestRank(durations, 0.95);
                    p99 = NearestRank(durations, 0.99);
                    observedMaximum = durations[^1];
                    jitter = durations[^1] - durations[0];
                }

                var samplesSatisfied = durations.Count >= budget.MinimumSamples;
                var failuresSatisfied = accumulator.FailureCount <= budget.MaximumFailures;
                var thresholdsSatisfied = durations.Count == 0 ||
                    budget.P50Milliseconds is { } p50Limit && p50 <= p50Limit &&
                    budget.P95Milliseconds is { } p95Limit && p95 <= p95Limit &&
                    budget.P99Milliseconds is { } p99Limit && p99 <= p99Limit &&
                    budget.ObservedMaxMilliseconds is { } maximumLimit && observedMaximum <= maximumLimit &&
                    budget.JitterMilliseconds is { } jitterLimit && jitter <= jitterLimit;
                var evidenceSatisfied = accumulator.DefectCount == 0 && accumulator.UnknownCount == 0;
                var passed = budget.Applicable
                    ? samplesSatisfied && failuresSatisfied && thresholdsSatisfied && evidenceSatisfied
                    : accumulator.DefectCount == 0;
                if (budget.Applicable)
                {
                    if (!samplesSatisfied)
                        _failures.Add("PerformanceSpanMinimumSamples", "Span=" + span + " Observed=" +
                            Number(durations.Count) + " Minimum=" + Number(budget.MinimumSamples), null, span);
                    if (!failuresSatisfied)
                        _failures.Add("PerformanceSpanMaximumFailures", "Span=" + span + " Failures=" +
                            Number(accumulator.FailureCount) + " Maximum=" + Number(budget.MaximumFailures), null, span);
                    if (!thresholdsSatisfied)
                        _failures.Add("PerformanceSpanBudgetExceeded", "Span=" + span + " " +
                            ExceededMetric(budget, p50, p95, p99, observedMaximum, jitter), null, span);
                    if (accumulator.UnknownCount > 0)
                        _failures.Add("PerformanceSpanUnknownObservation",
                            "Span=" + span + " Unknown=" + Number(accumulator.UnknownCount), null, span);
                }

                reports.Add(new PerformanceSpanReport(span, budget.Applicable, budget.Applicability,
                    budget.MinimumSamples, budget.MaximumFailures, durations.Count, accumulator.FailureCount,
                    accumulator.UnknownCount, accumulator.NotApplicableCount, accumulator.DefectCount,
                    p50, p95, p99, observedMaximum, jitter, passed));
            }

            return reports;
        }

        private List<PerformanceResourceReport> BuildResourceReports()
        {
            var reports = new List<PerformanceResourceReport>(_contract.Resources.Count);
            foreach (var budget in _contract.Resources)
            {
                var accumulator = _resourceAccumulators[(int)budget.Resource];
                var growth = accumulator.ObservedCount > 0
                    ? accumulator.Last!.Value - accumulator.First!.Value
                    : (double?)null;
                var passed = true;
                if (budget.Required)
                {
                    if (accumulator.ObservedCount < budget.MinimumSamples)
                    {
                        passed = false;
                        _failures.Add("PerformanceResourceMinimumSamples", "Resource=" + budget.Resource +
                            " Observed=" + Number(accumulator.ObservedCount) + " Minimum=" + Number(budget.MinimumSamples));
                    }

                    if (accumulator.UnknownCount > 0)
                    {
                        passed = false;
                        _failures.Add("PerformanceResourceUnknown", "Resource=" + budget.Resource +
                            " Unknown=" + Number(accumulator.UnknownCount));
                    }

                    if (accumulator.UnavailableCount > 0)
                    {
                        passed = false;
                        _failures.Add("PerformanceResourceUnavailable", "Resource=" + budget.Resource +
                            " Unavailable=" + Number(accumulator.UnavailableCount));
                    }

                    if (accumulator.InvalidCount > 0)
                    {
                        passed = false;
                        _failures.Add("PerformanceResourceValueInvalid", "Resource=" + budget.Resource +
                            " Invalid=" + Number(accumulator.InvalidCount));
                    }

                    if (accumulator.MissingCount > 0)
                    {
                        passed = false;
                        _failures.Add("PerformanceResourceMissing", "Resource=" + budget.Resource +
                            " Missing=" + Number(accumulator.MissingCount));
                    }

                    if (accumulator.Maximum is { } observedMaximum && budget.Maximum is { } maximum &&
                        observedMaximum > maximum)
                    {
                        passed = false;
                        _failures.Add("PerformanceResourceMaximumExceeded", "Resource=" + budget.Resource +
                            " Maximum=" + Invariant(observedMaximum) + " Limit=" + Invariant(maximum));
                    }

                    if (growth is { } growthValue && budget.MaximumGrowth is { } growthLimit &&
                        growthValue > growthLimit)
                    {
                        passed = false;
                        _failures.Add("PerformanceResourceGrowthExceeded", "Resource=" + budget.Resource +
                            " Growth=" + Invariant(growthValue) + " Limit=" + Invariant(growthLimit));
                    }

                    if (accumulator.ResetObserved)
                    {
                        passed = false;
                        _failures.Add("PerformanceResourceReset", "Resource=" + budget.Resource + " Reset=Observed");
                    }
                }

                reports.Add(new PerformanceResourceReport(budget.Resource, budget.Required, budget.Applicability,
                    Unit(budget.Resource), budget.MinimumSamples, budget.Maximum, budget.MaximumGrowth,
                    accumulator.ObservedCount, accumulator.UnknownCount, accumulator.UnavailableCount,
                    accumulator.MissingCount, accumulator.InvalidCount, accumulator.First, accumulator.Last,
                    accumulator.Maximum, growth, accumulator.ResetObserved, passed));
            }

            return reports;
        }

        private PerformanceCycleReport ToReport(CycleContext cycle)
        {
            double? elapsed = null;
            if (cycle.TriggerObserved is { } trigger && cycle.Completed is { } completed &&
                completed.Timestamp >= trigger.Timestamp)
                elapsed = Duration(trigger.Timestamp, completed.Timestamp);
            return new PerformanceCycleReport(cycle.Ordinal, cycle.Warmup, cycle.Correlation, cycle.InspectionId,
                cycle.ControllerEpoch, cycle.ControllerSequence, cycle.Position, cycle.AdmittedTimestamp,
                cycle.TriggerObserved?.Timestamp, cycle.Completed?.Timestamp, elapsed,
                cycle.Expected?.ReasonCode, cycle.FailureReasons, cycle.ExecutionFailed,
                cycle.Core?.ExecutionStatus, cycle.Core?.Decision, cycle.TargetFingerprint,
                cycle.ExpectedFrameHash, SanitizeText(cycle.Frame?.ValidPixelHash, 128), cycle.Frame?.Outcome);
        }

        private PerformanceCadenceReport BuildCadence()
        {
            var triggers = _events.Where(item => item.Kind == PerformanceEventKind.TriggerObserved &&
                    item.RuntimeEpoch == _header.RuntimeEpoch)
                .OrderBy(item => item.Timestamp)
                .ThenBy(item => item.Sequence)
                .ToArray();
            var intervals = new List<double>(Math.Max(0, triggers.Length - 1));
            for (var index = 1; index < triggers.Length; index++)
                intervals.Add(Duration(triggers[index - 1].Timestamp, triggers[index].Timestamp));
            var declaredInterval = _scenario?.TriggerInterval.TotalMilliseconds ?? 0;
            var bursts = triggers.Length == 0 ? 0 : 1;
            var maximumBurstLength = triggers.Length == 0 ? 0 : 1;
            var currentBurst = triggers.Length == 0 ? 0 : 1;
            foreach (var interval in intervals)
                if (interval >= declaredInterval)
                {
                    bursts++;
                    currentBurst = 1;
                }
                else
                {
                    currentBurst++;
                    if (currentBurst > maximumBurstLength) maximumBurstLength = currentBurst;
                }

            var state = PerformanceCadenceState.Unproven;
            var reason = "PerformanceCadenceToleranceUnspecified";
            if (_scenario?.MaximumCadenceDeviation is { } tolerance)
            {
                state = PerformanceCadenceState.Satisfied;
                reason = "PerformanceCadenceSatisfied";
                for (var index = 0; index < intervals.Count; index++)
                {
                    var withinBurst = _scenario.BurstSize > 1 && (index + 1) % _scenario.BurstSize != 0;
                    var expected = withinBurst ? _scenario.BurstInterval.TotalMilliseconds : declaredInterval;
                    if (Math.Abs(intervals[index] - expected) > tolerance.TotalMilliseconds)
                    { state = PerformanceCadenceState.Unsatisfied; reason = "PerformanceCadenceDeviationExceeded"; }
                }
                bursts = triggers.Length == 0 ? 0 : (triggers.Length + _scenario.BurstSize - 1) / _scenario.BurstSize;
                maximumBurstLength = Math.Min(triggers.Length, _scenario.BurstSize);
            }
            if (state != PerformanceCadenceState.Satisfied) _failures.Add(reason, "Cadence=" + state);
            return new PerformanceCadenceReport(state, reason,
                triggers.Length, declaredInterval, _scenario?.BurstSize ?? 0,
                _scenario?.BurstInterval.TotalMilliseconds ?? 0,
                intervals.Count == 0 ? (double?)null : intervals.Min(),
                intervals.Count == 0 ? (double?)null : intervals.Max(),
                intervals.Count == 0 ? (double?)null : intervals.Average(),
                bursts, maximumBurstLength);
        }

        private PerformanceDurableFact[] AdmittedFacts() => _capture.DurableFacts
            .Where(fact => fact.Kind == ProductionInspectionEventKind.Admitted &&
                fact.RuntimeEpoch == _header.RuntimeEpoch && fact.CorrelationId != Guid.Empty &&
                fact.InspectionId != Guid.Empty && fact.Position >= 0 && fact.Timestamp >= 0)
            .OrderBy(fact => fact.Position)
            .ToArray();

        private static PerformanceEvent? First(CycleContext cycle, PerformanceEventKind kind) =>
            cycle.Events.TryGetValue(kind, out var list) && list.Count > 0 ? list[0] : null;

        private static List<PerformanceEvent> EventsOfKind(CycleContext cycle, PerformanceEventKind kind) =>
            cycle.Events.TryGetValue(kind, out var list) ? list : EmptyEvents;

        private double Duration(long start, long end) => (end - start) * 1000d / _frequency;

        private static bool IsFailing(PerformanceObservationOutcome outcome) =>
            outcome is PerformanceObservationOutcome.Failed or PerformanceObservationOutcome.TimedOut or
                PerformanceObservationOutcome.Cancelled;

        private static double NearestRank(List<double> sorted, double percentile)
        {
            var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Count) index = sorted.Count - 1;
            return sorted[index];
        }

        private static string ExceededMetric(PerformanceSpanBudget budget, double? p50, double? p95, double? p99,
            double? observedMaximum, double? jitter)
        {
            if (p50 is { } p50Value && budget.P50Milliseconds is { } p50Limit && p50Value > p50Limit)
                return "P50=" + Invariant(p50Value) + " Limit=" + Invariant(p50Limit);
            if (p95 is { } p95Value && budget.P95Milliseconds is { } p95Limit && p95Value > p95Limit)
                return "P95=" + Invariant(p95Value) + " Limit=" + Invariant(p95Limit);
            if (p99 is { } p99Value && budget.P99Milliseconds is { } p99Limit && p99Value > p99Limit)
                return "P99=" + Invariant(p99Value) + " Limit=" + Invariant(p99Limit);
            if (observedMaximum is { } maximumValue && budget.ObservedMaxMilliseconds is { } maximumLimit &&
                maximumValue > maximumLimit)
                return "Maximum=" + Invariant(maximumValue) + " Limit=" + Invariant(maximumLimit);
            if (jitter is { } jitterValue && budget.JitterMilliseconds is { } jitterLimit && jitterValue > jitterLimit)
                return "Jitter=" + Invariant(jitterValue) + " Limit=" + Invariant(jitterLimit);
            return "Threshold=Unspecified";
        }

        private static bool IsCumulative(PerformanceResource resource) => resource is
            PerformanceResource.TotalAllocatedBytes or PerformanceResource.Gen0Collections or PerformanceResource.Gen1Collections or
            PerformanceResource.Gen2Collections or PerformanceResource.GcPauseMilliseconds or PerformanceResource.PeakFrameLeases or
            PerformanceResource.FramePoolExhaustions or PerformanceResource.SqliteCheckpointCount or PerformanceResource.DiagnosticDrops ||
            resource >= PerformanceResource.WpfSnapshotsReceived;

        private static PerformanceResourceUnit Unit(PerformanceResource resource) => resource switch
        {
            PerformanceResource.ProcessCpuPercent => PerformanceResourceUnit.Percent,
            PerformanceResource.WorkingSetBytes or PerformanceResource.PrivateBytes or
                PerformanceResource.ManagedHeapBytes or PerformanceResource.ManagedCommittedBytes or
                PerformanceResource.NativeMemoryBytes or PerformanceResource.NonGcPrivateBytesEstimate or
                PerformanceResource.TotalAllocatedBytes or PerformanceResource.AllocatedBytesPerCycle or
                PerformanceResource.SqliteDatabaseBytes or PerformanceResource.SqliteWalBytes or
                PerformanceResource.DiagnosticQueuedBytes or PerformanceResource.WpfImageCopyBytes =>
                PerformanceResourceUnit.Bytes,
            PerformanceResource.GcPauseMilliseconds or PerformanceResource.WpfSnapshotApplyMilliseconds or
                PerformanceResource.WpfImageCopyMilliseconds or PerformanceResource.WpfRenderMilliseconds =>
                PerformanceResourceUnit.Milliseconds,
            _ => PerformanceResourceUnit.Count
        };

        private static bool HashEquals(string? candidate, string expected)
        {
            if (candidate is null) return false;
            try
            {
                return string.Equals(PerformanceContractValue.ContentHash(candidate), expected, StringComparison.Ordinal);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryNormalizeHash(string candidate, out string normalized)
        {
            normalized = string.Empty;
            try
            {
                normalized = PerformanceContractValue.ContentHash(candidate);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string Key(Guid correlation, uint? epoch, uint? sequence) =>
            epoch is { } epochValue && sequence is { } sequenceValue
                ? correlation.ToString("D", CultureInfo.InvariantCulture) + "|" +
                  epochValue.ToString(CultureInfo.InvariantCulture) + "|" +
                  sequenceValue.ToString(CultureInfo.InvariantCulture)
                : correlation.ToString("D", CultureInfo.InvariantCulture) + "||";

        private static string Key(Guid correlation, uint epoch, uint sequence) =>
            correlation.ToString("D", CultureInfo.InvariantCulture) + "|" +
            epoch.ToString(CultureInfo.InvariantCulture) + "|" + sequence.ToString(CultureInfo.InvariantCulture);

        private static string Key(Guid correlation, Guid inspectionId, uint epoch, uint sequence) =>
            Key(correlation, epoch, sequence) + "|" + inspectionId.ToString("D", CultureInfo.InvariantCulture);

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Invariant(double value) => AlgorithmContractValidation.Invariant(value);

        private static string? SanitizeText(string? value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
            foreach (var character in value)
            {
                if (builder.Length >= maximumLength) break;
                if (char.IsControl(character) || char.IsSurrogate(character)) continue;
                builder.Append(character);
            }

            return builder.Length == 0 ? null : builder.ToString();
        }

        private static string SanitizeIdentifier(string? value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            var builder = new StringBuilder(Math.Min(value.Length, 128));
            foreach (var character in value)
            {
                if (builder.Length >= 128) break;
                var allowed = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
                builder.Append(allowed ? character : '_');
            }

            return builder.Length == 0 ? fallback : builder.ToString();
        }

        internal static PerformanceAggregateReport Aggregate(PerformanceContract contract,
            IEnumerable<PerformanceScenarioReport> reports)
        {
            var copied = AlgorithmContractValidation.Copy(reports, nameof(reports), MaximumReports);
            var collector = new FailureCollector(MaximumFailureDetails);
            var byScenario = new Dictionary<string, List<PerformanceScenarioReport>>(StringComparer.Ordinal);
            foreach (var report in copied)
            {
                if (!HashEquals(report.ContractHash, contract.ContentHash))
                {
                    collector.Add("PerformanceAggregateContractMismatch", "ScenarioId=" +
                        SanitizeIdentifier(report.ScenarioId, "Unknown") + " RunId=" + report.RunId.ToString("D"));
                    continue;
                }

                var scenario = contract.Scenarios.FirstOrDefault(item =>
                    string.Equals(item.Id, report.ScenarioId, StringComparison.Ordinal));
                if (scenario is null)
                {
                    collector.Add("PerformanceAggregateScenarioUnknown",
                        "ScenarioId=" + SanitizeIdentifier(report.ScenarioId, "Unknown"));
                    continue;
                }

                if (!byScenario.TryGetValue(scenario.Id, out var list))
                {
                    list = new List<PerformanceScenarioReport>();
                    byScenario.Add(scenario.Id, list);
                }

                list.Add(report);
            }

            var scenarios = new List<PerformanceAggregateScenario>(contract.Scenarios.Count);
            foreach (var scenario in contract.Scenarios)
            {
                if (!byScenario.TryGetValue(scenario.Id, out var list) || list.Count == 0)
                {
                    scenarios.Add(new PerformanceAggregateScenario(scenario.Id, scenario.Kind,
                        PerformanceReportStatus.NotRun, null, 0, 0));
                    collector.Add("PerformanceAggregateScenarioMissing",
                        "ScenarioId=" + scenario.Id + " Kind=" + scenario.Kind);
                    continue;
                }

                if (list.Count > 1)
                {
                    scenarios.Add(new PerformanceAggregateScenario(scenario.Id, scenario.Kind,
                        PerformanceReportStatus.Failed, null, list.Count, list.Sum(item => item.FailureCount)));
                    collector.Add("PerformanceAggregateScenarioDuplicate", "ScenarioId=" + scenario.Id +
                        " Reports=" + Number(list.Count) + " FailedRuns=" + Number(list.Count(item => !item.Passed)));
                    continue;
                }

                var run = list[0];
                scenarios.Add(new PerformanceAggregateScenario(scenario.Id, scenario.Kind,
                    run.Passed ? PerformanceReportStatus.Passed : PerformanceReportStatus.Failed,
                    run.RunId == Guid.Empty ? (Guid?)null : run.RunId, 1, run.FailureCount));
                if (!run.Passed)
                    collector.Add("PerformanceAggregateScenarioFailed", "ScenarioId=" + scenario.Id +
                        " RunId=" + run.RunId.ToString("D") + " Failures=" + Number(run.FailureCount));
            }

            var failures = collector.Snapshot();
            var passed = collector.Total == 0 && scenarios.All(item => item.Status == PerformanceReportStatus.Passed);
            return new PerformanceAggregateReport(PerformanceReportPurpose.FrameworkBaseline, contract.ContentHash,
                passed, collector.Total, collector.Truncated, failures, scenarios);
        }
    }
}
