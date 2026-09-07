using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Bounded read-only alarm history over the signed alarm event stream.</summary>
public sealed class SqliteAlarmHistoryQuery : IAlarmHistoryQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static int _outstanding;

    public SqliteAlarmHistoryQuery(ProductionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async ValueTask<AlarmHistoryPage> QueryAsync(AlarmHistoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.QueryTimeout < TimeSpan.FromMilliseconds(1) || _options.QueryTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(_options.QueryTimeout));
        if (_options.AlarmPolicy is null)
            return new AlarmHistoryPage(Array.Empty<AlarmHistoryRecord>(), 0, null, false, "AlarmPolicyNotConfigured");
        if (_options.AuditIntegrityPolicy is null || _options.LocalIdentity is null)
            return new AlarmHistoryPage(Array.Empty<AlarmHistoryRecord>(), 0, null, false, "AlarmPolicyRequiresIdentityAndAudit");
        if (Interlocked.Increment(ref _outstanding) > 64)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("AlarmHistoryQueryCapacityExceeded");
        }

        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("AlarmHistoryQueryDeadlineExceeded");
            return await Task.Run(() => QueryCore(filter, deadline, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SqliteNativeException ex) { throw new InvalidOperationException(ex.ReasonCode, ex); }
        catch (SqliteException ex) { throw new InvalidOperationException("AlarmHistoryStoreUnavailable", ex); }
        finally
        {
            if (entered) Slots.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private AlarmHistoryPage QueryCore(AlarmHistoryFilter filter, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            throw new InvalidOperationException(pathReason);
        using var key = WindowsMachineAuditKey.Open(_options.AuditIntegrityPolicy!, false, out _);
        using var connection = SqliteNative.Open(path, readOnly: true);
        AlarmStorageCodec.ConfigureSqliteLimit(connection.Handle!);
        var database = connection.Handle!;
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
            AuditChainDatabase.Require(schema == 7, schema < 7 ? "GovernedAlarmMigrationRequired" : "StoreSchemaTooNew");
            var verification = AuditChainDatabase.Verify(database, _options.AuditIntegrityPolicy!, key.KeyId,
                key.PublicKeyBase64,
                new AuditVerificationRequest(0, _options.AuditIntegrityPolicy!.MaximumVerificationEntries), false,
                deadline, validateAnchorReceipt: false);
            AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            var persistedPolicy = AlarmStorageCodec.ReadPersistedPolicy(database, deadline);
            AuditChainDatabase.Require(persistedPolicy is not null &&
                persistedPolicy.ContentHash == _options.AlarmPolicy!.ContentHash &&
                persistedPolicy.Id == _options.AlarmPolicy.Id && persistedPolicy.Version == _options.AlarmPolicy.Version,
                "AlarmPolicyBindingMismatch");
            AlarmStorageCodec.RequireConfiguredPolicy(persistedPolicy, _options.AlarmPolicy);
            var latest = AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0) FROM alarm_events;", deadline);
            if (filter.ThroughPosition is { } requested && requested > latest)
                throw new InvalidOperationException("AlarmHistoryCursorInvalid");
            var through = filter.ThroughPosition ?? latest;
            if (through < filter.AfterPosition)
                throw new InvalidOperationException("AlarmHistoryCursorInvalid");

            var records = AlarmStorageCodec.ReadEventPage(database, filter, through, deadline)
                .Select(item => item.Record).ToList();
            long? next = null;
            if (records.Count > filter.PageSize)
            {
                records.RemoveAt(records.Count - 1);
                next = records[^1].Position;
            }

            var (available, reason) = AlarmStorageCodec.ReadAvailability(database, _options.AlarmPolicy, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new AlarmHistoryPage(new ReadOnlyCollection<AlarmHistoryRecord>(records), through, next, available, reason);
        }
        finally
        {
            if (!committed)
                try { SQLitePCL.raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }
}

internal sealed record AlarmObservationUpdate(object? Result, IReadOnlyList<AlarmHistoryRecord> Events);

internal sealed record AlarmObservationWork(Guid RuntimeEpoch,
    Func<AlarmStateSnapshot, AlarmObservationUpdate> Update)
{
    internal object? Result { get; set; }
}

internal sealed record StoredAlarmEvent(long Position, Guid RuntimeEpoch, string SystemPrincipalId,
    AlarmHistoryRecord Record, AlarmPolicy? Policy);

internal sealed record DecodedAlarmPayload(AlarmHistoryRecord Record, AlarmPolicy? Policy,
    Guid RuntimeEpoch, string SystemPrincipalId);

internal static class AlarmStorageCodec
{
    private const int FormatVersion = 1;
    // SQLite values are bounded to 64 KiB by SqliteNative.  Keep policy/event JSON
    // below that ceiling after base64 wrapping in the signed audit row.
    // The public policy contract permits 256 rules with 64-character identifiers. The
    // canonical document can exceed 48 KiB, so use a separate bounded storage ceiling
    // below the SQLite connection limit.
    internal const int MaximumPayloadBytes = 131072;
    internal const int MaximumEncodedPayloadChars = 180000;
    internal const int SqliteValueLimitBytes = 262144;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null, WriteIndented = false };

    internal static void ConfigureSqliteLimit(SQLitePCL.sqlite3 database) =>
        SQLitePCL.raw.sqlite3_limit(database, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);

    internal static byte[] Encode(AlarmHistoryRecord record, AlarmPolicy? policy, Guid runtimeEpoch,
        string systemPrincipal)
    {
        if (runtimeEpoch == Guid.Empty && record.Transition != AlarmTransitionKind.PolicyActivated)
            throw new InvalidOperationException("AlarmRuntimeEpochRequired");
        if (systemPrincipal != SqliteCommandStore.SystemPrincipal)
            throw new InvalidOperationException("AlarmSystemPrincipalInvalid");
        var dto = new AlarmHistoryEnvelopeDto
        {
            FormatVersion = FormatVersion,
            Record = ToDto(record),
            Policy = policy is null ? null : ToDto(policy),
            RuntimeEpoch = runtimeEpoch.ToString("D"),
            SystemPrincipalId = systemPrincipal
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (payload.Length > MaximumPayloadBytes) throw new InvalidOperationException("AlarmEvidenceOversized");
        return payload;
    }

    internal static DecodedAlarmPayload Decode(byte[] payload, long expectedPosition)
    {
        if (payload is null || payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("AlarmHistoryPayloadInvalid");
        AlarmHistoryEnvelopeDto? dto;
        try { dto = JsonSerializer.Deserialize<AlarmHistoryEnvelopeDto>(payload, Json); }
        catch (JsonException) { throw new InvalidOperationException("AlarmHistoryPayloadInvalid"); }
        if (dto is null || dto.FormatVersion != FormatVersion || dto.Record is null)
            throw new InvalidOperationException("AlarmHistoryPayloadInvalid");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (!payload.SequenceEqual(canonical)) throw new InvalidOperationException("AlarmHistoryPayloadCanonicalMismatch");
        try
        {
            if (!Guid.TryParseExact(dto.RuntimeEpoch, "D", out var runtimeEpoch) ||
                dto.SystemPrincipalId != SqliteCommandStore.SystemPrincipal ||
                (runtimeEpoch == Guid.Empty && dto.Record.Transition != (int)AlarmTransitionKind.PolicyActivated) ||
                (runtimeEpoch != Guid.Empty && dto.Record.Transition == (int)AlarmTransitionKind.PolicyActivated))
                throw new InvalidOperationException("AlarmHistoryBindingMismatch");
            var record = FromDto(dto.Record);
            AlarmPolicy? policy = dto.Policy is null ? null : FromDto(dto.Policy);
            ValidateRecord(record, expectedPosition, policy);
            return new DecodedAlarmPayload(record, policy, runtimeEpoch, dto.SystemPrincipalId);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or JsonException)
        { throw new InvalidOperationException("AlarmHistoryPayloadInvalid", ex); }
    }

    internal static void ValidatePayload(byte[] payload, long expectedPosition) => _ = Decode(payload, expectedPosition);

    internal static string EncodePolicyJson(AlarmPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var json = JsonSerializer.SerializeToUtf8Bytes(ToDto(policy), Json);
        if (json.Length > MaximumPayloadBytes) throw new InvalidOperationException("AlarmPolicyEvidenceOversized");
        return Encoding.UTF8.GetString(json);
    }

    internal static AlarmPolicy DecodePolicyJson(string json, string expectedHash)
    {
        if (json is null or { Length: < 1 or > MaximumPayloadBytes }) throw new InvalidOperationException("AlarmPolicyPayloadInvalid");
        AlarmPolicyDto? dto;
        try { dto = JsonSerializer.Deserialize<AlarmPolicyDto>(json, Json); }
        catch (JsonException) { throw new InvalidOperationException("AlarmPolicyPayloadInvalid"); }
        if (dto is null) throw new InvalidOperationException("AlarmPolicyPayloadInvalid");
        var canonical = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(dto, Json));
        if (!string.Equals(canonical, json, StringComparison.Ordinal))
            throw new InvalidOperationException("AlarmPolicyPayloadCanonicalMismatch");
        var policy = FromDto(dto);
        if (!string.Equals(policy.ContentHash, expectedHash, StringComparison.Ordinal) ||
            !string.Equals(dto.ContentHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("AlarmPolicyHashMismatch");
        return policy;
    }

    internal static AlarmPolicy? ReadPersistedPolicy(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT PolicyId,Version,ContentHash,CanonicalJson,ActivatedAtUtc
            FROM alarm_policy_versions ORDER BY rowid LIMIT 2;", deadline,
            statement => (PolicyId: SqliteNative.ColumnText(statement, 0)!, Version: SqliteNative.ColumnText(statement, 1)!,
                Hash: SqliteNative.ColumnText(statement, 2)!, Json: SqliteNative.ColumnText(statement, 3)!,
                ActivatedAtUtc: SqliteNative.ColumnText(statement, 4)!));
        // Policy binding is independent of the potentially large transition history. Only
        // activation rows can authorize the immutable policy table.
        var activationEvents = ReadPolicyActivationEvents(database, deadline).ToArray();
        if (rows.Count > 1 || activationEvents.Length > 1)
            throw new InvalidOperationException("AlarmPolicyVersionConflict");
        var activationPolicies = activationEvents.Select(item => item.Policy).ToArray();
        if (rows.Count == 0)
        {
            if (activationPolicies.Length != 0) throw new InvalidOperationException("AlarmPolicyBindingMismatch");
            return null;
        }
        AlarmPolicy? result = null;
        foreach (var row in rows)
        {
            var policy = DecodePolicyJson(row.Json, row.Hash);
            if (policy.Id != row.PolicyId || policy.Version != row.Version)
                throw new InvalidOperationException("AlarmPolicyBindingMismatch");
            var signed = activationEvents.SingleOrDefault(item => item.Policy is not null &&
                item.Policy.Id == policy.Id && item.Policy.Version == policy.Version && item.Policy.ContentHash == policy.ContentHash);
            if (signed is null || !string.Equals(EncodePolicyJson(signed.Policy!), row.Json, StringComparison.Ordinal) ||
                !DateTimeOffset.TryParseExact(row.ActivatedAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var activatedAt) || signed.Record.AuditAtUtc != activatedAt)
                throw new InvalidOperationException("AlarmPolicyBindingMismatch");
            if (result is not null && (result.Id != policy.Id || result.Version != policy.Version || result.ContentHash != policy.ContentHash))
                throw new InvalidOperationException("AlarmPolicyVersionConflict");
            result = policy;
        }
        if (activationPolicies.Length != rows.Count)
            throw new InvalidOperationException("AlarmPolicyBindingMismatch");
        return result;
    }

    internal static void RequireConfiguredPolicy(AlarmPolicy? persisted, AlarmPolicy? configured)
    {
        if (persisted is null || configured is null)
        {
            if (persisted is not null || configured is not null)
                throw new InvalidOperationException("AlarmPolicyBindingMismatch");
            return;
        }

        if (persisted.Id != configured.Id || persisted.Version != configured.Version ||
            persisted.ContentHash != configured.ContentHash ||
            !string.Equals(EncodePolicyJson(persisted), EncodePolicyJson(configured), StringComparison.Ordinal))
            throw new InvalidOperationException("AlarmPolicyBindingMismatch");
    }

    internal static List<StoredAlarmEvent> ReadEvents(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        var result = ReadEventsQuery(database, AlarmEventSelect + " ORDER BY Position;", deadline);
        var expected = 1L;
        foreach (var item in result)
            if (item.Position != expected++) throw new InvalidOperationException("AlarmHistoryShapeInvalid");
        return result;
    }

    internal static List<StoredAlarmEvent> ReadEventPage(SQLitePCL.sqlite3 database,
        AlarmHistoryFilter filter, long throughPosition, StoreDeadline deadline)
    {
        var sql = new StringBuilder(AlarmEventSelect).Append(" WHERE Position>? AND Position<=?");
        var args = new List<string?>
        {
            filter.AfterPosition.ToString(CultureInfo.InvariantCulture),
            throughPosition.ToString(CultureInfo.InvariantCulture)
        };
        if (filter.InstanceId is { } instanceId)
        {
            sql.Append(" AND InstanceId=?");
            args.Add(instanceId.ToString("D"));
        }
        if (filter.Code is { } code)
        {
            sql.Append(" AND Code=?");
            args.Add(code);
        }
        sql.Append(" ORDER BY Position LIMIT ?;");
        args.Add((filter.PageSize + 1).ToString(CultureInfo.InvariantCulture));
        return ReadEventsQuery(database, sql.ToString(), deadline, args.ToArray());
    }

    internal static StoredAlarmEvent? ReadLatestBoundary(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        var row = ReadEventsQuery(database, AlarmEventSelect + @"
            WHERE Transition=? AND ReasonCode IN (?,?,?,?) ORDER BY Position DESC LIMIT 1;", deadline,
            ((int)AlarmTransitionKind.BoundaryRejected).ToString(CultureInfo.InvariantCulture),
            "AlarmCodeUnmapped", "AlarmSourceMismatch", "AlarmCapacityExceeded",
            "AlarmActiveInstanceCapacityExceeded");
        return row.SingleOrDefault();
    }

    internal static StoredAlarmEvent ReadEventAtPosition(SQLitePCL.sqlite3 database, long position,
        StoreDeadline deadline)
    {
        var rows = ReadEventsQuery(database, AlarmEventSelect + " WHERE Position=?;", deadline,
            position.ToString(CultureInfo.InvariantCulture));
        return rows.SingleOrDefault() ?? throw new InvalidOperationException("AlarmHistoryMissing");
    }

    internal static List<StoredAlarmEvent> ReadPolicyActivationEvents(SQLitePCL.sqlite3 database,
        StoreDeadline deadline) => ReadEventsQuery(database, AlarmEventSelect + " WHERE Transition=? ORDER BY Position LIMIT 2;",
        deadline, ((int)AlarmTransitionKind.PolicyActivated).ToString(CultureInfo.InvariantCulture));

    internal static (bool Available, string ReasonCode) ReadAvailability(SQLitePCL.sqlite3 database,
        AlarmPolicy? policy, StoreDeadline deadline)
    {
        if (policy is null) return (false, "AlarmPolicyNotConfigured");
        var boundary = ReadLatestBoundary(database, deadline);
        return boundary is null ? (true, "AlarmAuthorityAvailable") : (false, boundary.Record.ReasonCode);
    }

    private static List<StoredAlarmEvent> ReadEventsQuery(SQLitePCL.sqlite3 database, string sql,
        StoreDeadline deadline, params string?[] args)
    {
        var rows = AuditChainDatabase.Read(database, sql, deadline, ReadRawEvent, args);
        return rows.Select(DecodeRawEvent).ToList();
    }

    private static RawAlarmEvent ReadRawEvent(SQLitePCL.sqlite3_stmt statement) => new(
        SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1)!,
        SqliteNative.ColumnText(statement, 2)!, SqliteNative.ColumnText(statement, 3)!,
        SqliteNative.ColumnInt64(statement, 4), SqliteNative.ColumnText(statement, 5)!,
        SqliteNative.ColumnText(statement, 6)!, SqliteNative.ColumnText(statement, 7),
        SqliteNative.ColumnText(statement, 8), SqliteNative.ColumnText(statement, 9),
        SqliteNative.ColumnText(statement, 10), SqliteNative.ColumnText(statement, 11),
        SqliteNative.ColumnText(statement, 12), SqliteNative.ColumnText(statement, 13)!,
        SqliteNative.ColumnText(statement, 14)!, SqliteNative.ColumnText(statement, 15)!);

    private static StoredAlarmEvent DecodeRawEvent(RawAlarmEvent row)
    {
        if (!Guid.TryParseExact(row.EventId, "D", out var eventId) || eventId == Guid.Empty ||
            !Guid.TryParseExact(row.RuntimeEpoch, "D", out var runtimeEpoch) ||
            row.SystemPrincipal != SqliteCommandStore.SystemPrincipal || row.Payload.Length > MaximumEncodedPayloadChars)
            throw new InvalidOperationException("AlarmHistoryShapeInvalid");
        byte[] payload;
        try { payload = Convert.FromBase64String(row.Payload); }
        catch (FormatException ex) { throw new InvalidOperationException("AlarmHistoryPayloadInvalid", ex); }
        var decoded = Decode(payload, row.Position);
        var record = decoded.Record;
        if (record.EventId != eventId || row.Transition != (int)record.Transition ||
            row.ObservedAtUtc != record.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ||
            row.AuditAtUtc != record.AuditAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ||
            row.InstanceId != record.InstanceId?.ToString("D") || row.Code != record.Code || row.Source != record.Source ||
            row.ActorPrincipalId != record.ActorPrincipalId?.ToString("D") || row.SessionId != record.SessionId?.ToString("D") ||
            row.CorrelationId != record.CommandCorrelationId?.ToString("D") || row.ReasonCode != record.ReasonCode ||
            row.PayloadHash != Convert.ToHexString(SHA256.HashData(payload)))
            throw new InvalidOperationException("AlarmHistoryBindingMismatch");
        if (decoded.RuntimeEpoch != runtimeEpoch || decoded.SystemPrincipalId != row.SystemPrincipal)
            throw new InvalidOperationException("AlarmHistoryBindingMismatch");
        return new StoredAlarmEvent(row.Position, runtimeEpoch, row.SystemPrincipal, record, decoded.Policy);
    }

    private sealed record RawAlarmEvent(long Position, string EventId, string RuntimeEpoch,
        string SystemPrincipal, long Transition, string ObservedAtUtc, string AuditAtUtc, string? InstanceId,
        string? Code, string? Source, string? ActorPrincipalId, string? SessionId, string? CorrelationId,
        string ReasonCode, string Payload, string PayloadHash);

    private const string AlarmEventSelect = @"SELECT Position,EventId,RuntimeEpoch,SystemPrincipalId,Transition,
        ObservedAtUtc,AuditAtUtc,InstanceId,Code,Source,ActorPrincipalId,SessionId,CommandCorrelationId,ReasonCode,Payload,PayloadHash
        FROM alarm_events";

    internal static long AppendEvent(SQLitePCL.sqlite3 database, AuditIntegrityPolicy policy, IAuditSigningKey key,
        Guid runtimeEpoch, AlarmHistoryRecord input, AlarmPolicy? eventPolicy, Guid? expectedCorrelation,
        StoreDeadline deadline)
    {
        if (input.Position != 0) throw new InvalidOperationException("AlarmHistoryPositionMustBeZero");
        if (runtimeEpoch == Guid.Empty && input.Transition != AlarmTransitionKind.PolicyActivated)
            throw new InvalidOperationException("AlarmRuntimeEpochRequired");
        if (expectedCorrelation is { } correlation && input.CommandCorrelationId != correlation)
            throw new InvalidOperationException("AlarmCommandCorrelationMismatch");
        if (input.Instance is { LastTransitionPosition: not 0 })
            throw new InvalidOperationException("AlarmHistoryPositionForged");
        if (input.Transition == AlarmTransitionKind.PolicyActivated && eventPolicy is null)
            throw new InvalidOperationException("AlarmPolicyActivationMissing");
        if (input.Transition != AlarmTransitionKind.PolicyActivated && eventPolicy is not null)
            throw new InvalidOperationException("AlarmPolicyActivationUnexpected");

        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0) FROM alarm_events;", deadline) + 1);
        var prepared = input with
        {
            Position = position,
            Instance = input.Instance is { } instance ? instance with { LastTransitionPosition = position } : null
        };
        var payload = Encode(prepared, eventPolicy, runtimeEpoch, SqliteCommandStore.SystemPrincipal);
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        ValidatePayload(payload, position);
        AuditChainDatabase.Execute(database, @"INSERT INTO alarm_events(Position,EventId,RuntimeEpoch,SystemPrincipalId,
            Transition,ObservedAtUtc,AuditAtUtc,InstanceId,Code,Source,ActorPrincipalId,SessionId,CommandCorrelationId,
            ReasonCode,Payload,PayloadHash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), prepared.EventId.ToString("D"), runtimeEpoch.ToString("D"),
            SqliteCommandStore.SystemPrincipal, ((int)prepared.Transition).ToString(CultureInfo.InvariantCulture),
            prepared.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            prepared.AuditAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            prepared.InstanceId?.ToString("D"), prepared.Code, prepared.Source,
            prepared.ActorPrincipalId?.ToString("D"), prepared.SessionId?.ToString("D"),
            prepared.CommandCorrelationId?.ToString("D"), prepared.ReasonCode, Convert.ToBase64String(payload), hash);
        AuditChainDatabase.AppendAlarm(database, policy, key, position, deadline);
        return position;
    }

    internal static (bool Available, string ReasonCode) Availability(AlarmPolicy? policy, IReadOnlyList<StoredAlarmEvent> events)
    {
        if (policy is null) return (false, "AlarmPolicyNotConfigured");
        var boundary = events.LastOrDefault(item => item.Record.Transition == AlarmTransitionKind.BoundaryRejected &&
            item.Record.ReasonCode is "AlarmCodeUnmapped" or "AlarmSourceMismatch" or
            "AlarmCapacityExceeded" or "AlarmActiveInstanceCapacityExceeded");
        return boundary is null ? (true, "AlarmAuthorityAvailable") : (false, boundary.Record.ReasonCode);
    }

    internal static AlarmStateSnapshot BuildState(AlarmPolicy? policy, IReadOnlyList<StoredAlarmEvent> events,
        Guid runtimeEpoch)
    {
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("AlarmRuntimeEpochRequired", nameof(runtimeEpoch));
        if (policy is null)
            return new AlarmStateSnapshot(false, "AlarmPolicyNotConfigured", runtimeEpoch, 0, null,
                Array.Empty<AlarmInstanceSnapshot>(), new AlarmPlcProjection(Array.Empty<AlarmPlcEntry>(), 0, 0, false));

        var instances = new Dictionary<Guid, AlarmInstanceSnapshot>();
        AlarmPlcProjection? projection = null;
        foreach (var item in events)
        {
            if (item.Record.Transition == AlarmTransitionKind.Cleared && item.Record.Instance is { } cleared)
                instances.Remove(cleared.InstanceId);
            else if (item.Record.Instance is { } instance)
                instances[instance.InstanceId] = instance;
            if (item.Record.PlcProjection is { } supplied) projection = supplied;
        }

        var ordered = instances.Values.OrderBy(item => item.InstanceId).ToArray();
        var computedProjection = AlarmTransitions.Project(policy, ordered);
        if (projection is not null && !ProjectionEqual(projection, computedProjection))
            throw new InvalidOperationException("AlarmProjectionMismatch");
        projection = computedProjection;
        var availability = Availability(policy, events);
        var revision = events.Count == 0 ? 0 : events[^1].Position;
        return new AlarmStateSnapshot(availability.Available, availability.ReasonCode, runtimeEpoch, revision,
            policy, ordered, projection);
    }

    private static bool ProjectionEqual(AlarmPlcProjection left, AlarmPlcProjection right)
    {
        return left.TotalUncleared == right.TotalUncleared && left.BlockingCount == right.BlockingCount &&
            left.FaultAbortPresent == right.FaultAbortPresent && left.Entries.SequenceEqual(right.Entries);
    }

    private static AlarmHistoryRecordDto ToDto(AlarmHistoryRecord record) => new()
    {
        Position = record.Position, EventId = record.EventId.ToString("D"), InstanceId = record.InstanceId?.ToString("D"),
        Code = record.Code, Source = record.Source, Transition = (int)record.Transition,
        ObservedAtUtc = record.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        AuditAtUtc = record.AuditAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ActorPrincipalId = record.ActorPrincipalId?.ToString("D"), SessionId = record.SessionId?.ToString("D"),
        CommandCorrelationId = record.CommandCorrelationId?.ToString("D"), ReasonCode = record.ReasonCode,
        Instance = record.Instance is null ? null : ToDto(record.Instance),
        PlcProjection = record.PlcProjection is null ? null : ToDto(record.PlcProjection)
    };

    private static AlarmPolicyDto ToDto(AlarmPolicy policy) => new()
    {
        Id = policy.Id, Version = policy.Version, ContentHash = policy.ContentHash,
        SourceObservationFreshnessTicks = policy.SourceObservationFreshness.Ticks,
        MaximumActiveInstances = policy.MaximumActiveInstances, MaximumPlcEntries = policy.MaximumPlcEntries,
        Rules = policy.Rules.OrderBy(rule => rule.Code, StringComparer.Ordinal).Select(rule => new AlarmPolicyRuleDto
        {
            Code = rule.Code, Source = rule.Source, Severity = (int)rule.Severity,
            ProductionImpact = (int)rule.ProductionImpact, IsLatched = rule.IsLatched,
            Notification = (int)rule.Notification, PlcCode = rule.PlcCode, PlcPriority = rule.PlcPriority,
            ResetPrerequisites = (int)rule.ResetPrerequisites
        }).ToList()
    };

    private static AlarmInstanceDto ToDto(AlarmInstanceSnapshot instance) => new()
    {
        InstanceId = instance.InstanceId.ToString("D"), Code = instance.Code, Source = instance.Source,
        PolicyId = instance.PolicyId, PolicyVersion = instance.PolicyVersion, PolicyContentHash = instance.PolicyContentHash,
        Severity = (int)instance.Severity, ProductionImpact = (int)instance.ProductionImpact,
        IsLatched = instance.IsLatched, Notification = (int)instance.Notification, PlcCode = instance.PlcCode,
        PlcPriority = instance.PlcPriority, ResetPrerequisites = (int)instance.ResetPrerequisites,
        FirstObservedAtUtc = instance.FirstObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        LastObservedAtUtc = instance.LastObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        SourceHealthy = instance.SourceHealthy, SourceRuntimeEpoch = instance.SourceRuntimeEpoch.ToString("D"),
        SourceObservationSequence = instance.SourceObservationSequence, Lifecycle = (int)instance.Lifecycle,
        Acknowledged = instance.Acknowledged, AcknowledgedBy = instance.AcknowledgedBy?.ToString("D"),
        AcknowledgedAtUtc = instance.AcknowledgedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        LastTransitionPosition = instance.LastTransitionPosition
    };

    private static AlarmPlcProjectionDto ToDto(AlarmPlcProjection projection) => new()
    {
        Entries = projection.Entries.Select(entry => new AlarmPlcEntryDto
        {
            InstanceId = entry.InstanceId.ToString("D"), Code = entry.Code, PlcCode = entry.PlcCode,
            Severity = (int)entry.Severity, ProductionImpact = (int)entry.ProductionImpact
        }).ToList(),
        TotalUncleared = projection.TotalUncleared, BlockingCount = projection.BlockingCount,
        FaultAbortPresent = projection.FaultAbortPresent
    };

    private static AlarmPolicy FromDto(AlarmPolicyDto dto)
    {
        if (dto.Rules is null) throw new InvalidOperationException("AlarmPolicyPayloadInvalid");
        var policy = new AlarmPolicy(dto.Id ?? string.Empty, dto.Version ?? string.Empty,
            dto.Rules.Select(rule => new AlarmPolicyRule(rule.Code ?? string.Empty, rule.Source ?? string.Empty,
                (AlarmSeverity)rule.Severity, (ProductionImpact)rule.ProductionImpact, rule.IsLatched,
                (AlarmNotification)rule.Notification, rule.PlcCode, rule.PlcPriority,
                (AlarmResetPrerequisites)rule.ResetPrerequisites)),
            TimeSpan.FromTicks(dto.SourceObservationFreshnessTicks), dto.MaximumActiveInstances, dto.MaximumPlcEntries);
        if (!string.Equals(dto.ContentHash, policy.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("AlarmPolicyHashMismatch");
        return policy;
    }

    private static AlarmHistoryRecord FromDto(AlarmHistoryRecordDto dto)
    {
        if (!Guid.TryParseExact(dto.EventId, "D", out var eventId)) throw new InvalidOperationException("AlarmHistoryPayloadInvalid");
        return new AlarmHistoryRecord(dto.Position, eventId, ParseGuid(dto.InstanceId), dto.Code, dto.Source,
            ParseEnum<AlarmTransitionKind>(dto.Transition), ParseTime(dto.ObservedAtUtc), ParseTime(dto.AuditAtUtc),
            ParseGuid(dto.ActorPrincipalId), ParseGuid(dto.SessionId), ParseGuid(dto.CommandCorrelationId),
            dto.ReasonCode ?? string.Empty, dto.Instance is null ? null : FromDto(dto.Instance),
            dto.PlcProjection is null ? null : FromDto(dto.PlcProjection));
    }

    private static AlarmInstanceSnapshot FromDto(AlarmInstanceDto dto) => new(
        ParseRequiredGuid(dto.InstanceId), dto.Code ?? string.Empty, dto.Source ?? string.Empty,
        dto.PolicyId ?? string.Empty, dto.PolicyVersion ?? string.Empty, dto.PolicyContentHash ?? string.Empty,
        ParseEnum<AlarmSeverity>(dto.Severity), ParseEnum<ProductionImpact>(dto.ProductionImpact), dto.IsLatched,
        ParseEnum<AlarmNotification>(dto.Notification), dto.PlcCode, dto.PlcPriority,
        (AlarmResetPrerequisites)dto.ResetPrerequisites, ParseTime(dto.FirstObservedAtUtc),
        ParseTime(dto.LastObservedAtUtc), dto.SourceHealthy, ParseRequiredGuid(dto.SourceRuntimeEpoch),
        dto.SourceObservationSequence, ParseEnum<AlarmLifecycle>(dto.Lifecycle), dto.Acknowledged,
        ParseGuid(dto.AcknowledgedBy), ParseNullableTime(dto.AcknowledgedAtUtc), dto.LastTransitionPosition);

    private static AlarmPlcProjection FromDto(AlarmPlcProjectionDto dto)
    {
        if (dto.Entries is null) throw new InvalidOperationException("AlarmHistoryPayloadInvalid");
        return new AlarmPlcProjection(dto.Entries.Select(entry => new AlarmPlcEntry(ParseRequiredGuid(entry.InstanceId),
            entry.Code ?? string.Empty, entry.PlcCode, ParseEnum<AlarmSeverity>(entry.Severity),
            ParseEnum<ProductionImpact>(entry.ProductionImpact))), dto.TotalUncleared, dto.BlockingCount, dto.FaultAbortPresent);
    }

    private static void ValidateRecord(AlarmHistoryRecord record, long expectedPosition, AlarmPolicy? policy)
    {
        if (record.Position != expectedPosition || record.EventId == Guid.Empty || record.ReasonCode.Length is < 1 or > 128 ||
            record.ReasonCode.Any(char.IsControl) || !Enum.IsDefined(record.Transition) ||
            record.ObservedAtUtc == default || record.AuditAtUtc == default)
            throw new InvalidOperationException("AlarmHistoryShapeInvalid");
        ValidateIdentifier(record.Code, nullable: true);
        ValidateIdentifier(record.Source, nullable: true);
        if (record.Instance is { } instance)
        {
            if (instance.InstanceId == Guid.Empty || instance.LastTransitionPosition != expectedPosition ||
                instance.SourceRuntimeEpoch == Guid.Empty || instance.SourceObservationSequence < 0 ||
                !AuditCanonical.IsHash(instance.PolicyContentHash) || !Enum.IsDefined(instance.Severity) ||
                !Enum.IsDefined(instance.ProductionImpact) || !Enum.IsDefined(instance.Notification) ||
                !Enum.IsDefined(instance.Lifecycle) || instance.FirstObservedAtUtc == default || instance.LastObservedAtUtc == default)
                throw new InvalidOperationException("AlarmHistoryShapeInvalid");
            ValidateIdentifier(instance.Code, false);
            ValidateIdentifier(instance.Source, false);
            ValidateIdentifier(instance.PolicyId, false);
            ValidateIdentifier(instance.PolicyVersion, false);
        }
        if (record.Transition == AlarmTransitionKind.PolicyActivated)
        {
            if (record.Instance is not null || policy is null || record.Code is not null || record.Source is not null)
                throw new InvalidOperationException("AlarmPolicyActivationInvalid");
        }
        else if (policy is not null)
            throw new InvalidOperationException("AlarmPolicyActivationUnexpected");
        if (record.Transition is not (AlarmTransitionKind.PolicyActivated or AlarmTransitionKind.BoundaryRejected or AlarmTransitionKind.ProjectionChanged) &&
            record.Instance is null)
            throw new InvalidOperationException("AlarmInstanceRequired");
        if (record.Transition == AlarmTransitionKind.BoundaryRejected && record.Instance is not null)
            throw new InvalidOperationException("AlarmBoundaryInstanceForbidden");
        if (record.PlcProjection is { } projection && projection.Entries.Count > 16)
            throw new InvalidOperationException("AlarmHistoryShapeInvalid");
    }

    private static void ValidateIdentifier(string? value, bool nullable)
    {
        if (value is null && nullable) return;
        if (value is null || value.Length is < 1 or > 64 || value.Any(character => character is < '!' or > '~'))
            throw new InvalidOperationException("AlarmHistoryIdentifierInvalid");
    }

    private static Guid? ParseGuid(string? value) => value is null ? null : ParseRequiredGuid(value);
    private static Guid ParseRequiredGuid(string? value) => Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty
        ? result : throw new InvalidOperationException("AlarmHistoryGuidInvalid");
    private static DateTimeOffset ParseTime(string? value) => DateTimeOffset.TryParseExact(value, "O",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) && result != default
        ? result : throw new InvalidOperationException("AlarmHistoryTimeInvalid");
    private static DateTimeOffset? ParseNullableTime(string? value) => value is null ? null : ParseTime(value);
    private static T ParseEnum<T>(int value) where T : struct, Enum => Enum.IsDefined(typeof(T), value)
        ? (T)Enum.ToObject(typeof(T), value) : throw new InvalidOperationException("AlarmHistoryEnumInvalid");

    private sealed class AlarmHistoryEnvelopeDto
    {
        public int FormatVersion { get; set; }
        public string? RuntimeEpoch { get; set; }
        public string? SystemPrincipalId { get; set; }
        public AlarmHistoryRecordDto? Record { get; set; }
        public AlarmPolicyDto? Policy { get; set; }
    }

    private sealed class AlarmHistoryRecordDto
    {
        public long Position { get; set; }
        public string? EventId { get; set; }
        public string? InstanceId { get; set; }
        public string? Code { get; set; }
        public string? Source { get; set; }
        public int Transition { get; set; }
        public string? ObservedAtUtc { get; set; }
        public string? AuditAtUtc { get; set; }
        public string? ActorPrincipalId { get; set; }
        public string? SessionId { get; set; }
        public string? CommandCorrelationId { get; set; }
        public string? ReasonCode { get; set; }
        public AlarmInstanceDto? Instance { get; set; }
        public AlarmPlcProjectionDto? PlcProjection { get; set; }
    }

    private sealed class AlarmInstanceDto
    {
        public string? InstanceId { get; set; }
        public string? Code { get; set; }
        public string? Source { get; set; }
        public string? PolicyId { get; set; }
        public string? PolicyVersion { get; set; }
        public string? PolicyContentHash { get; set; }
        public int Severity { get; set; }
        public int ProductionImpact { get; set; }
        public bool IsLatched { get; set; }
        public int Notification { get; set; }
        public ushort? PlcCode { get; set; }
        public int PlcPriority { get; set; }
        public int ResetPrerequisites { get; set; }
        public string? FirstObservedAtUtc { get; set; }
        public string? LastObservedAtUtc { get; set; }
        public bool SourceHealthy { get; set; }
        public string? SourceRuntimeEpoch { get; set; }
        public long SourceObservationSequence { get; set; }
        public int Lifecycle { get; set; }
        public bool Acknowledged { get; set; }
        public string? AcknowledgedBy { get; set; }
        public string? AcknowledgedAtUtc { get; set; }
        public long LastTransitionPosition { get; set; }
    }

    private sealed class AlarmPlcProjectionDto
    {
        public List<AlarmPlcEntryDto>? Entries { get; set; }
        public int TotalUncleared { get; set; }
        public int BlockingCount { get; set; }
        public bool FaultAbortPresent { get; set; }
    }

    private sealed class AlarmPlcEntryDto
    {
        public string? InstanceId { get; set; }
        public string? Code { get; set; }
        public ushort PlcCode { get; set; }
        public int Severity { get; set; }
        public int ProductionImpact { get; set; }
    }

    private sealed class AlarmPolicyDto
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? ContentHash { get; set; }
        public long SourceObservationFreshnessTicks { get; set; }
        public int MaximumActiveInstances { get; set; }
        public int MaximumPlcEntries { get; set; }
        public List<AlarmPolicyRuleDto>? Rules { get; set; }
    }

    private sealed class AlarmPolicyRuleDto
    {
        public string? Code { get; set; }
        public string? Source { get; set; }
        public int Severity { get; set; }
        public int ProductionImpact { get; set; }
        public bool IsLatched { get; set; }
        public int Notification { get; set; }
        public ushort? PlcCode { get; set; }
        public int PlcPriority { get; set; }
        public int ResetPrerequisites { get; set; }
    }
}

internal sealed partial class SqliteCommandStore
{
    private const string AlarmSchemaSql = @"
        CREATE TABLE alarm_policy_versions(
            PolicyId TEXT NOT NULL CHECK(length(PolicyId)>0 AND length(PolicyId)<=64),
            Version TEXT NOT NULL CHECK(length(Version)>0 AND length(Version)<=64),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            CanonicalJson TEXT NOT NULL CHECK(length(CanonicalJson)>0 AND length(CanonicalJson)<=131072),
            ActivatedAtUtc TEXT NOT NULL,
            PRIMARY KEY(PolicyId,Version), UNIQUE(ContentHash));
        CREATE TABLE alarm_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            SystemPrincipalId TEXT NOT NULL CHECK(SystemPrincipalId='SharpInspect.Runtime'),
            Transition INTEGER NOT NULL CHECK(Transition IN (0,1,2,3,4,5,6,7,8)),
            ObservedAtUtc TEXT NOT NULL,
            AuditAtUtc TEXT NOT NULL,
            InstanceId TEXT NULL CHECK(InstanceId IS NULL OR length(InstanceId)=36),
            Code TEXT NULL,
            Source TEXT NULL,
            ActorPrincipalId TEXT NULL CHECK(ActorPrincipalId IS NULL OR length(ActorPrincipalId)=36),
            SessionId TEXT NULL CHECK(SessionId IS NULL OR length(SessionId)=36),
            CommandCorrelationId TEXT NULL CHECK(CommandCorrelationId IS NULL OR length(CommandCorrelationId)=36),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0 AND length(ReasonCode)<=128),
            Payload TEXT NOT NULL CHECK(length(Payload)>0 AND length(Payload)<=180000),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64));
        CREATE TRIGGER alarm_policy_immutable_update BEFORE UPDATE ON alarm_policy_versions BEGIN SELECT RAISE(ABORT,'ImmutableAlarmPolicy'); END;
        CREATE TRIGGER alarm_policy_immutable_delete BEFORE DELETE ON alarm_policy_versions BEGIN SELECT RAISE(ABORT,'ImmutableAlarmPolicy'); END;
        CREATE TRIGGER alarm_events_immutable_update BEFORE UPDATE ON alarm_events BEGIN SELECT RAISE(ABORT,'ImmutableAlarmEvent'); END;
        CREATE TRIGGER alarm_events_immutable_delete BEFORE DELETE ON alarm_events BEGIN SELECT RAISE(ABORT,'ImmutableAlarmEvent'); END;
        CREATE INDEX ix_alarm_events_instance ON alarm_events(InstanceId,Position);
        CREATE INDEX ix_alarm_events_code ON alarm_events(Code,Position);
        CREATE INDEX ix_alarm_events_transition ON alarm_events(Transition,Position);
        CREATE INDEX ix_alarm_events_boundary ON alarm_events(Transition,ReasonCode,Position);";

    internal SharpInspect.Abstractions.AlarmPolicy? ConfiguredAlarmPolicy => _options.AlarmPolicy;

    private void InitializeAlarmSchema(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        var policy = _options.AlarmPolicy;
        if (policy is null) return;
        var json = AlarmStorageCodec.EncodePolicyJson(policy);
        var activatedAt = DateTimeOffset.UtcNow;
        AuditChainDatabase.Execute(database, @"INSERT INTO alarm_policy_versions(PolicyId,Version,ContentHash,CanonicalJson,ActivatedAtUtc)
            VALUES(?,?,?,?,?);", deadline, policy.Id, policy.Version, policy.ContentHash, json,
            activatedAt.ToString("O", CultureInfo.InvariantCulture));
        var record = new AlarmHistoryRecord(0, Guid.NewGuid(), null, null, null,
            AlarmTransitionKind.PolicyActivated, activatedAt, activatedAt,
            null, null, null, "AlarmPolicyActivated", null, null);
        var position = AlarmStorageCodec.AppendEvent(database, _policy!, _signingKey!, Guid.Empty,
            record, policy, null, deadline);
        AuditChainDatabase.Require(position == 1, "AlarmPolicyActivationPositionInvalid");
    }

    internal async ValueTask<AlarmStateSnapshot> ReadAlarmStateAsync(Guid runtimeEpoch,
        CancellationToken cancellationToken = default)
    {
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("AlarmRuntimeEpochRequired", nameof(runtimeEpoch));
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || _policy is null || _signingKey is null)
            throw new InvalidOperationException("AlarmStoreUnavailable");
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath!, true);
            AlarmStorageCodec.ConfigureSqliteLimit(connection.Handle!);
            var database = connection.Handle!;
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            var committed = false;
            try
            {
                var alarmStore = _options.AlarmPolicy is not null;
                var verification = AuditChainDatabase.Verify(database, _policy, _signingKey.KeyId,
                    _signingKey.PublicKeyBase64,
                    alarmStore ? new AuditVerificationRequest(0, _policy.MaximumVerificationEntries) :
                        new AuditVerificationRequest(), !alarmStore, deadline,
                    validateAnchorReceipt: false);
                if (alarmStore) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
                var policy = AlarmStorageCodec.ReadPersistedPolicy(database, deadline);
                AlarmStorageCodec.RequireConfiguredPolicy(policy, _options.AlarmPolicy);
                var events = AlarmStorageCodec.ReadEvents(database, deadline);
                var state = AlarmStorageCodec.BuildState(policy, events, runtimeEpoch);
                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                committed = true;
                return state;
            }
            finally
            {
                if (!committed) try { SQLitePCL.raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<IdentityWriteResult> UpdateAlarmObservationAsync(Guid runtimeEpoch,
        Func<AlarmStateSnapshot, AlarmObservationUpdate> update, CancellationToken cancellationToken,
        StoreDeadline? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (runtimeEpoch == Guid.Empty) return ValueTask.FromResult(new IdentityWriteResult(false, "AlarmRuntimeEpochRequired"));
        if (_options.LocalIdentity is null || _queue is null || _queueSlots is null || Volatile.Read(ref _disposed) != 0)
            return ValueTask.FromResult(new IdentityWriteResult(false, "AlarmStoreUnavailable"));
        return EnqueueAlarmObservationAsync(new AlarmObservationWork(runtimeEpoch, update), cancellationToken, deadline);
    }

    private async ValueTask<IdentityWriteResult> EnqueueAlarmObservationAsync(AlarmObservationWork work,
        CancellationToken cancellationToken, StoreDeadline? commandDeadline)
    {
        var deadline = commandDeadline ?? new StoreDeadline(CommitTimeout);
        var result = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline, AlarmObservation: work),
            deadline, cancellationToken, "AlarmStoreUnavailable", "AlarmCommitDeadlineExceeded").ConfigureAwait(false);
        return new(result.Committed, result.ReasonCode, result.Committed ? work.Result : null);
    }

    private StoreWriteResult UpdateAlarmObservationCore(SQLitePCL.sqlite3 database, AlarmObservationWork work,
        StoreDeadline deadline)
    {
        var integrity = Integrity;
        if (integrity?.State != AuditIntegrityState.Verified)
            return new(false, integrity?.ReasonCode ?? "AlarmAuditUnavailable",
                RetryAfterIntegrityRecheck: integrity?.State == AuditIntegrityState.Verifying);
        var committed = false;
        SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
        try
        {
            var alarmStore = _options.AlarmPolicy is not null;
            var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
                _signingKey.PublicKeyBase64,
                alarmStore ? new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries) :
                    new AuditVerificationRequest(), !alarmStore, deadline,
                validateAnchorReceipt: false);
            if (alarmStore) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            var persistedPolicy = AlarmStorageCodec.ReadPersistedPolicy(database, deadline);
            AlarmStorageCodec.RequireConfiguredPolicy(persistedPolicy, _options.AlarmPolicy);
            var before = AlarmStorageCodec.BuildState(persistedPolicy,
                AlarmStorageCodec.ReadEvents(database, deadline), work.RuntimeEpoch);
            var evaluated = work.Update(before);
            if (evaluated.Events.Count == 0)
            {
                SqliteNative.Execute(database, "ROLLBACK;", deadline);
                committed = true;
                work.Result = evaluated.Result;
                return new(true, "AlarmObservationNoChange");
            }
            if (evaluated.Events.Count > 8) throw new InvalidOperationException("AlarmEventLimit");
            AppendAlarmEvents(database, evaluated.Events, work.RuntimeEpoch, null, deadline);
            var tail = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Result = evaluated.Result;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, tail);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying, "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new(true, "AlarmTransactionPersisted");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is InvalidOperationException invalid &&
                (invalid.Message.StartsWith("Alarm", StringComparison.Ordinal) ||
                 invalid.Message.StartsWith("Audit", StringComparison.Ordinal) ||
                 invalid.Message is "TraceStoreWalLimit")
                ? invalid.Message : "AlarmCommitFailed";
            return new(false, reason);
        }
        finally
        {
            if (!committed) Rollback(database);
        }
    }

    private void AppendAlarmEvents(SQLitePCL.sqlite3 database, IReadOnlyList<AlarmHistoryRecord> events,
        Guid runtimeEpoch, Guid? expectedCorrelation, StoreDeadline deadline)
    {
        if (events.Count is < 1 or > 8) throw new InvalidOperationException("AlarmEventLimit");
        foreach (var item in events)
            _ = AlarmStorageCodec.AppendEvent(database, _policy!, _signingKey!, runtimeEpoch, item, null,
                expectedCorrelation, deadline);
    }
}
