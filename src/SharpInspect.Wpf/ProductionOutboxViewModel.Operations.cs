using System.IO;
using System.Security.Cryptography;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public sealed partial class ProductionOutboxViewModel
{
    private readonly IProductionOutboxRecoveryService? _recovery;
    private readonly IProductionOutboxGovernanceQuery? _governance;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IStepUpAuthentication? _stepUp;
    private string _reasonText = string.Empty;
    private string _attemptsText = "1";
    private string _contentTypeText = "application/json";
    private byte[]? _correctionBytes;
    private string _correctionFileName = string.Empty;
    private string _correctionHash = string.Empty;
    private bool _submittedOperation;
    private bool _requiresFreshPending;
    private IReadOnlyList<ProductionOutboxRecoveryRecord> _recoveries = Array.Empty<ProductionOutboxRecoveryRecord>();
    private IReadOnlyList<ProductionOutboxCorrectionRecord> _corrections = Array.Empty<ProductionOutboxCorrectionRecord>();

    public IReadOnlyList<ProductionOutboxRecoveryRecord> Recoveries => _recoveries;
    public IReadOnlyList<ProductionOutboxCorrectionRecord> Corrections => _corrections;
    public bool OperationsConfigured => _recovery is not null && _sessions is not null && _stepUp is not null;
    public bool CanEditOperations => Available && !_requiresFreshPending && OperationsConfigured && UsableSession(_sessions?.Current) &&
        _selected is { ActiveAttemptId: null } &&
        (_selected.PermanentBlock || !_selected.RetryEligible);
    public bool CanRecover => CanEditOperations && _selected!.Delivery.Payload is not null && !string.IsNullOrWhiteSpace(_reasonText) &&
        int.TryParse(_attemptsText, out var attempts) && attempts is >= 1 and <= 16;
    public bool CanCorrect => CanEditOperations && !string.IsNullOrWhiteSpace(_reasonText) &&
        !string.IsNullOrWhiteSpace(_contentTypeText) && _correctionBytes is { Length: > 0 };
    public string ReasonText { get => _reasonText; set => SetOperationText(ref _reasonText, value, 256); }
    public string AttemptsText { get => _attemptsText; set => SetOperationText(ref _attemptsText, value, 2); }
    public string ContentTypeText { get => _contentTypeText; set => SetOperationText(ref _contentTypeText, value, 128); }
    public string CorrectionPayloadSummary => _correctionBytes is null ? "尚未选择更正报文。" :
        $"{_correctionFileName} · {_correctionBytes.Length} 字节 · SHA-256 {_correctionHash}";
    internal event EventHandler? SensitiveInputsInvalidated;

    private void SetOperationText(ref string field, string? value, int maximum)
    {
        EnsureUi();
        var bounded = (value ?? string.Empty).Trim();
        if (bounded.Length > maximum) bounded = bounded[..maximum];
        if (field == bounded) return;
        field = bounded;
        _generation++;
        DescribeInvalidatedWait();
        _cancellation?.Cancel();
        NotifyState();
    }

    /// <summary>The presentation owns the selected final bytes; callers cannot change a pending submission.</summary>
    public void SetCorrectionPayload(string fileName, ReadOnlySpan<byte> bytes)
    {
        EnsureUi();
        if (!CanEditOperations) throw new InvalidOperationException("OutboxOperationUnavailable");
        if (bytes.Length is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(bytes));
        _generation++;
        DescribeInvalidatedWait();
        _cancellation?.Cancel();
        ClearCorrectionPayload();
        _correctionBytes = bytes.ToArray();
        _correctionFileName = Path.GetFileName(fileName ?? string.Empty);
        if (_correctionFileName.Length > 128) _correctionFileName = _correctionFileName[..128];
        _correctionHash = Convert.ToHexString(SHA256.HashData(_correctionBytes));
        NotifyOperations();
    }

    public Task<RuntimeCommandOutcome?> RecoverWithStepUpAsync(string password) => SubmitOperationAsync(false, password);
    public Task<RuntimeCommandOutcome?> CorrectWithStepUpAsync(string password) => SubmitOperationAsync(true, password);

    private async Task<RuntimeCommandOutcome?> SubmitOperationAsync(bool correction, string password)
    {
        RuntimeCommand? command = null;
        InteractiveSession? session = null;
        CancellationTokenSource? cancellation = null;
        long generation = 0;
        string target = string.Empty;
        RuntimeCommandOutcome? outcome = null;
        var resultMessage = "处置结果暂未确认，请刷新查看持久记录。";
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!(correction ? CanCorrect : CanRecover)) return;
                session = _sessions!.Current;
                var item = _selected!;
                var correlation = Guid.NewGuid();
                var invocation = Invocation(session);
                if (correction)
                {
                    var request = new CreateCorrectiveOutboxDeliveryCommand(correlation, invocation,
                        item.Delivery.DeliveryId, item.Delivery.ContentHash, item.StateRevisionHash,
                        _contentTypeText, _correctionBytes!, _reasonText);
                    command = request;
                    target = request.AuthorizationTarget;
                }
                else
                {
                    var request = new RecoverOutboxDeliveryCommand(correlation, invocation,
                        item.Delivery.DeliveryId, item.Delivery.ContentHash, item.StateRevisionHash,
                        int.Parse(_attemptsText, System.Globalization.CultureInfo.InvariantCulture), _reasonText);
                    command = request;
                    target = request.AuthorizationTarget;
                }
                generation = ++_generation;
                _submittedOperation = false;
                cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _cancellation = cancellation;
                _busy = true;
                _status = "正在验证本次处置的身份与授权…";
                SensitiveInputsInvalidated?.Invoke(this, EventArgs.Empty);
                NotifyState();
            });
            if (command is null || cancellation is null || session is null) return null;
            var binding = new StepUpBinding(correction ? Permission.CreateCorrectiveOutboxDelivery : Permission.RecoverOutboxDelivery,
                command.CorrelationId, target, correction ? AuditedCommandKind.CreateCorrectiveOutboxDelivery : AuditedCommandKind.RecoverOutboxDelivery);
            var authentication = await Task.Run(async () => await _stepUp!.ReauthenticateAsync(
                new StepUpRequest(Guid.NewGuid(), command.Invocation, binding, password ?? string.Empty),
                cancellation.Token).ConfigureAwait(false)).WaitAsync(cancellation.Token).ConfigureAwait(false);
            password = string.Empty;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!authentication.Succeeded || authentication.GrantId is not { } grant || grant == Guid.Empty)
            {
                resultMessage = "身份验证未通过，处置未提交。";
                return null;
            }
            var maySubmit = false;
            await _dispatcher.InvokeAsync(() =>
            {
                maySubmit = !_disposed && generation == _generation && SameSession(session, _sessions!.Current);
                if (maySubmit)
                {
                    _submittedOperation = true;
                    _requiresFreshPending = true;
                    _status = "处置请求已发出，正在等待持久结果…";
                    NotifyState();
                }
            });
            if (!maySubmit) return null;
            var invocation = Invocation(session, grant);
            if (command is CreateCorrectiveOutboxDeliveryCommand corrective)
            {
                var result = await Task.Run(async () => await _recovery!.CreateCorrectiveDeliveryAsync(
                    corrective with { Invocation = invocation }, CancellationToken.None).ConfigureAwait(false))
                    .WaitAsync(cancellation.Token).ConfigureAwait(false);
                outcome = result.Outcome;
                if (outcome.Disposition == CommandDisposition.Accepted)
                    resultMessage = $"更正交付已接纳，交付 ID：{result.DeliveryId:D}。原项保持原状态；请读取历史确认。";
            }
            else
            {
                var result = await Task.Run(async () => await _recovery!.RecoverAsync(
                    ((RecoverOutboxDeliveryCommand)command) with { Invocation = invocation },
                    CancellationToken.None).ConfigureAwait(false)).WaitAsync(cancellation.Token).ConfigureAwait(false);
                outcome = result.Outcome;
                if (outcome.Disposition == CommandDisposition.Accepted)
                    resultMessage = "恢复已接纳，将以原身份和报文继续有界尝试；请刷新查看结果。";
            }
            if (outcome.Disposition != CommandDisposition.Accepted)
                resultMessage = "Runtime 拒绝了本次处置，请刷新并查看命令审计。";
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
                if (!_disposed && generation == _generation && SameSession(session, _sessions?.Current))
                {
                    _status = resultMessage;
                    // A response cannot authorize another operation on the old observed state.
                    _selected = null;
                    _rows.Clear();
                    _next = null;
                    ClearOperationInputs();
                }
                NotifyState();
            });
            cancellation?.Dispose();
        }
    }

    private void ClearCorrectionPayload()
    {
        if (_correctionBytes is not null) CryptographicOperations.ZeroMemory(_correctionBytes);
        _correctionBytes = null;
        _correctionFileName = _correctionHash = string.Empty;
    }
    private void DescribeInvalidatedWait()
    {
        if (!_busy) return;
        _status = _submittedOperation
            ? "处置请求已发出，结果尚未确认；请刷新交付记录并查看命令审计。"
            : "页面输入已变化，本次等待已结束；请重新查询或提交。";
    }
    private void ClearOperationInputs()
    {
        ClearCorrectionPayload();
        _reasonText = string.Empty;
        _attemptsText = "1";
        _contentTypeText = "application/json";
        SensitiveInputsInvalidated?.Invoke(this, EventArgs.Empty);
    }
    private void ClearGovernance()
    {
        _recoveries = Array.Empty<ProductionOutboxRecoveryRecord>();
        _corrections = Array.Empty<ProductionOutboxCorrectionRecord>();
    }
    private void ApplyGovernance(ProductionOutboxGovernanceSnapshot? snapshot, Guid deliveryId)
    {
        if (_governance is null) return;
        if (snapshot is not { Available: true } || snapshot.Recoveries.Count > 256 || snapshot.Corrections.Count > 256 ||
            snapshot.Recoveries.Any(x => x.DeliveryId != deliveryId || x.AuditSequence > snapshot.ThroughAuditSequence) ||
            snapshot.Corrections.Any(x => x.DeliveryId != deliveryId && x.SourceDeliveryId != deliveryId ||
                x.AuditSequence > snapshot.ThroughAuditSequence))
        { _status += " 人工处置历史暂不可用。"; return; }
        _recoveries = snapshot.Recoveries;
        _corrections = snapshot.Corrections;
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
        UsableSession(left) && UsableSession(right) && left!.SessionId == right!.SessionId && left.PrincipalId == right.PrincipalId;
    private static CommandInvocation Invocation(InteractiveSession session, Guid? grant = null) =>
        new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId, grant);
    private void NotifyOperations()
    {
        foreach (var name in new[] { nameof(OperationsConfigured), nameof(CanEditOperations), nameof(CanRecover), nameof(CanCorrect),
                     nameof(ReasonText), nameof(AttemptsText), nameof(ContentTypeText), nameof(CorrectionPayloadSummary),
                     nameof(Recoveries), nameof(Corrections) }) OnPropertyChanged(name);
    }
}
