using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class AlgorithmResultCodecTests
{
    [Fact]
    public async Task V142_C01_ProductionStructuredEvidencePreservesEveryOverlayAndTheOriginalIdentity()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Production, includeOverlay: true);
        var id = fixture.Outcome.Correlation.Value;
        var encoded = ProductionAlgorithmResultCodec.Encode(id, fixture.Outcome);
        var decoded = ProductionAlgorithmResultCodec.Decode(id, encoded.PayloadJson, encoded.PayloadHash);
        Assert.Equal(ExecutionKind.Production, decoded.Correlation.Kind);
        Assert.Equal(id, decoded.InspectionId);
        Assert.Equal(fixture.Outcome.PreparedInstanceId, decoded.PreparedInstanceId);
        Assert.Equal(fixture.Outcome.FrameMetadata.Correlation, decoded.FrameMetadata.Correlation);
        Assert.Equal(fixture.Outcome.FrameMetadata.RequiredBufferLength, decoded.FrameMetadata.RequiredBufferLength);
        Assert.Equal(fixture.Outcome.FrameMetadata.EffectiveCameraConfiguration,
            decoded.FrameMetadata.EffectiveCameraConfiguration);
        Assert.Equal(fixture.Outcome.ResultSchema.ContentHash, decoded.ResultSchema.ContentHash);
        Assert.Equal(fixture.Outcome.ValidatedResult!.Measurements.OrderBy(value => value.Key, StringComparer.Ordinal),
            decoded.Result.Measurements.OrderBy(value => value.Key, StringComparer.Ordinal));
        Assert.Equal(10, decoded.Result.OverlaySet.Primitives.Count);
        Assert.Equal(fixture.Outcome.AdmittedMonotonicTimestamp, decoded.AdmittedMonotonicTimestamp);
        Assert.Equal(fixture.Outcome.Timing.PolicyContentHash, decoded.Timing.PolicyContentHash);
        Assert.Equal(encoded.PayloadJson, decoded.PayloadJson);
        Assert.Equal(encoded.PayloadHash, decoded.PayloadHash);
        var reconstructed = new AlgorithmExecutionOutcome(fixture.Prepared, decoded.FrameMetadata,
            ExecutionStatus.Success, decoded.Result.ReasonCode, decoded.Result, decoded.Timing,
            decoded.AdmittedMonotonicTimestamp);
        var reencoded = ProductionAlgorithmResultCodec.Encode(id, reconstructed);
        Assert.Equal(encoded.PayloadJson, reencoded.PayloadJson);
        Assert.Equal(encoded.PayloadHash, reencoded.PayloadHash);
        Assert.Contains("\"FormatVersion\":2", encoded.PayloadJson);
        Assert.False(AlgorithmResultStorageCodec.TryEncode(id, fixture.Outcome, out var archive, out var reason));
        Assert.Null(archive);
        Assert.Equal("AlgorithmResultProductionForbidden", reason);
        Assert.Throws<InvalidOperationException>(() => AlgorithmResultStorageCodec.Decode(1,
            DateTimeOffset.UtcNow, encoded.PayloadJson, encoded.PayloadHash));
    }

    [Fact]
    public async Task V142_C02_ProductionCodecRejectsCrossIdentityArchiveAndNoncanonicalEvidence()
    {
        await using var fixture = Artifact.Create(ExecutionKind.Production, includeOverlay: true);
        var id = fixture.Outcome.Correlation.Value;
        var encoded = ProductionAlgorithmResultCodec.Encode(id, fixture.Outcome);
        Assert.Throws<InvalidOperationException>(() => ProductionAlgorithmResultCodec.Encode(Guid.NewGuid(), fixture.Outcome));
        Assert.Throws<InvalidOperationException>(() => ProductionAlgorithmResultCodec.Decode(Guid.NewGuid(), encoded.PayloadJson, encoded.PayloadHash));
        foreach (var changed in new[]
        {
            encoded.PayloadJson + " ",
            encoded.PayloadJson.Replace("\"FormatVersion\":2", "\"FormatVersion\":1", StringComparison.Ordinal),
            encoded.PayloadJson.Replace("\"Kind\":\"Production\"", "\"Kind\":\"Manual\"", StringComparison.Ordinal)
        })
        {
            Assert.Throws<InvalidOperationException>(() => ProductionAlgorithmResultCodec.Decode(id, changed,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changed)))));
        }
        Assert.Throws<InvalidOperationException>(() => ProductionAlgorithmResultCodec.Decode(id,
            encoded.PayloadJson.Replace("Score", "TamperedScore", StringComparison.Ordinal), encoded.PayloadHash));
        await using var manual = Artifact.Create(ExecutionKind.Manual, includeOverlay: true);
        var archive = Encode(manual);
        Assert.Throws<InvalidOperationException>(() => ProductionAlgorithmResultCodec.Decode(manual.Outcome.Correlation.Value,
            archive.PayloadJson, archive.PayloadHash));
    }
}
