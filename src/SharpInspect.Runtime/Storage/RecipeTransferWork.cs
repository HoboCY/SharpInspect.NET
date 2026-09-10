using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime.Storage;

/// <summary>Fresh actor facts captured by the authorization evaluator.</summary>
internal sealed record RecipeTransferVerifiedActor
{
    internal RecipeTransferVerifiedActor(Guid principalId, Guid sessionId,
        long authorizationRevision, DateTimeOffset verifiedAtUtc)
    {
        if (principalId == Guid.Empty || sessionId == Guid.Empty || authorizationRevision < 0 ||
            verifiedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("RecipeTransferVerifiedActorInvalid");
        PrincipalId = principalId;
        SessionId = sessionId;
        AuthorizationRevision = authorizationRevision;
        VerifiedAtUtc = verifiedAtUtc;
    }

    internal Guid PrincipalId { get; }
    internal Guid SessionId { get; }
    internal long AuthorizationRevision { get; }
    internal DateTimeOffset VerifiedAtUtc { get; }
}

/// <summary>
/// The immutable inputs which may cross the authorization/writer boundary.
/// Public callers cannot construct this type; the service creates it only
/// after package/source validation.  The writer still re-reads all durable
/// trust, key and draft heads before committing.
/// </summary>
internal sealed class RecipeTransferPreparedOperation
{
    internal RecipeTransferPreparedOperation(RecipeTransferCommand command,
        byte[]? packageBytes = null, RecipeTransferPackage? package = null,
        RecipeDraftDocument? draftDocument = null, RecipeTrustedSigner? signer = null,
        byte[]? protectedPrivateKey = null, RecipeTransferSource? frozenSource = null,
        string? frozenSourceContentHash = null, string? frozenKeyFingerprint = null,
        long? frozenTrustStoreVersion = null, Guid? newDraftId = null)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        if (packageBytes is { Length: > RecipeTransferPackageLimits.MaximumPackageBytes })
            throw new ArgumentOutOfRangeException(nameof(packageBytes), "RecipeTransferPackageTooLarge");
        PackageBytes = packageBytes is null ? null : (byte[])packageBytes.Clone();
        Package = package;
        DraftDocument = draftDocument;
        Signer = signer;
        ProtectedPrivateKey = protectedPrivateKey is null ? null : (byte[])protectedPrivateKey.Clone();
        FrozenSource = frozenSource;
        FrozenSourceContentHash = frozenSourceContentHash;
        FrozenKeyFingerprint = frozenKeyFingerprint;
        FrozenTrustStoreVersion = frozenTrustStoreVersion;
        if (newDraftId is Guid draftId && draftId == Guid.Empty)
            throw new ArgumentException("RecipeTransferDraftIdentityInvalid", nameof(newDraftId));
        NewDraftId = newDraftId;
    }

    internal RecipeTransferCommand Command { get; }
    internal byte[]? PackageBytes { get; }
    internal RecipeTransferPackage? Package { get; }
    internal RecipeDraftDocument? DraftDocument { get; }
    internal RecipeTrustedSigner? Signer { get; }
    internal byte[]? ProtectedPrivateKey { get; }
    internal RecipeTransferSource? FrozenSource { get; }
    internal string? FrozenSourceContentHash { get; }
    internal string? FrozenKeyFingerprint { get; }
    internal long? FrozenTrustStoreVersion { get; }
    internal Guid? NewDraftId { get; }
}

/// <summary>Only the verified durable projection is passed to the evaluator.</summary>
internal sealed record RecipeTransferStoreState(
    RecipeTrustStoreVersion? Trust,
    IReadOnlyList<RecipeSigningKeyRecord> SigningKeys,
    IReadOnlyList<RecipeImportProvenance> Imports,
    RecipeTransferHistoryRecord? LastEvent,
    long LastPosition,
    RecipeDraftHead? SourceDraft = null)
{
    internal RecipeTransferStoreState(RecipeTrustStoreVersion? trust,
        IEnumerable<RecipeSigningKeyRecord> signingKeys,
        IEnumerable<RecipeImportProvenance> imports,
        RecipeTransferHistoryRecord? lastEvent, long lastPosition,
        RecipeDraftHead? sourceDraft = null)
        : this(trust,
            new ReadOnlyCollection<RecipeSigningKeyRecord>(signingKeys?.ToArray() ??
                throw new ArgumentNullException(nameof(signingKeys))),
            new ReadOnlyCollection<RecipeImportProvenance>(imports?.ToArray() ??
                throw new ArgumentNullException(nameof(imports))), lastEvent, lastPosition, sourceDraft)
    { }
}

/// <summary>Result of the fresh identity/trust evaluation inside BEGIN IMMEDIATE.</summary>
internal sealed record RecipeTransferEvaluation
{
    internal RecipeTransferEvaluation(RecipeTransferResult result,
        IReadOnlyList<IdentityAuditEvent> events,
        IReadOnlyList<CommandAuditFact>? commandFacts,
        IIdentityTransactionGuard? guard,
        RecipeTransferVerifiedActor? verifiedActor)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Events = events ?? throw new ArgumentNullException(nameof(events));
        CommandFacts = commandFacts;
        Guard = guard;
        VerifiedActor = verifiedActor;
    }

    internal RecipeTransferResult Result { get; }
    internal IReadOnlyList<IdentityAuditEvent> Events { get; }
    internal IReadOnlyList<CommandAuditFact>? CommandFacts { get; }
    internal IIdentityTransactionGuard? Guard { get; }
    internal RecipeTransferVerifiedActor? VerifiedActor { get; }
}

/// <summary>One queued transfer operation; all mutation is performed by the writer.</summary>
internal sealed class RecipeTransferWork
{
    internal RecipeTransferWork(RecipeTransferCommand command,
        RecipeTransferPreparedOperation prepared,
        Func<IdentityAuthorityState, RecipeTransferStoreState, bool, RecipeTransferEvaluation> evaluate,
        CancellationToken cancellationToken)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
        Evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
        CancellationToken = cancellationToken;
    }

    internal RecipeTransferCommand Command { get; }
    internal RecipeTransferPreparedOperation Prepared { get; }
    internal Func<IdentityAuthorityState, RecipeTransferStoreState, bool, RecipeTransferEvaluation> Evaluate { get; }
    internal CancellationToken CancellationToken { get; }
    internal object? Result { get; set; }
}

internal sealed record RecipeTransferRow(long Position, RecipeTransferEventKind Kind,
    Guid OperationId, Guid PrincipalId, Guid SessionId, string SubjectHash, string ContentHash,
    DateTimeOffset RecordedAtUtc, string? CentralHash = null, long? CentralSequence = null);
