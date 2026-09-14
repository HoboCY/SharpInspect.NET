using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed partial class ProductionOutboxViewModelTests
{
    [Fact]
    [Trait("VerificationId", "V153_W10")]
    public async Task V153_W10_RecoveryBindsFreshStepUpAndConsumesTheObservedSelection()
    {
        var row = Row(1, permanent: true);
        var sessions = new Sessions();
        var recovery = new Recovery();
        var stepUp = new StepUp();
        await using var model = OperationsModel(row, recovery, sessions, stepUp);
        await model.RefreshAsync();
        model.ReasonText = "接收方权限已修复";
        model.AttemptsText = "2";
        Assert.True(model.CanRecover);
        var outcome = await model.RecoverWithStepUpAsync("ephemeral-fixture-password");
        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        var command = Assert.IsType<RecoverOutboxDeliveryCommand>(recovery.Last);
        Assert.Equal(row.Delivery.DeliveryId, command.DeliveryId);
        Assert.Equal(row.Delivery.ContentHash, command.ExpectedDeliveryContentHash);
        Assert.Equal(row.StateRevisionHash, command.ExpectedStateRevisionHash);
        Assert.Equal(2, command.RequestedAttempts);
        Assert.Equal(stepUp.Grant, command.Invocation.StepUpGrantId);
        Assert.Equal(sessions.Current.SessionId, command.Invocation.SessionId);
        Assert.Equal(new StepUpBinding(Permission.RecoverOutboxDelivery, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.RecoverOutboxDelivery), stepUp.Binding);
        Assert.Null(model.SelectedItem);
        Assert.False(model.CanRecover);
        Assert.Empty(model.ReasonText);
        Assert.DoesNotContain("ephemeral-fixture-password", model.StatusMessage);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("reason")]
    [InlineData("session")]
    [InlineData("deactivate")]
    [Trait("VerificationId", "V153_W11")]
    public async Task V153_W11_ChangedContextDuringStepUpNeverSubmitsOldRequest(string change)
    {
        var row = Row(1, permanent: true);
        var sessions = new Sessions();
        var recovery = new Recovery();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepUp = new StepUp(_ => { entered.TrySetResult(true); return release.Task; });
        await using var model = OperationsModel(row, recovery, sessions, stepUp);
        await model.RefreshAsync();
        model.ReasonText = "修复完成";
        var submit = model.RecoverWithStepUpAsync("ephemeral-fixture-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.IsBusy);
        Assert.Null(await model.RecoverWithStepUpAsync("another-fixture-password"));
        switch (change)
        {
            case "selection": model.SelectedItem = null; break;
            case "reason": model.ReasonText = "另一处置原因"; break;
            case "session": sessions.Lock(); break;
            default: model.Deactivate(); break;
        }
        release.TrySetResult(new(true, "Authenticated", stepUp.Grant));
        Assert.Null(await submit.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(recovery.Last);
        Assert.Equal(1, stepUp.CallCount);
    }

    [Fact]
    [Trait("VerificationId", "V153_W12")]
    public async Task V153_W12_CorrectiveSubmissionOwnsExactBytesAndBindsTheirHash()
    {
        var row = Row(1, permanent: true);
        var sessions = new Sessions();
        var recovery = new Recovery();
        var stepUp = new StepUp();
        await using var model = OperationsModel(row, recovery, sessions, stepUp);
        await model.RefreshAsync();
        model.ReasonText = "按审核后的最终内容更正";
        var bytes = new byte[] { 123, 34, 118, 34, 58, 50, 125 };
        var expected = bytes.ToArray();
        model.SetCorrectionPayload("approved.json", bytes);
        Array.Fill(bytes, (byte)0);
        Assert.True(model.CanCorrect);
        Assert.Equal(CommandDisposition.Accepted,
            (await model.CorrectWithStepUpAsync("ephemeral-fixture-password"))!.Disposition);
        var command = Assert.IsType<CreateCorrectiveOutboxDeliveryCommand>(recovery.Last);
        Assert.Equal(expected, command.CopyPayload());
        Assert.Equal(row.Delivery.DeliveryId, command.SourceDeliveryId);
        Assert.Equal(new StepUpBinding(Permission.CreateCorrectiveOutboxDelivery, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.CreateCorrectiveOutboxDelivery), stepUp.Binding);
        Assert.False(model.CanCorrect);
        Assert.Contains("尚未选择", model.CorrectionPayloadSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V153_W13")]
    public async Task V153_W13_MissingOrRejectedAuthenticationCannotReachRecovery(bool rejected)
    {
        var recovery = new Recovery();
        var stepUp = new StepUp(_ => Task.FromResult(new StepUpResult(false, "Denied")));
        await using var model = OperationsModel(Row(1, permanent: true), recovery,
            rejected ? new Sessions() : null, stepUp);
        await model.RefreshAsync();
        model.ReasonText = "修复完成";
        Assert.Null(await model.RecoverWithStepUpAsync("ephemeral-fixture-password"));
        Assert.Null(recovery.Last);
        Assert.Equal(rejected ? 1 : 0, stepUp.CallCount);
    }

    [Fact]
    [Trait("VerificationId", "V153_W14")]
    public async Task V153_W14_PostDispatchEditingEndsOnlyTheUiWaitAndRequiresFreshEvidence()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new Recovery(async _ => { entered.TrySetResult(true); await finish.Task; });
        await using var model = OperationsModel(Row(1, permanent: true), recovery, new Sessions(), new StepUp());
        await model.RefreshAsync();
        model.ReasonText = "修复完成";
        var submitting = model.RecoverWithStepUpAsync("ephemeral-fixture-password");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            model.HistoryDeliveryText = Guid.NewGuid().ToString("D");
            Assert.Null(await submitting.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(recovery.ObservedToken.CanBeCanceled);
            Assert.False(model.IsBusy);
            Assert.False(model.CanRecover);
            Assert.Contains("结果尚未确认", model.StatusMessage);
        }
        finally { finish.TrySetResult(true); }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign")]
    [InlineData("future")]
    [Trait("VerificationId", "V153_W15")]
    public async Task V153_W15_GovernanceRowsMustMatchDeliveryAndVerifiedWatermark(string variant)
    {
        var row = Row(1, permanent: true);
        var fact = new ProductionOutboxRecoveryRecord(Guid.NewGuid(),
            variant == "foreign" ? Guid.NewGuid() : row.Delivery.DeliveryId, 2, "依赖已修复",
            Guid.NewGuid(), DateTimeOffset.UtcNow, variant == "future" ? 21 : 20, Hash);
        var governance = new Governance(_ => Task.FromResult(new ProductionOutboxGovernanceSnapshot(
            true, "Verified", 20, new[] { fact }, null)));
        await using var model = new ProductionOutboxViewModel(new Query(_ => Task.FromResult(Page(row))),
            null, governance, null, null, new InlineUiDispatcher());
        await model.RefreshAsync();
        await model.ReadHistoryAsync();
        if (variant == "valid") Assert.Equal(fact, Assert.Single(model.Recoveries));
        else { Assert.Empty(model.Recoveries); Assert.Contains("历史暂不可用", model.StatusMessage); }
    }

    [Fact]
    [Trait("VerificationId", "V153_W16")]
    public async Task V153_W16_LateGovernanceCannotRestoreHiddenPrivateRows()
    {
        var row = Row(1, permanent: true);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ProductionOutboxGovernanceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var governance = new Governance(_ => { entered.TrySetResult(true); return finish.Task; });
        await using var model = new ProductionOutboxViewModel(new Query(_ => Task.FromResult(Page(row))),
            null, governance, null, null, new InlineUiDispatcher());
        await model.RefreshAsync();
        var reading = model.ReadHistoryAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        model.Deactivate();
        finish.TrySetResult(new(true, "Verified", 20, new[] { new ProductionOutboxRecoveryRecord(
            Guid.NewGuid(), row.Delivery.DeliveryId, 1, "修复完成", Guid.NewGuid(), DateTimeOffset.UtcNow, 20, Hash) }, null));
        await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(model.Recoveries);
        Assert.Empty(model.Corrections);
        Assert.Empty(model.Events);
        Assert.False(model.IsBusy);
    }

    private sealed class Governance : IProductionOutboxGovernanceQuery
    {
        private readonly Func<Guid?, Task<ProductionOutboxGovernanceSnapshot>> _read;
        internal Governance(Func<Guid?, Task<ProductionOutboxGovernanceSnapshot>> read) => _read = read;
        public async ValueTask<ProductionOutboxGovernanceSnapshot> ReadAsync(Guid? deliveryId = null,
            CancellationToken cancellationToken = default) => await _read(deliveryId);
    }

    private static ProductionOutboxViewModel OperationsModel(OutboxPendingItem row, Recovery recovery,
        Sessions? sessions, StepUp stepUp) => new(new Query(_ => Task.FromResult(Page(row))),
        recovery, null, sessions, stepUp, new InlineUiDispatcher());

    private sealed class Recovery : IProductionOutboxRecoveryService
    {
        private readonly Func<RecoverOutboxDeliveryCommand, Task>? _beforeRecovery;
        internal Recovery(Func<RecoverOutboxDeliveryCommand, Task>? beforeRecovery = null) => _beforeRecovery = beforeRecovery;
        internal RuntimeCommand? Last { get; private set; }
        internal CancellationToken ObservedToken { get; private set; }
        public async ValueTask<ProductionOutboxRecoveryResult> RecoverAsync(RecoverOutboxDeliveryCommand command,
            CancellationToken cancellationToken = default)
        {
            Last = command;
            ObservedToken = cancellationToken;
            if (_beforeRecovery is not null) await _beforeRecovery(command);
            return new ProductionOutboxRecoveryResult(
                new(command.CorrelationId, CommandDisposition.Accepted, "Persisted", AuditPersistence.Persisted),
                Guid.NewGuid(), command.DeliveryId, command.RequestedAttempts, command.RequestedAttempts, command.Reason);
        }
        public ValueTask<ProductionOutboxCorrectionResult> CreateCorrectiveDeliveryAsync(
            CreateCorrectiveOutboxDeliveryCommand command, CancellationToken cancellationToken = default)
        {
            Last = command;
            return ValueTask.FromResult(new ProductionOutboxCorrectionResult(
                new(command.CorrelationId, CommandDisposition.Accepted, "Persisted", AuditPersistence.Persisted),
                Guid.NewGuid(), command.SourceDeliveryId, Hash, Hash));
        }
    }
    private sealed class StepUp : IStepUpAuthentication
    {
        private readonly Func<StepUpRequest, Task<StepUpResult>>? _response;
        internal StepUp(Func<StepUpRequest, Task<StepUpResult>>? response = null) => _response = response;
        internal Guid Grant { get; } = Guid.NewGuid();
        internal StepUpBinding? Binding { get; private set; }
        internal int CallCount { get; private set; }
        public async ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            Binding = request.Binding;
            CallCount++;
            return _response is null ? new(true, "Authenticated", Grant) : await _response(request);
        }
    }
    private sealed class Sessions : IInteractiveSessionService
    {
        public InteractiveSession Current { get; private set; } = new(
            InteractiveSessionState.Authenticated, Guid.NewGuid().ToString("D"), Guid.NewGuid());
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;
        internal void Lock()
        {
            Current = new(InteractiveSessionState.Locked, null, null);
            Changed?.Invoke(this, new(Current));
        }
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
