using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// The durable state of one accepted production inspection.  Admission is a
/// separate fact from CoreCommitted; a frame or a result is never implied by
/// an admitted trigger.
/// </summary>
public enum ProductionInspectionState : byte
{
    Admitted = 1,
    CoreCommitted = 2,
    FaultTerminated = 3,
    RecoveryBlocked = 4
}

/// <summary>Which production image-evidence contract the cycle requested.</summary>
public enum ProductionEvidenceRequirement : byte
{
    None = 1,
    Optional = 2,
    Required = 3
}

/// <summary>Append-only lifecycle facts for one production inspection.</summary>
public enum ProductionInspectionEventKind : byte
{
    Admitted = 1,
    CoreCommitted = 2,
    FaultTerminated = 3,
    ResultValidRaised = 4,
    ResultAcknowledged = 5,
    AcknowledgementReset = 6,
    RecoveryRequired = 7,
    RecoveryCompleted = 8,
    PublicationPrepared = 9,
    ResultValidCleared = 10
}

/// <summary>
/// The immutable admission identity captured before any camera I/O.  The
/// Runtime is the only assembly allowed to construct this value.
/// </summary>
public sealed class ProductionInspectionAdmission
{
    internal ProductionInspectionAdmission(Guid inspectionId, Guid correlationId,
        Guid runtimeEpoch, string stationId, long admissionGeneration,
        PlcControllerCycle controllerCycle, ProductionEvidenceRequirement evidenceRequirement,
        RecipeActivationReference activationReference,
        RecipeActivationSnapshot activationSnapshot, string endpointBindingHash,
        string plcProfileHash, string plcPolicyHash, long connectionGeneration,
        int connectionAttempt, DateTimeOffset acceptedAtUtc,
        long acceptedMonotonicTimestamp, TraceStoragePolicySnapshot tracePolicySnapshot,
        IEnumerable<TraceRetentionObligation>? retentionObligations = null)
        : this(inspectionId, correlationId, runtimeEpoch, stationId, admissionGeneration,
            controllerCycle, evidenceRequirement, activationReference, activationSnapshot,
            endpointBindingHash, plcProfileHash, plcPolicyHash, connectionGeneration,
            connectionAttempt, acceptedAtUtc, acceptedMonotonicTimestamp, tracePolicySnapshot,
            retentionObligations, partIdentityEvidence: null)
    {
    }

    internal ProductionInspectionAdmission(Guid inspectionId, Guid correlationId,
        Guid runtimeEpoch, string stationId, long admissionGeneration,
        PlcControllerCycle controllerCycle, ProductionEvidenceRequirement evidenceRequirement,
        RecipeActivationReference activationReference,
        RecipeActivationSnapshot activationSnapshot, string endpointBindingHash,
        string plcProfileHash, string plcPolicyHash, long connectionGeneration,
        int connectionAttempt, DateTimeOffset acceptedAtUtc,
        long acceptedMonotonicTimestamp, TraceStoragePolicySnapshot tracePolicySnapshot,
        IEnumerable<TraceRetentionObligation>? retentionObligations,
        PartIdentityEvidence? partIdentityEvidence)
        : this(null, inspectionId, correlationId, runtimeEpoch, stationId, admissionGeneration,
            controllerCycle, evidenceRequirement, activationReference, activationSnapshot,
            endpointBindingHash, plcProfileHash, plcPolicyHash, connectionGeneration,
            connectionAttempt, acceptedAtUtc, acceptedMonotonicTimestamp, tracePolicySnapshot,
            retentionObligations, partIdentityEvidence) { }

    internal ProductionInspectionAdmission(EvidenceCapturePolicySnapshot? evidenceCapturePolicy,
        Guid inspectionId, Guid correlationId, Guid runtimeEpoch, string stationId, long admissionGeneration,
        PlcControllerCycle controllerCycle, ProductionEvidenceRequirement evidenceRequirement,
        RecipeActivationReference activationReference, RecipeActivationSnapshot activationSnapshot,
        string endpointBindingHash, string plcProfileHash, string plcPolicyHash, long connectionGeneration,
        int connectionAttempt, DateTimeOffset acceptedAtUtc, long acceptedMonotonicTimestamp,
        TraceStoragePolicySnapshot tracePolicySnapshot, IEnumerable<TraceRetentionObligation>? retentionObligations,
        PartIdentityEvidence? partIdentityEvidence)
    {
        if (inspectionId == Guid.Empty || correlationId == Guid.Empty || runtimeEpoch == Guid.Empty)
            throw new ArgumentException("ProductionInspectionAdmissionIdentityInvalid");
        if (activationReference is null || activationSnapshot is null || controllerCycle is null)
            throw new ArgumentNullException(nameof(activationReference));
        if (!activationSnapshot.ProductionAuthority)
            throw new ArgumentException("ProductionInspectionActivationAuthorityRequired",
                nameof(activationSnapshot));
        if (admissionGeneration < 0 || connectionGeneration < 0 || connectionAttempt < 0)
            throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        StationId = AlgorithmConfigurationValidation.Identifier(stationId, nameof(stationId));
        if (!Enum.IsDefined(typeof(ProductionEvidenceRequirement), evidenceRequirement) ||
            evidenceRequirement != ProductionEvidenceRequirement.None)
            throw new InvalidOperationException("ProductionInspectionEvidenceRequirementUnavailable");
        if (acceptedAtUtc == default || acceptedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ProductionInspectionTimestampInvalid", nameof(acceptedAtUtc));
        if (acceptedMonotonicTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(acceptedMonotonicTimestamp));

        InspectionId = inspectionId;
        CorrelationId = correlationId;
        RuntimeEpoch = runtimeEpoch;
        AdmissionGeneration = admissionGeneration;
        EvidenceRequirement = evidenceRequirement;
        ControllerCycle = controllerCycle;
        ActivationReference = activationReference;
        ActivationSnapshot = activationSnapshot;
        EndpointBindingHash = Hash(endpointBindingHash, nameof(endpointBindingHash));
        PlcProfileHash = Hash(plcProfileHash, nameof(plcProfileHash));
        PlcPolicyHash = Hash(plcPolicyHash, nameof(plcPolicyHash));
        ConnectionGeneration = connectionGeneration;
        ConnectionAttempt = connectionAttempt;
        AcceptedAtUtc = acceptedAtUtc;
        AcceptedMonotonicTimestamp = acceptedMonotonicTimestamp;
        TracePolicySnapshot = tracePolicySnapshot ??
            throw new ArgumentNullException(nameof(tracePolicySnapshot));
        var declaredCapture = activationSnapshot.Release.Source.Content.PolicyRequirements
            .SingleOrDefault(value => value.Kind == RecipePolicyKind.EvidenceCapture)?.Contract;
        if (declaredCapture != evidenceCapturePolicy?.Reference)
            throw new InvalidOperationException("ProductionInspectionEvidencePolicyBindingMismatch");
        EvidenceCapturePolicy = evidenceCapturePolicy;
        var declaredPartIdentity = activationSnapshot.Release.Source.Content.PartIdentityRequirement;
        if (declaredPartIdentity is null || declaredPartIdentity.Mode == PartIdentityRequirementMode.None)
        {
            if (partIdentityEvidence is not null)
                throw new InvalidOperationException("ProductionInspectionPartIdentityEvidenceUnexpected");
        }
        else
        {
            if (partIdentityEvidence is null)
                throw new InvalidOperationException("ProductionInspectionPartIdentityEvidenceMissing");
            if (partIdentityEvidence.LogicalRole != declaredPartIdentity!.LogicalRole ||
                partIdentityEvidence.FormatHash != declaredPartIdentity.Format!.ContentHash)
                throw new InvalidOperationException("ProductionInspectionPartIdentityBindingMismatch");
            if (partIdentityEvidence.Cycle.RuntimeEpoch != runtimeEpoch ||
                partIdentityEvidence.Cycle.EndpointBindingHash != EndpointBindingHash ||
                partIdentityEvidence.Cycle.ConnectionGeneration != connectionGeneration ||
                partIdentityEvidence.Cycle.ControllerEpoch != controllerCycle.ControllerEpoch ||
                partIdentityEvidence.Cycle.CycleSequence != controllerCycle.CycleSequence)
                throw new InvalidOperationException("ProductionInspectionPartIdentityCycleMismatch");
            if (partIdentityEvidence.Observation.ContentHash != partIdentityEvidence.ObservationHash ||
                partIdentityEvidence.Observation.Binding.ContentHash != partIdentityEvidence.BindingHash ||
                partIdentityEvidence.Observation.Cycle.ContentHash != partIdentityEvidence.Cycle.ContentHash ||
                partIdentityEvidence.Observation.SourceEpoch != partIdentityEvidence.SourceEpoch ||
                partIdentityEvidence.Observation.SourceGeneration != partIdentityEvidence.SourceGeneration)
                throw new InvalidOperationException("ProductionInspectionPartIdentityProvenanceMismatch");
            if (partIdentityEvidence.State == PartIdentityEvidenceState.NotProvided &&
                declaredPartIdentity.Mode != PartIdentityRequirementMode.Optional)
                throw new InvalidOperationException("ProductionInspectionRequiredPartIdentityMissing");
            if (partIdentityEvidence.SourceKind == PartIdentityProviderSourceKind.StablePlc)
            {
                if (partIdentityEvidence.StablePlcSnapshot is null ||
                    partIdentityEvidence.StageToken is not null ||
                    partIdentityEvidence.StablePlcSnapshot.SourceContractHash !=
                    partIdentityEvidence.SourceContractHash ||
                    !partIdentityEvidence.StablePlcSnapshot.Cycle.Matches(partIdentityEvidence.Cycle))
                    throw new InvalidOperationException("ProductionInspectionPartIdentityPlcProofMismatch");
            }
            else if (partIdentityEvidence.SourceKind == PartIdentityProviderSourceKind.Staged)
            {
                if (partIdentityEvidence.StablePlcSnapshot is not null ||
                    (partIdentityEvidence.State == PartIdentityEvidenceState.Provided &&
                     partIdentityEvidence.StageToken is null))
                    throw new InvalidOperationException("ProductionInspectionPartIdentityStageProofMismatch");
            }
            else
                throw new InvalidOperationException("ProductionInspectionPartIdentitySourceInvalid");
        }
        RetentionObligations = CopyObligations(retentionObligations,
            TracePolicySnapshot, nameof(retentionObligations));
        var hashParts = new List<string?>
        {
            evidenceCapturePolicy is not null ? "sharpinspect-production-inspection-admission-v3" :
            partIdentityEvidence is null ? "sharpinspect-production-inspection-admission-v1" :
                "sharpinspect-production-inspection-admission-v2", InspectionId.ToString("D"),
            CorrelationId.ToString("D"), RuntimeEpoch.ToString("D"), StationId,
            AdmissionGeneration.ToString(CultureInfo.InvariantCulture), EvidenceRequirement.ToString(),
            ControllerCycle.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            ControllerCycle.CycleSequence.ToString(CultureInfo.InvariantCulture),
            ActivationReference.Position.ToString(CultureInfo.InvariantCulture),
            ActivationReference.ActivationId.ToString("D"), ActivationReference.ContentHash,
            ActivationSnapshot.ContentHash, EndpointBindingHash, PlcProfileHash, PlcPolicyHash,
            ConnectionGeneration.ToString(CultureInfo.InvariantCulture),
            ConnectionAttempt.ToString(CultureInfo.InvariantCulture),
            AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            AcceptedMonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            TracePolicySnapshot.ContentHash,
            RetentionObligations.Count.ToString(CultureInfo.InvariantCulture)
        };
        hashParts.AddRange(RetentionObligations.Select(value => value.ContentHash));
        if (partIdentityEvidence is not null)
            hashParts.Add(partIdentityEvidence.ContentHash);
        if (evidenceCapturePolicy is not null)
            hashParts.AddRange(new[] { "evidence-capture-policy-v1", evidenceCapturePolicy.ContentHash,
                partIdentityEvidence?.ContentHash });
        PartIdentityEvidence = partIdentityEvidence;
        ContentHash = AlgorithmContractValidation.HashParts(hashParts);
    }

    public Guid InspectionId { get; }
    public Guid CorrelationId { get; }
    public Guid RuntimeEpoch { get; }
    public string StationId { get; }
    public long AdmissionGeneration { get; }
    public ProductionEvidenceRequirement EvidenceRequirement { get; }
    public PlcControllerCycle ControllerCycle { get; }
    public RecipeActivationReference ActivationReference { get; }
    public RecipeActivationSnapshot ActivationSnapshot { get; }
    public string EndpointBindingHash { get; }
    public string PlcProfileHash { get; }
    public string PlcPolicyHash { get; }
    public long ConnectionGeneration { get; }
    public int ConnectionAttempt { get; }
    public DateTimeOffset AcceptedAtUtc { get; }
    public long AcceptedMonotonicTimestamp { get; }
    public TraceStoragePolicySnapshot TracePolicySnapshot { get; }
    public ReadOnlyCollection<TraceRetentionObligation> RetentionObligations { get; }
    public PartIdentityEvidence? PartIdentityEvidence { get; }
    /// <summary>Null is a historical undeclared policy, never an implicit current policy.</summary>
    public EvidenceCapturePolicySnapshot? EvidenceCapturePolicy { get; }
    public string ContentHash { get; }

    private static string Hash(string value, string parameterName) =>
        RecipeActivationValidation.Hash(value, parameterName);

    private static ReadOnlyCollection<TraceRetentionObligation> CopyObligations(
        IEnumerable<TraceRetentionObligation>? values, TraceStoragePolicySnapshot snapshot,
        string parameterName)
    {
        var copied = AlgorithmContractValidation.Copy(values, parameterName, 64);
        foreach (var value in copied)
        {
            if (!string.Equals(value.Snapshot.ContentHash, snapshot.ContentHash,
                    StringComparison.Ordinal))
                throw new ArgumentException("ProductionInspectionRetentionSnapshotMismatch",
                    parameterName);
        }

        return new ReadOnlyCollection<TraceRetentionObligation>(
            copied.OrderBy(value => value.ContentHash, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>
/// The single immutable Core commit.  It may contain a typed acquisition
/// failure, but a missing frame without that failure is never represented as a
/// normal Unknown result.
/// </summary>
public sealed class ProductionInspectionCore
{
    internal ProductionInspectionCore(ProductionInspectionAdmission admission,
        ProductionInspectionState state, ExecutionStatus executionStatus,
        InspectionDecision decision, string reasonCode,
        CameraAcquisitionFailureKind? acquisitionFailureKind,
        string? acquisitionFailureReasonCode, FrameMetadata? frameMetadata,
        FrameProvenance? frameProvenance, Guid preparedAlgorithmInstanceId,
        AlgorithmIdentity? algorithm, AlgorithmConfigurationSnapshot? configuration,
        AlgorithmResultSchema? resultSchema, AlgorithmResult? result,
        FrameOverlaySnapshot? overlay, AlgorithmExecutionTimingSnapshot? timing,
        PlcResultPayloadSnapshot? plcPayload, string? structuredResultJson,
        string? structuredResultHash, string? partIdentity,
        DateTimeOffset committedAtUtc, long committedMonotonicTimestamp,
        IEnumerable<TraceRetentionObligation>? retentionObligations = null,
        FrameAcquisitionStart? acquisitionStart = null,
        long? executionAdmittedMonotonicTimestamp = null,
        long? executionMonotonicFrequency = null)
        : this(null, admission, state, executionStatus, decision, reasonCode, acquisitionFailureKind,
            acquisitionFailureReasonCode, frameMetadata, frameProvenance, preparedAlgorithmInstanceId,
            algorithm, configuration, resultSchema, result, overlay, timing, plcPayload, structuredResultJson,
            structuredResultHash, partIdentity, committedAtUtc, committedMonotonicTimestamp, retentionObligations,
            acquisitionStart, executionAdmittedMonotonicTimestamp, executionMonotonicFrequency) { }

    internal ProductionInspectionCore(ProductionImageEvidenceSnapshot? imageEvidence,
        ProductionInspectionAdmission admission, ProductionInspectionState state, ExecutionStatus executionStatus,
        InspectionDecision decision, string reasonCode, CameraAcquisitionFailureKind? acquisitionFailureKind,
        string? acquisitionFailureReasonCode, FrameMetadata? frameMetadata, FrameProvenance? frameProvenance,
        Guid preparedAlgorithmInstanceId, AlgorithmIdentity? algorithm, AlgorithmConfigurationSnapshot? configuration,
        AlgorithmResultSchema? resultSchema, AlgorithmResult? result, FrameOverlaySnapshot? overlay,
        AlgorithmExecutionTimingSnapshot? timing, PlcResultPayloadSnapshot? plcPayload, string? structuredResultJson,
        string? structuredResultHash, string? partIdentity, DateTimeOffset committedAtUtc,
        long committedMonotonicTimestamp, IEnumerable<TraceRetentionObligation>? retentionObligations,
        FrameAcquisitionStart? acquisitionStart, long? executionAdmittedMonotonicTimestamp,
        long? executionMonotonicFrequency)
    {
        Admission = admission ?? throw new ArgumentNullException(nameof(admission));
        if (Admission.EvidenceRequirement != ProductionEvidenceRequirement.None)
            throw new InvalidOperationException("ProductionInspectionEvidenceRequirementUnavailable");
        var declaredPartIdentity = Admission.ActivationSnapshot.Release.Source.Content.PartIdentityRequirement;
        if (declaredPartIdentity is null || declaredPartIdentity.Mode == PartIdentityRequirementMode.None)
        {
            if (Admission.PartIdentityEvidence is not null || partIdentity is not null)
                throw new InvalidOperationException("ProductionInspectionPartIdentityEvidenceUnexpected");
        }
        else
        {
            if (Admission.PartIdentityEvidence is null)
                throw new InvalidOperationException("ProductionInspectionPartIdentityEvidenceMissing");
            if (Admission.PartIdentityEvidence.State == PartIdentityEvidenceState.NotProvided &&
                declaredPartIdentity.Mode != PartIdentityRequirementMode.Optional)
                throw new InvalidOperationException("ProductionInspectionRequiredPartIdentityMissing");
            if (!string.Equals(partIdentity, Admission.PartIdentityEvidence.Value,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("ProductionInspectionPartIdentityValueMismatch");
        }
        if (!Enum.IsDefined(state) || state is ProductionInspectionState.Admitted)
            throw new ArgumentOutOfRangeException(nameof(state));
        if (!Enum.IsDefined(executionStatus) || !Enum.IsDefined(decision))
            throw new ArgumentOutOfRangeException(nameof(executionStatus));
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        if (committedAtUtc == default || committedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ProductionInspectionTimestampInvalid", nameof(committedAtUtc));
        if (committedMonotonicTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(committedMonotonicTimestamp));
        if (executionAdmittedMonotonicTimestamp.HasValue != executionMonotonicFrequency.HasValue ||
            executionAdmittedMonotonicTimestamp is <= 0 || executionMonotonicFrequency is <= 0)
            throw new ArgumentException("ProductionInspectionExecutionTimingReferenceInvalid");
        if (preparedAlgorithmInstanceId == Guid.Empty)
            throw new ArgumentException("ProductionInspectionPreparedAlgorithmRequired",
                nameof(preparedAlgorithmInstanceId));
        var frozenContent = Admission.ActivationSnapshot.Release.Source.Content;
        if (preparedAlgorithmInstanceId != Admission.ActivationSnapshot.PreparedAlgorithmInstanceId)
            throw new ArgumentException("ProductionInspectionPreparedAlgorithmActivationMismatch",
                nameof(preparedAlgorithmInstanceId));
        if (algorithm is not null && (!SameAlgorithm(algorithm, frozenContent.Algorithm.Algorithm) ||
            !SameAlgorithm(algorithm, Admission.ActivationSnapshot.PlcResultContract.Algorithm)))
            throw new ArgumentException("ProductionInspectionAlgorithmActivationMismatch", nameof(algorithm));
        if (configuration is not null && !SameConfiguration(configuration,
                frozenContent.Configuration, frozenContent.Algorithm.ConfigurationSchema))
            throw new ArgumentException("ProductionInspectionConfigurationActivationMismatch",
                nameof(configuration));
        if (resultSchema is not null && (!SameResultSchema(resultSchema, frozenContent.Algorithm.ResultSchema) ||
            !SameResultSchema(resultSchema, Admission.ActivationSnapshot.PlcResultContract.ResultSchema)))
            throw new ArgumentException("ProductionInspectionResultSchemaActivationMismatch",
                nameof(resultSchema));
        if (acquisitionFailureKind.HasValue != (acquisitionFailureReasonCode is not null))
            throw new ArgumentException("ProductionInspectionAcquisitionFailureMismatch");
        if (acquisitionFailureReasonCode is not null)
            acquisitionFailureReasonCode = AlgorithmConfigurationValidation.Identifier(
                acquisitionFailureReasonCode, nameof(acquisitionFailureReasonCode));
        if (frameMetadata is not null && !IsProductionCorrelation(frameMetadata.Correlation,
                admission.CorrelationId))
            throw new ArgumentException("ProductionInspectionFrameCorrelationMismatch",
                nameof(frameMetadata));
        if (frameProvenance is not null && !IsProductionCorrelation(frameProvenance.Correlation,
                admission.CorrelationId))
            throw new ArgumentException("ProductionInspectionProvenanceCorrelationMismatch",
                nameof(frameProvenance));
        if (result is not null && resultSchema is null)
            throw new ArgumentException("ProductionInspectionResultSchemaRequired", nameof(resultSchema));
        if (overlay is not null && resultSchema is null)
            throw new ArgumentException("ProductionInspectionOverlaySchemaRequired", nameof(resultSchema));
        if (overlay is not null && frameMetadata is null)
            throw new ArgumentException("ProductionInspectionOverlayFrameRequired", nameof(overlay));
        if (configuration is not null && resultSchema is null)
            throw new ArgumentException("ProductionInspectionConfigurationSchemaRequired",
                nameof(resultSchema));
        // Success 只在 CoreCommitted 且帧、算法、结果、PLC 与结构化证据齐全时成立；
        // 缺帧必须带有类型化采集失败。
        if (executionStatus == ExecutionStatus.Success)
        {
            if (state != ProductionInspectionState.CoreCommitted ||
                frameMetadata is null || frameProvenance is null || result is null || overlay is null ||
                resultSchema is null || plcPayload is null || algorithm is null || configuration is null ||
                timing is null || structuredResultJson is null || structuredResultHash is null)
                throw new ArgumentException("ProductionInspectionSuccessEvidenceIncomplete");
            if (acquisitionFailureKind.HasValue)
                throw new ArgumentException("ProductionInspectionSuccessContainsAcquisitionFailure");
        }
        else if (state == ProductionInspectionState.CoreCommitted && frameMetadata is null &&
            !acquisitionFailureKind.HasValue)
            throw new ArgumentException("ProductionInspectionTypedAcquisitionFailureRequired");
        if (plcPayload is not null)
        {
            if (plcPayload.InspectionId != admission.InspectionId)
                throw new ArgumentException("ProductionInspectionPayloadIdentityMismatch", nameof(plcPayload));
            if (plcPayload.Cycle != admission.ControllerCycle)
                throw new ArgumentException("ProductionInspectionPayloadCycleMismatch", nameof(plcPayload));
            if (plcPayload.Binding.ContentHash != admission.ActivationSnapshot.PlcResultContract.ContentHash ||
                !SameContract(plcPayload.Binding.Contract.Reference,
                    admission.ActivationSnapshot.PlcResultContract.Contract.Reference) ||
                !SameAlgorithm(plcPayload.Binding.Algorithm,
                    admission.ActivationSnapshot.PlcResultContract.Algorithm) ||
                !SameResultSchema(plcPayload.Binding.ResultSchema,
                    admission.ActivationSnapshot.PlcResultContract.ResultSchema))
                throw new ArgumentException("ProductionInspectionPayloadBindingMismatch", nameof(plcPayload));
            if (plcPayload.ExecutionStatus != executionStatus || plcPayload.Decision != decision ||
                (plcPayload.ReasonCode is { } payloadReason &&
                    !string.Equals(payloadReason, reasonCode, StringComparison.Ordinal)) ||
                (executionStatus != ExecutionStatus.Success && plcPayload.ReasonCode is null))
                throw new ArgumentException("ProductionInspectionPayloadOutcomeMismatch", nameof(plcPayload));
        }
        if (structuredResultJson is not null)
        {
            structuredResultJson = AlgorithmContractValidation.BoundedText(structuredResultJson,
                nameof(structuredResultJson), 4 * 1024 * 1024);
            var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(structuredResultJson)));
            if (structuredResultHash is null || !string.Equals(
                    RecipeActivationValidation.Hash(structuredResultHash, nameof(structuredResultHash)),
                    expectedHash, StringComparison.Ordinal))
                throw new ArgumentException("ProductionInspectionStructuredResultHashMismatch",
                    nameof(structuredResultHash));
        }
        else if (structuredResultHash is not null)
            throw new ArgumentException("ProductionInspectionStructuredResultHashMismatch",
                nameof(structuredResultHash));

        if (result is not null && (result.Decision != decision ||
            (result.ReasonCode is { } resultReason && !string.Equals(resultReason, reasonCode, StringComparison.Ordinal))))
            throw new ArgumentException("ProductionInspectionResultOutcomeMismatch", nameof(result));
        if (overlay is not null)
        {
            if (overlay.OverlaySetId != admission.InspectionId ||
                !IsProductionCorrelation(overlay.Correlation, admission.CorrelationId) ||
                frameMetadata is null || FrameHash(overlay.FrameMetadata) != FrameHash(frameMetadata) ||
                resultSchema is null || !SameResultSchema(overlay.ResultSchema, resultSchema))
                throw new ArgumentException("ProductionInspectionOverlayBindingMismatch", nameof(overlay));
            if (result is not null && !SameOverlaySet(overlay.OverlaySet, result.OverlaySet))
                throw new ArgumentException("ProductionInspectionOverlayResultMismatch", nameof(overlay));
        }
        if (timing is not null && !SameTiming(timing, Admission.ActivationSnapshot,
                frozenContent.AlgorithmExecutionTimeout))
            throw new ArgumentException("ProductionInspectionTimingBindingMismatch", nameof(timing));
        if (executionStatus == ExecutionStatus.Success &&
            !string.Equals(overlay!.ContentHash, structuredResultHash, StringComparison.Ordinal))
            throw new ArgumentException("ProductionInspectionStructuredOverlayHashMismatch",
                nameof(structuredResultHash));

        State = state;
        ExecutionStatus = executionStatus;
        Decision = decision;
        AcquisitionFailureKind = acquisitionFailureKind;
        AcquisitionFailureReasonCode = acquisitionFailureReasonCode;
        FrameMetadata = frameMetadata;
        FrameProvenance = frameProvenance;
        PreparedAlgorithmInstanceId = preparedAlgorithmInstanceId;
        Algorithm = algorithm;
        Configuration = configuration;
        ResultSchema = resultSchema;
        Result = result;
        Overlay = overlay;
        Timing = timing;
        PlcPayload = plcPayload;
        StructuredResultJson = structuredResultJson;
        StructuredResultHash = structuredResultHash;
        // 持久化 Core 始终从已接受的 Admission 证据派生；调用方传入的值只用于
        // 上面的绑定校验，不能成为第二个事实源。
        PartIdentity = Admission.PartIdentityEvidence?.Value;
        CommittedAtUtc = committedAtUtc;
        CommittedMonotonicTimestamp = committedMonotonicTimestamp;
        AcquisitionStart = acquisitionStart;
        ExecutionAdmittedMonotonicTimestamp = executionAdmittedMonotonicTimestamp;
        ExecutionMonotonicFrequency = executionMonotonicFrequency;
        RetentionObligations = ProductionInspectionCoreValidation.CopyObligations(
            retentionObligations, admission.TracePolicySnapshot);
        ValidateImageEvidence(imageEvidence);
        ImageEvidence = imageEvidence;
        var hashParts = new List<string?>
        {
            ImageEvidence is not null ? "sharpinspect-production-inspection-core-v3" :
            Admission.PartIdentityEvidence is null ? "sharpinspect-production-inspection-core-v1" :
                "sharpinspect-production-inspection-core-v2", Admission.ContentHash, State.ToString(),
            ExecutionStatus.ToString(), Decision.ToString(), ReasonCode,
            AcquisitionFailureKind?.ToString(), AcquisitionFailureReasonCode,
            FrameHash(FrameMetadata), ProvenanceHash(FrameProvenance),
            PreparedAlgorithmInstanceId.ToString("D"), Algorithm?.Id, Algorithm?.Version,
            Configuration?.ContentHash, ResultSchema?.ContentHash, StructuredResultHash,
            Overlay?.ContentHash, TimingHash(Timing), PlcPayload?.ContentHash,
            StructuredResultHash, PartIdentity, CommittedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            CommittedMonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            AcquisitionStartHash(AcquisitionStart),
            ExecutionAdmittedMonotonicTimestamp?.ToString(CultureInfo.InvariantCulture),
            ExecutionMonotonicFrequency?.ToString(CultureInfo.InvariantCulture),
            RetentionObligations.Count.ToString(CultureInfo.InvariantCulture)
        };
        hashParts.AddRange(RetentionObligations.Select(value => value.ContentHash));
        if (ImageEvidence is not null) hashParts.Add(ImageEvidence.ContentHash);
        ContentHash = AlgorithmContractValidation.HashParts(hashParts);
    }

    public ProductionInspectionAdmission Admission { get; }
    public ProductionInspectionState State { get; }
    public ExecutionStatus ExecutionStatus { get; }
    public InspectionDecision Decision { get; }
    public string ReasonCode { get; }
    public CameraAcquisitionFailureKind? AcquisitionFailureKind { get; }
    public string? AcquisitionFailureReasonCode { get; }
    public FrameMetadata? FrameMetadata { get; }
    public FrameProvenance? FrameProvenance { get; }
    public Guid PreparedAlgorithmInstanceId { get; }
    public AlgorithmIdentity? Algorithm { get; }
    public AlgorithmConfigurationSnapshot? Configuration { get; }
    public AlgorithmResultSchema? ResultSchema { get; }
    public AlgorithmResult? Result { get; }
    public FrameOverlaySnapshot? Overlay { get; }
    public AlgorithmExecutionTimingSnapshot? Timing { get; }
    public PlcResultPayloadSnapshot? PlcPayload { get; }
    public string? StructuredResultJson { get; }
    public string? StructuredResultHash { get; }
    public string? PartIdentity { get; }
    public DateTimeOffset CommittedAtUtc { get; }
    public long CommittedMonotonicTimestamp { get; }
    public FrameAcquisitionStart? AcquisitionStart { get; }
    public long? ExecutionAdmittedMonotonicTimestamp { get; }
    public long? ExecutionMonotonicFrequency { get; }
    public ReadOnlyCollection<TraceRetentionObligation> RetentionObligations { get; }
    public ProductionImageEvidenceSnapshot? ImageEvidence { get; }
    public string ContentHash { get; }

    private void ValidateImageEvidence(ProductionImageEvidenceSnapshot? evidence)
    {
        var policy = Admission.EvidenceCapturePolicy;
        if ((policy is null) != (evidence is null))
            throw new ArgumentException("ProductionImageEvidencePolicyMissing");
        if (policy is null) return;
        var required = policy.RequiresImage(FrameMetadata is not null, ExecutionStatus, Decision);
        var expected = FrameMetadata is null ? ProductionImageEvidenceState.NotAvailable :
            required ? ProductionImageEvidenceState.Pending : ProductionImageEvidenceState.NotRequired;
        if (evidence!.State != expected)
            throw new ArgumentException("ProductionImageEvidenceStateMismatch");
        if (FrameMetadata is null && (AcquisitionFailureReasonCode is null ||
            evidence.ReasonCode != AcquisitionFailureReasonCode))
            throw new ArgumentException("ProductionImageEvidenceAcquisitionReasonMismatch");
        if (evidence.Manifest is not { } manifest) return;
        var rule = Admission.TracePolicySnapshot.RetentionRules.Single(value =>
            value.EvidenceClass == TraceRetentionClass.AuthoritativeImage);
        if (manifest.InspectionId != Admission.InspectionId || manifest.AdmissionContentHash != Admission.ContentHash ||
            manifest.EvidencePolicyContentHash != policy.ContentHash || manifest.Width != FrameMetadata!.Width ||
            manifest.Height != FrameMetadata.Height || manifest.PixelFormat != FrameMetadata.PixelFormat ||
            manifest.ValidBits != FrameMetadata.ValidBits || manifest.InputMetadataHash != FrameHash(FrameMetadata) ||
            manifest.InputProvenanceHash != ProvenanceHash(FrameProvenance) ||
            manifest.TracePolicySnapshotHash != Admission.TracePolicySnapshot.ContentHash ||
            manifest.RetentionRuleHash != rule.ContentHash || manifest.CreatedAtUtc < Admission.AcceptedAtUtc ||
            manifest.CreatedAtUtc > CommittedAtUtc)
            throw new ArgumentException("ProductionImageManifestCoreBindingMismatch");
    }

    private static bool IsProductionCorrelation(ExecutionCorrelationId correlation, Guid value) =>
        correlation.Kind == ExecutionKind.Production && correlation.Value == value;

    private static bool SameAlgorithm(AlgorithmIdentity left, AlgorithmIdentity right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal);

    private static bool SameContract(RecipeContractReference left, RecipeContractReference right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool SameResultSchema(AlgorithmResultSchema left, RecipeContractReference right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool SameResultSchema(AlgorithmResultSchema left, AlgorithmResultSchema right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool SameConfiguration(AlgorithmConfigurationSnapshot actual,
        AlgorithmConfigurationSnapshot expected, AlgorithmConfigurationSchema schema) =>
        string.Equals(actual.ContentHash, expected.ContentHash, StringComparison.Ordinal) &&
        string.Equals(actual.SchemaId, expected.SchemaId, StringComparison.Ordinal) &&
        string.Equals(actual.SchemaVersion, expected.SchemaVersion, StringComparison.Ordinal) &&
        string.Equals(actual.SchemaContentHash, expected.SchemaContentHash, StringComparison.Ordinal) &&
        string.Equals(actual.CanonicalizationVersion, expected.CanonicalizationVersion,
            StringComparison.Ordinal) && actual.Validate(schema).Count == 0;

    private static bool SameTiming(AlgorithmExecutionTimingSnapshot actual,
        RecipeActivationSnapshot activation, TimeSpan expectedTimeout) =>
        SameContract(actual.Recipe, activation.Recipe) &&
        string.Equals(actual.PolicyId, activation.AlgorithmExecutionPolicy.Id, StringComparison.Ordinal) &&
        string.Equals(actual.PolicyVersion, activation.AlgorithmExecutionPolicy.Version,
            StringComparison.Ordinal) &&
        string.Equals(actual.PolicyContentHash, activation.AlgorithmExecutionPolicy.ContentHash,
            StringComparison.Ordinal) && actual.AlgorithmExecutionTimeout == expectedTimeout &&
        actual.CancellationGracePeriod == activation.AlgorithmExecutionPolicy.CancellationGracePeriod;

    private static bool SameContract(RecipeReference left, RecipeReference right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool SameOverlaySet(OutputOverlaySet left, OutputOverlaySet right) =>
        string.Equals(left.ContractId, right.ContractId, StringComparison.Ordinal) &&
        string.Equals(left.ContractVersion, right.ContractVersion, StringComparison.Ordinal) &&
        left.Primitives.SequenceEqual(right.Primitives);

    internal static string? FrameHash(FrameMetadata? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "production-frame-metadata-v1", value.Correlation.Value.ToString("D"),
            value.LogicalCameraRole, value.Width.ToString(CultureInfo.InvariantCulture),
            value.Height.ToString(CultureInfo.InvariantCulture), value.StrideBytes.ToString(CultureInfo.InvariantCulture),
            value.PixelFormat.ToString(), value.ValidBits?.ToString(CultureInfo.InvariantCulture),
            value.HostCaptureUtc.ToString("O", CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.ProductionAcquisitionMode.ToString(),
            value.EffectiveCameraConfiguration.ExposureTimeUs.ToString("R", CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.GainDb.ToString("R", CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.RegionOfInterest.OffsetX.ToString(CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.RegionOfInterest.OffsetY.ToString(CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.RegionOfInterest.Width.ToString(CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.RegionOfInterest.Height.ToString(CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.PixelFormat.ToString(),
            value.EffectiveCameraConfiguration.ValidBits?.ToString(CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.AcquisitionTimeoutMs.ToString(CultureInfo.InvariantCulture),
            value.EffectiveCameraConfiguration.TriggerDelayUs.ToString("R", CultureInfo.InvariantCulture)
        });

    internal static string? ProvenanceHash(FrameProvenance? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "production-frame-provenance-v1", value.Correlation.Value.ToString("D"), value.ProviderId,
            value.ProviderVersion, value.AdapterId, value.AdapterVersion, value.SdkId, value.SdkVersion,
            value.NativeRuntimeVersion, value.StableDeviceIdentity, value.ReportedModel,
            value.FirmwareVersion, value.NativePixelFormatDescription, value.NormalizationDetails,
            value.NormalizationAllocated ? "1" : "0", value.NormalizationTransformed ? "1" : "0",
            value.FrameCounter?.ToString(CultureInfo.InvariantCulture),
            DeviceTimestampHash(value.DeviceTimestamp),
            MilestonesHash(value.Milestones), PoolCopyHash(value.PoolCopyEvidence)
        });

    private static string? DeviceTimestampHash(DeviceTimestamp? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "production-device-timestamp-v1", value.Value.ToString(CultureInfo.InvariantCulture),
            value.TickFrequency?.ToString(CultureInfo.InvariantCulture), value.Unit,
            value.ClockDomain, value.CounterRollover?.ToString(CultureInfo.InvariantCulture),
            value.Synchronization.ToString()
        });

    private static string MilestonesHash(FrameAcquisitionMilestones value) =>
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "production-frame-milestones-v1",
            value.MonotonicFrequency.ToString(CultureInfo.InvariantCulture),
            TimePointHash(value.TriggerAccepted), TimePointHash(value.AcquisitionStarted),
            TimePointHash(value.NativeFrameReceived), TimePointHash(value.NormalizedFrameReady)
        });

    private static string? TimePointHash(FrameTimePoint? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            value.HostObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture)
        });

    private static string? PoolCopyHash(PoolCopyEvidence? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "production-pool-copy-v1", value.SourceStrideBytes.ToString(CultureInfo.InvariantCulture),
            value.DestinationStrideBytes.ToString(CultureInfo.InvariantCulture),
            value.InputNormalizationTransformed ? "1" : "0", value.IdentityPixelCopy ? "1" : "0",
            value.PaddingZeroed ? "1" : "0"
        });

    private static string? TimingHash(AlgorithmExecutionTimingSnapshot? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "production-execution-timing-v1", value.PolicyId, value.PolicyVersion,
            value.PolicyContentHash, value.AlgorithmExecutionTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
            value.CancellationGracePeriod.Ticks.ToString(CultureInfo.InvariantCulture)
        });

    private static string? AcquisitionStartHash(FrameAcquisitionStart? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "production-acquisition-start-v1",
            value.BusyAt.HostObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.BusyAt.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            value.DeadlineTimestamp.ToString(CultureInfo.InvariantCulture),
            value.MonotonicFrequency.ToString(CultureInfo.InvariantCulture)
        });
}

internal static class ProductionInspectionCoreValidation
{
    internal static ReadOnlyCollection<TraceRetentionObligation> CopyObligations(
        IEnumerable<TraceRetentionObligation>? values,
        TraceStoragePolicySnapshot snapshot) =>
        Copy(values, snapshot);

    private static ReadOnlyCollection<TraceRetentionObligation> Copy(
        IEnumerable<TraceRetentionObligation>? values, TraceStoragePolicySnapshot snapshot)
    {
        var copied = AlgorithmContractValidation.Copy(values, "retentionObligations", 64);
        foreach (var value in copied)
            if (!string.Equals(value.Snapshot.ContentHash, snapshot.ContentHash, StringComparison.Ordinal))
                throw new ArgumentException("ProductionInspectionRetentionSnapshotMismatch",
                    "retentionObligations");
        return new ReadOnlyCollection<TraceRetentionObligation>(
            copied.OrderBy(value => value.ContentHash, StringComparer.Ordinal).ToArray());
    }
}
