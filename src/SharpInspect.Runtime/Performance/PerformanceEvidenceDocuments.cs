using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Performance;

/// <summary>A locally recomputed artifact, with no signature or production qualification authority.</summary>
public sealed class PerformanceEvidenceDocument
{
    internal PerformanceEvidenceDocument(PerformanceRawCapture raw, PerformanceScenarioReport report, string rawHash, string reportHash)
    { Raw = raw; Report = report; RawHash = rawHash; ReportHash = reportHash; }
    public PerformanceRawCapture Raw { get; }
    public PerformanceScenarioReport Report { get; }
    public string RawHash { get; }
    public string ReportHash { get; }
}

/// <summary>
/// Bounded canonical UTF-8 export and offline readback. The document contains the complete frozen
/// contract, actual run header, retained raw scalar observations and recomputed report. Hashes detect
/// alteration; they do not authenticate externally supplied data or confer qualification authority.
/// No method is called on a Runtime hot path. File publication is explicit and never overwrites.
/// </summary>
public static class PerformanceEvidenceDocuments
{
    public const string Format = "SharpInspect.PerformanceEvidence.v1";
    public const int MaximumDocumentBytes = 256 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };
    private static readonly HashSet<Type> ClosedTypes = new()
    {
        typeof(PerformanceRawCapture), typeof(PerformanceContract), typeof(PerformanceScenario),
        typeof(PerformanceExpectedFailure), typeof(PerformanceSpanBudget), typeof(PerformanceResourceBudget),
        typeof(PerformanceCaptureBounds), typeof(PerformanceRunHeader), typeof(PerformanceEvent),
        typeof(PerformanceResourceSample), typeof(PerformanceResourceValue), typeof(PerformanceDurableFact),
        typeof(PerformanceFrameObservation), typeof(PerformanceCycleBinding), typeof(ExecutionCorrelationId)
    };
    private static readonly HashSet<Type> ScalarTypes = new()
    { typeof(string), typeof(bool), typeof(int), typeof(long), typeof(uint), typeof(double), typeof(Guid), typeof(DateTimeOffset), typeof(TimeSpan) };
    private static readonly HashSet<Type> EnumTypes = new()
    {
        typeof(PerformanceScenarioKind), typeof(PerformanceSpan), typeof(PerformanceResource),
        typeof(PerformancePercentileRule), typeof(PerformanceJitterRule), typeof(PerformanceEvidenceState),
        typeof(PerformanceEventKind), typeof(PerformanceObservationOutcome), typeof(ExecutionKind),
        typeof(ProductionInspectionEventKind), typeof(ExecutionStatus), typeof(InspectionDecision), typeof(VisionPixelFormat)
    };

    public static byte[] Encode(PerformanceRawCapture raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.State == PerformanceEvidenceState.Capturing) throw new InvalidOperationException("PerformanceCaptureNotTerminal");
        var limit = checked((int)Math.Min(raw.Contract.Capture.MaximumBytes, MaximumDocumentBytes));
        var rawBytes = Serialize(raw, limit);
        var report = PerformanceReportCalculator.Calculate(raw);
        var reportBytes = Serialize(report, limit);
        using var stream = new LimitedStream(limit);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Format", Format);
            writer.WriteString("RawHash", Hash(rawBytes));
            writer.WriteString("ReportHash", Hash(reportBytes));
            writer.WriteString("CalculationRules", PerformanceCollectorDescription.CalculationRules);
            writer.WriteString("ResourceRules", PerformanceCollectorDescription.ResourceRules);
            writer.WritePropertyName("Raw"); writer.WriteRawValue(rawBytes, skipInputValidation: false);
            writer.WritePropertyName("Report"); writer.WriteRawValue(reportBytes, skipInputValidation: false);
            writer.WriteEndObject(); writer.Flush();
        }
        return stream.ToArray();
    }

    public static PerformanceEvidenceDocument Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 2 or > MaximumDocumentBytes) throw new InvalidDataException("PerformanceDocumentSizeInvalid");
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.GetProperty("Format").GetString() != Format ||
                root.GetProperty("CalculationRules").GetString() != PerformanceCollectorDescription.CalculationRules ||
                root.GetProperty("ResourceRules").GetString() != PerformanceCollectorDescription.ResourceRules)
                throw new InvalidDataException("PerformanceDocumentFormatMismatch");
            var raw = (PerformanceRawCapture)ReadClosed(typeof(PerformanceRawCapture), root.GetProperty("Raw"))!;
            if (bytes.Length > raw.Contract.Capture.MaximumBytes) throw new InvalidDataException("PerformanceDocumentBudgetExceeded");
            // A closed reconstruction recomputes all nested contract hashes. Canonical equality rejects
            // unknown/duplicate fields, stale derived hashes, altered reports and alternate representations.
            var canonical = Encode(raw);
            if (!bytes.Span.SequenceEqual(canonical)) throw new InvalidDataException("PerformanceDocumentRecomputationMismatch");
            return new(raw, PerformanceReportCalculator.Calculate(raw), root.GetProperty("RawHash").GetString()!,
                root.GetProperty("ReportHash").GetString()!);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or ArgumentException or
            InvalidOperationException or TargetInvocationException or OverflowException)
        { throw new InvalidDataException("PerformanceDocumentInvalid", exception); }
    }

    public static async Task WriteNewAsync(string path, PerformanceRawCapture raw, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("PerformanceDocumentPathRequired", nameof(path));
        var bytes = Encode(raw);
        var destination = Path.GetFullPath(path);
        var temporary = destination + ".pending-" + Guid.NewGuid().ToString("N");
        // Failure artifacts are retained at the unique pending path. Rename publishes only a complete
        // flushed artifact; the destination is never replaced, including concurrent publication races.
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, destination, overwrite: false);
    }

    public static async Task<PerformanceEvidenceDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length is < 2 or > MaximumDocumentBytes) throw new InvalidDataException("PerformanceDocumentSizeInvalid");
        using var contents = new LimitedStream(checked((int)stream.Length));
        await stream.CopyToAsync(contents, cancellationToken).ConfigureAwait(false);
        return Decode(contents.ToArray());
    }

    private static byte[] Serialize<T>(T value, int limit)
    {
        using var stream = new LimitedStream(limit);
        JsonSerializer.Serialize(stream, value, JsonOptions);
        using var document = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        using var canonical = new LimitedStream(limit);
        using (var writer = new Utf8JsonWriter(canonical))
        { WriteCanonical(writer, document.RootElement); writer.Flush(); }
        return canonical.ToArray();
    }

    // Reflection/constructor property order differs across supported JSON runtimes. Freeze object
    // order explicitly; array order carries scenario ordinals/event order and is never reordered.
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // Types come only from the hard-coded domain graph, never from document-supplied type names.
    // Constructor invocation retains every domain validation and defensive collection copy.
    private static object? ReadClosed(Type type, JsonElement value)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!type.IsValueType || nullable is not null) return null;
            throw new InvalidDataException("PerformanceDocumentNullInvalid");
        }
        if (nullable is not null) return ReadClosed(nullable, value);
        if (ScalarTypes.Contains(type) || EnumTypes.Contains(type))
        {
            var scalar = value.Deserialize(type, JsonOptions);
            if (EnumTypes.Contains(type) && !Enum.IsDefined(type, scalar!)) throw new InvalidDataException("PerformanceDocumentEnumInvalid");
            return scalar;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            var elementType = type.GetGenericArguments()[0];
            var length = value.GetArrayLength();
            if (length > 1_000_000) throw new InvalidDataException("PerformanceDocumentArrayExceeded");
            var array = Array.CreateInstance(elementType, length);
            var index = 0;
            foreach (var element in value.EnumerateArray()) array.SetValue(ReadClosed(elementType, element), index++);
            return array;
        }
        if (!ClosedTypes.Contains(type)) throw new InvalidDataException("PerformanceDocumentTypeInvalid");
        var constructor = type.GetConstructors().Single();
        var parameters = constructor.GetParameters().Select(parameter =>
        {
            var property = type.GetProperty(parameter.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? throw new InvalidDataException("PerformanceDocumentShapeInvalid");
            return ReadClosed(parameter.ParameterType, value.GetProperty(property.Name));
        }).ToArray();
        return constructor.Invoke(parameters);
    }

    private sealed class LimitedStream : MemoryStream
    {
        private readonly int _maximum;
        internal LimitedStream(int maximum) : base(Math.Min(maximum, 64 * 1024)) => _maximum = maximum;
        private void Require(int count)
        { if (count < 0 || Position > _maximum - count) throw new InvalidDataException("PerformanceDocumentBudgetExceeded"); }
        public override void Write(byte[] buffer, int offset, int count) { Require(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Require(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Require(1); base.WriteByte(value); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
    }
}
