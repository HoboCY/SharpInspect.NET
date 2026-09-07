namespace SharpInspect.Abstractions;

public enum ConformanceEvidenceClassification { PublicTestData, Sensitive }
public enum ConformanceObservationStatus { Observed, Blocked }

/// <summary>Only explicitly public test data is eligible for the development evidence ledger.</summary>
public sealed class ConformanceEvidence
{
    private readonly byte[] _bytes;
    public ConformanceEvidence(string name, byte[] bytes,
        ConformanceEvidenceClassification classification = ConformanceEvidenceClassification.PublicTestData)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > 32768) throw new ArgumentOutOfRangeException(nameof(bytes));
        Name = name; Classification = classification; _bytes = bytes.ToArray();
    }
    public string Name { get; }
    public ConformanceEvidenceClassification Classification { get; }
    public byte[] GetBytes() => _bytes.ToArray();
}

/// <summary>The scenario supplies observations; it cannot select Pass, Fail, or waive a requirement.</summary>
public sealed class ConformanceObservation
{
    public ConformanceObservation(string observed, IReadOnlyList<ConformanceEvidence>? evidence = null,
        ConformanceObservationStatus status = ConformanceObservationStatus.Observed, string reason = "",
        ConformanceEvidenceClassification classification = ConformanceEvidenceClassification.PublicTestData)
    {
        Observed = observed; Status = status; Reason = reason; Classification = classification;
        if (evidence is { Count: > 8 }) throw new ArgumentOutOfRangeException(nameof(evidence));
        Evidence = Array.AsReadOnly((evidence ?? Array.Empty<ConformanceEvidence>()).ToArray());
    }
    public string Observed { get; }
    public ConformanceObservationStatus Status { get; }
    public string Reason { get; }
    public ConformanceEvidenceClassification Classification { get; }
    public IReadOnlyList<ConformanceEvidence> Evidence { get; }
}

public sealed record ConformanceExecutionContext(Guid TestExecutionId, string CandidateHash,
    string ContextHash, string TestId, string Preconditions, string Stimulus,
    IReadOnlyList<ConformanceEvidence> Inputs);

/// <summary>
/// Explicitly registered trusted development test code. No ledger writer or production authority
/// is provided to the scenario. This contract is not a sandbox or a hardware qualification.
/// </summary>
public interface IConformanceScenario
{
    string TestId { get; }
    string ScenarioHash { get; }
    Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context, CancellationToken cancellationToken);
}

public sealed record ConformanceArtifactReference(string Name, string Sha256, int Length,
    string ContentType = "application/octet-stream");

public sealed record ConformanceExecutionReservation(Guid TestExecutionId, string CandidateHash,
    string ContextHash, string ProfileHash, string TestId, QualificationLayer Layer,
    IReadOnlyList<string> RequirementIds, string EnvironmentJson, string ExpectedObservable,
    IReadOnlyList<ConformanceArtifactReference> Inputs, DateTimeOffset ReservedAtUtc,
    Guid? PredecessorExecutionId, string SystemPrincipalId);

public sealed record TestExecutionRecord(Guid TestExecutionId, ConformanceOutcome Outcome,
    string Observed, string ReasonCode, IReadOnlyList<ConformanceArtifactReference> Outputs,
    DateTimeOffset CompletedAtUtc, long DurationMilliseconds, string Anomalies,
    string EvidencePurpose = "DevelopmentOnly");

public sealed record ConformanceExecutionView(long Sequence, ConformanceExecutionReservation Reservation,
    TestExecutionRecord? Record)
{
    public ConformanceOutcome Outcome => Record?.Outcome ?? ConformanceOutcome.NotRun;
}

public sealed record ConformanceGateRow(string TestId, ConformanceOutcome Outcome,
    Guid? TestExecutionId, string ReasonCode);

/// <summary>A diagnostic projection of immutable records; it never issues a qualification.</summary>
public sealed record ConformanceAggregate(string CandidateHash, string ContextHash,
    QualificationLayer Layer, IReadOnlyList<ConformanceGateRow> Gates,
    bool HasProductFailure, bool AllSelectedCasesSatisfied,
    IReadOnlyList<Guid> ExecutionLineage)
{
    public bool CanIssueQualification => false;
    public string QualificationStatus => HasProductFailure ? "CandidateFailed" : "IncompleteDevelopmentEvidence";
}

public interface IConformanceQuery
{
    IReadOnlyList<ConformanceExecutionView> GetExecutions(long afterSequence = 0, int limit = 100);
    ConformanceAggregate Aggregate(string candidateHash, string contextHash, QualificationLayer layer);
    byte[] ReadArtifact(string sha256);
}
