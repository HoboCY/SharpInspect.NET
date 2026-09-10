using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed class PreviewDraftSavedWork
{
    internal PreviewDraftSavedWork(PreviewSessionHeader header, CommandAuditFact startFact,
        SavePreviewToDraftCommand command, RecipeDraftRevision committedDraft)
    {
        Header = header;
        StartFact = startFact;
        Command = command;
        CommittedDraft = committedDraft;
    }

    internal PreviewSessionHeader Header { get; }
    internal CommandAuditFact StartFact { get; }
    internal SavePreviewToDraftCommand Command { get; }
    internal RecipeDraftRevision CommittedDraft { get; }
    internal object? Result { get; set; }
}

/// <summary>Schema-19 bounded, append-only Preview session ledger.</summary>
internal sealed partial class SqliteCommandStore
{
    internal const string PreviewSessionStoreActivatedKind = "PreviewSessionStoreActivated";
    internal const string PreviewSessionEventKind = "PreviewSessionEvent";

    internal const string PreviewSessionSchemaSql = @"
        CREATE TABLE preview_session_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE preview_session_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            StartCorrelationId TEXT NOT NULL CHECK(length(StartCorrelationId)=36),
            AttemptId TEXT NOT NULL CHECK(length(AttemptId)=36),
            CommandCorrelationId TEXT NOT NULL CHECK(length(CommandCorrelationId)=36),
            CommandKind INTEGER NOT NULL CHECK(CommandKind IN (14,33,34,35,36)),
            Phase INTEGER NOT NULL CHECK(Phase IN (1,2,3,4,5,6,7,8,9,10)),
            Restoration INTEGER NOT NULL CHECK(Restoration IN (1,2,3,4,5)),
            Terminal INTEGER NOT NULL CHECK(Terminal IN (0,1)),
            LogicalCameraRole TEXT NOT NULL CHECK(length(LogicalCameraRole)>0),
            DraftId TEXT NOT NULL CHECK(length(DraftId)=36),
            DraftRevision INTEGER NOT NULL CHECK(DraftRevision>0),
            DraftContentHash TEXT NOT NULL CHECK(length(DraftContentHash)=64),
            BindingPosition INTEGER NOT NULL CHECK(BindingPosition>0),
            BindingRevision INTEGER NOT NULL CHECK(BindingRevision>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64),
            ActivePosition INTEGER NULL CHECK(ActivePosition IS NULL OR ActivePosition>0),
            ActiveActivationId TEXT NULL CHECK(ActiveActivationId IS NULL OR length(ActiveActivationId)=36),
            ActiveContentHash TEXT NULL CHECK(ActiveContentHash IS NULL OR length(ActiveContentHash)=64),
            ActiveSnapshotHash TEXT NULL CHECK(ActiveSnapshotHash IS NULL OR length(ActiveSnapshotHash)=64),
            ActiveCameraHash TEXT NULL CHECK(ActiveCameraHash IS NULL OR length(ActiveCameraHash)=64),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            ActorSessionId TEXT NOT NULL CHECK(length(ActorSessionId)=36),
            ActorAuthorizationRevision INTEGER NOT NULL CHECK(ActorAuthorizationRevision>=0),
            AuthorizationPolicyId TEXT NOT NULL,
            AuthorizationPolicyVersion TEXT NOT NULL,
            AuthorizationPolicyHash TEXT NOT NULL CHECK(length(AuthorizationPolicyHash)=64),
            AuthorizationTarget TEXT NOT NULL CHECK(length(AuthorizationTarget)=64),
            ChangeReason TEXT NOT NULL CHECK(length(ChangeReason)>0),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0),
            RecordedAtUtc TEXT NOT NULL,
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64),
            UNIQUE(SessionId,Position));
        CREATE INDEX ix_preview_session_events_session ON preview_session_events(SessionId,Position);
        CREATE INDEX ix_preview_session_events_attempt ON preview_session_events(AttemptId,Position);
        CREATE INDEX ix_preview_session_events_audit ON preview_session_events(AuditSequence);
        CREATE TRIGGER preview_session_config_immutable_update BEFORE UPDATE
            ON preview_session_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutablePreviewSessionConfiguration');
        END;
        CREATE TRIGGER preview_session_config_immutable_delete BEFORE DELETE
            ON preview_session_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutablePreviewSessionConfiguration');
        END;
        CREATE TRIGGER preview_session_event_immutable_update BEFORE UPDATE
            ON preview_session_events BEGIN
            SELECT RAISE(ABORT,'ImmutablePreviewSessionEvent');
        END;
        CREATE TRIGGER preview_session_event_immutable_delete BEFORE DELETE
            ON preview_session_events BEGIN
            SELECT RAISE(ABORT,'ImmutablePreviewSessionEvent');
        END;";

    internal ValueTask<IdentityWriteResult> UpdatePreviewSessionCommandAsync(
        PreviewSessionCommand command,
        Func<IdentityAuthorityState, PreviewSessionCommandState, PreviewSessionAdmissionInput?, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(update);
        if (command.CorrelationId == Guid.Empty)
            return ValueTask.FromResult(new IdentityWriteResult(false, "CorrelationIdRequired"));
        return EnqueueIdentityAsync(new IdentityWork(command, update), cancellationToken, deadline);
    }

    /// <summary>
    /// Appends the Preview save event only after the normal Draft writer has
    /// committed. The operation id is the sole lookup key for the existing
    /// Draft-14 authorization evidence; callers cannot supply an audit record as
    /// a substitute. Draft-14 has no command-fact lifecycle, so this writer
    /// creates the supplementary Preview Save lifecycle in the same transaction.
    /// </summary>
    internal ValueTask<IdentityWriteResult> RecordPreviewDraftSavedAsync(
        PreviewSessionHeader header, CommandAuditFact startFact,
        SavePreviewToDraftCommand command, RecipeDraftRevision committedDraft,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(startFact);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(committedDraft);
        ArgumentNullException.ThrowIfNull(deadline);
        if (_options.PreviewSessions is null || _queue is null || _queueSlots is null ||
            Volatile.Read(ref _disposed) != 0)
            return ValueTask.FromResult(new IdentityWriteResult(false, "PreviewSessionStoreUnavailable"));
        var work = new PreviewDraftSavedWork(header, startFact, command, committedDraft);
        return RecordPreviewDraftSavedQueuedAsync(work, deadline, cancellationToken);
    }

    private async ValueTask<IdentityWriteResult> RecordPreviewDraftSavedQueuedAsync(
        PreviewDraftSavedWork work, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var result = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                PreviewDraftSaved: work), deadline, cancellationToken,
            "PreviewSessionStoreUnavailable", "PreviewSessionCommitDeadlineExceeded").ConfigureAwait(false);
        return new IdentityWriteResult(result.Committed, result.ReasonCode,
            result.Committed ? work.Result : null);
    }

    private StoreWriteResult AppendPreviewDraftSavedCore(sqlite3 database,
        PreviewDraftSavedWork work, StoreDeadline deadline)
    {
        var integrity = Integrity;
        if (integrity?.State != AuditIntegrityState.Verified)
            return new(false, integrity?.ReasonCode ?? "PreviewSessionAuditUnavailable",
                RetryAfterIntegrityRecheck: integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");

        var options = _options.PreviewSessions;
        if (options is null) return new(false, "PreviewSessionConfigurationRequired");
        var transactionStarted = false;
        var committed = false;
        try
        {
            options.Validate();
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            VerifyPreviewStoreDependencies(database, options, deadline);
            RequireConfiguredPreviewSessions(database, options, deadline);

            var rows = ReadPreviewSessionRows(database, options, deadline);
            ValidatePreviewReplay(rows.Select(item => item.Event).ToArray(), options, database, deadline);
            var current = rows.Select(item => item.Event)
                .Where(item => item.SessionId == work.Header.SessionId)
                .OrderBy(item => item.Position).LastOrDefault() ??
                throw new InvalidOperationException("PreviewSessionStateChanged");
            AuditChainDatabase.Require(current.Header.IsActive && !current.Header.RecoveryRequired &&
                current.Header.ContentHash == work.Header.ContentHash, "PreviewSessionStateChanged");

            var startEvent = rows.Select(item => item.Event)
                .Where(item => item.SessionId == work.Header.SessionId &&
                    item.CommandKind == AuditedCommandKind.StartPreview)
                .OrderBy(item => item.Position).FirstOrDefault() ??
                throw new InvalidOperationException("PreviewSessionStartAuditMissing");
            AuditChainDatabase.Require(startEvent.CommandAuditSequence is > 0 &&
                startEvent.CommandAuditHash is { Length: 64 }, "PreviewSessionStartAuditMissing");
            var startReference = ReadCommandAuditReference(database,
                startEvent!.CommandAuditSequence!.Value, startEvent.CommandAuditHash!, deadline);
            var persistedStart = startReference is null ? null :
                ReadFact(database, startReference.AttemptId, 1, deadline);
            AuditChainDatabase.Require(persistedStart is not null &&
                persistedStart.Disposition == CommandDisposition.Accepted &&
                persistedStart.CommandKind == AuditedCommandKind.StartPreview &&
                persistedStart == work.StartFact, "PreviewSessionStartAuditMismatch");

            var expectedDraft = work.Command.ExpectedDraft;
            AuditChainDatabase.Require(expectedDraft.DraftId == work.CommittedDraft.DraftId &&
                work.CommittedDraft.Revision > 0 &&
                expectedDraft.Revision < long.MaxValue &&
                work.CommittedDraft.Revision == expectedDraft.Revision + 1 &&
                work.CommittedDraft.PreviousRevisionContentHash == expectedDraft.RevisionContentHash &&
                work.CommittedDraft.OperationId == work.Command.CorrelationId,
                "PreviewDraftCommandEvidenceInvalid");
            var committedReference = PreviewDraftReference.FromRevision(work.CommittedDraft);
            var persistedDraft = ReadRecipeDraftRevisionByReference(database, committedReference, deadline,
                _options.RecipeDrafts ?? throw new InvalidOperationException("RecipeDraftConfigurationRequired"));
            AuditChainDatabase.Require(SameRecipeDraftRevision(persistedDraft, work.CommittedDraft) &&
                persistedDraft.Content.CameraRole == current.Header.LogicalCameraRole &&
                persistedDraft.AuthorPrincipalId == current.Header.ActorPrincipalId &&
                persistedDraft.AuthorSessionId == current.Header.ActorSessionId &&
                persistedDraft.AuthorAuthorizationRevision == current.Header.ActorAuthorizationRevision,
                "PreviewDraftCommandEvidenceInvalid");

            var draftFact = ReadPreviewDraftCommandFact(database, work.CommittedDraft.OperationId, deadline);
            CommandAuditFact? generatedDraftTerminal = null;
            if (draftFact is null)
            {
                // The ordinary Draft-14 writer persists the signed
                // RecipeDraftSaved identity event and revision, but it has no
                // command-fact lifecycle.  Preview Save therefore creates its
                // own bounded command attempt from those already verified
                // facts.  It uses the same operation/correlation and the
                // Preview runtime epoch; no second Draft mutation or grant
                // reservation occurs here.
                AuditChainDatabase.Require(work.Command.Invocation.PrincipalId ==
                    current.ActorPrincipalId.ToString("D") &&
                    work.Command.Invocation.SessionId == current.ActorSessionId,
                    "PreviewDraftCommandEvidenceInvalid");
                var occurredAt = DateTimeOffset.UtcNow;
                var generatedDraftFact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(),
                    work.Command.CorrelationId, current.RuntimeEpoch, occurredAt,
                    AuditedCommandKind.SaveRecipeDraft, work.Command.Invocation.Source,
                    work.Command.Invocation.PrincipalId, work.Command.Invocation.SessionId,
                    work.Command.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                    CommandDisposition.Accepted, "PreviewSessionActionAuthorized",
                    current.ActorPrincipalId.ToString("D"));
                draftFact = generatedDraftFact;
                generatedDraftTerminal = new CommandAuditFact(Guid.NewGuid(),
                    generatedDraftFact.AttemptId, generatedDraftFact.CorrelationId,
                    generatedDraftFact.RuntimeEpoch, occurredAt, generatedDraftFact.CommandKind,
                    generatedDraftFact.Source, generatedDraftFact.ClaimedPrincipalId,
                    generatedDraftFact.ClaimedSessionId, generatedDraftFact.ClaimedStepUpGrantId,
                    CommandAuditPhase.Completed, null, "PreviewDraftSaved",
                    generatedDraftFact.AuthenticatedHumanPrincipalId);
            }
            var persistedDraftFact = draftFact ??
                throw new InvalidOperationException("PreviewDraftCommandMissing");
            ValidatePreviewDraftCommandFact(persistedDraftFact, work, current);
            if (generatedDraftTerminal is not null)
            {
                AppendPreviewDraftCommandFacts(database, persistedDraftFact, generatedDraftTerminal,
                    deadline);
            }
            else
            {
                var existingTerminal = ReadFact(database, persistedDraftFact.AttemptId, 2, deadline);
                AuditChainDatabase.Require(existingTerminal is not null &&
                    existingTerminal.CommandKind == AuditedCommandKind.SaveRecipeDraft &&
                    existingTerminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
                    existingTerminal.Disposition is null &&
                    SameCommandContext(persistedDraftFact, existingTerminal),
                    "PreviewDraftCommandTerminalMissing");
            }

            var existing = rows.Select(item => item.Event).SingleOrDefault(item =>
                item.SessionId == work.Header.SessionId &&
                item.CommandKind == AuditedCommandKind.SaveRecipeDraft &&
                item.CommandCorrelationId == work.Command.CorrelationId);
            if (existing is not null)
            {
                AuditChainDatabase.Require(existing.SavedDraft == committedReference &&
                    existing.CommandAuditSequence is > 0 && existing.CommandAuditHash is { Length: 64 },
                    "PreviewDraftCommandEvidenceInvalid");
                var existingReference = ReadCommandAuditReference(database,
                    existing.CommandAuditSequence!.Value, existing.CommandAuditHash!, deadline);
                AuditChainDatabase.Require(existingReference is not null &&
                    existingReference.AttemptId == persistedDraftFact.AttemptId &&
                    existingReference.CorrelationId == persistedDraftFact.CorrelationId &&
                    existingReference.CommandKind == persistedDraftFact.CommandKind,
                    "PreviewDraftCommandEvidenceInvalid");
                work.Result = new PreviewSessionCommandResult(new RuntimeCommandOutcome(
                    work.Command.CorrelationId, CommandDisposition.Accepted,
                    "PreviewDraftAlreadyRecorded", AuditPersistence.Persisted, persistedDraftFact.AttemptId),
                    existing.Header, existing, persistedDraftFact);
                Rollback(database);
                committed = true;
                return new(true, "PreviewDraftAlreadyRecorded");
            }

            var authorizationTarget = work.Command.AuthorizationTarget;
            var currentConfiguration = rows.Select(item => item.Event)
                .Where(item => item.SessionId == work.Header.SessionId)
                .OrderBy(item => item.Position).LastOrDefault()?.Configuration;
            var now = DateTimeOffset.UtcNow;
            var header = new PreviewSessionHeader(current.Header.Position, current.Header.SessionId,
                current.Header.RuntimeEpoch, current.Header.StartCorrelationId, current.Header.AttemptId,
                current.Header.Draft, current.Header.LogicalCameraRole, current.Header.CurrentBinding,
                current.Header.ActiveActivation, current.Header.ActiveSnapshotContentHash,
                current.Header.ActiveCameraContentHash, current.Header.ActorPrincipalId,
                current.Header.ActorSessionId, current.Header.ActorAuthorizationRevision,
                current.Header.AuthorizationPolicy, current.Header.AuthorizationTarget,
                current.Header.ChangeReason, current.Header.StartedAtUtc,
                PreviewSessionPhase.SavingDraft, PreviewRestorationState.Pending,
                "PreviewDraftSaved", recoveryRequired: false,
                work.Command.FrozenSettingsContentHash, committedReference);
            var candidate = new PreviewSessionEvent(Math.Max(1, rows.Count + 1L), header,
                persistedDraftFact.AttemptId, persistedDraftFact.CorrelationId, AuditedCommandKind.SaveRecipeDraft,
                PreviewSessionPhase.SavingDraft, PreviewRestorationState.Pending, "PreviewDraftSaved",
                terminal: true, header.ActorPrincipalId, header.ActorSessionId,
                header.ActorAuthorizationRevision, authorizationTarget, now, currentConfiguration,
                committedReference, work.Command.FrozenSettingsContentHash);
            var authorizationReference = ReadPreviewDraftAuthorizationReference(database, candidate,
                work.Command.Invocation.StepUpGrantId, deadline);
            var commandReference = ReadCommandAuditReferenceByEventId(database, persistedDraftFact.EventId, deadline);
            var persisted = AppendPreviewEventRecord(database, candidate, commandReference,
                authorizationReference, options, deadline);
            work.Result = new PreviewSessionCommandResult(new RuntimeCommandOutcome(
                work.Command.CorrelationId, CommandDisposition.Accepted, "PreviewDraftSaved",
                AuditPersistence.Persisted, persistedDraftFact.AttemptId), persisted.Header, persisted,
                persistedDraftFact);
            var tail = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, tail);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy!, AuditIntegrityState.Verifying,
                "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new(true, "PreviewDraftSaved");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return new(false, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Preview", StringComparison.Ordinal) ||
            ex.Message.StartsWith("RecipeDraft", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Identity", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Audit", StringComparison.Ordinal))
        { return new(false, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(ex, "PreviewDraftAuditCommitFailed")); }
        finally
        {
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    private void VerifyPreviewStoreDependencies(sqlite3 database,
        PreviewSessionStoreOptions options, StoreDeadline deadline)
    {
        var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
            _signingKey.PublicKeyBase64,
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
             previewOptions: options, importOptions: _options.CalibrationImports,
             manualOptions: _options.ManualInspections,
             productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
                RequireStationQualificationWriteSnapshot(database, verification, deadline);
        if (_options.AlarmPolicy is not null)
            AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
        if (_options.AlgorithmResultArchive is not null)
            AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
        AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
            _options.RecipeDrafts ?? throw new InvalidOperationException("RecipeDraftConfigurationRequired"));
        AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
            _options.CameraSetup ?? throw new InvalidOperationException("CameraSetupConfigurationRequired"));
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
        if (_options.RecipeReleases is not null)
            AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
        if (_options.PlcResultContracts is not null)
            AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                _options.PlcResultContracts);
        AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
            _options.RecipeActivations ?? throw new InvalidOperationException("RecipeActivationConfigurationRequired"),
            _options.RecipeReleases ?? throw new InvalidOperationException("RecipeReleaseConfigurationRequired"),
            _options.PlcResultContracts ?? throw new InvalidOperationException("PlcResultContractConfigurationRequired"),
            _options.CalibrationGovernance);
        AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline, options);
        if (_options.CalibrationImports is not null)
            AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline, _options.CalibrationImports);
             if (_options.ManualInspections is not null)
                 AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline, _options.ManualInspections);
             if (_options.ProductionAdmission is not null)
                 AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                     _options.ProductionAdmission);
    }

    private static CommandAuditFact? ReadPreviewDraftCommandFact(sqlite3 database,
        Guid operationId, StoreDeadline deadline)
    {
        var attempts = AuditChainDatabase.Read(database, @"
            SELECT AttemptId FROM command_facts
            WHERE CorrelationId=? AND CommandKind=? AND AggregateSequence=1 LIMIT 2;", deadline,
            statement => ParsePreviewGuid(SqliteNative.ColumnText(statement, 0)),
            operationId.ToString("D"), ((int)AuditedCommandKind.SaveRecipeDraft).ToString(CultureInfo.InvariantCulture));
        var facts = attempts.Select(attempt => ReadFact(database, attempt, 1, deadline))
            .Where(fact => fact is not null && fact.Phase == CommandAuditPhase.Outcome &&
                fact.Disposition == CommandDisposition.Accepted)
            .Cast<CommandAuditFact>().ToArray();
        AuditChainDatabase.Require(facts.Length <= 1, "PreviewDraftCommandAmbiguous");
        return facts.SingleOrDefault();
    }

    private static void ValidatePreviewDraftCommandFact(CommandAuditFact fact,
        PreviewDraftSavedWork work, PreviewSessionEvent current)
    {
        AuditChainDatabase.Require(fact.CommandKind == AuditedCommandKind.SaveRecipeDraft &&
            fact.Phase == CommandAuditPhase.Outcome &&
            fact.Disposition == CommandDisposition.Accepted &&
            fact.CorrelationId == work.Command.CorrelationId &&
            fact.RuntimeEpoch == current.RuntimeEpoch &&
            fact.Source == work.Command.Invocation.Source &&
            fact.ClaimedPrincipalId == current.ActorPrincipalId.ToString("D") &&
            fact.ClaimedSessionId == current.ActorSessionId &&
            fact.ClaimedStepUpGrantId == work.Command.Invocation.StepUpGrantId &&
            fact.AuthenticatedHumanPrincipalId == current.ActorPrincipalId.ToString("D"),
            "PreviewDraftCommandEvidenceInvalid");
    }

    private void AppendPreviewDraftCommandFacts(sqlite3 database,
        CommandAuditFact outcome, CommandAuditFact terminal, StoreDeadline deadline)
    {
        ValidateFact(outcome);
        ValidateFact(terminal);
        AuditChainDatabase.Require(outcome.Phase == CommandAuditPhase.Outcome &&
            outcome.Disposition == CommandDisposition.Accepted &&
            terminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
            terminal.Disposition is null && SameCommandContext(outcome, terminal),
            "PreviewDraftCommandLifecycleInvalid");
        AuditChainDatabase.Require(!Exists(database,
            "SELECT 1 FROM command_attempts WHERE AttemptId=? LIMIT 1;", outcome.AttemptId,
            deadline) && !Exists(database,
            "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;", outcome.EventId, deadline) &&
            !Exists(database,
            "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;", terminal.EventId, deadline),
            "PreviewDraftCommandDuplicate");
        InsertAttempt(database, outcome, deadline);
        InsertFact(database, outcome, aggregateSequence: 1, deadline);
        AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, outcome.EventId, deadline);
        InsertFact(database, terminal, aggregateSequence: 2, deadline);
        AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, terminal.EventId, deadline);
    }

    private static AuditIdentityReference ReadPreviewDraftAuthorizationReference(sqlite3 database,
        PreviewSessionEvent value, Guid? expectedStepUpGrantId, StoreDeadline deadline)
    {
        var schemaVersion = checked((int)AuditChainDatabase.Scalar(database,
            "PRAGMA user_version;", deadline));
        var station = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1;", deadline) ?? string.Empty;
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Sequence,IdentityPosition,Hash,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL
            ORDER BY IdentityPosition;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Hash: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Payload: SqliteNative.ColumnText(statement, 3) ?? string.Empty));
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Hash is { Length: 64 } && row.Payload.Length > 0,
                "PreviewSessionAuthorizationAuditMissing");
            byte[] payload;
            try { payload = Convert.FromBase64String(row.Payload); }
            catch (FormatException) { throw new InvalidOperationException("PreviewSessionAuthorizationAuditMissing"); }
            IdentityAuditEvent.VerifyPayload(payload, row.Ordinal, station, schemaVersion);
            if (IdentityAuditEvent.MatchesPreviewDraftAuthorization(payload, row.Ordinal, station,
                    value, expectedStepUpGrantId))
                return new AuditIdentityReference(row.Sequence, row.Hash, row.Payload);
        }
        throw new InvalidOperationException("PreviewDraftAuthorizationMissing");
    }

    /// <summary>
    /// Reads the durable preview owner used by startup recovery. The query is
    /// independent of interactive identity state and resolves every dependency
    /// from the same verified read snapshot. A corrupt or incomplete reference
    /// returns an unavailable result, never a fabricated public projection.
    /// </summary>
    internal async ValueTask<PreviewSessionRecoveryState> ReadPreviewRecoveryStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_options.PreviewSessions is null)
            return new(false, "PreviewSessionConfigurationRequired");
        StoreWriteResult initialized;
        try { initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        if (!initialized.Committed || _databasePath is null || _policy is null || _signingKey is null)
            return new(false, initialized.ReasonCode);
        try
        {
            return await Task.Run(() => ReadPreviewRecoveryStateCore(cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "PreviewSessionRecoveryUnavailable"));
        }
    }

    private PreviewSessionRecoveryState ReadPreviewRecoveryStateCore(
        CancellationToken cancellationToken)
    {
        var options = _options.PreviewSessions ??
            throw new InvalidOperationException("PreviewSessionConfigurationRequired");
        using var connection = SqliteNative.Open(_databasePath!, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        var deadline = new StoreDeadline(_options.QueryTimeout);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline,
            cancellationToken);
        var committed = false;
        try
        {
            var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
                _signingKey.PublicKeyBase64,
                new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries),
                startup: true, deadline, validateAnchorReceipt: false,
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
                 previewOptions: options, importOptions: _options.CalibrationImports,
                 manualOptions: _options.ManualInspections,
                 productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
                RequireStationQualificationWriteSnapshot(database, verification, deadline);
            RequireConfiguredPreviewSessions(database, options, deadline);
            if (_options.AlarmPolicy is not null)
                AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (_options.AlgorithmResultArchive is not null)
                AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            if (_options.RecipeDrafts is not null)
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
                AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, verification,
                    deadline, _options.CalibrationGovernance);
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
            AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline, options);
            if (_options.CalibrationImports is not null)
                AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline, _options.CalibrationImports);
                 if (_options.ManualInspections is not null)
                     AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline, _options.ManualInspections);
                 if (_options.ProductionAdmission is not null)
                     AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                         _options.ProductionAdmission);

            var rows = ReadPreviewSessionRows(database, options, deadline);
            ValidatePreviewReplay(rows.Select(value => value.Event).ToArray(), options, database, deadline);
            var recovery = rows.Any(value => value.Event.Header.RecoveryRequired ||
                value.Event.Restoration == PreviewRestorationState.RecoveryBlocked);
            var pending = rows.Select(value => value.Event).GroupBy(value => value.SessionId)
                .Select(group => group.OrderBy(value => value.Position).Last())
                .Where(value => value.Header.IsActive || value.Header.RecoveryRequired ||
                    value.Restoration == PreviewRestorationState.RecoveryBlocked)
                .OrderBy(value => value.Position).LastOrDefault();
            if (pending is null)
            {
                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                committed = true;
                return new(true, recovery ? "PreviewRecoveryRequired" : "PreviewSessionIdle",
                    null, null, null, null, recovery);
            }

            var start = rows.Select(value => value.Event)
                .Where(value => value.SessionId == pending.SessionId &&
                    value.CommandKind == AuditedCommandKind.StartPreview)
                .OrderBy(value => value.Position).FirstOrDefault();
            AuditChainDatabase.Require(start is not null && start.CommandAuditSequence is > 0 &&
                start.CommandAuditHash is { Length: 64 }, "PreviewSessionStartAuditMissing");
            var commandReference = ReadCommandAuditReference(database,
                start!.CommandAuditSequence!.Value, start.CommandAuditHash!, deadline);
            AuditChainDatabase.Require(commandReference is not null,
                "PreviewSessionStartCommandMissing");
            var startFact = ReadFact(database, commandReference!.AttemptId, 1, deadline);
            AuditChainDatabase.Require(startFact is not null &&
                startFact.Disposition == CommandDisposition.Accepted &&
                startFact.CommandKind == AuditedCommandKind.StartPreview,
                "PreviewSessionStartCommandMissing");
            var draft = ReadRecipeDraftRevisionByReference(database, pending.Header.Draft, deadline,
                _options.RecipeDrafts ?? throw new InvalidOperationException("RecipeDraftConfigurationRequired"));
            AuditChainDatabase.Require(draft.Content.CameraRole == pending.Header.LogicalCameraRole,
                "PreviewSessionDraftBindingMismatch");
            var baseline = pending.Header.ActiveActivation is null ? null :
                ReadRecipeActivationCommandState(database, deadline).Records.SingleOrDefault(record =>
                    record.Reference == pending.Header.ActiveActivation && record.Outcome.Succeeded &&
                    record.SuccessfulSnapshot is not null);
            AuditChainDatabase.Require(pending.Header.ActiveActivation is null || baseline is not null,
                "PreviewActiveBaselineMissing");
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new(true, recovery ? "PreviewRecoveryRequired" : "PreviewSessionRecoveryPending",
                pending.Header, startFact, draft, baseline, recovery);
        }
        finally
        {
            if (!committed)
                try { SQLitePCL.raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    internal static void InitializePreviewSessionSchema(sqlite3 database,
        PreviewSessionStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, PreviewSessionSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO preview_session_store_config
                (Id,FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            PreviewSessionStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendPreviewSessionStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredPreviewSessions(sqlite3 database,
        PreviewSessionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM preview_session_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEntries: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                BindingHash: SqliteNative.ColumnText(statement, 4) ?? string.Empty)).ToArray();
        AuditChainDatabase.Require(configured.Length == 1 &&
            configured[0].FormatVersion == PreviewSessionStoreOptions.FormatVersion &&
            configured[0].MaximumEntries == options.MaximumEntries &&
            configured[0].MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured[0].MaximumTotalBytes == options.MaximumTotalBytes &&
            configured[0].BindingHash == options.BindingHash, "PreviewSessionConfigurationMismatch");
    }

    internal static void VerifyPreviewSessionActivationPayload(sqlite3 database, byte[] payload,
        PreviewSessionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "PreviewSessionActivationBindingMismatch");
        RequireConfiguredPreviewSessions(database, options, deadline);
    }

    internal static void VerifyPreviewSessionAuditPayload(sqlite3 database, long position,
        byte[] auditPayload, PreviewSessionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(auditPayload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var row = ReadPreviewSessionRows(database, options, deadline)
            .SingleOrDefault(value => value.Position == position);
        AuditChainDatabase.Require(row is not null, "PreviewSessionAuditBindingMissing");
        var expected = EncodePreviewAuditPayload(row!.Event);
        AuditChainDatabase.Require(expected.SequenceEqual(auditPayload) &&
            PreviewSessionStorageCodec.PayloadHash(auditPayload) == row.Event.PayloadHash &&
            row.Event.AuditSequence > 0 && row.Event.AuditHash is { Length: 64 },
            "PreviewSessionAuditBindingMismatch");
    }

    private void AppendPreviewSessionIdentityMutation(sqlite3 database, IdentityUpdate update,
        PreviewSessionCommandState state, PreviewSessionCommand command, IdentityWork work,
        StoreDeadline deadline)
    {
        var mutation = update.PreviewSession ?? throw new InvalidOperationException("PreviewSessionMutationMissing");
        var options = _options.PreviewSessions ??
            throw new InvalidOperationException("PreviewSessionConfigurationRequired");
        options.Validate();
        AuditChainDatabase.Require(state.Enabled && update.Events.Count == 1 &&
            (update.CommandFacts is { Count: > 0 and <= 2 } || !mutation.Event.Terminal),
            "PreviewSessionMutationMismatch");
        var rows = ReadPreviewSessionRows(database, options, deadline);
        AuditChainDatabase.Require(rows.Count < options.MaximumEntries,
            "PreviewSessionEntryCapacityExceeded");
        var eventValue = mutation.Event;
        var commandFact = update.CommandFacts?.FirstOrDefault(value =>
            value.Phase != CommandAuditPhase.Outcome) ?? update.CommandFacts?.FirstOrDefault();
        AuditChainDatabase.Require(eventValue.SessionId == command.PreviewSessionId &&
            eventValue.CommandCorrelationId == command.CorrelationId &&
            eventValue.CommandKind == CommandKindForPreview(command) &&
            eventValue.Header.SessionId == command.PreviewSessionId &&
            // The header freezes the Start target for the whole session. Each
            // action carries its own exact target on the event.
            (eventValue.CommandKind != AuditedCommandKind.StartPreview ||
                eventValue.Header.AuthorizationTarget == command.AuthorizationTarget),
            "PreviewSessionMutationMismatch");
        var commandReference = commandFact is not null
            ? ReadCommandAuditReferenceByEventId(database, commandFact.EventId, deadline)
            : ReadCommandAuditReferenceForPreviewEvent(database, eventValue, deadline);
        if (commandReference is null)
            throw new InvalidOperationException("PreviewSessionCommandAuditMissing");
        var commandReferenceValue = commandReference.Value;
        var authorizationReference = ReadIdentityAuditReferenceByEventId(database, update.Events[0].EventId,
            deadline) ?? throw new InvalidOperationException("PreviewSessionAuthorizationAuditMissing");
        var position = checked(rows.Count + 1L);
        var previousHash = rows.Count == 0 ? null : rows[^1].Event.AuditHash;
        var withReferences = NewPreviewEvent(eventValue, position, commandReferenceValue.Sequence,
            commandReferenceValue.Hash, authorizationReference.Sequence, authorizationReference.Hash,
            payloadHash: null, auditSequence: 0, auditHash: null);
        var payloadHash = PreviewSessionStorageCodec.PayloadHash(EncodePreviewAuditPayload(withReferences));
        var beforeAudit = NewPreviewEvent(withReferences, position, commandReferenceValue.Sequence,
            commandReferenceValue.Hash, authorizationReference.Sequence, authorizationReference.Hash,
            payloadHash, 0, null);
        var auditPayload = EncodePreviewAuditPayload(beforeAudit);
        var auditSequence = AuditChainDatabase.AppendPreviewSessionLedgerEntry(database, _policy!, _signingKey!,
            position, auditPayload, options, deadline);
        var auditHash = AuditChainDatabase.Read(database,
            "SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND PreviewPosition=? LIMIT 2;",
            deadline, statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            auditSequence.ToString(CultureInfo.InvariantCulture), PreviewSessionEventKind,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(auditHash is { Length: 64 }, "PreviewSessionAuditEntryMissing");
        var persisted = NewPreviewEvent(beforeAudit, position, commandReferenceValue.Sequence,
            commandReferenceValue.Hash, authorizationReference.Sequence, authorizationReference.Hash,
            payloadHash, auditSequence, auditHash);
        var payload = PreviewSessionStorageCodec.Encode(persisted);
        var total = rows.Aggregate(0L, (sum, value) => checked(sum + value.Payload.Length));
        AuditChainDatabase.Require(checked(total + payload.Length) <= options.MaximumTotalBytes,
            "PreviewSessionTotalCapacityExceeded");
        var binding = persisted.Header.CurrentBinding;
        var active = persisted.Header.ActiveActivation;
        AuditChainDatabase.Execute(database, @"
            INSERT INTO preview_session_events(
                Position,PreviousHash,SessionId,RuntimeEpoch,StartCorrelationId,AttemptId,
                CommandCorrelationId,CommandKind,Phase,Restoration,Terminal,LogicalCameraRole,
                DraftId,DraftRevision,DraftContentHash,BindingPosition,BindingRevision,BindingHash,
                ActivePosition,ActiveActivationId,ActiveContentHash,ActiveSnapshotHash,ActiveCameraHash,
                ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,AuthorizationPolicyId,
                AuthorizationPolicyVersion,AuthorizationPolicyHash,AuthorizationTarget,ChangeReason,
                ReasonCode,RecordedAtUtc,ContentHash,PayloadHash,Payload,CommandAuditSequence,
                CommandAuditHash,AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), previousHash,
            persisted.SessionId.ToString("D"), persisted.RuntimeEpoch.ToString("D"),
            persisted.Header.StartCorrelationId.ToString("D"), persisted.AttemptId.ToString("D"),
            persisted.CommandCorrelationId.ToString("D"), ((int)persisted.CommandKind).ToString(CultureInfo.InvariantCulture),
            ((int)persisted.Phase).ToString(CultureInfo.InvariantCulture),
            ((int)persisted.Restoration).ToString(CultureInfo.InvariantCulture), persisted.Terminal ? "1" : "0",
            persisted.Header.LogicalCameraRole, persisted.Header.Draft.DraftId.ToString("D"),
            persisted.Header.Draft.Revision.ToString(CultureInfo.InvariantCulture), persisted.Header.Draft.RevisionContentHash,
            binding.Position.ToString(CultureInfo.InvariantCulture), binding.Revision.ToString(CultureInfo.InvariantCulture),
            binding.RevisionHash, active?.Position.ToString(CultureInfo.InvariantCulture),
            active?.ActivationId.ToString("D"), active?.ContentHash, persisted.Header.ActiveSnapshotContentHash,
            persisted.Header.ActiveCameraContentHash, persisted.ActorPrincipalId.ToString("D"),
            persisted.ActorSessionId.ToString("D"), persisted.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            persisted.Header.AuthorizationPolicy.Id, persisted.Header.AuthorizationPolicy.Version,
            persisted.Header.AuthorizationPolicy.ContentHash, persisted.AuthorizationTarget, persisted.Header.ChangeReason,
            persisted.ReasonCode, persisted.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), persisted.ContentHash,
            persisted.PayloadHash!, Convert.ToBase64String(payload),
            persisted.CommandAuditSequence!.Value.ToString(CultureInfo.InvariantCulture), persisted.CommandAuditHash!,
            persisted.AuthorizationAuditSequence!.Value.ToString(CultureInfo.InvariantCulture), persisted.AuthorizationAuditHash!,
            persisted.AuditSequence.ToString(CultureInfo.InvariantCulture), persisted.AuditHash!);
        AppendAdditionalPreviewTerminalFacts(database, mutation.AdditionalTerminalFacts,
            state, eventValue, deadline);
        if (work.Result is PreviewSessionCommandResult result)
            work.Result = result with { Header = persisted.Header, Event = persisted,
                CommandFact = commandFact };
    }

    private static AuditCommandReference? ReadCommandAuditReferenceForPreviewEvent(
        sqlite3 database, PreviewSessionEvent value, StoreDeadline deadline)
    {
        var row = AuditChainDatabase.Read(database, @"
            SELECT e.Sequence,e.Hash,f.AttemptId,f.CorrelationId,f.RuntimeEpoch,f.CommandKind
            FROM audit_entries e JOIN command_facts f ON f.Position=e.FactPosition
            WHERE e.Kind='CommandFact' AND f.AttemptId=? AND f.AggregateSequence=1
              AND f.CorrelationId=? AND f.CommandKind=? LIMIT 2;", deadline,
            statement => new AuditCommandReference(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                ParsePreviewGuid(SqliteNative.ColumnText(statement, 2)),
                ParsePreviewGuid(SqliteNative.ColumnText(statement, 3)),
                ParsePreviewGuid(SqliteNative.ColumnText(statement, 4)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 5)),
            value.AttemptId.ToString("D"), value.CommandCorrelationId.ToString("D"),
            ((int)value.CommandKind).ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        return row.Sequence > 0 && row.Hash is { Length: 64 } ? row : null;
    }

    private PreviewSessionEvent AppendPreviewEventRecord(sqlite3 database,
        PreviewSessionEvent value, AuditCommandReference commandReference,
        AuditIdentityReference authorizationReference, PreviewSessionStoreOptions options,
        StoreDeadline deadline)
    {
        var rows = ReadPreviewSessionRows(database, options, deadline);
        AuditChainDatabase.Require(rows.Count < options.MaximumEntries,
            "PreviewSessionEntryCapacityExceeded");
        var position = checked(rows.Count + 1L);
        var previousHash = rows.Count == 0 ? null : rows[^1].Event.AuditHash;
        var withReferences = NewPreviewEvent(value, position, commandReference.Sequence,
            commandReference.Hash, authorizationReference.Sequence, authorizationReference.Hash,
            payloadHash: null, auditSequence: 0, auditHash: null);
        var payloadHash = PreviewSessionStorageCodec.PayloadHash(EncodePreviewAuditPayload(withReferences));
        var beforeAudit = NewPreviewEvent(withReferences, position, commandReference.Sequence,
            commandReference.Hash, authorizationReference.Sequence, authorizationReference.Hash,
            payloadHash, 0, null);
        var auditPayload = EncodePreviewAuditPayload(beforeAudit);
        var auditSequence = AuditChainDatabase.AppendPreviewSessionLedgerEntry(database, _policy!, _signingKey!,
            position, auditPayload, options, deadline);
        var auditHash = AuditChainDatabase.Read(database,
            "SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND PreviewPosition=? LIMIT 2;",
            deadline, statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            auditSequence.ToString(CultureInfo.InvariantCulture), PreviewSessionEventKind,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(auditHash is { Length: 64 }, "PreviewSessionAuditEntryMissing");
        var persisted = NewPreviewEvent(beforeAudit, position, commandReference.Sequence,
            commandReference.Hash, authorizationReference.Sequence, authorizationReference.Hash,
            payloadHash, auditSequence, auditHash);
        var payload = PreviewSessionStorageCodec.Encode(persisted);
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        AuditChainDatabase.Require(checked(total + payload.Length) <= options.MaximumTotalBytes,
            "PreviewSessionTotalCapacityExceeded");
        var binding = persisted.Header.CurrentBinding;
        var active = persisted.Header.ActiveActivation;
        AuditChainDatabase.Execute(database, @"
            INSERT INTO preview_session_events(
                Position,PreviousHash,SessionId,RuntimeEpoch,StartCorrelationId,AttemptId,
                CommandCorrelationId,CommandKind,Phase,Restoration,Terminal,LogicalCameraRole,
                DraftId,DraftRevision,DraftContentHash,BindingPosition,BindingRevision,BindingHash,
                ActivePosition,ActiveActivationId,ActiveContentHash,ActiveSnapshotHash,ActiveCameraHash,
                ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,AuthorizationPolicyId,
                AuthorizationPolicyVersion,AuthorizationPolicyHash,AuthorizationTarget,ChangeReason,
                ReasonCode,RecordedAtUtc,ContentHash,PayloadHash,Payload,CommandAuditSequence,
                CommandAuditHash,AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), previousHash,
            persisted.SessionId.ToString("D"), persisted.RuntimeEpoch.ToString("D"),
            persisted.Header.StartCorrelationId.ToString("D"), persisted.AttemptId.ToString("D"),
            persisted.CommandCorrelationId.ToString("D"), ((int)persisted.CommandKind).ToString(CultureInfo.InvariantCulture),
            ((int)persisted.Phase).ToString(CultureInfo.InvariantCulture),
            ((int)persisted.Restoration).ToString(CultureInfo.InvariantCulture), persisted.Terminal ? "1" : "0",
            persisted.Header.LogicalCameraRole, persisted.Header.Draft.DraftId.ToString("D"),
            persisted.Header.Draft.Revision.ToString(CultureInfo.InvariantCulture), persisted.Header.Draft.RevisionContentHash,
            binding.Position.ToString(CultureInfo.InvariantCulture), binding.Revision.ToString(CultureInfo.InvariantCulture),
            binding.RevisionHash, active?.Position.ToString(CultureInfo.InvariantCulture),
            active?.ActivationId.ToString("D"), active?.ContentHash, persisted.Header.ActiveSnapshotContentHash,
            persisted.Header.ActiveCameraContentHash, persisted.ActorPrincipalId.ToString("D"),
            persisted.ActorSessionId.ToString("D"), persisted.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            persisted.Header.AuthorizationPolicy.Id, persisted.Header.AuthorizationPolicy.Version,
            persisted.Header.AuthorizationPolicy.ContentHash, persisted.AuthorizationTarget, persisted.Header.ChangeReason,
            persisted.ReasonCode, persisted.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), persisted.ContentHash,
            persisted.PayloadHash!, Convert.ToBase64String(payload),
            persisted.CommandAuditSequence!.Value.ToString(CultureInfo.InvariantCulture), persisted.CommandAuditHash!,
            persisted.AuthorizationAuditSequence!.Value.ToString(CultureInfo.InvariantCulture), persisted.AuthorizationAuditHash!,
            persisted.AuditSequence.ToString(CultureInfo.InvariantCulture), persisted.AuditHash!);
        return persisted;
    }

    private void AppendAdditionalPreviewTerminalFacts(sqlite3 database,
        IReadOnlyList<CommandAuditFact>? facts, PreviewSessionCommandState state,
        PreviewSessionEvent eventValue, StoreDeadline deadline)
    {
        if (facts is null || facts.Count == 0) return;
        AuditChainDatabase.Require(facts.Count == 1 && state.StartCommandFact is { } start &&
            eventValue.Terminal && (eventValue.Phase is PreviewSessionPhase.Closed or
            PreviewSessionPhase.RecoveryBlocked) &&
            facts[0].AttemptId == start.AttemptId && facts[0].CorrelationId == start.CorrelationId &&
            facts[0].CommandKind == AuditedCommandKind.StartPreview,
            "PreviewSessionAdditionalTerminalLimit");
        foreach (var fact in facts)
        {
            ValidateFact(fact);
            AuditChainDatabase.Require(fact.Phase is CommandAuditPhase.Completed or
                CommandAuditPhase.Failed && fact.Disposition is null,
                "PreviewSessionAdditionalTerminalInvalid");
            var attempt = ReadAttempt(database, fact.AttemptId, deadline);
            AuditChainDatabase.Require(attempt is not null &&
                attempt.Value.OutcomeDisposition == CommandDisposition.Accepted &&
                attempt.Value.Matches(fact), "PreviewSessionAdditionalTerminalContextMismatch");
            var existing = ReadFact(database, fact.AttemptId, 2, deadline);
            if (existing is not null)
            {
                AuditChainDatabase.Require(existing.EventId == fact.EventId &&
                    existing.Phase == fact.Phase && existing.ReasonCode == fact.ReasonCode &&
                    existing.OccurredAtUtc == fact.OccurredAtUtc &&
                    existing.CommandKind == fact.CommandKind && existing.Source == fact.Source &&
                    existing.ClaimedPrincipalId == fact.ClaimedPrincipalId &&
                    existing.ClaimedSessionId == fact.ClaimedSessionId &&
                    existing.ClaimedStepUpGrantId == fact.ClaimedStepUpGrantId &&
                    existing.AuthenticatedHumanPrincipalId == fact.AuthenticatedHumanPrincipalId,
                    "PreviewSessionAdditionalTerminalConflict");
                continue;
            }
            AuditChainDatabase.Require(!Exists(database,
                "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;", fact.EventId, deadline),
                "DuplicateEventId");
            InsertFact(database, fact, 2, deadline);
            AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, fact.EventId, deadline);
        }
    }

    private static PreviewSessionEvent NewPreviewEvent(PreviewSessionEvent value, long position,
        long commandSequence, string commandHash, long authorizationSequence, string authorizationHash,
        string? payloadHash, long auditSequence, string? auditHash) => new(position, value.Header,
        value.AttemptId, value.CommandCorrelationId, value.CommandKind, value.Phase, value.Restoration,
        value.ReasonCode, value.Terminal, value.ActorPrincipalId, value.ActorSessionId,
        value.ActorAuthorizationRevision, value.AuthorizationTarget, value.RecordedAtUtc,
        value.Configuration, value.SavedDraft, value.FrozenSettingsContentHash, commandSequence,
        commandHash, authorizationSequence, authorizationHash, payloadHash, auditSequence, auditHash);

    private static AuditedCommandKind CommandKindForPreview(PreviewSessionCommand command) => command switch
    {
        StartPreviewSessionCommand => AuditedCommandKind.StartPreview,
        ApplyPreviewTuningCommand => AuditedCommandKind.Tune,
        FreezePreviewSettingsCommand => AuditedCommandKind.Freeze,
        SavePreviewToDraftCommand => AuditedCommandKind.SaveRecipeDraft,
        ExitPreviewSessionCommand => AuditedCommandKind.Exit,
        PreviewSessionContinuationCommand continuation => continuation.OriginalCommandKind,
        _ => throw new InvalidOperationException("PreviewSessionCommandKindInvalid")
    };

    private static AuditCommandReference ReadCommandAuditReferenceByEventId(sqlite3 database,
        Guid eventId, StoreDeadline deadline)
    {
        var row = AuditChainDatabase.Read(database, @"
            SELECT e.Sequence,e.Hash,f.AttemptId,f.CorrelationId,f.RuntimeEpoch,f.CommandKind
            FROM audit_entries e JOIN command_facts f ON f.Position=e.FactPosition
            WHERE e.Kind='CommandFact' AND f.EventId=? LIMIT 2;", deadline,
            statement => new AuditCommandReference(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                ParsePreviewGuid(SqliteNative.ColumnText(statement, 2)),
                ParsePreviewGuid(SqliteNative.ColumnText(statement, 3)),
                ParsePreviewGuid(SqliteNative.ColumnText(statement, 4)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 5)), eventId.ToString("D"))
            .SingleOrDefault();
        AuditChainDatabase.Require(row.Sequence > 0 && row.Hash is { Length: 64 },
            "PreviewSessionCommandAuditMissing");
        return row;
    }

    private static AuditIdentityReference? ReadIdentityAuditReferenceByEventId(sqlite3 database,
        Guid eventId, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT e.Sequence,e.Hash,e.Payload
            FROM audit_entries e WHERE e.Kind='IdentityEvent' ORDER BY e.Sequence DESC;", deadline,
            statement => new AuditIdentityReference(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                SqliteNative.ColumnText(statement, 2) ?? string.Empty));
        var station = AuditChainDatabase.Text(database, "SELECT StationId FROM audit_policy WHERE Id=1;", deadline) ?? string.Empty;
        foreach (var row in rows)
        {
            try
            {
                if (IdentityAuditEvent.DecodeEventId(Convert.FromBase64String(row.Payload)) == eventId.ToString("D"))
                {
                    _ = row;
                    return row;
                }
            }
            catch (Exception) { }
        }
        return null;
    }

    private readonly record struct AuditCommandReference(long Sequence, string Hash, Guid AttemptId,
        Guid CorrelationId, Guid RuntimeEpoch, AuditedCommandKind CommandKind);
    private readonly record struct AuditIdentityReference(long Sequence, string Hash, string Payload);

    internal PreviewSessionCommandState ReadPreviewSessionCommandState(sqlite3 database,
        PreviewSessionCommand command, StoreDeadline deadline)
    {
        var options = _options.PreviewSessions;
        if (options is null)
            return new(false, null, Array.Empty<PreviewSessionEvent>(), null, null, null, false);
        options.Validate();
        RequireConfiguredPreviewSessions(database, options, deadline);
        var rows = ReadPreviewSessionRows(database, options, deadline);
        ValidatePreviewReplay(rows.Select(value => value.Event).ToArray(), options, database, deadline);
        var header = rows.Where(value => value.Event.SessionId == command.PreviewSessionId)
            .Select(value => value.Event.Header).LastOrDefault();
        var draftReference = header?.Draft ?? command switch
        {
            StartPreviewSessionCommand startCommand => startCommand.Draft,
            SavePreviewToDraftCommand save => save.ExpectedDraft,
            _ => null
        };
        var draft = draftReference is null ? null :
            ReadRecipeDraftRevisionByReference(database, draftReference, deadline,
                _options.RecipeDrafts ?? throw new InvalidOperationException("RecipeDraftConfigurationRequired"));
        var role = header?.LogicalCameraRole ?? draft?.Content.CameraRole;
        var camera = role is null ? null : ReadCameraSetupState(database, role, deadline);
        var activation = ReadRecipeActivationCommandState(database, deadline);
        // The public activation projection intentionally excludes the internal
        // T32 fixture. Preview recovery may use that exact immutable baseline,
        // so resolve the requested/header reference from the verified history,
        // never from the public Current projection.
        var expectedActive = command switch
        {
            StartPreviewSessionCommand startCommand => startCommand.ExpectedActive,
            _ => header?.ActiveActivation
        };
        var active = expectedActive is null ? null : activation.Records.SingleOrDefault(value =>
            value.Reference == expectedActive && value.Outcome.Succeeded &&
            value.SuccessfulSnapshot is not null);
        AuditChainDatabase.Require(expectedActive is null || active is not null,
            "PreviewActiveBaselineMissing");
        var admission = draft is not null && camera?.Binding is not null
            ? new PreviewSessionAdmissionInput(draft, camera, active) : null;
        var allEvents = rows.Where(value => value.Event.SessionId == command.PreviewSessionId)
            .Select(value => value.Event).ToArray();
        var current = allEvents.LastOrDefault();
        // Admission is a process-wide singleton.  A command for a fresh session
        // must observe a pending/recovery-blocked session from the durable ledger;
        // looking only at the requested SessionId would make a restart race able to
        // manufacture a second owner.
        var pending = rows.Select(value => value.Event).GroupBy(value => value.SessionId)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(value => value.Header.IsActive || value.Header.RecoveryRequired ||
                value.Restoration == PreviewRestorationState.RecoveryBlocked)
            .OrderBy(value => value.Position).LastOrDefault()?.Header;
        var recoveryRequired = rows.Any(value => value.Event.Header.RecoveryRequired ||
            value.Event.Restoration == PreviewRestorationState.RecoveryBlocked);
        CommandAuditFact? startFact = null;
        var start = rows.Select(value => value.Event)
            .Where(value => value.SessionId == command.PreviewSessionId &&
                value.CommandKind == AuditedCommandKind.StartPreview)
            .OrderBy(value => value.Position).FirstOrDefault();
        if (start is not null)
        {
            var reference = ReadCommandAuditReference(database, start.CommandAuditSequence!.Value,
                start.CommandAuditHash!, deadline);
            if (reference is not null)
                startFact = ReadFact(database, reference.AttemptId, 1, deadline);
        }
        return new PreviewSessionCommandState(true, current?.Header, allEvents, draft, camera,
            active, recoveryRequired, pending, startFact);
    }

    internal static void ValidatePreviewSessionHistory(sqlite3 database,
        PreviewSessionStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        RequireConfiguredPreviewSessions(database, options, deadline);
        var rows = ReadPreviewSessionRows(database, options, deadline);
        ValidatePreviewReplay(rows.Select(value => value.Event).ToArray(), options, database, deadline);
        var total = rows.Aggregate(0L, (sum, value) => checked(sum + value.Payload.Length));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "PreviewSessionEntryCapacityExceeded");
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
            "PreviewSessionTotalCapacityExceeded");
    }

    internal static PreviewSessionHistoryPage QueryPreviewSessionHistory(sqlite3 database,
        PreviewSessionStoreOptions options, PreviewSessionHistoryFilter filter, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 ||
            filter.PageSize is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(filter));
        options.Validate();
        RequireConfiguredPreviewSessions(database, options, deadline);
        var rows = ReadPreviewSessionRows(database, options, deadline);
        ValidatePreviewReplay(rows.Select(value => value.Event).ToArray(), options, database, deadline);
        var selected = rows.Where(value => (!filter.SessionId.HasValue ||
                value.Event.SessionId == filter.SessionId.Value) &&
            value.Position > filter.AfterPosition &&
            (!filter.ThroughPosition.HasValue || value.Position <= filter.ThroughPosition.Value))
            .Take(filter.PageSize).Select(value => value.Event).ToArray();
        var through = selected.Length == 0 ? filter.AfterPosition : selected[^1].Position;
        var hasMore = rows.Any(value => value.Position > through &&
            (!filter.SessionId.HasValue || value.Event.SessionId == filter.SessionId.Value) &&
            (!filter.ThroughPosition.HasValue || value.Position <= filter.ThroughPosition.Value));
        var pending = rows.Select(value => value.Event)
            .GroupBy(value => value.SessionId)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(value => (!filter.SessionId.HasValue || value.SessionId == filter.SessionId.Value) &&
                (value.Header.IsActive || value.Header.RecoveryRequired ||
                 value.Restoration == PreviewRestorationState.RecoveryBlocked))
            .OrderBy(value => value.Position).LastOrDefault();
        return new PreviewSessionHistoryPage(true, "PreviewSessionHistoryVerified",
            new ReadOnlyCollection<PreviewSessionEvent>(selected), through,
            hasMore ? through : null, pending?.Header, pending?.Header.RecoveryRequired == true);
    }

    internal static PreviewSessionHistoryReadResult ReadCurrentPreviewSession(sqlite3 database,
        PreviewSessionStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        RequireConfiguredPreviewSessions(database, options, deadline);
        var rows = ReadPreviewSessionRows(database, options, deadline);
        ValidatePreviewReplay(rows.Select(value => value.Event).ToArray(), options, database, deadline);
        var pending = rows.Select(value => value.Event)
            .GroupBy(value => value.SessionId)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(value => value.Header.IsActive || value.Header.RecoveryRequired ||
                value.Restoration == PreviewRestorationState.RecoveryBlocked)
            .OrderBy(value => value.Position).LastOrDefault();
        return new PreviewSessionHistoryReadResult(true,
            pending is null ? "PreviewSessionIdle" : pending.Header.RecoveryRequired ||
                pending.Restoration == PreviewRestorationState.RecoveryBlocked
                ? "PreviewSessionRecoveryRequired" : "PreviewSessionPending", pending?.Header,
            pending?.Header.RecoveryRequired == true ||
                pending?.Restoration == PreviewRestorationState.RecoveryBlocked);
    }

    internal static PreviewSessionHistoryReadResult ReadPreviewSession(sqlite3 database,
        PreviewSessionStoreOptions options, Guid sessionId, StoreDeadline deadline)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("PreviewSessionIdentityRequired", nameof(sessionId));
        options.Validate();
        RequireConfiguredPreviewSessions(database, options, deadline);
        var rows = ReadPreviewSessionRows(database, options, deadline);
        ValidatePreviewReplay(rows.Select(value => value.Event).ToArray(), options, database, deadline);
        var last = rows.Select(value => value.Event).LastOrDefault(value => value.SessionId == sessionId);
        return new PreviewSessionHistoryReadResult(true,
            last is null ? "PreviewSessionNotFound" : "PreviewSessionHistoryVerified", last?.Header,
            last?.Header.RecoveryRequired == true);
    }

    private static List<PreviewSessionStoredRow> ReadPreviewSessionRows(sqlite3 database,
        PreviewSessionStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var rows = AuditChainDatabase.Read(database,
            "SELECT Position,PreviousHash,Payload FROM preview_session_events ORDER BY Position;",
            deadline, statement =>
            {
                var position = SqliteNative.ColumnInt64(statement, 0);
                var previous = SqliteNative.ColumnText(statement, 1);
                var encoded = SqliteNative.ColumnText(statement, 2);
                AuditChainDatabase.Require(encoded is { Length: > 0 } &&
                    encoded.Length <= checked(options.MaximumPayloadBytes * 2),
                    "PreviewSessionPayloadCapacityExceeded");
                byte[] payload;
                try { payload = Convert.FromBase64String(encoded!); }
                catch (FormatException) { throw new InvalidOperationException("PreviewSessionPayloadInvalid"); }
                AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
                    "PreviewSessionPayloadCapacityExceeded");
                var value = PreviewSessionStorageCodec.Decode(payload);
                AuditChainDatabase.Require(value.Position == position &&
                    value.AuditSequence > 0 && value.AuditHash is { Length: 64 } &&
                    value.PayloadHash is { Length: 64 } &&
                    PreviewSessionStorageCodec.PayloadHash(EncodePreviewAuditPayload(value)) == value.PayloadHash,
                    "PreviewSessionEventBindingMismatch");
                return new PreviewSessionStoredRow(position, previous, payload, value);
            }).ToList();
        string? previousHash = null;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.PreviousHash == previousHash,
                "PreviewSessionPreviousHashMismatch");
            previousHash = row.Event.AuditHash;
        }
        return rows;
    }

    private static bool SameRecipeDraftRevision(RecipeDraftRevision left,
        RecipeDraftRevision right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Position == right.Position &&
            left.DraftId == right.DraftId &&
            left.Revision == right.Revision &&
            left.OperationId == right.OperationId &&
            left.PreviousRevisionContentHash == right.PreviousRevisionContentHash &&
            left.RevisionContentHash == right.RevisionContentHash &&
            left.Content.ContentHash == right.Content.ContentHash &&
            left.AuthorPrincipalId == right.AuthorPrincipalId &&
            left.AuthorSessionId == right.AuthorSessionId &&
            left.AuthorAuthorizationRevision == right.AuthorAuthorizationRevision &&
            left.ChangeReason == right.ChangeReason &&
            left.RecordedAtUtc == right.RecordedAtUtc;
    }

    private static void ValidatePreviewReplay(IReadOnlyList<PreviewSessionEvent> events,
        PreviewSessionStoreOptions options, sqlite3 database, StoreDeadline deadline)
    {
        if (events.Count == 0) return;
        AuditChainDatabase.Require(events.Count <= options.MaximumEntries,
            "PreviewSessionEntryCapacityExceeded");
        var total = 0L;
        var unfinished = 0;
        var previousPosition = 0L;
        foreach (var value in events.OrderBy(item => item.Position))
        {
            total = checked(total + PreviewSessionStorageCodec.Encode(value).Length);
            AuditChainDatabase.Require(value.Position == ++previousPosition,
                "PreviewSessionPositionGap");
            AuditChainDatabase.Require(value.Header.SessionId == value.SessionId &&
                value.Header.RuntimeEpoch == value.RuntimeEpoch &&
                value.Header.StartCorrelationId != Guid.Empty &&
                // Header.AttemptId is the immutable Start attempt. Later
                // action events have their own command attempt.
                (previousPosition != 1 || value.Header.AttemptId == value.AttemptId),
                "PreviewSessionHeaderBindingMismatch");
            AuditChainDatabase.Require(value.CommandAuditSequence is > 0 &&
                value.CommandAuditHash is { Length: 64 } && value.AuthorizationAuditSequence is > 0 &&
                value.AuthorizationAuditHash is { Length: 64 } && value.AuditSequence > 0 &&
                value.AuditHash is { Length: 64 }, "PreviewSessionAuthorizationAuditMissing");
            var central = AuditChainDatabase.Read(database, @"
                SELECT Kind,PreviewPosition,Payload,Hash,PreviousHash
                FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
                statement => (Kind: SqliteNative.ColumnText(statement, 0),
                    Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                    Payload: SqliteNative.ColumnText(statement, 2), Hash: SqliteNative.ColumnText(statement, 3),
                    Previous: SqliteNative.ColumnText(statement, 4)),
                value.AuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(central.Kind == PreviewSessionEventKind &&
                central.Position == value.Position && central.Hash == value.AuditHash &&
                central.Previous is { Length: 64 } && central.Payload is not null &&
                central.Payload == Convert.ToBase64String(EncodePreviewAuditPayload(value)),
                "PreviewSessionAuditBindingMismatch");
            var commandSequence = value.CommandAuditSequence ?? throw new InvalidOperationException(
                "PreviewSessionCommandAuditMismatch");
            var command = ReadCommandAuditReference(database, commandSequence,
                value.CommandAuditHash!, deadline) ?? throw new InvalidOperationException(
                    "PreviewSessionCommandAuditMismatch");
            AuditChainDatabase.Require(command.CorrelationId == value.CommandCorrelationId &&
                command.RuntimeEpoch == value.RuntimeEpoch && command.AttemptId == value.AttemptId &&
                command.CommandKind == value.CommandKind,
                "PreviewSessionCommandAuditMismatch");
            if (value.CommandKind != AuditedCommandKind.StartPreview && value.Terminal)
            {
                var actionOutcome = ReadFact(database, command.AttemptId, 1, deadline);
                AuditChainDatabase.Require(actionOutcome is not null &&
                    actionOutcome.Phase == CommandAuditPhase.Outcome &&
                    actionOutcome.Disposition == CommandDisposition.Accepted &&
                    SameCommandContext(actionOutcome!, command, value),
                    "PreviewSessionActionCommandMissing");
                var actionTerminal = ReadFact(database, command.AttemptId, 2, deadline);
                AuditChainDatabase.Require(actionTerminal is not null &&
                    actionTerminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
                    actionTerminal.Disposition is null &&
                    SameCommandContext(actionOutcome!, actionTerminal!),
                    "PreviewSessionActionCompletionMissing");
                ValidateFact(actionTerminal!);
            }
            var authorizationSequence = value.AuthorizationAuditSequence ?? throw new InvalidOperationException(
                "PreviewSessionAuthorizationAuditMismatch");
            var authorization = ReadIdentityAuditReference(database, authorizationSequence,
                value.AuthorizationAuditHash!, value, deadline);
            AuditChainDatabase.Require(authorization, "PreviewSessionAuthorizationAuditMismatch");
        }
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
            "PreviewSessionTotalCapacityExceeded");

        foreach (var session in events.GroupBy(item => item.SessionId))
        {
            var ordered = session.OrderBy(item => item.Position).ToArray();
            AuditChainDatabase.Require(ordered[0].CommandKind == AuditedCommandKind.StartPreview &&
                !ordered[0].Terminal && ordered[0].Phase == PreviewSessionPhase.Admitted &&
                ordered[0].Header.AttemptId == ordered[0].AttemptId,
                "PreviewSessionStartMissing");
            var startEvent = ordered[0];
            AuditChainDatabase.Require(startEvent.CommandAuditSequence is > 0 &&
                startEvent.CommandAuditHash is { Length: 64 }, "PreviewSessionStartAuditMissing");
            var startReference = ReadCommandAuditReference(database,
                startEvent.CommandAuditSequence!.Value, startEvent.CommandAuditHash!, deadline);
            AuditChainDatabase.Require(startReference is not null &&
                startReference.AttemptId == startEvent.AttemptId &&
                startReference.CorrelationId == startEvent.CommandCorrelationId &&
                startReference.RuntimeEpoch == startEvent.RuntimeEpoch &&
                startReference.CommandKind == AuditedCommandKind.StartPreview,
                "PreviewSessionStartAuditMismatch");
            var startOutcome = ReadFact(database, startReference!.AttemptId, 1, deadline);
            AuditChainDatabase.Require(startOutcome is not null &&
                startOutcome.Disposition == CommandDisposition.Accepted &&
                startOutcome.CommandKind == AuditedCommandKind.StartPreview &&
                startOutcome.Phase == CommandAuditPhase.Outcome &&
                startOutcome.AttemptId == startEvent.AttemptId &&
                startOutcome.CorrelationId == startEvent.CommandCorrelationId,
                "PreviewSessionStartCommandMissing");
            var startTerminalCount = ordered.Count(item => item.CommandKind == AuditedCommandKind.StartPreview && item.Terminal);
            AuditChainDatabase.Require(startTerminalCount <= 1, "PreviewSessionTerminalDuplicate");
            var last = ordered[^1];
            var terminal = last.Header.Phase is PreviewSessionPhase.Closed or PreviewSessionPhase.RecoveryBlocked;
            if (terminal)
            {
                // Historical admitted/streaming headers remain immutable and
                // active. Only the final header describes the session's current
                // ownership; requiring every historical header to be inactive
                // would reject a valid close sequence.
                AuditChainDatabase.Require(!last.Header.IsActive && last.Terminal,
                    "PreviewSessionTerminalMissing");
                var startTerminal = ReadFact(database, startEvent.AttemptId, 2, deadline);
                AuditChainDatabase.Require(startTerminal is not null &&
                    startTerminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
                    startTerminal.Disposition is null && SameCommandContext(startOutcome!, startTerminal),
                    "PreviewSessionStartCompletionMissing");
                ValidateFact(startTerminal!);
            }
            else
            {
                AuditChainDatabase.Require(last.Header.IsActive && !last.Header.RecoveryRequired,
                    "PreviewSessionReplayInvalid");
                AuditChainDatabase.Require(ReadFact(database, startEvent.AttemptId, 2, deadline) is null,
                    "PreviewSessionStartTerminalUnexpected");
                unfinished++;
            }
            var terminalIndex = -1;
            for (var index = 0; index < ordered.Length; index++)
            {
                if (ordered[index].Header.Phase is PreviewSessionPhase.Closed or
                    PreviewSessionPhase.RecoveryBlocked)
                {
                    terminalIndex = index;
                    break;
                }
            }
            AuditChainDatabase.Require(terminalIndex < 0 || terminalIndex == ordered.Length - 1,
                "PreviewSessionEventAfterTerminal");
            for (var i = 1; i < ordered.Length; i++)
            {
                var current = ordered[i];
                var prior = ordered[i - 1];
                AuditChainDatabase.Require(current.Header.StartCorrelationId == prior.Header.StartCorrelationId &&
                    current.Header.AttemptId == prior.Header.AttemptId &&
                    current.Header.Draft == prior.Header.Draft &&
                    current.Header.CurrentBinding == prior.Header.CurrentBinding &&
                    current.ActorPrincipalId == prior.ActorPrincipalId &&
                    current.ActorSessionId == prior.ActorSessionId,
                    "PreviewSessionReplayBindingMismatch");
                AuditChainDatabase.Require(current.CommandKind is AuditedCommandKind.Tune or
                    AuditedCommandKind.Freeze or AuditedCommandKind.SaveRecipeDraft or
                    AuditedCommandKind.Exit or AuditedCommandKind.StartPreview,
                    "PreviewSessionCommandKindInvalid");
            }
        }
        AuditChainDatabase.Require(unfinished <= 1, "PreviewSessionMultipleUnfinished");
    }

    private static byte[] EncodePreviewAuditPayload(PreviewSessionEvent value)
    {
        var withoutAudit = new PreviewSessionEvent(value.Position, value.Header, value.AttemptId,
            value.CommandCorrelationId, value.CommandKind, value.Phase, value.Restoration,
            value.ReasonCode, value.Terminal, value.ActorPrincipalId, value.ActorSessionId,
            value.ActorAuthorizationRevision, value.AuthorizationTarget, value.RecordedAtUtc,
            value.Configuration, value.SavedDraft, value.FrozenSettingsContentHash,
            value.CommandAuditSequence, value.CommandAuditHash, value.AuthorizationAuditSequence,
            value.AuthorizationAuditHash, null, 0, null);
        return PreviewSessionStorageCodec.Encode(withoutAudit);
    }

    private static bool SameCommandContext(CommandAuditFact fact,
        CommandAuditReference reference, PreviewSessionEvent value) =>
        fact.AttemptId == reference.AttemptId && fact.CorrelationId == reference.CorrelationId &&
        fact.RuntimeEpoch == reference.RuntimeEpoch && fact.CommandKind == reference.CommandKind &&
        fact.AttemptId == value.AttemptId && fact.CorrelationId == value.CommandCorrelationId &&
        fact.RuntimeEpoch == value.RuntimeEpoch;

    private static CommandAuditReference? ReadCommandAuditReference(sqlite3 database,
        long sequence, string hash, StoreDeadline deadline)
    {
        var row = AuditChainDatabase.Read(database, @"
            SELECT e.Hash,f.AttemptId,f.CorrelationId,f.RuntimeEpoch,f.CommandKind
            FROM audit_entries e JOIN command_facts f ON f.Position=e.FactPosition
            WHERE e.Sequence=? AND e.Kind='CommandFact' LIMIT 2;", deadline,
            statement => (Hash: SqliteNative.ColumnText(statement, 0),
                AttemptId: ParsePreviewGuid(SqliteNative.ColumnText(statement, 1)),
                CorrelationId: ParsePreviewGuid(SqliteNative.ColumnText(statement, 2)),
                RuntimeEpoch: ParsePreviewGuid(SqliteNative.ColumnText(statement, 3)),
                CommandKind: (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 4)),
            sequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        return row.Hash == hash ? new CommandAuditReference(row.Hash, row.AttemptId,
            row.CorrelationId, row.RuntimeEpoch, row.CommandKind) : null;
    }

    private static bool ReadIdentityAuditReference(sqlite3 database, long sequence,
        string hash, PreviewSessionEvent value, StoreDeadline deadline)
    {
        var schemaVersion = checked((int)AuditChainDatabase.Scalar(database,
            "PRAGMA user_version;", deadline));
        var row = AuditChainDatabase.Read(database,
            "SELECT Kind,Hash,Payload FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent' LIMIT 2;",
            deadline, statement => (Kind: SqliteNative.ColumnText(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1), Payload: SqliteNative.ColumnText(statement, 2)),
            sequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        if (row.Kind != "IdentityEvent" || row.Hash != hash || row.Payload is null) return false;
        try
        {
            var payload = Convert.FromBase64String(row.Payload);
            var ordinal = AuditChainDatabase.Scalar(database,
                "SELECT IdentityPosition FROM audit_entries WHERE Sequence=?;", deadline,
                sequence.ToString(CultureInfo.InvariantCulture));
            var station = AuditChainDatabase.Text(database,
                "SELECT StationId FROM audit_policy WHERE Id=1;", deadline) ?? string.Empty;
            IdentityAuditEvent.VerifyPayload(payload, ordinal, station, schemaVersion);
            Guid? stepUpGrantId = null;
            if (value.CommandKind == AuditedCommandKind.SaveRecipeDraft)
            {
                var commandReference = ReadCommandAuditReference(database,
                    value.CommandAuditSequence!.Value, value.CommandAuditHash!, deadline);
                var commandFact = commandReference is null ? null :
                    ReadFact(database, commandReference.AttemptId, 1, deadline);
                AuditChainDatabase.Require(commandFact is not null,
                    "PreviewSessionCommandAuditMissing");
                stepUpGrantId = commandFact!.ClaimedStepUpGrantId;
            }
            return value.CommandKind == AuditedCommandKind.SaveRecipeDraft
                ? IdentityAuditEvent.MatchesPreviewDraftAuthorization(payload, ordinal, station, value,
                    stepUpGrantId)
                : IdentityAuditEvent.MatchesPreviewAuthorization(payload, ordinal, station, value);
        }
        catch (Exception) { return false; }
    }

    private static Guid ParsePreviewGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty ? result :
        throw new InvalidOperationException("PreviewSessionGuidInvalid");

    private sealed record PreviewSessionStoredRow(long Position, string? PreviousHash,
        byte[] Payload, PreviewSessionEvent Event);
    private sealed record CommandAuditReference(string? Hash, Guid AttemptId, Guid CorrelationId,
        Guid RuntimeEpoch, AuditedCommandKind CommandKind);
}

internal sealed record PreviewSessionCommandState(bool Enabled,
    PreviewSessionHeader? Header, IReadOnlyList<PreviewSessionEvent> Events,
    RecipeDraftRevision? Draft, CameraSetupStoreSnapshot? CurrentBinding,
    RecipeActivationRecord? ActiveBaseline, bool RecoveryRequired,
    PreviewSessionHeader? PendingHeader = null,
    CommandAuditFact? StartCommandFact = null);

/// <summary>Verified startup recovery boundary for the one durable Preview owner.</summary>
internal sealed record PreviewSessionRecoveryState(bool Available, string ReasonCode,
    PreviewSessionHeader? Header = null, CommandAuditFact? StartFact = null,
    RecipeDraftRevision? Draft = null, RecipeActivationRecord? ActiveBaseline = null,
    bool RecoveryRequired = false);
