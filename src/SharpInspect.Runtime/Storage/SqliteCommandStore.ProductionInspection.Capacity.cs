using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>One read-only production inspection capacity observation.</summary>
internal sealed record ProductionInspectionCapacityResult(
    bool Available,
    bool CanAdmit,
    string ReasonCode,
    long EntryCount,
    long PayloadBytes,
    long RemainingEntries,
    long RemainingBytes,
    long ReservedCycleEntries,
    long ReservedCyclePayloadBytes,
    long ReservedAuditEntries,
    long AuditTailSequence);

internal sealed partial class SqliteCommandStore
{
    // Admission plus the longest legal tail, including fault/recovery capacity.
    private const int ProductionInspectionCycleEntries = 9;

    /// <summary>
    /// Reads and verifies the immutable production ledger in one read-only
    /// SQLite snapshot.  The result reserves the complete normal handshake,
    /// plus the central audit control reserve, before reporting CanAdmit.
    /// </summary>
    internal async ValueTask<ProductionInspectionCapacityResult>
        ReadProductionInspectionCapacityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProductionInspectionEnabled || _databasePath is null || _policy is null ||
            _options.LocalIdentity is null || _options.ProductionInspections is null)
            return Unavailable("ProductionInspectionConfigurationRequired");

        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed)
            return Unavailable(initialized.ReasonCode);

        return await Task.Run(() => ReadProductionInspectionCapacitySnapshot(cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    private ProductionInspectionCapacityResult ReadProductionInspectionCapacitySnapshot(
        CancellationToken cancellationToken)
    {
        var production = _options.ProductionInspections!;
        production.Validate();
        var deadline = new StoreDeadline(_options.QueryTimeout);
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return Unavailable(pathReason);

        using var key = WindowsMachineAuditKey.Open(_policy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            if (schema == RecipeSelectionStoreOptions.SchemaVersion && !RecipeSelectionEnabled)
                return Unavailable("RecipeSelectionConfigurationRequired");
            if (schema == ProductionRecoveryStoreOptions.SchemaVersion && !ProductionRecoveryEnabled)
                return Unavailable("ProductionRecoveryConfigurationRequired");
            if (schema is not (ProductionInspectionStoreOptions.SchemaVersion or
                PartIdentityStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion))
                return Unavailable(schema > ProductionInspectionStoreOptions.SchemaVersion
                    ? "ProductionInspectionGovernedMigrationRequired"
                    : "ProductionInspectionConfigurationRequired");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            var verification = AuditChainDatabase.Verify(database, _policy!, key.KeyId,
                key.PublicKeyBase64,
                new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries),
                startup: false, deadline, validateAnchorReceipt: false,
                archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts,
                cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery,
                cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                releaseOptions: _options.RecipeReleases,
                contractOptions: _options.PlcResultContracts,
                activationOptions: _options.RecipeActivations,
                previewOptions: _options.PreviewSessions,
                importOptions: _options.CalibrationImports,
                manualOptions: _options.ManualInspections,
                productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionRecoveryOptions: _options.ProductionRecovery,
                productionInspectionOptions: production,
                partIdentityOptions: _options.PartIdentities,
                productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections);
            AuditChainDatabase.RequireFullProductionInspectionVerification(database,
                verification, deadline, production);
            var rows = ReadProductionInspectionRows(database, production, deadline);
            var entryCount = rows.Count;
            var payloadBytes = rows.Aggregate(0L,
                (sum, row) => checked(sum + ProductionInspectionStoredPayloadBytes(row.Payload.Length)));
            var outstanding = ReadProductionInspectionReservation(database, deadline);
            var maximumStoredPayload = ProductionInspectionStoredPayloadBytes(production.MaximumPayloadBytes);
            var remainingEntries = checked((long)production.MaximumEntries - entryCount - outstanding.Rows);
            var remainingBytes = checked(production.MaximumTotalBytes - payloadBytes - outstanding.Rows * maximumStoredPayload);
            var reservedCyclePayloadBytes = checked((long)ProductionInspectionCycleEntries *
                maximumStoredPayload);
            var reservedAuditEntries = checked(
                ProductionInspectionStoreOptions.ControlVerificationReserve +
                ProductionInspectionStoreOptions.AuditEntriesPerAdmission +
                ProductionInspectionStoreOptions.AuditEntriesPerCore +
                (ProductionInspectionCycleEntries - 2) * ProductionInspectionStoreOptions.AuditEntriesPerEvent);
            var tail = AuditChainDatabase.Tail(database, deadline);
            var remainingAuditEntries = checked((long)_policy.MaximumVerificationEntries - tail.Sequence - outstanding.AuditEntries);
            var canReserveAudit = AuditChainDatabase.CanReserveProductionInspectionAudit(database, _policy, deadline,
                checked(outstanding.AuditEntries + reservedAuditEntries -
                    ProductionInspectionStoreOptions.ControlVerificationReserve - 1));
            var canAdmit = remainingEntries >= ProductionInspectionCycleEntries &&
                remainingBytes >= reservedCyclePayloadBytes &&
                remainingAuditEntries >= reservedAuditEntries && canReserveAudit;
            var reason = canAdmit ? "ProductionInspectionCapacityAvailable" :
                remainingEntries < ProductionInspectionCycleEntries
                    ? "ProductionInspectionEntryCapacityExceeded"
                    : remainingBytes < reservedCyclePayloadBytes
                        ? "ProductionInspectionTotalCapacityExceeded"
                        : "ProductionInspectionAuditCapacityExceeded";
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new(true, canAdmit, reason, entryCount, payloadBytes, remainingEntries,
                remainingBytes, ProductionInspectionCycleEntries, reservedCyclePayloadBytes,
                reservedAuditEntries, tail.Sequence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Unavailable(SqliteAuditIntegrityQuery.FaultReason(exception,
                "ProductionInspectionCapacityUnavailable"));
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private static ProductionInspectionCapacityResult Unavailable(string reason) =>
        new(false, false, string.IsNullOrWhiteSpace(reason)
            ? "ProductionInspectionCapacityUnavailable" : reason.Trim(),
            0, 0, 0, 0, ProductionInspectionCycleEntries, 0,
            ProductionInspectionStoreOptions.ControlVerificationReserve +
            ProductionInspectionStoreOptions.AuditEntriesPerAdmission +
            ProductionInspectionStoreOptions.AuditEntriesPerCore +
            ProductionInspectionCycleEntries * ProductionInspectionStoreOptions.AuditEntriesPerEvent, 0);
}
