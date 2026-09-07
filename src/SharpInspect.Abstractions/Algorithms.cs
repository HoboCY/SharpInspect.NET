using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SharpInspect.Abstractions;

/// <summary>Stable identity of one consumer supplied inspection algorithm.</summary>
public sealed record AlgorithmIdentity
{
    public AlgorithmIdentity(string id, string version)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
    }

    public string Id { get; }
    public string Version { get; }
}

/// <summary>Complete immutable descriptor bound by Recipe Activation.</summary>
public sealed record AlgorithmDescriptor
{
    public AlgorithmDescriptor(AlgorithmIdentity identity,
        AlgorithmConfigurationSchema configurationSchema,
        AlgorithmResultSchema resultSchema)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ConfigurationSchema = configurationSchema ?? throw new ArgumentNullException(nameof(configurationSchema));
        ResultSchema = resultSchema ?? throw new ArgumentNullException(nameof(resultSchema));
    }

    public AlgorithmIdentity Identity { get; }
    public AlgorithmConfigurationSchema ConfigurationSchema { get; }
    public AlgorithmResultSchema ResultSchema { get; }
}

/// <summary>
/// The capability-minimized input to one algorithm call. The frame is borrowed for the
/// duration of the call and is never exposed as a writable buffer or a general service locator.
/// </summary>
public sealed class AlgorithmExecutionContext
{
    public AlgorithmExecutionContext(ExecutionCorrelationId correlation,
        AlgorithmConfigurationSnapshot configuration, VisionFrame frame,
        IAlgorithmDiagnosticSink diagnostics)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(diagnostics);
        AlgorithmContractValidation.Correlation(correlation, nameof(correlation));
        if (frame.Correlation != correlation)
            throw new ArgumentException("AlgorithmFrameCorrelationMismatch", nameof(frame));

        Correlation = correlation;
        Configuration = configuration;
        Frame = frame;
        Diagnostics = diagnostics;
    }

    public ExecutionCorrelationId Correlation { get; }
    public AlgorithmConfigurationSnapshot Configuration { get; }
    public VisionFrame Frame { get; }
    public IAlgorithmDiagnosticSink Diagnostics { get; }
}

/// <summary>Consumer factory registered explicitly by the host.</summary>
public interface IVisionAlgorithmFactory
{
    AlgorithmDescriptor Descriptor { get; }

    ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
        AlgorithmConfigurationSnapshot configuration,
        CancellationToken cancellationToken = default);

    ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
        CancellationToken cancellationToken = default);
}

/// <summary>One prepared, reusable algorithm instance.</summary>
public interface IVisionAlgorithm : IAsyncDisposable
{
    ValueTask WarmUpAsync(CancellationToken cancellationToken = default);

    ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Closed typed value carried by a diagnostic event.</summary>
public sealed record AlgorithmDiagnosticField
{
    public AlgorithmDiagnosticField(string key, AlgorithmScalarValue value)
    {
        Key = AlgorithmContractValidation.Identifier(key, nameof(key));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Key { get; }
    public AlgorithmScalarValue Value { get; }
}

/// <summary>
/// A bounded structured diagnostic. It intentionally has no message, exception, path,
/// logger, or arbitrary object property.
/// </summary>
public sealed record AlgorithmDiagnosticEvent
{
    public AlgorithmDiagnosticEvent(string code,
        IEnumerable<AlgorithmDiagnosticField>? fields = null)
    {
        Code = AlgorithmContractValidation.Identifier(code, nameof(code));
        var copied = AlgorithmContractValidation.Copy(fields, nameof(fields), maximumCount: 32);
        if (copied.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("AlgorithmDiagnosticFieldDuplicate", nameof(fields));
        Fields = copied;
    }

    public string Code { get; }
    public ReadOnlyCollection<AlgorithmDiagnosticField> Fields { get; }
}

public enum AlgorithmDiagnosticEmission
{
    Accepted,
    Dropped
}

/// <summary>Runtime-owned bounded diagnostic channel for one execution.</summary>
public interface IAlgorithmDiagnosticSink
{
    AlgorithmDiagnosticEmission TryEmit(AlgorithmDiagnosticEvent diagnosticEvent);
}

internal static class AlgorithmContractValidation
{
    internal static string Identifier(string value, string parameterName, int maximumLength = 128)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length < 1 || value.Length > maximumLength || value.Trim() != value ||
            value.Any(char.IsControl))
            throw new ArgumentException("AlgorithmIdentifierInvalid", parameterName);
        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-'))
                throw new ArgumentException("AlgorithmIdentifierInvalid", parameterName);
        }

        return value;
    }

    internal static string BoundedText(string value, string parameterName, int maximumLength)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length < 1 || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("AlgorithmTextInvalid", parameterName);
        try
        {
            _ = new UTF8Encoding(false, true).GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("AlgorithmTextInvalid", parameterName, exception);
        }

        return value;
    }

    internal static void Correlation(ExecutionCorrelationId correlation, string parameterName)
    {
        if (!Enum.IsDefined(typeof(ExecutionKind), correlation.Kind) || correlation.Value == Guid.Empty)
            throw new ArgumentException("ExecutionCorrelationInvalid", parameterName);
    }

    internal static void Finite(double value, string parameterName)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(parameterName);
    }

    internal static void PositiveFinite(double value, string parameterName)
    {
        Finite(value, parameterName);
        if (value <= 0) throw new ArgumentOutOfRangeException(parameterName);
    }

    internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T>? values, string parameterName = "values",
        int maximumCount = 4096)
    {
        if (values is null) return new ReadOnlyCollection<T>(Array.Empty<T>());
        if (maximumCount < 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));

        var copied = new List<T>(Math.Min(maximumCount, 16));
        using var enumerator = values.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (copied.Count == maximumCount)
                throw new ArgumentException("AlgorithmCollectionCapacityExceeded", parameterName);
            if (enumerator.Current is null)
                throw new ArgumentException("AlgorithmCollectionContainsNull", parameterName);
            copied.Add(enumerator.Current);
        }

        return new ReadOnlyCollection<T>(copied);
    }

    internal static string HashParts(IEnumerable<string?> parts)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var part in parts)
        {
            var bytes = part is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(part);
            stream.WriteByte(part is null ? (byte)0 : (byte)1);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal static string Invariant(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
