using System.Security.Cryptography;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Conformance;

/// <summary>Opens an existing ledger without provisioning, repairing, or changing any evidence.</summary>
public sealed class ConformanceQuery : IConformanceQuery, IDisposable
{
    private readonly ConformanceLedger _ledger;
    private readonly object _sync = new();
    public ConformanceQuery(ConformanceLedgerOptions options) => _ledger = new ConformanceLedger(options, readOnly: true);
    public IReadOnlyList<ConformanceExecutionView> GetExecutions(long afterSequence = 0, int limit = 100)
    {
        using (ConformanceMonitor.Enter(_sync)) return new ConformanceReadModel(_ledger.ReadAll()).GetExecutions(afterSequence, limit);
    }
    public ConformanceAggregate Aggregate(string candidateHash, string contextHash, QualificationLayer layer)
    {
        using (ConformanceMonitor.Enter(_sync)) return new ConformanceReadModel(_ledger.ReadAll()).Aggregate(candidateHash, contextHash, layer);
    }
    public byte[] ReadArtifact(string sha256)
    {
        using (ConformanceMonitor.Enter(_sync)) return new ConformanceReadModel(_ledger.ReadAll()).ReadArtifact(sha256);
    }
    public void Dispose() { lock (_sync) _ledger.Dispose(); }
}

internal sealed class ConformanceReadModel
{
    private readonly IReadOnlyList<ConformanceLedgerEntry> _entries;
    public IReadOnlyList<ConformanceExecutionView> Executions { get; }
    public ConformanceReadModel(IReadOnlyList<ConformanceLedgerEntry> entries)
    {
        _entries = entries;
        var executions = new List<ConformanceExecutionView>();
        foreach (var entry in entries.Where(e => e.Kind == "reservation"))
        {
            var reservation = ConformanceFacility.Deserialize<ConformanceExecutionReservation>(entry.Payload);
            if (entry.Id != reservation.TestExecutionId.ToString("D")) Invalid();
            var profile = Profile(reservation.ProfileHash);
            _ = Candidate(reservation.CandidateHash);
            var context = Context(reservation.ContextHash);
            var test = profile.Cases.SingleOrDefault(t => t.TestId == reservation.TestId);
            if (context.ProfileHash != reservation.ProfileHash || test is null || test.Layer != reservation.Layer ||
                test.ExpectedObservable != reservation.ExpectedObservable ||
                !test.RequirementIds.OrderBy(i => i, StringComparer.Ordinal).SequenceEqual(
                    reservation.RequirementIds.OrderBy(i => i, StringComparer.Ordinal))) Invalid();
            var prior = executions.LastOrDefault(e => e.Reservation.TestId == reservation.TestId);
            if (reservation.PredecessorExecutionId != prior?.Reservation.TestExecutionId) Invalid();
            foreach (var artifact in reservation.Inputs) VerifyArtifact(artifact, entry.Sequence);
            var terminal = entries.SingleOrDefault(e => e.Kind == "result" && e.Id == entry.Id);
            TestExecutionRecord? record = null;
            if (terminal is not null)
            {
                record = ConformanceFacility.Deserialize<TestExecutionRecord>(terminal.Payload);
                if (terminal.Sequence <= entry.Sequence || record.TestExecutionId != reservation.TestExecutionId ||
                    !Enum.IsDefined(typeof(ConformanceOutcome), record.Outcome) || record.DurationMilliseconds < 0 ||
                    record.EvidencePurpose != "DevelopmentOnly" || record.Outputs.Count is < 1 or > 10) Invalid();
                if (record.Outcome == ConformanceOutcome.NotApplicable && (test!.Applicable ||
                    test.RequirementIds.Any(id => profile.Requirements.Single(r => r.RequirementId == id).Mandatory))) Invalid();
                if (record.Outcome == ConformanceOutcome.Pass && (!test!.Applicable ||
                    !string.Equals(record.Observed, test.ExpectedObservable, StringComparison.Ordinal))) Invalid();
                foreach (var artifact in record.Outputs) VerifyArtifact(artifact, terminal.Sequence);
                VerifyTerminal(reservation, record, test!);
                record = record with { Outputs = Array.AsReadOnly(record.Outputs.ToArray()) };
            }
            executions.Add(new ConformanceExecutionView(entry.Sequence, reservation with
            {
                Inputs = Array.AsReadOnly(reservation.Inputs.ToArray()),
                RequirementIds = Array.AsReadOnly(reservation.RequirementIds.ToArray())
            }, record));
        }
        if (entries.Any(e => e.Kind == "result" && !executions.Any(x => x.Reservation.TestExecutionId.ToString("D") == e.Id))) Invalid();
        Executions = executions.AsReadOnly();
    }

    public ConformanceProfile Profile(string hash) => ConformanceDocuments.ReadProfile(Document("profile", hash));
    public ReleaseCandidateDefinition Candidate(string hash) => ConformanceDocuments.ReadCandidate(Document("candidate", hash));
    public QualificationContextDefinition Context(string hash) => ConformanceDocuments.ReadContext(Document("context", hash));
    private FrozenConformanceDocument Document(string kind, string hash)
    {
        ConformanceFacility.Hash(hash);
        var entry = _entries.SingleOrDefault(e => e.Kind == kind && e.Id == hash)
            ?? throw new InvalidOperationException("ConformanceFrozenDocumentMissing");
        var document = ConformanceFacility.Deserialize<FrozenConformanceDocument>(entry.Payload);
        if (document.Sha256 != hash) Invalid();
        return document;
    }

    public IReadOnlyList<ConformanceExecutionView> GetExecutions(long afterSequence, int limit)
    {
        if (afterSequence < 0 || limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        return Array.AsReadOnly(Executions.Where(e => e.Sequence > afterSequence).Take(limit).ToArray());
    }

    public ConformanceAggregate Aggregate(string candidateHash, string contextHash, QualificationLayer layer)
    {
        _ = Candidate(candidateHash);
        var context = Context(contextHash);
        var profile = Profile(context.ProfileHash);
        if (!Enum.IsDefined(typeof(QualificationLayer), layer) || !profile.RequiredLayers.Contains(layer))
            throw new ArgumentException("ConformanceLayerNotFrozen");
        var failed = Executions.Any(e => e.Reservation.CandidateHash == candidateHash && e.Outcome == ConformanceOutcome.Fail);
        var relevant = Executions.Where(e => e.Reservation.CandidateHash == candidateHash &&
            e.Reservation.ContextHash == contextHash && e.Reservation.Layer == layer).ToArray();
        var rows = profile.Cases.Where(t => t.Layer == layer).Select(t =>
        {
            var latest = relevant.LastOrDefault(e => e.Reservation.TestId == t.TestId);
            return new ConformanceGateRow(t.TestId, latest?.Outcome ?? ConformanceOutcome.NotRun,
                latest?.Reservation.TestExecutionId, latest?.Record?.ReasonCode ?? "ConformanceExecutionMissingOrIncomplete");
        }).ToArray();
        return new ConformanceAggregate(candidateHash, contextHash, layer, Array.AsReadOnly(rows), failed,
            !failed && rows.Length > 0 && rows.All(r => r.Outcome is ConformanceOutcome.Pass or ConformanceOutcome.NotApplicable),
            Array.AsReadOnly(Executions.Where(e => e.Reservation.CandidateHash == candidateHash)
                .Select(e => e.Reservation.TestExecutionId).ToArray()));
    }

    public byte[] ReadArtifact(string sha256)
    {
        ConformanceFacility.Hash(sha256);
        var entry = _entries.SingleOrDefault(e => e.Kind == "artifact" && e.Id == sha256)
            ?? throw new InvalidOperationException("ConformanceArtifactMissing");
        var payload = ConformanceFacility.Deserialize<ConformanceFacility.ArtifactPayload>(entry.Payload);
        if (payload.Length is < 0 or > 32768 || payload.BytesBase64.Length > 43692 ||
            payload.Classification != "PublicTestData" || payload.ContentType != "application/octet-stream") Invalid();
        var bytes = Convert.FromBase64String(payload.BytesBase64);
        if (bytes.Length != payload.Length || Convert.ToHexString(SHA256.HashData(bytes)) != sha256) Invalid();
        return bytes;
    }
    private void VerifyArtifact(ConformanceArtifactReference reference, long beforeSequence)
    {
        ConformanceFacility.Identifier(reference.Name);
        var entry = _entries.SingleOrDefault(e => e.Kind == "artifact" && e.Id == reference.Sha256);
        if (entry is null || entry.Sequence >= beforeSequence || reference.ContentType != "application/octet-stream" ||
            ReadArtifact(reference.Sha256).Length != reference.Length) Invalid();
    }
    private void VerifyTerminal(ConformanceExecutionReservation reservation, TestExecutionRecord record, VerificationCase test)
    {
        if (record.Outputs.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != record.Outputs.Count) Invalid();
        var rawReference = record.Outputs.SingleOrDefault(a => a.Name == "facility-observation");
        if (rawReference is null) Invalid();
        var raw = ConformanceFacility.Deserialize<ConformanceFacility.FacilityObservationPayload>(
            ConformanceBindings.Utf8.GetString(ReadArtifact(rawReference!.Sha256)));
        if (raw.Schema != "observation-v1" || raw.Expected != reservation.ExpectedObservable ||
            raw.Observed != record.Observed || raw.ReasonCode != record.ReasonCode || raw.Anomalies != record.Anomalies ||
            raw.Outcome != record.Outcome) Invalid();
        if (record.Outcome is ConformanceOutcome.Pass or ConformanceOutcome.Fail)
        {
            if (!test.Applicable || test.Method != VerificationMethod.Executable || test.AcceptanceRule != "exact-text-v1" ||
                test.EvidenceContract != "public-test-data-v1" || test.RequiredEnvironment != "observed-environment" ||
                (record.Outcome == ConformanceOutcome.Pass) != string.Equals(record.Observed, test.ExpectedObservable, StringComparison.Ordinal) ||
                record.ReasonCode != (record.Outcome == ConformanceOutcome.Pass ? "FrozenExpectedObserved" : "ProductObservationMismatch")) Invalid();
        }
        else if (record.Outcome == ConformanceOutcome.InvalidHarness)
        {
            if (record.ReasonCode is not ("ConformanceHarnessChanged" or "ConformanceScenarioChanged" or
                "ConformanceContextBytesChanged" or "ConformanceEnvironmentChanged" or "ConformanceInputChanged")) Invalid();
            var mismatch = record.Outputs.SingleOrDefault(a => a.Name == "binding-mismatch");
            if (mismatch is null) Invalid();
            using var json = System.Text.Json.JsonDocument.Parse(ReadArtifact(mismatch!.Sha256));
            var element = json.RootElement;
            if (element.GetProperty("Schema").GetString() != "binding-mismatch-v1" ||
                element.GetProperty("Code").GetString() != record.ReasonCode) Invalid();
            var expected = element.GetProperty("ExpectedManifestHash").GetString()!;
            var actual = element.GetProperty("ActualManifestHash").GetString()!;
            ConformanceFacility.Hash(expected); ConformanceFacility.Hash(actual);
            if (actual == expected) Invalid();
        }
        else if (record.Outcome == ConformanceOutcome.NotApplicable)
        {
            var expected = $"excluded:{test.TestId}:unreachable:{reservation.CandidateHash}";
            var proof = reservation.Inputs.SingleOrDefault(a => a.Name == "applicability-proof");
            if (proof is null || test.ExclusionEvidence != ConformanceBindings.HashText(expected) ||
                proof.Sha256 != test.ExclusionEvidence || record.Observed != expected || record.ReasonCode != "FrozenOptionalExclusion") Invalid();
        }
        else if (record.Outcome != ConformanceOutcome.Blocked || record.ReasonCode is not
            ("ConformanceNotExecuted" or "ConformanceExclusionProofMissing" or "ConformanceVerificationMethodUnavailable" or
            "ConformanceRequiredEnvironmentUnavailable" or "ConformanceScenarioUnavailable" or "ConformanceScenarioBlocked" or
            "ConformanceExecutionCancelled" or "ConformanceScenarioDeadlineExceeded" or "ConformanceExecutionUnavailable" or
            "ConformanceCandidateBytesChanged" or "ConformanceCandidateBindingOutsideDirectory" or
            "ConformanceContextBindingInsideCandidateDirectory" or "ConformanceRawObservationCapacityExceeded")) Invalid();
    }
    private static void Invalid() => throw new InvalidOperationException("ConformanceRecordIntegrityInvalid");
}
