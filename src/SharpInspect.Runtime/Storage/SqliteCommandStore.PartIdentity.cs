using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The independent rejection writer receives an already constructed immutable
/// event.  A correction is different: the caller may supply only the command
/// and the freshly authorized actor tuple.  The writer resolves the immutable
/// admission/Core evidence while it owns the same BEGIN IMMEDIATE transaction,
/// so a public caller cannot smuggle a replacement evidence object into the
/// correction ledger.
/// </summary>
internal sealed record PartIdentityCorrectionWrite(
    CorrectProductionPartIdentityCommand Command,
    Guid RuntimeEpoch,
    Guid AttemptId,
    Guid ActorPrincipalId,
    Guid ActorSessionId,
    long AuthorizationRevision,
    Guid StepUpGrantId,
    DateTimeOffset AuthorizedAtUtc);

internal sealed record PartIdentityWriteRequest(
    PartIdentityHistoryEvent? Event = null,
    PartIdentityCorrectionWrite? Correction = null);

internal sealed class PartIdentityWork
{
    internal PartIdentityWork(PartIdentityWriteRequest request) => Request = request;
    internal PartIdentityWriteRequest Request { get; }
    internal TaskCompletionSource<PartIdentityHistoryEvent> EventCompletion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record PartIdentityStoredRow(long Position, string? PreviousHash,
    PartIdentityHistoryEvent Event, byte[] Payload, string PayloadHash);

internal sealed partial class SqliteCommandStore
{
    internal const string PartIdentitySchemaSql = @"
        CREATE TABLE part_identity_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE part_identity_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 2),
            CorrelationId TEXT NOT NULL CHECK(length(CorrelationId)=36),
            AttemptId TEXT NOT NULL CHECK(length(AttemptId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            StationId TEXT NOT NULL,
            ControllerEpoch INTEGER NOT NULL CHECK(ControllerEpoch>=0),
            CycleSequence INTEGER NOT NULL CHECK(CycleSequence>=0),
            EndpointBindingHash TEXT NOT NULL CHECK(length(EndpointBindingHash)=64),
            ReasonCode TEXT NOT NULL,
            InspectionId TEXT NULL CHECK(InspectionId IS NULL OR length(InspectionId)=36),
            AdmissionContentHash TEXT NULL CHECK(AdmissionContentHash IS NULL OR length(AdmissionContentHash)=64),
            ExpectedPreviousCorrectionHash TEXT NULL CHECK(ExpectedPreviousCorrectionHash IS NULL OR length(ExpectedPreviousCorrectionHash)=64),
            OldValue TEXT NULL, NewValue TEXT NULL,
            ActorPrincipalId TEXT NULL CHECK(ActorPrincipalId IS NULL OR length(ActorPrincipalId)=36),
            ActorSessionId TEXT NULL CHECK(ActorSessionId IS NULL OR length(ActorSessionId)=36),
            AuthorizationRevision INTEGER NOT NULL CHECK(AuthorizationRevision>=0),
            StepUpGrantId TEXT NULL CHECK(StepUpGrantId IS NULL OR length(StepUpGrantId)=36),
            AuthorizationTarget TEXT NULL,
            RecordedAtUtc TEXT NOT NULL,
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL,
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>=0),
            CommandAuditHash TEXT NULL CHECK(CommandAuditHash IS NULL OR length(CommandAuditHash)=64),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>=0),
            AuthorizationAuditHash TEXT NULL CHECK(AuthorizationAuditHash IS NULL OR length(AuthorizationAuditHash)=64),
            CHECK(Kind=1 OR (ControllerEpoch>0 AND CycleSequence>0)),
            CHECK((CommandAuditSequence=0 AND CommandAuditHash IS NULL) OR
                  (CommandAuditSequence>0 AND CommandAuditHash IS NOT NULL)),
            CHECK((AuthorizationAuditSequence=0 AND AuthorizationAuditHash IS NULL) OR
                  (AuthorizationAuditSequence>0 AND AuthorizationAuditHash IS NOT NULL)));
        CREATE INDEX ix_part_identity_inspection ON part_identity_events(InspectionId,Position);
        CREATE INDEX ix_part_identity_correlation ON part_identity_events(CorrelationId,Position);
        CREATE TRIGGER part_identity_config_immutable_update BEFORE UPDATE ON part_identity_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutablePartIdentityConfiguration'); END;
        CREATE TRIGGER part_identity_config_immutable_delete BEFORE DELETE ON part_identity_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutablePartIdentityConfiguration'); END;
        CREATE TRIGGER part_identity_event_immutable_update BEFORE UPDATE ON part_identity_events BEGIN
            SELECT RAISE(ABORT,'ImmutablePartIdentityEvent'); END;
        CREATE TRIGGER part_identity_event_immutable_delete BEFORE DELETE ON part_identity_events BEGIN
            SELECT RAISE(ABORT,'ImmutablePartIdentityEvent'); END;";

    internal static void InitializePartIdentitySchema(sqlite3 database,
        PartIdentityStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        options.Validate();
        SqliteNative.Execute(database, PartIdentitySchemaSql, deadline);
        AuditChainDatabase.Execute(database,
            "INSERT INTO part_identity_store_config VALUES(1,1,?,?,?,?);", deadline,
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendPartIdentityStoreActivation(database, policy, signingKey, options, deadline);
    }

    internal static void RequireConfiguredPartIdentity(sqlite3 database,
        PartIdentityStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var row = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM part_identity_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (Format: SqliteNative.ColumnInt64(statement, 0),
                Entries: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnInt64(statement, 2),
                Total: SqliteNative.ColumnInt64(statement, 3),
                Binding: SqliteNative.ColumnText(statement, 4))).SingleOrDefault();
        AuditChainDatabase.Require(row.Format == PartIdentityStoreOptions.FormatVersion &&
            row.Entries == options.MaximumEntries && row.Payload == options.MaximumPayloadBytes &&
            row.Total == options.MaximumTotalBytes && row.Binding == options.BindingHash,
            "PartIdentityConfigurationMismatch");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='PartIdentityStoreActivated';", deadline) == 1,
            "PartIdentityActivationMissing");
    }

    internal static IReadOnlyList<PartIdentityStoredRow> ReadPartIdentityRows(sqlite3 database,
        PartIdentityStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredPartIdentity(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,Payload,PayloadHash
            FROM part_identity_events ORDER BY Position LIMIT ?;", deadline,
            statement => ReadPartIdentityRow(statement, options),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "PartIdentityEntryCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
            "PartIdentityTotalCapacityExceeded");
        ValidatePartIdentityHistory(rows);
        return rows;
    }

    internal static long ReadPartIdentityAuditReserve(sqlite3 database, StoreDeadline deadline) => 0;

    internal static void VerifyPartIdentityActivationPayload(sqlite3 database, byte[] payload,
        PartIdentityStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "PartIdentityActivationPayloadMismatch");
        RequireConfiguredPartIdentity(database, options, deadline);
    }

    internal static void VerifyPartIdentityAuditPayload(sqlite3 database, long position,
        byte[] payload, PartIdentityStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var row = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,Payload,PayloadHash
            FROM part_identity_events WHERE Position=? LIMIT 2;", deadline,
            statement => ReadPartIdentityRow(statement, options),
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null && row.Position == position,
            "PartIdentityHistoryMissing");
        var central = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Hash,Payload FROM audit_entries
            WHERE PartIdentityPosition=? AND Kind='PartIdentityEvent' LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2)),
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(central.Hash is not null && central.Payload is not null,
            "PartIdentityAuditMissing");
        AuditChainDatabase.Require(row!.Event.AuditSequence == central.Sequence &&
            string.Equals(row.Event.AuditHash, central.Hash, StringComparison.Ordinal),
            "PartIdentityAuditReferenceMismatch");
        var expectedProjection = PartIdentityStorageCodec.EncodeAuditPayload(row.Event);
        AuditChainDatabase.Require(Convert.FromBase64String(central.Payload!).AsSpan()
                .SequenceEqual(expectedProjection) && payload.AsSpan().SequenceEqual(expectedProjection),
            "PartIdentityAuditPayloadMismatch");
    }

    internal static void ValidatePartIdentityHistory(sqlite3 database,
        PartIdentityStoreOptions options, StoreDeadline deadline)
    {
        var rows = ReadPartIdentityRows(database, options, deadline);
        ValidatePartIdentityHistory(rows);
        ValidatePartIdentityColumnProjection(database, rows, options, deadline);
        foreach (var row in rows)
            VerifyPartIdentityAuditPayload(database, row.Position,
                PartIdentityStorageCodec.EncodeAuditPayload(row.Event), options, deadline);
        ValidatePartIdentityAuthorizationAndSourceBindings(database, rows, deadline);
    }

    /// <summary>
    /// The event payload is the signed source of truth, but the SQLite columns
    /// are deliberately duplicated for indexed reads.  A cold reader must
    /// verify that projection too; otherwise a tampered indexed column can be
    /// silently returned by a query while the payload and audit chain remain
    /// intact.
    /// </summary>
    private static void ValidatePartIdentityColumnProjection(sqlite3 database,
        IReadOnlyList<PartIdentityStoredRow> rows, PartIdentityStoreOptions options,
        StoreDeadline deadline)
    {
        var projected = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,EventId,Kind,CorrelationId,AttemptId,RuntimeEpoch,
                StationId,ControllerEpoch,CycleSequence,EndpointBindingHash,ReasonCode,
                InspectionId,AdmissionContentHash,ExpectedPreviousCorrectionHash,OldValue,NewValue,
                ActorPrincipalId,ActorSessionId,AuthorizationRevision,StepUpGrantId,
                AuthorizationTarget,RecordedAtUtc,ContentHash,PayloadHash,Payload,AuditSequence,
                AuditHash,CommandAuditSequence,CommandAuditHash,AuthorizationAuditSequence,
                AuthorizationAuditHash
            FROM part_identity_events ORDER BY Position LIMIT ?;", deadline,
            statement => Enumerable.Range(0, 32)
                .Select(index => SqliteNative.ColumnText(statement, index)).ToArray(),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(projected.Count == rows.Count,
            "PartIdentityColumnProjectionCountMismatch");
        for (var index = 0; index < projected.Count; index++)
        {
            var row = rows[index];
            var value = row.Event;
            var expected = new string?[]
            {
                row.Position.ToString(CultureInfo.InvariantCulture), value.PreviousHash,
                value.EventId.ToString("D"), ((int)value.Kind).ToString(CultureInfo.InvariantCulture),
                value.CorrelationId.ToString("D"), value.AttemptId.ToString("D"),
                value.RuntimeEpoch.ToString("D"), value.StationId,
                value.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
                value.CycleSequence.ToString(CultureInfo.InvariantCulture), value.EndpointBindingHash,
                value.ReasonCode, value.InspectionId?.ToString("D"), value.AdmissionContentHash,
                value.ExpectedPreviousCorrectionHash, value.OldValue, value.NewValue,
                value.ActorPrincipalId?.ToString("D"), value.ActorSessionId?.ToString("D"),
                value.AuthorizationRevision.ToString(CultureInfo.InvariantCulture),
                value.StepUpGrantId?.ToString("D"), value.AuthorizationTarget,
                value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                value.ContentHash, row.PayloadHash, Convert.ToBase64String(row.Payload),
                value.AuditSequence.ToString(CultureInfo.InvariantCulture), value.AuditHash,
                value.CommandAuditSequence.ToString(CultureInfo.InvariantCulture), value.CommandAuditHash,
                value.AuthorizationAuditSequence.ToString(CultureInfo.InvariantCulture),
                value.AuthorizationAuditHash
            };
            AuditChainDatabase.Require(expected.Length == projected[index].Length &&
                expected.Zip(projected[index], (left, right) =>
                    string.Equals(left, right, StringComparison.Ordinal)).All(equal => equal),
                "PartIdentityColumnProjectionMismatch");
        }
    }

    private static void ValidatePartIdentityAuthorizationAndSourceBindings(sqlite3 database,
        IReadOnlyList<PartIdentityStoredRow> rows, StoreDeadline deadline)
    {
        var corrections = rows.Select(value => value.Event)
            .Where(value => value.Kind == PartIdentityHistoryEventKind.Correction).ToArray();
        foreach (var rejected in rows.Select(value => value.Event)
                     .Where(value => value.Kind == PartIdentityHistoryEventKind.RejectedTrigger))
        {
            AuditChainDatabase.Require(rejected.RejectionContext is not null && rejected.Evidence is null &&
                rejected.InspectionId is null && rejected.AdmissionContentHash is null &&
                rejected.ExpectedPreviousCorrectionHash is null && rejected.OldValue is null &&
                rejected.NewValue is null && rejected.ActorPrincipalId is null &&
                rejected.ActorSessionId is null && rejected.AuthorizationRevision == 0 &&
                rejected.StepUpGrantId is null && rejected.AuthorizationTarget is null &&
                rejected.CommandAuditSequence == 0 && rejected.CommandAuditHash is null &&
                rejected.AuthorizationAuditSequence == 0 && rejected.AuthorizationAuditHash is null,
                "PartIdentityRejectedAuthorizationReferenceInvalid");
        }
        if (corrections.Length == 0) return;

        var productionOptions = ReadProductionInspectionOptions(database, deadline);
        AuditChainDatabase.Require(productionOptions is not null,
            "PartIdentityCorrectionProductionInspectionConfigurationRequired");
        ValidateProductionInspectionHistory(database, productionOptions!, deadline);
        var productionRows = ReadProductionInspectionRows(database, productionOptions!, deadline);
        foreach (var productionRow in productionRows)
            VerifyProductionInspectionAuditPayload(database, productionRow.Position,
                ProductionInspectionStorageCodec.EncodeAuditBinding(productionRow.Event),
                productionOptions!, deadline);
        var station = ReadPartIdentityStation(database, deadline);
        foreach (var value in corrections)
        {
            AuditChainDatabase.Require(value.CommandAuditSequence > 0 &&
                value.CommandAuditHash is { Length: 64 } && value.AuthorizationAuditSequence > 0 &&
                value.AuthorizationAuditHash is { Length: 64 } && value.AuditSequence > 0,
                "PartIdentityCorrectionAuthorizationReferenceMissing");
            var inspectionRows = productionRows.Where(row =>
                row.Event.InspectionId == value.InspectionId).ToArray();
            var admissions = inspectionRows.Where(row =>
                row.Event.Kind == ProductionInspectionEventKind.Admitted).ToArray();
            var cores = inspectionRows.Where(row =>
                row.Event.Kind == ProductionInspectionEventKind.CoreCommitted).ToArray();
            AuditChainDatabase.Require(admissions.Length == 1 && cores.Length == 1,
                "PartIdentityCorrectionProductionInspectionMissing");
            var admission = admissions[0].Event.Admission;
            var core = cores[0].Event.Core;
            var priorCorrection = rows.Select(item => item.Event)
                .Where(item => item.Kind == PartIdentityHistoryEventKind.Correction &&
                    item.InspectionId == value.InspectionId && item.Position < value.Position)
                .OrderBy(item => item.Position).LastOrDefault();
            AuditChainDatabase.Require(core is not null && core.State == ProductionInspectionState.CoreCommitted &&
                string.Equals(admission.ContentHash, value.AdmissionContentHash, StringComparison.Ordinal) &&
                string.Equals(core.Admission.ContentHash, admission.ContentHash, StringComparison.Ordinal) &&
                value.StationId == admission.StationId &&
                value.ControllerEpoch == admission.ControllerCycle.ControllerEpoch &&
                value.CycleSequence == admission.ControllerCycle.CycleSequence &&
                string.Equals(value.EndpointBindingHash, admission.EndpointBindingHash, StringComparison.Ordinal) &&
                admission.PartIdentityEvidence is not null && value.Evidence is not null &&
                string.Equals(value.Evidence.ContentHash, admission.PartIdentityEvidence.ContentHash,
                    StringComparison.Ordinal) &&
                string.Equals(value.Evidence.Value, admission.PartIdentityEvidence.Value,
                    StringComparison.Ordinal) && string.Equals(value.OldValue,
                    priorCorrection?.NewValue ?? admission.PartIdentityEvidence.Value,
                    StringComparison.Ordinal) &&
                value.InspectionId is { } inspectionId && value.NewValue is { } correctedValue &&
                value.AuthorizationTarget == CorrectProductionPartIdentityCommand.ComputeAuthorizationTarget(
                    inspectionId, value.AdmissionContentHash!, value.ExpectedPreviousCorrectionHash,
                    value.OldValue, correctedValue, value.ReasonCode),
                "PartIdentityCorrectionSourceBindingMismatch");

            var accepted = ReadFact(database, value.AttemptId, aggregateSequence: 1, deadline);
            AuditChainDatabase.Require(accepted is not null && accepted.Phase == CommandAuditPhase.Outcome &&
                accepted.Disposition == CommandDisposition.Accepted &&
                accepted.CommandKind == AuditedCommandKind.CorrectHistoricalFact &&
                accepted.CorrelationId == value.CorrelationId && accepted.RuntimeEpoch == value.RuntimeEpoch &&
                accepted.Source == CommandSource.PhysicalConsole &&
                accepted.ClaimedPrincipalId == value.ActorPrincipalId?.ToString("D") &&
                accepted.ClaimedSessionId == value.ActorSessionId &&
                accepted.AuthenticatedHumanPrincipalId == value.ActorPrincipalId?.ToString("D") &&
                accepted.ClaimedStepUpGrantId == value.StepUpGrantId &&
                accepted.ReasonCode == "PartIdentityCorrectionAuthorized", "PartIdentityCorrectionCommandMismatch");
            var commandReference = ReadCommandAuditReference(database, accepted!.EventId, deadline);
            AuditChainDatabase.Require(commandReference.Sequence == value.CommandAuditSequence &&
                commandReference.Hash == value.CommandAuditHash && commandReference.Sequence < value.AuditSequence,
                "PartIdentityCorrectionCommandAuditMismatch");

            var identity = AuditChainDatabase.Read(database, @"
                SELECT Sequence,Hash,Payload,IdentityPosition FROM audit_entries
                WHERE Sequence=? AND Kind='IdentityEvent' LIMIT 2;", deadline,
                statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                    Hash: SqliteNative.ColumnText(statement, 1),
                    Payload: SqliteNative.ColumnText(statement, 2),
                    Position: SqliteNative.ColumnInt64(statement, 3)),
                value.AuthorizationAuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(identity.Sequence == value.AuthorizationAuditSequence &&
                identity.Hash == value.AuthorizationAuditHash && identity.Payload is not null &&
                identity.Position > 0 && identity.Sequence < value.AuditSequence,
                "PartIdentityCorrectionAuthorizationAuditMissing");
            byte[] identityPayload;
            try { identityPayload = Convert.FromBase64String(identity.Payload!); }
            catch (FormatException exception)
            { throw new InvalidOperationException("PartIdentityCorrectionAuthorizationAuditInvalid", exception); }
            AuditChainDatabase.Require(IdentityAuditEvent.MatchesPartIdentityCorrectionAuthorization(
                identityPayload, identity.Position, station, value, accepted!),
                "PartIdentityCorrectionAuthorizationAuditMismatch");
        }
    }

    private static string ReadPartIdentityStation(sqlite3 database, StoreDeadline deadline)
    {
        var row = AuditChainDatabase.Read(database, @"
            SELECT StationId,PolicyContentHash FROM identity_policy_binding WHERE Id=1 LIMIT 2;",
            deadline, statement => (StationId: SqliteNative.ColumnText(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1))).SingleOrDefault();
        AuditChainDatabase.Require(row.StationId is { Length: > 0 } && AuditCanonical.IsHash(row.Hash),
            "PartIdentityIdentityPolicyMissing");
        // The authorization policy is carried and signed in every identity
        // event.  The binding table protects the station and installation
        // identity; the matcher additionally requires a non-empty policy
        // tuple and its canonical hash.  PolicyContentHash is intentionally
        // not treated as the authorization-policy hash: they are different
        // contracts.
        return row.StationId!;
    }

    private static ProductionInspectionStoreOptions? ReadProductionInspectionOptions(
        sqlite3 database, StoreDeadline deadline)
    {
        var exists = AuditChainDatabase.Scalar(database, @"
            SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND
                name='production_inspection_store_config';", deadline);
        if (exists != 1) return null;
        var row = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes
            FROM production_inspection_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (Format: SqliteNative.ColumnInt64(statement, 0),
                Entries: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnInt64(statement, 2),
                Total: SqliteNative.ColumnInt64(statement, 3))).SingleOrDefault();
        AuditChainDatabase.Require(row.Format == ProductionInspectionStoreOptions.FormatVersion,
            "PartIdentityCorrectionProductionInspectionConfigurationInvalid");
        return new ProductionInspectionStoreOptions
        {
            MaximumEntries = checked((int)row.Entries),
            MaximumPayloadBytes = checked((int)row.Payload),
            MaximumTotalBytes = row.Total
        };
    }

    internal ValueTask<StoreWriteResult> AppendPartIdentityEventAsync(
        PartIdentityWriteRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        if (!PartIdentityEnabled)
            return ValueTask.FromException<StoreWriteResult>(
                new InvalidOperationException("PartIdentityConfigurationRequired"));
        ArgumentNullException.ThrowIfNull(request);
        var independentEvent = request.Event;
        if (independentEvent is null || request.Correction is not null)
            return ValueTask.FromException<StoreWriteResult>(
                new InvalidOperationException("PartIdentityIndependentEventRequired"));
        try { RequireIndependentRejectionEvent(independentEvent); }
        catch (InvalidOperationException exception) { return ValueTask.FromException<StoreWriteResult>(exception); }
        return AppendPartIdentityEventCoreAsync(request, deadline, cancellationToken);
    }

    private async ValueTask<StoreWriteResult> AppendPartIdentityEventCoreAsync(
        PartIdentityWriteRequest request, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || _queue is null || _queueSlots is null || _worker.IsCompleted)
            return new(false, "PartIdentityUnavailable");
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (deadline.Remaining <= TimeSpan.Zero)
            return new(false, "PartIdentityCommitDeadlineExceeded");
        if (!await _queueSlots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false))
            return new(false, "PartIdentityCommitDeadlineExceeded");
        var independentEvent = request.Event ??
            throw new InvalidOperationException("PartIdentityIndependentEventRequired");
        AuditChainDatabase.Require(request.Correction is null,
            "PartIdentityIndependentEventRequired");
        RequireIndependentRejectionEvent(independentEvent);
        var work = new PartIdentityWork(request);
        var queued = new WriteRequest(null, deadline, PartIdentity: work);
        if (!_queue.Writer.TryWrite(queued))
        {
            _queueSlots.Release();
            return new(false, "PartIdentityUnavailable");
        }
        return await queued.Completion.Task.ConfigureAwait(false);
    }

    private StoreWriteResult AppendPartIdentityCore(sqlite3 database, PartIdentityWork work,
        StoreDeadline deadline)
    {
        var options = _options.PartIdentities ??
            throw new InvalidOperationException("PartIdentityConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredPartIdentity(database, options, deadline);
            var verification = VerifyForProtocolLedger(database, deadline);
            AuditChainDatabase.Require(verification.State == AuditIntegrityState.Verified,
                verification.ReasonCode);
            AuditChainDatabase.RequireFullPartIdentityVerification(database, verification,
                deadline, options);
            var candidate = work.Request.Event ??
                throw new InvalidOperationException("PartIdentityIndependentEventRequired");
            RequireIndependentRejectionEvent(candidate);
            var persisted = AppendPartIdentityWithinTransaction(database, candidate,
                candidate.CommandAuditSequence, candidate.CommandAuditHash,
                candidate.AuthorizationAuditSequence, candidate.AuthorizationAuditHash,
                deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.EventCompletion.TrySetResult(persisted);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(policy, AuditIntegrityState.Verifying,
                "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new(true, "PartIdentityPersisted");
        }
        catch (InvalidOperationException exception) when (AuditChainDatabase.IsCapacityReason(exception.Message))
        { return new(false, exception.Message); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "PartIdentityCommitFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    /// <summary>
    /// Appends one PartIdentity row while the caller already owns BEGIN IMMEDIATE.
    /// Identity command/authorization references are supplied by the identity writer
    /// after it has appended the corresponding central facts.  This is the only path
    /// used by a correction, so the authorization and correction row cannot commit
    /// independently.
    /// </summary>
    internal PartIdentityHistoryEvent AppendPartIdentityWithinTransaction(
        sqlite3 database, PartIdentityHistoryEvent candidate,
        long commandAuditSequence, string? commandAuditHash,
        long authorizationAuditSequence, string? authorizationAuditHash,
        StoreDeadline deadline)
    {
        var options = _options.PartIdentities ??
            throw new InvalidOperationException("PartIdentityConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        AuditChainDatabase.Require(commandAuditSequence >= 0 &&
            (commandAuditSequence == 0) == (commandAuditHash is null) &&
            authorizationAuditSequence >= 0 &&
            (authorizationAuditSequence == 0) == (authorizationAuditHash is null),
            "PartIdentityAuditReferenceInvalid");
        if (candidate.Kind == PartIdentityHistoryEventKind.RejectedTrigger)
            RequireIndependentRejectionEvent(candidate);
        if (candidate.Kind == PartIdentityHistoryEventKind.Correction)
            AuditChainDatabase.Require(commandAuditSequence > 0 && authorizationAuditSequence > 0,
                "PartIdentityAuthorizationAuditMissing");

        var rows = ReadPartIdentityRows(database, options, deadline);
        ValidatePartIdentityColumnProjection(database, rows, options, deadline);
        AuditChainDatabase.Require(rows.Count < options.MaximumEntries,
            "PartIdentityEntryCapacityExceeded");
        var position = checked(rows.Count + 1L);
        var previousHash = rows.Count == 0 ? null : rows[^1].Event.ContentHash;
        var provisional = RebuildReferences(candidate, position, previousHash, 0, null,
            commandAuditSequence, commandAuditHash, authorizationAuditSequence, authorizationAuditHash);
        ValidatePartIdentityTransition(rows.Select(value => value.Event).ToArray(), provisional);
        var centralPayload = PartIdentityStorageCodec.EncodeAuditPayload(provisional);
        var central = AuditChainDatabase.AppendPartIdentityLedgerEntry(database, policy, signingKey,
            position, centralPayload, options, deadline);
        var persisted = RebuildReferences(provisional, position, previousHash, central.Sequence, central.Hash,
            commandAuditSequence, commandAuditHash, authorizationAuditSequence, authorizationAuditHash);
        var payload = PartIdentityStorageCodec.Encode(persisted);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
            "PartIdentityPayloadCapacityExceeded");
        var used = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        AuditChainDatabase.Require(checked(used + payload.Length) <= options.MaximumTotalBytes,
            "PartIdentityTotalCapacityExceeded");
        var payloadHash = PartIdentityStorageCodec.Hash(payload);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO part_identity_events(
                Position,PreviousHash,EventId,Kind,CorrelationId,AttemptId,RuntimeEpoch,StationId,
                ControllerEpoch,CycleSequence,EndpointBindingHash,ReasonCode,InspectionId,
                AdmissionContentHash,ExpectedPreviousCorrectionHash,OldValue,NewValue,
                ActorPrincipalId,ActorSessionId,AuthorizationRevision,StepUpGrantId,
                AuthorizationTarget,RecordedAtUtc,ContentHash,PayloadHash,Payload,AuditSequence,
                AuditHash,CommandAuditSequence,CommandAuditHash,AuthorizationAuditSequence,
                AuthorizationAuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), persisted.PreviousHash,
            persisted.EventId.ToString("D"), ((int)persisted.Kind).ToString(CultureInfo.InvariantCulture),
            persisted.CorrelationId.ToString("D"), persisted.AttemptId.ToString("D"),
            persisted.RuntimeEpoch.ToString("D"), persisted.StationId,
            persisted.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            persisted.CycleSequence.ToString(CultureInfo.InvariantCulture), persisted.EndpointBindingHash,
            persisted.ReasonCode, persisted.InspectionId?.ToString("D"), persisted.AdmissionContentHash,
            persisted.ExpectedPreviousCorrectionHash, persisted.OldValue, persisted.NewValue,
            persisted.ActorPrincipalId?.ToString("D"), persisted.ActorSessionId?.ToString("D"),
            persisted.AuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            persisted.StepUpGrantId?.ToString("D"), persisted.AuthorizationTarget,
            persisted.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            persisted.ContentHash, payloadHash, Convert.ToBase64String(payload),
            persisted.AuditSequence.ToString(CultureInfo.InvariantCulture), persisted.AuditHash,
            persisted.CommandAuditSequence.ToString(CultureInfo.InvariantCulture), persisted.CommandAuditHash,
            persisted.AuthorizationAuditSequence.ToString(CultureInfo.InvariantCulture), persisted.AuthorizationAuditHash);
        return persisted;
    }

    private static void RequireIndependentRejectionEvent(PartIdentityHistoryEvent candidate)
    {
        AuditChainDatabase.Require(candidate.Kind == PartIdentityHistoryEventKind.RejectedTrigger,
            "PartIdentityIndependentEventKindInvalid");
        AuditChainDatabase.Require(candidate.Evidence is null && candidate.RejectionContext is not null &&
            candidate.AuditSequence == 0 && candidate.AuditHash is null &&
            candidate.CommandAuditSequence == 0 && candidate.CommandAuditHash is null &&
            candidate.AuthorizationAuditSequence == 0 && candidate.AuthorizationAuditHash is null &&
            candidate.ActorPrincipalId is null && candidate.ActorSessionId is null &&
            candidate.StepUpGrantId is null && candidate.AuthorizationTarget is null &&
            candidate.AuthorizationRevision == 0,
            "PartIdentityIndependentEventAuthorizationReferenceInvalid");
    }

    /// <summary>
    /// Resolves the immutable production evidence used by a correction while
    /// the identity writer owns its SQLite transaction.  The correction
    /// command only names the original admission hash; it never supplies a
    /// replacement evidence payload.
    /// </summary>
    internal PartIdentityHistoryEvent BuildPartIdentityCorrectionCandidate(
        sqlite3 database, PartIdentityCorrectionWrite correction, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(correction);
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var rows = ReadProductionInspectionRows(database, options, deadline)
            .Where(value => value.Event.InspectionId == correction.Command.InspectionId)
            .OrderBy(value => value.Position).ToArray();
        var admission = rows.Where(value => value.Event.Kind == ProductionInspectionEventKind.Admitted)
            .Select(value => value.Event.Admission).SingleOrDefault();
        var core = rows.Where(value => value.Event.Kind == ProductionInspectionEventKind.CoreCommitted)
            .Select(value => value.Event.Core).SingleOrDefault();
        RequirePartIdentityCorrectionConflict(admission is not null && core is not null,
            "PartIdentityCorrectionProductionInspectionMissing");
        var admitted = admission!;
        var committedCore = core!;
        RequirePartIdentityCorrectionConflict(string.Equals(admitted.ContentHash,
                correction.Command.AdmissionContentHash, StringComparison.Ordinal) &&
            string.Equals(committedCore.Admission.ContentHash, admitted.ContentHash, StringComparison.Ordinal),
            "PartIdentityCorrectionAdmissionMismatch");
        var evidence = admitted.PartIdentityEvidence;
        RequirePartIdentityCorrectionConflict(evidence is not null,
            "PartIdentityCorrectionEvidenceMismatch");
        var acceptedEvidence = evidence ??
            throw new InvalidOperationException("PartIdentityCorrectionEvidenceMismatch");
        var partIdentityOptions = _options.PartIdentities ??
            throw new InvalidOperationException("PartIdentityConfigurationRequired");
        var prior = ReadPartIdentityRows(database, partIdentityOptions, deadline)
            .Select(value => value.Event)
            .Where(value => value.Kind == PartIdentityHistoryEventKind.Correction &&
                value.InspectionId == admitted.InspectionId)
            .OrderBy(value => value.Position).LastOrDefault();
        var expectedPrevious = prior?.ContentHash;
        var expectedOldValue = prior?.NewValue ?? acceptedEvidence.Value;
        RequirePartIdentityCorrectionConflict(correction.Command.ExpectedPreviousCorrectionHash == expectedPrevious &&
            string.Equals(correction.Command.PreviousValue, expectedOldValue, StringComparison.Ordinal),
            "PartIdentityCorrectionPreviousValueMismatch");
        RequirePartIdentityCorrectionConflict(committedCore.State == ProductionInspectionState.CoreCommitted,
            "PartIdentityCorrectionCoreNotCommitted");
        AuditChainDatabase.Require(correction.RuntimeEpoch != Guid.Empty &&
            correction.AttemptId != Guid.Empty &&
            correction.ActorPrincipalId != Guid.Empty && correction.ActorSessionId != Guid.Empty &&
            correction.StepUpGrantId != Guid.Empty && correction.AuthorizationRevision >= 0,
            "PartIdentityCorrectionAuthorizationInvalid");

        return new PartIdentityHistoryEvent(
            position: 1, previousHash: null, eventId: Guid.NewGuid(),
            kind: PartIdentityHistoryEventKind.Correction,
            correlationId: correction.Command.CorrelationId, attemptId: correction.AttemptId,
            runtimeEpoch: correction.RuntimeEpoch, stationId: admitted.StationId,
            controllerEpoch: admitted.ControllerCycle.ControllerEpoch,
            cycleSequence: admitted.ControllerCycle.CycleSequence,
            endpointBindingHash: admitted.EndpointBindingHash, evidence: acceptedEvidence,
            reasonCode: correction.Command.ReasonCode,
            inspectionId: admitted.InspectionId,
            admissionContentHash: admitted.ContentHash,
            expectedPreviousCorrectionHash: correction.Command.ExpectedPreviousCorrectionHash,
            oldValue: correction.Command.PreviousValue, newValue: correction.Command.CorrectedValue,
            actorPrincipalId: correction.ActorPrincipalId,
            actorSessionId: correction.ActorSessionId,
            authorizationRevision: correction.AuthorizationRevision,
            stepUpGrantId: correction.StepUpGrantId,
            authorizationTarget: correction.Command.AuthorizationTarget,
            recordedAtUtc: correction.AuthorizedAtUtc.ToUniversalTime());
    }

    /// <summary>
    /// Checks the immutable production source and the current correction
    /// predecessor before the identity writer appends any command or identity
    /// audit fact.  These are caller-visible conflicts, so they become an
    /// ordinary persisted rejection; structural/configuration failures remain
    /// exceptions and keep the writer fail-closed.
    /// </summary>
    internal string? TryPreparePartIdentityCorrectionRejection(
        sqlite3 database, PartIdentityCorrectionWrite correction, StoreDeadline deadline)
    {
        try
        {
            _ = BuildPartIdentityCorrectionCandidate(database, correction, deadline);
            return null;
        }
        catch (PartIdentityCorrectionConflictException exception)
        {
            return exception.Message is "PartIdentityCorrectionPreviousValueMismatch" or
                "PartIdentityCorrectionConflict"
                ? "PartIdentityCorrectionConflict"
                : exception.Message;
        }
    }

    private static void RequirePartIdentityCorrectionConflict(bool condition, string reason)
    {
        if (!condition) throw new PartIdentityCorrectionConflictException(reason);
    }

    private sealed class PartIdentityCorrectionConflictException : InvalidOperationException
    {
        internal PartIdentityCorrectionConflictException(string reason) : base(reason) { }
    }

    /// <summary>
    /// Rewrites an already authorized correction into an ordinary rejected
    /// identity transaction.  The commit guard has already been disposed by
    /// the caller, so the reserved StepUp grant is released and no PartIdentity
    /// ledger row is appended.
    /// </summary>
    internal static IdentityUpdate RejectPartIdentityCorrection(
        IdentityUpdate evaluated, string reason)
    {
        AuditChainDatabase.Require(evaluated.Result is PartIdentityCorrectionResult,
            "PartIdentityCorrectionResultRequired");
        AuditChainDatabase.Require(evaluated.CommandFacts is { Count: > 0 },
            "PartIdentityCorrectionCommandFactRequired");
        AuditChainDatabase.Require(evaluated.Events.Any(value =>
            value.Kind == IdentityEventKind.PartIdentityCorrectionAuthorized),
            "PartIdentityCorrectionAuthorizationEventRequired");

        var original = (PartIdentityCorrectionResult)evaluated.Result;
        var rejectedOutcome = new RuntimeCommandOutcome(original.Outcome.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Persisted,
            original.Outcome.AttemptId);
        var events = evaluated.Events.Select(value =>
            value.Kind == IdentityEventKind.PartIdentityCorrectionAuthorized
                ? value with { Kind = IdentityEventKind.ManagementRejected, ReasonCode = reason }
                : value).ToArray();
        var facts = evaluated.CommandFacts!.Select(value =>
            value.Phase == CommandAuditPhase.Outcome
                ? value with { Disposition = CommandDisposition.Rejected, ReasonCode = reason }
                : value).ToArray();
        return evaluated with
        {
            Result = new PartIdentityCorrectionResult(rejectedOutcome),
            Events = events,
            CommandFacts = facts,
            CommitGuard = null,
            PartIdentity = null
        };
    }

    internal static void ValidatePartIdentityHistory(IReadOnlyList<PartIdentityStoredRow> rows)
    {
        string? previous = null;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position > 0 && row.Event.Position == row.Position &&
                row.Event.PreviousHash == previous, "PartIdentityHistoryChainMismatch");
            AuditChainDatabase.Require(PartIdentityStorageCodec.Hash(row.Payload) == row.PayloadHash,
                "PartIdentityPayloadHashMismatch");
            var decoded = PartIdentityStorageCodec.Decode(row.Payload);
            AuditChainDatabase.Require(decoded.ContentHash == row.Event.ContentHash &&
                decoded.AuditSequence == row.Event.AuditSequence && decoded.AuditHash == row.Event.AuditHash,
                "PartIdentityPayloadBindingMismatch");
            previous = row.Event.ContentHash;
        }
        var latestByInspection = rows.Select(value => value.Event)
            .Where(value => value.Kind == PartIdentityHistoryEventKind.Correction && value.InspectionId is not null)
            .GroupBy(value => value.InspectionId!.Value);
        foreach (var group in latestByInspection)
        {
            string? correctionHash = null;
            string? previousValue = null;
            foreach (var value in group.OrderBy(value => value.Position))
            {
                AuditChainDatabase.Require(value.ExpectedPreviousCorrectionHash == correctionHash,
                    "PartIdentityCorrectionChainMismatch");
                AuditChainDatabase.Require(string.Equals(value.OldValue,
                    correctionHash is null ? value.Evidence?.Value : previousValue,
                    StringComparison.Ordinal), "PartIdentityCorrectionPreviousValueMismatch");
                correctionHash = value.ContentHash;
                previousValue = value.NewValue;
            }
        }
    }

    internal static void ValidatePartIdentityTransition(
        IReadOnlyList<PartIdentityHistoryEvent> priorEvents, PartIdentityHistoryEvent candidate)
    {
        if (candidate.Kind != PartIdentityHistoryEventKind.Correction || candidate.InspectionId is null)
            return;
        var previous = priorEvents.Where(value => value.Kind == PartIdentityHistoryEventKind.Correction &&
                value.InspectionId == candidate.InspectionId).OrderByDescending(value => value.Position)
            .FirstOrDefault();
        var expected = previous?.ContentHash;
        AuditChainDatabase.Require(candidate.ExpectedPreviousCorrectionHash == expected,
            "PartIdentityCorrectionConflict");
        if (previous is not null)
            AuditChainDatabase.Require(previous.NewValue == candidate.OldValue,
                "PartIdentityCorrectionPreviousValueMismatch");
    }

    private static PartIdentityHistoryEvent Rebuild(PartIdentityHistoryEvent value, long position,
        string? previousHash, long auditSequence, string? auditHash) => new(position, previousHash,
        value.EventId, value.Kind, value.CorrelationId, value.AttemptId, value.RuntimeEpoch,
        value.StationId, value.ControllerEpoch, value.CycleSequence, value.EndpointBindingHash,
        value.Evidence, value.ReasonCode, value.InspectionId, value.AdmissionContentHash,
        value.ExpectedPreviousCorrectionHash, value.OldValue, value.NewValue, value.ActorPrincipalId,
        value.ActorSessionId, value.AuthorizationRevision, value.StepUpGrantId, value.AuthorizationTarget,
        value.RecordedAtUtc, auditSequence, auditHash, null, value.CommandAuditSequence,
        value.CommandAuditHash, value.AuthorizationAuditSequence, value.AuthorizationAuditHash,
        value.RejectionContext);

    private static PartIdentityHistoryEvent RebuildReferences(PartIdentityHistoryEvent value,
        long position, string? previousHash, long auditSequence, string? auditHash,
        long commandAuditSequence, string? commandAuditHash,
        long authorizationAuditSequence, string? authorizationAuditHash) => new(position, previousHash,
        value.EventId, value.Kind, value.CorrelationId, value.AttemptId, value.RuntimeEpoch,
        value.StationId, value.ControllerEpoch, value.CycleSequence, value.EndpointBindingHash,
        value.Evidence, value.ReasonCode, value.InspectionId, value.AdmissionContentHash,
        value.ExpectedPreviousCorrectionHash, value.OldValue, value.NewValue, value.ActorPrincipalId,
        value.ActorSessionId, value.AuthorizationRevision, value.StepUpGrantId, value.AuthorizationTarget,
        value.RecordedAtUtc, auditSequence, auditHash, null, commandAuditSequence, commandAuditHash,
        authorizationAuditSequence, authorizationAuditHash, value.RejectionContext);

    private static PartIdentityStoredRow ReadPartIdentityRow(sqlite3_stmt statement,
        PartIdentityStoreOptions options)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var previous = SqliteNative.ColumnText(statement, 1);
        var encoded = SqliteNative.ColumnText(statement, 2) ?? throw new InvalidOperationException("PartIdentityPayloadInvalid");
        var payloadHash = SqliteNative.ColumnText(statement, 3) ?? throw new InvalidOperationException("PartIdentityPayloadInvalid");
        AuditChainDatabase.Require(encoded.Length <= options.MaximumPayloadBytes * 2 &&
            AuditCanonical.IsHash(payloadHash), "PartIdentityPayloadInvalid");
        var payload = Convert.FromBase64String(encoded);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes &&
            PartIdentityStorageCodec.Hash(payload) == payloadHash, "PartIdentityPayloadInvalid");
        var value = PartIdentityStorageCodec.Decode(payload);
        return new(position, previous, value, payload, payloadHash);
    }
}
