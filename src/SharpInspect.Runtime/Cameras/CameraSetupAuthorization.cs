using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Internal boundary between camera setup orchestration and the station's
/// authoritative identity service.  A camera coordinator must not inspect or
/// duplicate the identity grant table.  The identity service returns a
/// reservation which is consumed only after the corresponding audit terminal
/// fact has been durably accepted.
/// </summary>
internal interface ICameraSetupAuthorizer
{
    ValueTask<CameraSetupAuthorization> AuthorizeCameraSetupAsync(
        CommandInvocation invocation,
        bool readOnly,
        Guid operationId,
        string targetId,
        AuditedCommandKind commandKind,
        CancellationToken cancellationToken = default);
}

internal sealed class CameraSetupAuthorization
{
    internal CameraSetupAuthorization(bool authorized, string reasonCode, Guid principalId,
        Guid sessionId, long authorizationRevision,
        CameraSetupAuthorizationReservation? reservation)
    {
        Authorized = authorized;
        ReasonCode = NormalizeReason(reasonCode);
        PrincipalId = principalId;
        SessionId = sessionId;
        AuthorizationRevision = authorizationRevision;
        Reservation = reservation;
    }

    internal bool Authorized { get; }
    internal string ReasonCode { get; }
    internal Guid PrincipalId { get; }
    internal Guid SessionId { get; }
    internal long AuthorizationRevision { get; }
    internal CameraSetupAuthorizationReservation? Reservation { get; }

    internal static CameraSetupAuthorization Denied(string reasonCode) =>
        new(false, reasonCode, Guid.Empty, Guid.Empty, 0, null);

    private static string NormalizeReason(string? reasonCode) =>
        reasonCode is { Length: > 0 and <= 128 } &&
        reasonCode.All(static character => character is >= 'A' and <= 'Z' ||
            character is >= 'a' and <= 'z' || character is >= '0' and <= '9' ||
            character is '_' or '-')
            ? reasonCode
            : "AuthorizationUnavailable";
}

/// <summary>
/// One exact camera operation reservation.  The owning identity service supplies
/// the state transitions; this object only guarantees that commit or rollback
/// is invoked once, even when cancellation and cleanup race.
/// </summary>
internal sealed class CameraSetupAuthorizationReservation : IDisposable
{
    private readonly Action _commit;
    private readonly Action _rollback;
    private int _state;

    private CameraSetupAuthorizationReservation(Action commit, Action rollback)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
    }

    internal static CameraSetupAuthorizationReservation Create(Action commit, Action rollback) =>
        new(commit, rollback);

    internal void Commit()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        try { _commit(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
        try { _rollback(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
}
