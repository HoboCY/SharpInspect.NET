using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Frozen evidence-selection bounds. These establish whether computation has enough
/// input; they are not a Calibration Acceptance Policy or publication authority.
/// </summary>
public sealed class CalibrationEvidenceSelectionPolicy
{
    public CalibrationEvidenceSelectionPolicy(string id, string version, int minimumFrames,
        int minimumFeaturesPerFrame, double minimumImageCoverage)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        if (minimumFrames is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(minimumFrames));
        if (minimumFeaturesPerFrame is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(minimumFeaturesPerFrame));
        if (!double.IsFinite(minimumImageCoverage) || minimumImageCoverage is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumImageCoverage));
        MinimumFrames = minimumFrames;
        MinimumFeaturesPerFrame = minimumFeaturesPerFrame;
        MinimumImageCoverage = minimumImageCoverage;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-selection-policy-v1", Id, Version,
            minimumFrames.ToString(CultureInfo.InvariantCulture),
            minimumFeaturesPerFrame.ToString(CultureInfo.InvariantCulture),
            minimumImageCoverage.ToString("R", CultureInfo.InvariantCulture)
        });
    }
    public string Id { get; }
    public string Version { get; }
    public int MinimumFrames { get; }
    public int MinimumFeaturesPerFrame { get; }
    public double MinimumImageCoverage { get; }
    public string ContentHash { get; }
    public bool ProductionAuthority => false;
}

/// <summary>Complete immutable computation intent, fixed before session admission.</summary>
public sealed class CalibrationSessionPlan
{
    public CalibrationSessionPlan(CalibrationRequirement requirement,
        CalibrationProcedureDescriptor procedure, CalibrationProcedureInputPayload input,
        RequestedCameraConfiguration temporaryConfiguration,
        CalibrationEvidenceSelectionPolicy selectionPolicy)
    {
        Requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        Procedure = procedure ?? throw new ArgumentNullException(nameof(procedure));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        TemporaryConfiguration = temporaryConfiguration ?? throw new ArgumentNullException(nameof(temporaryConfiguration));
        SelectionPolicy = selectionPolicy ?? throw new ArgumentNullException(nameof(selectionPolicy));
        if (procedure.CalibrationKind != requirement.Kind)
            throw new ArgumentException("CalibrationProcedureKindMismatch", nameof(procedure));
        if (procedure.InputContract != input.InputContract)
            throw new ArgumentException("CalibrationProcedureInputContractMismatch", nameof(input));
        if (temporaryConfiguration.ProductionAcquisitionMode != ProductionAcquisitionMode.SoftwareTrigger)
            throw new ArgumentException("CalibrationSoftwareTriggerRequired", nameof(temporaryConfiguration));
        TemporaryConfigurationHash = CalibrationSessionContractHash.Configuration(temporaryConfiguration);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-session-plan-v1", requirement.ContentHash,
            procedure.ContentHash, input.ContentHash, TemporaryConfigurationHash, selectionPolicy.ContentHash
        });
    }
    public CalibrationRequirement Requirement { get; }
    public CalibrationProcedureDescriptor Procedure { get; }
    public CalibrationProcedureInputPayload Input { get; }
    public RequestedCameraConfiguration TemporaryConfiguration { get; }
    public CalibrationEvidenceSelectionPolicy SelectionPolicy { get; }
    public string TemporaryConfigurationHash { get; }
    public string ContentHash { get; }
}

/// <summary>Submitting this command requests admission; navigation never starts a session.</summary>
public sealed record StartCalibrationSessionCommand : RuntimeCommand
{
    public StartCalibrationSessionCommand(Guid correlationId, CommandInvocation invocation,
        CalibrationSessionPlan plan, long expectedBindingRevision, string expectedBindingRevisionHash,
        ImagingSetupRevisionReference expectedImagingSetup, string reason)
        : base(correlationId, invocation)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (expectedBindingRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedBindingRevision));
        ExpectedBindingRevision = expectedBindingRevision;
        ExpectedBindingRevisionHash = CameraSetupValidation.Hash(expectedBindingRevisionHash, nameof(expectedBindingRevisionHash));
        ExpectedImagingSetup = expectedImagingSetup ?? throw new ArgumentNullException(nameof(expectedImagingSetup));
        if (expectedImagingSetup.LogicalCameraRole != plan.Requirement.LogicalCameraRole)
            throw new ArgumentException("CalibrationImagingRoleMismatch", nameof(expectedImagingSetup));
        Reason = AlgorithmContractValidation.BoundedText(reason, nameof(reason), 512);
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-start-v1", plan.ContentHash,
            expectedBindingRevision.ToString(CultureInfo.InvariantCulture), ExpectedBindingRevisionHash,
            expectedImagingSetup.RevisionId.ToString("D"),
            expectedImagingSetup.Revision.ToString(CultureInfo.InvariantCulture),
            expectedImagingSetup.RevisionHash, Reason
        });
    }
    public CalibrationSessionPlan Plan { get; }
    public long ExpectedBindingRevision { get; }
    public string ExpectedBindingRevisionHash { get; }
    public ImagingSetupRevisionReference ExpectedImagingSetup { get; }
    public string Reason { get; }
    public string AuthorizationTarget { get; }
}

public abstract record CalibrationSessionCommand(Guid CorrelationId, CommandInvocation Invocation,
    Guid CalibrationSessionId) : RuntimeCommand(CorrelationId, Invocation)
{
    public string AuthorizationTarget => AlgorithmContractValidation.HashParts(new[]
    {
        "sharpinspect-calibration-action-v1", GetType().Name, CalibrationSessionId.ToString("D"),
        this is ExcludeCalibrationFrameCommand exclude ? exclude.FrameId.ToString("D") : null,
        this switch { ExcludeCalibrationFrameCommand excluded => excluded.Reason,
            ExitCalibrationSessionCommand exit => exit.Reason, _ => null },
        this is ExitCalibrationSessionCommand ending ? ending.Cancel.ToString() : null
    });
}
public sealed record CaptureCalibrationFrameCommand(Guid CorrelationId, CommandInvocation Invocation,
    Guid CalibrationSessionId) : CalibrationSessionCommand(CorrelationId, Invocation, CalibrationSessionId);
public sealed record ExcludeCalibrationFrameCommand(Guid CorrelationId, CommandInvocation Invocation,
    Guid CalibrationSessionId, Guid FrameId, string Reason)
    : CalibrationSessionCommand(CorrelationId, Invocation, CalibrationSessionId);
public sealed record ComputeCalibrationCandidateCommand(Guid CorrelationId, CommandInvocation Invocation,
    Guid CalibrationSessionId) : CalibrationSessionCommand(CorrelationId, Invocation, CalibrationSessionId);
public sealed record ExitCalibrationSessionCommand(Guid CorrelationId, CommandInvocation Invocation,
    Guid CalibrationSessionId, string Reason, bool Cancel = false)
    : CalibrationSessionCommand(CorrelationId, Invocation, CalibrationSessionId);

public enum CalibrationSessionPhase
{
    Admitted, Configuring, Collecting, Capturing, Computing, CandidateRetained,
    Restoring, Restored, RecoveryBlocked
}
public enum CalibrationSessionOutcome { Pending, Completed, Cancelled, Failed, RestartAborted }

/// <summary>Live display projection; detailed immutable evidence is queried separately.</summary>
public sealed record CalibrationSessionState(Guid SessionId, CalibrationSessionPhase Phase,
    CalibrationSessionOutcome Outcome, int FrameCount, int ObservationCount, int ExcludedFrameCount,
    Guid? CandidateId, string ReasonCode, bool RestorationVerified, bool OperationInProgress = false)
{
    public bool DevelopmentOnly => true;
    public bool ProductionAuthority => false;
}

internal static class CalibrationSessionContractHash
{
    internal static string Provenance(FrameProvenance value) => AlgorithmContractValidation.HashParts(new[]
    {
        "sharpinspect-calibration-frame-provenance-v1", value.Correlation.Kind.ToString(), value.Correlation.Value.ToString("D"),
        value.ProviderId, value.ProviderVersion, value.AdapterId, value.AdapterVersion, value.SdkId, value.SdkVersion,
        value.NativeRuntimeVersion, value.StableDeviceIdentity, value.ReportedModel, value.FirmwareVersion,
        value.NativePixelFormatDescription, value.NormalizationDetails, value.NormalizationAllocated.ToString(),
        value.NormalizationTransformed.ToString(), value.DeviceTimestamp?.Value.ToString(CultureInfo.InvariantCulture),
        value.DeviceTimestamp?.TickFrequency?.ToString(CultureInfo.InvariantCulture), value.DeviceTimestamp?.Unit,
        value.DeviceTimestamp?.ClockDomain, value.DeviceTimestamp?.CounterRollover?.ToString(CultureInfo.InvariantCulture),
        value.DeviceTimestamp?.Synchronization.ToString(), value.FrameCounter?.ToString(CultureInfo.InvariantCulture),
        value.Milestones.MonotonicFrequency.ToString(CultureInfo.InvariantCulture),
        Time(value.Milestones.TriggerAccepted), Time(value.Milestones.AcquisitionStarted),
        Time(value.Milestones.NativeFrameReceived), Time(value.Milestones.NormalizedFrameReady),
        value.PoolCopyEvidence?.SourceStrideBytes.ToString(CultureInfo.InvariantCulture),
        value.PoolCopyEvidence?.DestinationStrideBytes.ToString(CultureInfo.InvariantCulture),
        value.PoolCopyEvidence?.InputNormalizationTransformed.ToString()
    });

    private static string? Time(FrameTimePoint? point) => point is null ? null :
        point.HostObservedAtUtc.ToUniversalTime().ToString("O") + ":" + point.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture);

    internal static string Configuration(EffectiveCameraConfiguration value) => Configuration(
        new RequestedCameraConfiguration(value.ProductionAcquisitionMode, value.ExposureTimeUs,
            value.GainDb, value.RegionOfInterest, value.PixelFormat, value.ValidBits,
            value.AcquisitionTimeoutMs, value.TriggerDelayUs, value.WhiteBalanceRgb));

    internal static string Configuration(RequestedCameraConfiguration value) =>
        AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-camera-configuration-v1", value.ProductionAcquisitionMode.ToString(),
            value.ExposureTimeUs.ToString("R", CultureInfo.InvariantCulture),
            value.GainDb.ToString("R", CultureInfo.InvariantCulture),
            value.RegionOfInterest.OffsetX.ToString(CultureInfo.InvariantCulture),
            value.RegionOfInterest.OffsetY.ToString(CultureInfo.InvariantCulture),
            value.RegionOfInterest.Width.ToString(CultureInfo.InvariantCulture),
            value.RegionOfInterest.Height.ToString(CultureInfo.InvariantCulture), value.PixelFormat.ToString(),
            value.ValidBits?.ToString(CultureInfo.InvariantCulture),
            value.AcquisitionTimeoutMs.ToString(CultureInfo.InvariantCulture),
            value.TriggerDelayUs.ToString("R", CultureInfo.InvariantCulture),
            value.WhiteBalanceRgb?.Red.ToString("R", CultureInfo.InvariantCulture),
            value.WhiteBalanceRgb?.Green.ToString("R", CultureInfo.InvariantCulture),
            value.WhiteBalanceRgb?.Blue.ToString("R", CultureInfo.InvariantCulture)
        });
}
