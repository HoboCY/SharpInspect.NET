using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Bounded in-memory quarantine and governed transfer orchestration. Input paths are never extracted.</summary>
internal sealed class RecipeTransferService : IRecipeTransferService
{
    private readonly ProductionStoreOptions _options;
    private readonly LocalAuthorizationService _authorization;
    private readonly SqliteCommandStore _store;
    private readonly IRecipeDraftHistoryQuery _drafts;
    private readonly IReleasedRecipeQuery? _releases;
    private readonly IRecipeTransferHistoryQuery _history;
    private readonly IReadOnlyList<AlgorithmDescriptor> _descriptors;
    private readonly SqliteRecipeLifecycleQuery? _lifecycle;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly Guid _epoch = Guid.NewGuid();

    internal RecipeTransferService(ProductionStoreOptions options, LocalAuthorizationService authorization,
        SqliteCommandStore store, IRecipeDraftHistoryQuery drafts, IReleasedRecipeQuery? releases,
        IRecipeTransferHistoryQuery history, IReadOnlyList<AlgorithmDescriptor> descriptors)
    {
        _options = options; _authorization = authorization; _store = store; _drafts = drafts;
        _releases = releases; _history = history; _descriptors = descriptors.ToArray();
        // A read-only lifecycle projection owned by this service keeps the export
        // provenance honest without a constructor dependency or a new service edge.
        _lifecycle = options.RecipeLifecycle is null ? null : new SqliteRecipeLifecycleQuery(options);
        if (options.RecipeTransfers is null) throw new ArgumentException("RecipeTransferConfigurationRequired");
    }

    public ValueTask<RecipeTransferAccess> GetAccessAsync(CommandInvocation invocation, Permission permission,
        CancellationToken cancellationToken = default) => _authorization.GetRecipeTransferAccessAsync(invocation, permission, cancellationToken);
    public ValueTask<RecipeTrustReadResult> ReadTrustAsync(long? version = null, CancellationToken cancellationToken = default) =>
        _history.ReadTrustAsync(version, cancellationToken);
    public ValueTask<RecipeSigningKeysReadResult> ReadSigningKeysAsync(CancellationToken cancellationToken = default) =>
        _history.ReadSigningKeysAsync(cancellationToken);
    public ValueTask<RecipeImportReadResult> ReadImportAsync(Guid draftId, CancellationToken cancellationToken = default) =>
        _history.ReadImportAsync(draftId, cancellationToken);
    public ValueTask<RecipeTransferHistoryPage> QueryAsync(RecipeTransferFilter filter, CancellationToken cancellationToken = default) =>
        _history.QueryAsync(filter, cancellationToken);

    public ValueTask<RecipeTransferResult> ReplaceTrustAsync(ReplaceRecipeTrustStoreCommand command,
        CancellationToken cancellationToken = default) => ExecuteAsync(command, Permission.ManageRecipeTrustStore,
            _ => Task.FromResult(new RecipeTransferPreparedOperation(command)), cancellationToken);
    public ValueTask<RecipeTransferResult> RetireSigningKeyAsync(RetireRecipeSigningKeyCommand command,
        CancellationToken cancellationToken = default) => ExecuteAsync(command, Permission.ManageRecipeSigningKeys,
            _ => Task.FromResult(new RecipeTransferPreparedOperation(command)), cancellationToken);
    public ValueTask<RecipeTransferResult> CreateSigningKeyAsync(CreateRecipeSigningKeyCommand command,
        CancellationToken cancellationToken = default) => ExecuteAsync(command, Permission.ManageRecipeSigningKeys, token =>
        {
            token.ThrowIfCancellationRequested();
            var material = RecipeSigningKeyProtection.Generate(command, _options.LocalIdentity!.StationId);
            return Task.FromResult(new RecipeTransferPreparedOperation(command, signer: material.Signer,
                protectedPrivateKey: Convert.FromBase64String(material.ProtectedPrivateKeyBase64)));
        }, cancellationToken);

    public ValueTask<RecipeTransferResult> ExportAsync(ExportRecipeTransferCommand command,
        CancellationToken cancellationToken = default) => ExecuteAsync(command, Permission.ExportRecipe,
            token => PrepareExportAsync(command, token), cancellationToken);

    public ValueTask<RecipeTransferResult> ImportAsync(ImportRecipeTransferCommand command, ReadOnlyMemory<byte> packageBytes,
        CancellationToken cancellationToken = default) => ExecuteAsync(command, Permission.ImportRecipe,
            token => PrepareImportAsync(command, packageBytes, token), cancellationToken);

    private async ValueTask<RecipeTransferResult> ExecuteAsync(RecipeTransferCommand command, Permission permission,
        Func<CancellationToken, Task<RecipeTransferPreparedOperation>> prepare, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_slots.Wait(0)) return Failure(command, "RecipeTransferStagingCapacityExceeded");
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.CommitTimeout);
            var deadline = new StoreDeadline(_options.CommitTimeout);
            var access = await GetAccessAsync(command.Invocation, permission, budget.Token).ConfigureAwait(false);
            string? rejection = access.Allowed ? null : access.ReasonCode;
            var prepared = new RecipeTransferPreparedOperation(command);
            if (rejection is null)
            {
                try { prepared = await prepare(budget.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
                {
                    rejection = exception is InvalidOperationException or InvalidDataException &&
                        exception.Message.StartsWith("RecipeTransfer", StringComparison.Ordinal)
                        ? exception.Message : "RecipeTransferPreparationFailed";
                }
            }
            return await _authorization.ExecuteRecipeTransferAsync(command, prepared, rejection, _epoch, deadline, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failure(command, "RecipeTransferDeadlineExceeded"); }
        finally { _slots.Release(); }
    }

    private async Task<RecipeTransferPreparedOperation> PrepareImportAsync(ImportRecipeTransferCommand command,
        ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        if (input.Length is < 1 || input.Length > _options.RecipeTransfers!.MaximumPayloadBytes)
            throw Invalid("PackageTooLarge");
        // The snapshot is bounded and non-authoritative. No member path reaches the filesystem.
        var bytes = input.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != command.PackageBytesHash) throw Invalid("InputHashMismatch");
        if (!RecipeTransferPackageCodec.TryRead(bytes, out var package, out var reason)) throw new InvalidOperationException(reason);
        cancellationToken.ThrowIfCancellationRequested();
        var state = await _store.ReadRecipeTransferStateAsync(cancellationToken).ConfigureAwait(false);
        var trust = state.Trust ?? throw Invalid("SignerUntrusted");
        var signer = trust.Signers.SingleOrDefault(item => item.KeyId == package!.Manifest.Signature.KeyId &&
            item.Scope == package.Manifest.Signature.Scope) ?? throw Invalid("SignerUntrusted");
        var now = _authorization.RecipeTransferUtcNow;
        if (now < signer.NotBeforeUtc || now >= signer.NotAfterUtc || package!.Manifest.ExportedAtUtc > now ||
            package.Manifest.ExportedAtUtc < signer.NotBeforeUtc || package.Manifest.ExportedAtUtc >= signer.NotAfterUtc)
            throw Invalid("SignatureValidityRejected");
        VerifySignature(package, signer);
        var target = Guid.NewGuid();
        if (!RecipeTransferContentCodec.TryDecode(package.GetRecipeJsonBytes(), _descriptors,
            _options.RecipeTransfers.PortablePolicy, target, out var draft, out reason)) throw new InvalidOperationException(reason);
        if (!RecipeTransferContentCodec.DependenciesMatch(package.Manifest, draft!.Content)) throw Invalid("DependencyManifestMismatch");
        cancellationToken.ThrowIfCancellationRequested();
        return new(command, bytes, package, draft, signer, frozenTrustStoreVersion: trust.Version, newDraftId: target);
    }

    private async Task<RecipeTransferPreparedOperation> PrepareExportAsync(ExportRecipeTransferCommand command,
        CancellationToken cancellationToken)
    {
        RecipeDraftContent content;
        RecipeTransferSource source;
        if (command.Source.Draft is { } selectedDraft)
        {
            var read = await _drafts.ReadAsync(selectedDraft.DraftId, selectedDraft.Revision, cancellationToken).ConfigureAwait(false);
            var draft = read.Available ? read.Revision : null;
            if (draft is null || draft.RevisionContentHash != selectedDraft.RevisionContentHash) throw Invalid("ExportSourceUnavailable");
            content = draft.Content;
            source = new(draft.DraftId, content.RecipeKey, draft.Revision, draft.RevisionContentHash,
                await ReadDraftLifecycleAsync(draft.DraftId, cancellationToken).ConfigureAwait(false),
                _options.LocalIdentity!.StationId);
        }
        else
        {
            if (_releases is null || command.Source.Recipe is null) throw Invalid("ExportSourceUnavailable");
            var read = await _releases.ReadAsync(command.Source.Recipe, cancellationToken).ConfigureAwait(false);
            var released = read.Available ? read.Recipe : null;
            if (released is null || released.Record.ReleaseId != command.Source.ReleaseId ||
                released.Record.ContentHash != command.Source.ReleaseRecordContentHash) throw Invalid("ExportSourceUnavailable");
            content = released.Content;
            source = new(released.Record.ReleaseId, released.Reference.Id, released.Record.RecipeVersion, released.Record.ContentHash,
                await ReadReleaseLifecycleAsync(released.Record.Recipe, released.Record.ReleaseId,
                    released.Record.ContentHash, cancellationToken).ConfigureAwait(false),
                _options.LocalIdentity!.StationId);
        }
        if (!RecipeTransferContentCodec.TryEncode(content, _options.RecipeTransfers!.PortablePolicy,
            out var recipeBytes, out var portable, out var reason)) throw new InvalidOperationException(reason);
        var key = await _store.ReadRecipeTransferSigningMaterialAsync(command.SigningKeyId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid("SigningKeyUnavailable");
        var now = _authorization.RecipeTransferUtcNow;
        if (key.Signer.PublicKeyFingerprint != command.ExpectedPublicKeyFingerprint || now < key.Signer.NotBeforeUtc || now >= key.Signer.NotAfterUtc)
            throw Invalid("SigningKeyUnavailable");
        var manifest = new RecipeTransferPackageManifest(Guid.NewGuid(), source, now,
            new(key.Signer.KeyId, key.Signer.Scope), RecipeTransferContentCodec.Dependencies(portable!),
            new[] { new RecipeTransferMemberManifest(RecipeTransferPackageLimits.RecipeMemberPath, recipeBytes!.Length,
                Convert.ToHexString(SHA256.HashData(recipeBytes))) }, RecipeTransferPackageManifest.ZeroContentHash);
        manifest = RecipeTransferPackageCodec.FinalizeManifest(manifest, recipeBytes);
        var signature = RecipeSigningKeyProtection.Sign(key.ProtectedPrivateKeyBase64, key.Signer,
            _options.LocalIdentity!.StationId, RecipeTransferPackageCodec.GetSignatureInput(manifest));
        var bytes = RecipeTransferPackageCodec.Encode(manifest, recipeBytes, signature);
        if (!RecipeTransferPackageCodec.TryRead(bytes, out var package, out reason)) throw new InvalidOperationException(reason);
        VerifySignature(package!, key.Signer);
        cancellationToken.ThrowIfCancellationRequested();
        return new(command, bytes, package, signer: key.Signer, frozenSource: source,
            frozenSourceContentHash: content.ContentHash, frozenKeyFingerprint: key.Signer.PublicKeyFingerprint);
    }

    private static void VerifySignature(RecipeTransferPackage package, RecipeTrustedSigner signer)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signer.PublicKeyBase64), out _);
        if (!RecipeTransferPackageCodec.TryVerifySignature(package, key, out var reason)) throw new InvalidOperationException(reason);
    }

    /// <summary>
    /// The exported lifecycle claim is provenance only, but when this station keeps a
    /// verified lifecycle it must be the real one.  An unreadable projection fails closed
    /// instead of labelling an abandoned Draft as an open one.
    /// </summary>
    private async ValueTask<RecipeTransferSourceLifecycle> ReadDraftLifecycleAsync(Guid draftId,
        CancellationToken cancellationToken)
    {
        if (_lifecycle is null) return RecipeTransferSourceLifecycle.Draft;
        var read = await _lifecycle.ReadDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (!read.Available || read.State is null) throw Invalid("ExportSourceUnavailable");
        return read.State == RecipeDraftLifecycleState.Abandoned
            ? RecipeTransferSourceLifecycle.Abandoned : RecipeTransferSourceLifecycle.Draft;
    }

    /// <summary>
    /// A Released Recipe stays export material after retirement, so the exported package
    /// must say Retired rather than claiming the release is still available.
    /// </summary>
    private async ValueTask<RecipeTransferSourceLifecycle> ReadReleaseLifecycleAsync(RecipeReference recipe,
        Guid releaseId, string releaseRecordContentHash, CancellationToken cancellationToken)
    {
        if (_lifecycle is null) return RecipeTransferSourceLifecycle.Released;
        var read = await _lifecycle.ReadReleaseAsync(recipe, releaseId, releaseRecordContentHash, cancellationToken)
            .ConfigureAwait(false);
        if (!read.Available || read.State is null) throw Invalid("ExportSourceUnavailable");
        return read.State == ReleasedRecipeLifecycleState.Retired
            ? RecipeTransferSourceLifecycle.Retired : RecipeTransferSourceLifecycle.Released;
    }

    private static InvalidOperationException Invalid(string suffix) => new("RecipeTransfer" + suffix);
    private static RecipeTransferResult Failure(RecipeTransferCommand command, string reason) =>
        new(new(command.CorrelationId, CommandDisposition.Rejected, reason, AuditPersistence.Unavailable));
}
