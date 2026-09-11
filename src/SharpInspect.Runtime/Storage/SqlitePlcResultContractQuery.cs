using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Read-only schema17 contract history. Each operation verifies the central chain and ledger on one snapshot.</summary>
public sealed class SqlitePlcResultContractQuery : IPlcResultContractQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqlitePlcResultContractQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public ValueTask<PlcResultContractReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
        ReadCoreAsync(null, cancellationToken);

    public ValueTask<PlcResultContractReadResult> ReadAsync(RecipeContractReference reference,
        CancellationToken cancellationToken = default)
    {
        if (reference is null) return ValueTask.FromResult(new PlcResultContractReadResult(false,
            "PlcResultContractReferenceRequired"));
        return ReadCoreAsync(reference, cancellationToken);
    }

    public async ValueTask<PlcResultContractPage> QueryAsync(PlcResultContractFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            var query = await RunQueryAsync(filter, cancellationToken).ConfigureAwait(false);
            return query;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Empty(false, SqliteAuditIntegrityQuery.FaultReason(exception, "PlcResultContractQueryUnavailable")); }
    }

    private async ValueTask<PlcResultContractReadResult> ReadCoreAsync(RecipeContractReference? reference,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await RunQueryAsync(new PlcResultContractFilter(0, null, 200), cancellationToken,
                reference, reference is null).ConfigureAwait(false);
            if (!page.Available) return new(false, page.ReasonCode);
            var revision = reference is null ? page.Revisions.LastOrDefault() :
                page.Revisions.SingleOrDefault(value => value.Reference == reference);
            return new(true, revision is null ? "PlcResultContractHistoryVerified" : "PlcResultContractAvailable", revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "PlcResultContractQueryUnavailable")); }
    }

    private async ValueTask<PlcResultContractPage> RunQueryAsync(PlcResultContractFilter filter,
        CancellationToken cancellationToken, RecipeContractReference? exactReference = null,
        bool readCurrent = false)
    {
        var contractOptions = _options.PlcResultContracts;
        if (contractOptions is null) return Empty(false, "PlcResultContractUnavailable");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null ||
            _options.RecipeDrafts is null || _options.RecipeReleases is null)
            return Empty(false, "PlcResultContractsRequiresDraftsReleasesIdentityAndAudit");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return Empty(false, "PlcResultContractQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) return Empty(false, "PlcResultContractQueryDeadlineExceeded");
            var query = await Task.Run(() => QueryCore(filter, deadline, cancellationToken, exactReference, readCurrent),
                CancellationToken.None).ConfigureAwait(false);
            if (query.Page.Available)
                await VerifyExternalAnchorAsync(query.Checkpoint, deadline, cancellationToken).ConfigureAwait(false);
            return query.Page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Empty(false, SqliteAuditIntegrityQuery.FaultReason(exception, "PlcResultContractQueryUnavailable")); }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryCore(PlcResultContractFilter filter, StoreDeadline deadline,
        CancellationToken cancellationToken, RecipeContractReference? exactReference, bool readCurrent)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var contractOptions = _options.PlcResultContracts ??
            throw new InvalidOperationException("PlcResultContractUnavailable");
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(Empty(false, pathReason), null);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
        var database = connection.Handle!;
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
            StationQualificationReadGuard.RequireConfiguration(schema, _options);
            AuditChainDatabase.Require(schema is PlcResultContractStoreOptions.SchemaVersion or
                RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "PlcResultContractGovernedMigrationRequired" : "StoreSchemaTooNew");
            if (_options.ManualInspections is not null && schema < ManualInspectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ManualInspectionGovernedMigrationRequired");
            if (_options.ManualInspections is null && schema == ManualInspectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ManualInspectionConfigurationRequired");
            if (_options.ProductionAdmission is not null && schema < ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionGovernedMigrationRequired");
            if (_options.ProductionAdmission is null && schema == ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionConfigurationRequired");
            if (schema == ManualInspectionStoreOptions.SchemaVersion)
                AuditChainDatabase.Require(_options.CameraSetup is not null, "CameraSetupConfigurationRequired");
            if (_options.PreviewSessions is not null && schema < PreviewSessionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("PreviewSessionGovernedMigrationRequired");
            if (_options.PreviewSessions is null &&
                (schema == PreviewSessionStoreOptions.SchemaVersion || schema == CalibrationImportStoreOptions.SchemaVersion))
                throw new InvalidOperationException("PreviewSessionConfigurationRequired");
            if (_options.CalibrationImports is not null && schema < CalibrationImportStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationImportGovernedMigrationRequired");
            if (_options.CalibrationImports is null && schema == CalibrationImportStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationImportConfigurationRequired");
            if (schema == CalibrationImportStoreOptions.SchemaVersion &&
                (_options.PreviewSessions is null || _options.CalibrationGovernance is null ||
                    _options.CalibrationSessions is null || _options.ImagingSetup is null))
                throw new InvalidOperationException("CalibrationImportsRequiresPreviewGovernanceCalibrationAndImagingSetup");
            contractOptions.Validate();
            SqliteCommandStore.RequireConfiguredPlcResultContracts(database, contractOptions, deadline);
            if (schema == CalibrationImportStoreOptions.SchemaVersion)
                SqliteCommandStore.RequireConfiguredCalibrationImports(database, _options.CalibrationImports!, deadline);
            var policy = _options.AuditIntegrityPolicy!;
            var full = new AuditVerificationRequest(0, policy.MaximumVerificationEntries);
            var verification = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
                full, false, deadline, validateAnchorReceipt: false,
                archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup, calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance, releaseOptions: _options.RecipeReleases,
                  contractOptions: contractOptions,
                  activationOptions: _options.RecipeActivations,
                  previewOptions: schema is PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                       ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion
                      ? _options.PreviewSessions : null,
                  importOptions: schema is CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                      ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion
                       ? _options.CalibrationImports : null,
                  manualOptions: schema == ManualInspectionStoreOptions.SchemaVersion ||
                       ((schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion) && _options.ManualInspections is not null)
                      ? _options.ManualInspections : null,
                  productionAdmissionOptions: schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion
                      ? _options.ProductionAdmission : null,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                partIdentityOptions: _options.PartIdentities);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
        if (_options.PlcCommunication is not null)
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification, deadline,
                _options.PlcCommunication);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
            StationQualificationReadGuard.RequireVerified(database, verification, deadline, _options);
            if (_options.AlarmPolicy is not null)
                AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (_options.AlgorithmResultArchive is not null)
                AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            if (_options.RecipeDrafts is not null)
                AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline, _options.RecipeDrafts);
            if (_options.CameraSetup is not null)
                AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline, _options.CameraSetup);
            if (_options.CameraRecovery is not null)
                AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline, _options.CameraRecovery);
            if (_options.CameraNetwork is not null)
                AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline, _options.CameraNetwork);
            if (_options.ImagingSetup is not null)
                AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline, _options.ImagingSetup);
            if (_options.CalibrationGovernance is not null)
                AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, verification, deadline,
                    _options.CalibrationGovernance);
            if (_options.RecipeReleases is not null)
                AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                    _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
            AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline, contractOptions);
             if (_options.RecipeActivations is not null)
                 AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
                     _options.RecipeActivations, _options.RecipeReleases, contractOptions,
                     _options.CalibrationGovernance);
             if (_options.PreviewSessions is not null)
                 AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline,
                     _options.PreviewSessions);
             if (_options.CalibrationImports is not null)
                 AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline,
                     _options.CalibrationImports);
              if (_options.ManualInspections is not null)
                  AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline,
                      _options.ManualInspections);
              if (_options.ProductionAdmission is not null)
                  AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                      _options.ProductionAdmission);

            var stored = SqliteCommandStore.ReadPlcResultContractRows(database, contractOptions, deadline);
            var through = stored.Count == 0 ? 0 : stored[^1].Revision.Position;
            if (filter.ThroughPosition is { } requested && requested > through)
                throw new InvalidOperationException("PlcResultContractCursorInvalid");
            var effectiveThrough = filter.ThroughPosition ?? through;
            if (effectiveThrough < filter.AfterPosition)
                throw new InvalidOperationException("PlcResultContractCursorInvalid");
            IEnumerable<SqliteCommandStore.PlcResultContractStoredEvent> selected = stored.Where(value =>
                value.Revision.Position > filter.AfterPosition && value.Revision.Position <= effectiveThrough);
            var hasNext = false;
            if (readCurrent)
                selected = stored.TakeLast(1);
            else if (exactReference is not null)
                selected = selected.Where(value => value.Revision.Reference == exactReference);
            else
            {
                var list = selected.Take(filter.PageSize + 1).ToArray();
                hasNext = list.Length > filter.PageSize;
                selected = hasNext ? list[..filter.PageSize] : list;
            }
            var revisions = new ReadOnlyCollection<PlcResultContractRevision>(selected
                .Select(value => value.Revision).ToArray());
            if (exactReference is not null && revisions.Count > 1)
                throw new InvalidOperationException("PlcResultContractReferenceDuplicate");
            long? next = hasNext && revisions.Count > 0 ? revisions[^1].Position : null;
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new(new PlcResultContractPage(true, "PlcResultContractHistoryVerified", revisions,
                effectiveThrough, next), checkpoint);
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private async ValueTask VerifyExternalAnchorAsync(AuditCheckpoint? checkpoint, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        var policy = _options.AuditIntegrityPolicy!;
        if (!policy.RequireExternalAnchor) return;
        var anchor = _options.ExternalAuditAnchor ??
            throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        if (checkpoint is null) throw new InvalidOperationException("AuditCheckpointMissing");
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("PlcResultContractQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(remaining);
        var anchorTimeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = await AuditAnchorClient.InvokeAsync(anchor,
            token => anchor.ReadLatestAsync(policy.StationId, token), anchorTimeout, lifetime.Token)
            .ConfigureAwait(false);
        AuditChainDatabase.Require(latest is not null &&
            AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest), "AuditExternalAnchorMismatch");
    }

    private sealed record QueryResult(PlcResultContractPage Page, AuditCheckpoint? Checkpoint);

    private static void ValidateFilter(PlcResultContractFilter filter)
    {
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private static PlcResultContractPage Empty(bool available, string reason) =>
        new(available, reason, new ReadOnlyCollection<PlcResultContractRevision>(Array.Empty<PlcResultContractRevision>()), 0, null);
}
