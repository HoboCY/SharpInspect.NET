using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Converts the runtime-owned run admission and computation outcome into the
/// immutable Manual history projection.  The helper keeps the success payload
/// on the existing canonical algorithm-result codec; failure outcomes retain
/// their execution identity and timing without manufacturing a result.
/// </summary>
internal static class ManualInspectionRunEvidence
{
    internal static ManualInspectionRunRecord CreateTerminal(
        ManualInspectionRunRecord admitted,
        ManualInspectionSessionHeader header,
        AlgorithmExecutionOutcome? outcome,
        ExecutionStatus fallbackStatus,
        string reason,
        FrameMetadata? frame,
        FrameProvenance? provenance,
        Guid? preparedInstanceId,
        RecipeDraftContent source,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        long droppedDiagnosticCount)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(fallbackStatus))
            throw new ArgumentOutOfRangeException(nameof(fallbackStatus));
        if (completedAtUtc == default || completedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ManualInspectionTimestampInvalid", nameof(completedAtUtc));

        var effectiveStatus = outcome?.ExecutionStatus ?? fallbackStatus;
        if (!Enum.IsDefined(effectiveStatus))
            throw new ArgumentOutOfRangeException(nameof(fallbackStatus));
        var effectiveReason = outcome?.ReasonCode ?? reason;
        if (string.IsNullOrWhiteSpace(effectiveReason))
            effectiveReason = effectiveStatus == ExecutionStatus.Success
                ? "ManualInspectionCompleted" : "ManualInspectionExecutionFailed";

        var effectiveFrame = outcome?.FrameMetadata ?? frame;
        var effectivePrepared = outcome?.PreparedInstanceId ?? preparedInstanceId;
        var effectiveAlgorithm = outcome?.Algorithm ?? source.Algorithm.Algorithm;
        var effectiveTiming = outcome?.Timing;
        var effectiveMonotonic = outcome?.AdmittedMonotonicTimestamp;
        var effectiveFrequency = outcome?.MonotonicFrequency;
        var result = outcome?.ValidatedResult;
        var resultSchema = outcome?.ResultSchema;
        string? payload = null;
        string? payloadHash = null;
        FrameOverlaySnapshot? overlay = null;

        if (effectiveStatus == ExecutionStatus.Success)
        {
            if (outcome is null || result is null || resultSchema is null || effectiveFrame is null ||
                effectivePrepared is null || effectiveTiming is null)
                throw new InvalidOperationException("ManualInspectionSuccessfulOutcomeMissing");
            if (!AlgorithmResultStorageCodec.TryEncode(admitted.RunId, outcome,
                    out var document, out var encodeReason) || document is null)
                throw new InvalidOperationException(encodeReason.Length == 0
                    ? "ManualInspectionResultEncodingFailed" : encodeReason);
            payload = document.PayloadJson;
            payloadHash = document.PayloadHash;
            overlay = new FrameOverlaySnapshot(admitted.RunId, effectiveFrame, resultSchema,
                result.OverlaySet, payloadHash);
        }

        var terminalStatus = effectiveStatus switch
        {
            ExecutionStatus.Success => ManualInspectionRunStatus.Completed,
            ExecutionStatus.Timeout => ManualInspectionRunStatus.TimedOut,
            ExecutionStatus.Cancelled => ManualInspectionRunStatus.Cancelled,
            _ => ManualInspectionRunStatus.Failed
        };
        var partSource = admitted.PartIdentity is null
            ? ManualInspectionPartIdentitySource.NotProduction
            : ManualInspectionPartIdentitySource.HumanEntered;
        return new ManualInspectionRunRecord(admitted.Position, admitted.RunId, admitted.SessionId,
            admitted.RuntimeEpoch, admitted.CommandCorrelationId, admitted.AttemptId,
            admitted.PartIdentity, terminalStatus,
            result?.Decision ?? InspectionDecision.Unknown, effectiveStatus, effectiveReason,
            admitted.AdmittedAtUtc, startedAtUtc, completedAtUtc, effectiveFrame, provenance,
            payload, payloadHash, admitted.Evidence, admitted.CommandAuditSequence,
            admitted.CommandAuditHash, admitted.AuditSequence, admitted.AuditHash, partSource,
            admitted.PartIdentity is null ? null : header.ActorPrincipalId,
            admitted.PartIdentity is null ? null : header.ActorSessionId, effectivePrepared, effectiveAlgorithm,
            source.Configuration, outcome?.ConfigurationContentHash ?? source.Configuration.ContentHash,
            outcome?.ConfigurationSchemaId ?? source.Algorithm.ConfigurationSchema.Id,
            outcome?.ConfigurationSchemaVersion ?? source.Algorithm.ConfigurationSchema.Version,
            outcome?.ConfigurationSchemaContentHash ?? source.Algorithm.ConfigurationSchema.ContentHash,
            resultSchema, result, overlay, effectiveTiming, effectiveMonotonic, effectiveFrequency,
            Math.Max(0, droppedDiagnosticCount),
            result is null ? null : payloadHash,
            overlay?.ContentHash);
    }
}
