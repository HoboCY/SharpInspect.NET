using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Diagnostics;

internal sealed record SupportBundleProjectionResult(byte[] Bytes, string ContentHash, string MemberHash,
    int ScannedRecords, int IncludedRecords, int InvalidRecords, int OutsideScopeRecords);

/// <summary>A fresh closed projection. Neither raw source lines nor protected fields enter an artifact.</summary>
internal static class SupportBundleProjection
{
    internal static SupportBundleProjectionResult Build(Guid bundleId, SupportBundleScope scope,
        DiagnosticSupportPolicy policy, LoggingDiagnosticsPolicy logging, IReadOnlyList<string> source,
        DateTimeOffset created, DateTimeOffset expires)
    {
        if (bundleId == Guid.Empty || policy.LoggingPolicyHash != logging.ContentHash ||
            scope.ThroughUtc - scope.FromUtc > policy.MaximumScope || source.Count > policy.MaximumSourceRecords ||
            created.Offset != TimeSpan.Zero || expires.Offset != TimeSpan.Zero || expires <= created)
            throw new InvalidOperationException("SupportBundleProjectionScopeInvalid");
        var records = new List<DiagnosticRecord>(); var invalid = 0; var outside = 0; var sourceBytes = 0;
        foreach (var line in source)
        {
            if (line.Length > policy.MaximumSourceBytes - sourceBytes)
                throw new InvalidOperationException("SupportBundleSourceByteLimit");
            sourceBytes = checked(sourceBytes + Encoding.UTF8.GetByteCount(line));
            if (sourceBytes > policy.MaximumSourceBytes) throw new InvalidOperationException("SupportBundleSourceByteLimit");
            // Files are not trusted, including files originally written by this Runtime.
            var record = DiagnosticJson.Decode(line, logging, protectedChannel: false);
            if (record is null) { invalid++; continue; }
            if (!InScope(record, scope)) { outside++; continue; }
            records.Add(record);
        }
        using var member = new MemoryStream();
        using (var writer = new Utf8JsonWriter(member))
        {
            writer.WriteStartArray();
            foreach (var record in records)
            {
                // Re-encoding only the decoded safe record guarantees a fresh allowlist
                // projection; Encode receives no producer object or arbitrary exception.
                var encoded = DiagnosticJson.Encode(record, logging.SafeFiles.MaximumRecordBytes) ??
                    throw new InvalidOperationException("SupportBundleProjectionRecordTooLarge");
                using var safeRecord = JsonDocument.Parse(encoded.Line);
                safeRecord.RootElement.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        var memberBytes = member.ToArray(); var memberHash = Convert.ToHexString(SHA256.HashData(memberBytes));
        using var artifact = new MemoryStream();
        using (var writer = new Utf8JsonWriter(artifact))
        {
            writer.WriteStartObject(); writer.WriteNumber("formatVersion", 1); writer.WriteString("bundleId", bundleId);
            writer.WriteStartObject("manifest");
            writer.WriteString("supportPolicyHash", policy.ContentHash); writer.WriteString("loggingPolicyHash", logging.ContentHash);
            writer.WriteString("scopeHash", scope.ContentHash); writer.WriteString("createdAtUtc", created); writer.WriteString("expiresAtUtc", expires);
            writer.WriteStartObject("scope"); WriteScope(writer, scope); writer.WriteEndObject();
            writer.WriteString("source", "SafeOperationalHistory"); writer.WriteString("scanMode", "BoundedNewestTail");
            writer.WriteBoolean("exhaustive", false); writer.WriteString("earlierHistoryCoverage", "Unknown");
            writer.WriteNumber("maximumSourceRecords", policy.MaximumSourceRecords); writer.WriteNumber("maximumSourceBytes", policy.MaximumSourceBytes);
            writer.WriteNumber("scannedRecords", source.Count); writer.WriteNumber("scannedBytes", sourceBytes);
            writer.WriteNumber("includedRecords", records.Count); writer.WriteNumber("invalidRecordsOmitted", invalid);
            writer.WriteNumber("outsideScopeRecordsOmitted", outside);
            writer.WriteStartArray("excludedSources");
            foreach (var value in new[] { "ProtectedDiagnosticStore", "Images", "Recipes", "Database", "RawPartCodes",
                "Credentials", "PrivateKeys", "MemoryDumps", "ArbitraryExceptionPayloads" }) writer.WriteStringValue(value);
            writer.WriteEndArray(); writer.WriteBoolean("isBackup", false); writer.WriteEndObject();
            writer.WriteStartArray("members"); writer.WriteStartObject(); writer.WriteString("name", "diagnostics.json");
            writer.WriteString("sha256", memberHash); writer.WriteNumber("bytes", memberBytes.Length);
            writer.WritePropertyName("content");
            using (var document = JsonDocument.Parse(memberBytes)) document.RootElement.WriteTo(writer);
            writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
        }
        if (artifact.Length > policy.MaximumBundleBytes) throw new InvalidOperationException("SupportBundleByteLimit");
        var bytes = artifact.ToArray();
        return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)), memberHash, source.Count, records.Count, invalid, outside);
    }

    private static bool InScope(DiagnosticRecord record, SupportBundleScope scope) =>
        record.RuntimeEpoch == scope.RuntimeEpoch && record.ObservedAtUtc >= scope.FromUtc && record.ObservedAtUtc <= scope.ThroughUtc &&
        (scope.Executions.Count == 0 || record.Execution is not null && scope.Executions.Contains(record.Execution)) &&
        (scope.CommandCorrelations.Count == 0 || record.CommandCorrelationId is { } command && scope.CommandCorrelations.Contains(command)) &&
        (scope.CaptureSessions.Count == 0 || record.CaptureSessionId is { } capture && scope.CaptureSessions.Contains(capture));

    private static void WriteScope(Utf8JsonWriter writer, SupportBundleScope scope)
    {
        writer.WriteString("runtimeEpoch", scope.RuntimeEpoch); writer.WriteString("fromUtc", scope.FromUtc); writer.WriteString("throughUtc", scope.ThroughUtc);
        writer.WriteStartArray("executions");
        foreach (var value in scope.Executions)
        { writer.WriteStartObject(); writer.WriteString("kind", value.Kind.ToString()); writer.WriteString("id", value.Value); writer.WriteEndObject(); }
        writer.WriteEndArray(); writer.WriteStartArray("commandCorrelations");
        foreach (var value in scope.CommandCorrelations) writer.WriteStringValue(value);
        writer.WriteEndArray(); writer.WriteStartArray("captureSessions");
        foreach (var value in scope.CaptureSessions) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
