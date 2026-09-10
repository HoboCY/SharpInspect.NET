using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SQLitePCL;
using ProductionAdmissionEventType = SharpInspect.Abstractions.ProductionAdmissionEventKind;

namespace SharpInspect.Runtime.Storage;

/// <summary>Schema-22 storage and verified projections for production admission reports.</summary>
internal sealed partial class SqliteCommandStore
{
    internal const string ProductionAdmissionStoreActivatedKind = "ProductionAdmissionStoreActivated";
    internal const string ProductionAdmissionEventKind = "ProductionAdmissionEvent";

    internal const string ProductionAdmissionSchemaSql = @"
        CREATE TABLE production_admission_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE production_admission_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            Kind INTEGER NOT NULL CHECK(Kind IN (1,2,3,4)),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            CorrelationId TEXT NOT NULL CHECK(length(CorrelationId)=36),
            AttemptId TEXT NOT NULL CHECK(length(AttemptId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            AdmissionGeneration INTEGER NOT NULL CHECK(AdmissionGeneration>=0),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            ActorSessionId TEXT NOT NULL CHECK(length(ActorSessionId)=36),
            ActorAuthorizationRevision INTEGER NOT NULL CHECK(ActorAuthorizationRevision>=0),
            AuthorizationPolicyId TEXT NOT NULL,
            AuthorizationPolicyVersion TEXT NOT NULL,
            AuthorizationPolicyHash TEXT NOT NULL CHECK(length(AuthorizationPolicyHash)=64),
            StepUpGrantId TEXT NULL CHECK(StepUpGrantId IS NULL OR length(StepUpGrantId)=36),
            ExpectedDurableHeads TEXT NOT NULL,
            CurrentDurableHeads TEXT NOT NULL,
            AuthorizationTarget TEXT NOT NULL CHECK(length(AuthorizationTarget)=64),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0),
            ReportContentHash TEXT NOT NULL CHECK(length(ReportContentHash)=64),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            UNIQUE(CorrelationId,AttemptId,Kind));
        CREATE INDEX ix_production_admission_events_correlation
            ON production_admission_events(CorrelationId,Position);
        CREATE INDEX ix_production_admission_events_attempt
            ON production_admission_events(AttemptId,Position);
        CREATE INDEX ix_production_admission_events_audit
            ON production_admission_events(AuditSequence);
        CREATE TRIGGER production_admission_config_immutable_update BEFORE UPDATE
            ON production_admission_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionAdmissionConfiguration');
        END;
        CREATE TRIGGER production_admission_config_immutable_delete BEFORE DELETE
            ON production_admission_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionAdmissionConfiguration');
        END;
        CREATE TRIGGER production_admission_event_immutable_update BEFORE UPDATE
            ON production_admission_events BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionAdmissionEvent');
        END;
        CREATE TRIGGER production_admission_event_immutable_delete BEFORE DELETE
            ON production_admission_events BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionAdmissionEvent');
        END;";

    internal static void InitializeProductionAdmissionSchema(sqlite3 database,
        ProductionAdmissionStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, ProductionAdmissionSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO production_admission_store_config
                (Id,FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            ProductionAdmissionStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendProductionAdmissionStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredProductionAdmission(sqlite3 database,
        ProductionAdmissionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM production_admission_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEntries: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                BindingHash: SqliteNative.ColumnText(statement, 4) ?? string.Empty)).ToArray();
        AuditChainDatabase.Require(configured.Length == 1 &&
            configured[0].FormatVersion == ProductionAdmissionStoreOptions.FormatVersion &&
            configured[0].MaximumEntries == options.MaximumEntries &&
            configured[0].MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured[0].MaximumTotalBytes == options.MaximumTotalBytes &&
            configured[0].BindingHash == options.BindingHash,
            "ProductionAdmissionConfigurationMismatch");
    }

    internal static void VerifyProductionAdmissionActivationPayload(sqlite3 database,
        byte[] payload, ProductionAdmissionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        options.Validate();
        RequireConfiguredProductionAdmission(database, options, deadline);
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "ProductionAdmissionActivationBindingMismatch");
    }

    internal sealed record ProductionAdmissionStoredRow(long Position, string? PreviousHash,
        byte[] Payload, ProductionAdmissionHistoryEvent Event);

    /// <summary>
    /// Reads and cross-checks every scalar projection against the canonical
    /// event payload. This method is shared by the writer's recheck and the
    /// read-only history query so a replaced projection cannot be trusted.
    /// </summary>
    internal static List<ProductionAdmissionStoredRow> ReadProductionAdmissionRows(
        sqlite3 database, ProductionAdmissionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RequireConfiguredProductionAdmission(database, options, deadline);
        var decodedPayloadTotal = 0L;
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,PreviousHash,CorrelationId,AttemptId,RuntimeEpoch,
                AdmissionGeneration,ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,
                AuthorizationPolicyId,AuthorizationPolicyVersion,AuthorizationPolicyHash,StepUpGrantId,
                ExpectedDurableHeads,CurrentDurableHeads,AuthorizationTarget,ReasonCode,
                ReportContentHash,CommandAuditSequence,CommandAuditHash,AuthorizationAuditSequence,
                AuthorizationAuditHash,AuditSequence,AuditHash,PayloadHash,ContentHash,Payload
            FROM production_admission_events ORDER BY Position LIMIT ?;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var encoded = SqliteNative.ColumnText(statement, 27);
            AuditChainDatabase.Require(encoded is { Length: > 0 } &&
                encoded.Length <= checked(options.MaximumPayloadBytes * 2L),
                "ProductionAdmissionPayloadCapacityExceeded");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded!);
                AuditChainDatabase.Require(Convert.ToBase64String(payload) == encoded,
                    "ProductionAdmissionPayloadCanonicalMismatch");
            }
            catch (FormatException exception)
            { throw new InvalidOperationException("ProductionAdmissionPayloadInvalid", exception); }
            AuditChainDatabase.Require(payload.Length is > 0 && payload.Length <= options.MaximumPayloadBytes,
                "ProductionAdmissionPayloadCapacityExceeded");
            decodedPayloadTotal = checked(decodedPayloadTotal + payload.Length);
            AuditChainDatabase.Require(decodedPayloadTotal <= options.MaximumTotalBytes,
                "ProductionAdmissionTotalCapacityExceeded");
            var decoded = ProductionAdmissionStorageCodec.Decode(payload).Event;
            AuditChainDatabase.Require(decoded.Position == position &&
                (int)decoded.Kind == SqliteNative.ColumnInt64(statement, 1) &&
                decoded.CorrelationId == ParseProductionGuid(SqliteNative.ColumnText(statement, 3)) &&
                decoded.AttemptId == ParseProductionGuid(SqliteNative.ColumnText(statement, 4)) &&
                decoded.RuntimeEpoch == ParseProductionGuid(SqliteNative.ColumnText(statement, 5)) &&
                decoded.AdmissionGeneration == SqliteNative.ColumnInt64(statement, 6) &&
                decoded.ActorPrincipalId == ParseProductionGuid(SqliteNative.ColumnText(statement, 7)) &&
                decoded.ActorSessionId == ParseProductionGuid(SqliteNative.ColumnText(statement, 8)) &&
                decoded.ActorAuthorizationRevision == SqliteNative.ColumnInt64(statement, 9) &&
                decoded.AuthorizationPolicy.Id == SqliteNative.ColumnText(statement, 10) &&
                decoded.AuthorizationPolicy.Version == SqliteNative.ColumnText(statement, 11) &&
                decoded.AuthorizationPolicy.ContentHash == SqliteNative.ColumnText(statement, 12) &&
                decoded.StepUpGrantId == ParseNullableProductionGuid(SqliteNative.ColumnText(statement, 13)) &&
                EncodeHeads(decoded.ExpectedDurableHeads) == SqliteNative.ColumnText(statement, 14) &&
                EncodeHeads(decoded.CurrentDurableHeads) == SqliteNative.ColumnText(statement, 15) &&
                decoded.AuthorizationTarget == SqliteNative.ColumnText(statement, 16) &&
                decoded.ReasonCode == SqliteNative.ColumnText(statement, 17) &&
                decoded.Report.ContentHash == SqliteNative.ColumnText(statement, 18) &&
                decoded.CommandAuditSequence == SqliteNative.ColumnInt64Nullable(statement, 19) &&
                decoded.CommandAuditHash == SqliteNative.ColumnText(statement, 20) &&
                decoded.AuthorizationAuditSequence == SqliteNative.ColumnInt64Nullable(statement, 21) &&
                decoded.AuthorizationAuditHash == SqliteNative.ColumnText(statement, 22) &&
                decoded.AuditSequence == SqliteNative.ColumnInt64(statement, 23) &&
                decoded.AuditHash == SqliteNative.ColumnText(statement, 24) &&
                decoded.PayloadHash == SqliteNative.ColumnText(statement, 25) &&
                decoded.ContentHash == SqliteNative.ColumnText(statement, 26),
                "ProductionAdmissionEventColumnBindingMismatch");
            AuditChainDatabase.Require(decoded.PayloadHash == ProductionAdmissionStorageCodec.PayloadHash(
                ProductionAdmissionStorageCodec.EncodeAuditBinding(decoded)),
                "ProductionAdmissionPayloadHashMismatch");
            return new ProductionAdmissionStoredRow(position, SqliteNative.ColumnText(statement, 2),
                payload, decoded);
        }, Number(options.MaximumEntries + 1L)).ToList();
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries, "ProductionAdmissionEntryCapacityExceeded");
        long total = 0;
        string? previous = null;
        var attemptStates = new Dictionary<Guid, ProductionAdmissionEventType>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            AuditChainDatabase.Require(row.Position == index + 1L && row.PreviousHash == previous &&
                row.Event.AuditSequence > 0 && row.Event.AuditHash is { Length: 64 },
                "ProductionAdmissionLedgerOrderInvalid");
            if (attemptStates.TryGetValue(row.Event.AttemptId, out var priorState))
            {
                AuditChainDatabase.Require(priorState == ProductionAdmissionEventType.Admitted &&
                    row.Event.Kind is ProductionAdmissionEventType.Completed or ProductionAdmissionEventType.Failed,
                    "ProductionAdmissionEventSequenceInvalid");
            }
            else
            {
                AuditChainDatabase.Require(row.Event.Kind is ProductionAdmissionEventType.Admitted or
                    ProductionAdmissionEventType.Rejected, "ProductionAdmissionEventSequenceInvalid");
            }
            attemptStates[row.Event.AttemptId] = row.Event.Kind;
            total = checked(total + row.Payload.Length);
            AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
                "ProductionAdmissionTotalCapacityExceeded");
            previous = row.Event.AuditHash;
        }
        return rows;
    }

    internal static void VerifyProductionAdmissionAuditPayload(sqlite3 database, long position,
        byte[] auditPayload, ProductionAdmissionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(auditPayload);
        var row = ReadProductionAdmissionRows(database, options, deadline)
            .SingleOrDefault(value => value.Position == position);
        AuditChainDatabase.Require(row is not null &&
            ProductionAdmissionStorageCodec.EncodeAuditBinding(row!.Event).AsSpan().SequenceEqual(auditPayload),
            "ProductionAdmissionAuditPayloadMismatch");
    }

    /// <summary>Returns central entries reserved by admitted, not-yet-terminal attempts.</summary>
    internal static long ReadProductionAdmissionAuditReserve(sqlite3 database, StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "production_admission_events", deadline)) return 0;
        var rows = AuditChainDatabase.Read(database, @"
            SELECT AttemptId,Kind FROM production_admission_events ORDER BY Position;", deadline,
            statement => (Attempt: ParseProductionGuid(SqliteNative.ColumnText(statement, 0)),
                Kind: (ProductionAdmissionEventType)SqliteNative.ColumnInt64(statement, 1)));
        var latest = rows.GroupBy(value => value.Attempt).Select(group => group.Last());
        return checked(latest.Count(value => value.Kind == ProductionAdmissionEventType.Admitted) *
            ProductionAdmissionStoreOptions.AuditEntriesPerEvent);
    }

    /// <summary>
    /// Full linear ledger replay. The central verifier separately checks the
    /// signature chain; this pass checks the report/event state machine and
    /// every immutable foreign reference.
    /// </summary>
    internal static void VerifyAllProductionAdmissions(sqlite3 database,
        ProductionAdmissionStoreOptions options, StoreDeadline deadline)
    {
        var rows = ReadProductionAdmissionRows(database, options, deadline);
        var byAttempt = new Dictionary<Guid, ProductionAdmissionHistoryEvent>();
        var byCorrelation = new Dictionary<Guid, Guid>();
        var stationId = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1;", deadline) ??
            throw new InvalidOperationException("ProductionAdmissionStationMissing");
        foreach (var row in rows)
        {
            SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            var value = row.Event;
            AuditChainDatabase.Require(value.Report.RuntimeEpoch == value.RuntimeEpoch &&
                value.Report.AdmissionGeneration == value.AdmissionGeneration &&
                value.CommandAuditSequence < value.AuditSequence &&
                value.AuthorizationAuditSequence < value.AuditSequence,
                "ProductionAdmissionReportBindingMismatch");
            if (byCorrelation.TryGetValue(value.CorrelationId, out var priorAttempt))
            {
                // A duplicate request may be durably recorded as Rejected with
                // a fresh attempt.  It must never create another admission or
                // terminal continuation for the correlation's original attempt.
                if (priorAttempt != value.AttemptId)
                    AuditChainDatabase.Require(value.Kind == ProductionAdmissionEventType.Rejected,
                        "ProductionAdmissionCorrelationReused");
            }
            else byCorrelation.Add(value.CorrelationId, value.AttemptId);
            var terminal = value.Kind is ProductionAdmissionEventType.Completed or
                ProductionAdmissionEventType.Failed;
            var expectedAggregateSequence = terminal ? 2 : 1;
            var command = AuditChainDatabase.Read(database, @"
                SELECT e.Sequence,e.Hash,f.EventId,f.AttemptId,f.CorrelationId,f.RuntimeEpoch,
                    f.CommandKind,f.Source,f.Phase,f.Disposition,f.OccurredAtUtc,
                    f.AuthenticatedHumanPrincipalId,f.ClaimedPrincipalId,f.ClaimedSessionId,
                    f.ClaimedStepUpGrantId,f.ReasonCode
                FROM audit_entries e JOIN command_facts f ON f.Position=e.FactPosition
                WHERE e.Kind='CommandFact' AND f.AttemptId=? AND f.AggregateSequence=? LIMIT 2;", deadline,
                statement => new ProductionCommandReference(
                    SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                    ParseProductionGuid(SqliteNative.ColumnText(statement, 2)),
                    ParseProductionGuid(SqliteNative.ColumnText(statement, 3)),
                     ParseProductionGuid(SqliteNative.ColumnText(statement, 4)),
                     ParseProductionGuid(SqliteNative.ColumnText(statement, 5)),
                     (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 6),
                     ParseNullableProductionCommandSource(SqliteNative.ColumnText(statement, 7)),
                     (CommandAuditPhase)SqliteNative.ColumnInt64(statement, 8),
                     SqliteNative.ColumnInt64Nullable(statement, 9) is { } disposition
                         ? (CommandDisposition?)disposition : null,
                     SqliteNative.ColumnText(statement, 10), SqliteNative.ColumnText(statement, 11),
                     ParseNullableProductionGuid(SqliteNative.ColumnText(statement, 12)),
                     ParseNullableProductionGuid(SqliteNative.ColumnText(statement, 13)),
                     ParseNullableProductionGuid(SqliteNative.ColumnText(statement, 14)),
                     SqliteNative.ColumnText(statement, 15)),
                value.AttemptId.ToString("D"), expectedAggregateSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            var expectedPhase = terminal
                ? value.Kind == ProductionAdmissionEventType.Completed
                    ? CommandAuditPhase.Completed : CommandAuditPhase.Failed
                : CommandAuditPhase.Outcome;
            CommandDisposition? expectedDisposition = terminal ? null : value.Kind == ProductionAdmissionEventType.Rejected
                ? CommandDisposition.Rejected : CommandDisposition.Accepted;
            AuditChainDatabase.Require(command is not null &&
                command.Sequence == value.CommandAuditSequence &&
                command.Hash == value.CommandAuditHash && command.AttemptId == value.AttemptId &&
                command.CorrelationId == value.CorrelationId && command.RuntimeEpoch == value.RuntimeEpoch &&
                command.CommandKind == AuditedCommandKind.ArmProduction &&
                command.Source is { } source &&
                (value.Kind == ProductionAdmissionEventType.Rejected || source == CommandSource.PhysicalConsole) &&
                command.Phase == expectedPhase && command.Disposition == expectedDisposition &&
                command.AuthenticatedPrincipal == value.ActorPrincipalId.ToString("D") &&
                command.ClaimedPrincipal == value.ActorPrincipalId &&
                command.ClaimedSession == value.ActorSessionId && command.ClaimedStepUp == value.StepUpGrantId &&
                command.ReasonCode == value.ReasonCode &&
                command.OccurredAtUtc is not null && command.OccurredAtUtc.Length > 0,
                "ProductionAdmissionCommandAuditMismatch");
            if (value.AuthorizationAuditSequence is not { } authorizationAuditSequence || authorizationAuditSequence <= 0)
                throw new InvalidOperationException("ProductionAdmissionAuthorizationAuditMismatch");
            var identity = AuditChainDatabase.Read(database, @"
                SELECT Sequence,Hash,IdentityPosition,Payload
                FROM audit_entries
                WHERE Kind='IdentityEvent' AND Sequence=? LIMIT 2;", deadline,
                statement => new ProductionIdentityReference(
                    SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                    SqliteNative.ColumnInt64Nullable(statement, 2), SqliteNative.ColumnText(statement, 3) ?? string.Empty),
                authorizationAuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(identity is not null && identity.Sequence == value.AuthorizationAuditSequence &&
                identity.Hash == value.AuthorizationAuditHash && identity.IdentityPosition is > 0 &&
                identity.Payload is { Length: > 0 }, "ProductionAdmissionAuthorizationAuditMismatch");
            byte[] identityPayload;
            try
            {
                identityPayload = Convert.FromBase64String(identity!.Payload);
            }
            catch (FormatException exception)
            { throw new InvalidOperationException("ProductionAdmissionAuthorizationAuditMismatch", exception); }
            IdentityAuditEvent.VerifyPayload(identityPayload, identity.IdentityPosition!.Value,
                stationId, ProductionAdmissionStoreOptions.SchemaVersion);
            AuditChainDatabase.Require(IdentityAuditEvent.MatchesProductionAdmission(identityPayload, value,
                stationId), "ProductionAdmissionAuthorizationAuditMismatch");
            var identityEventId = IdentityAuditEvent.DecodeEventId(identityPayload);
            AuditChainDatabase.Require(identityEventId is { Length: 36 } &&
                Guid.TryParseExact(identityEventId, "D", out var parsedIdentityEventId) &&
                parsedIdentityEventId == command!.EventId,
                "ProductionAdmissionAuthorizationAuditMismatch");
            AuditChainDatabase.Require(value.AuthorizationTarget ==
                ProductionAdmissionAuthorizationTarget(stationId, value.Report, value.CorrelationId,
                    value.AttemptId, value.ExpectedDurableHeads),
                "ProductionAdmissionAuthorizationTargetMismatch");
            switch (value.Kind)
            {
                case ProductionAdmissionEventType.Admitted:
                    AuditChainDatabase.Require(!byAttempt.ContainsKey(value.AttemptId),
                        "ProductionAdmissionDuplicateAdmission");
                    AuditChainDatabase.Require(value.Report.CanArm &&
                        value.ExpectedDurableHeads.Count == value.CurrentDurableHeads.Count &&
                        value.ExpectedDurableHeads.All(pair => value.CurrentDurableHeads.TryGetValue(pair.Key,
                            out var current) && current == pair.Value),
                        "ProductionAdmissionAdmittedReportInvalid");
                    break;
                case ProductionAdmissionEventType.Rejected:
                    AuditChainDatabase.Require(!byAttempt.ContainsKey(value.AttemptId),
                        "ProductionAdmissionDuplicateRejection");
                    break;
                case ProductionAdmissionEventType.Completed:
                case ProductionAdmissionEventType.Failed:
                    AuditChainDatabase.Require(byAttempt.TryGetValue(value.AttemptId, out var admission) &&
                        admission.Kind == ProductionAdmissionEventType.Admitted &&
                        admission.CorrelationId == value.CorrelationId &&
                        admission.RuntimeEpoch == value.RuntimeEpoch &&
                        admission.AdmissionGeneration == value.AdmissionGeneration &&
                        admission.ActorPrincipalId == value.ActorPrincipalId &&
                        admission.ActorSessionId == value.ActorSessionId &&
                        admission.ActorAuthorizationRevision == value.ActorAuthorizationRevision &&
                        admission.AuthorizationPolicy == value.AuthorizationPolicy &&
                        admission.StepUpGrantId == value.StepUpGrantId &&
                        admission.Report.ContentHash == value.Report.ContentHash &&
                        admission.ExpectedDurableHeads.SequenceEqual(value.ExpectedDurableHeads) &&
                        admission.AuthorizationTarget == value.AuthorizationTarget,
                        "ProductionAdmissionTerminalWithoutAdmission");
                    if (value.Kind == ProductionAdmissionEventType.Completed &&
                        value.ReasonCode == "ProductionAdmissionFinalized")
                    {
                        // A successful finalization is only valid when the
                        // durable-head snapshot carried by the terminal row
                        // is exactly the snapshot admitted by the Arm.  The
                        // writer converts a changed snapshot to Failed before
                        // this verifier can observe it.
                        AuditChainDatabase.Require(ProductionAdmissionHeadsEqual(
                            value.ExpectedDurableHeads, value.CurrentDurableHeads),
                            "ProductionAdmissionFinalizedHeadsMismatch");
                    }
                    break;
            }
            byAttempt[value.AttemptId] = value;
        }
        // A rejected attempt is final. An admitted attempt may remain pending
        // after an unclean shutdown, but it cannot have more than one terminal.
        var terminalAttempts = rows.Where(row => row.Event.Kind is ProductionAdmissionEventType.Completed or ProductionAdmissionEventType.Failed)
            .GroupBy(row => row.Event.AttemptId);
        AuditChainDatabase.Require(terminalAttempts.All(group => group.Count() == 1),
            "ProductionAdmissionTerminalDuplicate");
    }

    private static string EncodeHeads(IReadOnlyDictionary<string, string> heads)
    {
        var fields = new List<string?> { "ProductionAdmissionHeadsV1" };
        foreach (var pair in heads) fields.AddRange(new[] { pair.Key, pair.Value });
        return Convert.ToBase64String(AuditCanonical.Encode("ProductionAdmissionHeadsV1", fields.Skip(1).ToArray()));
    }

    private static Guid ParseProductionGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty ? parsed :
        throw new InvalidOperationException("ProductionAdmissionGuidInvalid");

    private static Guid? ParseNullableProductionGuid(string? value) => value is null ? null : ParseProductionGuid(value);

    private static CommandSource? ParseNullableProductionCommandSource(string? value) => value is null
        ? null
        : Enum.TryParse<CommandSource>(value, ignoreCase: false, out var parsed) &&
            Enum.IsDefined(typeof(CommandSource), parsed) ? parsed :
            throw new InvalidOperationException("ProductionAdmissionCommandSourceInvalid");

    private sealed record ProductionCommandReference(long Sequence, string Hash, Guid EventId,
        Guid AttemptId, Guid CorrelationId, Guid RuntimeEpoch, AuditedCommandKind CommandKind,
        CommandSource? Source, CommandAuditPhase Phase, CommandDisposition? Disposition, string? OccurredAtUtc,
        string? AuthenticatedPrincipal, Guid? ClaimedPrincipal, Guid? ClaimedSession,
        Guid? ClaimedStepUp, string? ReasonCode);

    private sealed record ProductionIdentityReference(long Sequence, string Hash,
        long? IdentityPosition, string Payload);
}
