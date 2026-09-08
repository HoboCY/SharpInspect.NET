using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>The source of a physical setup declaration, not a claim that software senses optical movement.</summary>
public enum ImagingSetupChangeOrigin { OperatorDeclared = 0, SystemDetected = 1 }

/// <summary>Bounded physical descriptions recorded by an authorized person after optical maintenance.</summary>
public sealed record ImagingSetupDefinition
{
    public ImagingSetupDefinition(string lensIdentity, string focusOrFocalLengthState,
        string mountingPose, double workingDistanceMm, string sensorOrientation)
    {
        LensIdentity = Text(lensIdentity, nameof(lensIdentity));
        FocusOrFocalLengthState = Text(focusOrFocalLengthState, nameof(focusOrFocalLengthState));
        MountingPose = Text(mountingPose, nameof(mountingPose));
        if (!double.IsFinite(workingDistanceMm) || workingDistanceMm <= 0 || workingDistanceMm > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(workingDistanceMm));
        WorkingDistanceMm = workingDistanceMm;
        SensorOrientation = Text(sensorOrientation, nameof(sensorOrientation));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-imaging-setup-definition-v1", LensIdentity, FocusOrFocalLengthState,
            MountingPose, WorkingDistanceMm.ToString("R", CultureInfo.InvariantCulture), SensorOrientation
        });
    }
    public string LensIdentity { get; }
    public string FocusOrFocalLengthState { get; }
    public string MountingPose { get; }
    public double WorkingDistanceMm { get; }
    public string SensorOrientation { get; }
    public string ContentHash { get; }
    private static string Text(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("ImagingSetupDescriptionRequired", parameter);
        return AlgorithmContractValidation.BoundedText(value, parameter, 128);
    }
}

/// <summary>Exact, immutable imaging revision returned by the authoritative declaration ledger.</summary>
public sealed class ImagingSetupRevision
{
    internal ImagingSetupRevision(long position, string logicalCameraRole, long revision, Guid operationId,
        string? previousRevisionHash, CameraBindingRevision binding, ImagingSetupDefinition definition,
        ImagingSetupChangeOrigin origin, Guid actorPrincipalId, Guid sessionId, long authorizationRevision,
        string changeReason, DateTimeOffset recordedAtUtc)
    {
        if (position < 1 || revision < 1 || operationId == Guid.Empty || actorPrincipalId == Guid.Empty ||
            sessionId == Guid.Empty || authorizationRevision < 0)
            throw new ArgumentException("ImagingSetupRevisionIdentityInvalid");
        LogicalCameraRole = AlgorithmConfigurationValidation.Identifier(logicalCameraRole, nameof(logicalCameraRole));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (binding.LogicalRole != logicalCameraRole || binding.Revision < 1)
            throw new ArgumentException("ImagingSetupBindingMismatch");
        if (revision == 1 ? previousRevisionHash is not null : previousRevisionHash is null)
            throw new ArgumentException("ImagingSetupPredecessorInvalid");
        PreviousRevisionHash = previousRevisionHash is null ? null : CameraSetupValidation.Hash(previousRevisionHash,
            nameof(previousRevisionHash));
        Origin = AlgorithmConfigurationValidation.Enum(origin, nameof(origin));
        if (string.IsNullOrWhiteSpace(changeReason)) throw new ArgumentException("ImagingSetupChangeReasonRequired", nameof(changeReason));
        ChangeReason = AlgorithmContractValidation.BoundedText(changeReason, nameof(changeReason), 512);
        Position = position; Revision = revision; OperationId = operationId;
        ActorPrincipalId = actorPrincipalId; SessionId = sessionId; AuthorizationRevision = authorizationRevision;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        RevisionHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-imaging-setup-revision-v1", LogicalCameraRole,
            Revision.ToString(CultureInfo.InvariantCulture), OperationId.ToString("D"), PreviousRevisionHash,
            Binding.Revision.ToString(CultureInfo.InvariantCulture), Binding.RevisionHash,
            Binding.Target.ContentHash, Definition.ContentHash, Origin.ToString(),
            ActorPrincipalId.ToString("D"), SessionId.ToString("D"), AuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            ChangeReason, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }
    public long Position { get; }
    public string LogicalCameraRole { get; }
    public long Revision { get; }
    public Guid RevisionId => OperationId;
    public Guid OperationId { get; }
    public string? PreviousRevisionHash { get; }
    public string RevisionHash { get; }
    public CameraBindingRevision Binding { get; }
    public ImagingSetupDefinition Definition { get; }
    public ImagingSetupChangeOrigin Origin { get; }
    public Guid ActorPrincipalId { get; }
    public Guid SessionId { get; }
    public long AuthorizationRevision { get; }
    public string ChangeReason { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public bool AutomaticallyDetectsAllPhysicalChanges => false;
    public bool InvalidatesEarlierCalibrationProfiles => true;
    public bool ProductionReady => false;
}

/// <summary>Compare-and-swap request. Its complete physical intent is bound to one Step-Up grant.</summary>
public sealed class ImagingSetupChangeRequest
{
    public ImagingSetupChangeRequest(Guid operationId, CommandInvocation invocation, string logicalCameraRole,
        long expectedBindingRevision, string expectedBindingRevisionHash, long expectedRevision,
        string? expectedRevisionHash, ImagingSetupDefinition definition, string changeReason)
    {
        if (operationId == Guid.Empty || expectedBindingRevision < 1 || expectedRevision < 0)
            throw new ArgumentException("ImagingSetupChangeIdentityInvalid");
        if (expectedRevision == 0 ? expectedRevisionHash is not null : expectedRevisionHash is null)
            throw new ArgumentException("ImagingSetupExpectedRevisionInvalid");
        OperationId = operationId;
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        LogicalCameraRole = AlgorithmConfigurationValidation.Identifier(logicalCameraRole, nameof(logicalCameraRole));
        ExpectedBindingRevision = expectedBindingRevision;
        ExpectedBindingRevisionHash = CameraSetupValidation.Hash(expectedBindingRevisionHash, nameof(expectedBindingRevisionHash));
        ExpectedRevision = expectedRevision;
        ExpectedRevisionHash = expectedRevisionHash is null ? null : CameraSetupValidation.Hash(expectedRevisionHash, nameof(expectedRevisionHash));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(changeReason)) throw new ArgumentException("ImagingSetupChangeReasonRequired", nameof(changeReason));
        ChangeReason = AlgorithmContractValidation.BoundedText(changeReason, nameof(changeReason), 512);
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-imaging-setup-change-v1", LogicalCameraRole,
            ExpectedBindingRevision.ToString(CultureInfo.InvariantCulture), ExpectedBindingRevisionHash,
            ExpectedRevision.ToString(CultureInfo.InvariantCulture), ExpectedRevisionHash,
            Definition.ContentHash, ChangeReason
        });
    }
    public Guid OperationId { get; }
    public CommandInvocation Invocation { get; }
    public string LogicalCameraRole { get; }
    public long ExpectedBindingRevision { get; }
    public string ExpectedBindingRevisionHash { get; }
    public long ExpectedRevision { get; }
    public string? ExpectedRevisionHash { get; }
    public ImagingSetupDefinition Definition { get; }
    public string ChangeReason { get; }
    public string AuthorizationTarget { get; }
}

public sealed record ImagingSetupQueryResult(bool Available, string ReasonCode, ImagingSetupRevision? Current = null);
public sealed record ImagingSetupChangeResult(bool Succeeded, string ReasonCode, AuditPersistence AuditPersistence,
    ImagingSetupRevision? Revision = null);
public sealed record ImagingSetupHistoryResult(bool Available, string ReasonCode,
    IReadOnlyList<ImagingSetupRevision> Revisions, long ThroughPosition, long? NextAfterPosition);

public interface IImagingSetupRuntime
{
    ValueTask<ImagingSetupQueryResult> GetImagingSetupAsync(string logicalCameraRole, CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<ImagingSetupChangeResult> DeclareImagingSetupAsync(ImagingSetupChangeRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<ImagingSetupHistoryResult> QueryImagingSetupHistoryAsync(string logicalCameraRole,
        CommandInvocation invocation, long afterPosition = 0, long? throughPosition = null, int pageSize = 50,
        CancellationToken cancellationToken = default);
}
