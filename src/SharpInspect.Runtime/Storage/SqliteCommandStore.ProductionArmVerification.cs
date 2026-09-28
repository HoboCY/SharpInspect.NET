using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>The schema-31 handshake ledgers an arm verification may resolve. They
/// stay optional at the store level and are required only when a PLC-caused
/// attempt exists.</summary>
internal sealed record ProductionArmSourceOptions(RecipeSelectionStoreOptions? Selections,
    RecipeActivationStoreOptions? Activations, CalibrationGovernanceStoreOptions? Governance,
    ProductionAdmissionStoreOptions? Admission);

/// <summary>
/// Read- and write-side verification of the schema-32 arm ledger. Every stored
/// row is decoded, re-encoded and re-bound to its signed central-audit metadata
/// entry; the attempt state machine, the source gates and the bidirectional
/// row/metadata membership are all re-proved, so a corrupted, duplicated,
/// mistransitioned or re-pointed ledger fails closed instead of projecting.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal static void VerifyProductionArmReadGuard(sqlite3 database, ProductionStoreOptions options,
        StoreDeadline deadline)
    {
        var armOptions = options.ProductionArming ??
            throw new InvalidOperationException("ProductionArmConfigurationRequired");
        armOptions.Validate();
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) is
            ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion
            or ProductionImageEvidenceStoreOptions.SchemaVersion or ProductionImageFinalizationStoreOptions.SchemaVersion,
            "ProductionArmGovernedMigrationRequired");
        using var key = WindowsMachineAuditKey.Open(policy, false, out _);
        var report = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
            new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false, deadline,
            validateAnchorReceipt: false, archiveOptions: options.AlgorithmResultArchive,
            recipeDraftOptions: options.RecipeDrafts, cameraSetupOptions: options.CameraSetup,
            cameraRecoveryOptions: options.CameraRecovery, cameraNetworkOptions: options.CameraNetwork,
            imagingSetupOptions: options.ImagingSetup, calibrationSessionOptions: options.CalibrationSessions,
            governanceOptions: options.CalibrationGovernance, releaseOptions: options.RecipeReleases,
            contractOptions: options.PlcResultContracts, activationOptions: options.RecipeActivations,
            previewOptions: options.PreviewSessions, importOptions: options.CalibrationImports,
            manualOptions: options.ManualInspections, productionAdmissionOptions: options.ProductionAdmission,
            stationQualificationOptions: options.StationQualifications, recipeTransferOptions: options.RecipeTransfers,
            traceStoragePolicyOptions: options.TraceStoragePolicies,
            qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication,
            productionInspectionOptions: options.ProductionInspections,
            productionRecoveryOptions: options.ProductionRecovery, partIdentityOptions: options.PartIdentities,
            recipeSelectionOptions: options.RecipeSelections, productionArmOptions: armOptions, recipeLifecycleOptions: options.RecipeLifecycle, imageEvidenceOptions: options.ImageEvidence, imageFinalizationOptions: options.ImageFinalization);
        AuditChainDatabase.RequireFullProductionArmVerification(database, report, deadline);
    }

    internal static byte[] EncodeProductionArmAudit(ProductionArmStoredRow row) =>
        ProductionArmStorageCodec.EncodeAuditBinding(row);

    internal static ProductionArmSourceOptions ProductionArmSource(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.RecipeSelections, options.RecipeActivations, options.CalibrationGovernance, options.ProductionAdmission);
    }

    internal static void ValidateProductionArmAuditRow(sqlite3 database, ProductionArmStoredRow row,
        StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT Kind,Payload,Hash FROM audit_entries WHERE Sequence=?
            AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND
            DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND
            CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND
            CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND
            PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND
            CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND
            ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND
            RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL AND
            QualificationCyclePosition IS NULL AND PlcCommunicationPosition IS NULL AND
            ProductionInspectionPosition IS NULL AND PartIdentityPosition IS NULL LIMIT 2;", deadline,
            value => (Kind: SqliteNative.ColumnText(value, 0), Payload: SqliteNative.ColumnText(value, 1),
                Hash: SqliteNative.ColumnText(value, 2)), N(row.AuditSequence));
        if (rows.Count != 1 || rows[0].Kind != ProductionArmEventAuditKind ||
            rows[0].Hash != row.AuditHash ||
            rows[0].Payload != Convert.ToBase64String(EncodeProductionArmAudit(row)))
            throw new InvalidOperationException("ProductionArmCentralAuditMismatch");
    }

    /// <summary>
    /// Complete re-verification of the retained arm ledger: bounded capacity,
    /// contiguous positions and predecessor hashes, the attempt state machine,
    /// every source gate and every signed metadata binding.
    /// </summary>
    internal static void ValidateProductionArmHistory(sqlite3 database, ProductionArmSourceOptions source,
        ProductionArmStoreOptions options, IReadOnlyList<ProductionArmStoredRow> rows, StoreDeadline deadline)
    {
        RequireConfiguredProductionArm(database, options, deadline);
        if (rows.Count > options.MaxEvents) throw new InvalidOperationException("ProductionArmEntryCapacityExceeded");
        if (rows.Sum(row => (long)row.Payload.Length) > options.MaxTotalBytes)
            throw new InvalidOperationException("ProductionArmTotalCapacityExceeded");
        if (checked(rows.Count + ReadProductionArmAuditReserve(database, deadline)) > options.MaxEvents)
            throw new InvalidOperationException("ProductionArmEntryCapacityExceeded");
        var events = rows.Select(row => row.Event).ToArray();
        ProductionArmStoredRow? previous = null;
        foreach (var row in rows)
        {
            SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            if (row.Event.Position != (previous?.Event.Position ?? 0) + 1 ||
                !string.Equals(row.PreviousHash, previous?.AuditHash, StringComparison.Ordinal) ||
                row.AuditSequence <= (previous?.AuditSequence ?? 0) ||
                previous is not null && row.Event.RecordedAtUtc < previous.Event.RecordedAtUtc)
                throw new InvalidOperationException("ProductionArmHistorySequenceInvalid");
            ValidateProductionArmAuditRow(database, row, deadline);
            previous = row;
        }

        foreach (var group in rows.GroupBy(row => row.Event.AttemptId))
        {
            var attempt = group.OrderBy(row => row.Event.Position).Select(row => row.Event).ToArray();
            if (attempt[0].Kind != ProductionArmEventKind.Attempted ||
                attempt.Skip(1).Any(value => value.Kind == ProductionArmEventKind.Attempted))
                throw new InvalidOperationException("ProductionArmAttemptSequenceInvalid");
            var prior = new List<ProductionArmHistoryEvent> { attempt[0] };
            ValidateProductionArmAttemptSource(attempt[0], events);
            ValidateProductionArmInputStability(Array.Empty<ProductionArmHistoryEvent>(), attempt[0]);
            if (attempt[0].Cause == ProductionArmCause.PlcActivation)
            {
                if (attempt[0].Activation is null) throw new InvalidOperationException("ProductionArmPlcActivationRequired");
                ValidateProductionArmPlcHandshake(database, source, attempt[0].RuntimeEpoch,
                    attempt[0].PlcRequest!, attempt[0].Activation!, deadline);
            }
            foreach (var value in attempt.Skip(1))
            {
                SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
                var error = ProductionArmTransitionError(prior, value.Kind, value.Reason);
                if (error is not null) throw new InvalidOperationException(error);
                ValidateProductionArmContext(prior, value);
                ValidateProductionArmReadyReceipt(prior, value);
                ValidateProductionArmInputStability(prior, value);
                if (value.Kind == ProductionArmEventKind.Authorized)
                {
                    ValidateProductionArmHistoricalAuthorization(database, source, value, events, deadline);
                }
                else if (value.Kind == ProductionArmEventKind.ReadyConfirmed)
                {
                    var authorized = prior.Single(candidate =>
                        candidate.Kind == ProductionArmEventKind.Authorized);
                    if (!string.Equals(value.MaintenanceHeadHash, authorized.MaintenanceHeadHash,
                            StringComparison.Ordinal) ||
                        !ProductionArmHeads.Equal(value.CurrentDurableHeads, authorized.CurrentDurableHeads))
                        throw new InvalidOperationException("ProductionArmMaintenanceEvidenceChanged");
                    if (value.Report is not null && value.Report.ContentHash != authorized.Report?.ContentHash)
                        throw new InvalidOperationException("ProductionArmReportChanged");
                    if (value.Cause == ProductionArmCause.PlcActivation)
                        ValidateProductionArmPlcHandshake(database, source, value.RuntimeEpoch,
                            value.PlcRequest!, value.Activation!, deadline);
                }
                prior.Add(value);
            }
        }
    }

    private static void ValidateProductionArmAttemptSource(ProductionArmHistoryEvent attempted,
        IReadOnlyList<ProductionArmHistoryEvent> events)
    {
        if (attempted.Kind != ProductionArmEventKind.Attempted)
            throw new InvalidOperationException("ProductionArmAttemptSequenceInvalid");
        var duplicates = events.Where(value => value.Kind == ProductionArmEventKind.Attempted &&
            value.Position != attempted.Position);
        switch (attempted.Cause)
        {
            case ProductionArmCause.Startup:
                if (duplicates.Any(value => value.Cause == ProductionArmCause.Startup &&
                        value.RuntimeEpoch == attempted.RuntimeEpoch))
                    throw new InvalidOperationException("ProductionArmStartupEpochReused");
                break;
            case ProductionArmCause.PlcActivation:
                if (duplicates.Any(value => value.Cause == ProductionArmCause.PlcActivation && string.Equals(
                        value.PlcRequest?.RequestIdentityHash, attempted.PlcRequest!.RequestIdentityHash,
                        StringComparison.Ordinal)))
                    throw new InvalidOperationException("ProductionArmPlcRequestReused");
                break;
            default:
                if (duplicates.Any(value => value.HumanCommandId == attempted.HumanCommandId))
                    throw new InvalidOperationException("ProductionArmHumanCommandReused");
                break;
        }
    }

    /// <summary>
    /// Historical authorization evidence for one retained Authorized row. The
    /// fixed-gate report, maintenance head, bounded heads, exact PLC handshake,
    /// or the completed human command remain immutable and are re-proved here;
    /// only the live durable-head re-read belongs exclusively to the writer.
    /// </summary>
    private static void ValidateProductionArmHistoricalAuthorization(sqlite3 database,
        ProductionArmSourceOptions source, ProductionArmHistoryEvent value,
        IReadOnlyList<ProductionArmHistoryEvent> events, StoreDeadline deadline)
    {
        if (value.Report is null || !value.Report.CanArm)
            throw new InvalidOperationException("ProductionArmAdmissionGateBlocked");
        if (value.Report.RuntimeEpoch != value.RuntimeEpoch ||
            value.Report.AdmissionGeneration != value.AdmissionGeneration)
            throw new InvalidOperationException("ProductionArmReportContextMismatch");
        if (value.MaintenanceHeadHash is null)
            throw new InvalidOperationException("ProductionArmMaintenanceEvidenceUnavailable");
        if (!ProductionArmHeads.Equal(value.ExpectedDurableHeads, value.CurrentDurableHeads))
            throw new InvalidOperationException("ProductionArmDurableHeadsMismatch");
        if (value.Cause == ProductionArmCause.Startup)
        {
            if (events.Any(candidate => candidate.Position != value.Position &&
                    candidate.Cause == ProductionArmCause.Startup &&
                    candidate.RuntimeEpoch == value.RuntimeEpoch &&
                    candidate.Kind == ProductionArmEventKind.Authorized))
                throw new InvalidOperationException("ProductionArmStartupEpochReused");
            return;
        }
        if (value.Cause == ProductionArmCause.PlcActivation)
        {
            ValidateProductionArmPlcHandshake(database, source, value.RuntimeEpoch, value.PlcRequest!,
                value.Activation!, deadline);
            return;
        }
        ValidateProductionArmHumanAuthorization(database, source.Admission, value.HumanCommandId!.Value,
            value.HumanPrincipalId!.Value, value.HumanSessionId!.Value, value.RuntimeEpoch,
            value.AdmissionGeneration, value.Report, deadline);
    }

    private static void ValidateProductionArmContext(IReadOnlyList<ProductionArmHistoryEvent> prior, ProductionArmHistoryEvent value)
    {
        var first = prior[0];
        if (first.AttemptId != value.AttemptId || first.RuntimeEpoch != value.RuntimeEpoch || first.Cause != value.Cause ||
            first.StationId != value.StationId || first.StartupPolicy != value.StartupPolicy ||
            first.PostActivationPolicy != value.PostActivationPolicy || first.DeploymentHash != value.DeploymentHash ||
            first.PlcRequest?.ContentHash != value.PlcRequest?.ContentHash || first.Activation != value.Activation ||
            first.MaintenanceHeadHash != value.MaintenanceHeadHash || value.AdmissionGeneration < first.AdmissionGeneration ||
            first.HumanCommandId != value.HumanCommandId || first.HumanPrincipalId != value.HumanPrincipalId ||
            first.HumanSessionId != value.HumanSessionId)
            throw new InvalidOperationException("ProductionArmAttemptContextChanged");
        if (prior.LastOrDefault(item => item.Kind == ProductionArmEventKind.Authorized) is { } authorized &&
            (authorized.AdmissionGeneration != value.AdmissionGeneration || authorized.Report?.ContentHash != value.Report?.ContentHash))
            throw new InvalidOperationException("ProductionArmAuthorizedContextChanged");
        if (value.StatusDelivery && prior[^1].ReadyReceipt?.ContentHash != value.ReadyReceipt?.ContentHash)
            throw new InvalidOperationException("ProductionArmStatusReceiptChanged");
    }

    private static void ValidateProductionArmReadyReceipt(IReadOnlyList<ProductionArmHistoryEvent> prior, ProductionArmHistoryEvent value)
    {
        if (value.ReadyReceipt is not { } receipt)
        {
            if (value.Kind == ProductionArmEventKind.ReadyConfirmed)
                throw new InvalidOperationException("ProductionArmPhysicalReadyReceiptRequired");
            return;
        }
        var authorized = prior.SingleOrDefault(item => item.Kind == ProductionArmEventKind.Authorized);
        if (value.Kind is ProductionArmEventKind.Attempted or ProductionArmEventKind.Authorized or ProductionArmEventKind.Rejected ||
            authorized is null || receipt.AttemptId != value.AttemptId || receipt.RuntimeEpoch != value.RuntimeEpoch ||
            receipt.AuthorizationEventHash != authorized.ContentHash || receipt.Activation != authorized.Activation ||
            receipt.MaintenanceHeadHash != authorized.MaintenanceHeadHash ||
            receipt.AdmissionGeneration != authorized.AdmissionGeneration || receipt.ObservedAtUtc < authorized.RecordedAtUtc ||
            receipt.ObservedAtUtc > value.RecordedAtUtc ||
            value.PlcRequest is { } plc && receipt.ControllerEpoch != plc.ControllerEpoch)
            throw new InvalidOperationException("ProductionArmReadyReceiptContextMismatch");
    }

    private static void ValidateProductionArmInputStability(IReadOnlyList<ProductionArmHistoryEvent> prior, ProductionArmHistoryEvent value)
    {
        if ((value.Kind == ProductionArmEventKind.Attempted || value.Cause == ProductionArmCause.ManualMaintenanceArm) &&
            value.InputStability is not null)
            throw new InvalidOperationException("ProductionArmInputStabilitySourceConflict");
        if (value.Kind == ProductionArmEventKind.Authorized && value.Cause != ProductionArmCause.ManualMaintenanceArm &&
            value.InputStability is null)
            throw new InvalidOperationException("ProductionArmInputStabilityRequired");
        if (prior.LastOrDefault(item => item.Kind == ProductionArmEventKind.Authorized) is { } authorized &&
            authorized.InputStability?.ContentHash != value.InputStability?.ContentHash)
            throw new InvalidOperationException("ProductionArmInputStabilityChanged");
    }

}
