using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Closed classification and semantic constraints for one event property.</summary>
public sealed class DiagnosticFieldContract
{
    public DiagnosticFieldContract(string name, DiagnosticScalarKind kind, DiagnosticDataClass classification,
        bool required, double? minimum, double? maximum, IEnumerable<string>? symbols)
    {
        Name = AlgorithmContractValidation.Identifier(name, nameof(name));
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(classification))
            throw new ArgumentException("DiagnosticClassificationInvalid");
        Kind = kind; Classification = classification; Required = required;
        if (kind is DiagnosticScalarKind.Int64 or DiagnosticScalarKind.Number)
        {
            if (minimum is null || maximum is null || !double.IsFinite(minimum.Value) ||
                !double.IsFinite(maximum.Value) || minimum > maximum)
                throw new ArgumentException("DiagnosticNumericRangeRequired");
            if (kind == DiagnosticScalarKind.Int64 && (minimum < long.MinValue || maximum >= 9223372036854775808d ||
                Math.Truncate(minimum.Value) != minimum.Value || Math.Truncate(maximum.Value) != maximum.Value))
                throw new ArgumentException("DiagnosticIntegerRangeInvalid");
        }
        else if (minimum is not null || maximum is not null)
            throw new ArgumentException("DiagnosticNumericRangeUnexpected");
        Minimum = minimum; Maximum = maximum;
        MinimumInteger = kind == DiagnosticScalarKind.Int64 ? (long)minimum!.Value : null;
        MaximumInteger = kind == DiagnosticScalarKind.Int64 ? (long)maximum!.Value : null;
        var copied = AlgorithmContractValidation.Copy(symbols, nameof(symbols), 128);
        if (kind == DiagnosticScalarKind.Symbol && copied.Count == 0 || kind != DiagnosticScalarKind.Symbol && copied.Count != 0)
            throw new ArgumentException("DiagnosticFiniteSymbolsRequired");
        foreach (var value in copied) AlgorithmContractValidation.Identifier(value, nameof(symbols));
        if (copied.Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("DiagnosticSymbolDuplicate");
        Symbols = Array.AsReadOnly(copied.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "diagnostic-field-v1", Name, Kind.ToString(),
            Classification.ToString(), Required.ToString(), minimum?.ToString("R", CultureInfo.InvariantCulture),
            maximum?.ToString("R", CultureInfo.InvariantCulture) }.Concat(Symbols));
    }
    public string Name { get; }
    public DiagnosticScalarKind Kind { get; }
    public DiagnosticDataClass Classification { get; }
    public bool Required { get; }
    public double? Minimum { get; }
    public double? Maximum { get; }
    public long? MinimumInteger { get; }
    public long? MaximumInteger { get; }
    public ReadOnlyCollection<string> Symbols { get; }
    public string ContentHash { get; }
}

public sealed class DiagnosticEventContract
{
    public DiagnosticEventContract(string code, int schemaVersion, string component, DiagnosticLevel level,
        bool algorithmAllowed, IEnumerable<DiagnosticFieldContract> fields)
    {
        Code = AlgorithmContractValidation.Identifier(code, nameof(code));
        Component = AlgorithmContractValidation.Identifier(component, nameof(component));
        if (schemaVersion <= 0 || !Enum.IsDefined(level)) throw new ArgumentException("DiagnosticEventContractInvalid");
        SchemaVersion = schemaVersion; Level = level; AlgorithmAllowed = algorithmAllowed;
        var copied = AlgorithmContractValidation.Copy(fields, nameof(fields), 32);
        if (copied.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("DiagnosticFieldDuplicate");
        Fields = Array.AsReadOnly(copied.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "diagnostic-event-contract-v1", Code,
            schemaVersion.ToString(CultureInfo.InvariantCulture), Component, level.ToString(), algorithmAllowed.ToString() }
            .Concat(Fields.Select(value => value.ContentHash)));
    }
    public string Code { get; }
    public int SchemaVersion { get; }
    public string Component { get; }
    public DiagnosticLevel Level { get; }
    public bool AlgorithmAllowed { get; }
    public ReadOnlyCollection<DiagnosticFieldContract> Fields { get; }
    public string ContentHash { get; }
}

public sealed class DiagnosticProducerBudget
{
    public DiagnosticProducerBudget(int maximumEvents, long maximumBytes, int maximumProperties, int maximumSymbolBytes,
        int maximumEventsPerWindow, TimeSpan rateWindow, int maximumActiveExecutionScopes)
    {
        if (maximumEvents is < 1 or > 1_000_000 || maximumBytes is < 1 or > 4_294_967_296L ||
            maximumProperties is < 0 or > 32 || maximumSymbolBytes is < 1 or > 128 ||
            maximumEventsPerWindow is < 1 or > 1_000_000 || rateWindow <= TimeSpan.Zero || rateWindow > TimeSpan.FromHours(1) ||
            maximumActiveExecutionScopes is < 1 or > 1024) throw new ArgumentException("DiagnosticProducerBudgetInvalid");
        MaximumEvents = maximumEvents; MaximumBytes = maximumBytes; MaximumProperties = maximumProperties;
        MaximumSymbolBytes = maximumSymbolBytes; MaximumEventsPerWindow = maximumEventsPerWindow;
        RateWindow = rateWindow; MaximumActiveExecutionScopes = maximumActiveExecutionScopes;
        ContentHash = DiagnosticPolicyHash.Of("diagnostic-producer-budget-v1", maximumEvents, maximumBytes,
            maximumProperties, maximumSymbolBytes, maximumEventsPerWindow, rateWindow.Ticks, maximumActiveExecutionScopes);
    }
    public int MaximumEvents { get; }
    public long MaximumBytes { get; }
    public int MaximumProperties { get; }
    public int MaximumSymbolBytes { get; }
    public int MaximumEventsPerWindow { get; }
    public TimeSpan RateWindow { get; }
    public int MaximumActiveExecutionScopes { get; }
    public string ContentHash { get; }
}

public sealed class DiagnosticQueueBudget
{
    public DiagnosticQueueBudget(int normalRecords, long normalBytes, int reservedRecords, long reservedBytes,
        DiagnosticLevel reservationLevel, TimeSpan writeTimeout)
    {
        if (normalRecords is < 1 or > 65536 || reservedRecords is < 1 or > 65536 ||
            normalBytes is < 1 or > 4_294_967_296L || reservedBytes is < 1 or > 4_294_967_296L ||
            reservationLevel < DiagnosticLevel.Warning || !Enum.IsDefined(reservationLevel) ||
            writeTimeout <= TimeSpan.Zero || writeTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentException("DiagnosticQueueBudgetInvalid");
        NormalRecords = normalRecords; NormalBytes = normalBytes; ReservedRecords = reservedRecords;
        ReservedBytes = reservedBytes; ReservationLevel = reservationLevel; WriteTimeout = writeTimeout;
        ContentHash = DiagnosticPolicyHash.Of("diagnostic-queue-budget-v1", normalRecords, normalBytes,
            reservedRecords, reservedBytes, (int)reservationLevel, writeTimeout.Ticks);
    }
    public int NormalRecords { get; }
    public long NormalBytes { get; }
    public int ReservedRecords { get; }
    public long ReservedBytes { get; }
    public DiagnosticLevel ReservationLevel { get; }
    public TimeSpan WriteTimeout { get; }
    public string ContentHash { get; }
}

public sealed class DiagnosticFileBudget
{
    public DiagnosticFileBudget(long maximumRecordBytes, long maximumFileBytes, int maximumFiles,
        long maximumTotalBytes, TimeSpan rollAfter, TimeSpan retention)
    {
        if (maximumRecordBytes is < 256 or > 1024 * 1024 || maximumFileBytes < maximumRecordBytes ||
            maximumFileBytes > 1L << 32 || maximumFiles is < 1 or > 4096 || maximumTotalBytes < maximumFileBytes ||
            maximumTotalBytes > 1L << 40 || rollAfter <= TimeSpan.Zero || rollAfter > TimeSpan.FromDays(1) ||
            retention < rollAfter || retention > TimeSpan.FromDays(3650))
            throw new ArgumentException("DiagnosticFileBudgetInvalid");
        MaximumRecordBytes = maximumRecordBytes; MaximumFileBytes = maximumFileBytes; MaximumFiles = maximumFiles;
        MaximumTotalBytes = maximumTotalBytes; RollAfter = rollAfter; Retention = retention;
        ContentHash = DiagnosticPolicyHash.Of("diagnostic-file-budget-v1", maximumRecordBytes, maximumFileBytes,
            maximumFiles, maximumTotalBytes, rollAfter.Ticks, retention.Ticks);
    }
    public long MaximumRecordBytes { get; }
    public long MaximumFileBytes { get; }
    public int MaximumFiles { get; }
    public long MaximumTotalBytes { get; }
    public TimeSpan RollAfter { get; }
    public TimeSpan Retention { get; }
    public string ContentHash { get; }
}

/// <summary>Explicit immutable deployment policy; arbitrary text is not an admissible scalar.</summary>
public sealed class LoggingDiagnosticsPolicy
{
    public LoggingDiagnosticsPolicy(string id, string version, string approvalReference, string tracePolicyVersion,
        string tracePolicySnapshotHash, DiagnosticLevel baseline, IEnumerable<DiagnosticEventContract> contracts,
        DiagnosticProducerBudget producers, DiagnosticQueueBudget safeQueue, DiagnosticQueueBudget protectedQueue,
        DiagnosticQueueBudget forwardedQueue, DiagnosticFileBudget safeFiles, DiagnosticFileBudget protectedFiles,
        int maximumDropsPerHealthWindow, TimeSpan healthWindow, TimeSpan flushTimeout,
        int maximumQueryRecords, int maximumQueryBytes, TimeSpan maximumCaptureDuration, int maximumCaptureEvents)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        ApprovalReference = AlgorithmContractValidation.Identifier(approvalReference, nameof(approvalReference));
        TracePolicyVersion = AlgorithmContractValidation.Identifier(tracePolicyVersion, nameof(tracePolicyVersion));
        TracePolicySnapshotHash = TraceStoragePolicyValidation.Hash(tracePolicySnapshotHash, nameof(tracePolicySnapshotHash));
        if (!Enum.IsDefined(baseline) || baseline < DiagnosticLevel.Information)
            throw new ArgumentException("DiagnosticPermanentTraceDebugForbidden");
        Baseline = baseline;
        var copied = AlgorithmContractValidation.Copy(contracts, nameof(contracts), 256);
        if (copied.Count == 0 || copied.Select(value => (value.Code, value.SchemaVersion)).Distinct().Count() != copied.Count)
            throw new ArgumentException("DiagnosticContractCatalogInvalid");
        Contracts = Array.AsReadOnly(copied.OrderBy(value => value.Code, StringComparer.Ordinal).ThenBy(value => value.SchemaVersion).ToArray());
        Producers = producers ?? throw new ArgumentNullException(nameof(producers));
        SafeQueue = safeQueue ?? throw new ArgumentNullException(nameof(safeQueue));
        ProtectedQueue = protectedQueue ?? throw new ArgumentNullException(nameof(protectedQueue));
        ForwardedQueue = forwardedQueue ?? throw new ArgumentNullException(nameof(forwardedQueue));
        SafeFiles = safeFiles ?? throw new ArgumentNullException(nameof(safeFiles));
        ProtectedFiles = protectedFiles ?? throw new ArgumentNullException(nameof(protectedFiles));
        if (protectedFiles.Retention >= safeFiles.Retention || maximumDropsPerHealthWindow < 1 ||
            healthWindow <= TimeSpan.Zero || healthWindow > TimeSpan.FromHours(1) ||
            flushTimeout <= TimeSpan.Zero || flushTimeout > TimeSpan.FromSeconds(30) ||
            maximumQueryRecords is < 1 or > 1000 || maximumQueryBytes is < 256 or > 4 * 1024 * 1024 ||
            maximumCaptureDuration <= TimeSpan.Zero || maximumCaptureDuration > TimeSpan.FromHours(1) ||
            maximumCaptureEvents is < 1 or > 1_000_000)
            throw new ArgumentException("DiagnosticPolicyBoundsInvalid");
        if (new[] { safeQueue.NormalBytes, safeQueue.ReservedBytes, forwardedQueue.NormalBytes, forwardedQueue.ReservedBytes }
                .Any(value => value < safeFiles.MaximumRecordBytes) ||
            new[] { protectedQueue.NormalBytes, protectedQueue.ReservedBytes }.Any(value => value < protectedFiles.MaximumRecordBytes))
            throw new ArgumentException("DiagnosticRecordDoesNotFitQueue");
        MaximumDropsPerHealthWindow = maximumDropsPerHealthWindow; HealthWindow = healthWindow; FlushTimeout = flushTimeout;
        MaximumQueryRecords = maximumQueryRecords; MaximumQueryBytes = maximumQueryBytes;
        MaximumCaptureDuration = maximumCaptureDuration; MaximumCaptureEvents = maximumCaptureEvents;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "logging-diagnostics-policy-v1", Id, Version,
            ApprovalReference, TracePolicyVersion, TracePolicySnapshotHash, baseline.ToString(), producers.ContentHash,
            safeQueue.ContentHash, protectedQueue.ContentHash, forwardedQueue.ContentHash, safeFiles.ContentHash,
            protectedFiles.ContentHash, DiagnosticPolicyHash.Of("policy-limits", maximumDropsPerHealthWindow,
                healthWindow.Ticks, flushTimeout.Ticks, maximumQueryRecords, maximumQueryBytes,
                maximumCaptureDuration.Ticks, maximumCaptureEvents) }.Concat(Contracts.Select(value => value.ContentHash)));
    }
    public string Id { get; }
    public string Version { get; }
    public string ApprovalReference { get; }
    public string TracePolicyVersion { get; }
    public string TracePolicySnapshotHash { get; }
    public DiagnosticLevel Baseline { get; }
    public ReadOnlyCollection<DiagnosticEventContract> Contracts { get; }
    public DiagnosticProducerBudget Producers { get; }
    public DiagnosticQueueBudget SafeQueue { get; }
    public DiagnosticQueueBudget ProtectedQueue { get; }
    public DiagnosticQueueBudget ForwardedQueue { get; }
    public DiagnosticFileBudget SafeFiles { get; }
    public DiagnosticFileBudget ProtectedFiles { get; }
    public int MaximumDropsPerHealthWindow { get; }
    public TimeSpan HealthWindow { get; }
    public TimeSpan FlushTimeout { get; }
    public int MaximumQueryRecords { get; }
    public int MaximumQueryBytes { get; }
    public TimeSpan MaximumCaptureDuration { get; }
    public int MaximumCaptureEvents { get; }
    public string ContentHash { get; }
}

internal static class DiagnosticPolicyHash
{
    internal static string Of(string kind, params long[] values) => AlgorithmContractValidation.HashParts(
        new[] { kind }.Concat(values.Select(value => value.ToString(CultureInfo.InvariantCulture))));
}
