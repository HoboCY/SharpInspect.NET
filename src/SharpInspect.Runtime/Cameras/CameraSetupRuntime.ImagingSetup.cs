using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Non-production imaging setup declarations. This path only records an authorized
/// declaration against the current durable camera binding; it never publishes a
/// calibration or Ready state.
/// </summary>
internal sealed partial class CameraSetupRuntime
{
    public async ValueTask<ImagingSetupQueryResult> GetImagingSetupAsync(string logicalCameraRole,
        CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!ValidLogicalRole(logicalCameraRole))
            return new(false, "ImagingSetupLogicalRoleInvalid");
        if (_imagingPersistence is null)
            return new(false, "ImagingSetupUnavailable");
        var authorization = await AuthorizeAsync(invocation, readOnly: true, Guid.Empty,
            logicalCameraRole, AuditedCommandKind.Unsupported, cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized)
            return new(false, authorization.ReasonCode);
        try
        {
            var state = await _imagingPersistence.ReadAsync(logicalCameraRole, cancellationToken)
                .ConfigureAwait(false);
            return state.Current is { } current
                ? new ImagingSetupQueryResult(true, "ImagingSetupAvailable", current)
                : new ImagingSetupQueryResult(false, "ImagingSetupUnconfigured");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, SafeReason(ex.Message)); }
    }

    public ValueTask<ImagingSetupChangeResult> DeclareImagingSetupAsync(
        ImagingSetupChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ValueTask<ImagingSetupChangeResult>(RunDeclareImagingSetupAsync(request,
            cancellationToken));
    }

    public async ValueTask<ImagingSetupHistoryResult> QueryImagingSetupHistoryAsync(
        string logicalCameraRole, CommandInvocation invocation, long afterPosition = 0,
        long? throughPosition = null, int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!ValidLogicalRole(logicalCameraRole))
            return new(false, "ImagingSetupLogicalRoleInvalid", Array.Empty<ImagingSetupRevision>(), 0, null);
        if (afterPosition < 0 || throughPosition is < 0 || pageSize is < 1 or > 256)
            return new(false, "ImagingSetupHistoryCursorInvalid", Array.Empty<ImagingSetupRevision>(), 0, null);
        if (_imagingPersistence is null)
            return new(false, "ImagingSetupUnavailable", Array.Empty<ImagingSetupRevision>(), 0, null);
        var authorization = await AuthorizeAsync(invocation, readOnly: true, Guid.Empty,
            logicalCameraRole, AuditedCommandKind.Unsupported, cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized)
            return new(false, authorization.ReasonCode, Array.Empty<ImagingSetupRevision>(), 0, null);
        try
        {
            return await _imagingPersistence.QueryHistoryAsync(logicalCameraRole, afterPosition,
                throughPosition, pageSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, SafeReason(ex.Message), Array.Empty<ImagingSetupRevision>(), 0, null); }
    }

    private async Task<ImagingSetupChangeResult> RunDeclareImagingSetupAsync(
        ImagingSetupChangeRequest request, CancellationToken cancellationToken)
    {
        if (_imagingPersistence is null)
            return FailedImaging("ImagingSetupUnavailable");
        if (!ValidLogicalRole(request.LogicalCameraRole) ||
            request.Invocation is null || request.OperationId == Guid.Empty)
            return FailedImaging("ImagingSetupRequestInvalid");

        var authorization = await AuthorizeAsync(request.Invocation, readOnly: false,
            request.OperationId, request.AuthorizationTarget,
            AuditedCommandKind.DeclareImagingSetup, cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized)
            return FailedImaging(authorization.ReasonCode);

        using var linked = CreateOperationToken(cancellationToken);
        var drain = RegisterInFlight();
        var gateAcquired = false;
        try
        {
            gateAcquired = await WaitForOperationGateAsync(cancellationToken).ConfigureAwait(false);
            if (!gateAcquired)
            {
                authorization.Reservation?.Dispose();
                return FailedImaging("CameraSetupBusy");
            }
            var barrier = await CheckNetworkBarrierAsync(linked.Token).ConfigureAwait(false);
            if (barrier is not null)
            {
                authorization.Reservation?.Dispose();
                return FailedImaging(barrier);
            }
            var persisted = await LoadPersistedAsync(request.LogicalCameraRole, linked.Token)
                .ConfigureAwait(false);
            if (!persisted.Succeeded)
            {
                authorization.Reservation?.Dispose();
                return FailedImaging(persisted.ReasonCode);
            }
            if (persisted.Pending)
            {
                authorization.Reservation?.Dispose();
                return FailedImaging("CameraSetupOperationPending");
            }
            var precondition = CheckMutationPreconditions(request.LogicalCameraRole,
                request.ExpectedBindingRevision, request.ExpectedBindingRevisionHash,
                allowUnbound: false);
            if (precondition is not null)
            {
                authorization.Reservation?.Dispose();
                return FailedImaging(precondition);
            }

            var station = _readStation();
            var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), request.OperationId,
                station.RuntimeEpoch, DateTimeOffset.UtcNow,
                AuditedCommandKind.DeclareImagingSetup, request.Invocation.Source,
                request.Invocation.PrincipalId, request.Invocation.SessionId,
                request.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                CommandDisposition.Accepted, "ImagingSetupRevisionPersisted",
                authorization.PrincipalId.ToString("D"));
            var write = await _imagingPersistence.AppendAsync(
                new ImagingSetupPersistenceRequest(fact, request, authorization),
                new StoreDeadline(_audit?.CommitTimeout ?? _options.OperationTimeout), linked.Token)
                .ConfigureAwait(false);
            if (!write.Committed)
            {
                authorization.Reservation?.Dispose();
                return new ImagingSetupChangeResult(false, SafeReason(write.ReasonCode),
                    write.Fact is null ? AuditPersistence.Unavailable : AuditPersistence.Persisted);
            }

            var state = await _imagingPersistence.ReadAsync(request.LogicalCameraRole,
                linked.Token).ConfigureAwait(false);
            var revision = state.Revisions.SingleOrDefault(item => item.OperationId == request.OperationId);
            authorization.Reservation?.Dispose();
            return revision is null
                ? new ImagingSetupChangeResult(false, "ImagingSetupReadBackMissing", AuditPersistence.Unavailable)
                : new ImagingSetupChangeResult(true, "ImagingSetupPersisted", AuditPersistence.Persisted,
                    revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            authorization.Reservation?.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            authorization.Reservation?.Dispose();
            return FailedImaging("ImagingSetupCommitDeadlineExceeded", AuditPersistence.Unavailable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            authorization.Reservation?.Dispose();
            return FailedImaging(SafeReason(ex.Message), AuditPersistence.Unavailable);
        }
        finally
        {
            if (gateAcquired) _operationGate.Release();
            CompleteInFlight(drain);
        }
    }

    private static ImagingSetupChangeResult FailedImaging(string reason,
        AuditPersistence audit = AuditPersistence.NotAttempted) =>
        new(false, SafeImagingReason(reason), audit);

    private static string SafeImagingReason(string? reason) => reason is { Length: > 0 and <= 128 } &&
        reason.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            ? reason : "ImagingSetupUnavailable";
}
