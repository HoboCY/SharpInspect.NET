using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The schema-40 immutable diagnostic-operation ledger: one configuration row, one immutable
/// fact row per phase and one signed central-audit metadata entry per fact. The stored bytes
/// carry the complete typed fact, never an opaque hash alone.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string DiagnosticSupportSchemaSql = @"
        CREATE TABLE diagnostic_support_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            Payload TEXT NOT NULL CHECK(length(CAST(Payload AS BLOB)) BETWEEN 2 AND 65536),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE diagnostic_operation_facts(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            OperationId TEXT NOT NULL CHECK(length(OperationId)=36),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 2),
            AggregateSequence INTEGER NOT NULL CHECK(AggregateSequence BETWEEN 1 AND 4),
            Phase INTEGER NOT NULL CHECK(Phase BETWEEN 1 AND 5),
            Payload TEXT NOT NULL CHECK(length(CAST(Payload AS BLOB)) BETWEEN 2 AND 65536),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>1),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            UNIQUE(OperationId,AggregateSequence));
        CREATE INDEX ix_diagnostic_operation_phase ON diagnostic_operation_facts(OperationId,Phase);
        CREATE INDEX ix_diagnostic_operation_kind ON diagnostic_operation_facts(Kind,Position);
        CREATE TRIGGER diagnostic_support_config_no_update BEFORE UPDATE ON diagnostic_support_config
            BEGIN SELECT RAISE(ABORT,'ImmutableDiagnosticSupportConfig'); END;
        CREATE TRIGGER diagnostic_support_config_no_delete BEFORE DELETE ON diagnostic_support_config
            BEGIN SELECT RAISE(ABORT,'ImmutableDiagnosticSupportConfig'); END;
        CREATE TRIGGER diagnostic_operation_fact_no_update BEFORE UPDATE ON diagnostic_operation_facts
            BEGIN SELECT RAISE(ABORT,'ImmutableDiagnosticOperationFact'); END;
        CREATE TRIGGER diagnostic_operation_fact_no_delete BEFORE DELETE ON diagnostic_operation_facts
            BEGIN SELECT RAISE(ABORT,'ImmutableDiagnosticOperationFact'); END;
        CREATE TRIGGER diagnostic_operation_fact_contiguous BEFORE INSERT ON diagnostic_operation_facts
            BEGIN SELECT CASE WHEN NEW.Position<>COALESCE((SELECT MAX(Position) FROM diagnostic_operation_facts),0)+1
                THEN RAISE(ABORT,'DiagnosticSupportPositionGap') END; END;";

    internal static DiagnosticSupportConfiguration DiagnosticSupportConfigurationFor(ProductionStoreOptions store)
    {
        var option = store.DiagnosticSupport ?? throw new InvalidOperationException(
            "DiagnosticSupportConfigurationRequired");
        option.ValidateProfile(store);
        var policy = option.Policy;
        var logging = store.LoggingDiagnostics!;
        return new(option.BindingHash, policy.ContentHash, policy.Id, policy.Version, policy.ApprovalReference,
            option.SupportRootBindingHash, logging.Policy.ContentHash, store.TraceStoragePolicies!.BindingHash,
            RetentionConfigurationFor(store).BindingHash, policy.MaximumScope.Ticks, policy.MaximumSourceRecords,
            policy.MaximumSourceBytes, policy.MaximumBundleBytes, policy.ExportTimeout.Ticks,
            policy.AuthorizationCheckInterval.Ticks, policy.Retention.Ticks, policy.MaximumOperationFacts,
            policy.MaximumAuditPayloadBytes, policy.MaximumAuditBytes, option.MaximumOperations,
            option.MaximumTotalBytes);
    }

    internal static void InitializeDiagnosticSupportTables(sqlite3 database, ProductionStoreOptions store,
        StoreDeadline deadline)
    {
        var configuration = DiagnosticSupportConfigurationFor(store);
        var bytes = DiagnosticOperationStorageCodec.EncodeConfiguration(configuration);
        SqliteNative.Execute(database, DiagnosticSupportSchemaSql, deadline);
        AuditChainDatabase.Execute(database,
            "INSERT INTO diagnostic_support_config(Id,FormatVersion,Payload,BindingHash) VALUES(1,1,?,?);",
            deadline, Encoding.UTF8.GetString(bytes), configuration.BindingHash);
    }

    internal static DiagnosticSupportConfiguration ReadDiagnosticSupportConfiguration(sqlite3 database,
        StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database,
            "SELECT FormatVersion,Payload,BindingHash FROM diagnostic_support_config LIMIT 2;", deadline, statement =>
        {
            DiagnosticOperationStorageCodec.Require(SqliteNative.ColumnInt64(statement, 0) == 1,
                "ConfigurationVersionInvalid");
            var bytes = Encoding.UTF8.GetBytes(SqliteNative.ColumnText(statement, 1) ?? string.Empty);
            var value = DiagnosticOperationStorageCodec.DecodeConfiguration(bytes);
            DiagnosticOperationStorageCodec.Require(value.BindingHash == SqliteNative.ColumnText(statement, 2),
                "ConfigurationHashMismatch");
            return value;
        });
        DiagnosticOperationStorageCodec.Require(rows.Count == 1, "ConfigurationMissing");
        return rows[0];
    }

    internal static void RequireConfiguredDiagnosticSupport(sqlite3 database, ProductionStoreOptions store,
        StoreDeadline deadline)
    {
        var version = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
        if (store.DiagnosticSupport is null)
        {
            DiagnosticOperationStorageCodec.Require(version != DiagnosticSupportStoreOptions.SchemaVersion,
                "ConfigurationRequired");
            return;
        }
        DiagnosticOperationStorageCodec.Require(version == DiagnosticSupportStoreOptions.SchemaVersion,
            "GovernedMigrationRequired");
        DiagnosticOperationStorageCodec.Require(
            ReadDiagnosticSupportConfiguration(database, deadline).BindingHash ==
            DiagnosticSupportConfigurationFor(store).BindingHash, "ConfigurationMismatch");
    }

    internal static IReadOnlyList<DiagnosticOperationStoredRow> ReadDiagnosticOperationRows(sqlite3 database,
        DiagnosticSupportConfiguration configuration, StoreDeadline deadline)
    {
        var count = AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM diagnostic_operation_facts;", deadline);
        var bytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0) FROM diagnostic_operation_facts;", deadline);
        DiagnosticOperationStorageCodec.Require(count <= Math.Min(configuration.MaximumOperations, configuration.MaximumOperationFacts),
            "OperationCapacityExceeded");
        DiagnosticOperationStorageCodec.Require(bytes <= Math.Min(configuration.MaximumTotalBytes, configuration.MaximumAuditBytes), "TotalCapacityExceeded");
        var rows = AuditChainDatabase.Read(database, @"SELECT Position,EventId,OperationId,Kind,AggregateSequence,
            Phase,Payload,ContentHash,AuditSequence,AuditHash FROM diagnostic_operation_facts ORDER BY Position;",
            deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var payloadBytes = Encoding.UTF8.GetBytes(SqliteNative.ColumnText(statement, 6) ?? string.Empty);
            var payload = DiagnosticOperationStorageCodec.Decode(payloadBytes, configuration);
            var hash = SqliteNative.ColumnText(statement, 7) ?? string.Empty;
            DiagnosticOperationStorageCodec.Require(payload.EventId.ToString("D") == SqliteNative.ColumnText(statement, 1) &&
                payload.OperationId.ToString("D") == SqliteNative.ColumnText(statement, 2) &&
                (long)payload.Kind == SqliteNative.ColumnInt64(statement, 3) &&
                payload.AggregateSequence == SqliteNative.ColumnInt64(statement, 4) &&
                (long)payload.Phase == SqliteNative.ColumnInt64(statement, 5) &&
                hash == DiagnosticOperationStorageCodec.ContentHash(position, configuration.BindingHash, payloadBytes),
                "RowBindingInvalid");
            return new DiagnosticOperationStoredRow(position, payload, payloadBytes, hash,
                SqliteNative.ColumnInt64(statement, 8), SqliteNative.ColumnText(statement, 9) ?? string.Empty);
        });
        ValidateDiagnosticOperationHistory(configuration, rows);
        return rows;
    }

    /// <summary>Replays every stored operation with its one exact legal transition sequence.</summary>
    internal static void ValidateDiagnosticOperationHistory(DiagnosticSupportConfiguration configuration,
        IReadOnlyList<DiagnosticOperationStoredRow> rows)
    {
        foreach (var group in rows.GroupBy(row => row.Payload.OperationId))
        {
            var history = group.OrderBy(row => row.Payload.AggregateSequence).ToArray();
            var first = history[0].Payload;
            DiagnosticOperationStorageCodec.Require(first.Phase == DiagnosticOperationPhase.Admitted &&
                first.AggregateSequence == 1, "OperationStartInvalid");
            if (first.Kind == DiagnosticOperationKind.Capture)
                DiagnosticOperationStorageCodec.Require(first.ReasonCode == CaptureAdmittedReason,
                    "OperationReasonInvalid");
            else
                DiagnosticOperationStorageCodec.Require(first.ReasonCode == BundleAdmittedReason,
                    "OperationReasonInvalid");
            for (var index = 1; index < history.Length; index++)
            {
                var previous = history[index - 1].Payload;
                var current = history[index].Payload;
                DiagnosticOperationStorageCodec.Require(current.AggregateSequence == previous.AggregateSequence + 1,
                    "OperationSequenceInvalid");
                DiagnosticOperationStorageCodec.Require(LegalTransition(previous.Phase, current.Phase),
                    "OperationTransitionInvalid");
                RequireSameOperationBinding(previous, current);
                ValidateBundleFields(previous, current);
                ValidateCaptureStopFields(previous, current);
            }
            RequireTerminalIsLast(history);
        }
    }

    private static bool LegalTransition(DiagnosticOperationPhase previous, DiagnosticOperationPhase current) =>
        previous switch
        {
            DiagnosticOperationPhase.Admitted => current is DiagnosticOperationPhase.Sealed or
                DiagnosticOperationPhase.Failed or
                DiagnosticOperationPhase.Interrupted,
            DiagnosticOperationPhase.Sealed => current is DiagnosticOperationPhase.Completed or
                DiagnosticOperationPhase.Failed or DiagnosticOperationPhase.Interrupted,
            _ => false
        };

    private static void RequireTerminalIsLast(IReadOnlyList<DiagnosticOperationStoredRow> history)
    {
        for (var index = 0; index < history.Count - 1; index++)
            DiagnosticOperationStorageCodec.Require(history[index].Payload.Phase is not
                (DiagnosticOperationPhase.Completed or DiagnosticOperationPhase.Failed or
                    DiagnosticOperationPhase.Interrupted), "OperationTerminalNotLast");
    }

    private static void RequireSameOperationBinding(DiagnosticOperationPayload previous,
        DiagnosticOperationPayload current)
    {
        DiagnosticOperationStorageCodec.Require(current.EventId != previous.EventId &&
            current.Kind == previous.Kind && current.OperationId == previous.OperationId &&
            current.CommandCorrelationId == previous.CommandCorrelationId &&
            current.RuntimeEpoch == previous.RuntimeEpoch && current.Reason == previous.Reason &&
            current.ActorPrincipalId == previous.ActorPrincipalId && current.SessionId == previous.SessionId &&
            current.StepUpGrantId == previous.StepUpGrantId &&
            current.AuthorizationRevision == previous.AuthorizationRevision &&
            current.AuthorizationTarget == previous.AuthorizationTarget &&
            current.AuthorizationPolicyId == previous.AuthorizationPolicyId &&
            current.AuthorizationPolicyVersion == previous.AuthorizationPolicyVersion &&
            current.AuthorizationPolicyHash == previous.AuthorizationPolicyHash &&
            current.LoggingPolicyHash == previous.LoggingPolicyHash &&
            current.SupportPolicyHash == previous.SupportPolicyHash &&
            current.ConfigurationHash == previous.ConfigurationHash &&
            current.Command == previous.Command && current.Authorization == previous.Authorization &&
            current.AdmittedDeadlineUtc == previous.AdmittedDeadlineUtc &&
            current.AdmittedMaximumEvents == previous.AdmittedMaximumEvents &&
            JsonSerializer.SerializeToUtf8Bytes(current.CaptureProfile).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(previous.CaptureProfile)) &&
            JsonSerializer.SerializeToUtf8Bytes(current.SupportScope).AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(previous.SupportScope)),
            "OperationBindingChanged");
    }

    private static void ValidateBundleFields(DiagnosticOperationPayload previous,
        DiagnosticOperationPayload current)
    {
        if (current.Kind != DiagnosticOperationKind.Bundle) return;
        DiagnosticOperationStorageCodec.Require(previous.Phase == DiagnosticOperationPhase.Admitted || (current.BundleId, current.BundleContentHash, current.BundleBytes,
            current.BundleExpiresAtUtc) == (previous.BundleId, previous.BundleContentHash, previous.BundleBytes,
            previous.BundleExpiresAtUtc),
            "BundleEvidenceChanged");
        if (current.Phase == DiagnosticOperationPhase.Sealed ||
            current.Phase == DiagnosticOperationPhase.Completed && previous.Phase == DiagnosticOperationPhase.Sealed)
            DiagnosticOperationStorageCodec.Require(current.BundleId is { } id && id != Guid.Empty &&
                current.BundleContentHash is { Length: 64 } && current.BundleBytes is > 0 &&
                current.BundleExpiresAtUtc is { } expiry && DiagnosticOperationStorageCodec.Utc(expiry),
                "BundleEvidenceRequired");
        if (previous.Phase == DiagnosticOperationPhase.Admitted && current.Phase is DiagnosticOperationPhase.Failed or DiagnosticOperationPhase.Interrupted)
            DiagnosticOperationStorageCodec.Require(current.BundleId is null && current.BundleContentHash is null &&
                current.BundleBytes is null && current.BundleExpiresAtUtc is null, "BundleEvidenceForbidden");
        DiagnosticOperationStorageCodec.Require(current.StopCommand is null && current.StopAuthorization is null &&
            current.StopAuthorizationTarget is null, "BundleStopForbidden");
    }

    private static void ValidateCaptureStopFields(DiagnosticOperationPayload previous,
        DiagnosticOperationPayload current)
    {
        if (current.Kind != DiagnosticOperationKind.Capture) return;
        DiagnosticOperationStorageCodec.Require(current.BundleId is null && current.BundleContentHash is null &&
            current.BundleBytes is null && current.BundleExpiresAtUtc is null, "CaptureBundleForbidden");
        // A manual stop records the accepted StopDiagnosticCapture command; an automatic
        // deadline or event-cap close seals without one. Half a reference is never legal.
        DiagnosticOperationStorageCodec.Require((current.StopCommand is null) == (current.StopAuthorization is null) &&
            (current.StopCommand is null) == (current.StopAuthorizationTarget is null),
            "CaptureStopPartial");
        if (current.StopCommand is not null)
            DiagnosticOperationStorageCodec.Require(current.StopReason is not null &&
                current.StopAuthorizationTarget == BuildCaptureStopTarget(current, current.StopReason.Value),
                "CaptureStopRequired");
        if (current.Phase == DiagnosticOperationPhase.Sealed)
        {
            DiagnosticOperationStorageCodec.Require(current.ObservedEvents is not null, "CaptureObservationRequired");
            DiagnosticOperationStorageCodec.Require(current.ReasonCode switch
            {
                "DiagnosticCaptureTimeLimit" => current.ObservedAtUtc >= current.AdmittedDeadlineUtc,
                "DiagnosticCaptureEventLimit" => current.ObservedEvents == current.AdmittedMaximumEvents,
                "DiagnosticCaptureStopped" => current.StopCommand is not null,
                _ => false
            }, "CaptureTerminalReasonInvalid");
        }
        if (previous.Phase == DiagnosticOperationPhase.Sealed)
            DiagnosticOperationStorageCodec.Require(current.ObservedEvents == previous.ObservedEvents &&
                current.StopCommand == previous.StopCommand && current.StopAuthorization == previous.StopAuthorization &&
                current.StopAuthorizationTarget == previous.StopAuthorizationTarget && current.StopReason == previous.StopReason,
                "CaptureSealedEvidenceChanged");
    }

    /// <summary>
    /// The remaining terminal-fact reserve of every open operation: an Admitted operation still
    /// owes three future facts, a Sealed operation two and a terminal operation none. The
    /// maximum four-fact footprint of one admitted operation can therefore never consume the
    /// final shared audit capacity.
    /// </summary>
    internal static long ReadDiagnosticSupportAuditReserve(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(CASE latest.Phase WHEN 1 THEN 3 WHEN 2 THEN 2 ELSE 0 END),0) FROM (
                SELECT f.Phase FROM diagnostic_operation_facts f
                WHERE f.AggregateSequence=(SELECT MAX(g.AggregateSequence)
                    FROM diagnostic_operation_facts g WHERE g.OperationId=f.OperationId)) AS latest;", deadline);

    internal static string DiagnosticOperationBinding(DiagnosticOperationPayload value) =>
        DiagnosticOperationBinding(value.Kind, value.OperationId, value.RuntimeEpoch, value.CommandCorrelationId,
            value.Reason, value.ActorPrincipalId, value.SessionId, value.StepUpGrantId, value.AuthorizationRevision,
            value.AuthorizationTarget, value.AuthorizationPolicyId, value.AuthorizationPolicyVersion,
            value.AuthorizationPolicyHash, value.LoggingPolicyHash, value.SupportPolicyHash, value.ConfigurationHash);

    internal static string DiagnosticOperationBinding(DiagnosticOperationKind kind, Guid operationId, Guid runtimeEpoch,
        Guid commandCorrelationId, DiagnosticSupportReason reason, Guid actorPrincipalId, Guid sessionId,
        Guid stepUpGrantId, long authorizationRevision, string authorizationTarget, string authorizationPolicyId,
        string authorizationPolicyVersion, string authorizationPolicyHash, string loggingPolicyHash,
        string supportPolicyHash, string configurationHash) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("DiagnosticOperationBindingV1",
            operationId.ToString("D"), kind.ToString(), runtimeEpoch.ToString("D"),
            commandCorrelationId.ToString("D"), reason.ToString(),
            actorPrincipalId.ToString("D"), sessionId.ToString("D"), stepUpGrantId.ToString("D"),
            authorizationRevision.ToString(CultureInfo.InvariantCulture), authorizationTarget,
            authorizationPolicyId, authorizationPolicyVersion, authorizationPolicyHash,
            loggingPolicyHash, supportPolicyHash, configurationHash)));

    /// <summary>
    /// Re-proves one admitted fact against its accepted command fact and its
    /// DiagnosticOperationAuthorized identity event. This is the exact command/identity enum
    /// feature gate of the ledger: the operation kind, the required permission and the closed
    /// identity event kind all have to agree.
    /// </summary>
    internal static void RequireDiagnosticSupportAuthority(sqlite3 database,
        DiagnosticOperationStoredRow row, StoreDeadline deadline)
    {
        var payload = row.Payload;
        if (payload.Phase == DiagnosticOperationPhase.Admitted)
        {
            var (permission, commandKind) = payload.Kind == DiagnosticOperationKind.Capture
                ? (Permission.StartDiagnosticCapture, AuditedCommandKind.StartDiagnosticCapture)
                : (Permission.ExportSupportBundle, AuditedCommandKind.CreateSupportBundle);
            VerifyDiagnosticSupportOperationAuthority(database, permission, commandKind, payload,
                payload.Command, payload.Authorization, payload.AuthorizationTarget, admitted: true, deadline);
            return;
        }
        if (payload.Kind == DiagnosticOperationKind.Capture && payload.Phase != DiagnosticOperationPhase.Admitted &&
            payload.StopCommand is not null)
        {
            DiagnosticOperationStorageCodec.Require(payload.StopAuthorization is not null &&
                payload.StopAuthorizationTarget is { Length: 64 }, "StopAuthorityMissing");
            VerifyDiagnosticSupportOperationAuthority(database, Permission.StartDiagnosticCapture,
                AuditedCommandKind.StopDiagnosticCapture, payload, payload.StopCommand!,
                payload.StopAuthorization!, payload.StopAuthorizationTarget!, admitted: false, deadline);
        }
    }

    private static void VerifyDiagnosticSupportOperationAuthority(sqlite3 database, Permission permission,
        AuditedCommandKind commandKind, DiagnosticOperationPayload payload,
        DiagnosticAuditReference command, DiagnosticAuditReference authorization, string target,
        bool admitted, StoreDeadline deadline)
    {
        var station = AuditChainDatabase.Text(database,
            "SELECT StationId FROM identity_policy_binding WHERE Id=1;", deadline)!;
        var commandRows = AuditChainDatabase.Read(database, @"SELECT Sequence,Hash FROM audit_entries
            WHERE Sequence=? AND Kind='CommandFact' LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty),
            DiagnosticSupportNumber(command.Sequence));
        DiagnosticOperationStorageCodec.Require(commandRows.Count == 1 &&
            commandRows[0].Hash == command.Hash, "CommandAuditMissing");
        var exactCommand = ReadCommandAuditReference(database, command.EventId, deadline);
        DiagnosticOperationStorageCodec.Require(exactCommand.Sequence == command.Sequence &&
            exactCommand.Hash == command.Hash, "CommandReferenceMismatch");
        var facts = AuditChainDatabase.Read(database, @"SELECT CorrelationId,CommandKind,
            AuthenticatedHumanPrincipalId,Phase,Disposition,ReasonCode,ClaimedSessionId,ClaimedStepUpGrantId
            FROM command_facts WHERE EventId=? LIMIT 2;", deadline,
            statement => (CorrelationId: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Kind: checked((int)SqliteNative.ColumnInt64(statement, 1)),
                Actor: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Phase: SqliteNative.ColumnInt64(statement, 3),
                Disposition: SqliteNative.ColumnInt64(statement, 4),
                Reason: SqliteNative.ColumnText(statement, 5) ?? string.Empty,
                Session: SqliteNative.ColumnText(statement, 6) ?? string.Empty,
                Grant: SqliteNative.ColumnText(statement, 7) ?? string.Empty),
            command.EventId.ToString("D"));
        DiagnosticOperationStorageCodec.Require(facts.Count == 1 &&
            facts[0].Kind == (int)commandKind && facts[0].Actor == payload.ActorPrincipalId.ToString("D") &&
            facts[0].Phase == (int)CommandAuditPhase.Outcome &&
            facts[0].Disposition == (int)CommandDisposition.Accepted &&
            (!admitted || facts[0].Reason == payload.ReasonCode) &&
            facts[0].Session == payload.SessionId.ToString("D") &&
            (!admitted || facts[0].Grant == payload.StepUpGrantId.ToString("D")) &&
            Guid.TryParseExact(facts[0].Grant, "D", out var actualGrant) && actualGrant != Guid.Empty &&
            (!admitted || facts[0].CorrelationId == payload.CommandCorrelationId.ToString("D")),
            "CommandBindingMismatch");
        var commandCorrelation = facts[0].CorrelationId;
        var commandReason = facts[0].Reason;
        var authorizationRows = AuditChainDatabase.Read(database, @"SELECT Sequence,IdentityPosition,Payload,Hash
            FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent' LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 3) ?? string.Empty),
            DiagnosticSupportNumber(authorization.Sequence));
        DiagnosticOperationStorageCodec.Require(authorizationRows.Count == 1 &&
            authorizationRows[0].Sequence == authorization.Sequence &&
            authorizationRows[0].Hash == authorization.Hash && authorizationRows[0].Ordinal > 0,
            "AuthorizationAuditMissing");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(authorizationRows[0].Payload); }
        catch (FormatException) { throw new InvalidOperationException("DiagnosticSupportAuthorizationAuditInvalid"); }
        string?[] fields;
        try { fields = DecodeContractIdentityFields(bytes); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or EndOfStreamException
            or DecoderFallbackException)
        { throw new InvalidOperationException("DiagnosticSupportAuthorizationAuditInvalid"); }
        IdentityAuditEvent.VerifyPayload(bytes, authorizationRows[0].Ordinal, station,
            DiagnosticSupportStoreOptions.SchemaVersion);
        DiagnosticOperationStorageCodec.Require(fields.Length == 49 &&
            fields[1] == authorization.EventId.ToString("D") &&
            fields[2] == nameof(IdentityEventKind.DiagnosticOperationAuthorized) &&
            fields[4] == station && fields[5] == payload.ActorPrincipalId.ToString("D") &&
            fields[9] == commandReason && fields[25] == payload.SessionId.ToString("D") &&
            fields[27] == payload.AuthorizationPolicyId && fields[28] == payload.AuthorizationPolicyVersion &&
            fields[29] == payload.AuthorizationPolicyHash &&
            fields[30] == payload.ActorPrincipalId.ToString("D") &&
            fields[31] == commandCorrelation &&
            fields[32] == facts[0].Grant && fields[33] == permission.ToString() &&
            fields[37] == target && fields[38] == commandCorrelation &&
            fields[39] == commandKind.ToString() && fields[42] == payload.OperationId.ToString("D"),
            "AuthorityBindingMismatch");
    }

    /// <summary>
    /// One-to-one coverage of the accepted start/stop/create command facts and the admitted and
    /// capture-sealed rows. No accepted command may exist without exactly one durable fact and
    /// no fact may name a command that is not the exact accepted kind.
    /// </summary>
    internal static void RequireDiagnosticSupportAuthorityCoverage(sqlite3 database,
        IReadOnlyList<DiagnosticOperationStoredRow> rows, StoreDeadline deadline)
    {
        var accepted = AuditChainDatabase.Read(database, @"SELECT EventId,CommandKind FROM command_facts
            WHERE Phase=? AND Disposition=? AND CommandKind IN(?,?,?) ORDER BY Position;", deadline,
            statement => (EventId: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Kind: checked((int)SqliteNative.ColumnInt64(statement, 1))),
            DiagnosticSupportNumber((long)CommandAuditPhase.Outcome), DiagnosticSupportNumber((long)CommandDisposition.Accepted),
            DiagnosticSupportNumber((long)AuditedCommandKind.StartDiagnosticCapture),
            DiagnosticSupportNumber((long)AuditedCommandKind.StopDiagnosticCapture),
            DiagnosticSupportNumber((long)AuditedCommandKind.CreateSupportBundle));
        var admitted = rows.Where(row => row.Payload.Phase == DiagnosticOperationPhase.Admitted)
            .Select(row => (row.Payload.Kind, row.Payload.Command.EventId.ToString("D"))).ToArray();
        var latest = rows.GroupBy(row => row.Payload.OperationId).Select(group => group.Last()).ToArray();
        var stopped = latest.Where(row => row.Payload.Kind == DiagnosticOperationKind.Capture).Select(row =>
            row.Payload.Phase is DiagnosticOperationPhase.Admitted or DiagnosticOperationPhase.Sealed
                ? BindAcceptedDiagnosticStop(database, row.Payload, deadline) : row.Payload).ToArray();
        var sealedStops = stopped.Where(value => value.StopCommand is not null)
            .Select(value => value.StopCommand!.EventId.ToString("D")).ToArray();
        DiagnosticOperationStorageCodec.Require(
            admitted.Select(value => value.Item2).Distinct(StringComparer.Ordinal).Count() == admitted.Length &&
            sealedStops.Distinct(StringComparer.Ordinal).Count() == sealedStops.Length, "CoverageDuplicate");
        var starts = accepted.Where(value =>
            value.Kind == (int)AuditedCommandKind.StartDiagnosticCapture).ToArray();
        var bundles = accepted.Where(value => value.Kind == (int)AuditedCommandKind.CreateSupportBundle).ToArray();
        DiagnosticOperationStorageCodec.Require(
            admitted.Count(value => value.Kind == DiagnosticOperationKind.Capture) == starts.Length &&
            admitted.Count(value => value.Kind == DiagnosticOperationKind.Bundle) == bundles.Length &&
            starts.All(value => admitted.Any(row => row.Item2 == value.EventId)) &&
            bundles.All(value => admitted.Any(row => row.Item2 == value.EventId)),
            "CoverageMismatch");
        var stops = accepted.Where(value => value.Kind == (int)AuditedCommandKind.StopDiagnosticCapture).ToArray();
        DiagnosticOperationStorageCodec.Require(stops.Length == sealedStops.Length &&
            stops.All(value => sealedStops.Contains(value.EventId, StringComparer.Ordinal)), "StopCoverageMismatch");
    }

    internal const string CaptureAdmittedReason = "DiagnosticCaptureAdmitted";
    internal const string BundleAdmittedReason = "SupportBundleAdmitted";
    internal const string DiagnosticSupportActivationKind = DiagnosticSupportStoreOptions.ActivationKind;
    internal const string DiagnosticOperationAuditKind = DiagnosticSupportStoreOptions.OperationAuditKind;

    internal static string DiagnosticSupportNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}
