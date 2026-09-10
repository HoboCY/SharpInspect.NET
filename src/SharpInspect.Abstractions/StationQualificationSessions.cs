using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// The five production destinations that a qualification facility must bind and
/// isolate before it can provide a stimulus.  The values are a closed contract;
/// a missing or duplicated destination is never treated as isolated by default.
/// </summary>
public enum QualificationDestinationKind : byte
{
    ProductionPlcOutput = 1,
    OrdinaryOutbox = 2,
    Mes = 3,
    Spc = 4,
    Yield = 5
}

/// <summary>Raw route observed by a trusted qualification facility.</summary>
public enum QualificationDestinationRoute : byte
{
    Unknown = 1,
    Production = 2,
    IsolatedTest = 3,
    Disconnected = 4
}

/// <summary>
/// Exact identity of the versioned development harness.  A harness cannot be
/// used as production qualification authority, even when all of its observations
/// are healthy.
/// </summary>
public sealed record QualificationHarnessIdentity
{
    public QualificationHarnessIdentity(string id, string version, string contentHash,
        string coveredPathHash, bool developmentOnly)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        ContentHash = QualificationContractValidation.Hash(contentHash, nameof(contentHash));
        CoveredPathHash = QualificationContractValidation.Hash(coveredPathHash, nameof(coveredPathHash));
        DevelopmentOnly = developmentOnly;
    }

    public string Id { get; }
    public string Version { get; }
    public string ContentHash { get; }
    public string CoveredPathHash { get; }
    public bool DevelopmentOnly { get; }
}

/// <summary>
/// Transient controller settings used by an isolated qualification harness.
/// The bytes are copied on construction and on every read; they are never a
/// production controller configuration or a caller-owned buffer.
/// </summary>
public sealed class QualificationControllerConfiguration
{
    public const int MaximumConfigurationBytes = 64 * 1024;
    private readonly byte[] _configuration;

    public QualificationControllerConfiguration(string endpointBindingHash,
        string protocolBindingHash, ReadOnlyMemory<byte> configuration)
    {
        EndpointBindingHash = QualificationContractValidation.Hash(endpointBindingHash,
            nameof(endpointBindingHash));
        ProtocolBindingHash = QualificationContractValidation.Hash(protocolBindingHash,
            nameof(protocolBindingHash));
        if (configuration.Length is < 1 or > MaximumConfigurationBytes)
            throw new ArgumentException("QualificationControllerConfigurationCapacityExceeded",
                nameof(configuration));
        _configuration = configuration.ToArray();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-qualification-controller-configuration-v1",
            EndpointBindingHash, ProtocolBindingHash, Convert.ToHexString(_configuration)
        });
    }

    public string EndpointBindingHash { get; }
    public string ProtocolBindingHash { get; }

    /// <summary>Returns a fresh read-only memory owner on every access.</summary>
    public ReadOnlyMemory<byte> Configuration => new(_configuration.ToArray());

    /// <summary>Descriptive alias for consumers that prefer an explicit byte name.</summary>
    public ReadOnlyMemory<byte> ConfigurationBytes => new(_configuration.ToArray());

    public int ConfigurationLength => _configuration.Length;
    public string ContentHash { get; }

    public byte[] GetConfigurationBytes() => (byte[])_configuration.Clone();
}

/// <summary>One exact destination binding required by a qualification plan.</summary>
public sealed record QualificationDestinationBinding
{
    public QualificationDestinationBinding(QualificationDestinationKind kind,
        string destinationId, string targetBindingHash)
    {
        Kind = QualificationContractValidation.Enum(kind, nameof(kind));
        DestinationId = QualificationContractValidation.Text(destinationId, nameof(destinationId), 256);
        TargetBindingHash = QualificationContractValidation.Hash(targetBindingHash,
            nameof(targetBindingHash));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-qualification-destination-binding-v1", Kind.ToString(),
            DestinationId, TargetBindingHash
        });
    }

    public QualificationDestinationKind Kind { get; }
    public string DestinationId { get; }
    public string TargetBindingHash { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Complete immutable plan for one non-production station qualification session.
/// Target station identity and transient qualification context remain distinct
/// inputs even when a caller happens to use equal hash values.
/// </summary>
public sealed class StationQualificationPlan
{
    private static readonly QualificationDestinationKind[] RequiredDestinationKinds =
    {
        QualificationDestinationKind.ProductionPlcOutput,
        QualificationDestinationKind.OrdinaryOutbox,
        QualificationDestinationKind.Mes,
        QualificationDestinationKind.Spc,
        QualificationDestinationKind.Yield
    };

    public StationQualificationPlan(RecipeActivationReference targetActivation,
        string targetStationFingerprint, string releaseCandidateFingerprint, string profileHash,
        string qualificationContextHash, QualificationHarnessIdentity qualificationHarnessIdentity,
        QualificationControllerConfiguration targetControllerConfiguration,
        QualificationControllerConfiguration transientControllerConfiguration,
        IEnumerable<QualificationDestinationBinding> destinations,
        IEnumerable<string> scenarioIds)
    {
        TargetActivation = targetActivation ?? throw new ArgumentNullException(nameof(targetActivation));
        TargetStationFingerprint = QualificationContractValidation.Hash(targetStationFingerprint,
            nameof(targetStationFingerprint));
        ReleaseCandidateFingerprint = QualificationContractValidation.Hash(releaseCandidateFingerprint,
            nameof(releaseCandidateFingerprint));
        ProfileHash = QualificationContractValidation.Hash(profileHash, nameof(profileHash));
        QualificationContextHash = QualificationContractValidation.Hash(qualificationContextHash,
            nameof(qualificationContextHash));
        QualificationHarnessIdentity = qualificationHarnessIdentity ??
            throw new ArgumentNullException(nameof(qualificationHarnessIdentity));
        TargetControllerConfiguration = targetControllerConfiguration ??
            throw new ArgumentNullException(nameof(targetControllerConfiguration));
        TransientControllerConfiguration = transientControllerConfiguration ??
            throw new ArgumentNullException(nameof(transientControllerConfiguration));

        var copiedDestinations = AlgorithmContractValidation.Copy(destinations,
            nameof(destinations), RequiredDestinationKinds.Length).ToArray();
        if (copiedDestinations.Length != RequiredDestinationKinds.Length ||
            copiedDestinations.Select(value => value.Kind).Distinct().Count() != copiedDestinations.Length ||
            RequiredDestinationKinds.Any(kind => !copiedDestinations.Any(value => value.Kind == kind)))
            throw new ArgumentException("QualificationDestinationSetIncomplete", nameof(destinations));
        if (copiedDestinations.Select(value => value.DestinationId)
                .Distinct(StringComparer.Ordinal).Count() != copiedDestinations.Length)
            throw new ArgumentException("QualificationDestinationIdDuplicate", nameof(destinations));
        DestinationBindings = new ReadOnlyCollection<QualificationDestinationBinding>(
            copiedDestinations.OrderBy(value => value.Kind).ToArray());

        var scenarioValues = AlgorithmContractValidation.Copy(scenarioIds, nameof(scenarioIds), 64)
            .Select(value => QualificationContractValidation.Identifier(value, nameof(scenarioIds), 128))
            .ToArray();
        var copiedScenarios = scenarioValues.Distinct(StringComparer.Ordinal).ToArray();
        if (copiedScenarios.Length == 0)
            throw new ArgumentException("QualificationScenarioSetRequired", nameof(scenarioIds));
        if (copiedScenarios.Length != scenarioValues.Length)
            throw new ArgumentException("QualificationScenarioIdDuplicate", nameof(scenarioIds));
        ScenarioIds = new ReadOnlyCollection<string>(copiedScenarios.OrderBy(value => value,
            StringComparer.Ordinal).ToArray());

        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-station-qualification-plan-v1",
            TargetActivation.Position.ToString(CultureInfo.InvariantCulture),
            TargetActivation.ActivationId.ToString("D"), TargetActivation.ContentHash,
            TargetStationFingerprint, ReleaseCandidateFingerprint, ProfileHash,
            QualificationContextHash, QualificationHarnessIdentity.Id,
            QualificationHarnessIdentity.Version, QualificationHarnessIdentity.ContentHash,
            QualificationHarnessIdentity.CoveredPathHash,
            QualificationHarnessIdentity.DevelopmentOnly.ToString(),
            TargetControllerConfiguration.ContentHash, TransientControllerConfiguration.ContentHash,
            string.Join("|", DestinationBindings.Select(value => value.ContentHash)),
            string.Join("|", ScenarioIds)
        });
    }

    public RecipeActivationReference TargetActivation { get; }
    public string TargetStationFingerprint { get; }
    public string ReleaseCandidateFingerprint { get; }
    public string ProfileHash { get; }
    public string QualificationContextHash { get; }
    public QualificationHarnessIdentity QualificationHarnessIdentity { get; }
    public QualificationHarnessIdentity Harness => QualificationHarnessIdentity;
    public QualificationControllerConfiguration TargetControllerConfiguration { get; }
    public QualificationControllerConfiguration TransientControllerConfiguration { get; }
    public IReadOnlyList<QualificationDestinationBinding> DestinationBindings { get; }
    public IReadOnlyList<QualificationDestinationBinding> Destinations => DestinationBindings;
    public IReadOnlyList<string> ScenarioIds { get; }
    public string ContentHash { get; }
    public string PlanHash => ContentHash;
}

/// <summary>Base command for the qualification session boundary.</summary>
public abstract record StationQualificationCommand : RuntimeCommand
{
    protected StationQualificationCommand(Guid correlationId, CommandInvocation invocation, string reason)
        : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty)
            throw new ArgumentException("QualificationCommandCorrelationRequired", nameof(correlationId));
        ArgumentNullException.ThrowIfNull(invocation);
        Reason = QualificationContractValidation.Reason(reason, nameof(reason));
    }

    public string Reason { get; }
    public abstract string AuthorizationTarget { get; }

    protected string Target(string operation, params string?[] values) =>
        AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-station-qualification-command-v1", operation, Reason
        }.Concat(values));
}

/// <summary>Admits one exact frozen plan; the Runtime assigns the session identity.</summary>
public sealed record StartStationQualificationSessionCommand : StationQualificationCommand
{
    public StartStationQualificationSessionCommand(Guid correlationId, CommandInvocation invocation,
        StationQualificationPlan plan, string reason) : base(correlationId, invocation, reason)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        AuthorizationTarget = Target("Start", Plan.ContentHash);
    }

    public StationQualificationPlan Plan { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>Requests a normal or aborting session exit; it never carries a run identity.</summary>
public sealed record ExitStationQualificationSessionCommand : StationQualificationCommand
{
    public ExitStationQualificationSessionCommand(Guid correlationId, CommandInvocation invocation,
        Guid sessionId, bool abort, string reason) : base(correlationId, invocation, reason)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("QualificationSessionIdRequired", nameof(sessionId));
        SessionId = sessionId;
        Abort = abort;
        AuthorizationTarget = Target("Exit", sessionId.ToString("D"), abort.ToString());
    }

    public Guid SessionId { get; }
    public bool Abort { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>
/// Runtime-issued qualification run identity.  Public consumers can observe it
/// but cannot construct one or use it as a production/manual correlation.
/// </summary>
public sealed class QualificationRunId
{
    internal QualificationRunId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("QualificationRunIdRequired", nameof(value));
        Value = value;
        Correlation = new ExecutionCorrelationId(ExecutionKind.Qualification, value);
    }

    public Guid Value { get; }
    public ExecutionCorrelationId Correlation { get; }
}

public enum StationQualificationSessionPhase : byte
{
    Idle = 1,
    Admitted = 2,
    Isolating = 3,
    ReadyForStimulus = 4,
    Running = 5,
    Restoring = 6,
    Closed = 7,
    RecoveryBlocked = 8
}

public enum StationQualificationRestorationState : byte
{
    NotRequired = 1,
    Pending = 2,
    Restored = 3,
    RecoveryBlocked = 4
}

/// <summary>Runtime-owned, read-only session projection.</summary>
public sealed class StationQualificationSessionSnapshot
{
    internal StationQualificationSessionSnapshot(Guid runtimeEpoch, long revision, Guid? sessionId,
        StationQualificationSessionPhase phase, string reasonCode, DateTimeOffset observedAtUtc,
        Guid? actorPrincipalId, Guid? actorSessionId, StationQualificationPlan? plan,
        QualificationRunId? currentRunId, QualificationRunId? lastRunId,
        StationQualificationRestorationState restoration, bool exitRequested, bool recoveryRequired,
        Guid? lastCommandCorrelationId)
    {
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("QualificationRuntimeEpochRequired", nameof(runtimeEpoch));
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(typeof(StationQualificationSessionPhase), phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(typeof(StationQualificationRestorationState), restoration))
            throw new ArgumentOutOfRangeException(nameof(restoration));
        FrameMetadataValidation.Utc(observedAtUtc, nameof(observedAtUtc));
        if (sessionId == Guid.Empty || actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty ||
            lastCommandCorrelationId == Guid.Empty)
            throw new ArgumentException("QualificationSnapshotGuidInvalid");
        if (currentRunId is not null && currentRunId.Correlation.Kind != ExecutionKind.Qualification)
            throw new ArgumentException("QualificationRunCorrelationInvalid", nameof(currentRunId));
        if (lastRunId is not null && lastRunId.Correlation.Kind != ExecutionKind.Qualification)
            throw new ArgumentException("QualificationRunCorrelationInvalid", nameof(lastRunId));

        RuntimeEpoch = runtimeEpoch;
        Revision = revision;
        SessionId = sessionId;
        Phase = phase;
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
        ObservedAtUtc = observedAtUtc;
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        Plan = plan;
        CurrentRunId = currentRunId;
        LastRunId = lastRunId;
        Restoration = restoration;
        ExitRequested = exitRequested;
        RecoveryRequired = recoveryRequired;
        LastCommandCorrelationId = lastCommandCorrelationId;
    }

    public Guid RuntimeEpoch { get; }
    public long Revision { get; }
    public Guid? SessionId { get; }
    public StationQualificationSessionPhase Phase { get; }
    public string ReasonCode { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public Guid? ActorPrincipalId { get; }
    public Guid? ActorSessionId { get; }
    public StationQualificationPlan? Plan { get; }
    public QualificationRunId? CurrentRunId { get; }
    public QualificationRunId? LastRunId { get; }
    public StationQualificationRestorationState Restoration { get; }
    public bool ExitRequested { get; }
    public bool RecoveryRequired { get; }

    /// <summary>Correlation of the last accepted command, not a caller-owned run ID.</summary>
    public Guid? LastCommandCorrelationId { get; }

    public bool IsSessionActive => SessionId.HasValue && Phase != StationQualificationSessionPhase.Closed;

    // Qualification is a development-only evidence path.  These are deliberately
    // constants rather than caller-controlled claims in a snapshot constructor.
    public bool Ready => false;
    public bool ProductionAuthority => false;
    public bool CanIssueQualification => false;
}

public sealed record StationQualificationAccess(bool CanRun, string ReasonCode, bool RequiresStepUp)
{
    public bool Available => CanRun;
    public bool CanStart => CanRun;
}

public sealed record StationQualificationSessionReadResult(bool Available, string ReasonCode,
    StationQualificationSessionSnapshot? Snapshot = null);

/// <summary>Read-only session capability; mutation is submitted through Runtime commands.</summary>
public interface IStationQualificationSessionService
{
    ValueTask<StationQualificationAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<StationQualificationSessionReadResult> GetSnapshotAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>Exact host request for one privately-held facility lease.</summary>
public sealed record QualificationFacilityRequest
{
    public QualificationFacilityRequest(Guid sessionId, Guid leaseNonce, Guid runtimeEpoch,
        StationQualificationPlan plan)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("QualificationSessionIdRequired", nameof(sessionId));
        if (leaseNonce == Guid.Empty)
            throw new ArgumentException("QualificationLeaseNonceRequired", nameof(leaseNonce));
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("QualificationRuntimeEpochRequired", nameof(runtimeEpoch));
        SessionId = sessionId;
        LeaseNonce = leaseNonce;
        RuntimeEpoch = runtimeEpoch;
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    public Guid SessionId { get; }
    public Guid LeaseNonce { get; }
    public Guid RuntimeEpoch { get; }
    public StationQualificationPlan Plan { get; }
}

/// <summary>Result of one facility-side physical operation.</summary>
public sealed record QualificationFacilityOperationResult
{
    public QualificationFacilityOperationResult(bool succeeded, string reasonCode)
    {
        Succeeded = succeeded;
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
    }

    public bool Succeeded { get; }
    public string ReasonCode { get; }

    public static QualificationFacilityOperationResult Success(string reasonCode = "QualificationFacilityOperationApplied") =>
        new(true, QualificationContractValidation.Reason(reasonCode, nameof(reasonCode)));

    public static QualificationFacilityOperationResult Failure(string reasonCode) =>
        new(false, QualificationContractValidation.Reason(reasonCode, nameof(reasonCode)));
}

/// <summary>One raw destination observation supplied by the trusted facility.</summary>
public sealed record QualificationDestinationObservation
{
    public QualificationDestinationObservation(QualificationDestinationKind kind, string destinationId,
        string targetBindingHash, QualificationDestinationRoute route, bool outputEnabled)
    {
        Kind = QualificationContractValidation.Enum(kind, nameof(kind));
        DestinationId = QualificationContractValidation.Text(destinationId, nameof(destinationId), 256);
        TargetBindingHash = QualificationContractValidation.Hash(targetBindingHash,
            nameof(targetBindingHash));
        Route = QualificationContractValidation.Enum(route, nameof(route));
        OutputEnabled = outputEnabled;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-qualification-destination-observation-v1", Kind.ToString(),
            DestinationId, TargetBindingHash, Route.ToString(), OutputEnabled.ToString()
        });
    }

    public QualificationDestinationKind Kind { get; }
    public string DestinationId { get; }
    public string TargetBindingHash { get; }
    public QualificationDestinationRoute Route { get; }
    public bool OutputEnabled { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Raw facility observation.  It carries no aggregate qualification result or
/// caller-provided CanQualify flag; Runtime evaluates these facts itself.
/// </summary>
public sealed class QualificationFacilityObservation
{
    public QualificationFacilityObservation(QualificationHarnessIdentity identity, Guid sessionId, Guid leaseNonce,
        Guid runtimeEpoch, long sequence, DateTimeOffset observedAtUtc, bool connected,
        bool lineStopped, string? effectiveControllerConfigurationHash,
        IEnumerable<QualificationDestinationObservation> destinations)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("QualificationSessionIdRequired", nameof(sessionId));
        if (leaseNonce == Guid.Empty)
            throw new ArgumentException("QualificationLeaseNonceRequired", nameof(leaseNonce));
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("QualificationRuntimeEpochRequired", nameof(runtimeEpoch));
        if (sequence < 1)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        FrameMetadataValidation.Utc(observedAtUtc, nameof(observedAtUtc));
        SessionId = sessionId;
        LeaseNonce = leaseNonce;
        RuntimeEpoch = runtimeEpoch;
        Sequence = sequence;
        ObservedAtUtc = observedAtUtc;
        Connected = connected;
        LineStopped = lineStopped;
        EffectiveControllerConfigurationHash = effectiveControllerConfigurationHash is null
            ? null : QualificationContractValidation.Hash(effectiveControllerConfigurationHash,
                nameof(effectiveControllerConfigurationHash));

        var copied = AlgorithmContractValidation.Copy(destinations, nameof(destinations), 5).ToArray();
        if (copied.Select(value => value.Kind).Distinct().Count() != copied.Length ||
            copied.Select(value => value.DestinationId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
            throw new ArgumentException("QualificationDestinationObservationDuplicate", nameof(destinations));
        Destinations = new ReadOnlyCollection<QualificationDestinationObservation>(copied);
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-qualification-facility-observation-v1", Identity.Id, Identity.Version,
            Identity.ContentHash, Identity.CoveredPathHash, Identity.DevelopmentOnly.ToString(),
            SessionId.ToString("D"), LeaseNonce.ToString("D"), RuntimeEpoch.ToString("D"),
            Sequence.ToString(CultureInfo.InvariantCulture), ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Connected.ToString(), LineStopped.ToString(), EffectiveControllerConfigurationHash,
            string.Join("|", Destinations.Select(value => value.ContentHash))
        });
    }

    public QualificationHarnessIdentity Identity { get; }
    public Guid SessionId { get; }
    public Guid LeaseNonce { get; }
    public Guid RuntimeEpoch { get; }
    public long Sequence { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public bool Connected { get; }
    public bool LineStopped { get; }
    public string? EffectiveControllerConfigurationHash { get; }
    public IReadOnlyList<QualificationDestinationObservation> Destinations { get; }
    public string ContentHash { get; }
}

/// <summary>One stimulus pulled from the registered facility lease.</summary>
public sealed record QualificationFacilityStimulus
{
    public QualificationFacilityStimulus(Guid sessionId, Guid leaseNonce, long positiveSequence,
        string scenarioId, string qualificationContextHash, uint controllerEpoch, uint cycleSequence)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("QualificationSessionIdRequired", nameof(sessionId));
        if (leaseNonce == Guid.Empty)
            throw new ArgumentException("QualificationLeaseNonceRequired", nameof(leaseNonce));
        if (positiveSequence < 1)
            throw new ArgumentOutOfRangeException(nameof(positiveSequence));
        SessionId = sessionId;
        LeaseNonce = leaseNonce;
        PositiveSequence = positiveSequence;
        ScenarioId = QualificationContractValidation.Identifier(scenarioId, nameof(scenarioId), 128);
        QualificationContextHash = QualificationContractValidation.Hash(qualificationContextHash,
            nameof(qualificationContextHash));
        ControllerEpoch = controllerEpoch;
        CycleSequence = cycleSequence;
    }

    public Guid SessionId { get; }
    public Guid LeaseNonce { get; }
    public long PositiveSequence { get; }
    public long Sequence => PositiveSequence;
    public string ScenarioId { get; }
    public string QualificationContextHash { get; }
    public uint ControllerEpoch { get; }
    public uint CycleSequence { get; }
}

internal static class QualificationContractValidation
{
    internal static string Hash(string value, string parameterName) =>
        AlgorithmConfigurationValidation.Hash(value, parameterName).ToUpperInvariant();

    internal static string Identifier(string value, string parameterName, int maximumLength = 128) =>
        AlgorithmContractValidation.Identifier(value, parameterName, maximumLength);

    internal static string Text(string value, string parameterName, int maximumLength) =>
        AlgorithmContractValidation.BoundedText(value, parameterName, maximumLength);

    internal static string Reason(string value, string parameterName)
    {
        var result = AlgorithmContractValidation.BoundedText(value, parameterName, 256);
        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException("QualificationReasonRequired", parameterName);
        return result;
    }

    internal static T Enum<T>(T value, string parameterName) where T : struct, System.Enum =>
        System.Enum.IsDefined(typeof(T), value)
            ? value : throw new ArgumentOutOfRangeException(parameterName);
}
