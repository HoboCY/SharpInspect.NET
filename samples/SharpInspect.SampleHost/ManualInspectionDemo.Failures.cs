using SharpInspect.Abstractions;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

internal static partial class ManualInspectionDemo
{
    private static async Task<ManualFailureEvidence> RunFailureCasesAsync(
        ManualInspectionSessionViewModel viewModel, ManualInspectionAlgorithmFactory factory,
        IStationRuntime runtime, IManualInspectionHistoryQuery history,
        RuntimeCommandOutcome busy, RuntimeCommandOutcome preview, Guid initialSessionId, IEnumerable<Guid> initialRunIds)
    {
        var failures = new List<ManualFailureRunEvidence>();
        var sessions = new HashSet<Guid> { initialSessionId };
        var runIds = new HashSet<Guid>(initialRunIds);
        foreach (var expected in new[] { ExecutionStatus.Timeout, ExecutionStatus.Error })
        {
            if (expected == ExecutionStatus.Timeout) factory.TimeoutNextExecution();
            var start = await viewModel.StartAsync().ConfigureAwait(true);
            RequireAccepted(start, "ManualFailureStartRejected");
            var ready = await WaitForPhaseAsync(viewModel,
                snapshot => snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
                "ManualFailurePreparationUnavailable").ConfigureAwait(true);
            var sessionId = ready.SessionId!.Value;
            Require(sessions.Add(sessionId), "ManualReentryReusedSessionId");
            var run = await viewModel.RunOneAsync().ConfigureAwait(true);
            RequireAccepted(run, "ManualFailureRunNotAdmitted");
            var closed = await WaitForPhaseAsync(viewModel,
                snapshot => snapshot.SessionId == sessionId && snapshot.Phase == ManualInspectionSessionPhase.Closed,
                "ManualFailureSessionDidNotClose").ConfigureAwait(true);
            Require(closed.Restoration == ManualInspectionRestorationState.NoActiveBaselineClosed &&
                !closed.RecoveryRequired, "ManualFailureCameraRestorationUnavailable");
            var read = await history.ReadAsync(sessionId).ConfigureAwait(true);
            Require(read.Available && read.LatestRun is { Terminal: true }, "ManualFailureHistoryUnavailable");
            var record = read.LatestRun!;
            Require(read.Header?.SessionId == sessionId && read.Header.StartCorrelationId == start!.CorrelationId &&
                record.SessionId == sessionId && record.CommandCorrelationId == run!.CorrelationId && runIds.Add(record.RunId),
                "ManualFailureAcceptedCommandBindingMismatch");
            Require(record.ExecutionStatus == expected && record.Decision == InspectionDecision.Unknown &&
                record.AlgorithmResultPayload is null && record.Result is null && record.FrameOverlay is null,
                "ManualFailureManufacturedSuccess");
            if (expected == ExecutionStatus.Error)
                Require(record.FrameMetadata is null, "ManualAcquisitionFailureManufacturedFrame");
            var station = await runtime.GetSnapshotAsync().ConfigureAwait(true);
            Require(!station.Ready && station.ArmState == ProductionArmState.Disarmed &&
                station.Mode == ExclusiveMode.None && station.CurrentExecution is null,
                "ManualFailureChangedProductionAuthority");
            failures.Add(new(sessionId, start!.CorrelationId, record.RunId, run!.CorrelationId,
                record.ExecutionStatus!.Value.ToString(),
                record.ReasonCode, record.ResultSchemaContentHash, record.ContentHash,
                closed.Restoration.ToString()));
        }
        return new(busy.ReasonCode, preview.ReasonCode, failures);
    }

    private sealed record ManualFailureEvidence(string BusyReason, string PreviewConflictReason,
        IReadOnlyList<ManualFailureRunEvidence> Runs);
    private sealed record ManualFailureRunEvidence(Guid SessionId, Guid StartCorrelationId, Guid RunId,
        Guid CommandCorrelationId, string ExecutionStatus,
        string ReasonCode, string? ResultSchemaContentHash, string ContentHash, string Restoration);
}
