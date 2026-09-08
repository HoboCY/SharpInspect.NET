using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Contract-focused tests for the bounded camera setup payload codec. The cases
/// use only the public camera value constructors and the internal event boundary;
/// no private DTO or production state is inspected.
/// </summary>
public sealed class CameraSetupStorageCodecContractTests
{
    private const string Role = "TopCamera";

    [Fact]
    public void V117_E01_LogicalRoleAcceptsExactly64SafeAsciiCharacters()
    {
        var acceptedRole = new string('A', 64);
        var payload = CameraSetupStorageCodec.Encode(RebindCompleted(acceptedRole));
        var decoded = CameraSetupStorageCodec.Decode(payload, 1);

        Assert.Equal(acceptedRole, decoded.LogicalRole);
        Assert.Equal(payload, CameraSetupStorageCodec.Encode(decoded));
        AssertRejected(RebindCompleted(new string('A', 65)), "CameraSetupLogicalRoleInvalid");
        AssertRejected(RebindCompleted("相机"), "CameraSetupLogicalRoleInvalid");
    }

    [Fact]
    public void V117_E02_ActorAndSessionMustBePresentAndNonEmptyGuids()
    {
        var valid = RebindCompleted();

        AssertRejected(valid with { ActorPrincipalId = null }, "CameraSetupAuthorInvalid");
        AssertRejected(valid with { SessionId = null }, "CameraSetupAuthorInvalid");
        AssertRejected(valid with { ActorPrincipalId = Guid.Empty }, "CameraSetupAuthorInvalid");
        AssertRejected(valid with { SessionId = Guid.Empty }, "CameraSetupAuthorInvalid");
    }

    [Fact]
    public void V117_E03_RebindCannotCarryRequestedEffectiveOrProviderExtensionConfiguration()
    {
        var valid = RebindCompleted();
        var requested = RequestedConfiguration();
        var effective = EffectiveConfiguration();
        var extension = Extension(Target().Provider);

        AssertRejected(valid with { Requested = requested },
            "CameraSetupRebindConfigurationForbidden");
        AssertRejected(valid with { Requested = requested, Effective = effective },
            "CameraSetupRebindConfigurationForbidden");
        AssertRejected(valid with { Extension = extension },
            "CameraSetupRebindConfigurationForbidden");
    }

    [Fact]
    public void V117_E04_ApplyAdmissionRequiresRequestedAndForbidsEffective()
    {
        var valid = ApplyAdmission();

        AssertRejected(valid with { Requested = null }, "CameraSetupAdmissionInvalid");
        AssertRejected(valid with { Effective = EffectiveConfiguration() },
            "CameraSetupAdmissionInvalid");
    }

    [Fact]
    public void V117_E05_ApplyCompletedAndFailedTerminalShapesAreRejected()
    {
        var admission = ApplyAdmission();
        AssertRejected(admission with { Phase = CameraSetupEventPhase.Completed },
            "CameraSetupCompletedInvalid");

        var failedTerminal = ApplyTerminal(succeeded: false) with
        {
            Effective = EffectiveConfiguration(),
            Differences = new[]
            {
                new CameraConfigurationDifference(CameraNumericSetting.ExposureTimeUs, 100, 101)
            }
        };
        AssertRejected(failedTerminal, "CameraSetupTerminalInvalid");
    }

    [Fact]
    public void V117_E06_ExtensionProviderMustMatchAllFourTargetIdentityFieldsAndRoundTrips()
    {
        var target = Target();
        var valid = ApplyTerminal(succeeded: true) with { Extension = Extension(target.Provider) };

        var mismatchedProviders = new[]
        {
            new CameraProviderIdentity("Other.Provider", target.Provider.Version,
                target.Provider.AdapterPackageId, target.Provider.AdapterVersion),
            new CameraProviderIdentity(target.Provider.Id, "2",
                target.Provider.AdapterPackageId, target.Provider.AdapterVersion),
            new CameraProviderIdentity(target.Provider.Id, target.Provider.Version,
                "Other.Adapter", target.Provider.AdapterVersion),
            new CameraProviderIdentity(target.Provider.Id, target.Provider.Version,
                target.Provider.AdapterPackageId, "2")
        };

        foreach (var provider in mismatchedProviders)
            AssertRejected(valid with { Extension = Extension(provider) },
                "CameraSetupExtensionProviderMismatch");

        var payload = CameraSetupStorageCodec.Encode(valid);
        var decoded = CameraSetupStorageCodec.Decode(payload, 1);
        Assert.Equal(valid.Target, decoded.Target);
        Assert.Equal(valid.Extension, decoded.Extension);
        Assert.Equal(valid.Requested, decoded.Requested);
        Assert.Equal(valid.Effective, decoded.Effective);
        Assert.Equal(payload, CameraSetupStorageCodec.Encode(decoded));
    }

    private static void AssertRejected(CameraSetupEvent value, string reason)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CameraSetupStorageCodec.Encode(value));
        Assert.Equal(reason, exception.Message);
    }

    private static CameraSetupEvent RebindCompleted(string logicalRole = Role) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), logicalRole, AuditedCommandKind.RebindCamera,
            CameraSetupEventPhase.Completed, 1, target: Target(), succeeded: true,
            reasonCode: "CameraRebindCompleted", changeReason: "V117CodecContract",
            actorPrincipalId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
            authorAuthorizationRevision: 1, recordedAtUtc: RecordedAt());

    private static CameraSetupEvent ApplyAdmission() =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Role,
            AuditedCommandKind.ApplyCameraDebugConfiguration, CameraSetupEventPhase.Admission, 1,
            target: Target(), requested: RequestedConfiguration(), succeeded: true,
            reasonCode: "CameraDebugConfigurationAdmitted", changeReason: "V117CodecContract",
            actorPrincipalId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
            authorAuthorizationRevision: 1, recordedAtUtc: RecordedAt());

    private static CameraSetupEvent ApplyTerminal(bool succeeded) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Role,
            AuditedCommandKind.ApplyCameraDebugConfiguration, CameraSetupEventPhase.Terminal, 1,
            target: Target(), requested: RequestedConfiguration(),
            effective: succeeded ? EffectiveConfiguration() : null,
            health: succeeded ? null : FailureHealth(), succeeded: succeeded,
            reasonCode: succeeded ? "CameraConfigurationApplied" : "CameraConfigurationApplyFailed",
            changeReason: "V117CodecContract", actorPrincipalId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(), authorAuthorizationRevision: 1,
            recordedAtUtc: RecordedAt());

    private static CameraBindingTarget Target() => new(
        new CameraProviderIdentity("Camera.Provider", "1", "Camera.Adapter", "1"),
        "device-001");

    private static CameraProviderExtensionRequirement Extension(CameraProviderIdentity provider) =>
        new(provider, "Camera.FixedGamma", "1", new string('A', 64));

    private static RequestedCameraConfiguration RequestedConfiguration() =>
        new(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
            new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null,
            500, 0, null);

    private static EffectiveCameraConfiguration EffectiveConfiguration() =>
        new(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
            new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null,
            500, 0, null);

    private static CameraHealthSnapshot FailureHealth() => new(
        CameraProviderAvailability.Faulted, CameraConnectionState.Closed,
        CameraConfigurationState.Unknown, CameraAcquisitionState.Stopped,
        new FrameTimePoint(RecordedAt(), 1),
        new CameraFault(CameraFaultClassification.DeviceFault, "CameraConfigurationApplyFailed"));

    private static DateTimeOffset RecordedAt() =>
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
}
