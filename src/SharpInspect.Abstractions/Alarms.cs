using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace SharpInspect.Abstractions;

public enum AlarmSeverity { Info, Warning, Error, Critical }
public enum ProductionImpact { None, BlockNewTriggers, FaultAbort }
public enum AlarmNotification { None, UntilAcknowledged, UntilCleared }

[Flags]
public enum AlarmResetPrerequisites
{
    None = 0,
    NoActiveExecution = 1,
    NoPendingDelivery = 2,
    RecoveryComplete = 4,
    NoExclusiveMode = 8
}

public enum AlarmLifecycle { Active, RecoveredLatched, Cleared }
public enum AlarmTransitionKind
{
    PolicyActivated,
    Raised,
    Observed,
    SourceRecovered,
    Acknowledged,
    Reset,
    Cleared,
    BoundaryRejected,
    ProjectionChanged
}

/// <summary>One immutable, explicitly mapped alarm rule. Severity and production impact are independent.</summary>
public sealed record AlarmPolicyRule(
    string Code,
    string Source,
    AlarmSeverity Severity,
    ProductionImpact ProductionImpact,
    bool IsLatched,
    AlarmNotification Notification,
    ushort? PlcCode,
    int PlcPriority = 0,
    AlarmResetPrerequisites ResetPrerequisites = AlarmResetPrerequisites.None);

/// <summary>Versioned alarm mapping. Unknown alarm codes have no implicit rule.</summary>
public sealed class AlarmPolicy
{
    private readonly ReadOnlyCollection<AlarmPolicyRule> _rules;
    private readonly IReadOnlyDictionary<string, AlarmPolicyRule> _rulesByCode;

    public AlarmPolicy(string id, string version, IEnumerable<AlarmPolicyRule> rules,
        TimeSpan sourceObservationFreshness, int maximumActiveInstances = 256, int maximumPlcEntries = 1)
    {
        Id = AlarmContractValidation.Identifier(id, nameof(id));
        Version = AlarmContractValidation.Identifier(version, nameof(version));
        ArgumentNullException.ThrowIfNull(rules);
        var copied = rules.ToArray();
        if (copied.Length is < 1 or > 256)
            throw new ArgumentException("AlarmPolicyRuleCapacityInvalid", nameof(rules));
        if (copied.Any(rule => rule is null))
            throw new ArgumentException("AlarmPolicyRuleInvalid", nameof(rules));
        _rules = new ReadOnlyCollection<AlarmPolicyRule>(copied);
        SourceObservationFreshness = sourceObservationFreshness;
        MaximumActiveInstances = maximumActiveInstances;
        MaximumPlcEntries = maximumPlcEntries;
        Validate();
        _rulesByCode = _rules.ToDictionary(rule => rule.Code, StringComparer.Ordinal);
        ContentHash = ComputeContentHash();
    }

    public string Id { get; }
    public string Version { get; }
    public IReadOnlyList<AlarmPolicyRule> Rules => _rules;
    public TimeSpan SourceObservationFreshness { get; }
    public int MaximumActiveInstances { get; }
    public int MaximumPlcEntries { get; }
    public string ContentHash { get; }

    public bool TryGetRule(string code, out AlarmPolicyRule? rule)
    {
        if (code is not null && _rulesByCode.TryGetValue(code, out var found))
        {
            rule = found;
            return true;
        }

        rule = null;
        return false;
    }

    public void Validate()
    {
        if (MaximumActiveInstances is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaximumActiveInstances));
        if (MaximumPlcEntries is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(MaximumPlcEntries));
        if (SourceObservationFreshness <= TimeSpan.Zero || SourceObservationFreshness > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(SourceObservationFreshness));

        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            AlarmContractValidation.Identifier(rule.Code, nameof(rule.Code));
            AlarmContractValidation.Identifier(rule.Source, nameof(rule.Source));
            if (!codes.Add(rule.Code))
                throw new ArgumentException("AlarmPolicyDuplicateCode", nameof(Rules));
            AlarmContractValidation.Enum(rule.Severity, nameof(rule.Severity));
            AlarmContractValidation.Enum(rule.ProductionImpact, nameof(rule.ProductionImpact));
            AlarmContractValidation.Enum(rule.Notification, nameof(rule.Notification));
            AlarmContractValidation.Flags(rule.ResetPrerequisites, nameof(rule.ResetPrerequisites));
            if (rule.PlcCode is 0)
                throw new ArgumentException("AlarmPolicyPlcCodeInvalid", nameof(rule.PlcCode));
            if (rule.PlcPriority is < 0 or > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(rule.PlcPriority));
        }
    }

    private string ComputeContentHash()
    {
        using var stream = new MemoryStream();
        AlarmContractValidation.WriteInt32(stream, 1);
        AlarmContractValidation.WriteString(stream, "AlarmPolicy");
        AlarmContractValidation.WriteString(stream, Id);
        AlarmContractValidation.WriteString(stream, Version);
        AlarmContractValidation.WriteInt64(stream, SourceObservationFreshness.Ticks);
        AlarmContractValidation.WriteInt32(stream, MaximumActiveInstances);
        AlarmContractValidation.WriteInt32(stream, MaximumPlcEntries);
        foreach (var rule in _rules.OrderBy(item => item.Code, StringComparer.Ordinal))
        {
            AlarmContractValidation.WriteString(stream, rule.Code);
            AlarmContractValidation.WriteString(stream, rule.Source);
            AlarmContractValidation.WriteInt32(stream, (int)rule.Severity);
            AlarmContractValidation.WriteInt32(stream, (int)rule.ProductionImpact);
            stream.WriteByte(rule.IsLatched ? (byte)1 : (byte)0);
            AlarmContractValidation.WriteInt32(stream, (int)rule.Notification);
            AlarmContractValidation.WriteOptionalUInt16(stream, rule.PlcCode);
            AlarmContractValidation.WriteInt32(stream, rule.PlcPriority);
            AlarmContractValidation.WriteInt32(stream, (int)rule.ResetPrerequisites);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}

/// <summary>Authoritative identity and observation projection for one alarm instance.</summary>
public sealed record AlarmInstanceSnapshot(
    Guid InstanceId,
    string Code,
    string Source,
    string PolicyId,
    string PolicyVersion,
    string PolicyContentHash,
    AlarmSeverity Severity,
    ProductionImpact ProductionImpact,
    bool IsLatched,
    AlarmNotification Notification,
    ushort? PlcCode,
    int PlcPriority,
    AlarmResetPrerequisites ResetPrerequisites,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    bool SourceHealthy,
    Guid SourceRuntimeEpoch,
    long SourceObservationSequence,
    AlarmLifecycle Lifecycle,
    bool Acknowledged,
    Guid? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAtUtc,
    long LastTransitionPosition);

public sealed record AlarmPlcEntry(
    Guid InstanceId,
    string Code,
    ushort PlcCode,
    AlarmSeverity Severity,
    ProductionImpact ProductionImpact);

/// <summary>Bounded PLC projection that never removes instances from the authoritative state.</summary>
public sealed class AlarmPlcProjection
{
    private readonly ReadOnlyCollection<AlarmPlcEntry> _entries;

    public AlarmPlcProjection(IEnumerable<AlarmPlcEntry> entries, int totalUncleared,
        int blockingCount, bool faultAbortPresent)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.ToArray();
        if (copied.Length > 16)
            throw new ArgumentException("AlarmPlcProjectionCapacityExceeded", nameof(entries));
        if (copied.Any(entry => entry is null))
            throw new ArgumentException("AlarmPlcEntryInvalid", nameof(entries));
        if (totalUncleared < copied.Length)
            throw new ArgumentOutOfRangeException(nameof(totalUncleared));
        if (blockingCount is < 0 || blockingCount > totalUncleared)
            throw new ArgumentOutOfRangeException(nameof(blockingCount));

        var ids = new HashSet<Guid>();
        foreach (var entry in copied)
        {
            if (entry.InstanceId == Guid.Empty || !ids.Add(entry.InstanceId))
                throw new ArgumentException("AlarmPlcEntryInstanceInvalid", nameof(entries));
            AlarmContractValidation.Identifier(entry.Code, nameof(entry.Code));
            if (entry.PlcCode == 0)
                throw new ArgumentException("AlarmPlcCodeInvalid", nameof(entries));
            AlarmContractValidation.Enum(entry.Severity, nameof(entry.Severity));
            AlarmContractValidation.Enum(entry.ProductionImpact, nameof(entry.ProductionImpact));
        }

        _entries = new ReadOnlyCollection<AlarmPlcEntry>(copied);
        TotalUncleared = totalUncleared;
        BlockingCount = blockingCount;
        FaultAbortPresent = faultAbortPresent;
    }

    public IReadOnlyList<AlarmPlcEntry> Entries => _entries;
    public int TotalUncleared { get; }
    public int BlockingCount { get; }
    public bool FaultAbortPresent { get; }
    public int HiddenCount => TotalUncleared - _entries.Count;
}

/// <summary>Complete alarm authority projection; callers must not filter its instances.</summary>
public sealed class AlarmStateSnapshot
{
    private readonly ReadOnlyCollection<AlarmInstanceSnapshot> _instances;

    public AlarmStateSnapshot(bool available, string reasonCode, Guid runtimeEpoch, long revision,
        AlarmPolicy? policy, IEnumerable<AlarmInstanceSnapshot> instances, AlarmPlcProjection plc)
    {
        if (reasonCode is null) throw new ArgumentNullException(nameof(reasonCode));
        if (reasonCode.Length is < 1 or > 128 || reasonCode.Any(char.IsControl))
            throw new ArgumentException("AlarmReasonCodeInvalid", nameof(reasonCode));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(plc);
        var copied = instances.ToArray();
        if (copied.Length > 256)
            throw new ArgumentException("AlarmInstanceCapacityExceeded", nameof(instances));
        if (copied.Any(instance => instance is null))
            throw new ArgumentException("AlarmInstanceInvalid", nameof(instances));
        if (copied.Select(instance => instance.InstanceId).Distinct().Count() != copied.Length)
            throw new ArgumentException("AlarmDuplicateInstance", nameof(instances));

        Available = available;
        ReasonCode = reasonCode;
        RuntimeEpoch = runtimeEpoch;
        Revision = revision;
        Policy = policy;
        _instances = new ReadOnlyCollection<AlarmInstanceSnapshot>(copied);
        Plc = plc;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public Guid RuntimeEpoch { get; }
    public long Revision { get; }
    public AlarmPolicy? Policy { get; }
    public IReadOnlyList<AlarmInstanceSnapshot> Instances => _instances;
    public AlarmPlcProjection Plc { get; }
}

public sealed record AlarmHistoryRecord(
    long Position,
    Guid EventId,
    Guid? InstanceId,
    string? Code,
    string? Source,
    AlarmTransitionKind Transition,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset AuditAtUtc,
    Guid? ActorPrincipalId,
    Guid? SessionId,
    Guid? CommandCorrelationId,
    string ReasonCode,
    AlarmInstanceSnapshot? Instance,
    AlarmPlcProjection? PlcProjection);

public sealed record AlarmHistoryFilter
{
    public AlarmHistoryFilter(Guid? instanceId = null, string? code = null,
        long afterPosition = 0, long? throughPosition = null, int pageSize = 100)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("AlarmInstanceIdInvalid", nameof(instanceId));
        if (code is not null) AlarmContractValidation.Identifier(code, nameof(code));
        if (afterPosition < 0) throw new ArgumentOutOfRangeException(nameof(afterPosition));
        if (throughPosition is < 0 || throughPosition < afterPosition)
            throw new ArgumentOutOfRangeException(nameof(throughPosition));
        if (pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        InstanceId = instanceId;
        Code = code;
        AfterPosition = afterPosition;
        ThroughPosition = throughPosition;
        PageSize = pageSize;
    }

    public Guid? InstanceId { get; }
    public string? Code { get; }
    public long AfterPosition { get; }
    public long? ThroughPosition { get; }
    public int PageSize { get; }
}

public sealed record AlarmHistoryPage
{
    public AlarmHistoryPage(IEnumerable<AlarmHistoryRecord> records, long throughPosition,
        long? nextAfterPosition, bool available, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (throughPosition < 0) throw new ArgumentOutOfRangeException(nameof(throughPosition));
        if (nextAfterPosition is < 0) throw new ArgumentOutOfRangeException(nameof(nextAfterPosition));
        if (reasonCode is null) throw new ArgumentNullException(nameof(reasonCode));
        if (reasonCode.Length is < 1 or > 128 || reasonCode.Any(char.IsControl))
            throw new ArgumentException("AlarmReasonCodeInvalid", nameof(reasonCode));
        var copied = records.ToArray();
        if (copied.Length > 100)
            throw new ArgumentException("AlarmHistoryPageCapacityExceeded", nameof(records));
        if (copied.Any(record => record is null))
            throw new ArgumentException("AlarmHistoryRecordInvalid", nameof(records));
        Records = new ReadOnlyCollection<AlarmHistoryRecord>(copied);
        ThroughPosition = throughPosition;
        NextAfterPosition = nextAfterPosition;
        Available = available;
        ReasonCode = reasonCode;
    }

    public ReadOnlyCollection<AlarmHistoryRecord> Records { get; }
    public long ThroughPosition { get; }
    public long? NextAfterPosition { get; }
    public bool Available { get; }
    public string ReasonCode { get; }
}

public interface IAlarmHistoryQuery
{
    ValueTask<AlarmHistoryPage> QueryAsync(AlarmHistoryFilter filter,
        CancellationToken cancellationToken = default);
}

internal static class AlarmContractValidation
{
    internal static string Identifier(string value, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length is < 1 or > 64 || value.Any(character =>
            !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("AlarmIdentifierInvalid", parameterName);
        return value;
    }

    internal static void Enum<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!System.Enum.IsDefined(typeof(T), value))
            throw new ArgumentException("AlarmEnumInvalid", parameterName);
    }

    internal static void Flags(AlarmResetPrerequisites value, string parameterName)
    {
        const AlarmResetPrerequisites known = AlarmResetPrerequisites.NoActiveExecution |
            AlarmResetPrerequisites.NoPendingDelivery | AlarmResetPrerequisites.RecoveryComplete |
            AlarmResetPrerequisites.NoExclusiveMode;
        if ((value & ~known) != 0)
            throw new ArgumentException("AlarmResetPrerequisitesInvalid", parameterName);
    }

    internal static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    internal static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    internal static void WriteString(Stream stream, string value)
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    internal static void WriteOptionalUInt16(Stream stream, ushort? value)
    {
        stream.WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value.Value);
            stream.Write(bytes);
        }
    }
}
