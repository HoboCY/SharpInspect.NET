namespace SharpInspect.Runtime.Identity;

/// <summary>
/// One dedicated station-maintenance elevation. Revocation never calls Runtime, SQLite,
/// cancellation callbacks or a sink. <see cref="TryBeginCommit"/> linearizes the Prepared
/// boundary: a revocation that loses that race is recorded after the activation commit and
/// can never retroactively turn an already prepared handoff into an unauthorized one.
/// </summary>
internal sealed class StationMaintenanceAuthority
{
    private const int Active = 0;
    private const int Revoked = 1;
    private const int Committing = 2;
    private const int Committed = 3;

    private int _state;

    internal StationMaintenanceAuthority(Guid principalId, Guid sessionId, Guid credentialId, long revision,
        Guid operationId, Guid runtimeEpoch, int processId, DateTimeOffset processStartedUtc)
    {
        PrincipalId = principalId;
        SessionId = sessionId;
        CredentialId = credentialId;
        Revision = revision;
        OperationId = operationId;
        RuntimeEpoch = runtimeEpoch;
        ProcessId = processId;
        ProcessStartedUtc = processStartedUtc;
    }

    internal Guid PrincipalId { get; }
    internal Guid SessionId { get; }
    internal Guid CredentialId { get; }
    internal long Revision { get; }
    internal Guid OperationId { get; }
    internal Guid RuntimeEpoch { get; }
    internal int ProcessId { get; }
    internal DateTimeOffset ProcessStartedUtc { get; }
    /// <summary>The live identity/session-lease recheck the seal runs inside its serialized path.</summary>
    internal Func<CancellationToken, ValueTask<bool>>? LiveCheck { get; set; }

    internal bool Valid => Volatile.Read(ref _state) == Active;

    internal bool TryBeginCommit() => Interlocked.CompareExchange(ref _state, Committing, Active) == Active;
    internal void CompleteCommit() => Interlocked.CompareExchange(ref _state, Committed, Committing);

    internal void Revoke()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state == Revoked) return;
            if (Interlocked.CompareExchange(ref _state, Revoked, state) == state) return;
        }
    }
}
