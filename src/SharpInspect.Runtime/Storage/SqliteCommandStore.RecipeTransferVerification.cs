using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// A transfer read can expose a signing key or a trust decision to the
    /// caller.  It therefore performs the same full, co-enabled verification
    /// as a cold history query while the read-only transaction is open.
    /// </summary>
    internal static void VerifyRecipeTransferReadGuard(sqlite3 database,
        ProductionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(options);
        var policy = options.AuditIntegrityPolicy ??
            throw new InvalidOperationException("AuditPolicyNotConfigured");
        using var key = WindowsMachineAuditKey.Open(policy, false, out _);
        var report = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
            new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false,
            deadline, validateAnchorReceipt: false,
            archiveOptions: options.AlgorithmResultArchive,
            recipeDraftOptions: options.RecipeDrafts,
            cameraSetupOptions: options.CameraSetup,
            cameraRecoveryOptions: options.CameraRecovery,
            cameraNetworkOptions: options.CameraNetwork,
            imagingSetupOptions: options.ImagingSetup,
            calibrationSessionOptions: options.CalibrationSessions,
            governanceOptions: options.CalibrationGovernance,
            releaseOptions: options.RecipeReleases,
            contractOptions: options.PlcResultContracts,
            activationOptions: options.RecipeActivations,
            previewOptions: options.PreviewSessions,
            importOptions: options.CalibrationImports,
            manualOptions: options.ManualInspections,
            productionAdmissionOptions: options.ProductionAdmission,
            stationQualificationOptions: options.StationQualifications,
             recipeTransferOptions: options.RecipeTransfers,
             traceStoragePolicyOptions: options.TraceStoragePolicies,
                qualificationCycleOptions: options.QualificationCycles,
                plcCommunicationOptions: options.PlcCommunication,
                productionInspectionOptions: options.ProductionInspections,
                productionRecoveryOptions: options.ProductionRecovery,
                partIdentityOptions: options.PartIdentities,
                productionArmOptions: options.ProductionArming, recipeSelectionOptions: options.RecipeSelections, recipeLifecycleOptions: options.RecipeLifecycle);
        TraceStoragePolicyReadGuard.RequireVerified(database, report, deadline, options);

        if (options.AlgorithmResultArchive is not null)
            AuditChainDatabase.RequireFullAlgorithmResultVerification(database, report, deadline);
        if (options.RecipeDrafts is not null)
            AuditChainDatabase.RequireFullRecipeDraftVerification(database, report, deadline,
                options.RecipeDrafts);
        if (options.CameraSetup is not null)
            AuditChainDatabase.RequireFullCameraSetupVerification(database, report, deadline,
                options.CameraSetup);
        if (options.CameraRecovery is not null)
            AuditChainDatabase.RequireFullCameraRecoveryVerification(database, report, deadline,
                options.CameraRecovery);
        if (options.CameraNetwork is not null)
            AuditChainDatabase.RequireFullCameraNetworkVerification(database, report, deadline,
                options.CameraNetwork);
        if (options.ImagingSetup is not null)
            AuditChainDatabase.RequireFullImagingSetupVerification(database, report, deadline,
                options.ImagingSetup);
        if (options.CalibrationGovernance is not null)
            AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, report, deadline,
                options.CalibrationGovernance);
        if (options.RecipeReleases is not null)
            AuditChainDatabase.RequireFullRecipeReleaseVerification(database, report, deadline,
                options.RecipeReleases, options.RecipeDrafts, options.CalibrationGovernance);
        if (options.PlcResultContracts is not null)
            AuditChainDatabase.RequireFullPlcResultContractVerification(database, report, deadline,
                options.PlcResultContracts);
        if (options.RecipeActivations is not null)
            AuditChainDatabase.RequireFullRecipeActivationVerification(database, report, deadline,
                options.RecipeActivations, options.RecipeReleases, options.PlcResultContracts,
                options.CalibrationGovernance, recipeLifecycleOptions: options.RecipeLifecycle);
        if (options.PreviewSessions is not null)
            AuditChainDatabase.RequireFullPreviewSessionVerification(database, report, deadline,
                options.PreviewSessions);
        if (options.CalibrationImports is not null)
            AuditChainDatabase.RequireFullCalibrationImportVerification(database, report, deadline,
                options.CalibrationImports);
        if (options.ManualInspections is not null)
            AuditChainDatabase.RequireFullManualInspectionVerification(database, report, deadline,
                options.ManualInspections);
        if (options.ProductionAdmission is not null)
            AuditChainDatabase.RequireFullProductionAdmissionVerification(database, report, deadline,
                options.ProductionAdmission);
        if (options.StationQualifications is not null)
            AuditChainDatabase.RequireFullStationQualificationVerification(database, report, deadline,
                options.StationQualifications);
        AuditChainDatabase.RequireFullRecipeTransferVerification(database, report, deadline,
            options.RecipeTransfers);
        if (options.PlcCommunication is not null)
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, report, deadline,
                options.PlcCommunication);
    }

    internal static void VerifyRecipeTransferActivationPayload(sqlite3 database, byte[] payload,
        RecipeTransferStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes &&
            payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "RecipeTransferActivationPayloadMismatch");
    }

    /// <summary>
    /// Verifies the immutable transfer row and its central-audit projection.
    /// This method intentionally does not call the full history verifier because
    /// AuditChainDatabase.Verify invokes it while replaying the central chain.
    /// </summary>
    internal static void VerifyRecipeTransferAuditPayload(sqlite3 database, long position,
        byte[] payload, RecipeTransferStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        AuditChainDatabase.Require(position > 0 && payload.Length > 0 &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= RecipeTransferStoreOptions.MaximumPayloadBytesHardLimit,
            "RecipeTransferAuditPayloadInvalid");

        var row = AuditChainDatabase.Read(database, @"
            SELECT Kind,OperationId,PrincipalId,SessionId,SubjectHash,ContentHash,
                BindingHash,BindingPayload,RecordedAtUtc,CentralSequence,CentralHash
            FROM recipe_transfer_events WHERE Position=? LIMIT 2;", deadline,
            statement => new TransferEventRow(position,
                (RecipeTransferEventKind)SqliteNative.ColumnInt64(statement, 0),
                ParseGuid(SqliteNative.ColumnText(statement, 1)),
                ParseGuid(SqliteNative.ColumnText(statement, 2)),
                ParseGuid(SqliteNative.ColumnText(statement, 3)),
                 SqliteNative.ColumnText(statement, 4)!, SqliteNative.ColumnText(statement, 5)!,
                 SqliteNative.ColumnText(statement, 6)!, SqliteNative.ColumnText(statement, 7)!,
                 ParseTime(SqliteNative.ColumnText(statement, 8)),
                 SqliteNative.ColumnInt64(statement, 9), SqliteNative.ColumnText(statement, 10)!),
             position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();

        AuditChainDatabase.Require(row is not null && Enum.IsDefined(row.Kind) &&
            row.OperationId != Guid.Empty && row.PrincipalId != Guid.Empty && row.SessionId != Guid.Empty &&
            AuditCanonical.IsHash(row.SubjectHash) && AuditCanonical.IsHash(row.ContentHash) &&
            AuditCanonical.IsHash(row.BindingHash) && row.BindingPayload is { Length: > 0 } &&
            row.CentralSequence > 0 && AuditCanonical.IsHash(row.CentralHash),
            "RecipeTransferEventMissing");

        byte[] bindingPayload;
        try { bindingPayload = Convert.FromBase64String(row!.BindingPayload); }
        catch (FormatException exception)
        { throw new InvalidOperationException("RecipeTransferBindingPayloadInvalid", exception); }
        AuditChainDatabase.Require(bindingPayload.Length > 0 &&
            bindingPayload.Length <= options.MaximumPayloadBytes &&
            Convert.ToHexString(SHA256.HashData(bindingPayload)) == row.BindingHash,
            "RecipeTransferBindingPayloadInvalid");
        var expectedPayload = AuditCanonical.Encode("RecipeTransferEvent",
            ((int)row!.Kind).ToString(CultureInfo.InvariantCulture), row.OperationId.ToString("D"),
            row.PrincipalId.ToString("D"), row.SessionId.ToString("D"), row.SubjectHash,
            row.ContentHash, row.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            row.BindingHash, Convert.ToBase64String(bindingPayload));
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(expectedPayload),
            "RecipeTransferPayloadBindingMismatch");
        var expectedContentHash = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "RecipeTransferPersisted", ((int)row.Kind).ToString(CultureInfo.InvariantCulture),
            row.OperationId.ToString("D"), row.PrincipalId.ToString("D"), row.SessionId.ToString("D"),
            row.SubjectHash, row.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            row.BindingHash, Convert.ToBase64String(bindingPayload))));
        AuditChainDatabase.Require(row.ContentHash == expectedContentHash,
            "RecipeTransferContentHashMismatch");

        var central = AuditChainDatabase.Read(database, @"
            SELECT Kind,RecipeTransferPosition,Payload,Hash,Sequence
            FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0),
                Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2), Hash: SqliteNative.ColumnText(statement, 3),
                Sequence: SqliteNative.ColumnInt64(statement, 4)),
            row.CentralSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(central.Kind == "RecipeTransferEvent" &&
            central.Sequence == row.CentralSequence && central.Position == row.Position &&
            central.Hash == row.CentralHash && central.Payload == Convert.ToBase64String(payload),
            "RecipeTransferCentralBindingMismatch");
    }

    internal static void ValidateRecipeTransferHistory(sqlite3 database,
        RecipeTransferStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RequireConfiguredRecipeTransfers(database, options, deadline);

        var events = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,OperationId,PrincipalId,SessionId,SubjectHash,ContentHash,
                BindingHash,BindingPayload,RecordedAtUtc,CentralSequence,CentralHash
            FROM recipe_transfer_events ORDER BY Position LIMIT ?;", deadline,
            statement => new TransferEventRow(SqliteNative.ColumnInt64(statement, 0),
                (RecipeTransferEventKind)SqliteNative.ColumnInt64(statement, 1),
                ParseGuid(SqliteNative.ColumnText(statement, 2)),
                ParseGuid(SqliteNative.ColumnText(statement, 3)),
                ParseGuid(SqliteNative.ColumnText(statement, 4)),
                SqliteNative.ColumnText(statement, 5)!, SqliteNative.ColumnText(statement, 6)!,
                SqliteNative.ColumnText(statement, 7)!, SqliteNative.ColumnText(statement, 8)!,
                ParseTime(SqliteNative.ColumnText(statement, 9)), SqliteNative.ColumnInt64(statement, 10),
                SqliteNative.ColumnText(statement, 11)!),
            (options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(events.Count <= options.MaximumEntries,
            "RecipeTransferEntryCapacityExceeded");

        var totalBytes = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(length(CAST(a.Payload AS BLOB))),0)
            FROM audit_entries a WHERE a.RecipeTransferPosition IS NOT NULL;", deadline);
        AuditChainDatabase.Require(totalBytes <= options.MaximumTotalBytes,
            "RecipeTransferTotalCapacityExceeded");

        var seenOperations = new HashSet<Guid>();
        var expectedPosition = 1L;
        var identityRows = ReadRecipeTransferIdentityRows(database, deadline);
        foreach (var value in events)
        {
            AuditChainDatabase.Require(value.Position == expectedPosition++ &&
                seenOperations.Add(value.OperationId), "RecipeTransferPositionGap");
            var centralPayload = AuditChainDatabase.Read(database, @"
                SELECT Payload FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
                statement => SqliteNative.ColumnText(statement, 0),
                value.CentralSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(centralPayload is not null,
                "RecipeTransferCentralBindingMismatch");
            byte[] payload;
            try { payload = Convert.FromBase64String(centralPayload!); }
            catch (FormatException exception)
            { throw new InvalidOperationException("RecipeTransferCentralBindingMismatch", exception); }
            VerifyRecipeTransferAuditPayload(database, value.Position, payload, options, deadline);
            var authorization = VerifyRecipeTransferAuthorization(database, value, identityRows, deadline);
            VerifyRecipeTransferEventBinding(database, value, authorization.Revision,
                authorization.Target, deadline);
        }

        ValidateRecipeTransferTrust(database, events, options, deadline);
        ValidateRecipeTransferSigningKeys(database, events, options, deadline);
        ValidateRecipeTransferImports(database, events, options, deadline);
    }

    private static (long Revision, string Target) VerifyRecipeTransferAuthorization(sqlite3 database, TransferEventRow value,
        IReadOnlyList<TransferIdentityRow> identityRows, StoreDeadline deadline)
    {
        var accepted = AuditChainDatabase.Read(database, @"
            SELECT AttemptId FROM command_facts
            WHERE CorrelationId=? AND AggregateSequence=1 AND Phase=0 AND Disposition=0
            ORDER BY Position LIMIT 2;", deadline,
            statement => ParseGuid(SqliteNative.ColumnText(statement, 0)), value.OperationId.ToString("D"));
        AuditChainDatabase.Require(accepted.Count == 1, "RecipeTransferCommandAuditMismatch");
        var fact = ReadFact(database, accepted[0], 1, deadline);
        AuditChainDatabase.Require(fact is not null && fact.CorrelationId == value.OperationId &&
            fact.Phase == CommandAuditPhase.Outcome && fact.Disposition == CommandDisposition.Accepted &&
            fact.Source is { } source && Enum.IsDefined(source) && fact.ReasonCode == "RecipeTransferAuthorized" &&
            fact.ClaimedPrincipalId == value.PrincipalId.ToString("D") &&
            fact.ClaimedSessionId == value.SessionId &&
            fact.AuthenticatedHumanPrincipalId == value.PrincipalId.ToString("D") &&
            fact.CommandKind == ExpectedCommandKind(value.Kind), "RecipeTransferCommandAuditMismatch");

        var matches = identityRows.Where(row => row.CorrelationId == value.OperationId).ToArray();
        matches = matches.Where(row => row.Fields.Length == 49 &&
            row.Fields[2] == IdentityEventKind.RecipeTransferAuthorized.ToString()).ToArray();
        AuditChainDatabase.Require(matches.Length == 1, "RecipeTransferAuthorizationAuditMismatch");
        var identity = matches[0];
        var revisionText = identity.Fields.Length > 35 ? identity.Fields[35] : null;
        var revisionValid = long.TryParse(revisionText, NumberStyles.None,
            CultureInfo.InvariantCulture, out var revision);
        AuditChainDatabase.Require(identity.Fields.Length == 49 &&
            identity.Fields[2] == IdentityEventKind.RecipeTransferAuthorized.ToString() &&
            identity.Fields[42] == value.OperationId.ToString("D") &&
            identity.Fields[4] is { Length: > 0 } && identity.Fields[37] is { Length: 64 } &&
            AuditCanonical.IsHash(identity.Fields[37]) && identity.Fields[27] is { Length: > 0 } &&
            identity.Fields[28] is { Length: > 0 } && identity.Fields[29] is { Length: 64 } &&
            AuditCanonical.IsHash(identity.Fields[29]) &&
            revisionValid &&
            revision >= 0, "RecipeTransferAuthorizationAuditMismatch");
        var policy = new RecipeContractReference(identity.Fields[27]!, identity.Fields[28]!, identity.Fields[29]!);
        AuditChainDatabase.Require(IdentityAuditEvent.MatchesRecipeTransferAuthorization(identity.Payload,
            identity.IdentityPosition, identity.Fields[4]!, fact!, identity.Fields[37]!, value.PrincipalId,
            value.SessionId, revision, policy), "RecipeTransferAuthorizationAuditMismatch");
        return (revision, identity.Fields[37]!);
    }

    private static void VerifyRecipeTransferEventBinding(sqlite3 database, TransferEventRow value,
        long authorizationRevision, string authorizationTarget, StoreDeadline deadline)
    {
        byte[]? expected = null;
        if (value.Kind == RecipeTransferEventKind.TrustStoreReplaced)
        {
            var rows = AuditChainDatabase.Read(database, @"
                SELECT Version,OperationId,PrincipalId,SessionId,RecordedAtUtc,SignersJson
                FROM recipe_transfer_trust_versions WHERE OperationId=? LIMIT 2;", deadline,
                ReadTrustRow, value.OperationId.ToString("D"));
            AuditChainDatabase.Require(rows.Count == 1, "RecipeTransferTrustHistoryInvalid");
            var row = rows[0];
            var signers = DecodeSigners(row.SignersJson);
            var trust = new RecipeTrustStoreVersion(row.Version, signers, ParseGuid(row.OperationId),
                ParseGuid(row.PrincipalId), ParseGuid(row.SessionId), ParseTime(row.RecordedAtUtc));
            AuditChainDatabase.Require(trust.ContentHash == value.SubjectHash &&
                trust.PrincipalId == value.PrincipalId && trust.SessionId == value.SessionId &&
                trust.RecordedAtUtc == value.RecordedAtUtc,
                "RecipeTransferTrustHistoryInvalid");
            expected = EncodeHistoryBinding("RecipeTransferTrustBinding", value, authorizationRevision,
                authorizationTarget,
                new string?[]
                {
                    (row.Version - 1).ToString(CultureInfo.InvariantCulture),
                    row.Version.ToString(CultureInfo.InvariantCulture), trust.ContentHash,
                    EncodeSigners(trust.Signers), string.Join(";", trust.Signers.Select(item => item.ContentHash))
                });
        }
        else if (value.Kind is RecipeTransferEventKind.SigningKeyCreated or RecipeTransferEventKind.SigningKeyRetired)
        {
            var rows = AuditChainDatabase.Read(database, @"
                SELECT KeyId,SignerJson,ProtectedPrivateKeyBase64,Retired,OperationId,PrincipalId,RecordedAtUtc,ContentHash
                FROM recipe_transfer_signing_keys WHERE OperationId=? LIMIT 2;", deadline,
                statement => new TransferBindingKeyRow(SqliteNative.ColumnText(statement, 0)!,
                    SqliteNative.ColumnText(statement, 1)!, SqliteNative.ColumnText(statement, 2),
                    SqliteNative.ColumnInt64(statement, 3) != 0, ParseGuid(SqliteNative.ColumnText(statement, 4)),
                    ParseGuid(SqliteNative.ColumnText(statement, 5)), ParseTime(SqliteNative.ColumnText(statement, 6)),
                    SqliteNative.ColumnText(statement, 7)!), value.OperationId.ToString("D"));
            AuditChainDatabase.Require(rows.Count == 1, "RecipeTransferSigningKeyHistoryInvalid");
            var row = rows[0];
            var signer = DecodeSigner(row.SignerJson);
            AuditChainDatabase.Require(signer.ContentHash == value.SubjectHash &&
                signer.KeyId == row.KeyId && row.PrincipalId == value.PrincipalId &&
                row.RecordedAtUtc == value.RecordedAtUtc,
                "RecipeTransferSigningKeyHistoryInvalid");
            var created = value.Kind == RecipeTransferEventKind.SigningKeyCreated;
            AuditChainDatabase.Require(created != row.Retired, "RecipeTransferSigningKeyHistoryInvalid");
            var secretHash = row.ProtectedPrivateKeyBase64 is null ? null :
                Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(row.ProtectedPrivateKeyBase64)));
            var secretLength = row.ProtectedPrivateKeyBase64 is null ? null :
                Convert.FromBase64String(row.ProtectedPrivateKeyBase64).Length.ToString(CultureInfo.InvariantCulture);
            var keyFields = new string?[]
            {
                created ? "Created" : "Retired", signer.KeyId, signer.Scope, signer.Scheme,
                signer.PublicKeyBase64, signer.PublicKeyFingerprint,
                signer.NotBeforeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                signer.NotAfterUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                signer.ContentHash, secretHash, secretLength
            };
            if (!created) keyFields = keyFields.Append(signer.PublicKeyFingerprint).ToArray();
            expected = EncodeHistoryBinding("RecipeTransferSigningKeyBinding", value,
                authorizationRevision, authorizationTarget, keyFields);
        }
        else if (value.Kind == RecipeTransferEventKind.Imported)
        {
            var rows = AuditChainDatabase.Read(database, @"
                SELECT DraftId,FirstRevisionContentHash,SourceRecipeIdentity,SourceRevision,SourceLifecycle,
                    SourceStationId,SourceDescriptorContentHash,SourceSnapshotHash,PackageContentHash,PackageBytesHash,
                    SignerKeyId,SignerFingerprint,
                    SignatureScheme,SignatureBase64,TrustStoreVersion,TrustStoreContentHash,OperationId,
                    PrincipalId,SessionId,AuthorizationRevision,RecordedAtUtc
                FROM recipe_transfer_import_provenance WHERE OperationId=? LIMIT 2;", deadline,
                ReadImportRow, value.OperationId.ToString("D"));
            AuditChainDatabase.Require(rows.Count == 1, "RecipeTransferImportHistoryInvalid");
            var provenance = rows[0];
            AuditChainDatabase.Require(provenance.OperationId == value.OperationId &&
                provenance.PrincipalId == value.PrincipalId && provenance.SessionId == value.SessionId &&
                provenance.RecordedAtUtc == value.RecordedAtUtc,
                "RecipeTransferImportHistoryInvalid");
            var draftRows = ReadRecipeDraftRow(database, "WHERE DraftId=? AND Revision=1 LIMIT 2", deadline,
                provenance.DraftId.ToString("D"));
            RecipeDraftContent? content = null;
            var contentValid = draftRows.Count == 1 &&
                RecipeDraftStorageCodec.TryDecodeContent(draftRows[0].PayloadJson, draftRows[0].PayloadHash,
                    out content, out _) && content is not null;
            AuditChainDatabase.Require(contentValid &&
                draftRows[0].RevisionContentHash == provenance.FirstRevisionContentHash,
                "RecipeTransferImportDraftBindingMismatch");
            expected = EncodeHistoryBinding("RecipeTransferImportBinding", value, authorizationRevision,
                authorizationTarget,
                ProvenanceFields(provenance).Concat(new[] { content!.ContentHash, draftRows[0].PayloadHash }));
        }

        if (expected is not null)
            AuditChainDatabase.Require(Convert.ToHexString(SHA256.HashData(expected)) == value.BindingHash &&
                Convert.ToBase64String(expected) == value.BindingPayload,
                "RecipeTransferBindingSemanticMismatch");
        else
            AuditChainDatabase.Require(value.BindingPayload.Length > 0,
                "RecipeTransferBindingSemanticMismatch");
    }

    private static byte[] EncodeHistoryBinding(string kind, TransferEventRow value,
        long authorizationRevision, string authorizationTarget, IEnumerable<string?> fields)
    {
        var common = new string?[]
        {
            value.OperationId.ToString("D"), value.PrincipalId.ToString("D"),
            value.SessionId.ToString("D"), authorizationRevision.ToString(CultureInfo.InvariantCulture),
            authorizationTarget,
            value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };
        return AuditCanonical.Encode(kind, common.Concat(fields).ToArray());
    }

    private static void ValidateRecipeTransferTrust(sqlite3 database,
        IReadOnlyList<TransferEventRow> events, RecipeTransferStoreOptions options, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Version,OperationId,PrincipalId,SessionId,RecordedAtUtc,SignersJson,ContentHash
            FROM recipe_transfer_trust_versions ORDER BY Version LIMIT ?;", deadline,
            statement => (Version: SqliteNative.ColumnInt64(statement, 0),
                OperationId: ParseGuid(SqliteNative.ColumnText(statement, 1)),
                PrincipalId: ParseGuid(SqliteNative.ColumnText(statement, 2)),
                SessionId: ParseGuid(SqliteNative.ColumnText(statement, 3)),
                RecordedAtUtc: ParseTime(SqliteNative.ColumnText(statement, 4)),
                SignersJson: SqliteNative.ColumnText(statement, 5)!,
                ContentHash: SqliteNative.ColumnText(statement, 6)!),
            (options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "RecipeTransferEntryCapacityExceeded");
        var expected = 1L;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Version == expected++ && row.OperationId != Guid.Empty &&
                row.PrincipalId != Guid.Empty && row.SessionId != Guid.Empty, "RecipeTransferTrustHistoryInvalid");
            var signers = DecodeSigners(row.SignersJson);
            var value = new RecipeTrustStoreVersion(row.Version, signers, row.OperationId,
                row.PrincipalId, row.SessionId, row.RecordedAtUtc);
            AuditChainDatabase.Require(value.ContentHash == row.ContentHash &&
                FindEvent(events, row.OperationId, RecipeTransferEventKind.TrustStoreReplaced)?.SubjectHash == row.ContentHash,
                "RecipeTransferTrustHistoryInvalid");
        }
    }

    private static void ValidateRecipeTransferSigningKeys(sqlite3 database,
        IReadOnlyList<TransferEventRow> events, RecipeTransferStoreOptions options, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,KeyId,SignerJson,ProtectedPrivateKeyBase64,Retired,OperationId,
                PrincipalId,RecordedAtUtc,ContentHash
            FROM recipe_transfer_signing_keys ORDER BY Position LIMIT ?;", deadline,
            statement => new TransferKeyRow(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1)!, SqliteNative.ColumnText(statement, 2)!,
                SqliteNative.ColumnText(statement, 3), SqliteNative.ColumnInt64(statement, 4) != 0,
                ParseGuid(SqliteNative.ColumnText(statement, 5)), ParseGuid(SqliteNative.ColumnText(statement, 6)),
                ParseTime(SqliteNative.ColumnText(statement, 7)), SqliteNative.ColumnText(statement, 8)!),
            (options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "RecipeTransferEntryCapacityExceeded");
        var expectedPosition = 1L;
        var latest = new Dictionary<string, TransferKeyRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position == expectedPosition++ && row.OperationId != Guid.Empty &&
                row.PrincipalId != Guid.Empty, "RecipeTransferSigningKeyHistoryInvalid");
            var signer = DecodeSigner(row.SignerJson);
            AuditChainDatabase.Require(signer.KeyId == row.KeyId && signer.ContentHash == row.ContentHash,
                "RecipeTransferSigningKeyHistoryInvalid");
            if (!row.Retired)
            {
                AuditChainDatabase.Require(!latest.ContainsKey(row.KeyId),
                    "RecipeTransferSigningKeyHistoryInvalid");
                AuditChainDatabase.Require(row.ProtectedPrivateKeyBase64 is { Length: > 0 },
                    "RecipeTransferSigningKeyMaterialMissing");
                try { _ = Convert.FromBase64String(row.ProtectedPrivateKeyBase64!); }
                catch (FormatException exception)
                { throw new InvalidOperationException("RecipeTransferSigningKeyMaterialInvalid", exception); }
                AuditChainDatabase.Require(FindEvent(events, row.OperationId, RecipeTransferEventKind.SigningKeyCreated)?.SubjectHash == row.ContentHash,
                    "RecipeTransferSigningKeyHistoryInvalid");
            }
            else
            {
                AuditChainDatabase.Require(row.ProtectedPrivateKeyBase64 is null &&
                    latest.TryGetValue(row.KeyId, out var prior) && !prior.Retired &&
                    prior.ContentHash == row.ContentHash &&
                    FindEvent(events, row.OperationId, RecipeTransferEventKind.SigningKeyRetired)?.SubjectHash == row.ContentHash,
                    "RecipeTransferSigningKeyHistoryInvalid");
            }
            latest[row.KeyId] = row;
        }
    }

    private static void ValidateRecipeTransferImports(sqlite3 database,
        IReadOnlyList<TransferEventRow> events, RecipeTransferStoreOptions options, StoreDeadline deadline)
    {
            var rows = AuditChainDatabase.Read(database, @"
            SELECT DraftId,FirstRevisionContentHash,SourceRecipeIdentity,SourceRevision,SourceLifecycle,
                SourceStationId,SourceDescriptorContentHash,SourceSnapshotHash,PackageContentHash,PackageBytesHash,
                SignerKeyId,SignerFingerprint,
                SignatureScheme,SignatureBase64,TrustStoreVersion,TrustStoreContentHash,OperationId,
                PrincipalId,SessionId,AuthorizationRevision,RecordedAtUtc
            FROM recipe_transfer_import_provenance ORDER BY DraftId LIMIT ?;", deadline,
            ReadImportRow, (options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "RecipeTransferEntryCapacityExceeded");
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.DraftId != Guid.Empty && AuditCanonical.IsHash(row.FirstRevisionContentHash) &&
                AuditCanonical.IsHash(row.SourceDescriptorContentHash) &&
                AuditCanonical.IsHash(row.SourceSnapshotHash) && AuditCanonical.IsHash(row.PackageContentHash) &&
                AuditCanonical.IsHash(row.PackageBytesHash) && row.SignerKeyId.Length > 0 &&
                AuditCanonical.IsHash(row.SignerFingerprint) &&
                row.SignatureScheme == RecipeTransferPackageLimits.SignatureScheme &&
                row.SignatureBase64.Length > 0 && row.TrustStoreVersion > 0 &&
                row.OperationId != Guid.Empty && row.PrincipalId != Guid.Empty && row.SessionId != Guid.Empty &&
                row.AuthorizationRevision >= 0, "RecipeTransferImportHistoryInvalid");
            var source = ReconstructProvenanceSource(row);
            AuditChainDatabase.Require(source.ContentHash == row.SourceDescriptorContentHash,
                "RecipeTransferImportSourceBindingMismatch");
            RecipeTrustStoreVersion? trust = ReadRecipeTransferTrustVersion(database, options,
                row.TrustStoreVersion, deadline);
            var trustedSigner = trust?.Signers.SingleOrDefault(item => item.KeyId == row.SignerKeyId &&
                item.PublicKeyFingerprint == row.SignerFingerprint);
            AuditChainDatabase.Require(trust is not null && trust.ContentHash == row.TrustStoreContentHash &&
                trustedSigner is not null && row.SignatureBase64.Length > 0 &&
                Convert.FromBase64String(row.SignatureBase64).Length == RecipeTransferPackageLimits.SignatureBytes,
                "RecipeTransferImportTrustBindingMismatch");
            var eventRow = FindEvent(events, row.OperationId, RecipeTransferEventKind.Imported);
            AuditChainDatabase.Require(eventRow is not null && eventRow.SubjectHash == row.PackageContentHash,
                "RecipeTransferImportHistoryInvalid");
            var draft = ReadRecipeDraftRow(database, "WHERE DraftId=? AND Revision=1 LIMIT 2", deadline,
                row.DraftId.ToString("D")).SingleOrDefault();
            AuditChainDatabase.Require(draft is not null && draft.OperationId == row.OperationId &&
                draft.RevisionContentHash == row.FirstRevisionContentHash &&
                draft.AuthorPrincipalId == row.PrincipalId && draft.AuthorSessionId == row.SessionId &&
                draft.AuthorAuthorizationRevision == row.AuthorizationRevision,
                "RecipeTransferImportDraftBindingMismatch");
        }
    }

    private static IReadOnlyList<TransferIdentityRow> ReadRecipeTransferIdentityRows(sqlite3 database,
        StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Sequence,IdentityPosition,Hash,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY Sequence;", deadline,
            statement =>
            {
                var sequence = SqliteNative.ColumnInt64(statement, 0);
                var position = SqliteNative.ColumnInt64(statement, 1);
                var hash = SqliteNative.ColumnText(statement, 2)!;
                var encoded = SqliteNative.ColumnText(statement, 3)!;
                byte[] payload;
                try { payload = Convert.FromBase64String(encoded); }
                catch (FormatException exception)
                { throw new InvalidOperationException("RecipeTransferAuthorizationAuditMismatch", exception); }
                var fields = DecodeRecipeTransferIdentityFields(payload);
                AuditChainDatabase.Require(fields.Length == 49 && fields[4] is { Length: > 0 },
                    "RecipeTransferAuthorizationAuditMismatch");
                IdentityAuditEvent.VerifyPayload(payload, position, fields[4]!,
                    RecipeTransferStoreOptions.SchemaVersion);
                AuditChainDatabase.Require(AuditCanonical.IsHash(hash),
                    "RecipeTransferAuthorizationAuditMismatch");
                return new TransferIdentityRow(sequence, position, hash, payload, fields,
                    Guid.TryParseExact(fields[31], "D", out var correlation) ? correlation : Guid.Empty);
            });
        return rows;
    }

    private static string?[] DecodeRecipeTransferIdentityFields(byte[] payload)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        var versionBytes = reader.ReadBytes(4);
        AuditChainDatabase.Require(versionBytes.Length == 4 &&
            BinaryPrimitives.ReadInt32BigEndian(versionBytes) == AuditCanonical.CanonicalizationVersion,
            "RecipeTransferAuthorizationAuditMismatch");
        AuditChainDatabase.Require(reader.ReadByte() == 1, "RecipeTransferAuthorizationAuditMismatch");
        var labelLength = ReadIdentityLength(reader);
        var labelBytes = reader.ReadBytes(labelLength);
        AuditChainDatabase.Require(labelBytes.Length == labelLength &&
            new UTF8Encoding(false, true).GetString(labelBytes) == "IdentityEvent",
            "RecipeTransferAuthorizationAuditMismatch");
        var count = ReadIdentityInteger(reader);
        AuditChainDatabase.Require(count is 46 or 49, "RecipeTransferAuthorizationAuditMismatch");
        var fields = new string?[count];
        for (var index = 0; index < fields.Length; index++)
        {
            var marker = reader.ReadByte();
            if (marker == 0) continue;
            AuditChainDatabase.Require(marker == 1, "RecipeTransferAuthorizationAuditMismatch");
            var length = ReadIdentityLength(reader);
            var bytes = reader.ReadBytes(length);
            AuditChainDatabase.Require(bytes.Length == length,
                "RecipeTransferAuthorizationAuditMismatch");
            fields[index] = new UTF8Encoding(false, true).GetString(bytes);
        }
        AuditChainDatabase.Require(input.Position == input.Length,
            "RecipeTransferAuthorizationAuditMismatch");
        return fields;
    }

    private static int ReadIdentityInteger(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        AuditChainDatabase.Require(bytes.Length == 4, "RecipeTransferAuthorizationAuditMismatch");
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    private static int ReadIdentityLength(BinaryReader reader)
    {
        var length = ReadIdentityInteger(reader);
        AuditChainDatabase.Require(length is >= 0 and <= 1024, "RecipeTransferAuthorizationAuditMismatch");
        return length;
    }

    private static AuditedCommandKind ExpectedCommandKind(RecipeTransferEventKind kind) => kind switch
    {
        RecipeTransferEventKind.TrustStoreReplaced => AuditedCommandKind.ReplaceRecipeTrustStore,
        RecipeTransferEventKind.SigningKeyCreated => AuditedCommandKind.CreateRecipeSigningKey,
        RecipeTransferEventKind.SigningKeyRetired => AuditedCommandKind.RetireRecipeSigningKey,
        RecipeTransferEventKind.Exported => AuditedCommandKind.ExportRecipeTransfer,
        RecipeTransferEventKind.Imported => AuditedCommandKind.ImportRecipeTransfer,
        _ => throw new InvalidOperationException("RecipeTransferEventKindInvalid")
    };

    private static TransferEventRow? FindEvent(IReadOnlyList<TransferEventRow> events, Guid operationId,
        RecipeTransferEventKind kind) => events.SingleOrDefault(item => item.OperationId == operationId && item.Kind == kind);

    private sealed record TransferEventRow(long Position, RecipeTransferEventKind Kind, Guid OperationId,
        Guid PrincipalId, Guid SessionId, string SubjectHash, string ContentHash, string BindingHash,
        string BindingPayload, DateTimeOffset RecordedAtUtc, long CentralSequence, string CentralHash);

    private sealed record TransferIdentityRow(long Sequence, long IdentityPosition, string Hash,
        byte[] Payload, string?[] Fields, Guid CorrelationId);

    private sealed record TransferKeyRow(long Position, string KeyId, string SignerJson,
        string? ProtectedPrivateKeyBase64, bool Retired, Guid OperationId, Guid PrincipalId,
        DateTimeOffset RecordedAtUtc, string ContentHash);

    private sealed record TransferBindingKeyRow(string KeyId, string SignerJson,
        string? ProtectedPrivateKeyBase64, bool Retired, Guid OperationId, Guid PrincipalId,
        DateTimeOffset RecordedAtUtc, string ContentHash);
}
