using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>Revocation never calls Runtime, SQLite, cancellation callbacks or a sink.</summary>
internal sealed class DiagnosticSupportAuthority
{
    internal DiagnosticSupportAuthority(Guid principalId, Guid sessionId, Guid credentialId, long revision)
    { PrincipalId = principalId; SessionId = sessionId; CredentialId = credentialId; Revision = revision; }
    internal Guid PrincipalId { get; }
    internal Guid SessionId { get; }
    internal Guid CredentialId { get; }
    internal long Revision { get; }
    private int _revoked;
    private DiagnosticCaptureLease? _capture;
    internal bool Valid => Volatile.Read(ref _revoked) == 0;
    internal void Bind(DiagnosticCaptureLease capture)
    {
        if (Interlocked.CompareExchange(ref _capture, capture, null) is not null)
            throw new InvalidOperationException("DiagnosticAuthorityAlreadyBound");
        if (!Valid) capture.Revoke(DiagnosticCaptureEnd.AuthorityLost);
    }
    internal void Revoke()
    {
        Interlocked.Exchange(ref _revoked, 1);
        Volatile.Read(ref _capture)?.Revoke(DiagnosticCaptureEnd.AuthorityLost);
    }
}

internal enum DiagnosticCaptureEnd { None, EventLimit, TimeLimit, AuthorityLost, Stopped, Fault }

/// <summary>
/// One process-local elevation. The successful CAS linearizes each event admission;
/// revocation admits no new attempts, while admitted producers and physical writes stay charged.
/// </summary>
internal sealed class DiagnosticCaptureLease
{
    private sealed record State(int Attempts, DiagnosticCaptureEnd End);
    private State _state = new(0, DiagnosticCaptureEnd.None);
    private readonly Func<long> _timestamp;
    private readonly long _started;
    private long _physicalReferences;
    internal DiagnosticCaptureLease(Guid id, DiagnosticCaptureProfile profile, DiagnosticSupportAuthority authority,
        Func<long>? timestamp = null, long? startedTimestamp = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("DiagnosticCaptureIdentityInvalid");
        Id = id; Profile = profile; Authority = authority;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp; _started = startedTimestamp ?? _timestamp();
        authority.Bind(this);
    }
    internal Guid Id { get; }
    internal DiagnosticCaptureProfile Profile { get; }
    internal DiagnosticSupportAuthority Authority { get; }
    internal int Attempts => Volatile.Read(ref _state).Attempts;
    internal DiagnosticCaptureEnd End { get { Expire(); return Volatile.Read(ref _state).End; } }
    internal bool Drained => End != DiagnosticCaptureEnd.None && Interlocked.Read(ref _physicalReferences) == 0;
    internal long PhysicalReferences => Interlocked.Read(ref _physicalReferences);
    internal bool Matches(DiagnosticEventContract contract) => contract.Level >= Profile.MinimumLevel &&
        Profile.Components.Contains(contract.Component, StringComparer.Ordinal);
    internal void AddReference() => Interlocked.Increment(ref _physicalReferences);
    internal void ReleaseReference() => Interlocked.Decrement(ref _physicalReferences);

    internal bool TryEnter()
    {
        // The producer reference exists before terminal/drained can be observed.
        AddReference();
        while (true)
        {
            Expire();
            var previous = Volatile.Read(ref _state);
            if (previous.End != DiagnosticCaptureEnd.None) { ReleaseReference(); return false; }
            var attempts = previous.Attempts + 1;
            var next = new State(attempts, attempts == Profile.MaximumEvents ?
                DiagnosticCaptureEnd.EventLimit : DiagnosticCaptureEnd.None);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, next, previous), previous))
            {
                // A paused producer may resume after its pre-CAS clock sample. Recheck
                // without rejecting the legitimate Nth attempt merely for closing the cap.
                if (!Authority.Valid || (_timestamp() - _started) / (double)Stopwatch.Frequency >= Profile.Duration.TotalSeconds)
                { Expire(); ReleaseReference(); return false; }
                return true;
            }
        }
    }

    internal void Revoke(DiagnosticCaptureEnd reason)
    {
        if (reason == DiagnosticCaptureEnd.None) throw new ArgumentException("DiagnosticCaptureEndRequired");
        while (true)
        {
            var previous = Volatile.Read(ref _state);
            if (previous.End != DiagnosticCaptureEnd.None) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, previous with { End = reason }, previous), previous)) return;
        }
    }
    private void Expire()
    {
        if (!Authority.Valid) Revoke(DiagnosticCaptureEnd.AuthorityLost);
        else if ((_timestamp() - _started) / (double)Stopwatch.Frequency >= Profile.Duration.TotalSeconds)
            Revoke(DiagnosticCaptureEnd.TimeLimit);
    }
}
