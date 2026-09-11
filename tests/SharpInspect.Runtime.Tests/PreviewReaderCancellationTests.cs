using System.Diagnostics;
using SharpInspect.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V138_G09_ProviderCancellationResultDuringFreezePauseKeepsPreviewStreaming()
    {
        await using var harness = await ActivationHarness.CreateAsync(enablePreview: true);
        var invocation = harness.ActivationInvocation();
        await WaitForPreviewIdleAsync(harness, invocation);

        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V138 G09 provider cancellation result during freeze pause");
        AssertAccepted(await harness.Runtime.SubmitAsync(start));
        var streaming = await WaitForPreviewAsync(harness, invocation, sessionId,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null, "PreviewCancellationStartUnavailable");
        var previousSequence = streaming.LatestFrame!.Sequence;

        harness.CameraProvider.HoldActivationPreviewReadUntilCancellation();
        await harness.CameraProvider.ActivationPreviewReadCancellationEntered.WaitAsync(
            TimeSpan.FromSeconds(5));

        try
        {
            var freeze = new FreezePreviewSettingsCommand(Guid.NewGuid(), invocation, sessionId,
                "V138 G09 freeze while provider returns cancellation failure");
            AssertAccepted(await harness.Runtime.SubmitAsync(freeze));

            var afterCancellation = await WaitForPreviewStreamingAfterCancellationAsync(
                harness, invocation, sessionId, previousSequence);
            Assert.True(harness.CameraProvider.ActivationPreviewReadCancellationObserved);
            Assert.NotNull(afterCancellation.FrozenSettings);
            Assert.NotNull(afterCancellation.LatestFrame);
            Assert.True(afterCancellation.LatestFrame!.Sequence > previousSequence);

            var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
                cancel: false, "V138 G09 close after provider cancellation result");
            AssertAccepted(await harness.Runtime.SubmitAsync(exit));
            var closed = await WaitForPreviewAsync(harness, invocation, sessionId,
                snapshot => snapshot.Phase == PreviewSessionPhase.Closed &&
                    snapshot.Restoration == PreviewRestorationState.NoActiveBaselineClosed,
                "PreviewCancellationCloseUnavailable");
            Assert.Equal(PreviewRestorationState.NoActiveBaselineClosed, closed.Restoration);
        }
        finally
        {
            harness.CameraProvider.ReleaseActivationPreviewReadCancellation();
        }
    }

    private static async Task<PreviewSessionSnapshot> WaitForPreviewStreamingAfterCancellationAsync(
        ActivationHarness harness, CommandInvocation invocation, Guid sessionId,
        long previousSequence)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(10).TotalSeconds);
        PreviewSessionSnapshot? last = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var read = await ((IPreviewSessionService)harness.Runtime).GetSnapshotAsync(invocation);
            Assert.True(read.Available, read.ReasonCode);
            var snapshot = Assert.IsType<PreviewSessionSnapshot>(read.Snapshot);
            last = snapshot;
            if (snapshot.PreviewSessionId == sessionId &&
                snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.FrozenSettings is not null &&
                snapshot.LatestFrame is { Sequence: > 0 } frame &&
                frame.Sequence > previousSequence)
                return snapshot;
            if (snapshot.PreviewSessionId == sessionId &&
                snapshot.Phase is PreviewSessionPhase.Closed or PreviewSessionPhase.RecoveryBlocked)
                throw new XunitException(
                    $"PreviewCancellationStreamingLost:{snapshot.Phase}/{snapshot.ReasonCode}");
            await Task.Delay(25);
        }

        throw new XunitException(
            $"PreviewCancellationStreamingTimeout:{last?.Phase}/{last?.ReasonCode}");
    }
}

public sealed partial class RecipeActivationServiceTests
{
    internal sealed partial class VirtualCameraProvider
    {
        internal Task ActivationPreviewReadCancellationEntered =>
            _activationDevice.PreviewReadCancellationEntered;
        internal bool ActivationPreviewReadCancellationObserved =>
            _activationDevice.PreviewReadCancellationObserved;

        internal void HoldActivationPreviewReadUntilCancellation() =>
            _activationDevice.HoldNextPreviewReadUntilCancellation();

        internal void ReleaseActivationPreviewReadCancellation() =>
            _activationDevice.ReleasePreviewReadCancellation();
    }

    private sealed partial class VirtualCameraDevice
    {
        private TaskCompletionSource<bool>? _previewReadCancellationEntered;
        private TaskCompletionSource<bool>? _previewReadCancellationRelease;
        private bool _previewReadCancellationBarrier;
        private int _previewReadCancellationObserved;

        internal Task PreviewReadCancellationEntered
        {
            get
            {
                lock (_previewSync)
                    return _previewReadCancellationEntered?.Task ?? Task.CompletedTask;
            }
        }

        internal bool PreviewReadCancellationObserved =>
            Volatile.Read(ref _previewReadCancellationObserved) != 0;

        internal void HoldNextPreviewReadUntilCancellation()
        {
            lock (_previewSync)
            {
                if (_previewReadCancellationBarrier || _previewReadCancellationRelease is not null)
                    throw new InvalidOperationException("PreviewReadCancellationBarrierAlreadySet");
                _previewReadCancellationEntered = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _previewReadCancellationRelease = new(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _previewReadCancellationBarrier = true;
                Volatile.Write(ref _previewReadCancellationObserved, 0);
            }
        }

        internal void ReleasePreviewReadCancellation()
        {
            TaskCompletionSource<bool>? release;
            lock (_previewSync) release = _previewReadCancellationRelease;
            release?.TrySetResult(true);
        }

        private bool TryHoldNextPreviewReadUntilCancellation(CancellationToken cancellationToken,
            out ValueTask<CameraPreviewFrameResult> cancellationRead)
        {
            TaskCompletionSource<bool>? entered;
            TaskCompletionSource<bool>? release;
            lock (_previewSync)
            {
                if (!_previewReadCancellationBarrier)
                {
                    cancellationRead = default;
                    return false;
                }

                _previewReadCancellationBarrier = false;
                entered = _previewReadCancellationEntered;
                release = _previewReadCancellationRelease;
            }

            entered?.TrySetResult(true);
            cancellationRead = new ValueTask<CameraPreviewFrameResult>(
                WaitForPreviewReadCancellationAsync(cancellationToken, release));
            return true;
        }

        private async Task<CameraPreviewFrameResult> WaitForPreviewReadCancellationAsync(
            CancellationToken cancellationToken, TaskCompletionSource<bool>? release)
        {
            var cancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled);
            if (release is null)
                await cancelled.Task.ConfigureAwait(false);
            else
                await Task.WhenAny(cancelled.Task, release.Task).ConfigureAwait(false);

            var wasCancelled = cancelled.Task.IsCompleted;
            if (wasCancelled) Volatile.Write(ref _previewReadCancellationObserved, 1);
            lock (_previewSync)
            {
                if (ReferenceEquals(_previewReadCancellationRelease, release))
                    _previewReadCancellationRelease = null;
            }

            return CameraPreviewFrameResult.Failure(wasCancelled
                ? "PreviewOperationCancelled" : "VirtualCameraPreviewNoNewFrame");
        }
    }
}
