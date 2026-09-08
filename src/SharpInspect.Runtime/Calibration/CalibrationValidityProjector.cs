using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

internal static class CalibrationValidityProjector
{
    internal static CalibrationProfileValiditySnapshot Project(PublishedCalibrationProfileVersion profile,
        CalibrationAcceptancePolicy policy, IReadOnlyList<PhysicalCalibrationVerificationRecord> verifications,
        CalibrationCompatibilityState compatibility, DateTimeOffset nowUtc)
    {
        if (profile.AcceptancePolicy != policy.Reference || verifications.Any(value => value.Profile != profile.Reference ||
            value.Policy != policy.Reference)) throw new ArgumentException("CalibrationValidityReferenceMismatch");
        var latest = verifications.OrderBy(value => value.Position).LastOrDefault();
        var state = policy.PhysicalVerification.Applicability == CalibrationPolicyApplicability.NotApplicable
            ? CalibrationVerificationState.NotRequired : latest is null ? CalibrationVerificationState.Missing :
            !latest.Passed ? CalibrationVerificationState.Failed : nowUtc < latest.ValidUntilUtc!.Value ?
                CalibrationVerificationState.Current : CalibrationVerificationState.Expired;
        var reasons = new List<string>();
        if (compatibility != CalibrationCompatibilityState.Compatible)
            reasons.Add("Calibration" + compatibility);
        if (state == CalibrationVerificationState.Missing) reasons.Add("CalibrationPhysicalVerificationMissing");
        if (state == CalibrationVerificationState.Failed) reasons.Add("CalibrationPhysicalVerificationFailed");
        if (state == CalibrationVerificationState.Expired) reasons.Add("CalibrationVerificationOverdue");
        reasons.Add("DevelopmentEvidenceNotProductionEligible");
        return new(profile, latest?.Reference, state, compatibility, nowUtc, latest?.ValidUntilUtc, reasons);
    }

    internal static CalibrationCompatibilityState Compatibility(CalibrationProfileContent content,
        CameraSetupSnapshot? camera, ImagingSetupRevision? imaging)
    {
        if (camera?.Binding is null || camera.Requested is null || camera.Effective is null || imaging is null ||
            camera.Health.Connection != CameraConnectionState.Open || camera.Health.Configuration != CameraConfigurationState.Applied)
            return CalibrationCompatibilityState.Unavailable;
        if (camera.Binding.Target != content.Device) return CalibrationCompatibilityState.DeviceMismatch;
        if (ImagingSetupRevisionReference.FromRevision(imaging) != content.ImagingSetup ||
            imaging.Binding != camera.Binding) return CalibrationCompatibilityState.ImagingSetupMismatch;
        if (CalibrationFrameGeometry.FromRequested(camera.Requested) != content.RequestedGeometry)
            return CalibrationCompatibilityState.RequestedGeometryMismatch;
        if (CalibrationFrameGeometry.FromEffective(camera.Effective) != content.EffectiveGeometry)
            return CalibrationCompatibilityState.EffectiveGeometryMismatch;
        return CalibrationCompatibilityState.Compatible;
    }
}
