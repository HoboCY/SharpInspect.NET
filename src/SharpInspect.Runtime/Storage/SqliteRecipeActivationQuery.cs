using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Read-only access to the schema-18 activation ledger.  Each request opens one
/// query-only snapshot, verifies the central chain and every configured ledger,
/// then projects the result before committing that snapshot.
/// </summary>
public sealed class SqliteRecipeActivationQuery : IRecipeActivationQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteRecipeActivationQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<RecipeActivationReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = await RunQueryAsync(new RecipeActivationFilter(PageSize: 1), null, true,
                cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken)
                .ConfigureAwait(false);
            return query.Current ?? new(true, "RecipeActivationNoActiveRecord");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "RecipeActivationQueryUnavailable"));
        }
    }

    public async ValueTask<RecipeActivationReadResult> ReadAsync(RecipeActivationReference reference,
        CancellationToken cancellationToken = default)
    {
        if (reference is null)
            return new(false, "RecipeActivationReferenceRequired");
        try
        {
            var query = await RunQueryAsync(new RecipeActivationFilter(PageSize: 1), reference, false,
                cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return new(false, query.Page.ReasonCode);
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken)
                .ConfigureAwait(false);
            var record = query.Page.Records.SingleOrDefault();
            return record is null
                ? new(false, "RecipeActivationNotFound")
                : new(true, "RecipeActivationAvailable", record,
                    record.RecoveryRequired ? record : null, record.RecoveryRequired);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "RecipeActivationQueryUnavailable"));
        }
    }

    public async ValueTask<RecipeActivationPage> QueryAsync(RecipeActivationFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateFilter(filter);
        try
        {
            var query = await RunQueryAsync(filter, null, false, cancellationToken).ConfigureAwait(false);
            if (!query.Page.Available)
                return query.Page;
            await VerifyExternalAnchorAsync(query.Checkpoint, query.Deadline, cancellationToken)
                .ConfigureAwait(false);
            return query.Page;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Empty(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "RecipeActivationQueryUnavailable"));
        }
    }

    private async ValueTask<QueryResult> RunQueryAsync(RecipeActivationFilter filter,
        RecipeActivationReference? exactReference, bool readCurrent, CancellationToken cancellationToken)
    {
        var activationOptions = _options.RecipeActivations;
        if (activationOptions is null)
            return EmptyResult("RecipeActivationUnavailable");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null ||
            _options.CameraSetup is null || _options.RecipeDrafts is null ||
            _options.RecipeReleases is null || _options.PlcResultContracts is null)
            return EmptyResult("RecipeActivationsRequiresCameraDraftReleasePlcIdentityAndAudit");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            return EmptyResult("RecipeActivationQueryCapacityExceeded");
        }
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered)
                return new(Empty(false, "RecipeActivationQueryDeadlineExceeded"), null, null, deadline);
            var result = await Task.Run(() => QueryCore(filter, exactReference, readCurrent, deadline,
                cancellationToken), CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return EmptyResult(SqliteAuditIntegrityQuery.FaultReason(exception,
                "RecipeActivationQueryUnavailable"));
        }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private QueryResult QueryCore(RecipeActivationFilter filter, RecipeActivationReference? exactReference,
        bool readCurrent, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var activationOptions = _options.RecipeActivations ??
            throw new InvalidOperationException("RecipeActivationUnavailable");
        var draftOptions = _options.RecipeDrafts ??
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        var releaseOptions = _options.RecipeReleases ??
            throw new InvalidOperationException("RecipeReleaseConfigurationRequired");
        var contractOptions = _options.PlcResultContracts ??
            throw new InvalidOperationException("PlcResultContractConfigurationRequired");
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(Empty(false, pathReason), null, null, deadline);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
            AuditChainDatabase.Require(schema is RecipeActivationStoreOptions.SchemaVersion or
                    PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                    ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "RecipeActivationGovernedMigrationRequired" : "StoreSchemaTooNew");
            if (_options.ManualInspections is not null && schema < ManualInspectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ManualInspectionGovernedMigrationRequired");
            if (_options.ManualInspections is null && schema == ManualInspectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ManualInspectionConfigurationRequired");
            if (_options.ProductionAdmission is not null && schema < ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionGovernedMigrationRequired");
            if (_options.ProductionAdmission is null && schema == ProductionAdmissionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("ProductionAdmissionConfigurationRequired");
            if (_options.PreviewSessions is not null && schema < PreviewSessionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("PreviewSessionGovernedMigrationRequired");
            if (_options.PreviewSessions is null &&
                (schema == PreviewSessionStoreOptions.SchemaVersion || schema == CalibrationImportStoreOptions.SchemaVersion))
                throw new InvalidOperationException("PreviewSessionConfigurationRequired");
            if (_options.CalibrationImports is not null && schema < CalibrationImportStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationImportGovernedMigrationRequired");
            if (_options.CalibrationImports is null && schema == CalibrationImportStoreOptions.SchemaVersion)
                throw new InvalidOperationException("CalibrationImportConfigurationRequired");
            SqliteNative.ConfigureSqliteLimit(database, _options, schema);
            StationQualificationReadGuard.RequireConfiguration(schema, _options);
            activationOptions.Validate();
            SqliteCommandStore.RequireConfiguredRecipeActivations(database, activationOptions, deadline);

            var policy = _options.AuditIntegrityPolicy!;
            var verification = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
                new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false, deadline,
                validateAnchorReceipt: false,
                archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: draftOptions,
                cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery,
                cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                 releaseOptions: releaseOptions,
                 contractOptions: contractOptions,
                 activationOptions: activationOptions,
                  previewOptions: schema is PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                      ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion
                      ? _options.PreviewSessions : null,
                  importOptions: _options.CalibrationImports,
                  manualOptions: schema is ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion
                      ? _options.ManualInspections : null,
                  productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                productionRecoveryOptions: _options.ProductionRecovery,
                partIdentityOptions: _options.PartIdentities,
                productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections, recipeLifecycleOptions: _options.RecipeLifecycle, imageEvidenceOptions: _options.ImageEvidence);
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
                releaseOptions, draftOptions, _options.CalibrationGovernance);
            AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                contractOptions);
             AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
                 activationOptions, _options.RecipeReleases, _options.PlcResultContracts,
                 _options.CalibrationGovernance, recipeLifecycleOptions: _options.RecipeLifecycle);
             if (_options.PreviewSessions is not null)
                 AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline,
                     _options.PreviewSessions);
              if (_options.CalibrationImports is not null)
                  AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline, _options.CalibrationImports);
              if (_options.ManualInspections is not null)
                  AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline,
                      _options.ManualInspections);
              if (_options.ProductionAdmission is not null)
                  AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                      _options.ProductionAdmission);

            var profileResolver = SqliteCommandStore.CreateCalibrationProfileResolver(database,
                _options.CalibrationGovernance, deadline);
            var rows = SqliteCommandStore.ReadRecipeActivationRows(database, activationOptions, deadline,
                profileResolver);
            var releases = SqliteCommandStore.ReadRecipeReleaseRows(database, releaseOptions, deadline);
            var contracts = SqliteCommandStore.ReadPlcResultContractRows(database, contractOptions,
                deadline);
            var releaseRecords = releases.Select(value => value.Record).ToArray();
            var contractRecords = contracts.Select(value => value.Revision).ToArray();
            SqliteCommandStore.ValidateRecipeActivationHistory(database, activationOptions, rows,
                releaseRecords, contractRecords, deadline, _options.RecipeLifecycle);
            var records = rows.Select(value => value.Record).ToArray();
            var lifecycle = _options.RecipeLifecycle is null ? Array.Empty<RecipeLifecycleRecord>() :
                SqliteCommandStore.ReadRecipeLifecycleCommandState(database, _options, deadline).Lifecycle;
            var (current, pending) = CurrentAndPending(records, lifecycle);

            var through = records.Length == 0 ? 0 : records[^1].Position;
            if (filter.ThroughPosition is { } requested && requested > through)
                throw new InvalidOperationException("RecipeActivationCursorInvalid");
            var effectiveThrough = filter.ThroughPosition ?? through;
            if (effectiveThrough < filter.AfterPosition)
                throw new InvalidOperationException("RecipeActivationCursorInvalid");

            RecipeActivationRecord[] selected;
            var hasNext = false;
            if (exactReference is not null)
            {
                selected = records.Where(value => value.Position > filter.AfterPosition &&
                    value.Position <= effectiveThrough && value.Reference == exactReference).ToArray();
                if (selected.Length > 1)
                    throw new InvalidOperationException("RecipeActivationReferenceDuplicate");
            }
            else if (readCurrent)
            {
                selected = current is null ? Array.Empty<RecipeActivationRecord>() : new[] { current };
            }
            else
            {
                selected = records.Where(value => value.Position > filter.AfterPosition &&
                        value.Position <= effectiveThrough).Take(filter.PageSize + 1).ToArray();
                hasNext = selected.Length > filter.PageSize;
                if (hasNext)
                    selected = selected[..filter.PageSize];
            }
            var page = new RecipeActivationPage(true, "RecipeActivationHistoryVerified",
                new ReadOnlyCollection<RecipeActivationRecord>(selected), effectiveThrough,
                hasNext && selected.Length > 0 ? selected[^1].Position : null,
                new ReadOnlyCollection<RecipeActivationRecord>(pending.ToArray()));
            RecipeActivationReadResult? currentResult = null;
            if (readCurrent)
            {
                var reason = pending.Count > 0 ? "RecipeActivationRecoveryRequired" :
                    current is null ? "RecipeActivationNoActiveRecord" : "RecipeActivationCurrentAvailable";
                currentResult = new RecipeActivationReadResult(true, reason, current,
                    pending.LastOrDefault(), pending.Count > 0);
            }
            else if (exactReference is not null && selected.Length == 0)
            {
                page = page with { ReasonCode = "RecipeActivationHistoryVerified" };
            }
            var checkpoint = AuditChainDatabase.LatestCheckpoint(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new(page, currentResult, checkpoint, deadline);
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private static (RecipeActivationRecord? Current, IReadOnlyList<RecipeActivationRecord> Pending)
        CurrentAndPending(IReadOnlyList<RecipeActivationRecord> records, IReadOnlyList<RecipeLifecycleRecord> lifecycle)
    {
        var current = RecipeLifecycleProjection.EffectiveCurrent(records, lifecycle);
        var admissions = records.Where(value => value.Outcome.State == RecipeActivationOutcomeState.Admitted)
            .ToDictionary(value => value.Reference, value => value);
        var terminalReferences = records.Where(value => value.IsTerminal)
            .Select(value => value.AdmissionReference).Where(value => value is not null)
            .Select(value => value!).ToHashSet();
        var pending = admissions.Values.Where(value => !terminalReferences.Contains(value.Reference))
            .OrderBy(value => value.Position).ToArray();
        return (current, pending);
    }

    private async ValueTask VerifyExternalAnchorAsync(AuditCheckpoint? checkpoint, StoreDeadline? deadline,
        CancellationToken cancellationToken)
    {
        var policy = _options.AuditIntegrityPolicy;
        if (policy is null || !policy.RequireExternalAnchor)
            return;
        var anchor = _options.ExternalAuditAnchor ??
            throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        if (checkpoint is null)
            throw new InvalidOperationException("AuditCheckpointMissing");
        var effectiveDeadline = deadline ?? new StoreDeadline(_options.QueryTimeout);
        var remaining = effectiveDeadline.Remaining;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("RecipeActivationQueryDeadlineExceeded");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(remaining);
        var anchorTimeout = remaining < policy.AnchorTimeout ? remaining : policy.AnchorTimeout;
        var latest = await AuditAnchorClient.InvokeAsync(anchor,
            token => anchor.ReadLatestAsync(policy.StationId, token), anchorTimeout, lifetime.Token)
            .ConfigureAwait(false);
        AuditChainDatabase.Require(latest is not null &&
            AuditChainDatabase.ReceiptMatches(policy, checkpoint, latest), "AuditExternalAnchorMismatch");
    }

    private static void ValidateFilter(RecipeActivationFilter filter)
    {
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.ThroughPosition < filter.AfterPosition || filter.PageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(filter));
    }

    private static QueryResult EmptyResult(string reason) =>
        new(Empty(false, reason), null, null, null);

    private static RecipeActivationPage Empty(bool available, string reason) =>
        new(available, reason,
            new ReadOnlyCollection<RecipeActivationRecord>(Array.Empty<RecipeActivationRecord>()), 0, null,
            new ReadOnlyCollection<RecipeActivationRecord>(Array.Empty<RecipeActivationRecord>()));

    private sealed record QueryResult(RecipeActivationPage Page, RecipeActivationReadResult? Current,
        AuditCheckpoint? Checkpoint, StoreDeadline? Deadline);
}
