using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Preview;

internal static class PreviewDraftSettings
{
    internal static PreviewCameraProcessSettings FromRequested(RequestedCameraConfiguration value) =>
        new(value.ExposureTimeUs, value.GainDb, value.RegionOfInterest, value.PixelFormat,
            value.ValidBits, value.WhiteBalanceRgb);

    internal static RequestedCameraConfiguration Merge(RequestedCameraConfiguration original,
        PreviewCameraProcessSettings fixedValues) =>
        new(original.ProductionAcquisitionMode, fixedValues.ExposureTimeUs, fixedValues.GainDb,
            fixedValues.RegionOfInterest, fixedValues.PixelFormat, fixedValues.ValidBits,
            original.AcquisitionTimeoutMs, original.TriggerDelayUs, fixedValues.WhiteBalanceRgb);

    internal static RecipeDraftContent Merge(RecipeDraftContent original, PreviewCameraProcessSettings fixedValues) =>
        new(original.MigrationLineage, original.RecipeKey, original.DisplayName, original.Algorithm,
            original.Configuration, original.CameraRole, Merge(original.Camera, fixedValues),
            original.AlgorithmExecutionTimeout, original.AssetRequirements, original.PolicyRequirements,
            original.ValueOrigins, original.CameraProviderExtension, original.CalibrationRequirements,
            original.PartIdentityRequirement);

    internal static string? ValidateReadBack(CameraCapabilities capabilities, RequestedCameraConfiguration original,
        PreviewTuningConfiguration requested, PreviewTuningConfiguration actual, bool requireFixed)
    {
        if (requireFixed && !IsFixed(actual)) return "PreviewAutomaticControlsStillEnabled";
        var expected = capabilities.ValidateConfiguration(Merge(original, requested.ProcessSettings));
        var resolved = capabilities.ValidateConfiguration(Merge(original, actual.ProcessSettings));
        if (!expected.Succeeded || expected.Effective is null || !resolved.Succeeded || resolved.Effective is null)
            return "PreviewEffectiveConfigurationUnsupported";
        var reported = actual.ProcessSettings;
        var representable = resolved.Effective;
        // Reported values must themselves be representable; a second quantization
        // is not proof of the values actually applied by the adapter.
        if (representable.ExposureTimeUs != reported.ExposureTimeUs || representable.GainDb != reported.GainDb ||
            representable.WhiteBalanceRgb != reported.WhiteBalanceRgb ||
            representable.RegionOfInterest != reported.RegionOfInterest ||
            representable.PixelFormat != reported.PixelFormat || representable.ValidBits != reported.ValidBits)
            return "PreviewEffectiveConfigurationNotRepresentable";
        var fixedExpected = expected.Effective;
        if (reported.RegionOfInterest != fixedExpected.RegionOfInterest || reported.PixelFormat != fixedExpected.PixelFormat ||
            reported.ValidBits != fixedExpected.ValidBits ||
            requested.ExposureMode == PreviewAutomaticControlMode.Off &&
                (actual.ExposureMode != PreviewAutomaticControlMode.Off || reported.ExposureTimeUs != fixedExpected.ExposureTimeUs) ||
            requested.GainMode == PreviewAutomaticControlMode.Off &&
                (actual.GainMode != PreviewAutomaticControlMode.Off || reported.GainDb != fixedExpected.GainDb) ||
            requested.WhiteBalanceMode == PreviewAutomaticControlMode.Off &&
                (actual.WhiteBalanceMode != PreviewAutomaticControlMode.Off || reported.WhiteBalanceRgb != fixedExpected.WhiteBalanceRgb))
            return "PreviewConfigurationReadBackMismatch";
        return null;
    }

    internal static bool IsFixed(PreviewTuningConfiguration value) =>
        value.ExposureMode == PreviewAutomaticControlMode.Off && value.GainMode == PreviewAutomaticControlMode.Off &&
        value.WhiteBalanceMode == PreviewAutomaticControlMode.Off;
}
