using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Schema 12 durable camera network maintenance evidence.  The network stream
/// is append-only and is linked to the main signed audit chain by NetworkPosition.
/// A process restart therefore exposes an admission as pending; no provider call
/// is replayed by this storage layer.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string CameraNetworkSchemaSql = @"
        CREATE TABLE camera_network_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEvents INTEGER NOT NULL CHECK(MaximumEvents>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            MaximumPendingOperations INTEGER NOT NULL CHECK(MaximumPendingOperations>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE camera_network_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            OperationId TEXT NOT NULL CHECK(length(OperationId)=36),
            AttemptId TEXT NOT NULL CHECK(length(AttemptId)=36),
            CorrelationId TEXT NOT NULL CHECK(length(CorrelationId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            Phase INTEGER NOT NULL CHECK(Phase IN (0,1,2)),
            TargetProviderId TEXT NOT NULL,
            TargetProviderVersion TEXT NOT NULL,
            TargetAdapterPackageId TEXT NOT NULL,
            TargetAdapterVersion TEXT NOT NULL,
            TargetStableDeviceIdentity TEXT NOT NULL,
            TargetContentHash TEXT NOT NULL CHECK(length(TargetContentHash)=64),
            StationInterfaceId TEXT NULL,
            StationAddress TEXT NULL,
            StationPrefixLength INTEGER NULL CHECK(StationPrefixLength IS NULL OR StationPrefixLength BETWEEN 1 AND 30),
            StationGateway TEXT NULL,
            PreviousAddress TEXT NULL,
            PreviousPrefixLength INTEGER NULL CHECK(PreviousPrefixLength IS NULL OR PreviousPrefixLength BETWEEN 1 AND 30),
            PreviousGateway TEXT NULL,
            RequestedAddress TEXT NOT NULL,
            RequestedPrefixLength INTEGER NOT NULL CHECK(RequestedPrefixLength BETWEEN 1 AND 30),
            RequestedGateway TEXT NULL,
            ObservedAddress TEXT NULL,
            ObservedPrefixLength INTEGER NULL CHECK(ObservedPrefixLength IS NULL OR ObservedPrefixLength BETWEEN 1 AND 30),
            ObservedGateway TEXT NULL,
            State INTEGER NOT NULL CHECK(State IN (0,1,2,3)),
            IdentityVerified INTEGER NOT NULL CHECK(IdentityVerified IN (0,1)),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0 AND length(ReasonCode)<=128),
            ChangeReason TEXT NOT NULL CHECK(length(ChangeReason)>0 AND length(ChangeReason)<=256),
            ActorPrincipalId TEXT NULL CHECK(ActorPrincipalId IS NULL OR length(ActorPrincipalId)=36),
            SessionId TEXT NULL CHECK(SessionId IS NULL OR length(SessionId)=36),
            AuthorizationRevision INTEGER NOT NULL CHECK(AuthorizationRevision>=0),
            RecordedAtUtc TEXT NOT NULL,
            Payload TEXT NOT NULL,
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            UNIQUE(OperationId,Phase));
        CREATE INDEX ix_camera_network_target_position ON camera_network_events(TargetContentHash,Position);
        CREATE INDEX ix_camera_network_operation ON camera_network_events(OperationId,Position);
        CREATE TRIGGER camera_network_config_immutable_update BEFORE UPDATE ON camera_network_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraNetworkConfiguration');
        END;
        CREATE TRIGGER camera_network_config_immutable_delete BEFORE DELETE ON camera_network_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraNetworkConfiguration');
        END;
        CREATE TRIGGER camera_network_event_immutable_update BEFORE UPDATE ON camera_network_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraNetworkEvent');
        END;
        CREATE TRIGGER camera_network_event_immutable_delete BEFORE DELETE ON camera_network_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraNetworkEvent');
        END;";

    internal ValueTask<CameraNetworkReadState> ReadCameraNetworkStateAsync(
        CameraBindingTarget? target, CancellationToken cancellationToken = default)
    {
        if (!CameraNetworkEnabled)
            return ValueTask.FromResult(new CameraNetworkReadState(null, false, false,
                "CameraNetworkUnavailable"));
        return new ValueTask<CameraNetworkReadState>(ReadCameraNetworkStateCoreAsync(target, cancellationToken));
    }

    internal ValueTask<CameraNetworkAdmissionResult> AppendCameraNetworkAdmissionAsync(
        CameraNetworkChangeRequest request, CameraStationNetwork stationNetwork,
        CameraIpv4Configuration actualPrevious, CameraSetupAuthorization authorization,
        Guid runtimeEpoch, StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stationNetwork);
        ArgumentNullException.ThrowIfNull(actualPrevious);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(deadline);
        if (!CameraNetworkEnabled)
            return ValueTask.FromResult(new CameraNetworkAdmissionResult(
                new StoreWriteResult(false, "CameraNetworkUnavailable"), null));
        if (request.OperationId == Guid.Empty || runtimeEpoch == Guid.Empty)
            return ValueTask.FromResult(new CameraNetworkAdmissionResult(
                new StoreWriteResult(false, "CameraNetworkRequestInvalid"), null));
        var work = new CameraNetworkAdmissionWork(request, stationNetwork, actualPrevious,
            authorization, runtimeEpoch);
        return EnqueueNetworkAdmissionAsync(work, deadline, cancellationToken);
    }

    internal ValueTask<StoreWriteResult> AppendCameraNetworkTerminalAsync(
        CameraNetworkAdmission admission, CameraNetworkSnapshot snapshot,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(deadline);
        if (!CameraNetworkEnabled)
            return ValueTask.FromResult(new StoreWriteResult(false, "CameraNetworkUnavailable"));
        return EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                NetworkTerminal: new CameraNetworkTerminalWork(admission, snapshot)), deadline,
            cancellationToken, "CameraNetworkUnavailable", "CameraNetworkCommitDeadlineExceeded");
    }

    internal ValueTask<StoreWriteResult> AppendCameraNetworkRejectedAsync(
        CameraNetworkChangeRequest request, CameraStationNetwork? stationNetwork,
        CameraIpv4Configuration? actualPrevious, CameraSetupAuthorization? authorization,
        Guid runtimeEpoch, string reason, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deadline);
        if (!CameraNetworkEnabled)
            return ValueTask.FromResult(new StoreWriteResult(false, "CameraNetworkUnavailable"));
        // Rejections can be recorded before the coordinator has established a
        // runtime epoch.  The ledger still requires a non-empty correlation
        // epoch, so bind such a pre-admission rejection to a fresh local epoch
        // instead of dropping the requested target and reason.
        if (runtimeEpoch == Guid.Empty) runtimeEpoch = Guid.NewGuid();
        return EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                NetworkRejected: new CameraNetworkRejectedWork(request, stationNetwork,
                    actualPrevious, authorization, runtimeEpoch, reason)), deadline,
            cancellationToken, "CameraNetworkUnavailable", "CameraNetworkCommitDeadlineExceeded");
    }

    private ValueTask<CameraNetworkAdmissionResult> EnqueueNetworkAdmissionAsync(
        CameraNetworkAdmissionWork work, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CameraNetworkAdmissionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
            NetworkAdmission: work), deadline, cancellationToken,
            "CameraNetworkUnavailable", "CameraNetworkCommitDeadlineExceeded");
        _ = CompleteNetworkAdmissionAsync(queued, work, completion);
        return new ValueTask<CameraNetworkAdmissionResult>(completion.Task);
    }

    private static async Task CompleteNetworkAdmissionAsync(
        ValueTask<StoreWriteResult> queued, CameraNetworkAdmissionWork work,
        TaskCompletionSource<CameraNetworkAdmissionResult> completion)
    {
        try
        {
            var result = await queued.ConfigureAwait(false);
            completion.TrySetResult(new CameraNetworkAdmissionResult(result, work.Admission));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            completion.TrySetResult(new CameraNetworkAdmissionResult(
                new StoreWriteResult(false, "CameraNetworkUnavailable"), null));
        }
    }

    private sealed class CameraNetworkAdmissionWork
    {
        internal CameraNetworkAdmissionWork(CameraNetworkChangeRequest request,
            CameraStationNetwork stationNetwork, CameraIpv4Configuration actualPrevious,
            CameraSetupAuthorization authorization, Guid runtimeEpoch)
        {
            Request = request;
            StationNetwork = stationNetwork;
            ActualPrevious = actualPrevious;
            Authorization = authorization;
            RuntimeEpoch = runtimeEpoch;
        }

        internal CameraNetworkChangeRequest Request { get; }
        internal CameraStationNetwork StationNetwork { get; }
        internal CameraIpv4Configuration ActualPrevious { get; }
        internal CameraSetupAuthorization Authorization { get; }
        internal Guid RuntimeEpoch { get; }
        internal CameraNetworkAdmission? Admission { get; set; }
    }

    private sealed record CameraNetworkTerminalWork(CameraNetworkAdmission Admission,
        CameraNetworkSnapshot Snapshot);

    private sealed record CameraNetworkRejectedWork(CameraNetworkChangeRequest Request,
        CameraStationNetwork? StationNetwork, CameraIpv4Configuration? ActualPrevious,
        CameraSetupAuthorization? Authorization, Guid RuntimeEpoch, string Reason);

    internal sealed record CameraNetworkReadState(CameraNetworkSnapshot? Snapshot,
        bool HasPending, bool HasUnknown, string ReasonCode);

    private async Task<CameraNetworkReadState> ReadCameraNetworkStateCoreAsync(
        CameraBindingTarget? target, CancellationToken cancellationToken)
    {
        var initialized = await Initialization.ConfigureAwait(false);
        if (!initialized.Committed || !CameraNetworkEnabled || _databasePath is null ||
            _policy is null || _signingKey is null)
            throw new InvalidOperationException(initialized.ReasonCode);

        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath, readOnly: true);
            SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
            var database = connection.Handle!;
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            var committed = false;
            try
            {
                VerifyNetworkTransaction(database, deadline);
                _ = ReadIdentityState(database, deadline);
                var events = ReadCameraNetworkEvents(database, _options.CameraNetwork!, deadline);
                var pending = FindPendingNetworkAdmissions(events);
                var unknown = events.Any(item => item.Phase == CameraNetworkEventPhase.Terminal &&
                    item.State == CameraNetworkMaintenanceState.Unknown);
                var snapshot = target is null ? null : events
                    .Where(item => SameNetworkTarget(item.Target, target))
                    .OrderBy(item => item.Position)
                    .Select(SnapshotFor).LastOrDefault();
                var reason = pending.Count != 0 || unknown
                    ? "CameraNetworkReconciliationRequired"
                    : snapshot?.ReasonCode ?? "CameraNetworkMaintenanceNotRecorded";
                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                committed = true;
                return new CameraNetworkReadState(snapshot, pending.Count != 0, unknown, reason);
            }
            finally
            {
                if (!committed) Rollback(database);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private void VerifyNetworkTransaction(sqlite3 database, StoreDeadline deadline)
    {
        var alarmStore = _options.AlarmPolicy is not null;
        var archiveStore = _options.AlgorithmResultArchive is not null;
        var draftStore = _options.RecipeDrafts is not null;
        var cameraStore = _options.CameraSetup is not null;
        var recoveryStore = _options.CameraRecovery is not null;
        var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
            _signingKey.PublicKeyBase64,
            new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries), startup: true,
            deadline, validateAnchorReceipt: false,
            archiveOptions: archiveStore ? _options.AlgorithmResultArchive : null,
             recipeDraftOptions: draftStore ? _options.RecipeDrafts : null,
             cameraSetupOptions: cameraStore ? _options.CameraSetup : null,
             cameraRecoveryOptions: recoveryStore ? _options.CameraRecovery : null,
             cameraNetworkOptions: _options.CameraNetwork,
             imagingSetupOptions: _options.ImagingSetup,
                 calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                 releaseOptions: _options.RecipeReleases,
                 contractOptions: _options.PlcResultContracts,
                 activationOptions: _options.RecipeActivations);
        if (alarmStore) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
        if (archiveStore) AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
        if (draftStore) AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
            _options.RecipeDrafts);
        if (cameraStore) AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
            _options.CameraSetup);
        if (recoveryStore) AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
            _options.CameraRecovery);
        AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
            _options.CameraNetwork);
        if (_options.ImagingSetup is not null)
            AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                _options.ImagingSetup);
        if (_options.RecipeReleases is not null)
            AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
         if (_options.PlcResultContracts is not null)
             AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                 _options.PlcResultContracts);
         if (_options.RecipeActivations is not null)
             AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
                 _options.RecipeActivations, _options.RecipeReleases, _options.PlcResultContracts,
                 _options.CalibrationGovernance);
    }

    private StoreWriteResult AppendCameraNetworkAdmissionCore(sqlite3 database,
        CameraNetworkAdmissionWork work, StoreDeadline deadline)
    {
        if (Integrity?.State != AuditIntegrityState.Verified)
            return new(false, Integrity?.ReasonCode ?? "CameraNetworkAuditUnavailable",
                RetryAfterIntegrityRecheck: Integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");

        var transactionStarted = false;
        var committed = false;
        IIdentityTransactionGuard? guard = null;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            VerifyNetworkTransaction(database, deadline);
            var existing = ReadCameraNetworkEvents(database, _options.CameraNetwork!, deadline)
                .Any(item => item.OperationId == work.Request.OperationId);
            if (existing) return new StoreWriteResult(false, "CameraNetworkOperationConflict");

            var state = ReadIdentityState(database, deadline);
            var actor = state.EnumerateAccounts().SingleOrDefault(item =>
                item.PrincipalId == work.Authorization.PrincipalId);
            var reservation = work.Authorization.Reservation;
            if (!work.Authorization.Authorized || reservation is null)
                return new StoreWriteResult(false, SafeNetworkReason(work.Authorization.ReasonCode));
            if (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(Permission.ManageCameraBindings))
                return new StoreWriteResult(false, "PermissionDenied");
            if (actor.AuthorizationRevision != work.Authorization.AuthorizationRevision ||
                work.Authorization.SessionId == Guid.Empty || work.Authorization.PrincipalId == Guid.Empty)
                return new StoreWriteResult(false, "AuthorizationStale");
            if (!InvocationMatches(work.Request.Invocation, work.Authorization))
                return new StoreWriteResult(false, "AuthorizationContextMismatch");
            AuditChainDatabase.Require(LocalAuthorizationService.MatchesCameraSetupAuthorization(
                    reservation, work.Request.OperationId,
                    work.Request.AuthorizationTargetId,
                    AuditedCommandKind.ChangeCameraNetworkConfiguration),
                "AuthorizationContextMismatch");

            guard = LocalAuthorizationService.AcquireCameraSetupCommitGuard(
                reservation, state, requireActiveGrant: true, out var guardReason);
            if (guard is null) return new StoreWriteResult(false, guardReason);

            AuditChainDatabase.EnsureCameraNetworkTransactionCapacity(database, _policy!,
                CameraNetworkEventPhase.Admission, deadline);

            var fact = CreateNetworkAdmissionFact(work);
            var identity = CreateNetworkIdentityEvent(state.StationId, work.Request,
                work.Authorization, fact.OccurredAtUtc, IdentityEventKind.CameraSetupActionAuthorized,
                fact.ReasonCode);
            state.Revision = checked(state.Revision + 1);
            var identitySequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey!,
                BindAuthenticationPolicy(identity with { StateRevision = state.Revision }), deadline,
                AuditChainDatabase.CameraNetworkAuditWriteMode.Admission);
            var identityTail = AuditChainDatabase.LastIdentityEntry(database, deadline);
            AuditChainDatabase.Require(identityTail is not null && identityTail.Value.Sequence == identitySequence,
                "IdentityAuthorityAuditMismatch");
            state.LastIdentityAuditHash = identityTail!.Value.Hash;
            var protectedState = IdentityStateProtection.Protect(state);
            AuditChainDatabase.Execute(database, "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;", deadline,
                state.Revision.ToString(CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision,
                    identitySequence, _signingKey!));
            AppendIdentityCommandFacts(database, new[] { fact }, work.Request.OperationId, deadline,
                cameraNetworkMode: AuditChainDatabase.CameraNetworkAuditWriteMode.Admission);

            var eventValue = new CameraNetworkEvent(0, Guid.NewGuid(), work.Request.OperationId,
                fact.AttemptId, fact.CorrelationId, fact.RuntimeEpoch, CameraNetworkEventPhase.Admission,
                work.Request.Target, work.StationNetwork, work.ActualPrevious, work.Request.Requested,
                null, CameraNetworkMaintenanceState.Pending, false,
                fact.ReasonCode, work.Request.ChangeReason, work.Authorization.PrincipalId,
                work.Authorization.SessionId, work.Authorization.AuthorizationRevision, fact.OccurredAtUtc);
            AppendNetworkEvent(database, eventValue, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            // The one-time Step-Up grant is consumed only after the SQLite
            // transaction is durable. Disposal below rolls it back when the
            // transaction fails before COMMIT.
            try { guard?.Commit(); } catch (Exception) { }
            work.Admission = new CameraNetworkAdmission(fact, work.Request, work.StationNetwork,
                work.ActualPrevious, work.Authorization, work.RuntimeEpoch);
            var sequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, sequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying,
                "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new StoreWriteResult(true, "CameraNetworkAdmissionPersisted", fact);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("CameraNetwork", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Authorization", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Identity", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Audit", StringComparison.Ordinal))
        { return new StoreWriteResult(false, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new StoreWriteResult(false, SqliteAuditIntegrityQuery.FaultReason(ex, "CameraNetworkCommitFailed")); }
        finally
        {
            try { guard?.Dispose(); } catch { }
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendCameraNetworkTerminalCore(sqlite3 database,
        CameraNetworkTerminalWork work, StoreDeadline deadline)
    {
        if (Integrity?.State != AuditIntegrityState.Verified)
            return new(false, Integrity?.ReasonCode ?? "CameraNetworkAuditUnavailable",
                RetryAfterIntegrityRecheck: Integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");

        var transactionStarted = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            VerifyNetworkTransaction(database, deadline);
            ValidateNetworkAdmissionInput(work.Admission);
            var events = ReadCameraNetworkEvents(database, _options.CameraNetwork!, deadline);
            var admission = events.SingleOrDefault(item => item.OperationId == work.Admission.Request.OperationId &&
                item.Phase == CameraNetworkEventPhase.Admission);
            AuditChainDatabase.Require(admission is not null, "CameraNetworkAdmissionMissing");
            AuditChainDatabase.Require(!events.Any(item => item.OperationId == work.Admission.Request.OperationId &&
                item.Phase == CameraNetworkEventPhase.Terminal), "CameraNetworkTerminalDuplicate");
            AuditChainDatabase.Require(SameNetworkAdmission(admission!, work.Admission),
                "CameraNetworkAdmissionContextMismatch");
            var persistedFact = ReadFact(database, work.Admission.Fact.AttemptId, 1, deadline);
            AuditChainDatabase.Require(persistedFact is not null &&
                persistedFact.EventId == work.Admission.Fact.EventId &&
                persistedFact.Disposition == CommandDisposition.Accepted &&
                persistedFact.Phase == CommandAuditPhase.Outcome &&
                persistedFact.CommandKind == AuditedCommandKind.ChangeCameraNetworkConfiguration,
                "CameraNetworkAdmissionMissing");

            var state = ReadIdentityState(database, deadline);
            // Completion records an already authorized physical operation. It
            // retains the admission actor even after logout or privilege revocation;
            // it never authorizes or replays another device action.

            var terminalState = work.Snapshot.State;
            AuditChainDatabase.Require(terminalState is CameraNetworkMaintenanceState.Succeeded or
                CameraNetworkMaintenanceState.Failed or CameraNetworkMaintenanceState.Unknown,
                "CameraNetworkTerminalStateInvalid");
            AuditChainDatabase.Require(work.Snapshot.OperationId == work.Admission.Request.OperationId &&
                SameNetworkTarget(work.Snapshot.Target, work.Admission.Request.Target) &&
                SameNetworkConfiguration(work.Snapshot.Previous, work.Admission.ActualPrevious) &&
                SameNetworkConfiguration(work.Snapshot.Requested, work.Admission.Request.Requested),
                "CameraNetworkTerminalBindingMismatch");
            var terminalFact = new CommandAuditFact(Guid.NewGuid(), work.Admission.Fact.AttemptId,
                work.Admission.Fact.CorrelationId, work.Admission.Fact.RuntimeEpoch,
                work.Snapshot.RecordedAtUtc == default ? DateTimeOffset.UtcNow :
                    work.Snapshot.RecordedAtUtc.ToUniversalTime(),
                AuditedCommandKind.ChangeCameraNetworkConfiguration, work.Admission.Fact.Source,
                work.Admission.Fact.ClaimedPrincipalId, work.Admission.Fact.ClaimedSessionId,
                work.Admission.Fact.ClaimedStepUpGrantId,
                terminalState == CameraNetworkMaintenanceState.Succeeded
                    ? CommandAuditPhase.Completed : CommandAuditPhase.Failed,
                null, SafeNetworkReason(work.Snapshot.ReasonCode),
                work.Admission.Fact.AuthenticatedHumanPrincipalId);
            var identity = CreateNetworkIdentityEvent(state.StationId, work.Admission.Request,
                work.Admission.Authorization, terminalFact.OccurredAtUtc,
                IdentityEventKind.CameraSetupOperationCompleted, terminalFact.ReasonCode);
            AuditChainDatabase.EnsureCameraNetworkTransactionCapacity(database, _policy!,
                CameraNetworkEventPhase.Terminal, deadline);
            state.Revision = checked(state.Revision + 1);
            var identitySequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey!,
                BindAuthenticationPolicy(identity with { StateRevision = state.Revision }), deadline,
                AuditChainDatabase.CameraNetworkAuditWriteMode.Terminal);
            var identityTail = AuditChainDatabase.LastIdentityEntry(database, deadline);
            AuditChainDatabase.Require(identityTail is not null && identityTail.Value.Sequence == identitySequence,
                "IdentityAuthorityAuditMismatch");
            state.LastIdentityAuditHash = identityTail!.Value.Hash;
            var protectedState = IdentityStateProtection.Protect(state);
            AuditChainDatabase.Execute(database, "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;", deadline,
                state.Revision.ToString(CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision,
                    identitySequence, _signingKey!));
            AppendIdentityCommandFacts(database, new[] { terminalFact }, work.Admission.Request.OperationId,
                deadline, allowTerminalContinuation: true,
                cameraNetworkMode: AuditChainDatabase.CameraNetworkAuditWriteMode.Terminal);

            var eventValue = new CameraNetworkEvent(0, Guid.NewGuid(), work.Admission.Request.OperationId,
                work.Admission.Fact.AttemptId, work.Admission.Fact.CorrelationId,
                work.Admission.Fact.RuntimeEpoch, CameraNetworkEventPhase.Terminal,
                work.Admission.Request.Target, work.Admission.StationNetwork,
                work.Admission.ActualPrevious, work.Admission.Request.Requested,
                work.Snapshot.Observed, terminalState, work.Snapshot.IdentityVerified,
                terminalFact.ReasonCode, work.Admission.Request.ChangeReason,
                work.Admission.Authorization.PrincipalId,
                work.Admission.Authorization.SessionId,
                work.Admission.Authorization.AuthorizationRevision,
                terminalFact.OccurredAtUtc);
            AppendNetworkEvent(database, eventValue, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            var sequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, sequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying,
                "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new StoreWriteResult(true, "CameraNetworkTerminalPersisted", terminalFact);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("CameraNetwork", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Authorization", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Identity", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Audit", StringComparison.Ordinal))
        { return new StoreWriteResult(false, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new StoreWriteResult(false, SqliteAuditIntegrityQuery.FaultReason(ex, "CameraNetworkTerminalFailed")); }
        finally
        {
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendCameraNetworkRejectedCore(sqlite3 database,
        CameraNetworkRejectedWork work, StoreDeadline deadline)
    {
        if (Integrity?.State != AuditIntegrityState.Verified)
            return new(false, Integrity?.ReasonCode ?? "CameraNetworkAuditUnavailable",
                RetryAfterIntegrityRecheck: Integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");

        var transactionStarted = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            VerifyNetworkTransaction(database, deadline);
            var existing = ReadCameraNetworkEvents(database, _options.CameraNetwork!, deadline)
                .Any(item => item.OperationId == work.Request.OperationId);
            if (existing) return new StoreWriteResult(false, "CameraNetworkOperationConflict");

            var fact = CreateNetworkRejectedFact(work);
            AuditChainDatabase.EnsureCameraNetworkTransactionCapacity(database, _policy!,
                CameraNetworkEventPhase.Rejected, deadline);
            AppendIdentityCommandFacts(database, new[] { fact }, work.Request.OperationId, deadline);
            Guid? actor = work.Authorization is { Authorized: true } auth && auth.PrincipalId != Guid.Empty
                ? auth.PrincipalId : null;
            Guid? session = work.Authorization is { Authorized: true } authorized && authorized.SessionId != Guid.Empty
                ? authorized.SessionId : null;
            var revision = work.Authorization is { Authorized: true } revisionAuthorization
                ? revisionAuthorization.AuthorizationRevision : 0;
            var eventValue = new CameraNetworkEvent(0, Guid.NewGuid(), work.Request.OperationId,
                fact.AttemptId, fact.CorrelationId, work.RuntimeEpoch,
                CameraNetworkEventPhase.Rejected, work.Request.Target, work.StationNetwork,
                work.ActualPrevious, work.Request.Requested, null,
                CameraNetworkMaintenanceState.Failed, false, fact.ReasonCode,
                work.Request.ChangeReason, actor, session, revision, fact.OccurredAtUtc);
            AppendNetworkEvent(database, eventValue, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            var sequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, sequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying,
                "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new StoreWriteResult(true, "CameraNetworkRejectedPersisted", fact);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("CameraNetwork", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Audit", StringComparison.Ordinal))
        { return new StoreWriteResult(false, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new StoreWriteResult(false, SqliteAuditIntegrityQuery.FaultReason(ex, "CameraNetworkRejectedFailed")); }
        finally
        {
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    private void InitializeCameraNetworkSchema(sqlite3 database,
        CameraNetworkStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Execute(database, @"INSERT INTO camera_network_store_config
            (Id,FormatVersion,MaximumEvents,MaximumPayloadBytes,MaximumTotalBytes,MaximumPendingOperations,BindingHash)
            VALUES(1,?,?,?,?,?,?);", deadline, "1",
            options.MaximumEvents.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumPendingOperations.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        _ = AuditChainDatabase.AppendCameraNetworkActivation(database, _policy!, _signingKey!, options, deadline);
    }

    internal static void RequireConfiguredCameraNetwork(sqlite3 database,
        CameraNetworkStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var configs = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaximumEvents,
            MaximumPayloadBytes,MaximumTotalBytes,MaximumPendingOperations,BindingHash
            FROM camera_network_store_config WHERE Id=1;", deadline,
            statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEvents: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                MaximumPendingOperations: SqliteNative.ColumnInt64(statement, 4),
                BindingHash: SqliteNative.ColumnText(statement, 5)!)).ToArray();
        AuditChainDatabase.Require(configs.Length == 1 &&
            configs[0].FormatVersion == CameraNetworkStoreOptions.FormatVersion &&
            configs[0].MaximumEvents == options.MaximumEvents &&
            configs[0].MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configs[0].MaximumTotalBytes == options.MaximumTotalBytes &&
            configs[0].MaximumPendingOperations == options.MaximumPendingOperations &&
            configs[0].BindingHash == options.BindingHash, "CameraNetworkConfigurationMismatch");
    }

    internal static void VerifyCameraNetworkActivationPayload(sqlite3 database, byte[] payload,
        CameraNetworkStoreOptions options, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(payload.SequenceEqual(options.EncodeActivationPayload()),
            "CameraNetworkActivationMismatch");
        RequireConfiguredCameraNetwork(database, options, deadline);
    }

    internal static byte[] ReadAndValidateCameraNetwork(sqlite3 database, long position,
        byte[] auditPayload, StoreDeadline deadline, CameraNetworkStoreOptions options)
    {
        options.Validate();
        var row = ReadCameraNetworkRows(database, "WHERE Position=? LIMIT 1", deadline,
            MaximumEncodedPayloadChars(options.MaximumPayloadBytes),
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null, "CameraNetworkEventMissing");
        byte[] payload;
        try { payload = Convert.FromBase64String(row!.Payload); }
        catch (FormatException ex) { throw new InvalidOperationException("CameraNetworkPayloadInvalid", ex); }
        AuditChainDatabase.Require(payload.SequenceEqual(auditPayload), "CameraNetworkBindingMismatch");
        var value = CameraNetworkStorageCodec.Decode(payload, position);
        AuditChainDatabase.Require(CameraNetworkStorageCodec.Encode(value).SequenceEqual(payload) &&
            CameraNetworkStorageCodec.PayloadHash(payload) == row.PayloadHash,
            "CameraNetworkBindingMismatch");
        ValidateNetworkRow(row, value);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
            "CameraNetworkPayloadCapacityExceeded");
        return payload;
    }

    private static List<CameraNetworkEvent> ReadCameraNetworkEvents(sqlite3 database,
        CameraNetworkStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        // Keep the result bounded before SQLite materializes any payload.  The
        // extra row distinguishes an exact-capacity ledger from an oversized
        // one without allocating an unbounded list.
        var limit = checked((long)options.MaximumEvents + 1);
        return SqliteNative.WithStatement(database, @"SELECT Position,EventId,OperationId,AttemptId,
            CorrelationId,RuntimeEpoch,Phase,TargetProviderId,TargetProviderVersion,
            TargetAdapterPackageId,TargetAdapterVersion,TargetStableDeviceIdentity,TargetContentHash,
            StationInterfaceId,StationAddress,StationPrefixLength,StationGateway,PreviousAddress,
            PreviousPrefixLength,PreviousGateway,RequestedAddress,RequestedPrefixLength,RequestedGateway,
            ObservedAddress,ObservedPrefixLength,ObservedGateway,State,IdentityVerified,ReasonCode,
            ChangeReason,ActorPrincipalId,SessionId,AuthorizationRevision,RecordedAtUtc,Payload,PayloadHash,
            length(Payload) FROM camera_network_events ORDER BY Position LIMIT ?;", deadline, statement =>
        {
            SqliteNative.BindInt64(database, statement, 1, limit);
            var events = new List<CameraNetworkEvent>(Math.Min(options.MaximumEvents, 256));
            long totalBytes = 0;
            while (SqliteNative.Step(database, statement, deadline) == raw.SQLITE_ROW)
            {
                if (events.Count >= options.MaximumEvents)
                    throw new InvalidOperationException("CameraNetworkEventCapacityExceeded");
                var encodedLimit = MaximumEncodedPayloadChars(options.MaximumPayloadBytes);
                AuditChainDatabase.Require(SqliteNative.ColumnInt64(statement, 36) <= encodedLimit,
                    "CameraNetworkPayloadCapacityExceeded");
                var row = ReadCameraNetworkRow(statement, encodedLimit);
                var decodedLength = checked(row.Payload.Length / 4 * 3 -
                    (row.Payload.EndsWith("==", StringComparison.Ordinal) ? 2 :
                        row.Payload.EndsWith("=", StringComparison.Ordinal) ? 1 : 0));
                AuditChainDatabase.Require(row.Payload.Length % 4 == 0 && decodedLength > 0 &&
                    decodedLength <= options.MaximumPayloadBytes, "CameraNetworkPayloadCapacityExceeded");
                AuditChainDatabase.Require(checked(totalBytes + decodedLength) <= options.MaximumTotalBytes,
                    "CameraNetworkTotalCapacityExceeded");
                byte[] payload;
                try { payload = Convert.FromBase64String(row.Payload); }
                catch (FormatException ex)
                { throw new InvalidOperationException("CameraNetworkPayloadInvalid", ex); }
                AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
                    "CameraNetworkPayloadCapacityExceeded");
                totalBytes = checked(totalBytes + payload.Length);
                AuditChainDatabase.Require(totalBytes <= options.MaximumTotalBytes,
                    "CameraNetworkTotalCapacityExceeded");
                var value = CameraNetworkStorageCodec.Decode(payload, row.Position);
                AuditChainDatabase.Require(CameraNetworkStorageCodec.PayloadHash(payload) == row.PayloadHash &&
                    CameraNetworkStorageCodec.Encode(value).SequenceEqual(payload),
                    "CameraNetworkBindingMismatch");
                ValidateNetworkRow(row, value);
                events.Add(value);
            }
            return events;
        });
    }

    internal static void ValidateCameraNetworkHistory(sqlite3 database,
        CameraNetworkStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        RequireConfiguredCameraNetwork(database, options, deadline);
        var events = ReadCameraNetworkEvents(database, options, deadline);
        var eventIds = new HashSet<Guid>();
        var operations = new Dictionary<Guid, CameraNetworkEvent>();
        long expectedPosition = 1;
        foreach (var value in events)
        {
            AuditChainDatabase.Require(value.Position == expectedPosition++,
                "CameraNetworkPositionGap");
            AuditChainDatabase.Require(eventIds.Add(value.EventId), "CameraNetworkEventConflict");
            if (value.Phase == CameraNetworkEventPhase.Admission)
            {
                AuditChainDatabase.Require(!operations.ContainsKey(value.OperationId),
                    "CameraNetworkOperationConflict");
                operations.Add(value.OperationId, value);
            }
            else if (value.Phase == CameraNetworkEventPhase.Terminal)
            {
                AuditChainDatabase.Require(operations.TryGetValue(value.OperationId, out var admission) &&
                    SameNetworkAdmission(admission!, value), "CameraNetworkAdmissionBindingMismatch");
                operations.Remove(value.OperationId);
            }
            else
            {
                AuditChainDatabase.Require(!operations.ContainsKey(value.OperationId),
                    "CameraNetworkOperationConflict");
                operations.Add(value.OperationId, value);
                operations.Remove(value.OperationId);
            }
        }
        AuditChainDatabase.Require(operations.Count <= options.MaximumPendingOperations,
            "CameraNetworkPendingCapacityExceeded");
        ValidateCameraNetworkCrossChain(database, events, deadline);
    }

    private static void ValidateCameraNetworkCrossChain(sqlite3 database,
        IReadOnlyList<CameraNetworkEvent> events, StoreDeadline deadline)
    {
        var stationId = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1;", deadline);
        AuditChainDatabase.Require(stationId is { Length: > 0 },
            "CameraNetworkAuthorizationBindingMismatch");
        var identities = AuditChainDatabase.Read(database, @"
            SELECT IdentityPosition,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY IdentityPosition;",
            deadline, statement =>
        {
            var ordinal = SqliteNative.ColumnInt64(statement, 0);
            var encoded = SqliteNative.ColumnText(statement, 1);
            AuditChainDatabase.Require(encoded is { Length: > 0 },
                "CameraNetworkAuthorizationBindingMismatch");
            return (Ordinal: ordinal, Payload: Convert.FromBase64String(encoded!));
        });
        var authorizations = new List<CameraAuthorizationAudit>();
        foreach (var identity in identities)
        {
            if (IdentityAuditEvent.TryReadCameraNetworkAuthorization(identity.Payload,
                    identity.Ordinal, stationId!, out var binding))
                authorizations.Add(binding);
        }
        var commands = AuditChainDatabase.Read(database, @"
            SELECT AttemptId,CorrelationId,RuntimeEpoch,CommandKind,Source,
                ClaimedPrincipalId,ClaimedSessionId,ClaimedStepUpGrantId,Phase,Disposition,
                ReasonCode,AuthenticatedHumanPrincipalId FROM command_facts
            WHERE CommandKind=18 ORDER BY Position;", deadline, ReadNetworkCommandAudit);

        foreach (var value in events)
        {
            if (value.Phase == CameraNetworkEventPhase.Rejected)
            {
                var rejected = commands.SingleOrDefault(item => item.AttemptId == value.AttemptId &&
                    item.Phase == CommandAuditPhase.Outcome && item.Disposition == CommandDisposition.Rejected);
                AuditChainDatabase.Require(rejected is not null &&
                    rejected.CorrelationId == value.OperationId && rejected.ReasonCode == value.ReasonCode &&
                    rejected.AuthenticatedHumanPrincipalId == value.ActorPrincipalId?.ToString("D") &&
                    (value.ActorPrincipalId is null
                        ? value.SessionId is null && value.AuthorizationRevision == 0
                        : value.SessionId == rejected.ClaimedSessionId),
                    "CameraNetworkCommandBindingMismatch");
                continue;
            }
            var expectedIdentityKind = value.Phase == CameraNetworkEventPhase.Admission
                ? IdentityEventKind.CameraSetupActionAuthorized
                : IdentityEventKind.CameraSetupOperationCompleted;
            var identity = authorizations.SingleOrDefault(item => item.Kind == expectedIdentityKind &&
                item.OperationId == value.OperationId && item.ReasonCode == value.ReasonCode);
            AuditChainDatabase.Require(identity is not null &&
                identity.PrincipalId == value.ActorPrincipalId &&
                identity.ActorPrincipalId == value.ActorPrincipalId &&
                identity.CommandCorrelationId == value.OperationId &&
                identity.BoundCommandCorrelationId == value.OperationId &&
                identity.RequiredPermission == Permission.ManageCameraBindings.ToString() &&
                identity.ActionTargetId == value.Target.ContentHash &&
                identity.CommandKind == AuditedCommandKind.ChangeCameraNetworkConfiguration &&
                identity.SessionId == value.SessionId &&
                identity.AuthorizationRevision == value.AuthorizationRevision,
                "CameraNetworkAuthorizationBindingMismatch");
            var command = commands.SingleOrDefault(item => item.AttemptId == value.AttemptId &&
                item.CorrelationId == value.OperationId &&
                item.Phase == (value.Phase == CameraNetworkEventPhase.Admission
                    ? CommandAuditPhase.Outcome
                    : value.State == CameraNetworkMaintenanceState.Succeeded
                        ? CommandAuditPhase.Completed : CommandAuditPhase.Failed) &&
                item.ReasonCode == value.ReasonCode);
            AuditChainDatabase.Require(command is not null &&
                command.ClaimedPrincipalId == value.ActorPrincipalId?.ToString("D") &&
                command.ClaimedSessionId == value.SessionId &&
                command.AuthenticatedHumanPrincipalId == value.ActorPrincipalId?.ToString("D") &&
                (value.Phase != CameraNetworkEventPhase.Admission ||
                    command.Disposition == CommandDisposition.Accepted) &&
                (value.Phase == CameraNetworkEventPhase.Admission || command.Disposition is null),
                "CameraNetworkCommandBindingMismatch");
            if (value.Phase == CameraNetworkEventPhase.Terminal)
            {
                var admissionIdentity = authorizations.SingleOrDefault(item =>
                    item.Kind == IdentityEventKind.CameraSetupActionAuthorized &&
                    item.OperationId == value.OperationId);
                var admissionCommand = commands.SingleOrDefault(item =>
                    item.AttemptId == value.AttemptId && item.Phase == CommandAuditPhase.Outcome &&
                    item.Disposition == CommandDisposition.Accepted);
                AuditChainDatabase.Require(admissionIdentity is not null && admissionCommand is not null &&
                    SameNetworkAuthorization(admissionIdentity, identity!) &&
                    SameNetworkCommand(admissionCommand, command!),
                    "CameraNetworkAdmissionBindingMismatch");
            }
        }
    }

    private static NetworkCommandAudit ReadNetworkCommandAudit(sqlite3_stmt statement)
    {
        var attempt = ParseNetworkGuid(SqliteNative.ColumnText(statement, 0), "CameraNetworkCommandBindingMismatch");
        var correlation = ParseNetworkGuid(SqliteNative.ColumnText(statement, 1), "CameraNetworkCommandBindingMismatch");
        var epoch = ParseNetworkGuid(SqliteNative.ColumnText(statement, 2), "CameraNetworkCommandBindingMismatch");
        var commandKind = SqliteNative.ColumnInt64(statement, 3);
        AuditChainDatabase.Require(commandKind == (int)AuditedCommandKind.ChangeCameraNetworkConfiguration,
            "CameraNetworkCommandBindingMismatch");
        var sourceText = SqliteNative.ColumnText(statement, 4);
        CommandSource? source = null;
        if (sourceText is not null)
        {
            AuditChainDatabase.Require(int.TryParse(sourceText, NumberStyles.None,
                CultureInfo.InvariantCulture, out var sourceValue) &&
                Enum.IsDefined(typeof(CommandSource), sourceValue), "CameraNetworkCommandBindingMismatch");
            source = (CommandSource)sourceValue;
        }
        var principal = SqliteNative.ColumnText(statement, 5);
        var session = ParseNetworkNullableGuid(SqliteNative.ColumnText(statement, 6),
            "CameraNetworkCommandBindingMismatch");
        var grant = ParseNetworkNullableGuid(SqliteNative.ColumnText(statement, 7),
            "CameraNetworkCommandBindingMismatch");
        var phaseValue = SqliteNative.ColumnInt64(statement, 8);
        var dispositionText = SqliteNative.ColumnText(statement, 9);
        CommandDisposition? disposition = null;
        if (dispositionText is not null)
        {
            AuditChainDatabase.Require(long.TryParse(dispositionText, NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) &&
                Enum.IsDefined(typeof(CommandDisposition), (int)value),
                "CameraNetworkCommandBindingMismatch");
            disposition = (CommandDisposition)value;
        }
        AuditChainDatabase.Require(Enum.IsDefined(typeof(CommandAuditPhase), (int)phaseValue),
            "CameraNetworkCommandBindingMismatch");
        var reason = SqliteNative.ColumnText(statement, 10);
        AuditChainDatabase.Require(reason is { Length: > 0 and <= 256 },
            "CameraNetworkCommandBindingMismatch");
        var authenticated = SqliteNative.ColumnText(statement, 11);
        return new NetworkCommandAudit(attempt, correlation, epoch, source, principal, session,
            grant, (CommandAuditPhase)phaseValue, disposition, reason!, authenticated);
    }

    private void AppendNetworkEvent(sqlite3 database, CameraNetworkEvent input,
        StoreDeadline deadline)
    {
        var options = _options.CameraNetwork ?? throw new InvalidOperationException("CameraNetworkUnavailable");
        var count = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM camera_network_events;", deadline);
        var value = input with
        {
            Position = checked(count + 1),
            RecordedAtUtc = input.RecordedAtUtc == default
                ? DateTimeOffset.UtcNow : input.RecordedAtUtc.ToUniversalTime()
        };
        CameraNetworkStorageCodec.Validate(value);
        var payload = CameraNetworkStorageCodec.Encode(value);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
            "CameraNetworkPayloadCapacityExceeded");
        var pending = AuditChainDatabase.Scalar(database, @"
            SELECT COUNT(*) FROM camera_network_events admission
            WHERE admission.Phase=0 AND NOT EXISTS(
                SELECT 1 FROM camera_network_events terminal
                WHERE terminal.OperationId=admission.OperationId AND terminal.Phase=1);", deadline);
        var reservedTerminals = value.Phase == CameraNetworkEventPhase.Admission ? pending + 1 :
            value.Phase == CameraNetworkEventPhase.Terminal ? pending - 1 : pending;
        AuditChainDatabase.Require(reservedTerminals >= 0, "CameraNetworkAdmissionMissing");
        if (value.Phase == CameraNetworkEventPhase.Admission)
        {
            AuditChainDatabase.Require(reservedTerminals <= options.MaximumPendingOperations,
                "CameraNetworkPendingCapacityExceeded");
            // Reserve enough single-record space for every possible terminal,
            // including a longer safe reason and the largest valid readback.
            var terminal = value with { Position = options.MaximumEvents,
                Phase = CameraNetworkEventPhase.Terminal, State = CameraNetworkMaintenanceState.Unknown,
                IdentityVerified = true, ReasonCode = new string('R', 128),
                Observed = new CameraIpv4Configuration("223.255.255.253", 30, "223.255.255.254") };
            var successfulTerminal = terminal with { State = CameraNetworkMaintenanceState.Succeeded,
                Observed = value.Requested };
            AuditChainDatabase.Require(Math.Max(CameraNetworkStorageCodec.Encode(terminal).Length,
                CameraNetworkStorageCodec.Encode(successfulTerminal).Length) <= options.MaximumPayloadBytes,
                "CameraNetworkPayloadCapacityExceeded");
        }
        // Payload is stored as base64 text.  Count the decoded bytes so the
        // configured total bound is stable across the SQLite row and the
        // canonical signed bytes verified by ValidateCameraNetworkHistory.
        var total = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(
                (length(Payload) / 4) * 3 -
                (length(Payload) - length(rtrim(Payload, '=')))), 0)
            FROM camera_network_events;", deadline);
        AuditChainDatabase.Require(checked(total + payload.Length +
            reservedTerminals * options.MaximumPayloadBytes) <= options.MaximumTotalBytes,
            "CameraNetworkTotalCapacityExceeded");
        AuditChainDatabase.Require(checked(count + 1 + reservedTerminals) <= options.MaximumEvents,
            "CameraNetworkEventCapacityExceeded");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM camera_network_events WHERE OperationId=? AND Phase=?;", deadline,
            value.OperationId.ToString("D"), ((int)value.Phase).ToString(CultureInfo.InvariantCulture)) == 0,
            "CameraNetworkOperationConflict");
        InsertCameraNetworkRow(database, value, payload, deadline);
        _ = AuditChainDatabase.AppendCameraNetworkEvent(database, _policy!, _signingKey!,
            value.Position, deadline);
    }

    private static void InsertCameraNetworkRow(sqlite3 database, CameraNetworkEvent value,
        byte[] payload, StoreDeadline deadline)
    {
        const string sql = @"INSERT INTO camera_network_events(
            Position,EventId,OperationId,AttemptId,CorrelationId,RuntimeEpoch,Phase,
            TargetProviderId,TargetProviderVersion,TargetAdapterPackageId,TargetAdapterVersion,
            TargetStableDeviceIdentity,TargetContentHash,StationInterfaceId,StationAddress,
            StationPrefixLength,StationGateway,PreviousAddress,PreviousPrefixLength,PreviousGateway,
            RequestedAddress,RequestedPrefixLength,RequestedGateway,ObservedAddress,
            ObservedPrefixLength,ObservedGateway,State,IdentityVerified,ReasonCode,ChangeReason,
            ActorPrincipalId,SessionId,AuthorizationRevision,RecordedAtUtc,Payload,PayloadHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);";
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            var index = 1;
            var station = value.StationNetwork;
            SqliteNative.BindInt64(database, statement, index++, value.Position);
            SqliteNative.BindGuid(database, statement, index++, value.EventId);
            SqliteNative.BindGuid(database, statement, index++, value.OperationId);
            SqliteNative.BindGuid(database, statement, index++, value.AttemptId);
            SqliteNative.BindGuid(database, statement, index++, value.CorrelationId);
            SqliteNative.BindGuid(database, statement, index++, value.RuntimeEpoch);
            SqliteNative.BindInt(database, statement, index++, (int)value.Phase);
            SqliteNative.BindText(database, statement, index++, value.Target.Provider.Id);
            SqliteNative.BindText(database, statement, index++, value.Target.Provider.Version);
            SqliteNative.BindText(database, statement, index++, value.Target.Provider.AdapterPackageId);
            SqliteNative.BindText(database, statement, index++, value.Target.Provider.AdapterVersion);
            SqliteNative.BindText(database, statement, index++, value.Target.StableDeviceIdentity);
            SqliteNative.BindText(database, statement, index++, value.Target.ContentHash);
            SqliteNative.BindText(database, statement, index++, station?.InterfaceId);
            SqliteNative.BindText(database, statement, index++, station?.Configuration.Address);
            BindNullableInt(database, statement, index++, station?.Configuration.PrefixLength);
            SqliteNative.BindText(database, statement, index++, station?.Configuration.Gateway);
            SqliteNative.BindText(database, statement, index++, value.Previous?.Address);
            BindNullableInt(database, statement, index++, value.Previous?.PrefixLength);
            SqliteNative.BindText(database, statement, index++, value.Previous?.Gateway);
            SqliteNative.BindText(database, statement, index++, value.Requested.Address);
            SqliteNative.BindInt(database, statement, index++, value.Requested.PrefixLength);
            SqliteNative.BindText(database, statement, index++, value.Requested.Gateway);
            SqliteNative.BindText(database, statement, index++, value.Observed?.Address);
            BindNullableInt(database, statement, index++, value.Observed?.PrefixLength);
            SqliteNative.BindText(database, statement, index++, value.Observed?.Gateway);
            SqliteNative.BindInt(database, statement, index++, (int)value.State);
            SqliteNative.BindInt(database, statement, index++, value.IdentityVerified ? 1 : 0);
            SqliteNative.BindText(database, statement, index++, value.ReasonCode);
            SqliteNative.BindText(database, statement, index++, value.ChangeReason);
            BindNullableGuid(database, statement, index++, value.ActorPrincipalId);
            BindNullableGuid(database, statement, index++, value.SessionId);
            SqliteNative.BindInt64(database, statement, index++, value.AuthorizationRevision);
            SqliteNative.BindText(database, statement, index++, FormatNetworkTime(value.RecordedAtUtc));
            SqliteNative.BindText(database, statement, index++, Convert.ToBase64String(payload));
            SqliteNative.BindText(database, statement, index, CameraNetworkStorageCodec.PayloadHash(payload));
            SqliteNative.Step(database, statement, deadline);
            return 0;
        });
    }

    internal static List<CameraNetworkRow> ReadCameraNetworkRows(sqlite3 database,
        string suffix, StoreDeadline deadline, params string?[] args) =>
        ReadCameraNetworkRows(database, suffix, deadline,
            CameraNetworkStorageCodec.MaximumEncodedPayloadChars, args);

    private static List<CameraNetworkRow> ReadCameraNetworkRows(sqlite3 database,
        string suffix, StoreDeadline deadline, int maximumEncodedPayloadChars,
        params string?[] args) =>
        AuditChainDatabase.Read(database, @"SELECT Position,EventId,OperationId,AttemptId,
            CorrelationId,RuntimeEpoch,Phase,TargetProviderId,TargetProviderVersion,
            TargetAdapterPackageId,TargetAdapterVersion,TargetStableDeviceIdentity,TargetContentHash,
            StationInterfaceId,StationAddress,StationPrefixLength,StationGateway,PreviousAddress,
            PreviousPrefixLength,PreviousGateway,RequestedAddress,RequestedPrefixLength,RequestedGateway,
            ObservedAddress,ObservedPrefixLength,ObservedGateway,State,IdentityVerified,ReasonCode,
            ChangeReason,ActorPrincipalId,SessionId,AuthorizationRevision,RecordedAtUtc,Payload,PayloadHash,
            length(Payload)
            FROM camera_network_events " + suffix + ";", deadline, statement =>
        ReadCameraNetworkRow(statement, maximumEncodedPayloadChars), args);

    private static CameraNetworkRow ReadCameraNetworkRow(sqlite3_stmt statement,
        int maximumEncodedPayloadChars)
    {
        AuditChainDatabase.Require(SqliteNative.ColumnInt64(statement, 36) >= 0 &&
            SqliteNative.ColumnInt64(statement, 36) <= maximumEncodedPayloadChars,
            "CameraNetworkPayloadCapacityExceeded");
        return new(SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1)!,
            SqliteNative.ColumnText(statement, 2)!, SqliteNative.ColumnText(statement, 3)!,
            SqliteNative.ColumnText(statement, 4)!, SqliteNative.ColumnText(statement, 5)!,
            SqliteNative.ColumnInt64(statement, 6), SqliteNative.ColumnText(statement, 7)!,
            SqliteNative.ColumnText(statement, 8)!, SqliteNative.ColumnText(statement, 9)!,
            SqliteNative.ColumnText(statement, 10)!, SqliteNative.ColumnText(statement, 11)!,
            SqliteNative.ColumnText(statement, 12)!, SqliteNative.ColumnText(statement, 13),
            SqliteNative.ColumnText(statement, 14), SqliteNative.ColumnInt64Nullable(statement, 15),
            SqliteNative.ColumnText(statement, 16), SqliteNative.ColumnText(statement, 17),
            SqliteNative.ColumnInt64Nullable(statement, 18), SqliteNative.ColumnText(statement, 19),
            SqliteNative.ColumnText(statement, 20)!, SqliteNative.ColumnInt64(statement, 21),
            SqliteNative.ColumnText(statement, 22), SqliteNative.ColumnText(statement, 23),
            SqliteNative.ColumnInt64Nullable(statement, 24), SqliteNative.ColumnText(statement, 25),
            SqliteNative.ColumnInt64(statement, 26), SqliteNative.ColumnInt64(statement, 27),
            SqliteNative.ColumnText(statement, 28)!, SqliteNative.ColumnText(statement, 29)!,
            SqliteNative.ColumnText(statement, 30), SqliteNative.ColumnText(statement, 31),
            SqliteNative.ColumnInt64(statement, 32), SqliteNative.ColumnText(statement, 33)!,
            SqliteNative.ColumnText(statement, 34)!, SqliteNative.ColumnText(statement, 35)!);
    }

    private static int MaximumEncodedPayloadChars(int maximumPayloadBytes) =>
        Math.Min(CameraNetworkStorageCodec.MaximumEncodedPayloadChars,
            checked((maximumPayloadBytes + 2) / 3 * 4));

    private static void ValidateNetworkRow(CameraNetworkRow row, CameraNetworkEvent value)
    {
        AuditChainDatabase.Require(row.Position == value.Position && row.EventId == value.EventId.ToString("D") &&
            row.OperationId == value.OperationId.ToString("D") && row.AttemptId == value.AttemptId.ToString("D") &&
            row.CorrelationId == value.CorrelationId.ToString("D") && row.RuntimeEpoch == value.RuntimeEpoch.ToString("D") &&
            row.Phase == (long)value.Phase && row.TargetProviderId == value.Target.Provider.Id &&
            row.TargetProviderVersion == value.Target.Provider.Version &&
            row.TargetAdapterPackageId == value.Target.Provider.AdapterPackageId &&
            row.TargetAdapterVersion == value.Target.Provider.AdapterVersion &&
            row.TargetStableDeviceIdentity == value.Target.StableDeviceIdentity &&
            row.TargetContentHash == value.Target.ContentHash &&
            row.StationInterfaceId == value.StationNetwork?.InterfaceId &&
            row.StationAddress == value.StationNetwork?.Configuration.Address &&
            row.StationPrefixLength == value.StationNetwork?.Configuration.PrefixLength &&
            row.StationGateway == value.StationNetwork?.Configuration.Gateway &&
            row.PreviousAddress == value.Previous?.Address &&
            row.PreviousPrefixLength == value.Previous?.PrefixLength &&
            row.PreviousGateway == value.Previous?.Gateway &&
            row.RequestedAddress == value.Requested.Address &&
            row.RequestedPrefixLength == value.Requested.PrefixLength &&
            row.RequestedGateway == value.Requested.Gateway &&
            row.ObservedAddress == value.Observed?.Address &&
            row.ObservedPrefixLength == value.Observed?.PrefixLength &&
            row.ObservedGateway == value.Observed?.Gateway && row.State == (long)value.State &&
            row.IdentityVerified == (value.IdentityVerified ? 1 : 0) && row.ReasonCode == value.ReasonCode &&
            row.ChangeReason == value.ChangeReason && row.ActorPrincipalId == value.ActorPrincipalId?.ToString("D") &&
            row.SessionId == value.SessionId?.ToString("D") && row.AuthorizationRevision == value.AuthorizationRevision &&
            row.RecordedAtUtc == FormatNetworkTime(value.RecordedAtUtc), "CameraNetworkBindingMismatch");
    }

    private static CameraNetworkSnapshot SnapshotFor(CameraNetworkEvent value) => new(value.OperationId,
        value.Target, value.State, value.Previous, value.Requested, value.Observed,
        value.IdentityVerified, value.ReasonCode, value.RecordedAtUtc);

    private static List<CameraNetworkEvent> FindPendingNetworkAdmissions(
        IReadOnlyList<CameraNetworkEvent> events)
    {
        var pending = new Dictionary<Guid, CameraNetworkEvent>();
        foreach (var value in events)
        {
            if (value.Phase == CameraNetworkEventPhase.Admission) pending[value.OperationId] = value;
            else pending.Remove(value.OperationId);
        }
        return pending.Values.ToList();
    }

    private static bool SameNetworkAdmission(CameraNetworkEvent value,
        CameraNetworkAdmission admission) =>
        value.Phase == CameraNetworkEventPhase.Admission &&
        value.OperationId == admission.Request.OperationId && value.AttemptId == admission.Fact.AttemptId &&
        value.CorrelationId == admission.Fact.CorrelationId && value.RuntimeEpoch == admission.Fact.RuntimeEpoch &&
        SameNetworkTarget(value.Target, admission.Request.Target) &&
        SameNetworkStation(value.StationNetwork, admission.StationNetwork) &&
        SameNetworkConfiguration(value.Previous, admission.ActualPrevious) &&
        SameNetworkConfiguration(value.Requested, admission.Request.Requested) &&
        value.ActorPrincipalId == admission.Authorization.PrincipalId &&
        value.SessionId == admission.Authorization.SessionId &&
        value.AuthorizationRevision == admission.Authorization.AuthorizationRevision;

    private static bool SameNetworkAdmission(CameraNetworkEvent admission,
        CameraNetworkEvent terminal) =>
        admission.OperationId == terminal.OperationId && admission.AttemptId == terminal.AttemptId &&
        admission.CorrelationId == terminal.CorrelationId && admission.RuntimeEpoch == terminal.RuntimeEpoch &&
        SameNetworkTarget(admission.Target, terminal.Target) &&
        SameNetworkStation(admission.StationNetwork, terminal.StationNetwork) &&
        SameNetworkConfiguration(admission.Previous, terminal.Previous) &&
        SameNetworkConfiguration(admission.Requested, terminal.Requested) &&
        admission.ActorPrincipalId == terminal.ActorPrincipalId && admission.SessionId == terminal.SessionId &&
        admission.AuthorizationRevision == terminal.AuthorizationRevision;

    private static bool SameNetworkTarget(CameraBindingTarget left, CameraBindingTarget right) =>
        left.ContentHash == right.ContentHash && left.StableDeviceIdentity == right.StableDeviceIdentity &&
        left.Provider == right.Provider;

    private static bool SameNetworkConfiguration(CameraIpv4Configuration? left,
        CameraIpv4Configuration? right) => left is null && right is null ||
        left is not null && right is not null && left.Address == right.Address &&
        left.PrefixLength == right.PrefixLength && left.Gateway == right.Gateway;

    private static bool SameNetworkStation(CameraStationNetwork? left,
        CameraStationNetwork? right) => left is null && right is null || left is not null && right is not null &&
        left.InterfaceId == right.InterfaceId && SameNetworkConfiguration(left.Configuration, right.Configuration);

    private static string FormatNetworkTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static CommandAuditFact CreateNetworkAdmissionFact(CameraNetworkAdmissionWork work)
    {
        var invocation = work.Request.Invocation;
        return new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), work.Request.OperationId,
            work.RuntimeEpoch, DateTimeOffset.UtcNow,
            AuditedCommandKind.ChangeCameraNetworkConfiguration, invocation.Source,
            invocation.PrincipalId, invocation.SessionId, invocation.StepUpGrantId,
            CommandAuditPhase.Outcome, CommandDisposition.Accepted,
            "CameraNetworkChangeAdmitted", work.Authorization.PrincipalId.ToString("D"));
    }

    private static CommandAuditFact CreateNetworkRejectedFact(CameraNetworkRejectedWork work)
    {
        var invocation = work.Request.Invocation;
        Guid? principal = work.Authorization is { Authorized: true } authorization
            ? authorization.PrincipalId : null;
        return new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), work.Request.OperationId,
            work.RuntimeEpoch == Guid.Empty ? Guid.NewGuid() : work.RuntimeEpoch,
            DateTimeOffset.UtcNow, AuditedCommandKind.ChangeCameraNetworkConfiguration,
            invocation is not null && Enum.IsDefined(invocation.Source) ? invocation.Source : null,
            invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
            invocation?.SessionId, invocation?.StepUpGrantId, CommandAuditPhase.Outcome,
            CommandDisposition.Rejected, SafeNetworkReason(work.Reason), principal?.ToString("D"));
    }

    private static IdentityAuditEvent CreateNetworkIdentityEvent(string stationId,
        CameraNetworkChangeRequest request,
        CameraSetupAuthorization authorization, DateTimeOffset occurredAt,
        IdentityEventKind kind, string reason) => new(Guid.NewGuid(), kind, occurredAt,
        stationId, authorization.PrincipalId, null, null, null, SafeNetworkReason(reason),
        ActorPrincipalId: authorization.PrincipalId, CommandCorrelationId: request.OperationId,
        StepUpGrantId: request.Invocation.StepUpGrantId,
        RequiredPermission: Permission.ManageCameraBindings.ToString(),
        AuthorizationRevision: authorization.AuthorizationRevision,
        ActionTargetId: request.AuthorizationTargetId,
        BoundCommandCorrelationId: request.OperationId,
        ActionCommandKind: AuditedCommandKind.ChangeCameraNetworkConfiguration.ToString(),
        OperationId: request.OperationId, SessionId: authorization.SessionId);

    private static bool InvocationMatches(CommandInvocation invocation,
        CameraSetupAuthorization authorization) => invocation is not null &&
        invocation.PrincipalId == authorization.PrincipalId.ToString("D") &&
        invocation.SessionId == authorization.SessionId &&
        authorization.PrincipalId != Guid.Empty && authorization.SessionId != Guid.Empty;

    private static Guid? ParseInvocationGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty ? result : null;

    private static string SafeNetworkReason(string? reason) => reason is { Length: > 0 and <= 128 } &&
        reason.All(static character => character is >= 'A' and <= 'Z' || character is >= 'a' and <= 'z' ||
            character is >= '0' and <= '9' || character is '_' or '-') ? reason : "CameraNetworkUnavailable";

    private static void ValidateNetworkAdmissionInput(CameraNetworkAdmission admission)
    {
        AuditChainDatabase.Require(admission.Fact.Phase == CommandAuditPhase.Outcome &&
            admission.Fact.Disposition == CommandDisposition.Accepted &&
            admission.Fact.CommandKind == AuditedCommandKind.ChangeCameraNetworkConfiguration &&
            admission.Fact.AttemptId != Guid.Empty && admission.Fact.CorrelationId == admission.Request.OperationId &&
            admission.RuntimeEpoch == admission.Fact.RuntimeEpoch && admission.RuntimeEpoch != Guid.Empty &&
            admission.Authorization.Authorized && admission.Authorization.PrincipalId != Guid.Empty &&
            admission.Authorization.SessionId != Guid.Empty && admission.Authorization.Reservation is not null,
            "CameraNetworkAdmissionInvalid");
    }

    private static bool SameNetworkAuthorization(CameraAuthorizationAudit first,
        CameraAuthorizationAudit second) => first.PrincipalId == second.PrincipalId &&
        first.ActorPrincipalId == second.ActorPrincipalId && first.CommandCorrelationId == second.CommandCorrelationId &&
        first.StepUpGrantId == second.StepUpGrantId && first.RequiredPermission == second.RequiredPermission &&
        first.ActionTargetId == second.ActionTargetId && first.BoundCommandCorrelationId == second.BoundCommandCorrelationId &&
        first.CommandKind == second.CommandKind && first.OperationId == second.OperationId &&
        first.SessionId == second.SessionId && first.AuthorizationRevision == second.AuthorizationRevision;

    private static bool SameNetworkCommand(NetworkCommandAudit first,
        NetworkCommandAudit second) => first.AttemptId == second.AttemptId &&
        first.CorrelationId == second.CorrelationId && first.RuntimeEpoch == second.RuntimeEpoch &&
        first.Source == second.Source &&
        first.ClaimedPrincipalId == second.ClaimedPrincipalId &&
        first.ClaimedSessionId == second.ClaimedSessionId &&
        first.ClaimedStepUpGrantId == second.ClaimedStepUpGrantId &&
        first.AuthenticatedHumanPrincipalId == second.AuthenticatedHumanPrincipalId;

    private static Guid ParseNetworkGuid(string? value, string reason) =>
        Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty
            ? result : throw new InvalidOperationException(reason);

    private static Guid? ParseNetworkNullableGuid(string? value, string reason) =>
        value is null ? null : ParseNetworkGuid(value, reason);

    private static void BindNullableGuid(sqlite3 database, sqlite3_stmt statement,
        int index, Guid? value)
    {
        if (value is null) raw.sqlite3_bind_null(statement, index);
        else SqliteNative.BindGuid(database, statement, index, value.Value);
    }

    private static void BindNullableInt(sqlite3 database, sqlite3_stmt statement,
        int index, int? value)
    {
        if (value is null) raw.sqlite3_bind_null(statement, index);
        else SqliteNative.BindInt(database, statement, index, value.Value);
    }

    private sealed record NetworkCommandAudit(Guid AttemptId, Guid CorrelationId,
        Guid RuntimeEpoch, CommandSource? Source, string? ClaimedPrincipalId,
        Guid? ClaimedSessionId, Guid? ClaimedStepUpGrantId, CommandAuditPhase Phase,
        CommandDisposition? Disposition, string ReasonCode, string? AuthenticatedHumanPrincipalId);

    internal sealed record CameraNetworkRow(long Position, string EventId, string OperationId,
        string AttemptId, string CorrelationId, string RuntimeEpoch, long Phase,
        string TargetProviderId, string TargetProviderVersion, string TargetAdapterPackageId,
        string TargetAdapterVersion, string TargetStableDeviceIdentity, string TargetContentHash,
        string? StationInterfaceId, string? StationAddress, long? StationPrefixLength,
        string? StationGateway, string? PreviousAddress, long? PreviousPrefixLength,
        string? PreviousGateway, string RequestedAddress, long RequestedPrefixLength,
        string? RequestedGateway, string? ObservedAddress, long? ObservedPrefixLength,
        string? ObservedGateway, long State, long IdentityVerified, string ReasonCode,
        string ChangeReason, string? ActorPrincipalId, string? SessionId,
        long AuthorizationRevision, string RecordedAtUtc, string Payload, string PayloadHash);
}
