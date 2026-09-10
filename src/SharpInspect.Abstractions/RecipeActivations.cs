using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Identifies which authority produced an activation evidence record.</summary>
public enum RecipeActivationEvidenceKind : byte
{
    LocalAuthority = 1,
    InternalContractFixture = 2
}

public enum RecipeActivationCheckStatus : byte
{
    Passed = 1,
    Failed = 2,
    NotApplicable = 3,
    NotRun = 4
}

public enum RecipeActivationRestorationState : byte
{
    NotRequired = 1,
    Restored = 2,
    NoPreviousBaselineClosed = 3,
    Failed = 4
}

/// <summary>
/// An activation event identity. The content hash belongs to the complete immutable
/// event and therefore cannot be supplied as a mutable lookup key for a different event.
/// </summary>
public sealed record RecipeActivationReference
{
    public RecipeActivationReference(long position, Guid activationId, string contentHash)
    {
        if (position < 1 || activationId == Guid.Empty)
            throw new ArgumentException("RecipeActivationReferenceInvalid");
        Position = position;
        ActivationId = activationId;
        ContentHash = RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
    }

    public long Position { get; }
    public Guid ActivationId { get; }
    public string ContentHash { get; }
}

public enum RecipeActivationOutcomeState : byte
{
    /// <summary>Immutable intent admitted before any physical I/O is started.</summary>
    Admitted = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>One immutable gate result retained with both admission and terminal events.</summary>
public sealed class RecipeActivationCheck
{
    internal RecipeActivationCheck(string checkId, string subject, RecipeActivationCheckStatus status,
        string reasonCode, string? requestedEvidenceHash = null, string? effectiveEvidenceHash = null)
    {
        CheckId = AlgorithmConfigurationValidation.Identifier(checkId, nameof(checkId));
        Subject = AlgorithmContractValidation.BoundedText(subject, nameof(subject), 512);
        Status = AlgorithmConfigurationValidation.Enum(status, nameof(status));
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        RequestedEvidenceHash = RecipeActivationValidation.OptionalHash(requestedEvidenceHash,
            nameof(requestedEvidenceHash));
        EffectiveEvidenceHash = RecipeActivationValidation.OptionalHash(effectiveEvidenceHash,
            nameof(effectiveEvidenceHash));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-activation-check-v1", CheckId, Subject, Status.ToString(),
            ReasonCode, RequestedEvidenceHash, EffectiveEvidenceHash
        });
    }

    public string CheckId { get; }
    public string Subject { get; }
    public RecipeActivationCheckStatus Status { get; }
    public string ReasonCode { get; }
    public string? RequestedEvidenceHash { get; }
    public string? EffectiveEvidenceHash { get; }
    public string ContentHash { get; }
    public bool Satisfied => Status is RecipeActivationCheckStatus.Passed or
        RecipeActivationCheckStatus.NotApplicable;
    /// <summary>ADR-0096 permits these two records to remain absent until the separate arm gate.</summary>
    public bool RequiredForActivation => CheckId is not "V132.A19" and not "V132.A20";
}

/// <summary>Restoration evidence tied to the previous immutable activation baseline.</summary>
public sealed class RecipeActivationRestoration
{
    internal RecipeActivationRestoration(RecipeActivationRestorationState state, string reasonCode,
        string? requestedEvidenceHash = null, string? effectiveEvidenceHash = null,
        CameraSetupSnapshot? actualCamera = null)
    {
        State = AlgorithmConfigurationValidation.Enum(state, nameof(state));
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        RequestedEvidenceHash = RecipeActivationValidation.OptionalHash(requestedEvidenceHash,
            nameof(requestedEvidenceHash));
        EffectiveEvidenceHash = RecipeActivationValidation.OptionalHash(effectiveEvidenceHash,
            nameof(effectiveEvidenceHash));
        ActualCamera = actualCamera;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-activation-restoration-v1", State.ToString(), ReasonCode,
            RequestedEvidenceHash, EffectiveEvidenceHash,
            ActualCamera is null ? null : RecipeActivationValidation.CameraHash(ActualCamera)
        });
    }

    public RecipeActivationRestorationState State { get; }
    public string ReasonCode { get; }
    public string? RequestedEvidenceHash { get; }
    public string? EffectiveEvidenceHash { get; }
    public CameraSetupSnapshot? ActualCamera { get; }
    public string ContentHash { get; }
}

/// <summary>A stable terminal or pre-I/O admission outcome.</summary>
public sealed class RecipeActivationOutcome
{
    internal RecipeActivationOutcome(RecipeActivationOutcomeState state, string reasonCode)
    {
        State = AlgorithmConfigurationValidation.Enum(state, nameof(state));
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-activation-outcome-v1", State.ToString(), ReasonCode
        });
    }

    public RecipeActivationOutcomeState State { get; }
    public string ReasonCode { get; }
    public string ContentHash { get; }
    public bool IsTerminal => State is not RecipeActivationOutcomeState.Admitted;
    public bool Succeeded => State == RecipeActivationOutcomeState.Succeeded;
}

/// <summary>
/// The immutable intent admitted immediately before physical I/O. It freezes the
/// candidate, authorization, and previous activation baseline; it is not a success.
/// </summary>
public sealed class RecipeActivationAdmission
{
    internal RecipeActivationAdmission(long position, Guid activationId, Guid attemptId, Guid operationId,
        RecipeReference candidate, Guid releaseId, string releaseRecordContentHash,
        RecipeActivationReference? expectedActive, RecipeActivationReference? previousActivation,
        RecipeReference? previousRecipe, string? previousSnapshotContentHash,
        IEnumerable<CalibrationProfileSelection>? calibrationSelections, string changeReason,
        Guid actorPrincipalId, Guid actorSessionId, long actorAuthorizationRevision,
        RecipeContractReference authorizationPolicy, string authorizationTarget,
        RecipeActivationEvidenceKind evidenceKind, DateTimeOffset admittedAtUtc,
        HistoricalCalibrationSelectionIntent? historicalSelection = null)
    {
        if (position < 1 || activationId == Guid.Empty || attemptId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("RecipeActivationAdmissionIdentityInvalid");
        Position = position;
        ActivationId = activationId;
        AttemptId = attemptId;
        OperationId = operationId;
        Candidate = RecipeActivationValidation.Recipe(candidate, nameof(candidate));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        ExpectedActive = RecipeActivationValidation.Reference(expectedActive);
        PreviousActivation = RecipeActivationValidation.Reference(previousActivation);
        PreviousRecipe = previousRecipe is null ? null :
            RecipeActivationValidation.Recipe(previousRecipe, nameof(previousRecipe));
        PreviousSnapshotContentHash = RecipeActivationValidation.OptionalHash(previousSnapshotContentHash,
            nameof(previousSnapshotContentHash));
        if (PreviousActivation is null && (PreviousRecipe is not null || PreviousSnapshotContentHash is not null))
            throw new ArgumentException("RecipeActivationPreviousBaselineMismatch");
        if (PreviousActivation is not null && (PreviousRecipe is null || PreviousSnapshotContentHash is null))
            throw new ArgumentException("RecipeActivationPreviousBaselineRequired");
        CalibrationSelections = RecipeActivationValidation.CopySelections(calibrationSelections);
        ChangeReason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
        HistoricalSelection = historicalSelection;
        if (HistoricalSelection is not null &&
            !string.Equals(HistoricalSelection.Reason, ChangeReason, StringComparison.Ordinal))
            throw new ArgumentException("RecipeActivationHistoricalReasonMismatch", nameof(changeReason));
        ActorPrincipalId = RecipeActivationValidation.RequiredGuid(actorPrincipalId, nameof(actorPrincipalId));
        ActorSessionId = RecipeActivationValidation.RequiredGuid(actorSessionId, nameof(actorSessionId));
        if (actorAuthorizationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(actorAuthorizationRevision));
        ActorAuthorizationRevision = actorAuthorizationRevision;
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget, nameof(authorizationTarget));
        EvidenceKind = AlgorithmConfigurationValidation.Enum(evidenceKind, nameof(evidenceKind));
        AdmittedAtUtc = RecipeActivationValidation.Utc(admittedAtUtc, nameof(admittedAtUtc));
        var hashParts = new List<string?>
        {
            HistoricalSelection is null ? "sharpinspect-recipe-activation-admission-v1" :
                "sharpinspect-recipe-activation-admission-v2", Position.ToString(CultureInfo.InvariantCulture),
            ActivationId.ToString("D"), AttemptId.ToString("D"), OperationId.ToString("D"),
            Candidate.Id, Candidate.Version, Candidate.ContentHash, ReleaseId.ToString("D"),
            ReleaseRecordContentHash, ExpectedActive?.Position.ToString(CultureInfo.InvariantCulture),
            ExpectedActive?.ActivationId.ToString("D"), ExpectedActive?.ContentHash,
            PreviousActivation?.Position.ToString(CultureInfo.InvariantCulture),
            PreviousActivation?.ActivationId.ToString("D"), PreviousActivation?.ContentHash,
            PreviousRecipe?.Id, PreviousRecipe?.Version, PreviousRecipe?.ContentHash,
            PreviousSnapshotContentHash, CalibrationSelections.Count.ToString(CultureInfo.InvariantCulture)
        };
        hashParts.AddRange(CalibrationSelections.Select(RecipeActivationValidation.SelectionHash));
        if (HistoricalSelection is not null)
        {
            hashParts.Add(HistoricalSelection.Source);
            hashParts.Add(HistoricalSelection.PreviousExactProfile?.ProfileId.ToString("D"));
            hashParts.Add(HistoricalSelection.PreviousExactProfile?.Version.ToString(CultureInfo.InvariantCulture));
            hashParts.Add(HistoricalSelection.PreviousExactProfile?.ContentHash);
            hashParts.Add(HistoricalSelection.Reason);
            hashParts.Add(HistoricalSelection.ContentHash);
        }
        hashParts.AddRange(new[]
        {
            ChangeReason, ActorPrincipalId.ToString("D"), ActorSessionId.ToString("D"),
            ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            AuthorizationPolicy.Id, AuthorizationPolicy.Version, AuthorizationPolicy.ContentHash,
            AuthorizationTarget, EvidenceKind.ToString(), AdmittedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
        ContentHash = AlgorithmContractValidation.HashParts(hashParts);
    }

    public long Position { get; }
    public Guid ActivationId { get; }
    public Guid AttemptId { get; }
    public Guid OperationId { get; }
    public RecipeReference Candidate { get; }
    public Guid ReleaseId { get; }
    public string ReleaseRecordContentHash { get; }
    public RecipeActivationReference? ExpectedActive { get; }
    public RecipeActivationReference? PreviousActivation { get; }
    public RecipeReference? PreviousRecipe { get; }
    public string? PreviousSnapshotContentHash { get; }
    public ReadOnlyCollection<CalibrationProfileSelection> CalibrationSelections { get; }
    public string ChangeReason { get; }
    public HistoricalCalibrationSelectionIntent? HistoricalSelection { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public string AuthorizationTarget { get; }
    public RecipeActivationEvidenceKind EvidenceKind { get; }
    public DateTimeOffset AdmittedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Frozen runtime evidence used by a successful activation. Its authority kind is
/// explicit; an internal fixture can never become production authority by querying it.
/// </summary>
public sealed class RecipeActivationSnapshot
{
    internal RecipeActivationSnapshot(RecipeActivationEvidenceKind evidenceKind, RecipeReleaseRecord release,
        Guid preparedAlgorithmInstanceId, AlgorithmExecutionPolicy algorithmExecutionPolicy,
        CameraSetupSnapshot cameraSetup, PlcResultContractBinding plcResultContract,
        IEnumerable<CalibrationRunProfileBinding>? calibrationBindings,
        int framePoolCapacity, long framePoolMaximumBytes)
    {
        EvidenceKind = AlgorithmConfigurationValidation.Enum(evidenceKind, nameof(evidenceKind));
        Release = release ?? throw new ArgumentNullException(nameof(release));
        if (preparedAlgorithmInstanceId == Guid.Empty)
            throw new ArgumentException("RecipeActivationPreparedAlgorithmRequired", nameof(preparedAlgorithmInstanceId));
        PreparedAlgorithmInstanceId = preparedAlgorithmInstanceId;
        AlgorithmExecutionPolicy = algorithmExecutionPolicy ??
            throw new ArgumentNullException(nameof(algorithmExecutionPolicy));
        CameraSetup = cameraSetup ?? throw new ArgumentNullException(nameof(cameraSetup));
        PlcResultContract = plcResultContract ?? throw new ArgumentNullException(nameof(plcResultContract));
        CalibrationBindings = RecipeActivationValidation.CopyBindings(calibrationBindings);
        if (framePoolCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(framePoolCapacity));
        if (framePoolMaximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(framePoolMaximumBytes));
        FramePoolCapacity = framePoolCapacity;
        FramePoolMaximumBytes = framePoolMaximumBytes;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-activation-snapshot-v1", EvidenceKind.ToString(), Release.ContentHash,
            PreparedAlgorithmInstanceId.ToString("D"), AlgorithmExecutionPolicy.Id,
            AlgorithmExecutionPolicy.Version, AlgorithmExecutionPolicy.ContentHash,
            RecipeActivationValidation.CameraHash(CameraSetup), PlcResultContract.ContentHash,
            CalibrationBindings.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(CalibrationBindings.OrderBy(value => value.RequirementContentHash, StringComparer.Ordinal)
            .Select(value => value.ContentHash))
            .Concat(new[]
            {
                FramePoolCapacity.ToString(CultureInfo.InvariantCulture),
                FramePoolMaximumBytes.ToString(CultureInfo.InvariantCulture)
            }));
    }

    public RecipeActivationEvidenceKind EvidenceKind { get; }
    public RecipeReleaseRecord Release { get; }
    public RecipeReference Recipe => Release.Recipe;
    public Guid PreparedAlgorithmInstanceId { get; }
    public AlgorithmExecutionPolicy AlgorithmExecutionPolicy { get; }
    public CameraSetupSnapshot CameraSetup { get; }
    public PlcResultContractBinding PlcResultContract { get; }
    public ReadOnlyCollection<CalibrationRunProfileBinding> CalibrationBindings { get; }
    public int FramePoolCapacity { get; }
    public long FramePoolMaximumBytes { get; }
    public string ContentHash { get; }
    public bool DevelopmentOnly => EvidenceKind == RecipeActivationEvidenceKind.InternalContractFixture;
    public bool ProductionAuthority => EvidenceKind == RecipeActivationEvidenceKind.LocalAuthority;
}

/// <summary>
/// Public activation command. It carries only exact immutable identities and choices;
/// gate results and successful snapshots can only be produced inside the Runtime.
/// </summary>
public record ActivateRecipeCommand : RuntimeCommand
{
    public ActivateRecipeCommand(Guid correlationId, CommandInvocation invocation, RecipeReference candidate,
        Guid releaseId, string releaseRecordContentHash, RecipeActivationReference? expectedActive,
        IEnumerable<CalibrationProfileSelection>? calibrationSelections, string changeReason,
        Guid? operationId = null) : this(correlationId, invocation, candidate, releaseId,
            releaseRecordContentHash, expectedActive, calibrationSelections, changeReason,
            historicalSelection: null, operationId: operationId)
    {
    }

    internal ActivateRecipeCommand(Guid correlationId, CommandInvocation invocation, RecipeReference candidate,
        Guid releaseId, string releaseRecordContentHash, RecipeActivationReference? expectedActive,
        IEnumerable<CalibrationProfileSelection>? calibrationSelections, string changeReason,
        HistoricalCalibrationSelectionIntent? historicalSelection, Guid? operationId = null)
        : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty)
            throw new ArgumentException("RecipeActivationCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        Candidate = RecipeActivationValidation.Recipe(candidate, nameof(candidate));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        ExpectedActive = RecipeActivationValidation.Reference(expectedActive);
        CalibrationSelections = RecipeActivationValidation.CopySelections(calibrationSelections);
        ChangeReason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
        HistoricalSelection = historicalSelection;
        if (HistoricalSelection is not null &&
            !string.Equals(HistoricalSelection.Reason, ChangeReason, StringComparison.Ordinal))
            throw new ArgumentException("RecipeActivationHistoricalReasonMismatch", nameof(changeReason));
        OperationId = operationId ?? correlationId;
        if (OperationId == Guid.Empty)
            throw new ArgumentException("RecipeActivationOperationRequired", nameof(operationId));
        if (OperationId != correlationId)
            throw new ArgumentException("RecipeActivationOperationCorrelationMismatch", nameof(operationId));
        AuthorizationTarget = ComputeAuthorizationTarget(Candidate, ReleaseId,
            ReleaseRecordContentHash, ExpectedActive, CalibrationSelections, ChangeReason,
            HistoricalSelection);
    }

    internal static string ComputeAuthorizationTarget(RecipeReference candidate, Guid releaseId,
        string releaseRecordContentHash, RecipeActivationReference? expectedActive,
        IEnumerable<CalibrationProfileSelection>? calibrationSelections, string changeReason,
        HistoricalCalibrationSelectionIntent? historicalSelection = null)
    {
        var candidateCopy = RecipeActivationValidation.Recipe(candidate, nameof(candidate));
        var releaseHash = RecipeActivationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        var selections = RecipeActivationValidation.CopySelections(calibrationSelections);
        var reason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
        var parts = new List<string?>
        {
            historicalSelection is null ? "sharpinspect-activate-recipe-command-v1" :
                "sharpinspect-select-historical-calibration-command-v1", candidateCopy.Id, candidateCopy.Version,
            candidateCopy.ContentHash, RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId)).ToString("D"),
            releaseHash, expectedActive?.Position.ToString(CultureInfo.InvariantCulture),
            expectedActive?.ActivationId.ToString("D"), expectedActive?.ContentHash,
            selections.Count.ToString(CultureInfo.InvariantCulture)
        };
        parts.AddRange(selections.Select(RecipeActivationValidation.SelectionHash));
        if (historicalSelection is not null)
        {
            parts.Add(historicalSelection.Source);
            parts.Add(historicalSelection.PreviousExactProfile?.ProfileId.ToString("D"));
            parts.Add(historicalSelection.PreviousExactProfile?.Version.ToString(CultureInfo.InvariantCulture));
            parts.Add(historicalSelection.PreviousExactProfile?.ContentHash);
            parts.Add(historicalSelection.Reason);
            parts.Add(historicalSelection.ContentHash);
        }
        parts.Add(reason);
        return AlgorithmContractValidation.HashParts(parts);
    }

    public Guid OperationId { get; }
    public RecipeReference Candidate { get; }
    public Guid ReleaseId { get; }
    public string ReleaseRecordContentHash { get; }
    public RecipeActivationReference? ExpectedActive { get; }
    public ReadOnlyCollection<CalibrationProfileSelection> CalibrationSelections { get; }
    public string ChangeReason { get; }
    public HistoricalCalibrationSelectionIntent? HistoricalSelection { get; }
    public string AuthorizationTarget { get; }
}

/// <summary>One append-only activation event, either an admission or a terminal outcome.</summary>
public sealed class RecipeActivationRecord
{
    internal RecipeActivationRecord(long position, Guid activationId, Guid attemptId, Guid operationId,
        RecipeActivationReference? admissionReference, RecipeActivationReference? previousActivation,
        RecipeReference? previousRecipe, string? previousSnapshotContentHash, RecipeReference candidate,
        Guid releaseId, string releaseRecordContentHash, RecipeReference? resultingRecipe,
        RecipeActivationOutcome outcome, IEnumerable<RecipeActivationCheck> checks,
        RecipeActivationRestoration restoration, RecipeActivationSnapshot? successfulSnapshot,
        RecipeActivationEvidenceKind evidenceKind, Guid? actorPrincipalId, Guid? actorSessionId,
        long? actorAuthorizationRevision, RecipeContractReference? authorizationPolicy,
        string changeReason, string authorizationTarget, DateTimeOffset recordedAtUtc,
        RecipeActivationAdmission? admission = null,
        HistoricalCalibrationSelectionIntent? historicalSelection = null)
    {
        if (position < 1 || activationId == Guid.Empty || attemptId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("RecipeActivationRecordIdentityInvalid");
        Position = position;
        ActivationId = activationId;
        AttemptId = attemptId;
        OperationId = operationId;
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        EvidenceKind = AlgorithmConfigurationValidation.Enum(evidenceKind, nameof(evidenceKind));
        AdmissionReference = admissionReference;
        PreviousActivation = RecipeActivationValidation.Reference(previousActivation);
        PreviousRecipe = previousRecipe is null ? null :
            RecipeActivationValidation.Recipe(previousRecipe, nameof(previousRecipe));
        PreviousSnapshotContentHash = RecipeActivationValidation.OptionalHash(previousSnapshotContentHash,
            nameof(previousSnapshotContentHash));
        if (PreviousActivation is null && (PreviousRecipe is not null || PreviousSnapshotContentHash is not null))
            throw new ArgumentException("RecipeActivationPreviousBaselineMismatch");
        if (PreviousActivation is not null && (PreviousRecipe is null || PreviousSnapshotContentHash is null))
            throw new ArgumentException("RecipeActivationPreviousBaselineRequired");
        Candidate = RecipeActivationValidation.Recipe(candidate, nameof(candidate));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        ResultingRecipe = resultingRecipe is null ? null :
            RecipeActivationValidation.Recipe(resultingRecipe, nameof(resultingRecipe));
        Checks = RecipeActivationValidation.CopyChecks(checks);
        Restoration = restoration ?? throw new ArgumentNullException(nameof(restoration));
        SuccessfulSnapshot = successfulSnapshot;
        ChangeReason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget, nameof(authorizationTarget));
        RecordedAtUtc = RecipeActivationValidation.Utc(recordedAtUtc, nameof(recordedAtUtc));
        Admission = admission;
        HistoricalSelection = historicalSelection ?? admission?.HistoricalSelection;
        if (HistoricalSelection is not null &&
            !string.Equals(HistoricalSelection.Reason, ChangeReason, StringComparison.Ordinal))
            throw new ArgumentException("RecipeActivationHistoricalReasonMismatch", nameof(changeReason));
        if (admission is not null && historicalSelection is not null &&
            !Equals(historicalSelection, admission.HistoricalSelection))
            throw new ArgumentException("RecipeActivationHistoricalSelectionMismatch", nameof(historicalSelection));

        ValidateActor(actorPrincipalId, actorSessionId, actorAuthorizationRevision, authorizationPolicy,
            Outcome.State is RecipeActivationOutcomeState.Admitted or RecipeActivationOutcomeState.Succeeded);
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        ActorAuthorizationRevision = actorAuthorizationRevision;
        AuthorizationPolicy = authorizationPolicy;

        if (Outcome.State == RecipeActivationOutcomeState.Admitted)
        {
            if (admission is null || (admissionReference is not null &&
                    (admissionReference.Position != position || admissionReference.ActivationId != activationId)) ||
                successfulSnapshot is not null ||
                ResultingRecipe is not null)
                throw new ArgumentException("RecipeActivationAdmissionEvidenceInvalid");
            ValidateAdmission(admission);
        }
        else if (admission is not null)
        {
            throw new ArgumentException("RecipeActivationTerminalContainsAdmission");
        }

        if (Outcome.State == RecipeActivationOutcomeState.Succeeded)
        {
            if (SuccessfulSnapshot is null || ResultingRecipe is null || AdmissionReference is null ||
                ResultingRecipe != SuccessfulSnapshot.Recipe ||
                SuccessfulSnapshot.EvidenceKind != EvidenceKind ||
                Checks.Any(value => value.Status == RecipeActivationCheckStatus.Failed ||
                    value.Status == RecipeActivationCheckStatus.NotRun && value.RequiredForActivation) ||
                Enumerable.Range(1, 18).Any(number => !Checks.Any(value =>
                    value.CheckId == "V132.A" + number.ToString("D2", CultureInfo.InvariantCulture) && value.Satisfied)))
                throw new ArgumentException("RecipeActivationSuccessEvidenceInvalid");
        }
        else if (SuccessfulSnapshot is not null || ResultingRecipe is not null)
        {
            throw new ArgumentException("RecipeActivationNonSuccessContainsSnapshot");
        }

        if (AdmissionReference is not null)
            AdmissionReference = new RecipeActivationReference(AdmissionReference.Position,
                AdmissionReference.ActivationId, AdmissionReference.ContentHash);

        var hashParts = new List<string?>
        {
            HistoricalSelection is null ? "sharpinspect-recipe-activation-record-v1" :
                "sharpinspect-recipe-activation-record-v2", Position.ToString(CultureInfo.InvariantCulture),
            ActivationId.ToString("D"), AttemptId.ToString("D"), OperationId.ToString("D"),
            Outcome.ContentHash, EvidenceKind.ToString(),
            PreviousActivation?.Position.ToString(CultureInfo.InvariantCulture),
            PreviousActivation?.ActivationId.ToString("D"), PreviousActivation?.ContentHash,
            PreviousRecipe?.Id, PreviousRecipe?.Version, PreviousRecipe?.ContentHash,
            PreviousSnapshotContentHash, Candidate.Id, Candidate.Version, Candidate.ContentHash,
            ReleaseId.ToString("D"), ReleaseRecordContentHash,
            ResultingRecipe?.Id, ResultingRecipe?.Version, ResultingRecipe?.ContentHash,
            ChangeReason
        };
        if (HistoricalSelection is not null)
        {
            hashParts.Add(HistoricalSelection.Source);
            hashParts.Add(HistoricalSelection.PreviousExactProfile?.ProfileId.ToString("D"));
            hashParts.Add(HistoricalSelection.PreviousExactProfile?.Version.ToString(CultureInfo.InvariantCulture));
            hashParts.Add(HistoricalSelection.PreviousExactProfile?.ContentHash);
            hashParts.Add(HistoricalSelection.Reason);
            hashParts.Add(HistoricalSelection.ContentHash);
        }
        hashParts.AddRange(new[]
        {
            actorPrincipalId?.ToString("D"), actorSessionId?.ToString("D"),
            actorAuthorizationRevision?.ToString(CultureInfo.InvariantCulture),
            authorizationPolicy?.Id, authorizationPolicy?.Version, authorizationPolicy?.ContentHash,
            AuthorizationTarget, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Restoration.ContentHash, SuccessfulSnapshot?.ContentHash,
            Checks.Count.ToString(CultureInfo.InvariantCulture)
        });
        if (Outcome.State != RecipeActivationOutcomeState.Admitted)
        {
            hashParts.Insert(7, AdmissionReference?.Position.ToString(CultureInfo.InvariantCulture));
            hashParts.Insert(8, AdmissionReference?.ActivationId.ToString("D"));
            hashParts.Insert(9, AdmissionReference?.ContentHash);
        }
        hashParts.AddRange(Checks.Select(value => value.ContentHash));
        hashParts.Add(Admission?.ContentHash);
        ContentHash = AlgorithmContractValidation.HashParts(hashParts);

        // An admission event points to itself only after its content hash is complete;
        // that derived reference is deliberately excluded from the hash above.
        if (Outcome.State == RecipeActivationOutcomeState.Admitted)
        {
            var derivedReference = new RecipeActivationReference(Position, ActivationId, ContentHash);
            if (admissionReference is not null && AdmissionReference != derivedReference)
                throw new ArgumentException("RecipeActivationAdmissionReferenceMismatch");
            AdmissionReference = derivedReference;
        }
    }

    private void ValidateAdmission(RecipeActivationAdmission admission)
    {
        if (AttemptId != admission.AttemptId || OperationId != admission.OperationId ||
            Candidate != admission.Candidate || ReleaseId != admission.ReleaseId ||
            ReleaseRecordContentHash != admission.ReleaseRecordContentHash ||
            PreviousActivation != admission.PreviousActivation || PreviousRecipe != admission.PreviousRecipe ||
            PreviousSnapshotContentHash != admission.PreviousSnapshotContentHash ||
            ActorPrincipalId != admission.ActorPrincipalId || ActorSessionId != admission.ActorSessionId ||
            ActorAuthorizationRevision != admission.ActorAuthorizationRevision ||
            AuthorizationPolicy != admission.AuthorizationPolicy ||
            AuthorizationTarget != admission.AuthorizationTarget || ChangeReason != admission.ChangeReason ||
            EvidenceKind != admission.EvidenceKind ||
            !Equals(HistoricalSelection, admission.HistoricalSelection))
            throw new ArgumentException("RecipeActivationAdmissionRecordMismatch");
    }

    private static void ValidateActor(Guid? principalId, Guid? sessionId, long? authorizationRevision,
        RecipeContractReference? authorizationPolicy, bool required)
    {
        if (principalId is Guid principal && principal == Guid.Empty)
            throw new ArgumentException("RecipeActivationActorInvalid", nameof(principalId));
        if (sessionId is Guid session && session == Guid.Empty)
            throw new ArgumentException("RecipeActivationSessionInvalid", nameof(sessionId));
        if (authorizationRevision is < 0)
            throw new ArgumentOutOfRangeException(nameof(authorizationRevision));
        if ((principalId.HasValue || sessionId.HasValue || authorizationRevision.HasValue ||
                authorizationPolicy is not null) &&
            (!principalId.HasValue || !sessionId.HasValue || !authorizationRevision.HasValue ||
                authorizationPolicy is null))
            throw new ArgumentException("RecipeActivationActorBindingIncomplete");
        if (required && (!principalId.HasValue || !sessionId.HasValue ||
                !authorizationRevision.HasValue || authorizationPolicy is null))
            throw new ArgumentException("RecipeActivationAuthorizedActorRequired");
    }

    public long Position { get; }
    public Guid ActivationId { get; }
    public Guid AttemptId { get; }
    public Guid OperationId { get; }
    public RecipeActivationReference? AdmissionReference { get; private set; }
    public RecipeActivationReference? PreviousActivation { get; }
    public RecipeReference? PreviousRecipe { get; }
    public string? PreviousSnapshotContentHash { get; }
    public RecipeReference Candidate { get; }
    public Guid ReleaseId { get; }
    public string ReleaseRecordContentHash { get; }
    public RecipeReference? ResultingRecipe { get; }
    public RecipeActivationOutcome Outcome { get; }
    public ReadOnlyCollection<RecipeActivationCheck> Checks { get; }
    public RecipeActivationRestoration Restoration { get; }
    public RecipeActivationSnapshot? SuccessfulSnapshot { get; }
    public RecipeActivationAdmission? Admission { get; }
    public HistoricalCalibrationSelectionIntent? HistoricalSelection { get; }
    public RecipeActivationEvidenceKind EvidenceKind { get; }
    public Guid? ActorPrincipalId { get; }
    public Guid? ActorSessionId { get; }
    public long? ActorAuthorizationRevision { get; }
    public RecipeContractReference? AuthorizationPolicy { get; }
    public string ChangeReason { get; }
    public string AuthorizationTarget { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
    public RecipeActivationReference Reference => new(Position, ActivationId, ContentHash);
    public bool IsTerminal => Outcome.IsTerminal;
    public bool RecoveryRequired => Outcome.State == RecipeActivationOutcomeState.Admitted;
    public bool ProductionAuthority => Outcome.Succeeded &&
        EvidenceKind == RecipeActivationEvidenceKind.LocalAuthority;
    public bool DevelopmentOnly => EvidenceKind == RecipeActivationEvidenceKind.InternalContractFixture;
    /// <summary>Eligible to be selected as the current record; the query selects the latest one.</summary>
    public bool CanBeActive => ProductionAuthority;
}

public sealed record RecipeActivationAccess(bool CanActivate, string ReasonCode,
    RecipeContractReference? AuthorizationPolicy = null, bool RequiresStepUp = false)
{
    public bool ProductionAuthority => false;
}

public sealed record RecipeActivationResult(RuntimeCommandOutcome Outcome,
    RecipeActivationRecord? Record = null);

/// <summary>
/// The current query distinguishes a real active record from an unfinished admission.
/// A pending admission sets RecoveryRequired and must never be reported as Active.
/// </summary>
public sealed record RecipeActivationReadResult(bool Available, string ReasonCode,
    RecipeActivationRecord? Record = null, RecipeActivationRecord? PendingAdmission = null,
    bool RecoveryRequired = false);

public sealed record RecipeActivationFilter(long AfterPosition = 0, long? ThroughPosition = null,
    int PageSize = 20);

public sealed record RecipeActivationPage(bool Available, string ReasonCode,
    IReadOnlyList<RecipeActivationRecord> Records, long ThroughPosition, long? NextAfterPosition,
    IReadOnlyList<RecipeActivationRecord>? PendingAdmissions = null);

/// <summary>Read-only activation history; resolving it never starts a writer or hardware lease.</summary>
public interface IRecipeActivationQuery
{
    ValueTask<RecipeActivationReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default);
    ValueTask<RecipeActivationReadResult> ReadAsync(RecipeActivationReference reference,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeActivationPage> QueryAsync(RecipeActivationFilter filter,
        CancellationToken cancellationToken = default);
}

public interface IRecipeActivationService : IRecipeActivationQuery
{
    ValueTask<RecipeActivationAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeActivationResult> ActivateAsync(ActivateRecipeCommand command,
        CancellationToken cancellationToken = default);
}

internal static class RecipeActivationValidation
{
    internal static string Hash(string value, string parameterName) =>
        AlgorithmConfigurationValidation.Hash(value, parameterName).ToUpperInvariant();

    internal static string? OptionalHash(string? value, string parameterName) => value is null ? null :
        Hash(value, parameterName);

    internal static Guid RequiredGuid(Guid value, string parameterName) => value == Guid.Empty
        ? throw new ArgumentException("RecipeActivationGuidRequired", parameterName) : value;

    internal static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default)
            throw new ArgumentException("RecipeActivationTimestampRequired", parameterName);
        return value.ToUniversalTime();
    }

    internal static string Reason(string value, string parameterName) {
        var result = AlgorithmContractValidation.BoundedText(value, parameterName, 256);
        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException("RecipeActivationReasonRequired", parameterName);
        return result;
    }

    internal static RecipeReference Recipe(RecipeReference value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var reference = new RecipeContractReference(value.Id, value.Version, value.ContentHash);
        return new RecipeReference(reference.Id, reference.Version, reference.ContentHash);
    }

    internal static RecipeActivationReference? Reference(RecipeActivationReference? value) => value is null
        ? null : new RecipeActivationReference(value.Position, value.ActivationId, value.ContentHash);

    internal static ReadOnlyCollection<CalibrationProfileSelection> CopySelections(
        IEnumerable<CalibrationProfileSelection>? values)
    {
        var result = AlgorithmContractValidation.Copy(values, nameof(values), 8);
        if (result.Select(value => value.RequirementContentHash).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new ArgumentException("RecipeActivationCalibrationRequirementDuplicate", nameof(values));
        return result;
    }

    internal static ReadOnlyCollection<CalibrationRunProfileBinding> CopyBindings(
        IEnumerable<CalibrationRunProfileBinding>? values)
    {
        var result = AlgorithmContractValidation.Copy(values, nameof(values), 8);
        if (result.Select(value => value.RequirementContentHash).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new ArgumentException("RecipeActivationCalibrationBindingDuplicate", nameof(values));
        return result;
    }

    internal static ReadOnlyCollection<RecipeActivationCheck> CopyChecks(
        IEnumerable<RecipeActivationCheck>? values)
    {
        var result = AlgorithmContractValidation.Copy(values, nameof(values), 256);
        if (result.Select(value => (value.CheckId, value.Subject)).Distinct().Count() != result.Count)
            throw new ArgumentException("RecipeActivationCheckDuplicate", nameof(values));
        return result;
    }

    internal static string SelectionHash(CalibrationProfileSelection value) =>
        AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-activation-calibration-selection-v1", value.RequirementContentHash,
            value.Profile.ProfileId.ToString("D"), value.Profile.Version.ToString(CultureInfo.InvariantCulture),
            value.Profile.ContentHash
        });

    internal static string CameraHash(CameraSetupSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = new List<string?>
        {
            "sharpinspect-camera-setup-snapshot-v1", value.LogicalRole, value.ReasonCode,
            value.Binding is null ? null : "binding", value.Binding?.Position.ToString(CultureInfo.InvariantCulture),
            value.Binding?.LogicalRole, value.Binding?.Revision.ToString(CultureInfo.InvariantCulture),
            value.Binding?.OperationId.ToString("D"), value.Binding?.PreviousRevisionHash,
            value.Binding?.RevisionHash, value.Binding?.Target.ContentHash,
            value.Binding?.AuthorPrincipalId.ToString("D"), value.Binding?.AuthorSessionId.ToString("D"),
            value.Binding?.AuthorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            value.Binding?.ChangeReason, value.Binding?.RecordedAtUtc.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            value.Health.ProviderAvailability.ToString(), value.Health.Connection.ToString(),
            value.Health.Configuration.ToString(), value.Health.Acquisition.ToString(),
            value.Health.ObservedAt.HostObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            value.Health.ObservedAt.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            value.Health.LastFault?.Classification.ToString(), value.Health.LastFault?.ReasonCode,
            value.Health.LastFault?.DiagnosticCode
        };
        AddRequested(parts, value.Requested);
        AddEffective(parts, value.Effective);
        parts.Add(value.Differences.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var difference in value.Differences.OrderBy(item => item.Setting))
        {
            parts.Add(difference.Setting.ToString());
            parts.Add(Number(difference.Requested));
            parts.Add(Number(difference.Effective));
        }
        parts.Add(value.Extension is null ? null : "extension");
        parts.Add(value.Extension?.Provider.Id);
        parts.Add(value.Extension?.Provider.Version);
        parts.Add(value.Extension?.Provider.AdapterPackageId);
        parts.Add(value.Extension?.Provider.AdapterVersion);
        parts.Add(value.Extension?.ContractId);
        parts.Add(value.Extension?.ContractVersion);
        parts.Add(value.Extension?.ConfigurationContentHash);
        parts.Add(value.Capabilities?.ContentHash);
        return AlgorithmContractValidation.HashParts(parts);
    }

    private static void AddRequested(List<string?> parts, RequestedCameraConfiguration? value)
    {
        parts.Add(value is null ? null : "requested");
        if (value is null) return;
        AddConfiguration(parts, value.ProductionAcquisitionMode, value.ExposureTimeUs, value.GainDb,
            value.RegionOfInterest, value.PixelFormat, value.ValidBits, value.AcquisitionTimeoutMs,
            value.TriggerDelayUs, value.WhiteBalanceRgb);
    }

    private static void AddEffective(List<string?> parts, EffectiveCameraConfiguration? value)
    {
        parts.Add(value is null ? null : "effective");
        if (value is null) return;
        AddConfiguration(parts, value.ProductionAcquisitionMode, value.ExposureTimeUs, value.GainDb,
            value.RegionOfInterest, value.PixelFormat, value.ValidBits, value.AcquisitionTimeoutMs,
            value.TriggerDelayUs, value.WhiteBalanceRgb);
    }

    private static void AddConfiguration(List<string?> parts, ProductionAcquisitionMode mode,
        double exposure, double gain, RegionOfInterest roi, VisionPixelFormat pixelFormat, int? validBits,
        int timeoutMs, double triggerDelay, WhiteBalanceRgb? whiteBalance)
    {
        parts.Add(mode.ToString());
        parts.Add(Number(exposure));
        parts.Add(Number(gain));
        parts.Add(roi.OffsetX.ToString(CultureInfo.InvariantCulture));
        parts.Add(roi.OffsetY.ToString(CultureInfo.InvariantCulture));
        parts.Add(roi.Width.ToString(CultureInfo.InvariantCulture));
        parts.Add(roi.Height.ToString(CultureInfo.InvariantCulture));
        parts.Add(pixelFormat.ToString());
        parts.Add(validBits?.ToString(CultureInfo.InvariantCulture));
        parts.Add(timeoutMs.ToString(CultureInfo.InvariantCulture));
        parts.Add(Number(triggerDelay));
        parts.Add(whiteBalance is null ? null : "white-balance");
        parts.Add(Number(whiteBalance?.Red));
        parts.Add(Number(whiteBalance?.Green));
        parts.Add(Number(whiteBalance?.Blue));
    }

    private static string? Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);
}
