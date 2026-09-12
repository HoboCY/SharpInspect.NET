using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// Performs the same co-enabled, full-chain verification used by a cold
    /// history reader before a policy or its deployment scope is returned to a
    /// caller.  The caller owns the read transaction; this method only reads.
    /// </summary>
    internal static void VerifyTraceStoragePolicyReadGuard(sqlite3 database,
        ProductionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        var traceOptions = options.TraceStoragePolicies ?? throw new InvalidOperationException(
            "TraceStoragePolicyConfigurationRequired");
        traceOptions.Validate();
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException(
            "AuditPolicyNotConfigured");
        var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
        AuditChainDatabase.Require(schema is TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion,
            schema > ProductionRecoveryStoreOptions.SchemaVersion
                ? "TraceStoragePolicyGovernedMigrationRequired"
                : "TraceStoragePolicyConfigurationRequired");

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
            traceStoragePolicyOptions: traceOptions,
            qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication,
                productionInspectionOptions: options.ProductionInspections,
                productionRecoveryOptions: options.ProductionRecovery,
                partIdentityOptions: options.PartIdentities,
                productionArmOptions: options.ProductionArming, recipeSelectionOptions: options.RecipeSelections, recipeLifecycleOptions: options.RecipeLifecycle, imageEvidenceOptions: options.ImageEvidence);

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
        if (options.RecipeTransfers is not null)
            AuditChainDatabase.RequireFullRecipeTransferVerification(database, report, deadline,
                options.RecipeTransfers);
        AuditChainDatabase.RequireFullTraceStoragePolicyVerification(database, report, deadline,
            traceOptions);
        if (options.PlcCommunication is not null)
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, report, deadline,
                options.PlcCommunication);
    }

    internal static void VerifyTraceStoragePolicyActivationPayload(sqlite3 database, byte[] payload,
        TraceStoragePolicyStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        AuditChainDatabase.Require(payload.Length > 0 && payload.Length <= options.MaximumPayloadBytes &&
            payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "TraceStoragePolicyActivationPayloadMismatch");
    }

    internal static void VerifyTraceStoragePolicyAuditPayload(sqlite3 database, long position,
        byte[] payload, TraceStoragePolicyStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        AuditChainDatabase.Require(position > 0 && payload.Length > 0 &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= TraceStoragePolicyStoreOptions.MaximumPayloadBytesHardLimit,
            "TraceStoragePolicyAuditPayloadInvalid");
        var row = ReadTraceStoragePolicyRow(database, position, options, deadline);
        var expected = TraceStoragePolicyStorageCodec.EncodeEventPayload(row.Position,
            row.Publication, row.SnapshotHash, row.CommandSequence, row.CommandHash,
            row.IdentitySequence, row.IdentityHash, row.RecordedAtUtc, row.PublicationBytes);
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(expected) &&
            row.PayloadHash == Convert.ToHexString(SHA256.HashData(payload)),
            "TraceStoragePolicyPayloadBindingMismatch");

        var central = AuditChainDatabase.Read(database, @"
            SELECT Kind,TraceStoragePolicyPosition,Payload,Hash,Sequence
            FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0),
                Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2),
                Hash: SqliteNative.ColumnText(statement, 3),
                Sequence: SqliteNative.ColumnInt64(statement, 4)),
            row.CentralSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(central.Kind == "TraceStoragePolicyEvent" &&
            central.Sequence == row.CentralSequence && central.Position == row.Position &&
            central.Hash == row.CentralHash && central.Payload == Convert.ToBase64String(payload),
            "TraceStoragePolicyCentralBindingMismatch");
    }

    internal static void ValidateTraceStoragePolicyHistory(sqlite3 database,
        TraceStoragePolicyStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RequireConfiguredTraceStoragePolicies(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,Version,OperationId,PrincipalId,SessionId,AuthorizationRevision,
                StepUpGrantId,PolicyHash,PublicationHash,SnapshotHash,RecordedAtUtc,PublicationPayload,
                CommandSequence,CommandHash,IdentitySequence,IdentityHash,CentralSequence,CentralHash,PayloadHash,
                PolicyId,PolicyVersion,PreviousContentHash
            FROM trace_storage_policy_events ORDER BY Position LIMIT ?;", deadline,
            statement => ReadStoredRow(statement, options),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "TraceStoragePolicyEntryCapacityExceeded");
        var totalBytes = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0)
            FROM audit_entries WHERE TraceStoragePolicyPosition IS NOT NULL;", deadline);
        AuditChainDatabase.Require(totalBytes <= options.MaximumTotalBytes,
            "TraceStoragePolicyTotalCapacityExceeded");

        var seenPolicyVersions = new HashSet<string>(StringComparer.Ordinal);
        var previous = (string?)null;
        var expectedPosition = 1L;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position == expectedPosition++ &&
                row.Publication.Version == row.Position &&
                (row.Publication.PreviousContentHash == previous),
                "TraceStoragePolicyPositionGap");
            var policyKey = row.Publication.Policy.PolicyId + "\u001f" + row.Publication.Policy.Version;
            AuditChainDatabase.Require(seenPolicyVersions.Add(policyKey),
                "TraceStoragePolicyVersionReuse");
            var centralPayload = AuditChainDatabase.Read(database,
                "SELECT Payload FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
                statement => SqliteNative.ColumnText(statement, 0),
                row.CentralSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(centralPayload is not null,
                "TraceStoragePolicyCentralBindingMismatch");
            byte[] payload;
            try { payload = Convert.FromBase64String(centralPayload!); }
            catch (FormatException exception)
            { throw new InvalidOperationException("TraceStoragePolicyCentralBindingMismatch", exception); }
            VerifyTraceStoragePolicyAuditPayload(database, row.Position, payload, options, deadline);
            VerifyTraceStoragePolicyAuthorization(database, row, deadline);
            previous = row.Publication.ContentHash;
        }
    }

    private static void VerifyTraceStoragePolicyAuthorization(sqlite3 database,
        TraceStoragePolicyStoredRow row, StoreDeadline deadline)
    {
        var commandEntry = AuditChainDatabase.Read(database, @"
            SELECT Kind,FactPosition,Hash FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0),
                FactPosition: SqliteNative.ColumnInt64Nullable(statement, 1),
                Hash: SqliteNative.ColumnText(statement, 2)),
            row.CommandSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(commandEntry.Kind == "CommandFact" &&
            commandEntry.FactPosition is > 0 && commandEntry.Hash == row.CommandHash,
            "TraceStoragePolicyCommandAuditMismatch");
        var attempt = AuditChainDatabase.Read(database,
            "SELECT AttemptId FROM command_facts WHERE Position=? LIMIT 2;", deadline,
            statement => ParseGuid(SqliteNative.ColumnText(statement, 0)),
            commandEntry.FactPosition!.Value.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(attempt != Guid.Empty,
            "TraceStoragePolicyCommandAuditMismatch");
        var fact = ReadFact(database, attempt, 1, deadline);
        AuditChainDatabase.Require(fact is not null && fact.CorrelationId == row.Publication.OperationId &&
            fact.CommandKind == AuditedCommandKind.PublishTraceStoragePolicy &&
            fact.Phase == CommandAuditPhase.Outcome && fact.Disposition == CommandDisposition.Accepted &&
            fact.ReasonCode == "TraceStoragePolicyAuthorized" &&
            fact.ClaimedPrincipalId == row.Publication.PrincipalId.ToString("D") &&
            fact.ClaimedSessionId == row.Publication.SessionId &&
            fact.ClaimedStepUpGrantId == row.Publication.StepUpGrantId &&
            fact.AuthenticatedHumanPrincipalId == row.Publication.PrincipalId.ToString("D"),
            "TraceStoragePolicyCommandAuditMismatch");

        var identityEntry = AuditChainDatabase.Read(database, @"
            SELECT Kind,IdentityPosition,Hash,Payload FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0),
                IdentityPosition: SqliteNative.ColumnInt64Nullable(statement, 1),
                Hash: SqliteNative.ColumnText(statement, 2),
                Payload: SqliteNative.ColumnText(statement, 3)),
            row.IdentitySequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(identityEntry.Kind == "IdentityEvent" &&
            identityEntry.IdentityPosition is > 0 && identityEntry.Hash == row.IdentityHash &&
            identityEntry.Payload is { Length: > 0 },
            "TraceStoragePolicyAuthorizationAuditMismatch");
        byte[] identityPayload;
        try { identityPayload = Convert.FromBase64String(identityEntry.Payload!); }
        catch (FormatException exception)
        { throw new InvalidOperationException("TraceStoragePolicyAuthorizationAuditMismatch", exception); }
        var fields = DecodeRecipeTransferIdentityFields(identityPayload);
        AuditChainDatabase.Require(fields.Length == 49 && fields[4] is { Length: > 0 },
            "TraceStoragePolicyAuthorizationAuditMismatch");
        IdentityAuditEvent.VerifyPayload(identityPayload, identityEntry.IdentityPosition!.Value,
            fields[4] ?? throw new InvalidOperationException("TraceStoragePolicyAuthorizationAuditMismatch"),
            TraceStoragePolicyStoreOptions.SchemaVersion);
        AuditChainDatabase.Require(fields[2] == IdentityEventKind.TraceStoragePolicyAuthorized.ToString() &&
            fields[31] == row.Publication.OperationId.ToString("D") && fields[5] == row.Publication.PrincipalId.ToString("D") &&
            fields[25] == row.Publication.SessionId.ToString("D") && fields[32] == row.Publication.StepUpGrantId.ToString("D"),
            "TraceStoragePolicyAuthorizationAuditMismatch");
        AuditChainDatabase.Require(long.TryParse(fields[35], NumberStyles.None, CultureInfo.InvariantCulture,
            out var revision) && revision == row.Publication.AuthorizationRevision &&
            fields[37] is { Length: 64 } && AuditCanonical.IsHash(fields[37]) &&
            fields[27] is { Length: > 0 } && fields[28] is { Length: > 0 } &&
            fields[29] is { Length: 64 } && AuditCanonical.IsHash(fields[29]),
            "TraceStoragePolicyAuthorizationAuditMismatch");
        var policy = new RecipeContractReference(fields[27]!, fields[28]!, fields[29]!);
        AuditChainDatabase.Require(IdentityAuditEvent.MatchesTraceStoragePolicyAuthorization(identityPayload,
            identityEntry.IdentityPosition.Value, fields[4]!, fact!, fields[37]!,
            row.Publication.PrincipalId, row.Publication.SessionId,
            row.Publication.AuthorizationRevision, policy),
            "TraceStoragePolicyAuthorizationAuditMismatch");
    }
}
