using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

public enum DiagnosticLevel : byte { Trace = 1, Debug = 2, Information = 3, Warning = 4, Error = 5, Critical = 6 }
public enum DiagnosticDataClass : byte { Safe = 1, Protected = 2, Prohibited = 3 }
public enum DiagnosticScalarKind : byte { Boolean = 1, Int64 = 2, Number = 3, Symbol = 4 }
public enum DiagnosticSinkState : byte { Starting = 1, Healthy = 2, Unavailable = 3, Retiring = 4, Stopped = 5 }
public enum DiagnosticEmission : byte { Accepted = 1, Dropped = 2 }

/// <summary>An unclassified input, never a serializable diagnostic record.</summary>
public sealed class DiagnosticPropertyRequest
{
    public DiagnosticPropertyRequest(string name, object? value)
    {
        Name = AlgorithmContractValidation.Identifier(name, nameof(name));
        Value = value;
    }
    public string Name { get; }
    // The classifier accepts exact closed scalar types. It never calls a getter,
    // formatter or ToString on this object, and never queues this request.
    public object? Value { get; }
}

/// <summary>Untrusted input to a Runtime-owned producer; carries no authority or correlation.</summary>
public sealed class DiagnosticEmissionRequest
{
    public DiagnosticEmissionRequest(string code, int schemaVersion, params DiagnosticPropertyRequest[] properties)
    {
        Code = AlgorithmContractValidation.Identifier(code, nameof(code));
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Length > 32 || properties.Any(value => value is null))
            throw new ArgumentException("DiagnosticPropertyCountInvalid", nameof(properties));
        SchemaVersion = schemaVersion;
        Properties = Array.AsReadOnly((DiagnosticPropertyRequest[])properties.Clone());
    }
    public string Code { get; }
    public int SchemaVersion { get; }
    public ReadOnlyCollection<DiagnosticPropertyRequest> Properties { get; }
}

/// <summary>Already classified scalar projection. No arbitrary object representation is available.</summary>
public sealed class DiagnosticScalar
{
    private DiagnosticScalar(DiagnosticScalarKind kind, bool boolean, long integer, double number, string? symbol)
    { Kind = kind; Boolean = boolean; Integer = integer; Number = number; Symbol = symbol; }
    public DiagnosticScalarKind Kind { get; }
    public bool Boolean { get; }
    public long Integer { get; }
    public double Number { get; }
    public string? Symbol { get; }
    public static DiagnosticScalar FromBoolean(bool value) => new(DiagnosticScalarKind.Boolean, value, 0, 0, null);
    public static DiagnosticScalar FromInt64(long value) => new(DiagnosticScalarKind.Int64, false, value, 0, null);
    public static DiagnosticScalar FromNumber(double value) => double.IsFinite(value)
        ? new(DiagnosticScalarKind.Number, false, 0, value, null)
        : throw new ArgumentOutOfRangeException(nameof(value));
    public static DiagnosticScalar FromSymbol(string value) => new(DiagnosticScalarKind.Symbol, false, 0, 0,
        AlgorithmContractValidation.Identifier(value, nameof(value)));
}

public sealed record DiagnosticProperty(string Name, DiagnosticScalar Value);

/// <summary>Bounded safe/protected query projection; Runtime authorizes the protected channel.</summary>
public sealed record DiagnosticRecord(Guid EventId, string Code, int SchemaVersion, DiagnosticLevel Level,
    string Component, Guid RuntimeEpoch, DateTimeOffset ObservedAtUtc, ExecutionCorrelationId? Execution,
    Guid? CommandCorrelationId, string PolicyHash, IReadOnlyList<DiagnosticProperty> Properties);

public sealed record DiagnosticSinkHealth(DiagnosticSinkState State, string ReasonCode,
    int QueuedRecords, long QueuedBytes, int PhysicalCalls, long Written, long Dropped, long Failures);

public sealed record DiagnosticPipelineHealthSnapshot(bool Configured, Guid RuntimeEpoch,
    string? PolicyHash, DateTimeOffset ObservedAtUtc, long Submitted, long Accepted,
    long Rejected, long QuotaDropped, long SaturationDropped, long LateDropped,
    int ActiveExecutionScopes, DiagnosticSinkHealth? Safe, DiagnosticSinkHealth? Protected,
    DiagnosticSinkHealth? Forwarded, bool Unhealthy, string ReasonCode);

public interface IDiagnosticPipelineHealthQuery
{
    DiagnosticPipelineHealthSnapshot ReadHealth();
}

public sealed record DiagnosticHistoryRequest(bool Protected, int MaximumRecords, int MaximumBytes,
    CommandInvocation Invocation);
public sealed record DiagnosticHistoryPage(bool Available, string ReasonCode,
    IReadOnlyList<DiagnosticRecord> Records, int OmittedRecords);
public interface IDiagnosticHistoryQuery
{
    ValueTask<DiagnosticHistoryPage> ReadAsync(DiagnosticHistoryRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Host-only fatal boundary. Implementations never continue after handling this notification.</summary>
public interface IManagedFaultBoundary
{
    ValueTask HandleAsync(Exception exception, CancellationToken cancellationToken = default);
}
