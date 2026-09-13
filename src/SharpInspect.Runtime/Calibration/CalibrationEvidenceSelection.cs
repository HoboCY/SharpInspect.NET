using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>Framework-owned pre-computation sufficiency; never a calibration acceptance verdict.</summary>
internal static class CalibrationEvidenceSelection
{
    internal static CalibrationSelectionEvaluation Evaluate(CalibrationSessionHeader header,
        IReadOnlyList<CalibrationFrameEvidence> frames, IReadOnlyList<CalibrationObservationEvidence> observations,
        IReadOnlyList<CalibrationEvidenceExclusion> exclusions)
    {
        if (frames.Count > 64 || observations.Count > 64 || exclusions.Count > 64 ||
            frames.Any(frame => frame.SessionId != header.SessionId) ||
            frames.Select(frame => frame.FrameId).Distinct().Count() != frames.Count ||
            observations.Select(observation => observation.Frame.FrameId).Distinct().Count() != observations.Count ||
            exclusions.Select(exclusion => exclusion.FrameId).Distinct().Count() != exclusions.Count)
            throw new InvalidOperationException("CalibrationEvidenceSetInvalid");
        var byId = frames.ToDictionary(frame => frame.FrameId);
        foreach (var observation in observations)
            if (!byId.TryGetValue(observation.Frame.FrameId, out var frame) ||
                frame.SourceHash != observation.Frame.SourceHash ||
                observation.InputHash != header.Command.Plan.Input.ContentHash ||
                observation.Procedure.ContentHash != header.Command.Plan.Procedure.ContentHash)
                throw new InvalidOperationException("CalibrationObservationSourceMismatch");
        foreach (var exclusion in exclusions)
            if (!byId.ContainsKey(exclusion.FrameId) || exclusion.ActorPrincipalId != header.ActorPrincipalId ||
                exclusion.InteractiveSessionId != header.InteractiveSessionId ||
                string.IsNullOrWhiteSpace(exclusion.Reason) || exclusion.Reason.Length > 512)
                throw new InvalidOperationException("CalibrationExclusionInvalid");

        var excluded = exclusions.Select(exclusion => exclusion.FrameId).ToHashSet();
        var included = frames.Where(frame => !excluded.Contains(frame.FrameId)).ToArray();
        var includedObservations = observations.Where(observation => !excluded.Contains(observation.Frame.FrameId)).ToArray();
        var policy = header.Command.Plan.SelectionPolicy;
        var sufficientFeatures = includedObservations.Count(observation =>
            observation.Result.Features.Count >= policy.MinimumFeaturesPerFrame);
        var points = includedObservations.SelectMany(observation => observation.Result.Features.Select(feature =>
            (X: feature.PixelX / observation.Frame.Metadata.Width,
             Y: feature.PixelY / observation.Frame.Metadata.Height))).ToArray();
        // v1 指标是所有纳入的自动特征坐标相对整幅图像的轴对齐覆盖率，不做基于残差的筛选。
        var coverage = points.Length < 2 ? 0 :
            (points.Max(point => point.X) - points.Min(point => point.X)) *
            (points.Max(point => point.Y) - points.Min(point => point.Y));
        var reason = included.Length < policy.MinimumFrames ? "CalibrationInsufficientFrames" :
            includedObservations.Length != included.Length ? "CalibrationObservationMissing" :
            sufficientFeatures != included.Length ? "CalibrationInsufficientFeatures" :
            coverage < policy.MinimumImageCoverage ? "CalibrationInsufficientCoverage" : "CalibrationEvidenceSufficient";
        var fields = new List<string?>
        {
            "sharpinspect-calibration-selection-bounding-box-v1", header.ContentHash, policy.ContentHash,
            frames.Count.ToString(CultureInfo.InvariantCulture), observations.Count.ToString(CultureInfo.InvariantCulture),
            exclusions.Count.ToString(CultureInfo.InvariantCulture)
        };
        fields.AddRange(frames.OrderBy(frame => frame.FrameId).Select(frame => frame.SourceHash));
        fields.AddRange(observations.OrderBy(observation => observation.Frame.FrameId).Select(observation => observation.ContentHash));
        foreach (var exclusion in exclusions.OrderBy(exclusion => exclusion.FrameId))
            fields.AddRange(new[] { exclusion.FrameId.ToString("D"), exclusion.ActorPrincipalId.ToString("D"),
                exclusion.InteractiveSessionId.ToString("D"), exclusion.Reason,
                exclusion.RecordedAtUtc.ToUniversalTime().ToString("O") });
        return new(reason == "CalibrationEvidenceSufficient", reason, included.Length, sufficientFeatures,
            coverage, AlgorithmContractValidation.HashParts(fields));
    }
}
