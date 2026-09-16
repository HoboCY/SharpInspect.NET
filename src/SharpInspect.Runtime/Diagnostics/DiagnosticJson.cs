using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Diagnostics;

internal sealed record DiagnosticEnvelope(DiagnosticRecord Record, byte[] Line)
{
    internal DiagnosticCaptureLease? Capture { get; init; }
}

internal static class DiagnosticJson
{
    // Explicit primitive writer: no reflection serializer ever sees unclassified input.
    internal static DiagnosticEnvelope? Encode(DiagnosticRecord record, long maximumBytes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("eventId", record.EventId); writer.WriteString("code", record.Code);
            writer.WriteNumber("schemaVersion", record.SchemaVersion); writer.WriteNumber("level", (int)record.Level);
            writer.WriteString("component", record.Component); writer.WriteString("runtimeEpoch", record.RuntimeEpoch);
            writer.WriteString("observedAtUtc", record.ObservedAtUtc); writer.WriteString("policyHash", record.PolicyHash);
            if (record.Execution is { } execution)
            { writer.WriteNumber("executionKind", (int)execution.Kind); writer.WriteString("executionId", execution.Value); }
            if (record.CommandCorrelationId is { } command) writer.WriteString("commandId", command);
            if (record.CaptureSessionId is { } capture)
            { writer.WriteString("captureId", capture); writer.WriteString("captureProfileHash", record.CaptureProfileHash); }
            writer.WriteStartObject("properties");
            foreach (var property in record.Properties)
            {
                switch (property.Value.Kind)
                {
                    case DiagnosticScalarKind.Boolean: writer.WriteBoolean(property.Name, property.Value.Boolean); break;
                    case DiagnosticScalarKind.Int64: writer.WriteNumber(property.Name, property.Value.Integer); break;
                    case DiagnosticScalarKind.Number: writer.WriteNumber(property.Name, property.Value.Number); break;
                    case DiagnosticScalarKind.Symbol: writer.WriteString(property.Name, property.Value.Symbol); break;
                    default: throw new InvalidOperationException("DiagnosticScalarInvalid");
                }
            }
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.Length <= maximumBytes ? new(record, stream.ToArray()) : null;
    }

    // Files are not authority. Revalidate their entire closed shape and channel before query projection.
    internal static DiagnosticRecord? Decode(string line, LoggingDiagnosticsPolicy policy, bool protectedChannel)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.EnumerateObject())
                if (!names.Add(item.Name) || item.Name is not ("eventId" or "code" or "schemaVersion" or "level" or
                    "component" or "runtimeEpoch" or "observedAtUtc" or "policyHash" or "executionKind" or
                    "executionId" or "commandId" or "properties" or "captureId" or "captureProfileHash")) return null;
            var code = root.GetProperty("code").GetString(); var version = root.GetProperty("schemaVersion").GetInt32();
            var schema = policy.Contracts.FirstOrDefault(value => value.Code == code && value.SchemaVersion == version);
            if (schema is null || root.GetProperty("policyHash").GetString() != policy.ContentHash ||
                root.GetProperty("level").GetInt32() != (int)schema.Level ||
                root.GetProperty("component").GetString() != schema.Component) return null;
            var requests = new List<DiagnosticPropertyRequest>(32);
            foreach (var property in root.GetProperty("properties").EnumerateObject())
            {
                if (requests.Count == 32) return null;
                var field = schema.Fields.FirstOrDefault(value => value.Name == property.Name);
                if (field is null || field.Classification != (protectedChannel ? DiagnosticDataClass.Protected : DiagnosticDataClass.Safe)) return null;
                object? value = field.Kind switch
                {
                    DiagnosticScalarKind.Boolean => property.Value.GetBoolean(),
                    DiagnosticScalarKind.Int64 => property.Value.GetInt64(),
                    DiagnosticScalarKind.Number => property.Value.GetDouble(),
                    DiagnosticScalarKind.Symbol => property.Value.GetString(),
                    _ => null
                };
                requests.Add(new(property.Name, value));
            }
            if (protectedChannel && requests.Count == 0) return null;
            // Classify the channel subset using the exact same scalar boundary; required fields
            // belong to either channel and therefore are checked only in the selected projection.
            var channelSchema = new DiagnosticEventContract(schema.Code, schema.SchemaVersion, schema.Component,
                schema.Level, schema.AlgorithmAllowed, schema.Fields.Where(field => field.Classification ==
                    (protectedChannel ? DiagnosticDataClass.Protected : DiagnosticDataClass.Safe)));
            var classifier = new DiagnosticClassifier(policy);
            if (!classifier.TryClassifyProjection(new(code!, version, requests.ToArray()), channelSchema, out var values)) return null;
            ExecutionCorrelationId? execution = null;
            if (names.Contains("executionId") != names.Contains("executionKind")) return null;
            if (names.Contains("executionId"))
            {
                var kind = (ExecutionKind)root.GetProperty("executionKind").GetInt32();
                var id = root.GetProperty("executionId").GetGuid();
                if (!Enum.IsDefined(kind) || id == Guid.Empty) return null;
                execution = new(kind, id);
            }
            var eventId = root.GetProperty("eventId").GetGuid(); var epoch = root.GetProperty("runtimeEpoch").GetGuid();
            if (eventId == Guid.Empty || epoch == Guid.Empty) return null;
            if (names.Contains("captureId") != names.Contains("captureProfileHash")) return null;
            Guid? captureId = null; string? captureHash = null;
            if (names.Contains("captureId"))
            {
                captureId = root.GetProperty("captureId").GetGuid();
                captureHash = root.GetProperty("captureProfileHash").GetString();
                if (captureId == Guid.Empty || captureHash is not { Length: 64 } ||
                    captureHash.Any(value => value is not (>= '0' and <= '9' or >= 'A' and <= 'F'))) return null;
            }
            // Historical level is independent from current producer admission. A low-level
            // record must still carry valid capture attribution and obey every field rule.
            if (schema.Level < policy.Baseline && captureId is null) return null;
            var observed = root.GetProperty("observedAtUtc").GetDateTimeOffset();
            if (observed.Offset != TimeSpan.Zero) return null;
            return new(eventId, schema.Code, version, schema.Level, schema.Component, epoch,
                observed, execution,
                names.Contains("commandId") ? root.GetProperty("commandId").GetGuid() : null, policy.ContentHash,
                Array.AsReadOnly(values)) { CaptureSessionId = captureId, CaptureProfileHash = captureHash };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or
            KeyNotFoundException or ArgumentException or OverflowException) { return null; }
    }
}
