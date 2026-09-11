using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Production;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Bounded canonical codec for development algorithm-result evidence. The codec does not
/// assign audit positions or timestamps; those belong to the storage transaction.
/// </summary>
internal static class AlgorithmResultStorageCodec
{
    internal const int MaximumPayloadBytes = 1024 * 1024;
    private const int FormatVersion = 1;
    private const int CanonicalizationVersion = 1;
    private const int MaximumDepth = 64;
    private const int MaximumSchemaItems = 256;
    private const int MaximumOverlayElements = 4096;
    private const int MaximumOverlayPoints = 100_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = MaximumDepth
    };

    // Format 2 is the production structured-result domain. The existing archive
    // entry points accept only format 1 and continue to reject Production kind.
    // Both domains share the same strict DTOs and bounded primitive serialization.
    internal static ProductionAlgorithmResultDocument EncodeProduction(Guid inspectionId,
        AlgorithmExecutionOutcome outcome)
    {
        if (inspectionId == Guid.Empty || outcome is null || outcome.Correlation is null ||
            outcome.Correlation.Kind != ExecutionKind.Production || outcome.Correlation.Value != inspectionId ||
            outcome.ExecutionStatus != ExecutionStatus.Success || outcome.ValidatedResult is null)
            throw Invalid("ProductionStructuredResultInputsInvalid");
        try
        {
            ValidateOutcome(outcome, production: true);
            EnsureMaterializationBudget(outcome, MaximumPayloadBytes);
            var dto = PayloadDto.FromOutcome(inspectionId, outcome);
            dto.FormatVersion = 2;
            var bytes = SerializeBounded(dto, MaximumPayloadBytes);
            var json = StrictUtf8String(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return ProductionDocument(PayloadDto.ToDecoded(dto, production: true), json, hash);
        }
        catch (PayloadCapacityExceededException) { throw Invalid("ProductionStructuredResultCapacityExceeded"); }
        catch (Exception exception) when (IsBoundedValidationException(exception))
        { throw Invalid("ProductionStructuredResultInvalid", exception); }
    }

    internal static ProductionAlgorithmResultDocument DecodeProduction(Guid inspectionId,
        string payloadJson, string expectedHash)
    {
        try
        {
            if (inspectionId == Guid.Empty || !IsSha256(expectedHash))
                throw Invalid("ProductionStructuredResultIdentityInvalid");
            var bytes = StrictUtf8(payloadJson, MaximumPayloadBytes, "ProductionStructuredResultInvalid");
            if (Convert.ToHexString(SHA256.HashData(bytes)) != expectedHash)
                throw Invalid("ProductionStructuredResultHashMismatch");
            using var json = ParseJson(bytes);
            ValidatePayloadShape(json.RootElement);
            var dto = DeserializePayload(json.RootElement);
            if (dto.FormatVersion != 2 || dto.CanonicalizationVersion != CanonicalizationVersion)
                throw Invalid("ProductionStructuredResultVersionUnsupported");
            if (!bytes.AsSpan().SequenceEqual(SerializeBounded(dto, bytes.Length)))
                throw Invalid("ProductionStructuredResultCanonicalMismatch");
            var decoded = PayloadDto.ToDecoded(dto, production: true);
            if (decoded.RecordId != inspectionId || decoded.Correlation.Value != inspectionId ||
                AlgorithmResultValidator.Validate(decoded.Result, decoded.ResultSchema).Count != 0 ||
                decoded.Result.OverlaySet.ContractId != decoded.ResultSchema.OverlayContract.Id ||
                decoded.Result.OverlaySet.ContractVersion != decoded.ResultSchema.OverlayContract.Version)
                throw Invalid("ProductionStructuredResultBindingMismatch");
            return ProductionDocument(decoded, payloadJson, expectedHash);
        }
        catch (PayloadCapacityExceededException) { throw Invalid("ProductionStructuredResultCapacityExceeded"); }
        catch (Exception exception) when (IsBoundedValidationException(exception))
        { throw Invalid("ProductionStructuredResultInvalid", exception); }
    }

    private static ProductionAlgorithmResultDocument ProductionDocument(DecodedPayload decoded,
        string json, string hash) => new(decoded.RecordId, decoded.Correlation, decoded.PreparedInstanceId,
        decoded.Algorithm, decoded.ConfigurationContentHash, decoded.ConfigurationSchemaId,
        decoded.ConfigurationSchemaVersion, decoded.ConfigurationSchemaContentHash, decoded.FrameMetadata,
        decoded.ResultSchema, decoded.Result, decoded.Timing, decoded.AdmittedMonotonicTimestamp,
        decoded.MonotonicFrequency, json, hash);

    internal static string EncodeProductionSchema(AlgorithmResultSchema schema) =>
        JsonSerializer.Serialize(SchemaDto.From(schema), Json);

    internal static AlgorithmResultSchema DecodeProductionSchema(string encoded)
    {
        _ = StrictUtf8(encoded, MaximumPayloadBytes, "ProductionResultSchemaInvalid");
        var schema = SchemaDto.ToDomain(JsonSerializer.Deserialize<SchemaDto>(encoded, Json));
        if (EncodeProductionSchema(schema) != encoded) throw Invalid("ProductionResultSchemaCanonicalMismatch");
        return schema;
    }

    internal static string EncodeProductionTiming(AlgorithmExecutionTimingSnapshot timing) =>
        JsonSerializer.Serialize(TimingDto.From(timing), Json);

    internal static AlgorithmExecutionTimingSnapshot DecodeProductionTiming(string encoded)
    {
        _ = StrictUtf8(encoded, 16384, "ProductionExecutionTimingInvalid");
        var timing = TimingDto.ToDomain(JsonSerializer.Deserialize<TimingDto>(encoded, Json));
        if (EncodeProductionTiming(timing) != encoded) throw Invalid("ProductionExecutionTimingCanonicalMismatch");
        return timing;
    }

    internal static bool TryEncode(Guid recordId, AlgorithmExecutionOutcome outcome,
        out AlgorithmResultArchiveDocument? document, out string reasonCode)
    {
        return TryEncode(recordId, outcome, MaximumPayloadBytes, out document, out reasonCode);
    }

    // The bounded capacity seam is internal and is used only by deterministic tests.
    internal static bool TryEncode(Guid recordId, AlgorithmExecutionOutcome outcome,
        int maximumPayloadBytes, out AlgorithmResultArchiveDocument? document, out string reasonCode)
    {
        document = null;
        reasonCode = string.Empty;
        if (recordId == Guid.Empty)
            return Failure("AlgorithmResultRecordIdRequired", out reasonCode);
        if (outcome is null)
            return Failure("AlgorithmResultOutcomeRequired", out reasonCode);
        if (maximumPayloadBytes is < 1 or > MaximumPayloadBytes)
            return Failure("AlgorithmResultPayloadCapacityInvalid", out reasonCode);
        if (outcome.ExecutionStatus != ExecutionStatus.Success)
            return Failure("AlgorithmResultExecutionStatusInvalid", out reasonCode);
        if (outcome.ValidatedResult is null)
            return Failure("AlgorithmResultValidatedResultRequired", out reasonCode);
        if (outcome.Correlation is null || outcome.Correlation.Kind == ExecutionKind.Production)
            return Failure("AlgorithmResultProductionForbidden", out reasonCode);

        try
        {
            ValidateOutcome(outcome);
            EnsureMaterializationBudget(outcome, maximumPayloadBytes);
            var payload = PayloadDto.FromOutcome(recordId, outcome);
            var bytes = SerializeBounded(payload, maximumPayloadBytes);
            var payloadJson = StrictUtf8String(bytes);
            var payloadHash = Convert.ToHexString(SHA256.HashData(bytes));
            document = new AlgorithmResultArchiveDocument(recordId, DateTimeOffset.UtcNow,
                outcome.PreparedInstanceId, outcome.Algorithm, outcome.Correlation,
                outcome.ConfigurationContentHash, outcome.ConfigurationSchemaId,
                outcome.ConfigurationSchemaVersion, outcome.ConfigurationSchemaContentHash,
                outcome.FrameMetadata, outcome.ResultSchema, outcome.ValidatedResult,
                outcome.Timing, outcome.AdmittedMonotonicTimestamp, outcome.MonotonicFrequency,
                payloadJson, payloadHash);
            reasonCode = "AlgorithmResultEncoded";
            return true;
        }
        catch (PayloadCapacityExceededException)
        {
            reasonCode = "AlgorithmResultPayloadTooLarge";
            return false;
        }
        catch (InvalidOperationException exception) when (exception.Message == "AlgorithmResultContractViolation")
        {
            reasonCode = exception.Message;
            return false;
        }
        catch (Exception exception) when (IsBoundedValidationException(exception))
        {
            reasonCode = "AlgorithmResultPayloadInvalid";
            return false;
        }
    }

    internal static AlgorithmResultRecord Decode(long position, DateTimeOffset recordedAtUtc,
        string payloadJson, string expectedHash)
    {
        try
        {
            if (position < 0)
                throw Invalid("AlgorithmResultPositionInvalid");
            if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
                throw Invalid("AlgorithmResultRecordedAtUtcInvalid");
            if (!IsSha256(expectedHash))
                throw Invalid("AlgorithmResultPayloadHashInvalid");

            var bytes = StrictUtf8(payloadJson, MaximumPayloadBytes, "AlgorithmResultPayloadInvalid");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedHash,
                    StringComparison.Ordinal))
                throw Invalid("AlgorithmResultPayloadHashMismatch");

            using var json = ParseJson(bytes);
            ValidatePayloadShape(json.RootElement);
            var dto = DeserializePayload(json.RootElement);
            if (dto.FormatVersion != FormatVersion || dto.CanonicalizationVersion != CanonicalizationVersion)
                throw Invalid("AlgorithmResultPayloadVersionUnsupported");

            // The input has already passed the hard byte limit, so use its exact
            // length as the committed budget instead of renting an archive-sized
            // buffer for every small history row.
            var canonical = SerializeBounded(dto, bytes.Length);
            if (!bytes.AsSpan().SequenceEqual(canonical))
                throw Invalid("AlgorithmResultPayloadCanonicalMismatch");

            var decoded = PayloadDto.ToDecoded(dto);
            var issues = AlgorithmResultValidator.Validate(decoded.Result, decoded.ResultSchema);
            if (issues.Count != 0)
                throw Invalid("AlgorithmResultContractViolation");

            if (decoded.Result.OverlaySet.ContractId != decoded.ResultSchema.OverlayContract.Id ||
                decoded.Result.OverlaySet.ContractVersion != decoded.ResultSchema.OverlayContract.Version)
                throw Invalid("AlgorithmResultOverlayContractMismatch");

            return new AlgorithmResultRecord(position, decoded.RecordId, recordedAtUtc, expectedHash,
                decoded.PreparedInstanceId, decoded.Algorithm, decoded.ConfigurationContentHash,
                decoded.ConfigurationSchemaId, decoded.ConfigurationSchemaVersion,
                decoded.ConfigurationSchemaContentHash, decoded.FrameMetadata, decoded.ResultSchema,
                decoded.Result, decoded.Timing, decoded.AdmittedMonotonicTimestamp,
                decoded.MonotonicFrequency);
        }
        catch (PayloadCapacityExceededException)
        {
            throw Invalid("AlgorithmResultPayloadTooLarge");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (IsBoundedValidationException(exception))
        {
            throw Invalid("AlgorithmResultPayloadInvalid");
        }
    }

    private static void ValidateOutcome(AlgorithmExecutionOutcome outcome, bool production = false)
    {
        if (outcome.Correlation.Value == Guid.Empty ||
            (production ? outcome.Correlation.Kind != ExecutionKind.Production :
                outcome.Correlation.Kind is not (ExecutionKind.Manual or ExecutionKind.Qualification)))
            throw Invalid("AlgorithmResultCorrelationInvalid");
        if (outcome.PreparedInstanceId == Guid.Empty)
            throw Invalid("AlgorithmResultPreparedInstanceInvalid");
        if (outcome.FrameMetadata is null || outcome.ResultSchema is null || outcome.Timing is null)
            throw Invalid("AlgorithmResultMetadataRequired");
        if (outcome.FrameMetadata.Correlation != outcome.Correlation)
            throw Invalid("AlgorithmResultCorrelationMismatch");
        if (!IsSha256(outcome.ConfigurationContentHash) ||
            !IsSha256(outcome.ConfigurationSchemaContentHash))
            throw Invalid("AlgorithmResultConfigurationHashInvalid");
        if (!IsIdentifier(outcome.ConfigurationSchemaId) || !IsIdentifier(outcome.ConfigurationSchemaVersion))
            throw Invalid("AlgorithmResultConfigurationIdentityInvalid");
        _ = new AlgorithmIdentity(outcome.Algorithm.Id, outcome.Algorithm.Version);
        _ = new AlgorithmExecutionTimingSnapshotValidation(outcome.Timing);
        if (outcome.AdmittedMonotonicTimestamp < 0 || outcome.MonotonicFrequency < 1)
            throw Invalid("AlgorithmResultMonotonicEvidenceInvalid");

        var issues = AlgorithmResultValidator.Validate(outcome.ValidatedResult, outcome.ResultSchema);
        if (issues.Count != 0)
            throw Invalid("AlgorithmResultContractViolation");
    }

    /// <summary>
    /// Checks the large collection dimensions before the DTO projection allocates
    /// per-point objects. The estimate is intentionally conservative: a payload
    /// which could not fit under the hard byte budget is rejected before projection.
    /// </summary>
    private static void EnsureMaterializationBudget(AlgorithmExecutionOutcome outcome, int maximumBytes)
    {
        long estimate = 16 * 1024;
        var overlay = outcome.ValidatedResult!.OverlaySet;
        foreach (var primitive in overlay.Primitives)
        {
            estimate = checked(estimate + 512L + (long)primitive.PointCount * 96);
            if (primitive is OverlayText text)
                estimate = checked(estimate + TextBudget(text.Text));
            if (estimate > maximumBytes)
                throw new PayloadCapacityExceededException();
        }

        // Result schemas and measurements are also bounded by their contracts, but
        // their strings can be large. Count their encoded upper bound without making
        // any collection or point copies.
        foreach (var field in outcome.ResultSchema.Measurements)
        {
            estimate = checked(estimate + TextBudget(field.Key) + TextBudget(field.Unit));
            if (field.Constraints?.AllowedValues is { } values)
                foreach (var value in values) estimate = checked(estimate + TextBudget(value));
            if (estimate > maximumBytes) throw new PayloadCapacityExceededException();
        }
        foreach (var measurement in outcome.ValidatedResult.Measurements)
        {
            estimate = checked(estimate + TextBudget(measurement.Key) + TextBudget(measurement.Unit));
            if (measurement.Value.Type is AlgorithmScalarType.String or AlgorithmScalarType.Enum)
                estimate = checked(estimate + TextBudget(measurement.Value.Type == AlgorithmScalarType.String
                    ? measurement.Value.AsString() : measurement.Value.AsEnum()));
            if (estimate > maximumBytes) throw new PayloadCapacityExceededException();
        }
    }

    private static long TextBudget(string value)
    {
        // JsonEncodedText accounts for the actual strict encoder expansion without
        // retaining the encoded value. The fixed envelope allowance covers property
        // names, punctuation and scalar fields around the value.
        var encodedLength = JsonEncodedText.Encode(value, Json.Encoder).EncodedUtf8Bytes.Length;
        return checked(64L + encodedLength);
    }

    private static byte[] SerializeBounded(PayloadDto payload, int maximumBytes)
    {
        using var buffer = new BoundedBufferWriter(maximumBytes);
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = Json.Encoder,
                Indented = false,
                SkipValidation = false
            }))
            {
                JsonSerializer.Serialize(writer, payload, Json);
                writer.Flush();
            }

            return buffer.ToArray();
        }
        catch (PayloadCapacityExceededException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Invalid("AlgorithmResultPayloadInvalid", exception);
        }
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
        }
        catch (JsonException exception)
        {
            throw Invalid("AlgorithmResultPayloadJsonInvalid", exception);
        }
    }

    private static PayloadDto DeserializePayload(JsonElement root)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<PayloadDto>(root.GetRawText(), Json);
            return dto ?? throw Invalid("AlgorithmResultPayloadInvalid");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Invalid("AlgorithmResultPayloadInvalid", exception);
        }
    }

    private static void ValidatePayloadShape(JsonElement root)
    {
        Object(root, TopProperties);
        Object(root.GetProperty("Correlation"), CorrelationProperties);
        Object(root.GetProperty("Algorithm"), AlgorithmProperties);
        Object(root.GetProperty("Configuration"), ConfigurationProperties);
        var frame = root.GetProperty("Frame");
        Object(frame, FrameProperties);
        Object(frame.GetProperty("Correlation"), CorrelationProperties);
        Object(frame.GetProperty("EffectiveCameraConfiguration"), CameraProperties);
        Object(frame.GetProperty("EffectiveCameraConfiguration").GetProperty("RegionOfInterest"), RoiProperties);
        var whiteBalance = frame.GetProperty("EffectiveCameraConfiguration").GetProperty("WhiteBalanceRgb");
        if (whiteBalance.ValueKind != JsonValueKind.Null) Object(whiteBalance, WhiteBalanceProperties);

        var schema = root.GetProperty("ResultSchema");
        Object(schema, SchemaProperties);
        Array(schema.GetProperty("Measurements"), MaximumSchemaItems, field =>
        {
            Object(field, FieldProperties);
            var constraints = field.GetProperty("Constraints");
            if (constraints.ValueKind != JsonValueKind.Null)
            {
                Object(constraints, ConstraintProperties);
                var allowed = constraints.GetProperty("AllowedValues");
                if (allowed.ValueKind != JsonValueKind.Null) Array(allowed, MaximumSchemaItems);
            }

            var defaultValue = field.GetProperty("AuthoringDefault");
            if (defaultValue.ValueKind != JsonValueKind.Null) Scalar(defaultValue);
        });
        Array(schema.GetProperty("ReasonCodes"), MaximumSchemaItems);
        Object(schema.GetProperty("OverlayContract"), OverlayContractProperties);

        var result = root.GetProperty("Result");
        Object(result, ResultProperties);
        Array(result.GetProperty("Measurements"), MaximumSchemaItems, measurement =>
        {
            Object(measurement, ResultMeasurementProperties);
            Scalar(measurement.GetProperty("Value"));
        });
        Overlay(result.GetProperty("OverlaySet"));

        Object(root.GetProperty("OverlayRenderingContract"), RenderingContractProperties);
        var timing = root.GetProperty("Timing");
        Object(timing, TimingProperties);
        Object(timing.GetProperty("Recipe"), RecipeProperties);
    }

    private static void Overlay(JsonElement overlay)
    {
        Object(overlay, OverlayProperties);
        Array(overlay.GetProperty("Primitives"), MaximumOverlayElements, primitive =>
        {
            Object(primitive, PrimitiveProperties);
            var style = primitive.GetProperty("Style");
            Object(style, StyleProperties);
            Object(style.GetProperty("StrokeColor"), ColorProperties);
            var fillColor = style.GetProperty("FillColor");
            if (fillColor.ValueKind != JsonValueKind.Null) Object(fillColor, ColorProperties);
            var fields = new[] { "Center", "Start", "End", "TopLeft", "Anchor" };
            foreach (var name in fields)
            {
                var point = primitive.GetProperty(name);
                if (point.ValueKind != JsonValueKind.Null) Object(point, PointProperties);
            }

            var points = primitive.GetProperty("Points");
            if (points.ValueKind != JsonValueKind.Null)
                Array(points, MaximumOverlayPoints, point => Object(point, PointProperties));
        });
    }

    private static void Scalar(JsonElement scalar) => Object(scalar, ScalarProperties);

    private static void Object(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid("AlgorithmResultPayloadShapeInvalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw Invalid("AlgorithmResultPayloadDuplicateProperty");
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid("AlgorithmResultPayloadUnknownProperty");
        }

        if (names.Count != expected.Length || expected.Any(name => !names.Contains(name)))
            throw Invalid("AlgorithmResultPayloadPropertyMissing");
    }

    private static void Array(JsonElement element, int maximum, Action<JsonElement>? each = null)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Invalid("AlgorithmResultPayloadArrayInvalid");
        if (element.GetArrayLength() > maximum)
            throw Invalid("AlgorithmResultPayloadCapacityExceeded");
        if (each is null) return;
        foreach (var item in element.EnumerateArray()) each(item);
    }

    private static byte[] StrictUtf8(string? value, int maximumBytes, string reason)
    {
        if (value is null || value.Length > maximumBytes)
            throw Invalid(reason);
        try
        {
            var encoding = new UTF8Encoding(false, true);
            if (encoding.GetByteCount(value) > maximumBytes)
                throw Invalid(reason);
            return encoding.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw Invalid("AlgorithmResultPayloadUtf8Invalid", exception);
        }
    }

    private static string StrictUtf8String(byte[] value)
    {
        try { return new UTF8Encoding(false, true).GetString(value); }
        catch (DecoderFallbackException exception) { throw Invalid("AlgorithmResultPayloadUtf8Invalid", exception); }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool Failure(string reason, out string reasonCode)
    {
        reasonCode = reason;
        return false;
    }

    private static InvalidOperationException Invalid(string reason, Exception? inner = null) =>
        new(reason, inner);

    private static bool IsBoundedValidationException(Exception exception) =>
        exception is ArgumentException or FormatException or OverflowException or JsonException or
        NotSupportedException or InvalidOperationException;

    private sealed class PayloadCapacityExceededException : IOException { }

    /// <summary>
    /// Incremental writer used by Utf8JsonWriter. Its rented backing segment is bounded
    /// by the payload limit plus one bounded token allowance; committed bytes are still
    /// checked independently against the payload limit. This avoids discarded segment
    /// tails making a valid near-limit payload fail without retaining a large array per
    /// history row after serialization.
    /// </summary>
    private sealed class BoundedBufferWriter : IBufferWriter<byte>, IDisposable
    {
        // Utf8JsonWriter may request a large contiguous span for a single token and
        // then consume only part of it. Keep that bounded slack separate from the
        // committed payload limit so discarded segment tails cannot reject a valid
        // payload near the limit or cause an unbounded allocation.
        // JavaScriptEncoder.Default can expand a permitted 65,536-character
        // non-ASCII overlay text to roughly 384 KiB of JSON escapes. Utf8JsonWriter
        // may request a larger contiguous growth span while emitting that token;
        // 768 KiB remains a fixed, bounded allowance for the one token.
        private const int MaximumTokenBytes = 768 * 1024;
        private readonly int _maximumBytes;
        private readonly int _allocationLimit;
        private byte[]? _buffer;
        private int _committed;
        private int _pending;

        public BoundedBufferWriter(int maximumBytes)
        {
            _maximumBytes = maximumBytes;
            _allocationLimit = checked(maximumBytes + MaximumTokenBytes);
            _buffer = ArrayPool<byte>.Shared.Rent(_allocationLimit);
        }

        public void Advance(int count)
        {
            if (count < 0 || count > _pending)
                throw new PayloadCapacityExceededException();
            if (_buffer is null)
                throw new PayloadCapacityExceededException();
            var committed = checked(_committed + count);
            if (committed > _maximumBytes)
                throw new PayloadCapacityExceededException();
            _committed = committed;
            _pending = 0;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => GetWritable(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => GetWritable(sizeHint).Span;

        private Memory<byte> GetWritable(int sizeHint)
        {
            if (sizeHint < 0 || _pending != 0)
                throw new PayloadCapacityExceededException();
            if (_buffer is null)
                throw new PayloadCapacityExceededException();
            var required = sizeHint == 0 ? 1 : sizeHint;
            if (required > MaximumTokenBytes)
                throw new PayloadCapacityExceededException();

            var available = _allocationLimit - _committed;
            if (available < required)
                throw new PayloadCapacityExceededException();
            _pending = available;
            return _buffer.AsMemory(_committed, available);
        }

        public byte[] ToArray()
        {
            if (_pending != 0)
                throw new PayloadCapacityExceededException();
            if (_buffer is null)
                throw new PayloadCapacityExceededException();
            return _buffer.AsSpan(0, _committed).ToArray();
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = null;
            _pending = 0;
            _committed = 0;
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed class AlgorithmExecutionTimingSnapshotValidation
    {
        public AlgorithmExecutionTimingSnapshotValidation(AlgorithmExecutionTimingSnapshot timing)
        {
            if (!IsIdentifier(timing.PolicyId) || !IsIdentifier(timing.PolicyVersion) ||
                !IsSha256(timing.PolicyContentHash) ||
                !AlgorithmExecutionPolicy.IsRepresentableDuration(timing.AlgorithmExecutionTimeout) ||
                !AlgorithmExecutionPolicy.IsRepresentableDuration(timing.CancellationGracePeriod))
                throw Invalid("AlgorithmResultTimingInvalid");
            var recipe = timing.Recipe;
            if (!IsIdentifier(recipe.Id) || !IsIdentifier(recipe.Version) || !IsSha256(recipe.ContentHash))
                throw Invalid("AlgorithmResultRecipeInvalid");
        }
    }

    private static bool IsIdentifier(string? value) => value is { Length: > 0 } && value.Trim() == value &&
        value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static readonly string[] TopProperties =
    {
        "FormatVersion", "CanonicalizationVersion", "RecordId", "Correlation", "PreparedInstanceId",
        "Algorithm", "Configuration", "Frame", "ResultSchema", "Result", "OverlayRenderingContract",
        "Timing", "AdmittedMonotonicTimestamp", "MonotonicFrequency"
    };
    private static readonly string[] CorrelationProperties = { "Kind", "Value" };
    private static readonly string[] AlgorithmProperties = { "Id", "Version" };
    private static readonly string[] ConfigurationProperties =
        { "SchemaId", "SchemaVersion", "SchemaContentHash", "ContentHash" };
    private static readonly string[] FrameProperties =
    {
        "Correlation", "LogicalCameraRole", "Width", "Height", "StrideBytes", "ValidRowBytes",
        "RequiredBufferLength", "FullBufferLayoutLength", "PixelFormat", "ValidBits", "HostCaptureUtc",
        "EffectiveCameraConfiguration"
    };
    private static readonly string[] CameraProperties =
    {
        "ProductionAcquisitionMode", "ExposureTimeUs", "GainDb", "RegionOfInterest", "PixelFormat",
        "ValidBits", "AcquisitionTimeoutMs", "TriggerDelayUs", "WhiteBalanceRgb"
    };
    private static readonly string[] RoiProperties = { "OffsetX", "OffsetY", "Width", "Height" };
    private static readonly string[] WhiteBalanceProperties = { "Red", "Green", "Blue" };
    private static readonly string[] SchemaProperties =
        { "Id", "Version", "ContentHash", "Measurements", "ReasonCodes", "OverlayContract" };
    private static readonly string[] FieldProperties =
        { "Key", "Type", "Unit", "Required", "Constraints", "AuthoringDefault" };
    private static readonly string[] ConstraintProperties =
        { "MinInt64", "MaxInt64", "MinFloat64", "MaxFloat64", "MinLength", "MaxLength", "AllowedValues" };
    private static readonly string[] OverlayContractProperties =
        { "Id", "Version", "MaximumElements", "MaximumTotalPoints", "MaximumPointsPerElement", "MaximumTextLength", "ContentHash" };
    private static readonly string[] ResultProperties = { "Decision", "ReasonCode", "Measurements", "OverlaySet" };
    private static readonly string[] ResultMeasurementProperties = { "Key", "Unit", "Value" };
    private static readonly string[] OverlayProperties = { "ContractId", "ContractVersion", "Primitives" };
    private static readonly string[] RenderingContractProperties = { "Id", "Version", "ContentHash" };
    private static readonly string[] TimingProperties =
        { "Recipe", "PolicyId", "PolicyVersion", "PolicyContentHash", "AlgorithmExecutionTimeoutTicks", "CancellationGracePeriodTicks" };
    private static readonly string[] RecipeProperties = { "Id", "Version", "ContentHash" };
    private static readonly string[] ScalarProperties = { "Type", "Boolean", "Int64", "Float64", "Text" };
    private static readonly string[] PrimitiveProperties =
    {
        "Kind", "Style", "Center", "Start", "End", "Points", "TopLeft", "Anchor", "Width", "Height",
        "RotationDegrees", "Radius", "RadiusX", "RadiusY", "MarkerKind", "Size", "Text", "AnchorKind"
    };
    private static readonly string[] StyleProperties =
        { "StrokeColor", "StrokeWidth", "FillColor", "StrokePattern", "MarkerSize", "TextSize", "TextAnchor" };
    private static readonly string[] ColorProperties = { "Red", "Green", "Blue", "Alpha" };
    private static readonly string[] PointProperties = { "X", "Y" };

    private sealed class PayloadDto
    {
        public int FormatVersion { get; set; }
        public int CanonicalizationVersion { get; set; }
        public string? RecordId { get; set; }
        public CorrelationDto? Correlation { get; set; }
        public string? PreparedInstanceId { get; set; }
        public AlgorithmDto? Algorithm { get; set; }
        public ConfigurationDto? Configuration { get; set; }
        public FrameDto? Frame { get; set; }
        public SchemaDto? ResultSchema { get; set; }
        public ResultDto? Result { get; set; }
        public RenderingContractDto? OverlayRenderingContract { get; set; }
        public TimingDto? Timing { get; set; }
        public long AdmittedMonotonicTimestamp { get; set; }
        public long MonotonicFrequency { get; set; }

        internal static PayloadDto FromOutcome(Guid recordId, AlgorithmExecutionOutcome outcome) => new()
        {
            FormatVersion = AlgorithmResultStorageCodec.FormatVersion,
            CanonicalizationVersion = AlgorithmResultStorageCodec.CanonicalizationVersion,
            RecordId = recordId.ToString("D"),
            Correlation = CorrelationDto.From(outcome.Correlation),
            PreparedInstanceId = outcome.PreparedInstanceId.ToString("D"),
            Algorithm = new AlgorithmDto { Id = outcome.Algorithm.Id, Version = outcome.Algorithm.Version },
            Configuration = new ConfigurationDto
            {
                SchemaId = outcome.ConfigurationSchemaId,
                SchemaVersion = outcome.ConfigurationSchemaVersion,
                SchemaContentHash = outcome.ConfigurationSchemaContentHash,
                ContentHash = outcome.ConfigurationContentHash
            },
            Frame = FrameDto.From(outcome.FrameMetadata),
            ResultSchema = SchemaDto.From(outcome.ResultSchema),
            Result = ResultDto.From(outcome.ValidatedResult!),
            OverlayRenderingContract = new RenderingContractDto
            {
                Id = SharpInspect.Abstractions.OverlayRenderingContract.Id,
                Version = SharpInspect.Abstractions.OverlayRenderingContract.Version,
                ContentHash = SharpInspect.Abstractions.OverlayRenderingContract.ContentHash
            },
            Timing = TimingDto.From(outcome.Timing),
            AdmittedMonotonicTimestamp = outcome.AdmittedMonotonicTimestamp,
            MonotonicFrequency = outcome.MonotonicFrequency
        };

        internal static DecodedPayload ToDecoded(PayloadDto dto, bool production = false)
        {
            var recordId = GuidValue(dto.RecordId, "AlgorithmResultRecordIdInvalid");
            var correlation = CorrelationDto.ToDomain(dto.Correlation, production);
            var algorithm = new AlgorithmIdentity(Required(dto.Algorithm?.Id, "AlgorithmResultAlgorithmInvalid"),
                Required(dto.Algorithm?.Version, "AlgorithmResultAlgorithmInvalid"));
            var configuration = dto.Configuration ?? throw Invalid("AlgorithmResultConfigurationInvalid");
            var resultSchema = SchemaDto.ToDomain(dto.ResultSchema);
            var result = ResultDto.ToDomain(dto.Result);
            if (dto.OverlayRenderingContract is null ||
                dto.OverlayRenderingContract.Id != SharpInspect.Abstractions.OverlayRenderingContract.Id ||
                dto.OverlayRenderingContract.Version != SharpInspect.Abstractions.OverlayRenderingContract.Version ||
                dto.OverlayRenderingContract.ContentHash != SharpInspect.Abstractions.OverlayRenderingContract.ContentHash)
                throw Invalid("AlgorithmResultRenderingContractMismatch");
            var frame = FrameDto.ToDomain(dto.Frame, production);
            if (frame.Correlation != correlation)
                throw Invalid("AlgorithmResultCorrelationMismatch");
            var timing = TimingDto.ToDomain(dto.Timing);
            var configSchemaId = Required(configuration.SchemaId, "AlgorithmResultConfigurationInvalid");
            var configSchemaVersion = Required(configuration.SchemaVersion, "AlgorithmResultConfigurationInvalid");
            var configSchemaHash = RequiredHash(configuration.SchemaContentHash, "AlgorithmResultConfigurationHashInvalid");
            var configHash = RequiredHash(configuration.ContentHash, "AlgorithmResultConfigurationHashInvalid");
            var preparedId = GuidValue(dto.PreparedInstanceId, "AlgorithmResultPreparedInstanceInvalid");
            if (preparedId == Guid.Empty || dto.AdmittedMonotonicTimestamp < 0 || dto.MonotonicFrequency < 1)
                throw Invalid("AlgorithmResultMonotonicEvidenceInvalid");
            return new DecodedPayload(recordId, correlation, preparedId, algorithm, configHash,
                configSchemaId, configSchemaVersion, configSchemaHash, frame, resultSchema, result,
                timing, dto.AdmittedMonotonicTimestamp, dto.MonotonicFrequency);
        }
    }

    private sealed record DecodedPayload(Guid RecordId, ExecutionCorrelationId Correlation,
        Guid PreparedInstanceId, AlgorithmIdentity Algorithm, string ConfigurationContentHash,
        string ConfigurationSchemaId, string ConfigurationSchemaVersion, string ConfigurationSchemaContentHash,
        FrameMetadata FrameMetadata, AlgorithmResultSchema ResultSchema, AlgorithmResult Result,
        AlgorithmExecutionTimingSnapshot Timing, long AdmittedMonotonicTimestamp, long MonotonicFrequency);

    private sealed class CorrelationDto
    {
        public string? Kind { get; set; }
        public string? Value { get; set; }
        internal static CorrelationDto From(ExecutionCorrelationId value) => new()
        { Kind = value.Kind.ToString(), Value = value.Value.ToString("D") };
        internal static ExecutionCorrelationId ToDomain(CorrelationDto? value, bool production = false)
        {
            if (value is null) throw Invalid("AlgorithmResultCorrelationInvalid");
            var kind = EnumValue<ExecutionKind>(value.Kind, "AlgorithmResultCorrelationInvalid");
            var id = GuidValue(value.Value, "AlgorithmResultCorrelationInvalid");
            if (id == Guid.Empty || (production ? kind != ExecutionKind.Production : kind == ExecutionKind.Production))
                throw Invalid("AlgorithmResultProductionForbidden");
            return new ExecutionCorrelationId(kind, id);
        }
    }

    private sealed class AlgorithmDto { public string? Id { get; set; } public string? Version { get; set; } }
    private sealed class ConfigurationDto
    {
        public string? SchemaId { get; set; }
        public string? SchemaVersion { get; set; }
        public string? SchemaContentHash { get; set; }
        public string? ContentHash { get; set; }
    }

    private sealed class FrameDto
    {
        public CorrelationDto? Correlation { get; set; }
        public string? LogicalCameraRole { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int StrideBytes { get; set; }
        public int ValidRowBytes { get; set; }
        public long RequiredBufferLength { get; set; }
        public long FullBufferLayoutLength { get; set; }
        public string? PixelFormat { get; set; }
        public int? ValidBits { get; set; }
        public string? HostCaptureUtc { get; set; }
        public CameraDto? EffectiveCameraConfiguration { get; set; }

        internal static FrameDto From(FrameMetadata value) => new()
        {
            Correlation = CorrelationDto.From(value.Correlation), LogicalCameraRole = value.LogicalCameraRole,
            Width = value.Width, Height = value.Height, StrideBytes = value.StrideBytes,
            ValidRowBytes = value.ValidRowBytes, RequiredBufferLength = value.RequiredBufferLength,
            FullBufferLayoutLength = value.FullBufferLayoutLength, PixelFormat = value.PixelFormat.ToString(),
            ValidBits = value.ValidBits, HostCaptureUtc = value.HostCaptureUtc.ToString("O", CultureInfo.InvariantCulture),
            EffectiveCameraConfiguration = CameraDto.From(value.EffectiveCameraConfiguration)
        };

        internal FrameMetadata ToDomain(bool production = false)
        {
            var correlation = CorrelationDto.ToDomain(Correlation, production);
            var pixelFormat = EnumValue<VisionPixelFormat>(PixelFormat, "AlgorithmResultFrameInvalid");
            var utc = UtcValue(HostCaptureUtc, "AlgorithmResultFrameInvalid");
            var camera = CameraDto.ToDomain(EffectiveCameraConfiguration);
            var frame = new FrameMetadata(correlation, Required(LogicalCameraRole, "AlgorithmResultFrameInvalid"),
                Width, Height, StrideBytes, pixelFormat, ValidBits, utc, camera);
            if (frame.ValidRowBytes != ValidRowBytes || frame.RequiredBufferLength != RequiredBufferLength ||
                frame.FullBufferLayoutLength != FullBufferLayoutLength)
                throw Invalid("AlgorithmResultFrameDerivedValueMismatch");
            return frame;
        }
        internal static FrameMetadata ToDomain(FrameDto? value, bool production = false) =>
            (value ?? throw Invalid("AlgorithmResultFrameInvalid")).ToDomain(production);
    }

    private sealed class CameraDto
    {
        public string? ProductionAcquisitionMode { get; set; }
        public double ExposureTimeUs { get; set; }
        public double GainDb { get; set; }
        public RoiDto? RegionOfInterest { get; set; }
        public string? PixelFormat { get; set; }
        public int? ValidBits { get; set; }
        public int AcquisitionTimeoutMs { get; set; }
        public double TriggerDelayUs { get; set; }
        public WhiteBalanceDto? WhiteBalanceRgb { get; set; }

        internal static CameraDto From(EffectiveCameraConfiguration value) => new()
        {
            ProductionAcquisitionMode = value.ProductionAcquisitionMode.ToString(), ExposureTimeUs = value.ExposureTimeUs,
            GainDb = value.GainDb, RegionOfInterest = new RoiDto { OffsetX = value.RegionOfInterest.OffsetX,
                OffsetY = value.RegionOfInterest.OffsetY, Width = value.RegionOfInterest.Width, Height = value.RegionOfInterest.Height },
            PixelFormat = value.PixelFormat.ToString(), ValidBits = value.ValidBits,
            AcquisitionTimeoutMs = value.AcquisitionTimeoutMs, TriggerDelayUs = value.TriggerDelayUs,
            WhiteBalanceRgb = value.WhiteBalanceRgb is null ? null : new WhiteBalanceDto
            { Red = value.WhiteBalanceRgb.Red, Green = value.WhiteBalanceRgb.Green, Blue = value.WhiteBalanceRgb.Blue }
        };

        internal static EffectiveCameraConfiguration ToDomain(CameraDto? value)
        {
            if (value is null) throw Invalid("AlgorithmResultCameraInvalid");
            var mode = EnumValue<ProductionAcquisitionMode>(value.ProductionAcquisitionMode, "AlgorithmResultCameraInvalid");
            var format = EnumValue<VisionPixelFormat>(value.PixelFormat, "AlgorithmResultCameraInvalid");
            var roi = value.RegionOfInterest ?? throw Invalid("AlgorithmResultCameraInvalid");
            var white = value.WhiteBalanceRgb is null ? null :
                new WhiteBalanceRgb(value.WhiteBalanceRgb.Red, value.WhiteBalanceRgb.Green, value.WhiteBalanceRgb.Blue);
            return new EffectiveCameraConfiguration(mode, value.ExposureTimeUs, value.GainDb,
                new RegionOfInterest(roi.OffsetX, roi.OffsetY, roi.Width, roi.Height), format,
                value.ValidBits, value.AcquisitionTimeoutMs, value.TriggerDelayUs, white);
        }
    }
    private sealed class RoiDto { public int OffsetX { get; set; } public int OffsetY { get; set; } public int Width { get; set; } public int Height { get; set; } }
    private sealed class WhiteBalanceDto { public double Red { get; set; } public double Green { get; set; } public double Blue { get; set; } }

    private sealed class SchemaDto
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? ContentHash { get; set; }
        public List<FieldDto>? Measurements { get; set; }
        public List<string>? ReasonCodes { get; set; }
        public OverlayContractDto? OverlayContract { get; set; }

        internal static SchemaDto From(AlgorithmResultSchema value) => new()
        {
            Id = value.Id, Version = value.Version, ContentHash = value.ContentHash,
            Measurements = value.Measurements.OrderBy(item => item.Key, StringComparer.Ordinal).Select(FieldDto.From).ToList(),
            ReasonCodes = value.ReasonCodes.OrderBy(item => item, StringComparer.Ordinal).ToList(),
            OverlayContract = OverlayContractDto.From(value.OverlayContract)
        };

        internal static AlgorithmResultSchema ToDomain(SchemaDto? value)
        {
            if (value is null || value.Measurements is null || value.ReasonCodes is null || value.OverlayContract is null)
                throw Invalid("AlgorithmResultSchemaInvalid");
            var fields = value.Measurements.Select(FieldDto.ToDomain).ToList();
            var schema = new AlgorithmResultSchema(Required(value.Id, "AlgorithmResultSchemaInvalid"),
                Required(value.Version, "AlgorithmResultSchemaInvalid"), fields, value.ReasonCodes,
                OverlayContractDto.ToDomain(value.OverlayContract));
            if (!string.Equals(schema.ContentHash, RequiredHash(value.ContentHash, "AlgorithmResultSchemaHashInvalid"), StringComparison.Ordinal))
                throw Invalid("AlgorithmResultSchemaHashMismatch");
            return schema;
        }
    }

    private sealed class FieldDto
    {
        public string? Key { get; set; }
        public string? Type { get; set; }
        public string? Unit { get; set; }
        public bool Required { get; set; }
        public ConstraintDto? Constraints { get; set; }
        public ScalarDto? AuthoringDefault { get; set; }
        internal static FieldDto From(AlgorithmFieldDefinition value) => new()
        { Key = value.Key, Type = value.Type.ToString(), Unit = value.Unit, Required = value.Required,
            Constraints = ConstraintDto.From(value.Constraints), AuthoringDefault = value.AuthoringDefault is null ? null : ScalarDto.From(value.AuthoringDefault) };
        internal static AlgorithmFieldDefinition ToDomain(FieldDto value)
        {
            if (value is null) throw Invalid("AlgorithmResultSchemaFieldInvalid");
            var type = EnumValue<AlgorithmScalarType>(value.Type, "AlgorithmResultSchemaFieldInvalid");
            if (value.AuthoringDefault is not null)
                throw Invalid("AlgorithmResultDefaultsForbidden");
            return new AlgorithmFieldDefinition(Required(value.Key, "AlgorithmResultSchemaFieldInvalid"), type,
                Required(value.Unit, "AlgorithmResultSchemaFieldInvalid"), value.Required,
                ConstraintDto.ToDomain(value.Constraints), null);
        }
    }

    private sealed class ConstraintDto
    {
        public long? MinInt64 { get; set; }
        public long? MaxInt64 { get; set; }
        public double? MinFloat64 { get; set; }
        public double? MaxFloat64 { get; set; }
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
        public List<string>? AllowedValues { get; set; }
        internal static ConstraintDto? From(AlgorithmScalarConstraints? value) => value is null ? null : new()
        { MinInt64 = value.MinInt64, MaxInt64 = value.MaxInt64, MinFloat64 = value.MinFloat64,
            MaxFloat64 = value.MaxFloat64, MinLength = value.MinLength, MaxLength = value.MaxLength,
            AllowedValues = value.AllowedValues?.OrderBy(item => item, StringComparer.Ordinal).ToList() };
        internal static AlgorithmScalarConstraints? ToDomain(ConstraintDto? value) => value is null ? null :
            new(value.MinInt64, value.MaxInt64, value.MinFloat64, value.MaxFloat64,
                value.MinLength, value.MaxLength, value.AllowedValues);
    }

    private sealed class OverlayContractDto
    {
        public string? Id { get; set; }
        public string? Version { get; set; }
        public int MaximumElements { get; set; }
        public int MaximumTotalPoints { get; set; }
        public int MaximumPointsPerElement { get; set; }
        public int MaximumTextLength { get; set; }
        public string? ContentHash { get; set; }
        internal static OverlayContractDto From(OverlayContract value) => new()
        { Id = value.Id, Version = value.Version, MaximumElements = value.MaximumElements,
            MaximumTotalPoints = value.MaximumTotalPoints, MaximumPointsPerElement = value.MaximumPointsPerElement,
            MaximumTextLength = value.MaximumTextLength, ContentHash = value.ContentHash };
        internal static OverlayContract ToDomain(OverlayContractDto value)
        {
            var contract = new OverlayContract(Required(value.Id, "AlgorithmResultOverlayContractInvalid"),
                Required(value.Version, "AlgorithmResultOverlayContractInvalid"), value.MaximumElements,
                value.MaximumTotalPoints, value.MaximumPointsPerElement, value.MaximumTextLength);
            if (contract.ContentHash != RequiredHash(value.ContentHash, "AlgorithmResultOverlayContractHashInvalid"))
                throw Invalid("AlgorithmResultOverlayContractHashMismatch");
            return contract;
        }
    }

    private sealed class ResultDto
    {
        public string? Decision { get; set; }
        public string? ReasonCode { get; set; }
        public List<ResultMeasurementDto>? Measurements { get; set; }
        public OverlayDto? OverlaySet { get; set; }
        internal static ResultDto From(AlgorithmResult value) => new()
        { Decision = value.Decision.ToString(), ReasonCode = value.ReasonCode,
            Measurements = value.Measurements.OrderBy(item => item.Key, StringComparer.Ordinal).Select(ResultMeasurementDto.From).ToList(),
            OverlaySet = OverlayDto.From(value.OverlaySet) };
        internal static AlgorithmResult ToDomain(ResultDto? value)
        {
            if (value is null || value.Measurements is null || value.OverlaySet is null)
                throw Invalid("AlgorithmResultInvalid");
            var decision = EnumValue<InspectionDecision>(value.Decision, "AlgorithmResultDecisionInvalid");
            return new AlgorithmResult(decision, value.ReasonCode,
                value.Measurements.Select(ResultMeasurementDto.ToDomain), OverlayDto.ToDomain(value.OverlaySet));
        }
    }
    private sealed class ResultMeasurementDto
    {
        public string? Key { get; set; }
        public string? Unit { get; set; }
        public ScalarDto? Value { get; set; }
        internal static ResultMeasurementDto From(AlgorithmMeasurement value) => new()
        { Key = value.Key, Unit = value.Unit, Value = ScalarDto.From(value.Value) };
        internal static AlgorithmMeasurement ToDomain(ResultMeasurementDto value) => new(
            Required(value.Key, "AlgorithmResultMeasurementInvalid"), Required(value.Unit, "AlgorithmResultMeasurementInvalid"),
            ScalarDto.ToDomain(value.Value));
    }

    private sealed class ScalarDto
    {
        public string? Type { get; set; }
        public bool? Boolean { get; set; }
        public long? Int64 { get; set; }
        public double? Float64 { get; set; }
        public string? Text { get; set; }
        internal static ScalarDto From(AlgorithmScalarValue value) => value.Type switch
        {
            AlgorithmScalarType.Boolean => new() { Type = "Boolean", Boolean = value.AsBoolean() },
            AlgorithmScalarType.Int64 => new() { Type = "Int64", Int64 = value.AsInt64() },
            AlgorithmScalarType.Float64 => new() { Type = "Float64", Float64 = value.AsFloat64() },
            AlgorithmScalarType.String => new() { Type = "String", Text = value.AsString() },
            AlgorithmScalarType.Enum => new() { Type = "Enum", Text = value.AsEnum() },
            _ => throw Invalid("AlgorithmResultScalarTypeInvalid")
        };
        internal static AlgorithmScalarValue ToDomain(ScalarDto? value)
        {
            if (value is null) throw Invalid("AlgorithmResultScalarInvalid");
            var type = EnumValue<AlgorithmScalarType>(value.Type, "AlgorithmResultScalarTypeInvalid");
            return type switch
            {
                AlgorithmScalarType.Boolean when value.Boolean.HasValue && value.Int64 is null && value.Float64 is null && value.Text is null => AlgorithmScalarValue.FromBoolean(value.Boolean.Value),
                AlgorithmScalarType.Int64 when value.Int64.HasValue && value.Boolean is null && value.Float64 is null && value.Text is null => AlgorithmScalarValue.FromInt64(value.Int64.Value),
                AlgorithmScalarType.Float64 when value.Float64.HasValue && value.Boolean is null && value.Int64 is null && value.Text is null => AlgorithmScalarValue.FromFloat64(value.Float64.Value),
                AlgorithmScalarType.String when value.Text is not null && value.Boolean is null && value.Int64 is null && value.Float64 is null => AlgorithmScalarValue.FromString(value.Text),
                AlgorithmScalarType.Enum when value.Text is not null && value.Boolean is null && value.Int64 is null && value.Float64 is null => AlgorithmScalarValue.FromEnum(value.Text),
                _ => throw Invalid("AlgorithmResultScalarShapeInvalid")
            };
        }
    }

    private sealed class OverlayDto
    {
        public string? ContractId { get; set; }
        public string? ContractVersion { get; set; }
        public List<PrimitiveDto>? Primitives { get; set; }
        internal static OverlayDto From(OutputOverlaySet value) => new()
        { ContractId = value.ContractId, ContractVersion = value.ContractVersion,
            Primitives = value.Primitives.Select(PrimitiveDto.From).ToList() };
        internal static OutputOverlaySet ToDomain(OverlayDto value)
        {
            if (value.Primitives is null) throw Invalid("AlgorithmResultOverlayInvalid");
            return new OutputOverlaySet(Required(value.ContractId, "AlgorithmResultOverlayInvalid"),
                Required(value.ContractVersion, "AlgorithmResultOverlayInvalid"), value.Primitives.Select(PrimitiveDto.ToDomain));
        }
    }

    private sealed class PrimitiveDto
    {
        public string? Kind { get; set; }
        public StyleDto? Style { get; set; }
        public PointDto? Center { get; set; }
        public PointDto? Start { get; set; }
        public PointDto? End { get; set; }
        public List<PointDto>? Points { get; set; }
        public PointDto? TopLeft { get; set; }
        public PointDto? Anchor { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public double? RotationDegrees { get; set; }
        public double? Radius { get; set; }
        public double? RadiusX { get; set; }
        public double? RadiusY { get; set; }
        public string? MarkerKind { get; set; }
        public double? Size { get; set; }
        public string? Text { get; set; }
        public string? AnchorKind { get; set; }

        internal static PrimitiveDto From(OverlayPrimitive value)
        {
            var result = new PrimitiveDto { Kind = value.Kind.ToString(), Style = StyleDto.From(value.Style) };
            switch (value)
            {
                case OverlayMarker marker: result.Center = PointDto.From(marker.Center); result.MarkerKind = marker.MarkerKind.ToString(); result.Size = marker.Size; break;
                case OverlayLineSegment line: result.Start = PointDto.From(line.Start); result.End = PointDto.From(line.End); break;
                case OverlayArrow arrow: result.Start = PointDto.From(arrow.Start); result.End = PointDto.From(arrow.End); break;
                case OverlayPolyline polyline: result.Points = polyline.Points.Select(PointDto.From).ToList(); break;
                case OverlayPolygon polygon: result.Points = polygon.Points.Select(PointDto.From).ToList(); break;
                case OverlayAxisAlignedRectangle rectangle: result.TopLeft = PointDto.From(rectangle.TopLeft); result.Width = rectangle.Width; result.Height = rectangle.Height; break;
                case OverlayRotatedRectangle rectangle: result.Center = PointDto.From(rectangle.Center); result.Width = rectangle.Width; result.Height = rectangle.Height; result.RotationDegrees = rectangle.RotationDegrees; break;
                case OverlayCircle circle: result.Center = PointDto.From(circle.Center); result.Radius = circle.Radius; break;
                case OverlayEllipse ellipse: result.Center = PointDto.From(ellipse.Center); result.RadiusX = ellipse.RadiusX; result.RadiusY = ellipse.RadiusY; result.RotationDegrees = ellipse.RotationDegrees; break;
                case OverlayText text: result.Anchor = PointDto.From(text.Anchor); result.Text = text.Text; result.AnchorKind = text.AnchorKind.ToString(); break;
                default: throw Invalid("AlgorithmResultOverlayPrimitiveUnknown");
            }
            return result;
        }

        internal static OverlayPrimitive ToDomain(PrimitiveDto value)
        {
            var kind = EnumValue<OverlayPrimitiveKind>(value.Kind, "AlgorithmResultOverlayPrimitiveUnknown");
            EnsurePrimitiveShape(value, kind);
            var style = StyleDto.ToDomain(value.Style);
            return kind switch
            {
                OverlayPrimitiveKind.Marker => new OverlayMarker(Point(value.Center), EnumValue<OverlayMarkerKind>(value.MarkerKind, "AlgorithmResultOverlayEnumInvalid"), Number(value.Size, "AlgorithmResultOverlayInvalid"), style),
                OverlayPrimitiveKind.LineSegment => new OverlayLineSegment(Point(value.Start), Point(value.End), style),
                OverlayPrimitiveKind.Arrow => new OverlayArrow(Point(value.Start), Point(value.End), style),
                OverlayPrimitiveKind.Polyline => new OverlayPolyline(Points(value.Points, 2), style),
                OverlayPrimitiveKind.Polygon => new OverlayPolygon(Points(value.Points, 3), style),
                OverlayPrimitiveKind.AxisAlignedRectangle => new OverlayAxisAlignedRectangle(Point(value.TopLeft), Number(value.Width, "AlgorithmResultOverlayInvalid"), Number(value.Height, "AlgorithmResultOverlayInvalid"), style),
                OverlayPrimitiveKind.RotatedRectangle => new OverlayRotatedRectangle(Point(value.Center), Number(value.Width, "AlgorithmResultOverlayInvalid"), Number(value.Height, "AlgorithmResultOverlayInvalid"), Number(value.RotationDegrees, "AlgorithmResultOverlayInvalid"), style),
                OverlayPrimitiveKind.Circle => new OverlayCircle(Point(value.Center), Number(value.Radius, "AlgorithmResultOverlayInvalid"), style),
                OverlayPrimitiveKind.Ellipse => new OverlayEllipse(Point(value.Center), Number(value.RadiusX, "AlgorithmResultOverlayInvalid"), Number(value.RadiusY, "AlgorithmResultOverlayInvalid"), Number(value.RotationDegrees, "AlgorithmResultOverlayInvalid"), style),
                OverlayPrimitiveKind.Text => new OverlayText(Point(value.Anchor), Required(value.Text, "AlgorithmResultOverlayInvalid"), style, EnumValue<OverlayTextAnchor>(value.AnchorKind, "AlgorithmResultOverlayEnumInvalid")),
                _ => throw Invalid("AlgorithmResultOverlayPrimitiveUnknown")
            };
        }

        private static void EnsurePrimitiveShape(PrimitiveDto value, OverlayPrimitiveKind kind)
        {
            var active = kind switch
            {
                OverlayPrimitiveKind.Marker => new[] { "Center", "MarkerKind", "Size" },
                OverlayPrimitiveKind.LineSegment or OverlayPrimitiveKind.Arrow => new[] { "Start", "End" },
                OverlayPrimitiveKind.Polyline or OverlayPrimitiveKind.Polygon => new[] { "Points" },
                OverlayPrimitiveKind.AxisAlignedRectangle => new[] { "TopLeft", "Width", "Height" },
                OverlayPrimitiveKind.RotatedRectangle => new[] { "Center", "Width", "Height", "RotationDegrees" },
                OverlayPrimitiveKind.Circle => new[] { "Center", "Radius" },
                OverlayPrimitiveKind.Ellipse => new[] { "Center", "RadiusX", "RadiusY", "RotationDegrees" },
                OverlayPrimitiveKind.Text => new[] { "Anchor", "Text", "AnchorKind" },
                _ => throw Invalid("AlgorithmResultOverlayPrimitiveUnknown")
            };
            var activeSet = new HashSet<string>(active, StringComparer.Ordinal);
            var present = new (string Name, bool Present)[]
            {
                ("Center", value.Center is not null), ("Start", value.Start is not null),
                ("End", value.End is not null), ("Points", value.Points is not null),
                ("TopLeft", value.TopLeft is not null), ("Anchor", value.Anchor is not null),
                ("Width", value.Width.HasValue), ("Height", value.Height.HasValue),
                ("RotationDegrees", value.RotationDegrees.HasValue), ("Radius", value.Radius.HasValue),
                ("RadiusX", value.RadiusX.HasValue), ("RadiusY", value.RadiusY.HasValue),
                ("MarkerKind", value.MarkerKind is not null), ("Size", value.Size.HasValue),
                ("Text", value.Text is not null), ("AnchorKind", value.AnchorKind is not null)
            };
            if (present.Any(item => item.Present && !activeSet.Contains(item.Name)))
                throw Invalid("AlgorithmResultOverlayPrimitiveShapeInvalid");
        }
    }

    private sealed class StyleDto
    {
        public ColorDto? StrokeColor { get; set; }
        public double StrokeWidth { get; set; }
        public ColorDto? FillColor { get; set; }
        public string? StrokePattern { get; set; }
        public double MarkerSize { get; set; }
        public double TextSize { get; set; }
        public string? TextAnchor { get; set; }
        internal static StyleDto From(OverlayStyle value) => new()
        { StrokeColor = ColorDto.From(value.StrokeColor), StrokeWidth = value.StrokeWidth,
            FillColor = value.FillColor is null ? null : ColorDto.From(value.FillColor.Value), StrokePattern = value.StrokePattern.ToString(),
            MarkerSize = value.MarkerSize, TextSize = value.TextSize, TextAnchor = value.TextAnchor.ToString() };
        internal static OverlayStyle ToDomain(StyleDto? value)
        {
            if (value is null) throw Invalid("AlgorithmResultOverlayStyleInvalid");
            return new OverlayStyle(ColorDto.ToDomain(value.StrokeColor), value.StrokeWidth,
                value.FillColor is null ? null : ColorDto.ToDomain(value.FillColor),
                EnumValue<OverlayStrokePattern>(value.StrokePattern, "AlgorithmResultOverlayEnumInvalid"),
                value.MarkerSize, value.TextSize,
                EnumValue<OverlayTextAnchor>(value.TextAnchor, "AlgorithmResultOverlayEnumInvalid"));
        }
    }
    private sealed class ColorDto
    {
        public byte Red { get; set; }
        public byte Green { get; set; }
        public byte Blue { get; set; }
        public byte Alpha { get; set; }
        internal static ColorDto From(OverlayColor value) => new() { Red = value.Red, Green = value.Green, Blue = value.Blue, Alpha = value.Alpha };
        internal static OverlayColor ToDomain(ColorDto? value) => value is null ? throw Invalid("AlgorithmResultOverlayColorInvalid") : new(value.Red, value.Green, value.Blue, value.Alpha);
    }
    private sealed class PointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        internal static PointDto From(OverlayPoint value) => new() { X = value.X, Y = value.Y };
    }

    private sealed class TimingDto
    {
        public RecipeDto? Recipe { get; set; }
        public string? PolicyId { get; set; }
        public string? PolicyVersion { get; set; }
        public string? PolicyContentHash { get; set; }
        public long AlgorithmExecutionTimeoutTicks { get; set; }
        public long CancellationGracePeriodTicks { get; set; }
        internal static TimingDto From(AlgorithmExecutionTimingSnapshot value) => new()
        { Recipe = new RecipeDto { Id = value.Recipe.Id, Version = value.Recipe.Version, ContentHash = value.Recipe.ContentHash },
            PolicyId = value.PolicyId, PolicyVersion = value.PolicyVersion, PolicyContentHash = value.PolicyContentHash,
            AlgorithmExecutionTimeoutTicks = value.AlgorithmExecutionTimeout.Ticks,
            CancellationGracePeriodTicks = value.CancellationGracePeriod.Ticks };
        internal static AlgorithmExecutionTimingSnapshot ToDomain(TimingDto? value)
        {
            if (value is null || value.Recipe is null) throw Invalid("AlgorithmResultTimingInvalid");
            var recipe = new RecipeReference(Required(value.Recipe.Id, "AlgorithmResultRecipeInvalid"),
                Required(value.Recipe.Version, "AlgorithmResultRecipeInvalid"),
                RequiredHash(value.Recipe.ContentHash, "AlgorithmResultRecipeInvalid"));
            var timeout = new TimeSpan(value.AlgorithmExecutionTimeoutTicks);
            var grace = new TimeSpan(value.CancellationGracePeriodTicks);
            if (!IsIdentifier(value.PolicyId) || !IsIdentifier(value.PolicyVersion) ||
                !IsSha256(value.PolicyContentHash) || !AlgorithmExecutionPolicy.IsRepresentableDuration(timeout) ||
                !AlgorithmExecutionPolicy.IsRepresentableDuration(grace))
                throw Invalid("AlgorithmResultTimingInvalid");
            return new AlgorithmExecutionTimingSnapshot(recipe, value.PolicyId!, value.PolicyVersion!,
                value.PolicyContentHash!, timeout, grace);
        }
    }
    private sealed class RecipeDto { public string? Id { get; set; } public string? Version { get; set; } public string? ContentHash { get; set; } }
    private sealed class RenderingContractDto { public string? Id { get; set; } public string? Version { get; set; } public string? ContentHash { get; set; } }

    private static string Required(string? value, string reason) =>
        string.IsNullOrEmpty(value) ? throw Invalid(reason) : value;
    private static string RequiredHash(string? value, string reason) =>
        IsSha256(value) ? value! : throw Invalid(reason);
    private static Guid GuidValue(string? value, string reason) =>
        Guid.TryParseExact(value, "D", out var result) ? result : throw Invalid(reason);
    private static DateTimeOffset UtcValue(string? value, string reason)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var result) || result.Offset != TimeSpan.Zero)
            throw Invalid(reason);
        return result;
    }
    private static T EnumValue<T>(string? value, string reason) where T : struct, Enum
    {
        if (value is null || !Enum.GetNames<T>().Contains(value, StringComparer.Ordinal))
            throw Invalid(reason);
        return Enum.Parse<T>(value, false);
    }
    private static double Number(double? value, string reason) => value is { } result && double.IsFinite(result) ? result : throw Invalid(reason);
    private static OverlayPoint Point(PointDto? value) => value is null ? throw Invalid("AlgorithmResultOverlayPointInvalid") : new(value.X, value.Y);
    private static IReadOnlyList<OverlayPoint> Points(List<PointDto>? values, int minimum)
    {
        if (values is null || values.Count < minimum || values.Count > MaximumOverlayPoints)
            throw Invalid("AlgorithmResultOverlayPointsInvalid");
        return values.Select(Point).ToArray();
    }
}
