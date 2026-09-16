using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public sealed partial class StorageRetentionViewModel
{
    /// <summary>Raised whenever the panel must discard the password box (session change, completed action, deactivation).</summary>
    internal event EventHandler? SensitiveInputsInvalidated;

    public bool OperationsConfigured => _retention is not null && _sessions is not null && _stepUp is not null;

    public Task<RuntimeCommandOutcome?> PlaceHoldWithStepUpAsync(string password) =>
        SubmitChangeAsync(EvidenceRetentionChange.PlaceHold, password);
    public Task<RuntimeCommandOutcome?> ReleaseHoldWithStepUpAsync(string password) =>
        SubmitChangeAsync(EvidenceRetentionChange.ReleaseHold, password);
    public Task<RuntimeCommandOutcome?> ExtendWithStepUpAsync(string password) =>
        SubmitChangeAsync(EvidenceRetentionChange.Extend, password);

    // 读取结果从不授权：每次处置都必须重新通过 Runtime 的权限检查与同命令绑定的新鲜 Step-Up。
    private async Task<RuntimeCommandOutcome?> SubmitChangeAsync(EvidenceRetentionChange change, string password)
    {
        RuntimeCommand? command = null;
        InteractiveSession? session = null;
        CancellationTokenSource? cancellation = null;
        long generation = 0;
        RuntimeCommandOutcome? outcome = null;
        var resultMessage = "处置结果暂未确认；请刷新后读取最新保留账本。";
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!CanChange(change)) return;
                session = _sessions!.Current;
                var subject = _selected!;
                var correlation = Guid.NewGuid();
                var invocation = Invocation(session);
                Guid? holdId = null;
                DateTimeOffset? extendedUntil = null;
                if (change == EvidenceRetentionChange.PlaceHold) holdId = Guid.NewGuid();
                else if (change == EvidenceRetentionChange.ReleaseHold) holdId = _selectedHoldId;
                else if (TryParseExtension(out var until)) extendedUntil = until;
                else return;
                var request = new ChangeEvidenceRetentionCommand(correlation, invocation, subject.Obligation.Owner,
                    subject.Revision, change, holdId, extendedUntil, _reasonText);
                command = request;
                generation = ++_generation;
                cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _cancellation = cancellation;
                _busy = true;
                _submittedChange = false;
                _status = "正在核验保留处置权限与本次身份…";
                SensitiveInputsInvalidated?.Invoke(this, EventArgs.Empty);
                Notify();
            });
            if (command is null || cancellation is null || session is null) return null;
            var access = await Task.Run(async () => await _retention!.GetAccessAsync(command.Invocation, cancellation.Token)
                .ConfigureAwait(false)).WaitAsync(cancellation.Token).ConfigureAwait(false);
            if (!access.Allowed)
            {
                resultMessage = "当前会话没有删除证据的保留处置权限，操作未提交。";
                return null;
            }
            var retentionCommand = (ChangeEvidenceRetentionCommand)command;
            var binding = new StepUpBinding(Permission.DeleteEvidence, retentionCommand.CorrelationId,
                retentionCommand.AuthorizationTarget, AuditedCommandKind.ChangeEvidenceRetention);
            var authentication = await Task.Run(async () => await _stepUp!.ReauthenticateAsync(
                new StepUpRequest(Guid.NewGuid(), retentionCommand.Invocation, binding, password ?? string.Empty),
                cancellation.Token).ConfigureAwait(false)).WaitAsync(cancellation.Token).ConfigureAwait(false);
            password = string.Empty;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!authentication.Succeeded || authentication.GrantId is not { } grant || grant == Guid.Empty)
            {
                resultMessage = "身份验证未通过，保留处置未提交。";
                return null;
            }
            var maySubmit = false;
            await _dispatcher.InvokeAsync(() =>
            {
                maySubmit = !_disposed && generation == _generation && SameSession(session, _sessions!.Current);
                if (maySubmit)
                {
                    _submittedChange = true;
                    _status = "保留处置请求已发出，正在等待持久结果…";
                    Notify();
                }
            });
            if (!maySubmit) return null;
            var granted = retentionCommand with { Invocation = Invocation(session, grant) };
            var result = await Task.Run(async () => await _retention!.ChangeAsync(granted, CancellationToken.None)
                .ConfigureAwait(false)).WaitAsync(cancellation.Token).ConfigureAwait(false);
            outcome = result.Outcome;
            resultMessage = result.Succeeded
                ? $"{ChangeLabel(change)}已记录；请以最新读取确认主体的修订与保留状态。"
                : outcome.Disposition == CommandDisposition.Accepted
                    ? "保留处置结果尚未确认；请刷新并核对最新记录。"
                    : "本次保留处置未被接受；请读取最新记录后重试。";
            return outcome;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
        finally
        {
            password = string.Empty;
            await _dispatcher.InvokeAsync(() =>
            {
                if (cancellation is null) return;
                if (ReferenceEquals(_cancellation, cancellation)) { _cancellation = null; _busy = false; }
                if (!_disposed && SameSession(session, _sessions?.Current))
                {
                    _status = generation == _generation
                        ? resultMessage
                        : _submittedChange
                            ? "保留处置请求已发出，结果尚未确认；请刷新并读取最新账本。"
                            : "页面输入已变化，本次等待已结束；请重新查询或提交。";
                    // 已提交或已拒绝的处置都会改变已观察的修订；先丢弃旧选择与旧行，再读取最新账本。
                    ClearRows();
                    ClearOperationInputs();
                    _policyText = _retention is null ? _policyText : "保留记录已变化；请读取最新策略与统计。";
                }
                Notify();
            });
            cancellation?.Dispose();
            if (outcome is not null && !_disposed && generation == _generation &&
                SameSession(session, _sessions?.Current))
            {
                try { await RefreshAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
    }

    private bool CanChange(EvidenceRetentionChange change)
    {
        if (_disposed || _busy || !OperationsConfigured || _selected is not { } subject) return false;
        if (!UsableSession(_sessions!.Current) || string.IsNullOrWhiteSpace(_reasonText)) return false;
        if (subject.Disposition is not (EvidenceRetentionDisposition.Retained or EvidenceRetentionDisposition.Held) ||
            subject.DeleteOperationId is not null) return false;
        return change switch
        {
            EvidenceRetentionChange.PlaceHold => true,
            EvidenceRetentionChange.ReleaseHold => _selectedHoldId is { } hold && subject.ActiveHolds.Contains(hold),
            EvidenceRetentionChange.Extend => TryParseExtension(out var until) && until > subject.EffectiveUntilUtc,
            _ => false
        };
    }

    private bool TryParseExtension(out DateTimeOffset until)
    {
        until = default;
        if (!DateTimeOffset.TryParse(_extendedUntilText, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var parsed) || parsed == default || parsed.Offset != TimeSpan.Zero) return false;
        until = parsed;
        return true;
    }

    private async void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        try { await _dispatcher.InvokeAsync(() => { if (!_disposed) Deactivate(); }); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private static bool UsableSession(InteractiveSession? session) => session is
        { State: InteractiveSessionState.Authenticated, SessionId: { } id } && id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(session.PrincipalId);

    private static bool SameSession(InteractiveSession? left, InteractiveSession? right) =>
        UsableSession(left) && UsableSession(right) && left!.SessionId == right!.SessionId &&
        left.PrincipalId == right.PrincipalId;

    private static CommandInvocation Invocation(InteractiveSession session, Guid? grant = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grant);

    private static string ChangeLabel(EvidenceRetentionChange change) => change switch
    {
        EvidenceRetentionChange.PlaceHold => "登记人工保留",
        EvidenceRetentionChange.ReleaseHold => "释放人工保留",
        EvidenceRetentionChange.Extend => "延长保留期",
        _ => "保留处置"
    };
}
