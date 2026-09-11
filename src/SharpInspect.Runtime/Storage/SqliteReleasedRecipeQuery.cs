using System.Collections.ObjectModel;
using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Independent read-only access to the schema-16 release ledger.  Resolving or
/// using this capability never creates a writer, algorithm factory, or device
/// connection; every call verifies the signed central chain and the complete
/// release projection on its own snapshot.
/// </summary>
public sealed class SqliteReleasedRecipeQuery : IReleasedRecipeQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteReleasedRecipeQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<ReleasedRecipeReadResult> ReadAsync(RecipeReference reference,
        CancellationToken cancellationToken = default)
    {
        if (reference is null) return new(false, "RecipeReleaseReferenceRequired");
        try
        {
            var query = await RunQueryAsync(new ReleasedRecipeFilter(reference.Id, 0, null, 200),
                cancellationToken, reference).ConfigureAwait(false);
            if (!query.Page.Available) return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken)
                .ConfigureAwait(false);
            var recipe = query.Page.Recipes.SingleOrDefault();
            return recipe is null
                ? new(false, "ReleasedRecipeNotFound")
                : new(true, "ReleasedRecipeAvailable", recipe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeReleaseQueryUnavailable")); }
    }

    public async ValueTask<ReleasedRecipePage> QueryAsync(ReleasedRecipeFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            var query = await RunQueryAsync(filter, cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available) return query.Page;
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken)
                .ConfigureAwait(false);
            return query.Page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Empty(false, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeReleaseQueryUnavailable")); }
    }

    private async ValueTask<QueryResult> RunQueryAsync(ReleasedRecipeFilter filter,
        CancellationToken cancellationToken, RecipeReference? exactReference = null)
    {
        var releaseOptions = _options.RecipeReleases;
        if (releaseOptions is null)
            return new(Empty(false, "RecipeReleaseUnavailable"), null, null);
        if (_options.RecipeDrafts is null || _options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            return new(Empty(false, "RecipeReleaseRequiresDraftIdentityAndAudit"), null, null);
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return new(Empty(false, "RecipeReleaseQueryCapacityExceeded"), null, null);
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) return new(Empty(false, "RecipeReleaseQueryDeadlineExceeded"), null, deadline);
            var result = await Task.Run(() => QueryCore(filter, deadline, cancellationToken, exactReference),
                CancellationToken.None).ConfigureAwait(false);
            return new(result.Page, result.Checkpoint, deadline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(Empty(false, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeReleaseQueryUnavailable")), null, null); }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryCore(ReleasedRecipeFilter filter, StoreDeadline deadline,
        CancellationToken cancellationToken, RecipeReference? exactReference = null)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var releaseOptions = _options.RecipeReleases ??
            throw new InvalidOperationException("RecipeReleaseUnavailable");
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(Empty(false, pathReason), null, null);
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
            AuditChainDatabase.Require(schema is RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "RecipeReleaseGovernedMigrationRequired" : "StoreSchemaTooNew");
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
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            StationQualificationReadGuard.RequireConfiguration(schema, _options);
            var policy = _options.AuditIntegrityPolicy!;
            var verification = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
                new AuditVerificationRequest(0, policy.MaximumVerificationEntries), false, deadline,
                validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                 releaseOptions: releaseOptions,
                 contractOptions: _options.PlcResultContracts,
                 activationOptions: _options.RecipeActivations,
                  previewOptions: schema is PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                       ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion
                      ? _options.PreviewSessions : null,
                   importOptions: schema is CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                       ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion
                       ? _options.CalibrationImports : null,
                   manualOptions: schema == ManualInspectionStoreOptions.SchemaVersion ||
                        ((schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion) && _options.ManualInspections is not null)
                       ? _options.ManualInspections : null,
                    productionAdmissionOptions: schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion
                       ? _options.ProductionAdmission : null,
                stationQualificationOptions: _options.StationQualifications,
                 recipeTransferOptions: _options.RecipeTransfers,
                 traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                productionRecoveryOptions: _options.ProductionRecovery,
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
            AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                _options.RecipeDrafts);
            if (_options.CameraSetup is not null)
                AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                    _options.CameraSetup);
            if (_options.CameraRecovery is not null)
                AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                    _options.CameraRecovery);
            if (_options.CameraNetwork is not null)
                AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                    _options.CameraNetwork);
            if (_options.ImagingSetup is not null)
                AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                    _options.ImagingSetup);
            if (_options.CalibrationGovernance is not null)
                AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, verification, deadline,
                    _options.CalibrationGovernance);
            AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                releaseOptions, _options.RecipeDrafts, _options.CalibrationGovernance);
            if (_options.PlcResultContracts is not null)
                AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                    _options.PlcResultContracts);
             if (_options.RecipeActivations is not null)
                 AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
                     _options.RecipeActivations, releaseOptions, _options.PlcResultContracts,
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

            var history = SqliteCommandStore.ReadAllRecipeDraftHistory(database, deadline);
            var policies = SqliteCommandStore.ReadCalibrationAcceptancePolicies(database,
                _options.CalibrationGovernance, deadline);
            SqliteCommandStore.ValidateRecipeReleaseHistory(database, releaseOptions,
                _options.RecipeDrafts!, policies, history, deadline, _options.CalibrationGovernance);
            var stored = SqliteCommandStore.ReadRecipeReleaseRows(database, releaseOptions, deadline);
            var through = stored.Count;
            if (filter.ThroughPosition is { } requested && requested > through)
                throw new InvalidOperationException("RecipeReleaseCursorInvalid");
            var effectiveThrough = filter.ThroughPosition ?? through;
            if (effectiveThrough < filter.AfterPosition)
                throw new InvalidOperationException("RecipeReleaseCursorInvalid");
            SqliteCommandStore.RecipeReleaseStoredEvent[] selected;
            var hasNext = false;
            if (exactReference is not null)
            {
                selected = stored.Where(value => value.Record.Position > filter.AfterPosition &&
                        value.Record.Position <= effectiveThrough &&
                        value.Record.Recipe == exactReference).ToArray();
                if (selected.Length > 1)
                    throw new InvalidOperationException("ReleasedRecipeReferenceDuplicate");
            }
            else
            {
                selected = stored.Where(value => value.Record.Position > filter.AfterPosition &&
                        value.Record.Position <= effectiveThrough &&
                        (filter.RecipeKey is null || value.Record.Recipe.Id == filter.RecipeKey))
                    .Take(filter.PageSize + 1).ToArray();
                hasNext = selected.Length > filter.PageSize;
                if (hasNext) selected = selected[..filter.PageSize];
            }
            var recipes = new ReadOnlyCollection<ReleasedRecipe>(selected
                .Select(value => new ReleasedRecipe(value.Record)).ToArray());
            long? next = hasNext && recipes.Count > 0 ? recipes[^1].Record.Position : null;
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new QueryResult(new ReleasedRecipePage(true, "ReleasedRecipeHistoryVerified", recipes,
                effectiveThrough, next), checkpoint);
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private async ValueTask VerifyExternalAnchorAsync(AuditCheckpoint? checkpoint, StoreDeadline? deadline,
        CancellationToken cancellationToken)
    {
        var policy = _options.AuditIntegrityPolicy;
        if (policy is null || !policy.RequireExternalAnchor) return;
        var anchor = _options.ExternalAuditAnchor ??
            throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        if (checkpoint is null) throw new InvalidOperationException("AuditCheckpointMissing");
        var effectiveDeadline = deadline ?? new StoreDeadline(_options.QueryTimeout);
        var remaining = effectiveDeadline.Remaining;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("RecipeReleaseQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(remaining);
        var anchorTimeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = await AuditAnchorClient.InvokeAsync(anchor,
            token => anchor.ReadLatestAsync(policy.StationId, token), anchorTimeout, lifetime.Token)
            .ConfigureAwait(false);
        AuditChainDatabase.Require(latest is not null &&
            AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest), "AuditExternalAnchorMismatch");
    }

    private static void ValidateFilter(ReleasedRecipeFilter filter)
    {
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 200 ||
            filter.RecipeKey is { Length: > 256 })
            throw new ArgumentOutOfRangeException(nameof(filter));
        if (filter.RecipeKey is { Length: 0 })
            throw new ArgumentException("RecipeReleaseRecipeKeyInvalid", nameof(filter));
    }

    private static ReleasedRecipePage Empty(bool available, string reason) => new(available, reason,
        new ReadOnlyCollection<ReleasedRecipe>(Array.Empty<ReleasedRecipe>()), 0, null);

    private sealed record QueryResult(ReleasedRecipePage Page, AuditCheckpoint? Checkpoint,
        StoreDeadline? Deadline = null);
}
