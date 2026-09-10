namespace SharpInspect.Abstractions;

/// <summary>Immutable imported origin. Source lifecycle and signatures are lineage, never local release authority.</summary>
public sealed record RecipeImportProvenance(Guid DraftId, string FirstRevisionContentHash,
    string SourceRecipeIdentity, string SourceRevision, string SourceLifecycle, string SourceSnapshotHash,
    string PackageContentHash, string PackageBytesHash, string SignerKeyId, string SignerFingerprint,
    string SignatureScheme, string SignatureBase64, long TrustStoreVersion, string TrustStoreContentHash,
    Guid OperationId, Guid PrincipalId, Guid SessionId, long AuthorizationRevision, DateTimeOffset RecordedAtUtc,
    string? SourceStationId, string SourceDescriptorContentHash);

public enum RecipeTransferEventKind : byte
{
    TrustStoreReplaced = 1, SigningKeyCreated = 2, SigningKeyRetired = 3, Exported = 4, Imported = 5
}

public sealed record RecipeTransferHistoryRecord(long Position, RecipeTransferEventKind Kind, Guid OperationId,
    Guid PrincipalId, Guid SessionId, string SubjectHash, string ContentHash, DateTimeOffset RecordedAtUtc);
public sealed record RecipeTransferFilter(long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 20);
public sealed record RecipeTransferHistoryPage(bool Available, string ReasonCode,
    IReadOnlyList<RecipeTransferHistoryRecord> Records, long ThroughPosition, long? NextAfterPosition);
public sealed record RecipeTrustReadResult(bool Available, string ReasonCode, RecipeTrustStoreVersion? Trust = null);
public sealed record RecipeSigningKeysReadResult(bool Available, string ReasonCode, IReadOnlyList<RecipeSigningKeyRecord> Keys);
public sealed record RecipeImportReadResult(bool Available, string ReasonCode, RecipeImportProvenance? Provenance = null);
public sealed record RecipeTransferAccess(bool Allowed, string ReasonCode, bool RequiresStepUp);

/// <summary>Only returned after the exact package and source have been durably audited.</summary>
public sealed class RecipeTransferExport
{
    private readonly byte[] _bytes;
    internal RecipeTransferExport(byte[] bytes, string packageContentHash, string packageBytesHash)
    {
        _bytes = (byte[])bytes.Clone(); PackageContentHash = packageContentHash; PackageBytesHash = packageBytesHash;
    }
    public int Length => _bytes.Length;
    public string PackageContentHash { get; }
    public string PackageBytesHash { get; }
    public byte[] ToArray() => (byte[])_bytes.Clone();
}

public sealed record RecipeTransferResult(RuntimeCommandOutcome Outcome, RecipeTrustStoreVersion? Trust = null,
    RecipeSigningKeyRecord? SigningKey = null, RecipeDraftRevision? Draft = null,
    RecipeImportProvenance? Import = null, RecipeTransferExport? Export = null)
{
    public bool Succeeded => Outcome.Disposition == CommandDisposition.Accepted && Outcome.Audit == AuditPersistence.Persisted;
}

/// <summary>Independent bounded read-only governance and origin history.</summary>
public interface IRecipeTransferHistoryQuery
{
    ValueTask<RecipeTrustReadResult> ReadTrustAsync(long? version = null, CancellationToken cancellationToken = default);
    ValueTask<RecipeSigningKeysReadResult> ReadSigningKeysAsync(CancellationToken cancellationToken = default);
    ValueTask<RecipeImportReadResult> ReadImportAsync(Guid draftId, CancellationToken cancellationToken = default);
    ValueTask<RecipeTransferHistoryPage> QueryAsync(RecipeTransferFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>Governed data interchange. It cannot release, activate, arm or install executable dependencies.</summary>
public interface IRecipeTransferService : IRecipeTransferHistoryQuery
{
    ValueTask<RecipeTransferAccess> GetAccessAsync(CommandInvocation invocation, Permission permission,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeTransferResult> ReplaceTrustAsync(ReplaceRecipeTrustStoreCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeTransferResult> CreateSigningKeyAsync(CreateRecipeSigningKeyCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeTransferResult> RetireSigningKeyAsync(RetireRecipeSigningKeyCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeTransferResult> ExportAsync(ExportRecipeTransferCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeTransferResult> ImportAsync(ImportRecipeTransferCommand command, ReadOnlyMemory<byte> packageBytes,
        CancellationToken cancellationToken = default);
}
