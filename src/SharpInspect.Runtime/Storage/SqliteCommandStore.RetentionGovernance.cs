using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record RetentionAuthorityEvidence(Guid CommandEventId, long CommandAuditSequence,
    string CommandAuditHash, Guid AuthorizationEventId, long AuthorizationAuditSequence,
    string AuthorizationAuditHash, string AuthorizationPolicyId, string AuthorizationPolicyVersion,
    string AuthorizationPolicyHash);

internal sealed partial class SqliteCommandStore
{
    internal ValueTask<IdentityWriteResult> UpdateRetentionGovernanceAsync(ChangeEvidenceRetentionCommand command,
        Func<IdentityAuthorityState, EvidenceRetentionReadState, bool, IdentityUpdate> update, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), CancellationToken.None, deadline);

    private EvidenceRetentionReadState ReadRetentionGovernanceState(sqlite3 database, StoreDeadline deadline)
    {
        RequireConfiguredRetention(database, _options, deadline);
        var config = ReadRetentionConfiguration(database, deadline);
        var rows = ReadRetentionRows(database, config, deadline);
        var replay = new EvidenceRetentionReplay();
        foreach (var row in rows) replay.Apply(row);
        var tail = AuditChainDatabase.Tail(database, deadline);
        return new(config, rows, replay, tail.Sequence, tail.Hash,
            rows.Sum(x => (long)EvidenceRetentionCodec.Encode(x.Payload, config.MaximumPayloadBytes).Length));
    }

    internal static string RetentionOperationBinding(EvidenceRetentionPayload fact) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fact with { Authority = null })));

    private void PrepareRetentionGovernanceCapacity(sqlite3 database, IdentityUpdate update, EvidenceRetentionReadState state,
        StoreDeadline deadline)
    {
        var reserve = state.Replay.FutureReserve;
        var config = state.Configuration;
        var mutation = update.Retention!;
        var authorization = update.Events[0];
        var authorizationPolicy = _options.LocalIdentity!.AuthorizationPolicy;
        // Use the largest sequence representation before any event is appended.
        // Actual event ids, actor, reason and policy bytes are already frozen.
        _ = EvidenceRetentionCodec.Encode(mutation with { Authority = new(
            update.CommandFacts![0].EventId, long.MaxValue, new string('0', 64),
            authorization.EventId, long.MaxValue, new string('0', 64),
            authorizationPolicy.Id, authorizationPolicy.Version, authorizationPolicy.ContentHash) },
            config.MaximumPayloadBytes);

        EvidenceRetentionCodec.Require(state.Rows.Count + reserve + 1 <= config.MaximumEvents &&
            checked(state.PayloadBytes + (reserve + 1) * config.MaximumPayloadBytes) <= config.MaximumTotalBytes,
            "LedgerCapacityExceeded");
        AuditChainDatabase.EnsureRetentionTransactionCapacity(database, _policy!, 3, reserve, deadline);
    }

    private void AppendRetentionIdentityMutation(sqlite3 database, IdentityUpdate update,
        EvidenceRetentionReadState state, ChangeEvidenceRetentionCommand command, IdentityWork work, StoreDeadline deadline)
    {
        var fact = update.Retention!;
        EvidenceRetentionCodec.Require(update.Events.Count == 1 && update.CommandFacts is { Count: 1 } &&
            update.Events[0].Kind == IdentityEventKind.EvidenceRetentionChanged &&
            update.CommandFacts[0].CommandKind == AuditedCommandKind.ChangeEvidenceRetention &&
            update.CommandFacts[0].Disposition == CommandDisposition.Accepted &&
            update.Events[0].RecoverySafetyEvidence == RetentionOperationBinding(fact) &&
            fact.Authority is null && fact.OperationId == command.CorrelationId && fact.Owner == command.Owner &&
            fact.AggregateSequence == command.ExpectedRevision + 1 && fact.Reason == command.Reason &&
            fact.AuthorizationTarget == command.AuthorizationTarget &&
            fact.HoldId == command.HoldId && fact.ExtendedUntilUtc == command.ExtendedUntilUtc &&
            fact.HumanPrincipalId == update.Events[0].ActorPrincipalId &&
            fact.RecordedAtUtc == update.Events[0].OccurredAtUtc, "GovernanceMutationMismatch");
        var kind = command.Change switch
        {
            EvidenceRetentionChange.PlaceHold => EvidenceRetentionEventKind.HoldPlaced,
            EvidenceRetentionChange.ReleaseHold => EvidenceRetentionEventKind.HoldReleased,
            EvidenceRetentionChange.Extend => EvidenceRetentionEventKind.Extended,
            _ => throw new InvalidOperationException("RetentionChangeInvalid")
        };
        EvidenceRetentionCodec.Require(fact.Kind == kind, "GovernanceMutationMismatch");
        var auth = update.Events[0];
        var commandAudit = ReadCommandAuditReference(database, update.CommandFacts![0].EventId, deadline);
        var authAudit = ReadOutboxGovernanceAuthorizationReference(database, auth.EventId, deadline);
        fact = fact with { Authority = new(update.CommandFacts[0].EventId, commandAudit.Sequence,
            commandAudit.Hash!, auth.EventId, authAudit.Sequence, authAudit.Hash, _options.LocalIdentity!.AuthorizationPolicy.Id,
            _options.LocalIdentity.AuthorizationPolicy.Version, _options.LocalIdentity.AuthorizationPolicy.ContentHash) };
        var row = AppendRetentionInTransaction(database, state, fact, deadline);
        work.Result = new EvidenceRetentionResult(new(command.CorrelationId, CommandDisposition.Accepted,
            fact.ReasonCode, AuditPersistence.Persisted, update.CommandFacts[0].AttemptId),
            state.Replay.Subjects[row.Payload.Owner].ToStatus());
    }

    internal static void RequireRetentionAuthority(sqlite3 database, EvidenceRetentionStoredRow row, StoreDeadline deadline)
    {
        var fact = row.Payload;
        if (fact.Kind is not (EvidenceRetentionEventKind.HoldPlaced or EvidenceRetentionEventKind.HoldReleased or
            EvidenceRetentionEventKind.Extended)) return;
        var authority = fact.Authority!;
        EvidenceRetentionCodec.Require(authority.CommandAuditSequence < row.AuditSequence &&
            authority.AuthorizationAuditSequence < row.AuditSequence, "AuthorizationOrderInvalid");
        var station = AuditChainDatabase.Text(database, "SELECT StationId FROM identity_policy_binding WHERE Id=1;", deadline)!;
        VerifyProductionOutboxOperationAuthority(database, station, authority.AuthorizationEventId,
            authority.AuthorizationAuditSequence, authority.AuthorizationAuditHash, IdentityEventKind.EvidenceRetentionChanged,
            fact.ReasonCode, Permission.DeleteEvidence, AuditedCommandKind.ChangeEvidenceRetention,
            fact.HumanPrincipalId!.Value, fact.SessionId!.Value, fact.StepUpGrantId!.Value,
            authority.AuthorizationPolicyId, authority.AuthorizationPolicyVersion, authority.AuthorizationPolicyHash,
            fact.OperationId, authority.CommandEventId, authority.CommandAuditSequence, authority.CommandAuditHash,
            fact.AuthorizationTarget!, RetentionOperationBinding(fact), deadline, TraceStorageRetentionOptions.SchemaVersion);
        var fields = DecodeContractIdentityFields(Convert.FromBase64String(AuditChainDatabase.Text(database,
            "SELECT Payload FROM audit_entries WHERE Sequence=?;", deadline, RetentionNumber(authority.AuthorizationAuditSequence))!));
        EvidenceRetentionCodec.Require(fields[3] == fact.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
            fields[35] == fact.AuthorizationRevision!.Value.ToString(CultureInfo.InvariantCulture),
            "AuthorizationTimeOrRevisionMismatch");
    }

    internal static Guid? RetentionAuthorizationEventId(byte[] payload)
    {
        var fields = DecodeContractIdentityFields(payload);
        if (fields.Length < 3 || fields[2] != nameof(IdentityEventKind.EvidenceRetentionChanged)) return null;
        EvidenceRetentionCodec.Require(fields.Length == 49 && Guid.TryParseExact(fields[1], "D", out _),
            "AuthorizationIdentityInvalid");
        return Guid.ParseExact(fields[1]!, "D");
    }

    internal static void RequireRetentionAuthorityCoverage(sqlite3 database,
        IReadOnlyList<EvidenceRetentionStoredRow> rows, StoreDeadline deadline)
    {
        var accepted = AuditChainDatabase.Read(database, @"SELECT EventId FROM command_facts
            WHERE CommandKind=59 AND Phase=? AND Disposition=?;", deadline,
            statement => Guid.ParseExact(SqliteNative.ColumnText(statement, 0)!, "D"),
            RetentionNumber((long)CommandAuditPhase.Outcome), RetentionNumber((long)CommandDisposition.Accepted));
        var actual = rows.Where(x => x.Payload.Authority is not null)
            .Select(x => x.Payload.Authority!.CommandEventId).ToArray();
        EvidenceRetentionCodec.Require(actual.Length == actual.Distinct().Count() &&
            accepted.Count == actual.Length && accepted.All(actual.Contains), "AuthorizationCoverageMismatch");
    }
}
