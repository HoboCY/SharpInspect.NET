using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal async ValueTask<RecipeTransferStoreState> ReadRecipeTransferStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!RecipeTransferEnabled || _databasePath is null)
            throw new InvalidOperationException("RecipeTransferConfigurationRequired");
        await Initialization.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = SqliteNative.Open(_databasePath, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
        var deadline = new StoreDeadline(_options.QueryTimeout);
        SqliteNative.Execute(connection.Handle!, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        try
        {
            VerifyRecipeTransferReadGuard(connection.Handle!, _options, deadline);
            var result = ReadRecipeTransferState(connection.Handle!, _options.RecipeTransfers!, deadline);
            SqliteNative.Execute(connection.Handle!, "COMMIT;", deadline, cancellationToken);
            return result;
        }
        catch
        {
            try { SqliteNative.Execute(connection.Handle!, "ROLLBACK;", deadline); } catch { }
            throw;
        }
    }

    internal async ValueTask<RecipeSigningKeyMaterial?> ReadRecipeTransferSigningMaterialAsync(
        string keyId, CancellationToken cancellationToken = default)
    {
        if (!RecipeTransferEnabled || _databasePath is null)
            return null;
        keyId = AlgorithmConfigurationValidation.Identifier(keyId, nameof(keyId));
        await Initialization.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = SqliteNative.Open(_databasePath, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
        var deadline = new StoreDeadline(_options.QueryTimeout);
        SqliteNative.Execute(connection.Handle!, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        try
        {
            VerifyRecipeTransferReadGuard(connection.Handle!, _options, deadline);
            var value = AuditChainDatabase.Read(connection.Handle!, @"
                SELECT SignerJson,ProtectedPrivateKeyBase64,Retired
                FROM recipe_transfer_signing_keys WHERE KeyId=? ORDER BY Position DESC LIMIT 1;", deadline,
                statement => (SignerJson: SqliteNative.ColumnText(statement, 0),
                    Secret: SqliteNative.ColumnText(statement, 1), Retired: SqliteNative.ColumnInt64(statement, 2)),
                keyId).SingleOrDefault();
            SqliteNative.Execute(connection.Handle!, "COMMIT;", deadline, cancellationToken);
            if (value.SignerJson is null || value.Secret is null || value.Retired != 0)
                return null;
            _ = Convert.FromBase64String(value.Secret);
            return new RecipeSigningKeyMaterial(DecodeSigner(value.SignerJson), value.Secret);
        }
        catch
        {
            try { SqliteNative.Execute(connection.Handle!, "ROLLBACK;", deadline); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Enqueues a transfer operation after the caller has performed its
    /// public package/source checks.  The evaluator is deliberately invoked
    /// again inside the writer transaction with the signed identity state and
    /// current transfer heads.
    /// </summary>
    internal ValueTask<RecipeTransferResult> ExecuteRecipeTransferAsync(
        RecipeTransferCommand command, RecipeTransferPreparedOperation prepared,
        Func<IdentityAuthorityState, RecipeTransferStoreState, bool, RecipeTransferEvaluation> evaluate,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(evaluate);
        if (!RecipeTransferEnabled)
            return ValueTask.FromResult(TransferFailure(command, "RecipeTransferConfigurationRequired"));
        if (prepared.Command.CorrelationId != command.CorrelationId)
            return ValueTask.FromResult(TransferFailure(command, "RecipeTransferPreparedCommandMismatch"));
        var work = new RecipeTransferWork(command, prepared, evaluate, cancellationToken);
        return EnqueueRecipeTransferAsync(work, deadline, cancellationToken);
    }

    private async ValueTask<RecipeTransferResult> EnqueueRecipeTransferAsync(
        RecipeTransferWork work, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        if (_queue is null || _queueSlots is null || Volatile.Read(ref _disposed) != 0)
            return TransferFailure(work.Command, "RecipeTransferUnavailable");
        try
        {
            var result = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                    RecipeTransfer: work), deadline, cancellationToken,
                "RecipeTransferUnavailable", "RecipeTransferCommitDeadlineExceeded").ConfigureAwait(false);
            return result.Committed && work.Result is RecipeTransferResult transfer
                ? transfer : TransferFailure(work.Command, result.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return TransferFailure(work.Command, ex is InvalidOperationException ? ex.Message : "RecipeTransferUnavailable"); }
    }

    private StoreWriteResult AppendRecipeTransferCore(sqlite3 database,
        RecipeTransferWork work, StoreDeadline deadline)
    {
        if (work.CancellationToken.IsCancellationRequested)
            return new StoreWriteResult(false, "RecipeTransferCancelled");
        var integrity = Integrity;
        if (integrity?.State != AuditIntegrityState.Verified)
            return new StoreWriteResult(false, integrity?.ReasonCode ?? "RecipeTransferAuditUnavailable",
                RetryAfterIntegrityRecheck: integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new StoreWriteResult(false, "TraceStoreWalLimit");

        var committed = false;
        IIdentityTransactionGuard? guard = null;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            if (work.CancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("RecipeTransferCancelled");
            VerifyRecipeTransferReadGuard(database, _options, deadline);
            RequireConfiguredRecipeTransfers(database, _options.RecipeTransfers!, deadline);
            var identity = ReadIdentityState(database, deadline);
            var transferState = ReadRecipeTransferState(database, _options.RecipeTransfers!, deadline, work.Command);
            var duplicate = Exists(database,
                "SELECT 1 FROM recipe_transfer_events WHERE OperationId=? LIMIT 1;",
                work.Command.CorrelationId, deadline);
            identity.Revision = checked(identity.Revision + 1);
            var evaluated = work.Evaluate(identity, transferState, duplicate);
            ArgumentNullException.ThrowIfNull(evaluated);
            guard = evaluated.Guard;
            if (work.CancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("RecipeTransferCancelled");
            if (duplicate && evaluated.Result.Outcome.Disposition == CommandDisposition.Accepted &&
                evaluated.Events.Count == 0 && evaluated.CommandFacts is null)
            {
                var originalFact = VerifyDuplicateRecipeTransfer(database, work.Command, evaluated.VerifiedActor, deadline);
                var replay = ReadPersistedRecipeTransferResult(database, work.Command, evaluated.Result,
                    deadline) with
                {
                    Outcome = new(originalFact.CorrelationId, originalFact.Disposition!.Value,
                        originalFact.ReasonCode, AuditPersistence.Persisted, originalFact.AttemptId)
                };
                SqliteNative.Execute(database, "ROLLBACK;", deadline);
                committed = true;
                work.Result = replay;
                guard?.Commit();
                return new StoreWriteResult(true, replay.Outcome.ReasonCode);
            }
            AuditChainDatabase.Require(evaluated.Events.Count is > 0 and <= 8,
                "RecipeTransferIdentityEventRequired");
            if (evaluated.CommandFacts is { Count: > 0 })
                AuditChainDatabase.Require(evaluated.CommandFacts.Count <= 2,
                    "RecipeTransferCommandFactsLimit");
            if (evaluated.Result.Outcome.Disposition == CommandDisposition.Accepted &&
                !(duplicate && evaluated.Events.Count == 0 && evaluated.CommandFacts is null))
                EnsureRecipeTransferCapacity(database, _options.RecipeTransfers!, deadline);

            var identitySequence = 0L;
            foreach (var identityEvent in evaluated.Events)
            {
                identitySequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey!,
                    BindAuthenticationPolicy(identityEvent with { StateRevision = identity.Revision }), deadline);
            }
            AppendIdentityCommandFacts(database, evaluated.CommandFacts, work.Command.CorrelationId, deadline);

            RecipeTransferResult result = evaluated.Result;
            if (evaluated.Result.Outcome.Disposition == CommandDisposition.Accepted)
            {
                AuditChainDatabase.Require(evaluated.VerifiedActor is not null,
                    "RecipeTransferVerifiedActorRequired");
                if (duplicate)
                {
                    // An accepted operation is immutable and can only be replayed
                    // after the fresh authorization callback has accepted it again.
                    result = ReadPersistedRecipeTransferResult(database, work.Command, evaluated.Result,
                        deadline);
                }
                else
                {
                    result = PersistRecipeTransferMutation(database, work, evaluated, transferState,
                        identity, deadline);
                    if (work.CancellationToken.IsCancellationRequested)
                        throw new InvalidOperationException("RecipeTransferCancelled");
                }
            }

            var tail = AuditChainDatabase.LastIdentityEntry(database, deadline);
            AuditChainDatabase.Require(tail is not null && identitySequence > 0 &&
                tail.Value.Sequence == identitySequence, "RecipeTransferIdentityAuditMismatch");
            identity.LastIdentityAuditHash = tail!.Value.Hash;
            var protectedState = IdentityStateProtection.Protect(identity);
            AuditChainDatabase.Execute(database,
                "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;",
                deadline, identity.Revision.ToString(CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, identity.StationId, identity.Revision,
                    identitySequence, _signingKey!));
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            if (work.CancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("RecipeTransferCancelled");
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Result = result;
            guard?.Commit();
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy!, AuditIntegrityState.Verifying,
                "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new StoreWriteResult(true, result.Outcome.ReasonCode, evaluated.CommandFacts?.FirstOrDefault());
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return new StoreWriteResult(false, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is InvalidOperationException invalid &&
                (invalid.Message.StartsWith("RecipeTransfer", StringComparison.Ordinal) ||
                 invalid.Message.StartsWith("Identity", StringComparison.Ordinal) ||
                 invalid.Message.StartsWith("RecipeDraft", StringComparison.Ordinal))
                ? invalid.Message : SqliteAuditIntegrityQuery.FaultReason(ex, "RecipeTransferCommitFailed");
            if (reason.StartsWith("Audit", StringComparison.Ordinal) && !AuditChainDatabase.IsCapacityReason(reason))
                SetIntegrityFault(reason, IsStructuralFault(reason));
            return new StoreWriteResult(false, reason);
        }
        finally
        {
            try { guard?.Dispose(); } catch (Exception) { }
            if (!committed) Rollback(database);
        }
    }

    private RecipeTransferResult PersistRecipeTransferMutation(sqlite3 database,
        RecipeTransferWork work, RecipeTransferEvaluation evaluated, RecipeTransferStoreState state,
        IdentityAuthorityState identity, StoreDeadline deadline)
    {
        var actor = evaluated.VerifiedActor ?? throw new InvalidOperationException(
            "RecipeTransferVerifiedActorRequired");
        var recordedAt = actor.VerifiedAtUtc;
        var command = work.Command;
        RecipeTransferEventKind kind;
        string subjectHash;
        byte[] bindingPayload;
        RecipeTransferResult result = evaluated.Result;
        switch (command)
        {
            case ReplaceRecipeTrustStoreCommand replace:
            {
                var currentVersion = state.Trust?.Version ?? 0;
                AuditChainDatabase.Require(replace.ExpectedVersion == currentVersion,
                    "RecipeTransferTrustVersionChanged");
                var next = checked(currentVersion + 1);
                var trust = new RecipeTrustStoreVersion(next, replace.Signers, command.CorrelationId,
                    actor.PrincipalId, actor.SessionId, recordedAt);
                AuditChainDatabase.Execute(database, @"
                    INSERT INTO recipe_transfer_trust_versions(Version,OperationId,PrincipalId,SessionId,
                        RecordedAtUtc,SignersJson,ContentHash) VALUES(?,?,?,?,?,?,?);", deadline,
                    next.ToString(CultureInfo.InvariantCulture), command.CorrelationId.ToString("D"),
                    actor.PrincipalId.ToString("D"), actor.SessionId.ToString("D"), FormatTime(recordedAt),
                    EncodeSigners(trust.Signers), trust.ContentHash);
                kind = RecipeTransferEventKind.TrustStoreReplaced;
                subjectHash = trust.ContentHash;
                bindingPayload = EncodeRecipeTransferBinding("RecipeTransferTrustBinding", command, actor,
                    recordedAt, replace.ExpectedVersion.ToString(CultureInfo.InvariantCulture),
                    next.ToString(CultureInfo.InvariantCulture), trust.ContentHash,
                    EncodeSigners(trust.Signers),
                    string.Join(";", trust.Signers.Select(item => item.ContentHash)));
                result = result with { Trust = trust };
                break;
            }
            case CreateRecipeSigningKeyCommand create:
            {
                var signer = work.Prepared.Signer ?? throw new InvalidOperationException("RecipeTransferSignerMissing");
                var secret = work.Prepared.ProtectedPrivateKey;
                AuditChainDatabase.Require(secret is { Length: > 0 }, "RecipeTransferSigningKeyMaterialMissing");
                var secretBytes = secret!;
                AuditChainDatabase.Require(signer.KeyId == create.KeyId && signer.Scope == create.Scope &&
                    signer.NotBeforeUtc == create.NotBeforeUtc && signer.NotAfterUtc == create.NotAfterUtc,
                    "RecipeTransferSigningKeyPreparedMismatch");
                var duplicate = AuditChainDatabase.Scalar(database,
                    "SELECT 1 FROM recipe_transfer_signing_keys WHERE KeyId=? AND Retired=0 LIMIT 1;",
                    deadline, create.KeyId) != 0;
                AuditChainDatabase.Require(!duplicate, "RecipeTransferSigningKeyAlreadyExists");
                var position = checked(AuditChainDatabase.Scalar(database,
                    "SELECT COALESCE(MAX(Position),0)+1 FROM recipe_transfer_signing_keys;", deadline));
                var keyContentHash = signer.ContentHash;
                AuditChainDatabase.Execute(database, @"
                    INSERT INTO recipe_transfer_signing_keys(Position,KeyId,SignerJson,ProtectedPrivateKeyBase64,
                        Retired,OperationId,PrincipalId,RecordedAtUtc,ContentHash)
                    VALUES(?,?,?,?,?,?,?,?,?);", deadline, position.ToString(CultureInfo.InvariantCulture),
                    signer.KeyId, EncodeSigner(signer), Convert.ToBase64String(secretBytes), "0",
                    command.CorrelationId.ToString("D"), actor.PrincipalId.ToString("D"), FormatTime(recordedAt),
                    keyContentHash);
                kind = RecipeTransferEventKind.SigningKeyCreated;
                subjectHash = keyContentHash;
                bindingPayload = EncodeRecipeTransferBinding("RecipeTransferSigningKeyBinding", command,
                    actor, recordedAt, "Created", signer.KeyId, signer.Scope, signer.Scheme,
                    signer.PublicKeyBase64, signer.PublicKeyFingerprint,
                    signer.NotBeforeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    signer.NotAfterUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    signer.ContentHash, Convert.ToHexString(SHA256.HashData(secretBytes)),
                    secretBytes.Length.ToString(CultureInfo.InvariantCulture));
                result = result with { SigningKey = new RecipeSigningKeyRecord(signer.KeyId, signer, false,
                    command.CorrelationId, actor.PrincipalId, recordedAt) };
                break;
            }
            case RetireRecipeSigningKeyCommand retire:
            {
                var key = state.SigningKeys.SingleOrDefault(item => item.KeyId == retire.KeyId);
                AuditChainDatabase.Require(key is not null && !key.Retired &&
                    key.Signer.PublicKeyFingerprint == retire.ExpectedPublicKeyFingerprint,
                    "RecipeTransferSigningKeyRetireMismatch");
                var position = checked(AuditChainDatabase.Scalar(database,
                    "SELECT COALESCE(MAX(Position),0)+1 FROM recipe_transfer_signing_keys;", deadline));
                AuditChainDatabase.Execute(database, @"
                    INSERT INTO recipe_transfer_signing_keys(Position,KeyId,SignerJson,ProtectedPrivateKeyBase64,
                        Retired,OperationId,PrincipalId,RecordedAtUtc,ContentHash)
                    VALUES(?,?,?,?,?,?,?,?,?);", deadline, position.ToString(CultureInfo.InvariantCulture),
                    key!.KeyId, EncodeSigner(key.Signer), null, "1", command.CorrelationId.ToString("D"),
                    actor.PrincipalId.ToString("D"), FormatTime(recordedAt), key.Signer.ContentHash);
                kind = RecipeTransferEventKind.SigningKeyRetired;
                subjectHash = key.Signer.ContentHash;
                bindingPayload = EncodeRecipeTransferBinding("RecipeTransferSigningKeyBinding", command,
                    actor, recordedAt, "Retired", key.Signer.KeyId, key.Signer.Scope,
                    key.Signer.Scheme, key.Signer.PublicKeyBase64, key.Signer.PublicKeyFingerprint,
                    key.Signer.NotBeforeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    key.Signer.NotAfterUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    key.Signer.ContentHash, null, null,
                    retire.ExpectedPublicKeyFingerprint);
                result = result with { SigningKey = key with { Retired = true,
                    OperationId = command.CorrelationId, PrincipalId = actor.PrincipalId, RecordedAtUtc = recordedAt } };
                break;
            }
            case ExportRecipeTransferCommand export:
            {
                var bytes = work.Prepared.PackageBytes ?? throw new InvalidOperationException("RecipeTransferExportBytesMissing");
                var package = work.Prepared.Package ?? throw new InvalidOperationException("RecipeTransferExportPackageMissing");
                AuditChainDatabase.Require(bytes.Length <= _options.RecipeTransfers!.MaximumPayloadBytes,
                    "RecipeTransferPayloadCapacityExceeded");
                var bytesHash = Convert.ToHexString(SHA256.HashData(bytes));
                var frozenSource = work.Prepared.FrozenSource ??
                    throw new InvalidOperationException("RecipeTransferExportSourceMissing");
                var signer = work.Prepared.Signer ??
                    throw new InvalidOperationException("RecipeTransferExportSignerMissing");
                RequireExportSource(export.Source, frozenSource);
                RequirePackageSource(package.Manifest.Source, frozenSource);
                var sourceContent = ReadCurrentExportContent(database, export.Source, frozenSource,
                    work.Prepared.FrozenSourceContentHash, state, deadline);
                RequirePackageIntegrity(package, bytes, signer, recordedAt);
                if (!RecipeTransferContentCodec.TryEncode(sourceContent,
                    _options.RecipeTransfers!.PortablePolicy, out var projectedBytes, out _, out var projectionReason) ||
                    projectedBytes is null || !projectedBytes.AsSpan().SequenceEqual(package.GetRecipeJsonBytes()))
                    throw new InvalidOperationException(projectionReason);
                AuditChainDatabase.Require(package.Manifest.Signature.KeyId == export.SigningKeyId &&
                    package.Manifest.Signature.ContentHash ==
                        new RecipeTransferSignatureInfo(signer.KeyId, signer.Scope).ContentHash &&
                    signer.PublicKeyFingerprint == export.ExpectedPublicKeyFingerprint &&
                    signer.PublicKeyFingerprint == work.Prepared.FrozenKeyFingerprint &&
                    work.Prepared.FrozenSourceContentHash is { Length: 64 } &&
                    state.SigningKeys.Any(item => !item.Retired && item.KeyId == signer.KeyId &&
                        item.Signer.ContentHash == signer.ContentHash),
                    "RecipeTransferExportPreparedMismatch");
                kind = RecipeTransferEventKind.Exported;
                subjectHash = package.Manifest.ContentHash;
                bindingPayload = EncodeTransferBinding("RecipeTransferExportBinding", command, actor,
                    recordedAt, ExportSourceFields(export.Source).Concat(SourceFields(frozenSource)).Concat(new[]
                    {
                        package.Manifest.PackageId.ToString("D"), package.Manifest.ContentHash, bytesHash,
                        Convert.ToHexString(SHA256.HashData(package.GetRecipeJsonBytes())),
                        Convert.ToHexString(SHA256.HashData(package.GetSignatureBytes())),
                        package.Manifest.Signature.KeyId, package.Manifest.Signature.Scope,
                        package.Manifest.Signature.Scheme, package.Manifest.Signature.ContentHash,
                        signer.ContentHash, signer.PublicKeyFingerprint, work.Prepared.FrozenSourceContentHash,
                        EncodePackageDependencies(package), EncodePackageMembers(package)
                    }).ToArray());
                result = result with { Export = new RecipeTransferExport(bytes, package.Manifest.ContentHash, bytesHash) };
                break;
            }
            case ImportRecipeTransferCommand import:
            {
                var package = work.Prepared.Package ?? throw new InvalidOperationException("RecipeTransferImportPackageMissing");
                var document = work.Prepared.DraftDocument ?? throw new InvalidOperationException("RecipeTransferDraftMissing");
                var bytes = work.Prepared.PackageBytes ?? throw new InvalidOperationException("RecipeTransferPackageBytesMissing");
                AuditChainDatabase.Require(work.Prepared.NewDraftId is Guid newDraftId &&
                    document.Content.RecipeKey == "import." + newDraftId.ToString("N"),
                    "RecipeTransferDraftIdentityMismatch");
                AuditChainDatabase.Require(Convert.ToHexString(SHA256.HashData(bytes)) == import.PackageBytesHash,
                    "RecipeTransferPackageHashMismatch");
                var signer = work.Prepared.Signer ?? throw new InvalidOperationException("RecipeTransferSignerMissing");
                var trust = state.Trust ?? throw new InvalidOperationException("RecipeTransferTrustMissing");
                AuditChainDatabase.Require(work.Prepared.FrozenTrustStoreVersion == trust.Version &&
                    trust.Signers.Any(item => item.ContentHash == signer.ContentHash && item.KeyId == signer.KeyId &&
                        item.Scope == signer.Scope && item.PublicKeyFingerprint == signer.PublicKeyFingerprint),
                    "RecipeTransferTrustChanged");
                RequirePackageIntegrity(package, bytes, signer, recordedAt);
                AuditChainDatabase.Require(package.Manifest.Signature.KeyId == signer.KeyId &&
                    package.Manifest.Signature.Scope == signer.Scope &&
                    package.Manifest.Signature.Scheme == RecipeTransferPackageLimits.SignatureScheme,
                    "RecipeTransferSignerMismatch");
                if (!RecipeTransferContentCodec.TryEncode(document.Content,
                    _options.RecipeTransfers!.PortablePolicy, out var projectedBytes, out _, out var projectionReason) ||
                    projectedBytes is null || !projectedBytes.AsSpan().SequenceEqual(package.GetRecipeJsonBytes()) ||
                    !RecipeTransferContentCodec.DependenciesMatch(package.Manifest, document.Content))
                    throw new InvalidOperationException(projectionReason);
                if (!RecipeDraftStorageCodec.TryEncodeContent(document.Content,
                    out var encodedDocument, out var documentReason) || encodedDocument is null ||
                    encodedDocument.Content.ContentHash != document.Content.ContentHash ||
                    encodedDocument.PayloadJson != document.PayloadJson ||
                    encodedDocument.PayloadHash != document.PayloadHash)
                    throw new InvalidOperationException(documentReason);
                var draftId = work.Prepared.NewDraftId ??
                    throw new InvalidOperationException("RecipeTransferDraftIdentityMissing");
                var draftRequest = new RecipeDraftSaveRequest(command.CorrelationId, draftId, 0, null,
                    document.Content, command.Reason, command.Invocation, command.Invocation.StepUpGrantId);
                var mutation = new RecipeDraftMutation(actor.PrincipalId, actor.SessionId,
                    actor.AuthorizationRevision, command.Reason);
                EnsureRecipeDraftCapacity(database, _options.RecipeDrafts!, document, deadline);
                var draft = InsertRecipeDraftRevision(database, draftRequest, document, null, mutation, deadline);
                AuditChainDatabase.AppendRecipeDraftRevision(database, _policy!, _signingKey!, draft.Position, deadline);
                var source = package.Manifest.Source;
                var provenance = new RecipeImportProvenance(draft.DraftId, draft.RevisionContentHash,
                    source.SourceId.ToString("D") + ":" + source.RecipeKey,
                    source.Revision.ToString(CultureInfo.InvariantCulture), source.Lifecycle.ToString(),
                    source.RevisionContentHash, package.Manifest.ContentHash, import.PackageBytesHash,
                    package.Manifest.Signature.KeyId, signer.PublicKeyFingerprint, package.Manifest.Signature.Scheme,
                    Convert.ToBase64String(package.GetSignatureBytes()), trust.Version, trust.ContentHash,
                    command.CorrelationId, actor.PrincipalId, actor.SessionId, actor.AuthorizationRevision,
                    recordedAt, source.SourceStationId, source.ContentHash);
                AuditChainDatabase.Execute(database, @"
                    INSERT INTO recipe_transfer_import_provenance(DraftId,FirstRevisionContentHash,
                        SourceRecipeIdentity,SourceRevision,SourceLifecycle,SourceStationId,
                        SourceDescriptorContentHash,SourceSnapshotHash,
                        PackageContentHash,PackageBytesHash,SignerKeyId,SignerFingerprint,SignatureScheme,
                        SignatureBase64,TrustStoreVersion,TrustStoreContentHash,OperationId,PrincipalId,
                        SessionId,AuthorizationRevision,RecordedAtUtc)
                    VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                    provenance.DraftId.ToString("D"), provenance.FirstRevisionContentHash,
                    provenance.SourceRecipeIdentity, provenance.SourceRevision, provenance.SourceLifecycle,
                    provenance.SourceStationId, provenance.SourceDescriptorContentHash,
                    provenance.SourceSnapshotHash, provenance.PackageContentHash, provenance.PackageBytesHash,
                    provenance.SignerKeyId, provenance.SignerFingerprint, provenance.SignatureScheme,
                    provenance.SignatureBase64, provenance.TrustStoreVersion.ToString(CultureInfo.InvariantCulture),
                    provenance.TrustStoreContentHash, provenance.OperationId.ToString("D"),
                    provenance.PrincipalId.ToString("D"), provenance.SessionId.ToString("D"),
                    provenance.AuthorizationRevision.ToString(CultureInfo.InvariantCulture), FormatTime(recordedAt));
                kind = RecipeTransferEventKind.Imported;
                subjectHash = package.Manifest.ContentHash;
                bindingPayload = EncodeTransferBinding("RecipeTransferImportBinding", command, actor,
                    recordedAt, ProvenanceFields(provenance).Concat(new[]
                    { document.Content.ContentHash, document.PayloadHash }).ToArray());
                result = result with { Draft = draft, Import = provenance };
                break;
            }
            default:
                throw new InvalidOperationException("RecipeTransferCommandUnsupported");
        }

        var bindingHash = Convert.ToHexString(SHA256.HashData(bindingPayload));
        var contentHash = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "RecipeTransferPersisted", ((int)kind).ToString(CultureInfo.InvariantCulture),
            command.CorrelationId.ToString("D"), actor.PrincipalId.ToString("D"), actor.SessionId.ToString("D"),
            subjectHash, recordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), bindingHash,
            Convert.ToBase64String(bindingPayload))));
        var payload = AuditCanonical.Encode("RecipeTransferEvent", ((int)kind).ToString(CultureInfo.InvariantCulture),
            command.CorrelationId.ToString("D"), actor.PrincipalId.ToString("D"), actor.SessionId.ToString("D"),
            subjectHash, contentHash, recordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            bindingHash, Convert.ToBase64String(bindingPayload));
        _ = AppendRecipeTransferEvent(database, _policy!, _signingKey!, kind, command.CorrelationId,
            actor, subjectHash, contentHash, recordedAt, payload, bindingPayload, _options.RecipeTransfers!, deadline);
        return result;
    }

    private static void EnsureRecipeTransferCapacity(sqlite3 database,
        RecipeTransferStoreOptions options, StoreDeadline deadline)
    {
        var entries = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM recipe_transfer_events;", deadline);
        var bytes = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0)
            FROM audit_entries WHERE RecipeTransferPosition IS NOT NULL;", deadline);
        AuditChainDatabase.Require(entries < options.MaximumEntries && bytes < options.MaximumTotalBytes,
            "RecipeTransferEntryCapacityExceeded");
    }

    private static CommandAuditFact VerifyDuplicateRecipeTransfer(sqlite3 database,
        RecipeTransferCommand command, RecipeTransferVerifiedActor? actor, StoreDeadline deadline)
    {
        var stored = AuditChainDatabase.Read(database, @"
            SELECT Kind,PrincipalId,SessionId FROM recipe_transfer_events
            WHERE OperationId=? LIMIT 2;", deadline,
            statement => ((RecipeTransferEventKind)SqliteNative.ColumnInt64(statement, 0),
                ParseGuid(SqliteNative.ColumnText(statement, 1)),
                ParseGuid(SqliteNative.ColumnText(statement, 2))), command.CorrelationId.ToString("D"));
        AuditChainDatabase.Require(stored.Count == 1, "RecipeTransferDuplicateCorrelationMismatch");
        var row = stored[0];
        AuditChainDatabase.Require(ExpectedCommandKind(row.Item1) == CommandKind(command) &&
            (actor is null || (row.Item2 == actor.PrincipalId && row.Item3 == actor.SessionId)),
            "RecipeTransferDuplicateCorrelationMismatch");
        var attempts = AuditChainDatabase.Read(database, @"
            SELECT AttemptId FROM command_facts
            WHERE CorrelationId=? AND AggregateSequence=1 AND Phase=0 AND Disposition=0
            ORDER BY Position LIMIT 2;", deadline,
            statement => ParseGuid(SqliteNative.ColumnText(statement, 0)), command.CorrelationId.ToString("D"));
        AuditChainDatabase.Require(attempts.Count == 1, "RecipeTransferDuplicateCommandMismatch");
        var fact = ReadFact(database, attempts[0], 1, deadline);
        AuditChainDatabase.Require(fact is not null && fact.CommandKind == CommandKind(command) &&
            fact.Source == command.Invocation.Source &&
            fact.ClaimedStepUpGrantId == command.Invocation.StepUpGrantId &&
            (actor is null || (fact.ClaimedPrincipalId == actor.PrincipalId.ToString("D") &&
                fact.ClaimedSessionId == actor.SessionId)), "RecipeTransferDuplicateCommandMismatch");
        var identities = ReadRecipeTransferIdentityRows(database, deadline)
            .Where(item => item.CorrelationId == command.CorrelationId && item.Fields.Length == 49 &&
                item.Fields[2] == IdentityEventKind.RecipeTransferAuthorized.ToString()).ToArray();
        AuditChainDatabase.Require(identities.Length == 1 && identities[0].Fields[37] == command.AuthorizationTarget &&
            identities[0].Fields[39] == CommandKind(command).ToString() &&
            identities[0].Fields[32] == command.Invocation.StepUpGrantId?.ToString("D"),
            "RecipeTransferDuplicateAuthorizationMismatch");
        return fact!;
    }

    private static AuditedCommandKind CommandKind(RecipeTransferCommand command) => command switch
    {
        ReplaceRecipeTrustStoreCommand => AuditedCommandKind.ReplaceRecipeTrustStore,
        CreateRecipeSigningKeyCommand => AuditedCommandKind.CreateRecipeSigningKey,
        RetireRecipeSigningKeyCommand => AuditedCommandKind.RetireRecipeSigningKey,
        ExportRecipeTransferCommand => AuditedCommandKind.ExportRecipeTransfer,
        ImportRecipeTransferCommand => AuditedCommandKind.ImportRecipeTransfer,
        _ => throw new InvalidOperationException("RecipeTransferCommandUnsupported")
    };

    private RecipeDraftContent ReadCurrentExportContent(sqlite3 database,
        RecipeTransferSourceSelection selection, RecipeTransferSource frozenSource,
        string? frozenContentHash, RecipeTransferStoreState state, StoreDeadline deadline)
    {
        if (selection.Kind == RecipeTransferSourceKind.Draft)
        {
            var head = state.SourceDraft;
            AuditChainDatabase.Require(head is not null && head.DraftId == frozenSource.SourceId &&
                head.Revision == frozenSource.Revision && head.RevisionContentHash == frozenSource.RevisionContentHash,
                "RecipeTransferExportSourceMismatch");
            if (!RecipeDraftStorageCodec.TryDecodeContent(head!.PayloadJson, head.PayloadHash,
                    out var content, out var reason) || content is null)
                throw new InvalidOperationException(reason);
            AuditChainDatabase.Require(content.ContentHash == frozenContentHash,
                "RecipeTransferExportSourceChanged");
            return content;
        }

        AuditChainDatabase.Require(_options.RecipeReleases is not null,
            "RecipeTransferExportSourceUnavailable");
        var releases = ReadRecipeReleaseRows(database, _options.RecipeReleases!, deadline);
        var selected = releases.SingleOrDefault(item =>
            item.Record.ReleaseId == selection.ReleaseId &&
            item.Record.ContentHash == selection.ReleaseRecordContentHash &&
            item.Record.Recipe.Id == selection.Recipe!.Id &&
            item.Record.Recipe.Version == selection.Recipe.Version &&
            item.Record.Recipe.ContentHash == selection.Recipe.ContentHash);
        var record = selected?.Record;
        AuditChainDatabase.Require(record is not null &&
            record.Source.Content.ContentHash == frozenContentHash &&
            frozenSource.SourceId == record.ReleaseId &&
            frozenSource.Revision == record.RecipeVersion &&
            frozenSource.RevisionContentHash == record.ContentHash,
            "RecipeTransferExportSourceMismatch");
        return record!.Source.Content;
    }

    private static byte[] EncodeRecipeTransferBinding(string kind, RecipeTransferCommand command,
        RecipeTransferVerifiedActor actor, DateTimeOffset recordedAt, params string?[] fields) =>
        EncodeTransferBinding(kind, command, actor, recordedAt, fields);

    private static byte[] EncodeTransferBinding(string kind, RecipeTransferCommand command,
        RecipeTransferVerifiedActor actor, DateTimeOffset recordedAt, IEnumerable<string?> fields)
    {
        var common = new string?[]
        {
            command.CorrelationId.ToString("D"), actor.PrincipalId.ToString("D"),
            actor.SessionId.ToString("D"), actor.AuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            command.AuthorizationTarget,
            recordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };
        return AuditCanonical.Encode(kind, common.Concat(fields).ToArray());
    }

    private static string?[] ExportSourceFields(RecipeTransferSourceSelection source)
    {
        if (source.Kind == RecipeTransferSourceKind.Draft)
        {
            var draft = source.Draft ?? throw new InvalidOperationException("RecipeTransferExportSourceMissing");
            return new[] { source.Kind.ToString(), source.ContentHash, draft.DraftId.ToString("D"),
                draft.Revision.ToString(CultureInfo.InvariantCulture), draft.RevisionContentHash };
        }
        var recipe = source.Recipe ?? throw new InvalidOperationException("RecipeTransferExportSourceMissing");
        return new[] { source.Kind.ToString(), source.ContentHash, recipe.Id, recipe.Version,
            recipe.ContentHash, source.ReleaseId?.ToString("D"), source.ReleaseRecordContentHash };
    }

    private static string?[] SourceFields(RecipeTransferSource source) => new[]
    {
        source.ContentHash, source.SourceId.ToString("D"), source.RecipeKey,
        source.Revision.ToString(CultureInfo.InvariantCulture), source.RevisionContentHash,
        source.Lifecycle.ToString(), source.SourceStationId
    };

    private static string?[] ProvenanceFields(RecipeImportProvenance value) => new[]
    {
        value.DraftId.ToString("D"), value.FirstRevisionContentHash, value.SourceRecipeIdentity,
        value.SourceRevision, value.SourceLifecycle, value.SourceStationId,
        value.SourceDescriptorContentHash, value.SourceSnapshotHash,
        value.PackageContentHash, value.PackageBytesHash, value.SignerKeyId, value.SignerFingerprint,
        value.SignatureScheme, value.SignatureBase64, value.TrustStoreVersion.ToString(CultureInfo.InvariantCulture),
        value.TrustStoreContentHash, value.OperationId.ToString("D"), value.PrincipalId.ToString("D"),
        value.SessionId.ToString("D"), value.AuthorizationRevision.ToString(CultureInfo.InvariantCulture),
        value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    };

    private static RecipeTransferSource ReconstructProvenanceSource(RecipeImportProvenance value)
    {
        var separator = value.SourceRecipeIdentity.IndexOf(':');
        AuditChainDatabase.Require(separator > 0 && separator < value.SourceRecipeIdentity.Length - 1,
            "RecipeTransferImportSourceBindingMismatch");
        AuditChainDatabase.Require(Guid.TryParseExact(value.SourceRecipeIdentity[..separator], "D",
            out var sourceId), "RecipeTransferImportSourceBindingMismatch");
        AuditChainDatabase.Require(long.TryParse(value.SourceRevision, NumberStyles.None,
            CultureInfo.InvariantCulture, out var revision) && revision > 0,
            "RecipeTransferImportSourceBindingMismatch");
        AuditChainDatabase.Require(Enum.TryParse<RecipeTransferSourceLifecycle>(value.SourceLifecycle,
            ignoreCase: false, out var lifecycle) && lifecycle.ToString() == value.SourceLifecycle,
            "RecipeTransferImportSourceBindingMismatch");
        try
        {
            return new RecipeTransferSource(sourceId, value.SourceRecipeIdentity[(separator + 1)..],
                revision, value.SourceSnapshotHash, lifecycle, value.SourceStationId);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("RecipeTransferImportSourceBindingMismatch", exception);
        }
    }

    private static string EncodePackageDependencies(RecipeTransferPackage package) =>
        Convert.ToBase64String(AuditCanonical.Encode("RecipeTransferPackageDependencies",
            package.Manifest.Dependencies.SelectMany(item => new[] { item.Kind, item.Contract.Id,
                item.Contract.Version, item.Contract.ContentHash, item.ContentHash }).ToArray()));

    private static string EncodePackageMembers(RecipeTransferPackage package) =>
        Convert.ToBase64String(AuditCanonical.Encode("RecipeTransferPackageMembers",
            package.Manifest.Members.SelectMany(item => new[] { item.RelativePath,
                item.Length.ToString(CultureInfo.InvariantCulture), item.ContentHash }).ToArray()));

    private static void RequireExportSource(RecipeTransferSourceSelection selected,
        RecipeTransferSource frozen)
    {
        var expectedSelectionHash = selected.Kind == RecipeTransferSourceKind.Draft
            ? AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-transfer-source-v1",
                selected.Kind.ToString(), selected.Draft!.DraftId.ToString("D"),
                selected.Draft.Revision.ToString(CultureInfo.InvariantCulture),
                selected.Draft.RevisionContentHash })
            : AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-transfer-source-v1",
                selected.Kind.ToString(), selected.Recipe!.Id, selected.Recipe.Version,
                selected.Recipe.ContentHash, selected.ReleaseId!.Value.ToString("D"),
                selected.ReleaseRecordContentHash });
        AuditChainDatabase.Require(selected.ContentHash == expectedSelectionHash,
            "RecipeTransferExportSourceMismatch");
        if (selected.Kind == RecipeTransferSourceKind.Draft)
        {
            var draft = selected.Draft;
            AuditChainDatabase.Require(draft is not null && frozen.Lifecycle == RecipeTransferSourceLifecycle.Draft &&
                frozen.SourceId == draft.DraftId && frozen.Revision == draft.Revision &&
                frozen.RevisionContentHash == draft.RevisionContentHash, "RecipeTransferExportSourceMismatch");
            return;
        }
        var recipe = selected.Recipe;
        AuditChainDatabase.Require(recipe is not null && frozen.Lifecycle == RecipeTransferSourceLifecycle.Released &&
            frozen.SourceId == selected.ReleaseId && frozen.RevisionContentHash == selected.ReleaseRecordContentHash &&
            frozen.RecipeKey == recipe.Id && frozen.Revision.ToString(CultureInfo.InvariantCulture) == recipe.Version,
            "RecipeTransferExportSourceMismatch");
    }

    private static void RequirePackageSource(RecipeTransferSource packageSource,
        RecipeTransferSource expected)
    {
        AuditChainDatabase.Require(packageSource.ContentHash == expected.ContentHash &&
            packageSource.SourceId == expected.SourceId && packageSource.RecipeKey == expected.RecipeKey &&
            packageSource.Revision == expected.Revision &&
            packageSource.RevisionContentHash == expected.RevisionContentHash &&
            packageSource.Lifecycle == expected.Lifecycle &&
            packageSource.SourceStationId == expected.SourceStationId,
            "RecipeTransferPackageSourceMismatch");
    }

    private static void RequirePackageIntegrity(RecipeTransferPackage package, byte[] bytes,
        RecipeTrustedSigner signer, DateTimeOffset recordedAt)
    {
        if (!RecipeTransferPackageCodec.TryRead(bytes, out var reparsed, out var parseReason) || reparsed is null)
            throw new InvalidOperationException(parseReason);
        RequireSamePackage(package, reparsed);
        AuditChainDatabase.Require(bytes is { Length: > 0 and <= RecipeTransferPackageLimits.MaximumPackageBytes } &&
            package.Manifest.ContentHash != RecipeTransferPackageManifest.ZeroContentHash &&
            package.Manifest.Signature.KeyId == signer.KeyId &&
            package.Manifest.Signature.Scope == signer.Scope &&
            package.Manifest.Signature.Scheme == RecipeTransferPackageLimits.SignatureScheme &&
            package.GetSignatureBytes().Length == RecipeTransferPackageLimits.SignatureBytes &&
            package.Manifest.ExportedAtUtc <= recordedAt && recordedAt >= signer.NotBeforeUtc &&
            recordedAt < signer.NotAfterUtc, "RecipeTransferPackageInvalid");
        var recipeBytes = package.GetRecipeJsonBytes();
        var member = package.Manifest.Members.SingleOrDefault(item =>
            item.RelativePath == RecipeTransferPackageLimits.RecipeMemberPath);
        AuditChainDatabase.Require(member is not null && member.Length == recipeBytes.Length &&
            member.ContentHash == Convert.ToHexString(SHA256.HashData(recipeBytes)) &&
            Convert.ToHexString(SHA256.HashData(bytes)).Length == 64, "RecipeTransferPackageInvalid");
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signer.PublicKeyBase64), out _);
        AuditChainDatabase.Require(RecipeTransferPackageCodec.TryVerifySignature(package, key, out _),
            "RecipeTransferSignatureInvalid");
    }

    private static void RequireSamePackage(RecipeTransferPackage expected,
        RecipeTransferPackage actual)
    {
        var expectedManifest = expected.Manifest;
        var actualManifest = actual.Manifest;
        AuditChainDatabase.Require(expectedManifest.ContentHash == actualManifest.ContentHash &&
            expectedManifest.PackageId == actualManifest.PackageId &&
            expectedManifest.Source.ContentHash == actualManifest.Source.ContentHash &&
            expectedManifest.Source.SourceId == actualManifest.Source.SourceId &&
            expectedManifest.Source.RecipeKey == actualManifest.Source.RecipeKey &&
            expectedManifest.Source.Revision == actualManifest.Source.Revision &&
            expectedManifest.Source.RevisionContentHash == actualManifest.Source.RevisionContentHash &&
            expectedManifest.Source.Lifecycle == actualManifest.Source.Lifecycle &&
            expectedManifest.Source.SourceStationId == actualManifest.Source.SourceStationId &&
            expectedManifest.ExportedAtUtc == actualManifest.ExportedAtUtc &&
            expectedManifest.Signature.KeyId == actualManifest.Signature.KeyId &&
            expectedManifest.Signature.Scope == actualManifest.Signature.Scope &&
            expectedManifest.Signature.Scheme == actualManifest.Signature.Scheme &&
            expectedManifest.Signature.ContentHash == actualManifest.Signature.ContentHash &&
            expectedManifest.Dependencies.Select(item => new
                {
                    item.Kind, ContractId = item.Contract.Id, ContractVersion = item.Contract.Version,
                    ContractHash = item.Contract.ContentHash, DependencyHash = item.ContentHash
                })
                .SequenceEqual(actualManifest.Dependencies.Select(item => new
                {
                    item.Kind, ContractId = item.Contract.Id, ContractVersion = item.Contract.Version,
                    ContractHash = item.Contract.ContentHash, DependencyHash = item.ContentHash
                })) &&
            expectedManifest.Members.Select(item => new { item.RelativePath, item.Length, MemberHash = item.ContentHash })
                .SequenceEqual(actualManifest.Members.Select(item =>
                    new { item.RelativePath, item.Length, MemberHash = item.ContentHash })) &&
            expected.GetRecipeJsonBytes().AsSpan().SequenceEqual(actual.GetRecipeJsonBytes()) &&
            expected.GetSignatureBytes().AsSpan().SequenceEqual(actual.GetSignatureBytes()),
            "RecipeTransferPreparedPackageMismatch");
    }

    private RecipeTransferResult ReadPersistedRecipeTransferResult(sqlite3 database,
        RecipeTransferCommand command, RecipeTransferResult result, StoreDeadline deadline)
    {
        if (command is ImportRecipeTransferCommand import)
        {
            var provenance = AuditChainDatabase.Read(database, @"
                SELECT DraftId,FirstRevisionContentHash,SourceRecipeIdentity,SourceRevision,SourceLifecycle,
                    SourceStationId,SourceDescriptorContentHash,SourceSnapshotHash,PackageContentHash,PackageBytesHash,
                    SignerKeyId,SignerFingerprint,
                    SignatureScheme,SignatureBase64,TrustStoreVersion,TrustStoreContentHash,OperationId,
                    PrincipalId,SessionId,AuthorizationRevision,RecordedAtUtc
                FROM recipe_transfer_import_provenance WHERE OperationId=? LIMIT 2;", deadline,
                ReadImportRow, import.CorrelationId.ToString("D")).SingleOrDefault();
            if (provenance is null) return result;
            var draftRow = ReadRecipeDraftRow(database,
                "WHERE DraftId=? AND Revision=1 LIMIT 2", deadline,
                provenance.DraftId.ToString("D")).SingleOrDefault();
            var draft = draftRow is null ? null : ToPublicRevision(database, draftRow, deadline);
            return result with { Draft = draft, Import = provenance };
        }
        // Only imports retain their complete return value. In particular a regenerated
        // export is a different package: never report success with an absent artifact.
        throw new InvalidOperationException("RecipeTransferReplayResultUnavailable");
    }

    private static RecipeTransferResult TransferFailure(RecipeTransferCommand command, string reason) =>
        new(new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected, reason,
            AuditPersistence.Unavailable));

}
