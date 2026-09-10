using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime.Storage;

/// <summary>Fresh actor facts captured by the policy authorization evaluator.</summary>
internal sealed record TraceStoragePolicyVerifiedActor
{
    internal TraceStoragePolicyVerifiedActor(Guid principalId, Guid sessionId,
        long authorizationRevision, DateTimeOffset verifiedAtUtc, RecipeContractReference authorizationPolicy)
    {
        if (principalId == Guid.Empty || sessionId == Guid.Empty || authorizationRevision < 0 ||
            verifiedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("TraceStoragePolicyVerifiedActorInvalid");
        PrincipalId = principalId;
        SessionId = sessionId;
        AuthorizationRevision = authorizationRevision;
        VerifiedAtUtc = verifiedAtUtc;
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
    }

    internal Guid PrincipalId { get; }
    internal Guid SessionId { get; }
    internal long AuthorizationRevision { get; }
    internal DateTimeOffset VerifiedAtUtc { get; }
    internal RecipeContractReference AuthorizationPolicy { get; }
}

/// <summary>Fresh identity decision returned by the authorization boundary.</summary>
internal sealed record TraceStoragePolicyEvaluation
{
    internal TraceStoragePolicyEvaluation(TraceStoragePolicyResult result,
        IReadOnlyList<IdentityAuditEvent> events,
        IReadOnlyList<CommandAuditFact>? commandFacts,
        IIdentityTransactionGuard? guard,
        TraceStoragePolicyVerifiedActor? verifiedActor)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Events = events ?? throw new ArgumentNullException(nameof(events));
        CommandFacts = commandFacts;
        Guard = guard;
        VerifiedActor = verifiedActor;
    }

    internal TraceStoragePolicyResult Result { get; }
    internal IReadOnlyList<IdentityAuditEvent> Events { get; }
    internal IReadOnlyList<CommandAuditFact>? CommandFacts { get; }
    internal IIdentityTransactionGuard? Guard { get; }
    internal TraceStoragePolicyVerifiedActor? VerifiedActor { get; }
}

/// <summary>
/// One queued policy publication.  The command and evaluator are retained
/// until the writer has re-read the signed identity and policy ledger inside
/// its own transaction; no public caller can use this type to bypass that
/// second check.
/// </summary>
internal sealed class TraceStoragePolicyWork
{
    internal TraceStoragePolicyWork(PublishTraceStoragePolicyCommand command,
        Func<IdentityAuthorityState, bool, TraceStoragePolicyEvaluation> evaluate,
        CancellationToken cancellationToken)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
        CancellationToken = cancellationToken;
    }

    internal PublishTraceStoragePolicyCommand Command { get; }
    internal Func<IdentityAuthorityState, bool, TraceStoragePolicyEvaluation> Evaluate { get; }
    internal CancellationToken CancellationToken { get; }
    internal TraceStoragePolicyResult? Result { get; set; }
}

/// <summary>Immutable row projection used by the writer and full verifier.</summary>
internal sealed record TraceStoragePolicyRow(long Position, string EventKind,
    long PolicyVersion, string PolicyHash, string DeploymentScopeHash,
    string PayloadHash, string BindingHash, DateTimeOffset RecordedAtUtc,
    long CentralSequence, string CentralHash);

internal enum TraceStoragePolicyEventKind
{
    Published = 1
}
