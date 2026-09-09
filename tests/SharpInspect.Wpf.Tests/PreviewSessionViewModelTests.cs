using System.Runtime.CompilerServices;
using System.Windows.Media;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class PreviewSessionViewModelTests
{
    [Fact]
    [Trait("VerificationId", "V133_W01")]
    public void PreviewDisplayImage_ScalesDeclaredMono16RangeAndFreezesBitmap()
    {
        var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var frame = new CameraPreviewFrame(sessionId, 7,
            new FrameTimePoint(DateTimeOffset.UnixEpoch, 1),
            2, 1, 4, VisionPixelFormat.Mono16, 10,
            new byte[] { 0, 0, 0xFF, 0x03 });

        var image = PreviewDisplayImage.CopyFromFrame(frame);

        Assert.True(image.BitmapSource.IsFrozen);
        Assert.Equal(2, image.BitmapSource.PixelWidth);
        Assert.Equal(1, image.BitmapSource.PixelHeight);
        Assert.Equal(PixelFormats.Gray8, image.BitmapSource.Format);
        Assert.Equal(sessionId, image.SessionId);
        Assert.Equal(7, image.Sequence);
        var pixels = new byte[2];
        image.BitmapSource.CopyPixels(pixels, 2, 0);
        Assert.Equal(new byte[] { 0, 255 }, pixels);
    }

    [Fact]
    [Trait("VerificationId", "V133_W02")]
    public void PreviewDisplayImage_PreservesBgr24CanonicalPixelsAndRowPaddingIsExcluded()
    {
        var frame = new CameraPreviewFrame(Guid.NewGuid(), 1,
            new FrameTimePoint(DateTimeOffset.UnixEpoch, 2),
            1, 1, 4, VisionPixelFormat.Bgr24, null,
            new byte[] { 4, 5, 6, 0xEE });

        var image = PreviewDisplayImage.CopyFromPreviewFrame(frame);

        Assert.Equal(PixelFormats.Bgr24, image.BitmapSource.Format);
        Assert.Equal(3, image.DisplayBufferLength);
        var pixels = new byte[3];
        image.BitmapSource.CopyPixels(pixels, 3, 0);
        Assert.Equal(new byte[] { 4, 5, 6 }, pixels);
    }

    [Fact]
    [Trait("VerificationId", "V133_W03")]
    public async Task StartUsesExactDraftExpectedActiveAndAuthenticatedInvocation()
    {
        var fixture = Fixture.Create();
        fixture.Preview.Results.Enqueue(new PreviewSessionReadResult(true, "PreviewStreaming",
            fixture.ActiveSnapshot()));
        await using var viewModel = fixture.CreateViewModel();

        var reason = "进入预览调参";
        var requestSessionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var outcome = await viewModel.StartPreviewSessionAsync(requestSessionId,
            fixture.Draft, fixture.ExpectedActive, reason);

        var command = Assert.IsType<StartPreviewSessionCommand>(fixture.Runtime.Commands.Single());
        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        Assert.Equal(requestSessionId, command.PreviewSessionId);
        Assert.Equal(fixture.Draft, command.Draft);
        Assert.Equal(fixture.ExpectedActive, command.ExpectedActive);
        Assert.Equal(reason, command.ChangeReason);
        Assert.Equal(fixture.PrincipalId.ToString("D"), command.Invocation.PrincipalId);
        Assert.Equal(fixture.SessionId, command.Invocation.SessionId);
        Assert.Equal(PreviewSessionPhase.Streaming, viewModel.Phase);
        Assert.False(viewModel.Ready);
    }

    [Fact]
    [Trait("VerificationId", "V133_W04")]
    public async Task OlderPreviewSequenceCannotReplaceLatestDisplayAndDeactivateDoesNotExitRuntime()
    {
        var fixture = Fixture.Create();
        fixture.Preview.Results.Enqueue(new PreviewSessionReadResult(true, "PreviewStreaming",
            fixture.ActiveSnapshot(sequence: 2, revision: 2)));
        fixture.Preview.Results.Enqueue(new PreviewSessionReadResult(true, "PreviewStreaming",
            fixture.ActiveSnapshot(sequence: 1, revision: 3)));
        await using var viewModel = fixture.CreateViewModel();

        await viewModel.RefreshAsync();
        Assert.Equal(2, viewModel.LatestImage?.Sequence);
        await viewModel.RefreshAsync();
        Assert.Equal(2, viewModel.LatestImage?.Sequence);

        viewModel.Deactivate();
        Assert.Null(viewModel.LatestImage);
        Assert.Empty(fixture.Runtime.Commands);
        Assert.DoesNotContain(fixture.Runtime.Commands, command =>
            command is ExitPreviewSessionCommand or GracefulProductionStopCommand);
    }

    [Fact]
    [Trait("VerificationId", "V133_W05")]
    public async Task DeactivateDoesNotCancelPendingAcceptedCommandOrSubmitPreviewExit()
    {
        var fixture = Fixture.Create();
        fixture.Preview.Results.Enqueue(new PreviewSessionReadResult(true, "PreviewStreaming",
            fixture.ActiveSnapshot()));
        var submitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubmit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.SubmitStarted = submitStarted;
        fixture.Runtime.ReleaseSubmit = releaseSubmit;
        await using var viewModel = fixture.CreateViewModel();

        var pending = viewModel.StartPreviewSessionAsync(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            fixture.Draft, fixture.ExpectedActive, "页面离开期间保持 Runtime 命令独立");

        await submitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.Deactivate();

        Assert.False(fixture.Runtime.SubmittedCancellation.IsCancellationRequested);
        Assert.DoesNotContain(fixture.Runtime.Commands, command =>
            command is ExitPreviewSessionCommand or GracefulProductionStopCommand);

        releaseSubmit.TrySetResult(true);
        _ = await pending;

        Assert.False(fixture.Runtime.SubmittedCancellation.IsCancellationRequested);
        Assert.DoesNotContain(fixture.Runtime.Commands, command =>
            command is ExitPreviewSessionCommand or GracefulProductionStopCommand);
    }

    private sealed class Fixture
    {
        public readonly Guid PrincipalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public readonly Guid SessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        public readonly Guid RuntimeEpoch = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        public readonly Guid PreviewSessionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        public readonly Guid DraftId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        private Fixture()
        {
            Sessions = new FakeSessions(new InteractiveSession(
                InteractiveSessionState.Authenticated, PrincipalId.ToString("D"), SessionId));
            Runtime = new FakeRuntime();
            Preview = new FakePreviewQuery();
            Draft = new PreviewDraftReference(DraftId, 3, Hash);
            ExpectedActive = new RecipeActivationReference(11,
                Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Hash);
        }

        private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        public FakeRuntime Runtime { get; }
        public FakePreviewQuery Preview { get; }
        public FakeSessions Sessions { get; }
        public PreviewDraftReference Draft { get; }
        public RecipeActivationReference ExpectedActive { get; }

        public static Fixture Create() => new();

        public PreviewSessionViewModel CreateViewModel() =>
            new(Runtime, Preview, Sessions, new InlineDispatcher(), pollInterval: TimeSpan.FromSeconds(1));

        public PreviewSessionSnapshot ActiveSnapshot(long sequence = 1, long revision = 1)
        {
            var frame = new CameraPreviewFrame(PreviewSessionId, sequence,
                new FrameTimePoint(DateTimeOffset.UnixEpoch, sequence),
                1, 1, 1, VisionPixelFormat.Mono8, null, new byte[] { checked((byte)sequence) });
            var process = new PreviewCameraProcessSettings(10, 0,
                new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null);
            return new PreviewSessionSnapshot(RuntimeEpoch, revision, PreviewSessionId,
                PreviewSessionPhase.Streaming, "PreviewStreaming", Draft,
                new PreviewTuningConfiguration(process), null, frame,
                PreviewRestorationState.NotRequired, false, null, null);
        }
    }

    private sealed class FakeRuntime : IStationRuntime
    {
        public List<RuntimeCommand> Commands { get; } = new();
        public TaskCompletionSource<bool>? SubmitStarted { get; set; }
        public TaskCompletionSource<bool>? ReleaseSubmit { get; set; }
        public CancellationToken SubmittedCancellation { get; private set; }

        public ValueTask<StationStateSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            SubmittedCancellation = cancellationToken;
            SubmitStarted?.TrySetResult(true);
            return SubmitCoreAsync(command, cancellationToken);
        }

        private async ValueTask<RuntimeCommandOutcome> SubmitCoreAsync(
            RuntimeCommand command, CancellationToken cancellationToken)
        {
            var releaseSubmit = ReleaseSubmit;
            if (releaseSubmit is not null)
                await releaseSubmit.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new RuntimeCommandOutcome(command.CorrelationId,
                CommandDisposition.Accepted, "PreviewCommandAccepted", AuditPersistence.Persisted);
        }
    }

    private sealed class FakePreviewQuery : IPreviewSessionService
    {
        public PreviewSessionAccess Access { get; set; } =
            new(true, "PreviewAccessAvailable", RequiresStepUp: false);
        public Queue<PreviewSessionReadResult> Results { get; } = new();
        public int SnapshotReads { get; private set; }

        public ValueTask<PreviewSessionAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Access);
        }

        public ValueTask<PreviewSessionReadResult> GetSnapshotAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotReads++;
            return ValueTask.FromResult(Results.Count == 0
                ? new PreviewSessionReadResult(false, "PreviewSnapshotUnavailable", null)
                : Results.Dequeue());
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        public FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;

        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId,
            SessionLockReason reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Publish(InteractiveSession session)
        {
            Current = session;
            Changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }
}
