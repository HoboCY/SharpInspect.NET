using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private const string IdentitySchemaSql = @"
        CREATE TABLE identity_policy_binding(Id INTEGER PRIMARY KEY CHECK(Id=1), StationId TEXT NOT NULL,
            InstallationKeyId TEXT NOT NULL, PolicyContentHash TEXT NOT NULL);
        CREATE TABLE identity_authority(Id INTEGER PRIMARY KEY CHECK(Id=1), Revision INTEGER NOT NULL CHECK(Revision>=0),
            ProtectedState TEXT NOT NULL, LastAuditSequence INTEGER NOT NULL, StateSignature TEXT NOT NULL);
        CREATE TABLE recovery_operations(
            OperationId TEXT NOT NULL PRIMARY KEY CHECK(length(OperationId)=36),
            Kind TEXT NOT NULL CHECK(length(Kind)>0 AND length(Kind)<=64),
            Succeeded INTEGER NOT NULL CHECK(Succeeded=1),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0 AND length(ReasonCode)<=128),
            KitId TEXT NULL CHECK(KitId IS NULL OR length(KitId)=36),
            PrincipalId TEXT NULL CHECK(PrincipalId IS NULL OR length(PrincipalId)=36),
            DeliveryCommitted INTEGER NOT NULL CHECK(DeliveryCommitted IN (0,1)),
            StateRevision INTEGER NOT NULL CHECK(StateRevision>=0),
            IdentitySequence INTEGER NOT NULL CHECK(IdentitySequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            RecordHash TEXT NOT NULL CHECK(length(RecordHash)=64));
        CREATE TRIGGER identity_policy_immutable_update BEFORE UPDATE ON identity_policy_binding BEGIN SELECT RAISE(ABORT,'ImmutableIdentityPolicy'); END;
        CREATE TRIGGER identity_policy_immutable_delete BEFORE DELETE ON identity_policy_binding BEGIN SELECT RAISE(ABORT,'ImmutableIdentityPolicy'); END;
        CREATE TRIGGER identity_authority_no_delete BEFORE DELETE ON identity_authority BEGIN SELECT RAISE(ABORT,'IdentityAuthorityRequired'); END;
        CREATE TRIGGER recovery_operations_immutable_update BEFORE UPDATE ON recovery_operations BEGIN SELECT RAISE(ABORT,'ImmutableRecoveryOperation'); END;
        CREATE TRIGGER recovery_operations_immutable_delete BEFORE DELETE ON recovery_operations BEGIN SELECT RAISE(ABORT,'ImmutableRecoveryOperation'); END;";

    private void InitializeIdentitySchema(sqlite3 database, StoreDeadline deadline)
    {
        SqliteNative.Execute(database, IdentitySchemaSql, deadline);
        var identity = _options.LocalIdentity!;
        AuditChainDatabase.Execute(database, "INSERT INTO identity_policy_binding VALUES(1,?,?,?);", deadline,
            identity.StationId, _signingKey!.KeyId, identity.PolicyContentHash);
        var state = new IdentityAuthorityState { StationId = identity.StationId, InstallationKeyId = _signingKey.KeyId,
            PolicyContentHash = identity.PolicyContentHash };
        var fact = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.IdentityConfigured, DateTimeOffset.UtcNow,
            state.StationId, null, null, null, null, "IdentityBootstrapRequired", PasswordPolicyVersion: identity.PasswordPolicy.Version,
            BlocklistId: identity.PasswordPolicy.Blocklist!.Id, BlocklistVersion: identity.PasswordPolicy.Blocklist.Version,
            HashBaselineVersion: identity.HashBaselineVersion, HashTargetCost: identity.Baseline.TargetIterations);
        var sequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey, BindAuthenticationPolicy(fact), deadline);
        state.LastIdentityAuditHash = AuditChainDatabase.Tail(database, deadline).Hash;
        var protectedState = IdentityStateProtection.Protect(state);
        AuditChainDatabase.Execute(database, "INSERT INTO identity_authority VALUES(1,0,?,?,?);", deadline,
            protectedState, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision, sequence, _signingKey));
    }

    internal async ValueTask<IdentityAuthorityState> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || _options.LocalIdentity is null || _signingKey is null)
            throw new InvalidOperationException("IdentityStoreUnavailable");
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath!, true);
            var database = connection.Handle!;
            SqliteNative.ConfigureSqliteLimit(database, _options);
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            var alarmStore = _options.AlarmPolicy is not null;
            var archiveStore = _options.AlgorithmResultArchive is not null;
            var draftStore = _options.RecipeDrafts is not null;
            var cameraStore = _options.CameraSetup is not null;
            var recoveryStore = _options.CameraRecovery is not null;
            var networkStore = _options.CameraNetwork is not null;
            var imagingStore = _options.ImagingSetup is not null;
            var calibrationStore = _options.CalibrationSessions is not null;
            var releaseStore = _options.RecipeReleases is not null;
            var contractStore = _options.PlcResultContracts is not null;
            var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey.KeyId,
                _signingKey.PublicKeyBase64,
                alarmStore || archiveStore || draftStore || cameraStore || recoveryStore || networkStore || imagingStore || calibrationStore || releaseStore || contractStore ? new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries) :
                    new AuditVerificationRequest(), !alarmStore && !archiveStore && !draftStore && !cameraStore && !recoveryStore && !networkStore && !imagingStore && !calibrationStore && !releaseStore && !contractStore, deadline,
                validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                 releaseOptions: _options.RecipeReleases,
                 contractOptions: _options.PlcResultContracts);
            if (alarmStore) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (_options.AlgorithmResultArchive is not null)
                AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            if (draftStore) AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                _options.RecipeDrafts);
            if (cameraStore) AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                _options.CameraSetup);
            if (recoveryStore) AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                _options.CameraRecovery);
            if (networkStore) AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                _options.CameraNetwork);
            if (imagingStore) AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                _options.ImagingSetup);
            if (releaseStore) AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
            if (contractStore) AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                _options.PlcResultContracts);
            var state = ReadIdentityState(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            return state;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one durable recovery operation only after the signed state and audit chain
    /// have been verified on the same read snapshot.  The index contains successful operations;
    /// rejected attempts intentionally have no durable idempotency slot.</summary>
    internal async ValueTask<RecoveryOperationState?> ReadRecoveryOperationAsync(Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("OperationIdRequired", nameof(operationId));
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || _options.LocalIdentity is null || _signingKey is null)
            throw new InvalidOperationException("IdentityStoreUnavailable");
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath!, true);
            var database = connection.Handle!;
            SqliteNative.ConfigureSqliteLimit(database, _options);
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            var alarmStore = _options.AlarmPolicy is not null;
            var archiveStore = _options.AlgorithmResultArchive is not null;
            var draftStore = _options.RecipeDrafts is not null;
            var cameraStore = _options.CameraSetup is not null;
            var recoveryStore = _options.CameraRecovery is not null;
            var networkStore = _options.CameraNetwork is not null;
            var imagingStore = _options.ImagingSetup is not null;
            var calibrationStore = _options.CalibrationSessions is not null;
            var releaseStore = _options.RecipeReleases is not null;
            var contractStore = _options.PlcResultContracts is not null;
            var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey.KeyId,
                _signingKey.PublicKeyBase64,
                alarmStore || archiveStore || draftStore || cameraStore || recoveryStore || networkStore || imagingStore || calibrationStore || releaseStore || contractStore ? new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries) :
                    new AuditVerificationRequest(), !alarmStore && !archiveStore && !draftStore && !cameraStore && !recoveryStore && !networkStore && !imagingStore && !calibrationStore && !releaseStore && !contractStore, deadline,
                validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                 releaseOptions: _options.RecipeReleases,
                 contractOptions: _options.PlcResultContracts);
            if (alarmStore) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (_options.AlgorithmResultArchive is not null)
                AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            if (draftStore) AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                _options.RecipeDrafts);
            if (cameraStore) AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                _options.CameraSetup);
            if (recoveryStore) AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                _options.CameraRecovery);
            if (networkStore) AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                _options.CameraNetwork);
            if (imagingStore) AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                _options.ImagingSetup);
            if (releaseStore) AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
            if (contractStore) AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                _options.PlcResultContracts);
            _ = ReadIdentityState(database, deadline);
            var operation = ReadRecoveryOperation(database, operationId, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            return operation;
        }, cancellationToken).ConfigureAwait(false);
    }

    private IdentityAuthorityState ReadIdentityState(sqlite3 database, StoreDeadline deadline)
    {
        var options = _options.LocalIdentity!;
        var binding = AuditChainDatabase.Read(database, "SELECT StationId,InstallationKeyId,PolicyContentHash FROM identity_policy_binding WHERE Id=1;",
            deadline, s => Enumerable.Range(0, 3).Select(i => SqliteNative.ColumnText(s, i)).ToArray()).SingleOrDefault();
        AuditChainDatabase.Require(binding is not null && binding.SequenceEqual(new[] { options.StationId, _signingKey!.KeyId,
            options.PolicyContentHash }), "IdentityPolicyBindingMismatch");
        var row = AuditChainDatabase.Read(database, "SELECT Revision,ProtectedState,LastAuditSequence,StateSignature FROM identity_authority WHERE Id=1;", deadline,
            s => (Revision: SqliteNative.ColumnInt64(s, 0), Protected: SqliteNative.ColumnText(s, 1)!, Sequence: SqliteNative.ColumnInt64(s, 2),
                Signature: SqliteNative.ColumnText(s, 3)!)).SingleOrDefault();
        AuditChainDatabase.Require(row.Protected is not null, "IdentityAuthorityMissing");
        IdentityStateProtection.VerifySignature(row.Protected!, row.Signature, options.StationId, row.Revision, row.Sequence, _signingKey!);
        var state = IdentityStateProtection.Unprotect(row.Protected!, options.StationId, _signingKey!.KeyId, row.Revision);
        // The policy is inside the signed ciphertext as well as the lookup binding;
        // changing a database binding and deployment settings cannot retarget old credentials.
        AuditChainDatabase.Require(state.PolicyContentHash == options.PolicyContentHash, "IdentityPolicyBindingMismatch");
        var schemaVersion = checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
        var latest = AuditChainDatabase.Read(database, "SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries WHERE IdentityPosition IS NOT NULL ORDER BY IdentityPosition DESC LIMIT 1;",
            deadline, s => (Sequence: SqliteNative.ColumnInt64(s, 0), Ordinal: SqliteNative.ColumnInt64(s, 1), Payload: SqliteNative.ColumnText(s, 2)!,
                Hash: SqliteNative.ColumnText(s, 3)!)).SingleOrDefault();
        AuditChainDatabase.Require(latest.Sequence == row.Sequence && state.LastIdentityAuditHash == latest.Hash && IdentityAuditEvent.VerifyPayload(
            Convert.FromBase64String(latest.Payload), latest.Ordinal, options.StationId, schemaVersion) == state.Revision, "IdentityAuthorityAuditMismatch");
        ValidateRecoveryOperationIndex(database, state, deadline);
        return state;
    }

    internal ValueTask<IdentityWriteResult> UpdateIdentityAsync(Func<IdentityAuthorityState, IdentityUpdate> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return EnqueueIdentityAsync(new IdentityWork(update), cancellationToken);
    }

    /// <summary>Runs a recovery mutation on the authoritative writer.  The callback receives
    /// the permanently indexed successful operation, if one exists, so retries cannot depend
    /// on a bounded encrypted-state journal.</summary>
    internal ValueTask<IdentityWriteResult> UpdateRecoveryIdentityAsync(Guid operationId,
        Func<IdentityAuthorityState, RecoveryOperationState?, IdentityUpdate> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (operationId == Guid.Empty)
            return ValueTask.FromResult(new IdentityWriteResult(false, "OperationIdRequired"));
        return EnqueueIdentityAsync(new IdentityWork(operationId, update), cancellationToken);
    }

    /// <summary>
    /// Runs a command-aware identity update on the same bounded writer queue. The callback is
    /// invoked after BEGIN IMMEDIATE and a fresh signed state read. Its second argument reports
    /// whether an accepted outcome for the correlation id already exists; it is deliberately a
    /// bounded indexed lookup rather than an in-memory history of successful commands.
    /// </summary>
    internal ValueTask<IdentityWriteResult> UpdateIdentityCommandAsync(Guid correlationId,
        Func<IdentityAuthorityState, bool, IdentityUpdate> update, CancellationToken cancellationToken,
        StoreDeadline? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (correlationId == Guid.Empty)
            return ValueTask.FromResult(new IdentityWriteResult(false, "CorrelationIdRequired"));
        return EnqueueIdentityAsync(new IdentityWork(correlationId, update), cancellationToken, deadline);
    }

    /// <summary>Runs an authenticated alarm transition against the fresh identity and alarm projections
    /// while retaining one SQLite transaction for identity events, alarm history and command facts.</summary>
    internal ValueTask<IdentityWriteResult> UpdateAlarmCommandAsync(Guid correlationId, Guid runtimeEpoch,
        Func<IdentityAuthorityState, AlarmStateSnapshot, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (correlationId == Guid.Empty)
            return ValueTask.FromResult(new IdentityWriteResult(false, "CorrelationIdRequired"));
        if (runtimeEpoch == Guid.Empty)
            return ValueTask.FromResult(new IdentityWriteResult(false, "AlarmRuntimeEpochRequired"));
        return EnqueueIdentityAsync(new IdentityWork(correlationId, runtimeEpoch, update), cancellationToken, deadline);
    }

    private async ValueTask<IdentityWriteResult> EnqueueIdentityAsync(IdentityWork work,
        CancellationToken cancellationToken, StoreDeadline? commandDeadline = null)
    {
        if (_options.LocalIdentity is null || _queue is null || _queueSlots is null || Volatile.Read(ref _disposed) != 0)
            return new(false, "IdentityStoreUnavailable");
        var deadline = commandDeadline ?? new StoreDeadline(CommitTimeout);
        var result = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline, Identity: work),
            deadline, cancellationToken, "IdentityStoreUnavailable", "IdentityCommitDeadlineExceeded").ConfigureAwait(false);
        return new(result.Committed, result.ReasonCode, result.Committed ? work.Result : null);
    }

    private StoreWriteResult UpdateIdentityCore(sqlite3 database, IdentityWork work, StoreDeadline deadline)
    {
        var integrity = Integrity;
        if (integrity?.State != AuditIntegrityState.Verified)
            return new(false, integrity?.ReasonCode ?? "IdentityAuditUnavailable",
                RetryAfterIntegrityRecheck: integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes) return new(false, "TraceStoreWalLimit");
        var committed = false;
        IdentityUpdate? decision = null;
        IIdentityTransactionGuard? guard = null;
        SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
        try
        {
            var alarmStore = _options.AlarmPolicy is not null;
            var archiveStore = _options.AlgorithmResultArchive is not null;
            var draftStore = _options.RecipeDrafts is not null;
            var cameraStore = _options.CameraSetup is not null;
            var recoveryStore = _options.CameraRecovery is not null;
            var networkStore = _options.CameraNetwork is not null;
            var imagingStore = _options.ImagingSetup is not null;
            var calibrationStore = _options.CalibrationSessions is not null;
            var releaseStore = _options.RecipeReleases is not null;
            var contractStore = _options.PlcResultContracts is not null;
            var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
                _signingKey.PublicKeyBase64,
                alarmStore || archiveStore || draftStore || cameraStore || recoveryStore || networkStore || imagingStore || calibrationStore || releaseStore || contractStore ? new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries) :
                    new AuditVerificationRequest(), !alarmStore && !archiveStore && !draftStore && !cameraStore && !recoveryStore && !networkStore && !imagingStore && !calibrationStore && !releaseStore && !contractStore, deadline,
                validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                 releaseOptions: _options.RecipeReleases,
                 contractOptions: _options.PlcResultContracts);
            if (alarmStore) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (_options.AlgorithmResultArchive is not null)
                AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            if (draftStore) AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                _options.RecipeDrafts);
            if (cameraStore) AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                _options.CameraSetup);
            if (recoveryStore) AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                _options.CameraRecovery);
            if (networkStore) AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                _options.CameraNetwork);
            if (imagingStore) AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                _options.ImagingSetup);
            if (releaseStore) AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
            if (contractStore) AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                _options.PlcResultContracts);
            var state = ReadIdentityState(database, deadline);
            state.Revision = checked(state.Revision + 1);
            var duplicateCorrelation = (work.CommandUpdate is not null || work.AlarmCommandUpdate is not null ||
                work.CalibrationGovernanceUpdate is not null || work.RecipeReleaseUpdate is not null ||
                work.PlcResultContractUpdate is not null) &&
                Exists(database, "SELECT 1 FROM command_attempts WHERE CorrelationId=? AND OutcomeDisposition=0 LIMIT 1;",
                    work.CommandCorrelationId!.Value, deadline);
            var existingRecoveryOperation = work.RecoveryOperationUpdate is null ? null :
                ReadRecoveryOperation(database, work.RecoveryOperationId!.Value, deadline);
            var cameraRole = work.CameraSetupLogicalRole ?? work.ImagingSetupLogicalRole;
            var cameraState = cameraRole is null ? null :
                ReadCameraSetupState(database, cameraRole, deadline);
            var imagingState = work.ImagingSetupUpdate is null ? null :
                ReadImagingSetupState(database, work.ImagingSetupLogicalRole!, deadline);
            // A Rebind admission has no camera stream row until hardware work
            // finishes.  The verified read projection therefore carries its
            // operation id separately.  Refuse a different operation at the
            // writer boundary as well as at the runtime bridge; the matching
            // operation is left to the callback to validate as a terminal
            // continuation with its original authorization context.
            if (cameraState is not null &&
                ((cameraState.PendingAdmission is { OperationId: var pendingAdmission } &&
                    pendingAdmission != work.CameraSetupOperationId) ||
                 (cameraState.Pending is { OperationId: var pendingCameraEvent } &&
                    pendingCameraEvent != work.CameraSetupOperationId)))
                return new(false, "CameraSetupOperationPending");
            if (cameraState is not null && _options.CameraSetup is { } cameraOptions &&
                cameraState.PendingOperationCount >= cameraOptions.MaximumPendingOperations &&
                cameraState.PendingAdmission?.OperationId != work.CameraSetupOperationId &&
                cameraState.Pending?.OperationId != work.CameraSetupOperationId)
                return new(false, "CameraSetupPendingCapacityExceeded");
            var duplicateCameraOperation = work.CameraSetupUpdate is not null &&
                ReadCameraSetupOperation(database, work.CameraSetupOperationId!.Value, deadline);
            var duplicateImagingOperation = work.ImagingSetupUpdate is not null &&
                ReadImagingSetupOperation(database, work.ImagingSetupOperationId!.Value, deadline);
            var persistedAlarmPolicy = work.AlarmCommandUpdate is null
                ? null
                : AlarmStorageCodec.ReadPersistedPolicy(database, deadline);
            if (work.AlarmCommandUpdate is not null)
                AlarmStorageCodec.RequireConfiguredPolicy(persistedAlarmPolicy, _options.AlarmPolicy);
            var alarmState = work.AlarmCommandUpdate is null ? null :
                AlarmStorageCodec.BuildState(persistedAlarmPolicy,
                    AlarmStorageCodec.ReadEvents(database, deadline), work.RuntimeEpoch);
            var governanceState = work.CalibrationGovernanceCommand is null ? null :
                ReadCalibrationGovernanceCommandState(database, work.CalibrationGovernanceCommand, deadline);
            var releaseState = work.RecipeReleaseCommand is null ? null :
                ReadRecipeReleaseCommandState(database, work.RecipeReleaseCommand, deadline);
            var contractState = work.PlcResultContractCommand is null ? null :
                ReadPlcResultContractCommandState(database, deadline);
            var evaluated = work.Evaluate(state, alarmState, duplicateCorrelation, existingRecoveryOperation,
                cameraState, duplicateCameraOperation, imagingState, duplicateImagingOperation, governanceState,
                releaseState, contractState);
            decision = evaluated;
            guard = evaluated.CommitGuard;
            if (evaluated.NoMutation)
            {
                AuditChainDatabase.Require(evaluated.Events.Count == 0 && evaluated.CommandFacts is null &&
                    evaluated.CameraEvents is null && evaluated.ImagingRevision is null &&
                    evaluated.CalibrationAdmission is null && evaluated.CalibrationGovernance is null &&
                    evaluated.RecipeRelease is null && evaluated.PlcResultContract is null && guard is null,
                    "IdentityNoMutationInvalid");
                Rollback(database);
                committed = true;
                work.Result = evaluated.Result;
                return new(true, "IdentityTransactionAlreadyPersisted");
            }
            AuditChainDatabase.Require(evaluated.Events.Count is > 0 and <= 8, "IdentityAuditEventRequired");
            long identitySequence = 0;
            long recoveryIdentitySequence = 0;
            var recoveryEventCount = 0;
            IdentityAuditEvent? recoveryAuditEvent = null;
            foreach (var fact in evaluated.Events)
            {
                identitySequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey,
                    BindAuthenticationPolicy(fact with { StateRevision = state.Revision }), deadline);
                if (evaluated.CompletedRecoveryOperation is { } operation &&
                    fact.OperationId == operation.OperationId)
                {
                    recoveryIdentitySequence = identitySequence;
                    recoveryAuditEvent = fact;
                    recoveryEventCount++;
                }
            }
            if (evaluated.CompletedRecoveryOperation is { } completedRecovery)
            {
                AuditChainDatabase.Require(work.RecoveryOperationId == completedRecovery.OperationId &&
                    completedRecovery.Succeeded && recoveryIdentitySequence > 0 && recoveryEventCount == 1 &&
                    recoveryAuditEvent is not null && recoveryAuditEvent.ReasonCode == completedRecovery.ReasonCode &&
                    recoveryAuditEvent.RecoveryKitId == completedRecovery.KitId &&
                    recoveryAuditEvent.PrincipalId == completedRecovery.PrincipalId,
                    "RecoveryOperationAuditBindingInvalid");
                AppendRecoveryOperation(database, state, completedRecovery, state.Revision,
                    recoveryIdentitySequence, deadline);
            }
            if (evaluated.AlarmEvents is { Count: > 0 } alarmEvents)
                AppendAlarmEvents(database, alarmEvents, work.RuntimeEpoch, work.CommandCorrelationId, deadline);
            if (evaluated.CameraEvents is { Count: > 0 } cameraEvents)
                AppendCameraSetupEvents(database, cameraEvents, deadline);
            ImagingSetupRevision? imagingRevision = null;
            if (evaluated.ImagingRevision is { } imagingMutation)
            {
                AuditChainDatabase.EnsureNextSequenceAvailable(database, _policy!, deadline,
                    archiveData: false, cameraSetupData: false, cameraNetworkData: false,
                    imagingData: true);
                imagingRevision = InsertImagingSetupRevision(database, imagingMutation,
                    imagingState!, deadline);
                AuditChainDatabase.AppendImagingSetupRevision(database, _policy!, _signingKey!,
                    _options.ImagingSetup!, imagingRevision.Position, deadline);
            }
            if (evaluated.CalibrationAdmission is { } calibrationHeader)
                AppendCalibrationAdmission(database, calibrationHeader, identitySequence, deadline);
            var identityTail = AuditChainDatabase.LastIdentityEntry(database, deadline);
            if (identityTail is null || identityTail.Value.Sequence != identitySequence)
                throw new InvalidOperationException("IdentityAuthorityAuditMismatch");
            state.LastIdentityAuditHash = identityTail.Value.Hash;
            var protectedState = IdentityStateProtection.Protect(state);
            AuditChainDatabase.Execute(database, "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;", deadline,
                state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision, identitySequence, _signingKey));
            AppendIdentityCommandFacts(database, evaluated.CommandFacts, work.CommandCorrelationId, deadline,
                allowTerminalContinuation: work.CameraSetupUpdate is not null);
            if (evaluated.CalibrationGovernance is not null)
                AppendGovernanceIdentityMutation(database, evaluated, governanceState!,
                    work.CalibrationGovernanceCommand!, deadline);
            if (evaluated.RecipeRelease is not null)
            {
                AppendRecipeReleaseIdentityMutation(database, evaluated, releaseState!,
                    work.RecipeReleaseCommand!, deadline);
            }
            if (evaluated.PlcResultContract is not null)
            {
                AppendPlcResultContractIdentityMutation(database, evaluated, contractState!,
                    work.PlcResultContractCommand!, deadline);
            }
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Result = evaluated.ImagingRevision is not null && evaluated.Result is ImagingSetupPersistenceCommit commit
                ? commit with { Revision = imagingRevision } : evaluated.Result;
            try { guard?.Commit(); }
            catch (Exception) { /* The transaction is durable; the internal guard contract is no-throw. */ }
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            try
            {
                PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying, "AuditRecheckPending"));
                WakeIntegrityMonitor();
            }
            catch (Exception) { /* Post-commit monitoring is advisory and cannot change the result. */ }
            return new(true, "IdentityTransactionPersisted");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is InvalidOperationException && ex.Message.StartsWith("Identity", StringComparison.Ordinal)
                ? ex.Message : SqliteAuditIntegrityQuery.FaultReason(ex, "IdentityCommitFailed");
            if (reason.StartsWith("Audit", StringComparison.Ordinal) &&
                !AuditChainDatabase.IsCapacityReason(reason))
                SetIntegrityFault(reason, IsStructuralFault(reason));
            if (reason is "IdentityStateInvalid" or "IdentityPolicyBindingMismatch" or "IdentityAuthorityAuditMismatch" or
                "IdentityAuthorityMissing" or "IdentityStateBindingMismatch" or "IdentityStateSignatureInvalid" or
                "RecoveryOperationIndexInvalid" or "RecoveryOperationAuditBindingInvalid") SetIntegrityFault(reason, true);
            return new(false, reason);
        }
        finally
        {
            try { guard?.Dispose(); }
            catch (Exception) { /* Disposal must not prevent rollback or rewrite a durable result. */ }
            finally
            {
                if (!committed) Rollback(database);
            }
        }
    }

    private IdentityAuditEvent BindAuthenticationPolicy(IdentityAuditEvent fact) => fact with
    { AuthenticationPolicyId = _options.LocalIdentity!.AuthenticationPolicy.Id,
        AuthenticationPolicyVersion = _options.LocalIdentity.AuthenticationPolicy.Version,
        AuthenticationPolicyHash = _options.LocalIdentity.AuthenticationPolicy.ContentHash,
        AuthorizationPolicyId = _options.LocalIdentity.AuthorizationPolicy.Id,
        AuthorizationPolicyVersion = _options.LocalIdentity.AuthorizationPolicy.Version,
        AuthorizationPolicyHash = _options.LocalIdentity.AuthorizationPolicy.ContentHash };

    private RecoveryOperationState? ReadRecoveryOperation(sqlite3 database, Guid operationId,
        StoreDeadline deadline)
    {
        var row = AuditChainDatabase.Read(database, @"SELECT OperationId,Kind,Succeeded,ReasonCode,KitId,
            PrincipalId,DeliveryCommitted,StateRevision,IdentitySequence,AuditHash,RecordHash
            FROM recovery_operations WHERE OperationId=?;", deadline,
            statement => ReadRecoveryOperationRow(statement), operationId.ToString("D")).SingleOrDefault();
        return row is null ? null : ValidateRecoveryOperationRow(database, row, operationId, deadline);
    }

    private void ValidateRecoveryOperationIndex(sqlite3 database, IdentityAuthorityState state,
        StoreDeadline deadline)
    {
        AuditChainDatabase.Require(state.RecoveryOperationCount >= 0 &&
            AuditCanonical.IsHash(state.RecoveryOperationRootHash), "RecoveryOperationIndexInvalid");
        var rows = AuditChainDatabase.Read(database, @"SELECT OperationId,Kind,Succeeded,ReasonCode,KitId,
            PrincipalId,DeliveryCommitted,StateRevision,IdentitySequence,AuditHash,RecordHash
            FROM recovery_operations ORDER BY IdentitySequence,OperationId;", deadline,
            statement => ReadRecoveryOperationRow(statement)).ToArray();
        var previousSequence = 0L;
        var count = 0L;
        var root = AuditCanonical.GenesisHash;
        foreach (var row in rows)
        {
            var operation = ValidateRecoveryOperationRow(database, row, expectedOperationId: null, deadline);
            AuditChainDatabase.Require(row.IdentitySequence > previousSequence, "RecoveryOperationIndexInvalid");
            previousSequence = row.IdentitySequence;
            root = RecoveryOperationRootHash(root, operation, row.StateRevision, row.IdentitySequence,
                row.AuditHash!, row.RecordHash!);
            count = checked(count + 1);
        }
        AuditChainDatabase.Require(count == state.RecoveryOperationCount &&
            string.Equals(root, state.RecoveryOperationRootHash, StringComparison.Ordinal),
            "RecoveryOperationIndexInvalid");
    }

    private static RecoveryOperationIndexRow ReadRecoveryOperationRow(sqlite3_stmt statement) =>
        new(SqliteNative.ColumnText(statement, 0), SqliteNative.ColumnText(statement, 1),
            SqliteNative.ColumnInt64(statement, 2), SqliteNative.ColumnText(statement, 3),
            SqliteNative.ColumnText(statement, 4), SqliteNative.ColumnText(statement, 5),
            SqliteNative.ColumnInt64(statement, 6), SqliteNative.ColumnInt64(statement, 7),
            SqliteNative.ColumnInt64(statement, 8), SqliteNative.ColumnText(statement, 9),
            SqliteNative.ColumnText(statement, 10));

    private RecoveryOperationState ValidateRecoveryOperationRow(sqlite3 database,
        RecoveryOperationIndexRow row, Guid? expectedOperationId, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(Guid.TryParseExact(row.OperationId, "D", out var parsedOperation) &&
            parsedOperation != Guid.Empty && (expectedOperationId is null || parsedOperation == expectedOperationId) &&
            row.Succeeded == 1 && (row.DeliveryCommitted == 0 || row.DeliveryCommitted == 1) &&
            row.StateRevision >= 0 && row.IdentitySequence > 0 && row.AuditHash is { Length: 64 } &&
            row.RecordHash is { Length: 64 }, "RecoveryOperationIndexInvalid");
        Guid? kitId = ParseNullableGuid(row.KitId, "RecoveryOperationIndexInvalid");
        Guid? principalId = ParseNullableGuid(row.PrincipalId, "RecoveryOperationIndexInvalid");
        var operation = new RecoveryOperationState
        {
            OperationId = parsedOperation, Kind = row.Kind ?? string.Empty, Succeeded = true,
            ReasonCode = row.ReasonCode ?? string.Empty, KitId = kitId, PrincipalId = principalId,
            DeliveryCommitted = row.DeliveryCommitted == 1
        };
        AuditChainDatabase.Require(operation.Kind.Length is > 0 and <= 64 &&
            operation.ReasonCode.Length is > 0 and <= 128, "RecoveryOperationIndexInvalid");
        var auditHash = AuditChainDatabase.Text(database,
            "SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent';", deadline,
            row.IdentitySequence.ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(auditHash == row.AuditHash &&
            RecoveryOperationRecordHash(operation, row.StateRevision, row.IdentitySequence, row.AuditHash!) == row.RecordHash,
            "RecoveryOperationIndexInvalid");
        return operation;
    }

    private void AppendRecoveryOperation(sqlite3 database, IdentityAuthorityState state,
        RecoveryOperationState operation, long stateRevision, long identitySequence,
        StoreDeadline deadline)
    {
        AuditChainDatabase.Require(operation.OperationId != Guid.Empty && operation.Succeeded &&
            operation.Kind is { Length: > 0 and <= 64 } && operation.ReasonCode is { Length: > 0 and <= 128 },
            "RecoveryOperationInvalid");
        var auditHash = AuditChainDatabase.Text(database,
            "SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent';", deadline,
            identitySequence.ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(auditHash is { Length: 64 }, "RecoveryOperationAuditBindingInvalid");
        var recordHash = RecoveryOperationRecordHash(operation, stateRevision, identitySequence, auditHash!);
        AuditChainDatabase.Execute(database, @"INSERT INTO recovery_operations(OperationId,Kind,Succeeded,
            ReasonCode,KitId,PrincipalId,DeliveryCommitted,StateRevision,IdentitySequence,AuditHash,RecordHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?);", deadline, operation.OperationId.ToString("D"), operation.Kind,
            operation.Succeeded ? "1" : "0", operation.ReasonCode, operation.KitId?.ToString("D"),
            operation.PrincipalId?.ToString("D"), operation.DeliveryCommitted ? "1" : "0",
            stateRevision.ToString(CultureInfo.InvariantCulture), identitySequence.ToString(CultureInfo.InvariantCulture),
            auditHash, recordHash);
        state.RecoveryOperationRootHash = RecoveryOperationRootHash(state.RecoveryOperationRootHash,
            operation, stateRevision, identitySequence, auditHash!, recordHash);
        state.RecoveryOperationCount = checked(state.RecoveryOperationCount + 1);
    }

    private static string RecoveryOperationRecordHash(RecoveryOperationState operation, long stateRevision,
        long identitySequence, string auditHash) => Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "RecoveryOperationIndex", operation.OperationId.ToString("D"), operation.Kind,
            operation.Succeeded ? "1" : "0", operation.ReasonCode, operation.KitId?.ToString("D"),
            operation.PrincipalId?.ToString("D"), operation.DeliveryCommitted ? "1" : "0",
            stateRevision.ToString(CultureInfo.InvariantCulture), identitySequence.ToString(CultureInfo.InvariantCulture),
            auditHash)));

    private static string RecoveryOperationRootHash(string previousRoot, RecoveryOperationState operation,
        long stateRevision, long identitySequence, string auditHash, string recordHash) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("RecoveryOperationRoot", previousRoot,
            operation.OperationId.ToString("D"), operation.Kind, operation.Succeeded ? "1" : "0",
            operation.ReasonCode, operation.KitId?.ToString("D"), operation.PrincipalId?.ToString("D"),
            operation.DeliveryCommitted ? "1" : "0", stateRevision.ToString(CultureInfo.InvariantCulture),
            identitySequence.ToString(CultureInfo.InvariantCulture), auditHash, recordHash)));

    private static Guid? ParseNullableGuid(string? value, string reason)
    {
        if (value is null) return null;
        AuditChainDatabase.Require(Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty, reason);
        return parsed;
    }

    private sealed record RecoveryOperationIndexRow(string? OperationId, string? Kind, long Succeeded,
        string? ReasonCode, string? KitId, string? PrincipalId, long DeliveryCommitted,
        long StateRevision, long IdentitySequence, string? AuditHash, string? RecordHash);

    private void AppendIdentityCommandFacts(sqlite3 database, IReadOnlyList<CommandAuditFact>? facts,
        Guid? expectedCorrelationId, StoreDeadline deadline, bool allowTerminalContinuation = false,
        AuditChainDatabase.CameraNetworkAuditWriteMode cameraNetworkMode = AuditChainDatabase.CameraNetworkAuditWriteMode.Generic)
    {
        if (facts is null || facts.Count == 0)
        {
            AuditChainDatabase.Require(expectedCorrelationId is null, "IdentityCommandFactsRequired");
            return;
        }
        AuditChainDatabase.Require(facts.Count <= 2, "IdentityCommandFactsLimit");

        // Camera setup persists its admission and terminal in separate identity
        // transactions.  The terminal therefore continues the already durable
        // command attempt instead of pretending that a second Outcome exists.
        // Keep this escape hatch private to the camera writer; ordinary identity
        // commands retain the outcome-first contract below.
        if (allowTerminalContinuation && facts.Count == 1 &&
            facts[0].Phase != CommandAuditPhase.Outcome)
        {
            var continuation = facts[0];
            ValidateFact(continuation);
            AuditChainDatabase.Require(expectedCorrelationId is not null &&
                continuation.CorrelationId == expectedCorrelationId.Value,
                "IdentityCommandCorrelationMismatch");
            var attempt = ReadAttempt(database, continuation.AttemptId, deadline);
            AuditChainDatabase.Require(attempt is not null &&
                attempt.Value.OutcomeDisposition == CommandDisposition.Accepted &&
                attempt.Value.Matches(continuation), "IdentityCommandTerminalContextMismatch");
            var existing = ReadFact(database, continuation.AttemptId, aggregateSequence: 2, deadline);
            if (existing is not null)
            {
                AuditChainDatabase.Require(existing.EventId == continuation.EventId &&
                    existing.Phase == continuation.Phase && existing.ReasonCode == continuation.ReasonCode &&
                    existing.OccurredAtUtc == continuation.OccurredAtUtc &&
                    existing.CommandKind == continuation.CommandKind && existing.Source == continuation.Source &&
                    existing.ClaimedPrincipalId == continuation.ClaimedPrincipalId &&
                    existing.ClaimedSessionId == continuation.ClaimedSessionId &&
                    existing.ClaimedStepUpGrantId == continuation.ClaimedStepUpGrantId &&
                    existing.AuthenticatedHumanPrincipalId == continuation.AuthenticatedHumanPrincipalId,
                    "DuplicateTerminalConflict");
                return;
            }
            AuditChainDatabase.Require(!Exists(database,
                "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;", continuation.EventId, deadline),
                "DuplicateEventId");
            InsertFact(database, continuation, aggregateSequence: 2, deadline);
            AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, continuation.EventId, deadline, cameraNetworkMode);
            return;
        }

        var outcome = facts[0];
        ValidateFact(outcome);
        AuditChainDatabase.Require(outcome.Phase == CommandAuditPhase.Outcome,
            "IdentityCommandOutcomeRequired");
        AuditChainDatabase.Require(expectedCorrelationId is null || outcome.CorrelationId == expectedCorrelationId.Value,
            "IdentityCommandCorrelationMismatch");
        AuditChainDatabase.Require(!Exists(database, "SELECT 1 FROM command_attempts WHERE AttemptId=? LIMIT 1;",
            outcome.AttemptId, deadline), "DuplicateAttemptId");
        AuditChainDatabase.Require(!Exists(database, "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;",
            outcome.EventId, deadline), "DuplicateEventId");

        var terminal = facts.Count == 2 ? facts[1] : null;
        if (outcome.Disposition == CommandDisposition.Rejected)
            AuditChainDatabase.Require(terminal is null, "IdentityCommandRejectedTerminalForbidden");
        if (terminal is not null)
        {
            ValidateFact(terminal);
            AuditChainDatabase.Require(terminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
                terminal.Disposition is null && SameCommandContext(outcome, terminal),
                "IdentityCommandTerminalInvalid");
            AuditChainDatabase.Require(!Exists(database, "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;",
                terminal.EventId, deadline), "DuplicateEventId");
        }

        InsertAttempt(database, outcome, deadline);
        InsertFact(database, outcome, aggregateSequence: 1, deadline);
        AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, outcome.EventId, deadline, cameraNetworkMode);
        if (terminal is not null)
        {
            InsertFact(database, terminal, aggregateSequence: 2, deadline);
            AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, terminal.EventId, deadline, cameraNetworkMode);
        }
    }

    private static bool SameCommandContext(CommandAuditFact first, CommandAuditFact second) =>
        first.AttemptId == second.AttemptId && first.CorrelationId == second.CorrelationId &&
        first.RuntimeEpoch == second.RuntimeEpoch && first.CommandKind == second.CommandKind &&
        first.Source == second.Source && first.ClaimedPrincipalId == second.ClaimedPrincipalId &&
        first.ClaimedSessionId == second.ClaimedSessionId &&
        first.ClaimedStepUpGrantId == second.ClaimedStepUpGrantId &&
        first.AuthenticatedHumanPrincipalId == second.AuthenticatedHumanPrincipalId;

    private sealed class IdentityWork
    {
        internal IdentityWork(CalibrationGovernanceCommand command,
            Func<IdentityAuthorityState, CalibrationGovernanceCommandState, bool, IdentityUpdate> update)
        {
            CommandCorrelationId = command.CorrelationId;
            CalibrationGovernanceCommand = command;
            CalibrationGovernanceUpdate = update;
        }

        internal IdentityWork(ReleaseRecipeCommand command,
            Func<IdentityAuthorityState, RecipeReleaseCommandState, bool, IdentityUpdate> update)
        {
            CommandCorrelationId = command.CorrelationId;
            RecipeReleaseCommand = command;
            RecipeReleaseUpdate = update;
        }

        internal IdentityWork(ChangePlcResultContractCommand command,
            Func<IdentityAuthorityState, PlcResultContractCommandState, bool, IdentityUpdate> update)
        {
            CommandCorrelationId = command.CorrelationId;
            PlcResultContractCommand = command;
            PlcResultContractUpdate = update;
        }

        internal IdentityWork(Func<IdentityAuthorityState, IdentityUpdate> update) => Update = update;

        internal IdentityWork(Guid commandCorrelationId,
            Func<IdentityAuthorityState, bool, IdentityUpdate> commandUpdate)
        {
            CommandCorrelationId = commandCorrelationId;
            CommandUpdate = commandUpdate;
        }

        internal IdentityWork(Guid recoveryOperationId,
            Func<IdentityAuthorityState, RecoveryOperationState?, IdentityUpdate> recoveryUpdate)
        {
            RecoveryOperationId = recoveryOperationId;
            RecoveryOperationUpdate = recoveryUpdate;
        }

        internal IdentityWork(Guid commandCorrelationId, Guid runtimeEpoch,
            Func<IdentityAuthorityState, AlarmStateSnapshot, bool, IdentityUpdate> alarmCommandUpdate)
        {
            CommandCorrelationId = commandCorrelationId;
            RuntimeEpoch = runtimeEpoch;
            AlarmCommandUpdate = alarmCommandUpdate;
        }

        internal IdentityWork(Guid operationId, string logicalRole,
            Func<IdentityAuthorityState, CameraSetupStoreSnapshot, bool, IdentityUpdate> cameraSetupUpdate)
        {
            CommandCorrelationId = operationId;
            CameraSetupOperationId = operationId;
            CameraSetupLogicalRole = logicalRole;
            CameraSetupUpdate = cameraSetupUpdate;
        }

        internal IdentityWork(Guid operationId, string logicalRole,
            Func<IdentityAuthorityState, CameraSetupStoreSnapshot, ImagingSetupStoreSnapshot, bool,
                IdentityUpdate> imagingSetupUpdate)
        {
            CommandCorrelationId = operationId;
            ImagingSetupOperationId = operationId;
            ImagingSetupLogicalRole = logicalRole;
            ImagingSetupUpdate = imagingSetupUpdate;
        }

        internal Func<IdentityAuthorityState, IdentityUpdate>? Update { get; }
        internal Guid? CommandCorrelationId { get; }
        internal Func<IdentityAuthorityState, bool, IdentityUpdate>? CommandUpdate { get; }
        internal Func<IdentityAuthorityState, AlarmStateSnapshot, bool, IdentityUpdate>? AlarmCommandUpdate { get; }
        internal Guid RuntimeEpoch { get; }
        internal Guid? RecoveryOperationId { get; }
        internal Func<IdentityAuthorityState, RecoveryOperationState?, IdentityUpdate>? RecoveryOperationUpdate { get; }
        internal Guid? CameraSetupOperationId { get; }
        internal string? CameraSetupLogicalRole { get; }
        internal Func<IdentityAuthorityState, CameraSetupStoreSnapshot, bool, IdentityUpdate>? CameraSetupUpdate { get; }
        internal Guid? ImagingSetupOperationId { get; }
        internal string? ImagingSetupLogicalRole { get; }
        internal Func<IdentityAuthorityState, CameraSetupStoreSnapshot, ImagingSetupStoreSnapshot, bool,
            IdentityUpdate>? ImagingSetupUpdate { get; }
        internal object? Result { get; set; }
        internal CalibrationGovernanceCommand? CalibrationGovernanceCommand { get; }
        internal Func<IdentityAuthorityState, CalibrationGovernanceCommandState, bool, IdentityUpdate>?
            CalibrationGovernanceUpdate { get; }
        internal ReleaseRecipeCommand? RecipeReleaseCommand { get; }
        internal Func<IdentityAuthorityState, RecipeReleaseCommandState, bool, IdentityUpdate>?
            RecipeReleaseUpdate { get; }
        internal ChangePlcResultContractCommand? PlcResultContractCommand { get; }
        internal Func<IdentityAuthorityState, PlcResultContractCommandState, bool, IdentityUpdate>?
            PlcResultContractUpdate { get; }

        internal IdentityUpdate Evaluate(IdentityAuthorityState state, AlarmStateSnapshot? alarmState,
            bool duplicateCorrelation, RecoveryOperationState? existingRecoveryOperation,
            CameraSetupStoreSnapshot? cameraSetupState, bool duplicateCameraOperation,
            ImagingSetupStoreSnapshot? imagingSetupState = null, bool duplicateImagingOperation = false,
            CalibrationGovernanceCommandState? governanceState = null,
            RecipeReleaseCommandState? releaseState = null,
            PlcResultContractCommandState? plcResultContractState = null) =>
            RecipeReleaseUpdate is not null ? RecipeReleaseUpdate(state, releaseState!, duplicateCorrelation) :
            PlcResultContractUpdate is not null ? PlcResultContractUpdate(state, plcResultContractState!, duplicateCorrelation) :
            CalibrationGovernanceUpdate is not null ? CalibrationGovernanceUpdate(state, governanceState!, duplicateCorrelation) :
            AlarmCommandUpdate is not null ? AlarmCommandUpdate(state, alarmState!, duplicateCorrelation) :
            CommandUpdate is not null ? CommandUpdate(state, duplicateCorrelation) :
            RecoveryOperationUpdate is not null ? RecoveryOperationUpdate(state, existingRecoveryOperation) :
            CameraSetupUpdate is not null ? CameraSetupUpdate(state, cameraSetupState!, duplicateCameraOperation) :
            ImagingSetupUpdate is not null ? ImagingSetupUpdate(state, cameraSetupState!, imagingSetupState!, duplicateImagingOperation) :
            Update!(state);
    }
}

internal interface IIdentityTransactionGuard : IDisposable
{
    void Commit();
}

internal sealed record IdentityUpdate(
    object Result,
    IReadOnlyList<IdentityAuditEvent> Events,
    IReadOnlyList<CommandAuditFact>? CommandFacts = null,
    IIdentityTransactionGuard? CommitGuard = null,
    RecoveryOperationState? CompletedRecoveryOperation = null,
    IReadOnlyList<AlarmHistoryRecord>? AlarmEvents = null,
    IReadOnlyList<CameraSetupEvent>? CameraEvents = null,
    ImagingSetupRevisionMutation? ImagingRevision = null,
    CalibrationSessionHeader? CalibrationAdmission = null,
    bool NoMutation = false,
     CalibrationGovernanceMutation? CalibrationGovernance = null,
     RecipeReleaseMutation? RecipeRelease = null,
     PlcResultContractMutation? PlcResultContract = null);
internal sealed record IdentityWriteResult(bool Committed, string ReasonCode, object? Result = null);
