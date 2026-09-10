using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal const string RecipeTransferSchemaSql = @"
        CREATE TABLE recipe_transfer_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            PortablePolicyId TEXT NOT NULL, PortablePolicyVersion TEXT NOT NULL,
            PortablePolicyHash TEXT NOT NULL CHECK(length(PortablePolicyHash)=64),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE recipe_transfer_trust_versions(
            Version INTEGER NOT NULL PRIMARY KEY CHECK(Version>0),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            PrincipalId TEXT NOT NULL CHECK(length(PrincipalId)=36),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            RecordedAtUtc TEXT NOT NULL, SignersJson TEXT NOT NULL,
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64));
        CREATE TABLE recipe_transfer_signing_keys(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            KeyId TEXT NOT NULL, SignerJson TEXT NOT NULL,
            ProtectedPrivateKeyBase64 TEXT NULL,
            Retired INTEGER NOT NULL CHECK(Retired IN (0,1)),
            OperationId TEXT NOT NULL CHECK(length(OperationId)=36),
            PrincipalId TEXT NOT NULL CHECK(length(PrincipalId)=36),
            RecordedAtUtc TEXT NOT NULL,
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            UNIQUE(KeyId,Position));
        CREATE INDEX ix_recipe_transfer_signing_key_id ON recipe_transfer_signing_keys(KeyId,Position);
        CREATE TABLE recipe_transfer_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            Kind INTEGER NOT NULL CHECK(Kind IN (1,2,3,4,5)),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            PrincipalId TEXT NOT NULL CHECK(length(PrincipalId)=36),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            SubjectHash TEXT NOT NULL CHECK(length(SubjectHash)=64),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64),
            BindingPayload TEXT NOT NULL,
            RecordedAtUtc TEXT NOT NULL,
            CentralSequence INTEGER NOT NULL CHECK(CentralSequence>0),
            CentralHash TEXT NOT NULL CHECK(length(CentralHash)=64));
        CREATE TABLE recipe_transfer_import_provenance(
            DraftId TEXT NOT NULL PRIMARY KEY CHECK(length(DraftId)=36),
            FirstRevisionContentHash TEXT NOT NULL CHECK(length(FirstRevisionContentHash)=64),
            SourceRecipeIdentity TEXT NOT NULL, SourceRevision TEXT NOT NULL,
            SourceLifecycle TEXT NOT NULL, SourceStationId TEXT NULL,
            SourceDescriptorContentHash TEXT NOT NULL CHECK(length(SourceDescriptorContentHash)=64),
            SourceSnapshotHash TEXT NOT NULL CHECK(length(SourceSnapshotHash)=64),
            PackageContentHash TEXT NOT NULL CHECK(length(PackageContentHash)=64),
            PackageBytesHash TEXT NOT NULL CHECK(length(PackageBytesHash)=64),
            SignerKeyId TEXT NOT NULL, SignerFingerprint TEXT NOT NULL CHECK(length(SignerFingerprint)=64),
            SignatureScheme TEXT NOT NULL, SignatureBase64 TEXT NOT NULL,
            TrustStoreVersion INTEGER NOT NULL CHECK(TrustStoreVersion>0),
            TrustStoreContentHash TEXT NOT NULL CHECK(length(TrustStoreContentHash)=64),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            PrincipalId TEXT NOT NULL CHECK(length(PrincipalId)=36),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            AuthorizationRevision INTEGER NOT NULL CHECK(AuthorizationRevision>=0),
            RecordedAtUtc TEXT NOT NULL);
        CREATE TRIGGER recipe_transfer_config_immutable_update BEFORE UPDATE ON recipe_transfer_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferConfiguration'); END;
        CREATE TRIGGER recipe_transfer_config_immutable_delete BEFORE DELETE ON recipe_transfer_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferConfiguration'); END;
        CREATE TRIGGER recipe_transfer_trust_immutable_update BEFORE UPDATE ON recipe_transfer_trust_versions BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferTrust'); END;
        CREATE TRIGGER recipe_transfer_trust_immutable_delete BEFORE DELETE ON recipe_transfer_trust_versions BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferTrust'); END;
        CREATE TRIGGER recipe_transfer_key_immutable_update BEFORE UPDATE ON recipe_transfer_signing_keys BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferSigningKey'); END;
        CREATE TRIGGER recipe_transfer_key_immutable_delete BEFORE DELETE ON recipe_transfer_signing_keys BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferSigningKey'); END;
        CREATE TRIGGER recipe_transfer_event_immutable_update BEFORE UPDATE ON recipe_transfer_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferEvent'); END;
        CREATE TRIGGER recipe_transfer_event_immutable_delete BEFORE DELETE ON recipe_transfer_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferEvent'); END;
        CREATE TRIGGER recipe_transfer_import_immutable_update BEFORE UPDATE ON recipe_transfer_import_provenance BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferImport'); END;
        CREATE TRIGGER recipe_transfer_import_immutable_delete BEFORE DELETE ON recipe_transfer_import_provenance BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeTransferImport'); END;";

    internal static void InitializeRecipeTransferSchema(sqlite3 database,
        RecipeTransferStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        SqliteNative.Execute(database, RecipeTransferSchemaSql, deadline);
        AuditChainDatabase.Execute(database,
            "INSERT INTO recipe_transfer_store_config VALUES(1,1,?,?,?,?,?,?,?);", deadline,
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            options.PortablePolicy.Id, options.PortablePolicy.Version,
            options.PortablePolicy.ContentHash, options.BindingHash);
        AuditChainDatabase.AppendRecipeTransferStoreActivation(database, policy, signingKey, options, deadline);
    }

    internal static void RequireConfiguredRecipeTransfers(sqlite3 database,
        RecipeTransferStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var row = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,
                PortablePolicyId,PortablePolicyVersion,PortablePolicyHash,BindingHash
            FROM recipe_transfer_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (Format: SqliteNative.ColumnInt64(statement, 0),
                Entries: SqliteNative.ColumnInt64(statement, 1), Payload: SqliteNative.ColumnInt64(statement, 2),
                Total: SqliteNative.ColumnInt64(statement, 3), PolicyId: SqliteNative.ColumnText(statement, 4),
                PolicyVersion: SqliteNative.ColumnText(statement, 5), PolicyHash: SqliteNative.ColumnText(statement, 6),
                Binding: SqliteNative.ColumnText(statement, 7))).SingleOrDefault();
        AuditChainDatabase.Require(row.Format == RecipeTransferStoreOptions.FormatVersion &&
            row.Entries == options.MaximumEntries && row.Payload == options.MaximumPayloadBytes &&
            row.Total == options.MaximumTotalBytes && row.PolicyId == options.PortablePolicy.Id &&
            row.PolicyVersion == options.PortablePolicy.Version && row.PolicyHash == options.PortablePolicy.ContentHash &&
            row.Binding == options.BindingHash, "RecipeTransferConfigurationMismatch");
        var activation = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeTransferStoreActivated';", deadline);
        AuditChainDatabase.Require(activation == 1, "RecipeTransferActivationMissing");
    }

    internal static RecipeTransferStoreState ReadRecipeTransferState(sqlite3 database,
        RecipeTransferStoreOptions options, StoreDeadline deadline,
        RecipeTransferCommand? command = null)
    {
        RequireConfiguredRecipeTransfers(database, options, deadline);
        var trustRows = AuditChainDatabase.Read(database, @"
            SELECT Version,OperationId,PrincipalId,SessionId,RecordedAtUtc,SignersJson
            FROM recipe_transfer_trust_versions ORDER BY Version DESC LIMIT 1;", deadline,
            ReadTrustRow);
        RecipeTrustStoreVersion? trust = trustRows.Count == 0 ? null :
            new RecipeTrustStoreVersion(trustRows[0].Version, DecodeSigners(trustRows[0].SignersJson),
                ParseGuid(trustRows[0].OperationId), ParseGuid(trustRows[0].PrincipalId),
                ParseGuid(trustRows[0].SessionId), ParseTime(trustRows[0].RecordedAtUtc));

        var keys = AuditChainDatabase.Read(database, @"
            SELECT KeyId,SignerJson,Retired,OperationId,PrincipalId,RecordedAtUtc
            FROM recipe_transfer_signing_keys k
            WHERE Position=(SELECT MAX(k2.Position) FROM recipe_transfer_signing_keys k2 WHERE k2.KeyId=k.KeyId)
            ORDER BY KeyId LIMIT ?;", deadline, statement => new RecipeSigningKeyRecord(
                SqliteNative.ColumnText(statement, 0)!, DecodeSigner(SqliteNative.ColumnText(statement, 1)!),
                SqliteNative.ColumnInt64(statement, 2) != 0,
                ParseGuid(SqliteNative.ColumnText(statement, 3)), ParseGuid(SqliteNative.ColumnText(statement, 4)),
                ParseTime(SqliteNative.ColumnText(statement, 5))),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture)).ToArray();
        AuditChainDatabase.Require(keys.Length <= options.MaximumEntries,
            "RecipeTransferEntryCapacityExceeded");

        var imports = AuditChainDatabase.Read(database, @"
            SELECT DraftId,FirstRevisionContentHash,SourceRecipeIdentity,SourceRevision,SourceLifecycle,
                SourceStationId,SourceDescriptorContentHash,SourceSnapshotHash,PackageContentHash,PackageBytesHash,
                SignerKeyId,SignerFingerprint,
                SignatureScheme,SignatureBase64,TrustStoreVersion,TrustStoreContentHash,OperationId,
                PrincipalId,SessionId,AuthorizationRevision,RecordedAtUtc
            FROM recipe_transfer_import_provenance ORDER BY DraftId LIMIT ?;", deadline, ReadImportRow,
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture)).ToArray();
        AuditChainDatabase.Require(imports.Length <= options.MaximumEntries,
            "RecipeTransferEntryCapacityExceeded");
        var last = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,OperationId,PrincipalId,SessionId,SubjectHash,ContentHash,RecordedAtUtc
            FROM recipe_transfer_events ORDER BY Position DESC LIMIT 1;", deadline,
            statement => new RecipeTransferHistoryRecord(SqliteNative.ColumnInt64(statement, 0),
                (RecipeTransferEventKind)SqliteNative.ColumnInt64(statement, 1),
                ParseGuid(SqliteNative.ColumnText(statement, 2)), ParseGuid(SqliteNative.ColumnText(statement, 3)),
                ParseGuid(SqliteNative.ColumnText(statement, 4)), SqliteNative.ColumnText(statement, 5)!,
                SqliteNative.ColumnText(statement, 6)!, ParseTime(SqliteNative.ColumnText(statement, 7)))).SingleOrDefault();
        var position = last?.Position ?? 0;
        RecipeDraftHead? source = null;
        if (command is ExportRecipeTransferCommand export && export.Source.Kind == RecipeTransferSourceKind.Draft &&
            export.Source.Draft is { } draft)
            source = ReadRecipeDraftRow(database, "WHERE DraftId=? AND Revision=? LIMIT 2", deadline,
                draft.DraftId.ToString("D"), draft.Revision.ToString(CultureInfo.InvariantCulture))
                .SingleOrDefault();
        return new RecipeTransferStoreState(trust, keys, imports, last, position, source);
    }

    internal static RecipeTrustStoreVersion? ReadRecipeTransferTrustVersion(sqlite3 database,
        RecipeTransferStoreOptions options, long? version, StoreDeadline deadline)
    {
        RequireConfiguredRecipeTransfers(database, options, deadline);
        var predicate = version is null ? string.Empty : " WHERE Version=?";
        var values = version is null ? Array.Empty<string?>() :
            new[] { version.Value.ToString(CultureInfo.InvariantCulture) };
        var rows = AuditChainDatabase.Read(database, $@"
            SELECT Version,OperationId,PrincipalId,SessionId,RecordedAtUtc,SignersJson
            FROM recipe_transfer_trust_versions{predicate}
            ORDER BY Version DESC LIMIT 1;", deadline, ReadTrustRow, values);
        if (rows.Count == 0) return null;
        var row = rows[0];
        var result = new RecipeTrustStoreVersion(row.Version, DecodeSigners(row.SignersJson),
            ParseGuid(row.OperationId), ParseGuid(row.PrincipalId),
            ParseGuid(row.SessionId), ParseTime(row.RecordedAtUtc));
        return result;
    }

    internal static long AppendRecipeTransferEvent(sqlite3 database, AuditIntegrityPolicy policy,
        IAuditSigningKey signingKey, RecipeTransferEventKind kind, Guid operationId,
        RecipeTransferVerifiedActor actor, string subjectHash, string contentHash,
        DateTimeOffset recordedAtUtc, byte[] payload, byte[] bindingPayload,
        RecipeTransferStoreOptions options,
        StoreDeadline deadline)
    {
        AuditChainDatabase.Require(operationId != Guid.Empty && actor.PrincipalId != Guid.Empty &&
            actor.SessionId != Guid.Empty && AuditCanonical.IsHash(subjectHash) && AuditCanonical.IsHash(contentHash) &&
            payload.Length > 0 && payload.Length <= options.MaximumPayloadBytes &&
            bindingPayload is { Length: > 0 } && bindingPayload.Length <= options.MaximumPayloadBytes,
            "RecipeTransferEventInvalid");
        var bindingHash = Convert.ToHexString(SHA256.HashData(bindingPayload));
        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM recipe_transfer_events;", deadline));
        var central = AuditChainDatabase.AppendRecipeTransferLedgerEntry(database, policy, signingKey,
            position, payload, options, deadline);
        var usedEntries = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM recipe_transfer_events;", deadline);
        var usedBytes = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0)
            FROM audit_entries WHERE RecipeTransferPosition IS NOT NULL;", deadline);
        AuditChainDatabase.Require(usedEntries < options.MaximumEntries &&
            usedBytes <= options.MaximumTotalBytes,
            "RecipeTransferEntryCapacityExceeded");
        AuditChainDatabase.Execute(database, @"
            INSERT INTO recipe_transfer_events(Position,Kind,OperationId,PrincipalId,SessionId,SubjectHash,
                ContentHash,BindingHash,BindingPayload,RecordedAtUtc,CentralSequence,CentralHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), ((int)kind).ToString(CultureInfo.InvariantCulture),
            operationId.ToString("D"), actor.PrincipalId.ToString("D"), actor.SessionId.ToString("D"), subjectHash,
            contentHash, bindingHash, Convert.ToBase64String(bindingPayload),
            recordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            central.Sequence.ToString(CultureInfo.InvariantCulture), central.Hash);
        return position;
    }

    internal static string EncodeRecipeTransferPayload(RecipeTransferEventKind kind,
        Guid operationId, Guid principalId, Guid sessionId, string subjectHash, string contentHash,
        DateTimeOffset recordedAtUtc, string? detail = null) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("RecipeTransferEvent", ((int)kind).ToString(CultureInfo.InvariantCulture),
            operationId.ToString("D"), principalId.ToString("D"), sessionId.ToString("D"), subjectHash,
            contentHash, recordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), detail)));

    private static (long Version, string OperationId, string PrincipalId, string SessionId,
        string RecordedAtUtc, string SignersJson) ReadTrustRow(sqlite3_stmt statement) =>
        (SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1)!,
            SqliteNative.ColumnText(statement, 2)!, SqliteNative.ColumnText(statement, 3)!,
            SqliteNative.ColumnText(statement, 4)!, SqliteNative.ColumnText(statement, 5)!);

    private static RecipeImportProvenance ReadImportRow(sqlite3_stmt statement) => new(
        ParseGuid(SqliteNative.ColumnText(statement, 0)), SqliteNative.ColumnText(statement, 1)!,
        SqliteNative.ColumnText(statement, 2)!, SqliteNative.ColumnText(statement, 3)!,
        SqliteNative.ColumnText(statement, 4)!, SqliteNative.ColumnText(statement, 7)!,
        SqliteNative.ColumnText(statement, 8)!, SqliteNative.ColumnText(statement, 9)!,
        SqliteNative.ColumnText(statement, 10)!, SqliteNative.ColumnText(statement, 11)!,
        SqliteNative.ColumnText(statement, 12)!, SqliteNative.ColumnText(statement, 13)!,
        SqliteNative.ColumnInt64(statement, 14), SqliteNative.ColumnText(statement, 15)!,
        ParseGuid(SqliteNative.ColumnText(statement, 16)), ParseGuid(SqliteNative.ColumnText(statement, 17)),
        ParseGuid(SqliteNative.ColumnText(statement, 18)), SqliteNative.ColumnInt64(statement, 19),
        ParseTime(SqliteNative.ColumnText(statement, 20)), SqliteNative.ColumnText(statement, 5),
        SqliteNative.ColumnText(statement, 6)!);

    private static string EncodeSigners(IEnumerable<RecipeTrustedSigner> signers) =>
        JsonSerializer.Serialize(signers.Select(item => new SignerDto(item.KeyId, item.PublicKeyBase64,
            item.Scope, item.NotBeforeUtc, item.NotAfterUtc)).ToArray());

    private static RecipeTrustedSigner[] DecodeSigners(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 128 * 1024)
            throw new InvalidOperationException("RecipeTransferTrustCorrupt");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        AuditChainDatabase.Require(document.RootElement.ValueKind == JsonValueKind.Array &&
            document.RootElement.GetArrayLength() <= 64, "RecipeTransferTrustCorrupt");
        foreach (var item in document.RootElement.EnumerateArray())
            ValidateSignerJson(item);
        var values = JsonSerializer.Deserialize<SignerDto[]>(json) ??
            throw new InvalidOperationException("RecipeTransferTrustCorrupt");
        AuditChainDatabase.Require(values.Length == document.RootElement.GetArrayLength() &&
            JsonSerializer.Serialize(values) == json, "RecipeTransferTrustCorrupt");
        return values.Select(item => new RecipeTrustedSigner(item.KeyId, item.PublicKeyBase64, item.Scope,
            item.NotBeforeUtc, item.NotAfterUtc)).ToArray();
    }

    private static string EncodeSigner(RecipeTrustedSigner signer) =>
        JsonSerializer.Serialize(new SignerDto(signer.KeyId, signer.PublicKeyBase64, signer.Scope,
            signer.NotBeforeUtc, signer.NotAfterUtc));

    private static RecipeTrustedSigner DecodeSigner(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 8 * 1024)
            throw new InvalidOperationException("RecipeTransferSignerCorrupt");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        ValidateSignerJson(document.RootElement);
        var item = JsonSerializer.Deserialize<SignerDto>(json) ??
            throw new InvalidOperationException("RecipeTransferSignerCorrupt");
        AuditChainDatabase.Require(JsonSerializer.Serialize(item) == json,
            "RecipeTransferSignerCorrupt");
        return new RecipeTrustedSigner(item.KeyId, item.PublicKeyBase64, item.Scope,
            item.NotBeforeUtc, item.NotAfterUtc);
    }

    private static void ValidateSignerJson(JsonElement value)
    {
        AuditChainDatabase.Require(value.ValueKind == JsonValueKind.Object,
            "RecipeTransferSignerCorrupt");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        { "KeyId", "PublicKeyBase64", "Scope", "NotBeforeUtc", "NotAfterUtc" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            AuditChainDatabase.Require(allowed.Contains(property.Name) && seen.Add(property.Name),
                "RecipeTransferSignerCorrupt");
        AuditChainDatabase.Require(seen.SetEquals(allowed), "RecipeTransferSignerCorrupt");
        foreach (var name in allowed)
            AuditChainDatabase.Require(value.GetProperty(name).ValueKind == JsonValueKind.String,
                "RecipeTransferSignerCorrupt");
    }

    private sealed record SignerDto(string KeyId, string PublicKeyBase64, string Scope,
        DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc);
}
