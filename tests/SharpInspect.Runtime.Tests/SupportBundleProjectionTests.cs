using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class SupportBundleProjectionTests
{
    internal static DiagnosticSupportPolicy Policy(LoggingDiagnosticsPolicy logging, int maximumRecords = 10,
        int maximumBytes = 16384, TimeSpan? retention = null) => new("support-test", "v1", "local-test",
        logging.ContentHash, logging.TracePolicySnapshotHash, TimeSpan.FromDays(1), maximumRecords, maximumBytes,
        65536, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20), retention ?? TimeSpan.FromSeconds(10),
        1000, 16384, 16 * 1024 * 1024);
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
    private static DiagnosticRecord Record(LoggingDiagnosticsPolicy policy, Guid epoch, Guid? command = null) =>
        new(Guid.NewGuid(), "Runtime.Detail", 1, DiagnosticLevel.Debug, "Runtime", epoch, Start.AddMinutes(1),
            null, command, policy.ContentHash, new[] { new DiagnosticProperty("State", DiagnosticScalar.FromSymbol("Idle")) })
        { CaptureSessionId = Guid.NewGuid(), CaptureProfileHash = new string('A', 64) };
    private static string Line(DiagnosticRecord value) => Encoding.UTF8.GetString(DiagnosticJson.Encode(value, 4096)!.Line);

    [Fact, Trait("VerificationId", "V158_B01")]
    public void V158_B01_ManifestReportsBoundedCoverageAndMatchesExactMemberHash()
    {
        var logging = DiagnosticCapturePipelineTests.Policy(); var policy = Policy(logging); var epoch = Guid.NewGuid();
        var bundle = Guid.NewGuid(); var command = Guid.NewGuid();
        var scope = new SupportBundleScope(epoch, Start, Start.AddHours(1), Array.Empty<ExecutionCorrelationId>(), new[] { command }, Array.Empty<Guid>());
        var included = Record(logging, epoch, command); var excluded = Record(logging, epoch, Guid.NewGuid());
        var result = SupportBundleProjection.Build(bundle, scope, policy, logging,
            new[] { Line(included), Line(excluded), "{broken" }, Start.AddHours(2), Start.AddHours(3));
        Assert.Equal(3, result.ScannedRecords); Assert.Equal(1, result.IncludedRecords);
        Assert.Equal(1, result.InvalidRecords); Assert.Equal(1, result.OutsideScopeRecords);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(result.Bytes)), result.ContentHash);
        using var doc = JsonDocument.Parse(result.Bytes); var root = doc.RootElement;
        Assert.Equal(bundle, root.GetProperty("bundleId").GetGuid());
        var manifest = root.GetProperty("manifest");
        Assert.False(manifest.GetProperty("exhaustive").GetBoolean());
        Assert.Equal("Unknown", manifest.GetProperty("earlierHistoryCoverage").GetString());
        Assert.Equal(scope.ContentHash, manifest.GetProperty("scopeHash").GetString());
        var member = Assert.Single(root.GetProperty("members").EnumerateArray());
        var memberBytes = Encoding.UTF8.GetBytes(member.GetProperty("content").GetRawText());
        Assert.Equal(memberBytes.Length, member.GetProperty("bytes").GetInt32());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(memberBytes)), member.GetProperty("sha256").GetString());
        Assert.Equal(included.EventId, Assert.Single(member.GetProperty("content").EnumerateArray()).GetProperty("eventId").GetGuid());
    }

    [Fact, Trait("VerificationId", "V158_B02")]
    public void V158_B02_ReclassificationOmitsProtectedAndSecretCanarySourceRecords()
    {
        var logging = DiagnosticCapturePipelineTests.Policy(); var epoch = Guid.NewGuid(); var safe = Line(Record(logging, epoch));
        var hostile = new[] { safe.Replace("Idle", "SECRET-CANARY", StringComparison.Ordinal),
            safe.Replace("State", "password", StringComparison.Ordinal),
            safe.Replace("\"State\":\"Idle\"", "\"VendorState\":\"Known\"", StringComparison.Ordinal),
            safe.Replace("\"State\":\"Idle\"", "\"rawPartIdentity\":\"PART-CANARY\"", StringComparison.Ordinal),
            safe.Replace("\"State\":\"Idle\"", "\"imageBytes\":\"IMAGE-CANARY\"", StringComparison.Ordinal) };
        var scope = new SupportBundleScope(epoch, Start, Start.AddHours(1), Array.Empty<ExecutionCorrelationId>(), Array.Empty<Guid>(), Array.Empty<Guid>());
        var result = SupportBundleProjection.Build(Guid.NewGuid(), scope, Policy(logging), logging,
            hostile.Append(safe).ToArray(), Start.AddHours(2), Start.AddHours(3));
        Assert.Equal(5, result.InvalidRecords); Assert.Equal(1, result.IncludedRecords);
        var text = Encoding.UTF8.GetString(result.Bytes);
        Assert.DoesNotContain("CANARY", text); Assert.DoesNotContain("VendorState", text);
        Assert.DoesNotContain("Known", text); Assert.DoesNotContain("password", text);
    }

    [Fact, Trait("VerificationId", "V158_B03")]
    public void V158_B03_BytesRecordsPolicyAndWindowRemainIndependentlyBounded()
    {
        var logging = DiagnosticCapturePipelineTests.Policy(); var epoch = Guid.NewGuid(); var line = Line(Record(logging, epoch));
        var scope = new SupportBundleScope(epoch, Start, Start.AddHours(1), Array.Empty<ExecutionCorrelationId>(), Array.Empty<Guid>(), Array.Empty<Guid>());
        Assert.Throws<InvalidOperationException>(() => SupportBundleProjection.Build(Guid.NewGuid(), scope,
            Policy(logging, maximumRecords: 1), logging, new[] { line, line }, Start, Start.AddHours(3)));
        Assert.Throws<InvalidOperationException>(() => SupportBundleProjection.Build(Guid.NewGuid(), scope,
            Policy(logging, maximumBytes: 256), logging, new[] { line }, Start, Start.AddHours(3)));
        var tooWide = new SupportBundleScope(epoch, Start, Start.AddDays(2), Array.Empty<ExecutionCorrelationId>(), Array.Empty<Guid>(), Array.Empty<Guid>());
        Assert.Throws<InvalidOperationException>(() => SupportBundleProjection.Build(Guid.NewGuid(), tooWide,
            Policy(logging), logging, Array.Empty<string>(), Start, Start.AddHours(3)));
    }
}
