using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace SharpInspect.Wpf.Tests;

/// <summary>
/// Presentation boundary tests for the explicit Recipe Release service.  The
/// fake service models the already constrained Runtime boundary; these tests
/// assert the exact revision, policy, authorization target and UI state that
/// the editor exposes to a human approver.
/// </summary>
public sealed class RecipeDraftReleaseEditorTests
{
    [Fact]
    public async Task V130_W10_ReopeningSavedRevisionAfterSessionClearRestoresReleaseReadiness()
    {
        await using var fixture = await OpenFixtureAsync();
        var source = fixture.Model.CurrentRevision!;
        fixture.Model.ReleaseReason = "previous person's unsaved release reason";
        fixture.Model.ClearTransientState();
        Assert.Equal(string.Empty, fixture.Model.ChangeReason);
        Assert.Equal("发布配方草稿", fixture.Model.ReleaseReason);
        Assert.False(fixture.Model.CanReleaseWithStepUp);

        await fixture.Model.RefreshAsync();
        fixture.Model.SelectedHistory = Assert.Single(fixture.Model.History);
        await fixture.Model.OpenSelectedAsync();

        Assert.True(fixture.Model.IsValid, fixture.Model.ValidationReasonCode);
        Assert.Equal(source.Content.ContentHash, fixture.Model.DraftContentHash);
        Assert.Equal(source.RevisionContentHash, fixture.Model.CurrentRevision!.RevisionContentHash);
        Assert.True(fixture.Model.CanReleaseWithStepUp);
    }

    [Fact]
    public async Task V130_W01_SingleApproverReleaseUsesExactRevisionAndStepUpBinding()
    {
        await using var fixture = await OpenFixtureAsync();

        var source = fixture.Model.CurrentRevision!;
        var result = await fixture.Model.ReleaseWithStepUpAsync("temporary-password");

        Assert.NotNull(result);
        Assert.Equal(CommandDisposition.Accepted, result!.Outcome.Disposition);
        Assert.NotNull(result.Recipe);
        Assert.True(result.Recipe!.Available);
        Assert.Same(result.Recipe, fixture.Model.ReleasedRecipe);
        Assert.Equal(source.DraftId, result.Recipe.Record.Source.DraftId);
        Assert.Equal(source.Revision, result.Recipe.Record.Source.Revision);
        Assert.Equal(source.RevisionContentHash, result.Recipe.Record.Source.RevisionContentHash);
        Assert.Equal(fixture.Service.Access.Policy, fixture.Model.ReleasePolicy);

        Assert.NotNull(fixture.Service.LastCommand);
        var command = fixture.Service.LastCommand!;
        Assert.Equal(source.DraftId, command.DraftId);
        Assert.Equal(source.Revision, command.ExpectedRevision);
        Assert.Equal(source.RevisionContentHash, command.ExpectedRevisionContentHash);
        Assert.Equal(fixture.Service.Access.Policy!.Reference, command.GovernancePolicy);
        Assert.Equal(command.AuthorizationTarget, fixture.StepUp.LastRequest!.Binding.TargetId);
        Assert.Equal(command.CorrelationId, fixture.StepUp.LastRequest.Binding.CommandCorrelationId);
        Assert.Equal(Permission.ReleaseRecipe, fixture.StepUp.LastRequest.Binding.Permission);
        Assert.Equal(AuditedCommandKind.ReleaseRecipe, fixture.StepUp.LastRequest.Binding.CommandKind);
        Assert.Equal("temporary-password", fixture.StepUp.LastPassword);
        Assert.Equal(fixture.StepUp.LastResult!.GrantId, command.Invocation.StepUpGrantId);
    }

    [Fact]
    public async Task V130_W02_MakerCheckerConflictIsShownAndDoesNotPublish()
    {
        await using var fixture = await OpenFixtureAsync(
            new RecipeGovernancePolicy("Recipe.Governance", "2", RecipeGovernanceMode.MakerCheckerRelease));
        fixture.Service.ReleaseOverride = (command, _) => ValueTask.FromResult(
            new RecipeReleaseResult(
                new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                    "RecipeReleaseMakerCheckerConflict", AuditPersistence.Persisted)));
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var result = await fixture.Model.ReleaseWithStepUpAsync("temporary-password");

        Assert.NotNull(result);
        Assert.Equal(CommandDisposition.Rejected, result!.Outcome.Disposition);
        Assert.Equal("RecipeReleaseMakerCheckerConflict", fixture.Model.ErrorCode);
        Assert.Contains("RecipeReleaseMakerCheckerConflict", fixture.Model.ReleaseStatusText);
        Assert.Null(fixture.Model.ReleasedRecipe);
        Assert.Equal(sourceHash, fixture.Model.CurrentRevision!.RevisionContentHash);
    }

    [Fact]
    public async Task V130_W03_ConcurrentEditAfterStepUpNeverCallsReleaseService()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await OpenFixtureAsync(stepUp: new FakeStepUp(request =>
        {
            entered.TrySetResult(true);
            return new ValueTask<StepUpResult>(complete.Task);
        }));
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var release = fixture.Model.ReleaseWithStepUpAsync("temporary-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Model.DisplayName = "changed while release was waiting";
        complete.TrySetResult(new StepUpResult(true, "StepUpAccepted", FixtureGrantId));

        var result = await release.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(result);
        Assert.Equal(0, fixture.Service.ReleaseCount);
        Assert.Equal("RecipeReleaseContentChanged", fixture.Model.ErrorCode);
        Assert.Null(fixture.Model.ReleasedRecipe);
        Assert.Equal(sourceHash, fixture.Model.CurrentRevision!.RevisionContentHash);
    }

    [Fact]
    public async Task V130_W04_DependencyFailureRemainsRejectedAndVisible()
    {
        await using var fixture = await OpenFixtureAsync();
        fixture.Service.ReleaseOverride = (command, _) => ValueTask.FromResult(
            new RecipeReleaseResult(
                new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                    "RecipeReleaseDependencyInvalid", AuditPersistence.Persisted)));

        var result = await fixture.Model.ReleaseWithStepUpAsync("temporary-password");

        Assert.NotNull(result);
        Assert.Equal(CommandDisposition.Rejected, result!.Outcome.Disposition);
        Assert.Equal("RecipeReleaseDependencyInvalid", fixture.Model.ErrorCode);
        Assert.Contains("RecipeReleaseDependencyInvalid", fixture.Model.ReleaseStatusText);
        Assert.Null(fixture.Model.ReleasedRecipe);
    }

    [Fact]
    public async Task V130_W05_AuditFailureRemainsRejectedAndVisible()
    {
        await using var fixture = await OpenFixtureAsync();
        fixture.Service.ReleaseOverride = (command, _) => ValueTask.FromResult(
            new RecipeReleaseResult(
                new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                    "RecipeReleaseAuditIntegrityFailed", AuditPersistence.Unavailable)));

        var result = await fixture.Model.ReleaseWithStepUpAsync("temporary-password");

        Assert.NotNull(result);
        Assert.Equal(CommandDisposition.Rejected, result!.Outcome.Disposition);
        Assert.Equal("RecipeReleaseAuditIntegrityFailed", fixture.Model.ErrorCode);
        Assert.Contains("RecipeReleaseAuditIntegrityFailed", fixture.Model.ReleaseStatusText);
        Assert.Null(fixture.Model.ReleasedRecipe);
    }

    [Fact]
    public async Task V130_W06_PanelStepUpClickClearsPasswordAndUsesFullAuthorizationTarget()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ReleaseFixture? fixture = null;
            Window? window = null;
            try
            {
                fixture = OpenFixtureAsync().GetAwaiter().GetResult();
                var panel = new RecipeDraftEditorPanel(fixture.Model);
                window = new Window
                {
                    Content = panel, Width = 1100, Height = 900, ShowInTaskbar = false,
                    ShowActivated = false, Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                window.UpdateLayout();

                var passwordBox = Assert.IsType<PasswordBox>(panel.FindName("ReleaseStepUpPasswordBox"));
                var button = Assert.IsType<Button>(panel.FindName("ReleaseStepUpButton"));
                Assert.True(fixture.Model.CanReleaseWithStepUp);
                Assert.True(button.IsEnabled);

                passwordBox.Password = "temporary-password";
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(string.Empty, passwordBox.Password);
                Assert.Equal("temporary-password", fixture.StepUp.LastPassword);
                Assert.Equal(Permission.ReleaseRecipe, fixture.StepUp.LastRequest!.Binding.Permission);
                Assert.Equal(AuditedCommandKind.ReleaseRecipe, fixture.StepUp.LastRequest.Binding.CommandKind);
                Assert.Equal(fixture.Service.LastCommand!.AuthorizationTarget,
                    fixture.StepUp.LastRequest.Binding.TargetId);
                Assert.NotNull(fixture.Model.ReleasedRecipe);
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
            finally
            {
                window?.Close();
                if (fixture is not null)
                    fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V130_W07_LateCancellationOrEditCannotRewriteACommittedOutcome(bool edit)
    {
        await using var fixture = await OpenFixtureAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Service.AfterCommit = () =>
        {
            if (edit) fixture.Model.DisplayName = "new unreviewed content";
            else cancellation.Cancel();
        };
        var result = await fixture.Model.ReleaseWithStepUpAsync("temporary-password", cancellation.Token);
        Assert.NotNull(result);
        Assert.Equal(CommandDisposition.Accepted, result!.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        Assert.NotNull(result.Recipe);
        Assert.NotEqual("RecipeReleaseCancelled", fixture.Model.ErrorCode);
        if (edit)
        {
            Assert.Null(fixture.Model.ReleasedRecipe);
            Assert.False(fixture.Model.CanRelease);
        }
        else Assert.Same(result.Recipe, fixture.Model.ReleasedRecipe);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("AuditUnavailable")]
    [InlineData("InvalidResult")]
    [InlineData("Cancelled")]
    [InlineData("Exception")]
    public async Task V130_W08_QueuedOldResultCannotOverwriteANewerDraft(string failure)
    {
        var dispatcher = new PausingDispatcher();
        await using var fixture = await OpenFixtureAsync(dispatcher: dispatcher);
        using var cancellation = new CancellationTokenSource();
        fixture.Service.ReleaseOverride = (command, _) =>
        {
            dispatcher.PauseNext = true;
            if (failure == "Cancelled")
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<RecipeReleaseResult>(cancellation.Token);
            }
            if (failure == "Exception") throw new InvalidOperationException("ServiceUnavailable");
            return ValueTask.FromResult(new RecipeReleaseResult(new RuntimeCommandOutcome(
                command.CorrelationId, failure == "Rejected" ? CommandDisposition.Rejected : CommandDisposition.Accepted,
                "RecipeReleaseDependencyInvalid", failure == "AuditUnavailable" ? AuditPersistence.Unavailable : AuditPersistence.Persisted)));
        };
        var pending = fixture.Model.ReleaseWithStepUpAsync("temporary-password", cancellation.Token);
        await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            fixture.Model.DisplayName = "changed before queued failure applied";
            var expectedError = fixture.Model.ErrorCode;
            var expectedStatus = fixture.Model.ReleaseStatusText;
            dispatcher.Resume.TrySetResult(true);
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            if (failure is "Cancelled" or "Exception") Assert.Null(outcome);
            else Assert.NotNull(outcome);
            Assert.Equal(expectedError, fixture.Model.ErrorCode);
            Assert.Equal(expectedStatus, fixture.Model.ReleaseStatusText);
            Assert.Null(fixture.Model.ReleasedRecipe);
        }
        finally { dispatcher.Resume.TrySetResult(true); }
    }

    [Theory]
    [InlineData("Access")]
    [InlineData("StepUp")]
    public async Task V130_W09_QueuedAuthorityRejectionCannotOverwriteANewerDraft(string stage)
    {
        var dispatcher = new PausingDispatcher();
        var stepUp = stage == "StepUp" ? new FakeStepUp(_ =>
        {
            dispatcher.PauseNext = true;
            return ValueTask.FromResult(new StepUpResult(false, "StepUpAuthenticationRejected", null));
        }) : null;
        await using var fixture = await OpenFixtureAsync(stepUp: stepUp, dispatcher: dispatcher);
        if (stage == "Access") fixture.Service.AccessOverride = () =>
        {
            dispatcher.PauseNext = true;
            return new(false, "PermissionDenied", fixture.Service.Access.Policy);
        };
        var pending = fixture.Model.ReleaseWithStepUpAsync("temporary-password");
        await dispatcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            fixture.Model.DisplayName = "changed before authority reply was applied";
            var expectedError = fixture.Model.ErrorCode;
            dispatcher.Resume.TrySetResult(true);
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(expectedError, fixture.Model.ErrorCode);
            Assert.DoesNotContain("发布未完成", fixture.Model.ReleaseStatusText);
            Assert.Equal(0, fixture.Service.ReleaseCount);
            Assert.Null(fixture.Model.ReleasedRecipe);
        }
        finally { dispatcher.Resume.TrySetResult(true); }
    }

    private sealed class PausingDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        internal bool PauseNext { get; set; }
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask InvokeAsync(Action action)
        {
            if (PauseNext)
            {
                PauseNext = false;
                Entered.TrySetResult(true);
                await Resume.Task;
            }
            action();
        }
    }

    private static readonly Guid FixtureGrantId =
        Guid.Parse("00000000-0000-0000-0000-000000000303");

    private static async Task<ReleaseFixture> OpenFixtureAsync(
        RecipeGovernancePolicy? policy = null, FakeStepUp? stepUp = null, IUiDispatcher? dispatcher = null)
    {
        var descriptor = Descriptor();
        var content = Content(descriptor);
        var sessions = new FakeSessions(Authenticated());
        var revision = new RecipeDraftRevision(1,
            Guid.Parse("00000000-0000-0000-0000-000000000301"), 1,
            Guid.Parse("00000000-0000-0000-0000-000000000302"), null,
            new string('A', 64), content,
            Guid.Parse("00000000-0000-0000-0000-000000000201"),
            sessions.Current.SessionId!.Value, 7, "seed",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var editor = new FakeEditor(descriptor, revision);
        var effectiveStepUp = stepUp ?? new FakeStepUp(_ =>
            ValueTask.FromResult(new StepUpResult(true, "StepUpAccepted", FixtureGrantId)));
        var service = new FakeReleaseService(editor,
            policy ?? new RecipeGovernancePolicy("Recipe.Governance", "1",
                RecipeGovernanceMode.SingleApproverRelease));
        var model = new RecipeDraftEditorViewModel(editor, sessions,
            dispatcher ?? new InlineUiDispatcher(), null, effectiveStepUp,
            null, service);

        await model.RefreshAsync();
        model.SelectedHistory = Assert.Single(model.History);
        await model.OpenSelectedAsync();
        return new ReleaseFixture(model, editor, sessions, service,
            effectiveStepUp);
    }

    private static AlgorithmDescriptor Descriptor()
    {
        var fields = new[]
        {
            new AlgorithmFieldDefinition("Count", AlgorithmScalarType.Int64, "items", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
                AlgorithmScalarValue.FromInt64(4), "整数值")
        };
        var schema = new AlgorithmConfigurationSchema("Recipe.Config", "1", fields);
        var overlay = new OverlayContract("Recipe.Overlay", "1");
        var result = new AlgorithmResultSchema("Recipe.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlay);
        return new AlgorithmDescriptor(new AlgorithmIdentity("Recipe.Algorithm", "1"),
            schema, result);
    }

    private static RecipeDraftContent Content(AlgorithmDescriptor descriptor)
    {
        var entry = new AlgorithmConfigurationEntry("Count", "items",
            AlgorithmScalarValue.FromInt64(4));
        var configuration = AlgorithmConfigurationSnapshot.Create(
            descriptor.ConfigurationSchema, new[] { entry });
        return new RecipeDraftContent("ReleaseRecipe", "Release Recipe",
            RecipeAlgorithmBinding.FromDescriptor(descriptor), configuration, "Primary",
            new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
                new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 1000, 0, null),
            TimeSpan.FromMilliseconds(250), Array.Empty<RecipeAssetRequirement>(),
            Array.Empty<RecipePolicyRequirement>(),
            new[] { new RecipeDraftFieldOrigin("Count", RecipeDraftValueOrigin.AuthoringDefault) });
    }

    private static InteractiveSession Authenticated() => new(
        InteractiveSessionState.Authenticated,
        "00000000-0000-0000-0000-000000000201",
        Guid.Parse("00000000-0000-0000-0000-000000000202"));

    private sealed class ReleaseFixture : IAsyncDisposable
    {
        public ReleaseFixture(RecipeDraftEditorViewModel model, FakeEditor editor,
            FakeSessions sessions, FakeReleaseService service, FakeStepUp stepUp)
        {
            Model = model; Editor = editor; Sessions = sessions; Service = service; StepUp = stepUp;
        }

        public RecipeDraftEditorViewModel Model { get; }
        public FakeEditor Editor { get; }
        public FakeSessions Sessions { get; }
        public FakeReleaseService Service { get; }
        public FakeStepUp StepUp { get; }
        public ValueTask DisposeAsync() => Model.DisposeAsync();
    }

    private sealed class FakeEditor : IRecipeDraftEditor
    {
        private readonly AlgorithmDescriptor _descriptor;
        private RecipeDraftRevision _stored;

        public FakeEditor(AlgorithmDescriptor descriptor, RecipeDraftRevision stored)
        { _descriptor = descriptor; _stored = stored; }

        public IReadOnlyList<AlgorithmDescriptor> Algorithms => new[] { _descriptor };
        public RecipeDraftAccess Access { get; set; } =
            new(true, "RecipeDraftAccessGranted", false);
        public RecipeDraftValidationResult Validation { get; set; } =
            new(true, "RecipeDraftValid", Array.Empty<AlgorithmValidationIssue>());
        public IReadOnlyList<AlgorithmConfigurationEntry> GetAuthoringDefaults(
            AlgorithmIdentity algorithm) => new[]
            {
                new AlgorithmConfigurationEntry("Count", "items", AlgorithmScalarValue.FromInt64(4))
            };
        public ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Access);
        public ValueTask<RecipeDraftValidationResult> ValidateAsync(RecipeDraftContent content,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Validation);
        public ValueTask<RecipeDraftSaveResult> SaveAsync(RecipeDraftSaveRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new RecipeDraftSaveResult(false, "RecipeDraftSaveRejected", null,
                Array.Empty<AlgorithmValidationIssue>()));
        public ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            draftId == _stored.DraftId && (revision is null || revision == _stored.Revision)
                ? new RecipeDraftReadResult(true, "RecipeDraftRead", _stored)
                : new RecipeDraftReadResult(false, "RecipeDraftNotFound", null));
        public ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new RecipeDraftPage(true, "RecipeDraftHistoryAvailable",
                new[] { _stored }, _stored.Position, null));
    }

    private sealed class FakeReleaseService : IRecipeReleaseService
    {
        private readonly FakeEditor _editor;
        public FakeReleaseService(FakeEditor editor, RecipeGovernancePolicy policy)
        { _editor = editor; Access = new(true, "RecipeReleaseAccessGranted", policy); }

        public RecipeReleaseAccess Access { get; set; }
        public Func<RecipeReleaseAccess>? AccessOverride { get; set; }
        public ReleaseRecipeCommand? LastCommand { get; private set; }
        public int ReleaseCount { get; private set; }
        public Action? AfterCommit { get; set; }
        public Func<ReleaseRecipeCommand, CancellationToken, ValueTask<RecipeReleaseResult>>? ReleaseOverride { get; set; }

        public ValueTask<RecipeReleaseAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AccessOverride?.Invoke() ?? Access);

        public ValueTask<RecipeReleaseResult> ReleaseAsync(ReleaseRecipeCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            ReleaseCount++;
            if (ReleaseOverride is not null) return ReleaseOverride(command, cancellationToken);

            var source = _editor.ReadAsync(command.DraftId, command.ExpectedRevision,
                cancellationToken).GetAwaiter().GetResult().Revision!;
            var check = new RecipeReleaseValidationCheck("Structure", "RecipeDraft", true,
                "Passed");
            var change = new RecipeReleaseChange("DisplayName", "before", "after",
                source.AuthorPrincipalId, source.DraftId, source.Revision);
            var grant = command.Invocation.StepUpGrantId ?? FixtureGrantId;
            var record = new RecipeReleaseRecord(1,
                Guid.Parse("00000000-0000-0000-0000-000000000304"), command.CorrelationId, 1,
                source, Access.Policy!, new[] { check }, new[] { change },
                source.AuthorPrincipalId, command.Invocation.SessionId!.Value,
                source.AuthorAuthorizationRevision, grant,
                new RecipeContractReference("Authorization.Policy", "1", new string('D', 64)),
                command.ReleaseReason, command.AuthorizationTarget, DateTimeOffset.UtcNow);
            var released = new ReleasedRecipe(record);
            AfterCommit?.Invoke();
            return ValueTask.FromResult(new RecipeReleaseResult(
                new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Accepted,
                    "RecipeReleased", AuditPersistence.Persisted), released));
        }

        public ValueTask<ReleasedRecipeReadResult> ReadAsync(RecipeReference reference,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new ReleasedRecipeReadResult(false, "RecipeReleaseNotFound"));

        public ValueTask<ReleasedRecipePage> QueryAsync(ReleasedRecipeFilter filter,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new ReleasedRecipePage(true, "RecipeReleaseHistoryAvailable",
                Array.Empty<ReleasedRecipe>(), 0, null));
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        private readonly Func<StepUpRequest, ValueTask<StepUpResult>> _authenticate;
        public FakeStepUp(Func<StepUpRequest, ValueTask<StepUpResult>> authenticate) =>
            _authenticate = authenticate;
        public StepUpRequest? LastRequest { get; private set; }
        public StepUpResult? LastResult { get; private set; }
        public string? LastPassword => LastRequest?.Password;
        public async ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            LastResult = await _authenticate(request).ConfigureAwait(false);
            return LastResult;
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        public FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed { add { } remove { } }
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId,
            SessionLockReason reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
