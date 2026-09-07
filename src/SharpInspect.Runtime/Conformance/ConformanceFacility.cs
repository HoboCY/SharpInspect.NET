using System.Diagnostics;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Conformance;

public sealed record ConformanceExecutionRequest(string CandidateHash, string ContextHash, string TestId,
    ConformanceBindings Bindings, IReadOnlyList<ConformanceEvidence> Inputs,
    Guid? PredecessorExecutionId = null);

/// <summary>
/// Explicit development execution facility. A reservation is durable before trusted scenario code
/// runs. Results and their evidence commit together. This facility cannot issue release or station authority.
/// </summary>
public sealed class ConformanceFacility : IDisposable
{
    private readonly ConformanceLedger _ledger;
    private readonly IConformanceScenario[] _scenarios;
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly object _sync = new();
    private readonly TimeSpan _scenarioTimeout;
    private bool _disposed;
    internal Func<Task>? BeforeTerminalDecision { get; set; }

    public ConformanceFacility(ConformanceLedgerOptions options, IReadOnlyList<IConformanceScenario> scenarios,
        TimeSpan? scenarioTimeout = null)
    {
        if (scenarios.Count > 128 || scenarios.Any(s => s is null) ||
            scenarios.Select(s => s.TestId).Distinct(StringComparer.Ordinal).Count() != scenarios.Count)
            throw new ArgumentException("ConformanceScenarioRegistryInvalid");
        _scenarioTimeout = scenarioTimeout ?? TimeSpan.FromSeconds(30);
        if (_scenarioTimeout < TimeSpan.FromMilliseconds(20) || _scenarioTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(scenarioTimeout));
        _scenarios = scenarios.ToArray();
        foreach (var scenario in _scenarios) { Identifier(scenario.TestId); Hash(scenario.ScenarioHash); }
        _ledger = new ConformanceLedger(options);
    }

    public void Freeze(FrozenConformanceDocument profile, FrozenConformanceDocument candidate,
        FrozenConformanceDocument context)
    {
        var definition = ConformanceDocuments.ReadProfile(profile);
        _ = ConformanceDocuments.ReadCandidate(candidate);
        var contextDefinition = ConformanceDocuments.ReadContext(context);
        if (contextDefinition.ProfileHash != profile.Sha256)
            throw new ArgumentException("ConformanceContextProfileMismatch");
        using (Enter())
        {
            ThrowIfDisposed();
            var existing = _ledger.ReadAll();
            foreach (var old in existing.Where(e => e.Kind == "profile"))
            {
                var prior = ConformanceDocuments.ReadProfile(Deserialize<FrozenConformanceDocument>(old.Payload));
                if (prior.ProfileId == definition.ProfileId && prior.Version == definition.Version && old.Id != profile.Sha256)
                    throw new InvalidOperationException("ConformanceProfileVersionAlreadyFrozen");
            }
            var items = new[] { ("profile", profile), ("candidate", candidate), ("context", context) }
                .Where(p => !existing.Any(e => e.Kind == p.Item1 && e.Id == p.Item2.Sha256))
                .Select(p => new ConformanceLedgerItem(p.Item1, p.Item2.Sha256, JsonSerializer.Serialize(p.Item2))).ToArray();
            if (items.Length > 0) _ledger.Append(items);
        }
    }

    public IReadOnlyList<ConformanceExecutionView> GetExecutions(long afterSequence = 0, int limit = 100) =>
        Query(model => model.GetExecutions(afterSequence, limit));

    public ConformanceAggregate Aggregate(string candidateHash, string contextHash, QualificationLayer layer) =>
        Query(model => model.Aggregate(candidateHash, contextHash, layer));

    public byte[] ReadArtifact(string sha256) => Query(model => model.ReadArtifact(sha256));

    public async Task<TestExecutionRecord> ExecuteAsync(ConformanceExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_execution.Wait(0)) throw new InvalidOperationException("ConformanceExecutionBusy");
        Task<ConformanceObservation>? observationTask = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            request = request with { Inputs = Array.AsReadOnly(request.Inputs.ToArray()) };
            Hash(request.CandidateHash); Hash(request.ContextHash); Identifier(request.TestId);
            if (request.Inputs.Count > 8) throw new ArgumentException("ConformanceInputLimit");
            var inputArtifacts = Evidence(request.Inputs);
            ConformanceExecutionReservation reservation;
            ReleaseCandidateDefinition candidate;
            QualificationContextDefinition context;
            VerificationCase test;
            using (Enter())
            {
                ThrowIfDisposed();
                var model = new ConformanceReadModel(_ledger.ReadAll());
                candidate = model.Candidate(request.CandidateHash);
                context = model.Context(request.ContextHash);
                var profile = model.Profile(context.ProfileHash);
                test = profile.Cases.SingleOrDefault(t => t.TestId == request.TestId)
                    ?? throw new ArgumentException("ConformanceTestNotFrozen");
                // This foundation cannot be used to produce formal qualification evidence.
                if (profile.Claim != ConformanceClaim.DevelopmentOnly)
                    throw new InvalidOperationException("FormalConformanceExecutionNotAvailable");
                var previous = model.Executions.LastOrDefault(e => e.Reservation.TestId == test.TestId);
                if (request.PredecessorExecutionId != previous?.Reservation.TestExecutionId)
                    throw new InvalidOperationException("ConformancePredecessorRequiredOrMismatched");
                reservation = new ConformanceExecutionReservation(Guid.NewGuid(), request.CandidateHash,
                    request.ContextHash, context.ProfileHash, test.TestId, test.Layer,
                    Array.AsReadOnly(test.RequirementIds.ToArray()), ConformanceBindings.CaptureEnvironmentJson(),
                    test.ExpectedObservable, Array.AsReadOnly(inputArtifacts.Select(a => a.Reference).ToArray()),
                    DateTimeOffset.UtcNow, request.PredecessorExecutionId, "SharpInspect.ConformanceDevelopmentFacility");
                AppendWithArtifacts("reservation", reservation.TestExecutionId.ToString("D"), reservation, inputArtifacts);
            }
            var timer = Stopwatch.StartNew();
            var outcome = ConformanceOutcome.Blocked;
            var observed = "";
            var reason = "ConformanceNotExecuted";
            var anomalies = "";
            var outputs = new List<PreparedEvidence>();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(_scenarioTimeout);
            var terminalDecision = 0;
            using var cancelRegistration = lifetime.Token.Register(() => Interlocked.CompareExchange(ref terminalDecision, 1, 0));
            try
            {
                request.Bindings.Verify(candidate, context, _scenarios);
                if (inputArtifacts.Count != context.Datasets.Count ||
                    inputArtifacts.Any(a => !context.Datasets.Any(d => d.Name == a.Reference.Name && d.Sha256 == a.Reference.Sha256)))
                    throw new ConformanceBindingMismatchException("ConformanceInputChanged", context.Datasets,
                        inputArtifacts.Select(a => new FingerprintComponent(a.Reference.Name, a.Reference.Sha256)).ToArray());
                if (!test.Applicable)
                {
                    // An executable manifest proof is required, not a caller-supplied waiver.
                    var proof = request.Inputs.SingleOrDefault(e => e.Name == "applicability-proof");
                    var expectedProof = $"excluded:{test.TestId}:unreachable:{request.CandidateHash}";
                    if (proof is not null && ConformanceBindings.Utf8.GetString(proof.GetBytes()) == expectedProof &&
                        test.ExclusionEvidence == ConformanceBindings.HashText(expectedProof) &&
                        context.Datasets.Any(d => d.Name == "applicability-proof" && d.Sha256 == test.ExclusionEvidence))
                    {
                        outcome = ConformanceOutcome.NotApplicable; observed = expectedProof; reason = "FrozenOptionalExclusion";
                    }
                    else reason = "ConformanceExclusionProofMissing";
                }
                else if (test.RequiredEnvironment != "observed-environment") reason = "ConformanceRequiredEnvironmentUnavailable";
                else if (test.Method != VerificationMethod.Executable || test.AcceptanceRule != "exact-text-v1" ||
                    test.EvidenceContract != "public-test-data-v1") reason = "ConformanceVerificationMethodUnavailable";
                else if (_scenarios.SingleOrDefault(s => s.TestId == test.TestId) is not { } scenario)
                    reason = "ConformanceScenarioUnavailable";
                else
                {
                    // Reserve has committed and no writer/monitor is held while the scenario runs.
                    var invocation = new ConformanceExecutionContext(reservation.TestExecutionId, request.CandidateHash,
                        request.ContextHash, test.TestId, test.Preconditions, test.Stimulus,
                        Array.AsReadOnly(request.Inputs.ToArray()));
                    observationTask = Task.Run(() => scenario.ObserveAsync(invocation, lifetime.Token), CancellationToken.None);
                    var observation = await observationTask.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    if (observation.Classification != ConformanceEvidenceClassification.PublicTestData)
                        throw new ArgumentException("SensitiveConformanceObservationRejected");
                    Text(observation.Observed, 8192); Text(observation.Reason, 1024);
                    if (!Enum.IsDefined(typeof(ConformanceObservationStatus), observation.Status))
                        throw new InvalidOperationException("ConformanceObservationInvalid");
                    outputs.AddRange(Evidence(observation.Evidence));
                    observed = observation.Observed;
                    if (observation.Status == ConformanceObservationStatus.Blocked)
                        reason = "ConformanceScenarioBlocked";
                    else
                    {
                        outcome = string.Equals(observed, test.ExpectedObservable, StringComparison.Ordinal)
                            ? ConformanceOutcome.Pass : ConformanceOutcome.Fail;
                        reason = outcome == ConformanceOutcome.Pass ? "FrozenExpectedObserved" : "ProductObservationMismatch";
                    }
                }
                request.Bindings.Verify(candidate, context, _scenarios);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                outcome = ConformanceOutcome.Blocked;
                reason = cancellationToken.IsCancellationRequested ? "ConformanceExecutionCancelled" : "ConformanceScenarioDeadlineExceeded";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Generic exceptions do not prove InvalidHarness and may not leak secret exception messages.
                outcome = ConformanceOutcome.Blocked;
                reason = "ConformanceExecutionUnavailable";
                if (exception is ConformanceBindingMismatchException && exception.Message is "ConformanceHarnessChanged" or
                    "ConformanceScenarioChanged" or "ConformanceContextBytesChanged" or "ConformanceEnvironmentChanged" or "ConformanceInputChanged")
                {
                    outcome = ConformanceOutcome.InvalidHarness;
                    reason = exception.Message;
                }
                else if (exception is InvalidOperationException && exception.Message is
                    "ConformanceCandidateBytesChanged" or "ConformanceCandidateBindingOutsideDirectory" or "ConformanceContextBindingInsideCandidateDirectory")
                    reason = exception.Message;
                if (exception is ConformanceBindingMismatchException mismatch)
                    outputs.Add(Prepare(new ConformanceEvidence("binding-mismatch", ConformanceBindings.Utf8.GetBytes(mismatch.EvidenceJson))));
                anomalies = exception.GetType().Name;
            }
            if (BeforeTerminalDecision is { } barrier) await barrier().ConfigureAwait(false);
            // Cancellation and completion compete once, before persistence begins. Neither can revise the winner.
            if (Interlocked.CompareExchange(ref terminalDecision, 2, 0) == 1)
            {
                outcome = ConformanceOutcome.Blocked;
                reason = cancellationToken.IsCancellationRequested ? "ConformanceExecutionCancelled" : "ConformanceScenarioDeadlineExceeded";
            }
            var raw = JsonSerializer.Serialize(new FacilityObservationPayload("observation-v1", test.ExpectedObservable,
                observed, reason, anomalies, outcome));
            if (ConformanceBindings.Utf8.GetByteCount(raw) > 32768)
            {
                outcome = ConformanceOutcome.Blocked; reason = "ConformanceRawObservationCapacityExceeded";
                observed = ""; anomalies = "Oversize observation was not retained";
                raw = JsonSerializer.Serialize(new FacilityObservationPayload("observation-v1", test.ExpectedObservable,
                    observed, reason, anomalies, outcome));
            }
            outputs.Add(Prepare(new ConformanceEvidence("facility-observation", ConformanceBindings.Utf8.GetBytes(raw))));
            var result = new TestExecutionRecord(reservation.TestExecutionId, outcome, observed, reason,
                Array.AsReadOnly(outputs.Select(a => a.Reference).ToArray()), DateTimeOffset.UtcNow,
                timer.ElapsedMilliseconds, anomalies);
            using (Enter())
            {
                ThrowIfDisposed();
                AppendWithArtifacts("result", reservation.TestExecutionId.ToString("D"), result, outputs);
            }
            return result;
        }
        finally
        {
            // An uncooperative scenario retains the real execution slot until it actually finishes.
            if (observationTask is { IsCompleted: false })
                _ = observationTask.ContinueWith(t => { _ = t.Exception; _execution.Release(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else { _ = observationTask?.Exception; _execution.Release(); }
        }
    }

    private T Query<T>(Func<ConformanceReadModel, T> query)
    {
        using (Enter()) { ThrowIfDisposed(); return query(new ConformanceReadModel(_ledger.ReadAll())); }
    }

    private void AppendWithArtifacts<T>(string kind, string id, T value, IEnumerable<PreparedEvidence> artifacts)
    {
        var existing = _ledger.ReadAll();
        var items = artifacts.DistinctBy(a => a.Reference.Sha256)
            .Where(a => !existing.Any(e => e.Kind == "artifact" && e.Id == a.Reference.Sha256))
            .Select(a => new ConformanceLedgerItem("artifact", a.Reference.Sha256, a.Payload)).ToList();
        items.Add(new ConformanceLedgerItem(kind, id, JsonSerializer.Serialize(value)));
        _ledger.Append(items);
    }

    internal sealed record ArtifactPayload(int Length, string BytesBase64, string Classification = "PublicTestData",
        string ContentType = "application/octet-stream");
    internal sealed record FacilityObservationPayload(string Schema, string Expected, string Observed,
        string ReasonCode, string Anomalies, ConformanceOutcome Outcome);
    private sealed record PreparedEvidence(ConformanceArtifactReference Reference, string Payload);
    private static PreparedEvidence Prepare(ConformanceEvidence evidence)
    {
        Identifier(evidence.Name);
        if (evidence.Classification != ConformanceEvidenceClassification.PublicTestData)
            throw new ArgumentException("SensitiveConformanceEvidenceRejected");
        var bytes = evidence.GetBytes();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        return new PreparedEvidence(new ConformanceArtifactReference(evidence.Name, hash, bytes.Length),
            JsonSerializer.Serialize(new ArtifactPayload(bytes.Length, Convert.ToBase64String(bytes))));
    }
    private static List<PreparedEvidence> Evidence(IReadOnlyList<ConformanceEvidence> evidence)
    {
        if (evidence.Count > 8 || evidence.Any(e => e.Name is "facility-observation" or "binding-mismatch") ||
            evidence.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != evidence.Count)
            throw new ArgumentException("ConformanceEvidenceNamesInvalid");
        return evidence.Select(Prepare).ToList();
    }
    internal static void Text(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ConformanceBindings.Utf8.GetByteCount(value) > max) throw new ArgumentException("ConformanceTextTooLarge");
    }
    internal static void Identifier(string value)
    {
        Text(value, 128);
        if (value.Length == 0 || value.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("ConformanceIdentifierInvalid");
    }
    internal static void Hash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("ConformanceHashInvalid");
    }
    internal static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidOperationException("ConformanceRecordInvalid");
    private IDisposable Enter() => ConformanceMonitor.Enter(_sync);
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(ConformanceFacility)); }
    public void Dispose() { lock (_sync) { if (_disposed) return; _disposed = true; _ledger.Dispose(); } }
}

internal sealed class ConformanceMonitor : IDisposable
{
    private readonly object _sync;
    private ConformanceMonitor(object sync) => _sync = sync;
    public static ConformanceMonitor Enter(object sync)
    {
        if (!Monitor.TryEnter(sync, TimeSpan.FromMilliseconds(50)))
            throw new InvalidOperationException("ConformanceOperationBusy");
        return new ConformanceMonitor(sync);
    }
    public void Dispose() => Monitor.Exit(_sync);
}
