using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationComputationEvidenceTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashThree = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string HashFour = "4444444444444444444444444444444444444444444444444444444444444444";

    // Frozen from the schema-14 V1 DTO before computation evidence was added.  The
    // candidate has no evidence, so this exact payload must continue to decode and
    // to be emitted byte-for-byte by the V1 branch.  The bytes and hash were
    // captured from the published T24 assemblies in the isolated capture log
    // E:\temp\SharpInspect.NET-validation-artifacts\ticket25\t24-canonical-capture.log:
    // old Abstractions SHA-256 1589E2788CFFB0124A35CAD6F40A20045D5411E475BD2F97C69B1B7022AC4B37,
    // old Runtime SHA-256 6F6F3C2E256B8A4137170AABEC74FFB992F7CC0C27DD4B4808416C3932E528AA,
    // payload length 951, payload SHA-256 09BABE7CA1FFCCD4DEE1FDEDA3E2D119128147E181F95A4FE98FD986E05A402B.
    // The published net6.0-windows code was loaded in an isolated pwsh host
    // (.NET 10.0.11); this records the old code's bytes, not a net6 host claim.
    private const string FrozenV1CandidateHash =
        "11BA9D7DA18D9EC3B981FE18546643C677D6BA5696BAFBF516663D6826955077";
    private const string FrozenV1EventBase64 =
        "eyJGb3JtYXRWZXJzaW9uIjoxLCJFdmVudElkIjoiMzMzMzMzMzMtMzMzMy0zMzMzLTMzMzMtMzMzMzMzMzMzMzMzIiwiU2Vzc2lvbklkIjoiMjIyMjIyMjItMjIyMi0yMjIyLTIyMjItMjIyMjIyMjIyMjIyIiwiT3BlcmF0aW9uSWQiOiI0NDQ0NDQ0NC00NDQ0LTQ0NDQtNDQ0NC00NDQ0NDQ0NDQ0NDQiLCJQaGFzZSI6NSwiT3V0Y29tZSI6MCwiUmVhc29uQ29kZSI6IkNhbmRpZGF0ZVJldGFpbmVkIiwiT2NjdXJyZWRBdFV0YyI6IjIwMjQtMDEtMDJUMDM6MDQ6MDUuMDAwMDAwMFx1MDAyQjAwOjAwIiwiRnJhbWUiOm51bGwsIk9ic2VydmF0aW9uIjpudWxsLCJFeGNsdXNpb24iOm51bGwsIkNhbmRpZGF0ZSI6eyJDYW5kaWRhdGVJZCI6IjExMTExMTExLTExMTEtMTExMS0xMTExLTExMTExMTExMTExMSIsIlNlc3Npb25JZCI6IjIyMjIyMjIyLTIyMjItMjIyMi0yMjIyLTIyMjIyMjIyMjIyMiIsIlNlc3Npb25IZWFkZXJIYXNoIjoiMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMyIsIlNlbGVjdGlvbkhhc2giOiI0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0IiwiUmVzdWx0Ijp7IkNvZWZmaWNpZW50cyI6eyJGb3JtYXQiOnsiSWQiOiJjb2VmZi1mb3JtYXQiLCJWZXJzaW9uIjoiMSIsIkNvbnRlbnRIYXNoIjoiQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQSJ9LCJDYW5vbmljYWxCeXRlcyI6IkFRSUQifSwiUXVhbGl0eU1ldHJpY3MiOltdLCJEaWFnbm9zdGljcyI6W119LCJDb21wdXRlZEF0VXRjIjoiMjAyNC0wMS0wMlQwMzowNDowNS4wMDAwMDAwXHUwMDJCMDA6MDAifSwiQXV0aG9yaXphdGlvbkNvbW1hbmQiOm51bGwsIlRlbXBvcmFyeUNvbmZpZ3VyYXRpb24iOm51bGx9";

    [Fact]
    public void V125_S01_EvidenceEnvelopeDefensivelyCopiesBytesAndBindsFormat()
    {
        var format = Contract("evidence-format");
        var bytes = new byte[] { 9, 8, 7 };
        var expectedCanonicalHash = Convert.ToHexString(SHA256.HashData(bytes));
        var payload = new CalibrationComputationEvidencePayload(format, bytes);

        bytes[0] = 0;
        Assert.Equal(expectedCanonicalHash, payload.CanonicalBytesHash);
        Assert.Equal(3, payload.Length);

        var exposedBytes = payload.GetBytes();
        exposedBytes[1] = 0;
        Assert.Equal(new byte[] { 9, 8, 7 }, payload.GetBytes());

        var differentFormat = new CalibrationComputationEvidencePayload(
            Contract("other-evidence-format"), new byte[] { 9, 8, 7 });
        Assert.Equal(payload.CanonicalBytesHash, differentFormat.CanonicalBytesHash);
        Assert.NotEqual(payload.ContentHash, differentFormat.ContentHash);

        Assert.Throws<ArgumentException>(() => new CalibrationComputationEvidencePayload(
            format, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => new CalibrationComputationEvidencePayload(
            format, new byte[CalibrationComputationEvidencePayload.MaximumBytes + 1]));
    }

    [Fact]
    public void V125_S02_ComputationResultRetainsOptionalEvidence()
    {
        var legacy = Computation();
        Assert.Null(legacy.Evidence);

        var evidence = Evidence();
        var result = new CalibrationProcedureComputationResult(
            legacy.Coefficients, legacy.QualityMetrics, legacy.Diagnostics, evidence);

        Assert.Same(evidence, result.Evidence);
        Assert.Same(legacy.Coefficients, result.Coefficients);
    }

    [Fact]
    public void V125_S03_LegacyV1EventBytesAndCandidateHashRemainFrozen()
    {
        var value = Event();

        Assert.Equal(FrozenV1CandidateHash, value.Candidate!.ContentHash);
        var encoded = CalibrationSessionStorageCodec.EncodeEvent(value);
        Assert.Equal(FrozenV1EventBase64, Convert.ToBase64String(encoded));

        var decoded = CalibrationSessionStorageCodec.DecodeEvent(
            Convert.FromBase64String(FrozenV1EventBase64));
        Assert.Null(decoded.Candidate!.Result.Evidence);
        Assert.Equal(FrozenV1CandidateHash, decoded.Candidate!.ContentHash);
    }

    [Fact]
    public void V125_S04_EvidenceEventRoundTripsBytesAndHashesThroughV2Root()
    {
        var value = Event(Evidence());
        var encoded = CalibrationSessionStorageCodec.EncodeEvent(value);
        var json = Encoding.UTF8.GetString(encoded);

        Assert.StartsWith("{\"FormatVersion\":2,", json, StringComparison.Ordinal);
        Assert.Contains("\"Evidence\":{\"Format\":", json, StringComparison.Ordinal);

        var decoded = CalibrationSessionStorageCodec.DecodeEvent(encoded);
        var decodedEvidence = decoded.Candidate!.Result.Evidence;
        Assert.NotNull(decodedEvidence);
        var expectedEvidence = value.Candidate!.Result.Evidence!;
        Assert.Equal(expectedEvidence.Format, decodedEvidence!.Format);
        Assert.Equal(expectedEvidence.CanonicalBytesHash,
            decodedEvidence.CanonicalBytesHash);
        Assert.Equal(expectedEvidence.ContentHash, decodedEvidence.ContentHash);
        Assert.Equal(expectedEvidence.GetBytes(), decodedEvidence.GetBytes());
        Assert.Equal(value.Candidate!.ContentHash, decoded.Candidate!.ContentHash);
        Assert.NotEqual(FrozenV1CandidateHash, decoded.Candidate!.ContentHash);
    }

    [Fact]
    public void V125_S05_UnknownOrNonCanonicalEventVersionsAreRejected()
    {
        var value = Event(Evidence());
        var canonical = Encoding.UTF8.GetString(CalibrationSessionStorageCodec.EncodeEvent(value));
        var unknown = Encoding.UTF8.GetBytes(canonical.Replace(
            "{\"FormatVersion\":2,", "{\"FormatVersion\":99,", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() =>
            CalibrationSessionStorageCodec.DecodeEvent(unknown));

        var nonCanonical = Encoding.UTF8.GetBytes(canonical + " ");
        Assert.Throws<InvalidOperationException>(() =>
            CalibrationSessionStorageCodec.DecodeEvent(nonCanonical));
    }

    [Fact]
    public void V125_S06_EvidenceTamperChangesCandidateAndWholeEventBindings()
    {
        var value = Event(Evidence());
        var encoded = CalibrationSessionStorageCodec.EncodeEvent(value);
        var canonical = Encoding.UTF8.GetString(encoded);
        var tampered = Encoding.UTF8.GetBytes(canonical.Replace(
            "\"CanonicalBytes\":\"CQgH\"", "\"CanonicalBytes\":\"CQgI\"",
            StringComparison.Ordinal));
        var tamperedValue = CalibrationSessionStorageCodec.DecodeEvent(tampered);

        Assert.NotEqual(value.Candidate!.ContentHash, tamperedValue.Candidate!.ContentHash);
        var originalEventHash = CalibrationSessionStorageCodec.EventContentHash(
            value, 1, null, encoded, 7, HashA);
        var tamperedEventHash = CalibrationSessionStorageCodec.EventContentHash(
            tamperedValue, 1, null, tampered, 7, HashA);
        Assert.NotEqual(originalEventHash, tamperedEventHash);
    }

    [Fact]
    public void V125_S07_MalformedEvidenceTamperIsRejected()
    {
        var value = Event(Evidence());
        var canonical = Encoding.UTF8.GetString(CalibrationSessionStorageCodec.EncodeEvent(value));
        var tampered = Encoding.UTF8.GetBytes(canonical.Replace(
            "\"CanonicalBytes\":\"CQgH\"", "\"CanonicalBytes\":\"*\"",
            StringComparison.Ordinal));

        Assert.Throws<InvalidOperationException>(() =>
            CalibrationSessionStorageCodec.DecodeEvent(tampered));
    }

    [Fact]
    public void V125_S08_ExtractionReceiptIsBoundedDefensiveAndLegacyConstructorShapesRemain()
    {
        var format = Contract("extraction-receipt");
        var bytes = new byte[] { 5, 4, 3 };
        var expectedCanonicalHash = Convert.ToHexString(SHA256.HashData(bytes));
        var receipt = new CalibrationExtractionReceipt(format, bytes);

        bytes[0] = 0;
        Assert.Equal(expectedCanonicalHash, receipt.CanonicalBytesHash);
        Assert.Equal(3, receipt.Length);
        var exposedBytes = receipt.GetBytes();
        exposedBytes[1] = 0;
        Assert.Equal(new byte[] { 5, 4, 3 }, receipt.GetBytes());
        var differentFormat = new CalibrationExtractionReceipt(
            Contract("other-extraction-receipt"), new byte[] { 5, 4, 3 });
        Assert.Equal(receipt.CanonicalBytesHash, differentFormat.CanonicalBytesHash);
        Assert.NotEqual(receipt.ContentHash, differentFormat.ContentHash);
        Assert.Throws<ArgumentException>(() => new CalibrationExtractionReceipt(
            format, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => new CalibrationExtractionReceipt(
            format, new byte[CalibrationExtractionReceipt.MaximumBytes + 1]));

        var legacyExtraction = new CalibrationExtractionResult(Array.Empty<CalibrationImageFeature>(),
            Array.Empty<CalibrationProcedureDiagnostic>());
        Assert.Null(legacyExtraction.Receipt);
        var currentExtraction = new CalibrationExtractionResult(Array.Empty<CalibrationImageFeature>(),
            Array.Empty<CalibrationProcedureDiagnostic>(), receipt);
        Assert.Same(receipt, currentExtraction.Receipt);

        Assert.NotNull(typeof(CalibrationExtractionResult).GetConstructor(new[]
        {
            typeof(IEnumerable<CalibrationImageFeature>),
            typeof(IEnumerable<CalibrationProcedureDiagnostic>)
        }));
        Assert.NotNull(typeof(CalibrationExtractionResult).GetConstructor(new[]
        {
            typeof(IEnumerable<CalibrationImageFeature>),
            typeof(IEnumerable<CalibrationProcedureDiagnostic>),
            typeof(CalibrationExtractionReceipt)
        }));
        Assert.NotNull(typeof(CalibrationObservationInput).GetConstructor(new[]
        {
            typeof(VisionFrame), typeof(IEnumerable<CalibrationImageFeature>), typeof(Guid),
            typeof(Guid), typeof(string)
        }));
        Assert.NotNull(typeof(CalibrationObservationInput).GetConstructor(new[]
        {
            typeof(VisionFrame), typeof(IEnumerable<CalibrationImageFeature>), typeof(Guid),
            typeof(Guid), typeof(string), typeof(CalibrationExtractionReceipt)
        }));
        Assert.NotNull(typeof(CalibrationProcedureComputationResult).GetConstructor(new[]
        {
            typeof(CalibrationCoefficientPayload),
            typeof(IEnumerable<CalibrationQualityMetric>),
            typeof(IEnumerable<CalibrationProcedureDiagnostic>)
        }));
        Assert.NotNull(typeof(CalibrationProcedureComputationResult).GetConstructor(new[]
        {
            typeof(CalibrationCoefficientPayload),
            typeof(IEnumerable<CalibrationQualityMetric>),
            typeof(IEnumerable<CalibrationProcedureDiagnostic>),
            typeof(CalibrationComputationEvidencePayload)
        }));
    }

    [Fact]
    public void V125_S09_ObservationReceiptUsesV2AndRoundTripsWhileLegacyObservationUsesV1()
    {
        var current = ObservationEvent(Observation(ExtractionReceipt()));
        var encoded = CalibrationSessionStorageCodec.EncodeEvent(current);
        var json = Encoding.UTF8.GetString(encoded);

        Assert.StartsWith("{\"FormatVersion\":2,", json, StringComparison.Ordinal);
        Assert.Contains("\"Receipt\":{\"Format\":", json, StringComparison.Ordinal);

        var decoded = CalibrationSessionStorageCodec.DecodeEvent(encoded);
        var expectedReceipt = current.Observation!.Result.Receipt!;
        var decodedReceipt = decoded.Observation!.Result.Receipt;
        Assert.NotNull(decodedReceipt);
        Assert.Equal(expectedReceipt.Format, decodedReceipt!.Format);
        Assert.Equal(expectedReceipt.CanonicalBytesHash, decodedReceipt.CanonicalBytesHash);
        Assert.Equal(expectedReceipt.ContentHash, decodedReceipt.ContentHash);
        Assert.Equal(expectedReceipt.GetBytes(), decodedReceipt.GetBytes());
        Assert.Equal(current.Observation!.ContentHash, decoded.Observation!.ContentHash);

        var legacy = ObservationEvent(Observation());
        var legacyJson = Encoding.UTF8.GetString(CalibrationSessionStorageCodec.EncodeEvent(legacy));
        Assert.StartsWith("{\"FormatVersion\":1,", legacyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Receipt\"", legacyJson, StringComparison.Ordinal);
        var legacyDecoded = CalibrationSessionStorageCodec.DecodeEvent(
            CalibrationSessionStorageCodec.EncodeEvent(legacy));
        Assert.Null(legacyDecoded.Observation!.Result.Receipt);
        Assert.Equal(legacy.Observation!.ContentHash, legacyDecoded.Observation!.ContentHash);
    }

    [Fact]
    public void V125_S10_ObservationReceiptTamperChangesObservationAndEventBindings()
    {
        var value = ObservationEvent(Observation(ExtractionReceipt()));
        var encoded = CalibrationSessionStorageCodec.EncodeEvent(value);
        var canonical = Encoding.UTF8.GetString(encoded);
        var tampered = Encoding.UTF8.GetBytes(canonical.Replace(
            "\"CanonicalBytes\":\"BQQD\"", "\"CanonicalBytes\":\"BQQE\"",
            StringComparison.Ordinal));
        var tamperedValue = CalibrationSessionStorageCodec.DecodeEvent(tampered);

        Assert.NotEqual(value.Observation!.ContentHash, tamperedValue.Observation!.ContentHash);
        var originalEventHash = CalibrationSessionStorageCodec.EventContentHash(
            value, 1, null, encoded, 7, HashA);
        var tamperedEventHash = CalibrationSessionStorageCodec.EventContentHash(
            tamperedValue, 1, null, tampered, 7, HashA);
        Assert.NotEqual(originalEventHash, tamperedEventHash);

        var malformed = Encoding.UTF8.GetBytes(canonical.Replace(
            "\"CanonicalBytes\":\"BQQD\"", "\"CanonicalBytes\":\"*\"",
            StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() =>
            CalibrationSessionStorageCodec.DecodeEvent(malformed));
    }

    private static CalibrationComputationEvidencePayload Evidence() =>
        new(Contract("evidence-format"), new byte[] { 9, 8, 7 });

    private static CalibrationProcedureComputationResult Computation() =>
        new(new CalibrationCoefficientPayload(Contract("coeff-format"), new byte[] { 1, 2, 3 }));

    private static CalibrationProcedureComputationResult Computation(
        CalibrationComputationEvidencePayload evidence) =>
        new(new CalibrationCoefficientPayload(Contract("coeff-format"), new byte[] { 1, 2, 3 }),
            evidence: evidence);

    private static CalibrationSessionEvent Event(
        CalibrationComputationEvidencePayload? evidence = null)
    {
        var sessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var candidate = new CalibrationCandidateEvidence(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), sessionId,
            HashThree, HashFour, evidence is null ? Computation() : Computation(evidence),
            DateTimeOffset.Parse("2024-01-02T03:04:05.0000000+00:00"));
        return new CalibrationSessionEvent(
            Guid.Parse("33333333-3333-3333-3333-333333333333"), sessionId,
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            CalibrationSessionPhase.CandidateRetained, CalibrationSessionOutcome.Pending,
            "CandidateRetained", DateTimeOffset.Parse("2024-01-02T03:04:05.0000000+00:00"),
            candidate: candidate);
    }

    private static RecipeContractReference Contract(string id) =>
        new(id, "1", HashA);

    private static CalibrationExtractionReceipt ExtractionReceipt() =>
        new(Contract("extraction-receipt"), new byte[] { 5, 4, 3 });

    private static CalibrationObservationEvidence Observation(
        CalibrationExtractionReceipt? receipt = null)
    {
        var sessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var frameId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var correlation = new ExecutionCorrelationId(ExecutionKind.Calibration, frameId);
        var configuration = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var metadata = new FrameMetadata(correlation, "TopCamera", 2, 2, 2,
            VisionPixelFormat.Mono8, null, DateTimeOffset.UnixEpoch, configuration);
        var provenance = new FrameProvenance(correlation, "fixture-provider", "1", "fixture-adapter", "1",
            "fixture-sdk", "1", null, "fixture-device", null, null, "Mono8", "CanonicalRows",
            false, false, null, null, new FrameAcquisitionMilestones(1, null, null, null, null));
        var pixels = new byte[] { 1, 2, 3, 4 };
        var pixelHash = Convert.ToHexString(SHA256.HashData(pixels));
        var frame = new CalibrationFrameEvidence(sessionId, frameId, metadata, provenance,
            pixelHash, pixels.Length, pixelHash + ".bin");
        var procedure = new CalibrationProcedureDescriptor(Contract("procedure"),
            Contract("input"), CalibrationKind.Intrinsic);
        var result = new CalibrationExtractionResult(
            new[] { new CalibrationImageFeature("corner", 0, 0) },
            Array.Empty<CalibrationProcedureDiagnostic>(), receipt);
        return new CalibrationObservationEvidence(Guid.Parse("66666666-6666-6666-6666-666666666666"),
            frame, procedure, HashA, result);
    }

    private static CalibrationSessionEvent ObservationEvent(CalibrationObservationEvidence observation) =>
        new(Guid.Parse("77777777-7777-7777-7777-777777777777"), observation.Frame.SessionId,
            Guid.Parse("88888888-8888-8888-8888-888888888888"), CalibrationSessionPhase.Collecting,
            CalibrationSessionOutcome.Pending, "CalibrationObservationRetained",
            DateTimeOffset.UnixEpoch, observation: observation);
}
