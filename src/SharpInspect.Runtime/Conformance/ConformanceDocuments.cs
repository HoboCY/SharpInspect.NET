using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Conformance;

public static class ConformanceDocuments
{
    private const int SchemaVersion = 1;
    private const int CanonicalizationVersion = 1;
    private const int MaximumCanonicalBytes = 24 * 1024;
    private const int MaximumEnvelopeBytes = 48 * 1024;

    public static FrozenConformanceDocument FreezeProfile(ConformanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Freeze("ConformanceProfile", SerializeProfile(profile));
    }

    public static FrozenConformanceDocument FreezeCandidate(ReleaseCandidateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateCandidateManifest(definition);
        return Freeze("ReleaseCandidateDefinition", SerializeCandidate(definition));
    }

    public static FrozenConformanceDocument FreezeContext(QualificationContextDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Freeze("QualificationContextDefinition", SerializeContext(definition));
    }

    public static ConformanceProfile ReadProfile(FrozenConformanceDocument document)
    {
        var root = ReadRoot(document, "ConformanceProfile");
        try
        {
            var profile = ParseProfile(root);
            EnsureCanonical(document, SerializeProfile(profile));
            return profile;
        }
        catch (InvalidDataException) { throw; }
        catch (ArgumentException exception) { throw new InvalidDataException("ConformanceProfileInvalid", exception); }
    }

    public static ReleaseCandidateDefinition ReadCandidate(FrozenConformanceDocument document)
    {
        var root = ReadRoot(document, "ReleaseCandidateDefinition");
        try
        {
            var candidate = ParseCandidate(root);
            ValidateCandidateManifest(candidate);
            EnsureCanonical(document, SerializeCandidate(candidate));
            return candidate;
        }
        catch (InvalidDataException) { throw; }
        catch (ArgumentException exception) { throw new InvalidDataException("ReleaseCandidateInvalid", exception); }
    }

    public static QualificationContextDefinition ReadContext(FrozenConformanceDocument document)
    {
        var root = ReadRoot(document, "QualificationContextDefinition");
        try
        {
            var context = ParseContext(root);
            EnsureCanonical(document, SerializeContext(context));
            return context;
        }
        catch (InvalidDataException) { throw; }
        catch (ArgumentException exception) { throw new InvalidDataException("QualificationContextInvalid", exception); }
    }

    private static FrozenConformanceDocument Freeze(string kind, string canonicalJson)
    {
        var bytes = StrictUtf8(canonicalJson);
        if (bytes.Length > MaximumCanonicalBytes) throw new InvalidOperationException("ConformanceDocumentTooLarge");
        var document = new FrozenConformanceDocument(kind, canonicalJson,
            Convert.ToHexString(SHA256.HashData(bytes)));
        if (JsonSerializer.SerializeToUtf8Bytes(document).Length > MaximumEnvelopeBytes)
            throw new InvalidOperationException("ConformanceEnvelopeTooLarge");
        return document;
    }

    private static JsonElement ReadRoot(FrozenConformanceDocument document, string expectedKind)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.Kind, expectedKind, StringComparison.Ordinal))
            throw new InvalidDataException("ConformanceDocumentKindMismatch");
        var suppliedHash = document.Sha256;
        if (suppliedHash is null || suppliedHash.Length != 64 ||
            suppliedHash.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("ConformanceDocumentHashInvalid");
        try
        {
            if (JsonSerializer.SerializeToUtf8Bytes(document).Length > MaximumEnvelopeBytes)
                throw new InvalidDataException("ConformanceEnvelopeTooLarge");
        }
        catch (JsonException exception) { throw new InvalidDataException("ConformanceEnvelopeInvalid", exception); }

        byte[] bytes;
        try { bytes = StrictUtf8(document.CanonicalJson); }
        catch (ArgumentException exception) { throw new InvalidDataException("ConformanceDocumentEncodingInvalid", exception); }
        if (bytes.Length > MaximumCanonicalBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), suppliedHash, StringComparison.Ordinal))
            throw new InvalidDataException("ConformanceDocumentHashMismatch");
        try
        {
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            return parsed.RootElement.Clone();
        }
        catch (JsonException exception) { throw new InvalidDataException("ConformanceDocumentJsonInvalid", exception); }
    }

    private static void EnsureCanonical(FrozenConformanceDocument document, string canonical)
    {
        if (!string.Equals(canonical, document.CanonicalJson, StringComparison.Ordinal))
            throw new InvalidDataException("ConformanceDocumentNotCanonical");
    }

    private static void ValidateCandidateManifest(ReleaseCandidateDefinition definition)
    {
        if (definition.EmbeddedAssets.Count(component =>
            string.Equals(component.Name, "candidate-file-manifest", StringComparison.Ordinal)) != 1)
            throw new ArgumentException("ConformanceCandidateManifestRequired", nameof(definition));
    }

    private static string SerializeProfile(ConformanceProfile profile)
    {
        using var stream = new MemoryStream();
        using (var writer = Writer(stream))
        {
            writer.WriteStartObject();
            WriteHeader(writer);
            writer.WriteString("ProfileId", profile.ProfileId);
            writer.WriteNumber("Version", profile.Version);
            writer.WriteString("Claim", profile.Claim.ToString());
            writer.WriteString("ClaimScope", profile.ClaimScope);
            WriteStrings(writer, "SupportedPlatforms", profile.SupportedPlatforms.OrderBy(value => value, StringComparer.Ordinal));
            WriteStrings(writer, "Features", profile.Features.OrderBy(value => value, StringComparer.Ordinal));
            WriteEnums(writer, "RequiredLayers", profile.RequiredLayers.OrderBy(layer => (int)layer));
            writer.WriteString("EvidenceRetentionRule", profile.EvidenceRetentionRule);
            writer.WriteString("ReleaseAuthority", profile.ReleaseAuthority);
            writer.WritePropertyName("Requirements");
            writer.WriteStartArray();
            foreach (var requirement in profile.Requirements.OrderBy(item => item.RequirementId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("RequirementId", requirement.RequirementId);
                writer.WriteString("SourceReference", requirement.SourceReference);
                writer.WriteString("Invariant", requirement.Invariant);
                writer.WriteBoolean("Mandatory", requirement.Mandatory);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("Cases");
            writer.WriteStartArray();
            foreach (var verification in profile.Cases.OrderBy(item => item.TestId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("TestId", verification.TestId);
                writer.WriteString("Layer", verification.Layer.ToString());
                writer.WriteString("Method", verification.Method.ToString());
                WriteStrings(writer, "RequirementIds", verification.RequirementIds.OrderBy(value => value, StringComparer.Ordinal));
                writer.WriteBoolean("Applicable", verification.Applicable);
                writer.WriteString("ApplicabilityRule", verification.ApplicabilityRule);
                writer.WriteString("ExclusionEvidence", verification.ExclusionEvidence);
                writer.WriteString("RequiredEnvironment", verification.RequiredEnvironment);
                writer.WriteString("Preconditions", verification.Preconditions);
                writer.WriteString("Stimulus", verification.Stimulus);
                writer.WriteString("ExpectedObservable", verification.ExpectedObservable);
                writer.WriteString("EvidenceContract", verification.EvidenceContract);
                writer.WriteString("AcceptanceRule", verification.AcceptanceRule);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return StrictUtf8String(stream.ToArray());
    }

    private static string SerializeCandidate(ReleaseCandidateDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = Writer(stream))
        {
            writer.WriteStartObject();
            WriteHeader(writer);
            writer.WriteString("SourceRevision", definition.SourceRevision);
            WriteComponents(writer, "PublicApi", definition.PublicApi);
            WriteComponents(writer, "Schemas", definition.Schemas);
            WriteComponents(writer, "Packages", definition.Packages);
            WriteComponents(writer, "Migrations", definition.Migrations);
            WriteComponents(writer, "EmbeddedAssets", definition.EmbeddedAssets);
            WriteComponents(writer, "DependencyLocks", definition.DependencyLocks);
            WriteComponents(writer, "BuildConfiguration", definition.BuildConfiguration);
            writer.WriteEndObject();
        }
        return StrictUtf8String(stream.ToArray());
    }

    private static string SerializeContext(QualificationContextDefinition definition)
    {
        using var stream = new MemoryStream();
        using (var writer = Writer(stream))
        {
            writer.WriteStartObject();
            WriteHeader(writer);
            writer.WriteString("ProfileHash", definition.ProfileHash);
            WriteComponents(writer, "Harnesses", definition.Harnesses);
            WriteComponents(writer, "Scenarios", definition.Scenarios);
            WriteComponents(writer, "Datasets", definition.Datasets);
            WriteComponents(writer, "Seeds", definition.Seeds);
            WriteComponents(writer, "Thresholds", definition.Thresholds);
            WriteComponents(writer, "CalculationRules", definition.CalculationRules);
            WriteComponents(writer, "EnvironmentConfiguration", definition.EnvironmentConfiguration);
            writer.WriteEndObject();
        }
        return StrictUtf8String(stream.ToArray());
    }

    private static Utf8JsonWriter Writer(Stream stream) => new(stream, new JsonWriterOptions
    { Encoder = JavaScriptEncoder.Default, Indented = false, SkipValidation = false });

    private static void WriteHeader(Utf8JsonWriter writer)
    {
        writer.WriteNumber("SchemaVersion", SchemaVersion);
        writer.WriteNumber("CanonicalizationVersion", CanonicalizationVersion);
    }

    private static void WriteStrings(Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteEnums(Utf8JsonWriter writer, string propertyName, IEnumerable<QualificationLayer> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value.ToString());
        writer.WriteEndArray();
    }

    private static void WriteComponents(Utf8JsonWriter writer, string propertyName,
        IReadOnlyList<FingerprintComponent> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("Name", value.Name);
            writer.WriteString("Sha256", value.Sha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static ConformanceProfile ParseProfile(JsonElement root)
    {
        var properties = Properties(root, "SchemaVersion", "CanonicalizationVersion", "ProfileId", "Version", "Claim", "ClaimScope", "SupportedPlatforms",
            "Features", "RequiredLayers", "EvidenceRetentionRule", "ReleaseAuthority", "Requirements", "Cases");
        ValidateHeader(properties);
        var requirements = new List<ConformanceRequirement>();
        foreach (var item in Array(properties["Requirements"], "Requirements"))
        {
            var fields = Properties(item, "RequirementId", "SourceReference", "Invariant", "Mandatory");
            requirements.Add(new ConformanceRequirement(String(fields["RequirementId"]), String(fields["SourceReference"]),
                String(fields["Invariant"]), Boolean(fields["Mandatory"])));
        }
        var cases = new List<VerificationCase>();
        foreach (var item in Array(properties["Cases"], "Cases"))
        {
            var fields = Properties(item, "TestId", "Layer", "Method", "RequirementIds", "Applicable",
                "ApplicabilityRule", "ExclusionEvidence", "RequiredEnvironment", "Preconditions", "Stimulus",
                "ExpectedObservable", "EvidenceContract", "AcceptanceRule");
            cases.Add(new VerificationCase(String(fields["TestId"]), Enum<QualificationLayer>(fields["Layer"]),
                Enum<VerificationMethod>(fields["Method"]), Strings(fields["RequirementIds"], "RequirementIds"),
                Boolean(fields["Applicable"]), String(fields["ApplicabilityRule"]), String(fields["ExclusionEvidence"]),
                String(fields["RequiredEnvironment"]), String(fields["Preconditions"]), String(fields["Stimulus"]),
                String(fields["ExpectedObservable"]), String(fields["EvidenceContract"]), String(fields["AcceptanceRule"])));
        }
        return new ConformanceProfile(String(properties["ProfileId"]), Integer(properties["Version"]),
            Enum<ConformanceClaim>(properties["Claim"]), String(properties["ClaimScope"]),
            Strings(properties["SupportedPlatforms"], "SupportedPlatforms"), Strings(properties["Features"], "Features"),
            Enums<QualificationLayer>(properties["RequiredLayers"], "RequiredLayers"),
            String(properties["EvidenceRetentionRule"]), String(properties["ReleaseAuthority"]), requirements, cases);
    }

    private static ReleaseCandidateDefinition ParseCandidate(JsonElement root)
    {
        var properties = Properties(root, "SchemaVersion", "CanonicalizationVersion", "SourceRevision", "PublicApi", "Schemas", "Packages", "Migrations",
            "EmbeddedAssets", "DependencyLocks", "BuildConfiguration");
        ValidateHeader(properties);
        return new ReleaseCandidateDefinition(String(properties["SourceRevision"]),
            Components(properties["PublicApi"], "PublicApi"), Components(properties["Schemas"], "Schemas"),
            Components(properties["Packages"], "Packages"), Components(properties["Migrations"], "Migrations"),
            Components(properties["EmbeddedAssets"], "EmbeddedAssets"), Components(properties["DependencyLocks"], "DependencyLocks"),
            Components(properties["BuildConfiguration"], "BuildConfiguration"));
    }

    private static QualificationContextDefinition ParseContext(JsonElement root)
    {
        var properties = Properties(root, "SchemaVersion", "CanonicalizationVersion", "ProfileHash", "Harnesses", "Scenarios", "Datasets", "Seeds", "Thresholds",
            "CalculationRules", "EnvironmentConfiguration");
        ValidateHeader(properties);
        return new QualificationContextDefinition(String(properties["ProfileHash"]), Components(properties["Harnesses"], "Harnesses"),
            Components(properties["Scenarios"], "Scenarios"), Components(properties["Datasets"], "Datasets"),
            Components(properties["Seeds"], "Seeds"), Components(properties["Thresholds"], "Thresholds"),
            Components(properties["CalculationRules"], "CalculationRules"), Components(properties["EnvironmentConfiguration"], "EnvironmentConfiguration"));
    }

    private static void ValidateHeader(IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (Integer(properties["SchemaVersion"]) != SchemaVersion ||
            Integer(properties["CanonicalizationVersion"]) != CanonicalizationVersion)
            throw new InvalidDataException("ConformanceVersionInvalid");
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("ConformanceObjectExpected");
        var allowed = new HashSet<string>(expected, StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !result.TryAdd(property.Name, property.Value))
                throw new InvalidDataException("ConformancePropertyInvalid");
        }
        if (result.Count != expected.Length) throw new InvalidDataException("ConformancePropertyMissing");
        return result;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ConformanceArrayExpected:" + name);
        return element.EnumerateArray().ToArray();
    }

    private static string String(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException("ConformanceStringExpected");
        return element.GetString()!;
    }

    private static bool Boolean(JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("ConformanceBooleanExpected");
        return element.GetBoolean();
    }

    private static int Integer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw new InvalidDataException("ConformanceIntegerExpected");
        return value;
    }

    private static T Enum<T>(JsonElement element) where T : struct, System.Enum
    {
        var value = String(element);
        if (!System.Enum.TryParse<T>(value, out var parsed) || !System.Enum.IsDefined(typeof(T), parsed) ||
            !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException("ConformanceEnumInvalid");
        return parsed;
    }

    private static IReadOnlyList<T> Enums<T>(JsonElement element, string name) where T : struct, System.Enum
    {
        return Array(element, name).Select(Enum<T>).ToArray();
    }

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        return Array(element, name).Select(String).ToArray();
    }

    private static IReadOnlyList<FingerprintComponent> Components(JsonElement element, string name)
    {
        var result = new List<FingerprintComponent>();
        foreach (var item in Array(element, name))
        {
            var properties = Properties(item, "Name", "Sha256");
            result.Add(new FingerprintComponent(String(properties["Name"]), String(properties["Sha256"])));
        }
        return result;
    }

    private static byte[] StrictUtf8(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        try { return new UTF8Encoding(false, true).GetBytes(value); }
        catch (EncoderFallbackException exception) { throw new ArgumentException("ConformanceUtf8Invalid", nameof(value), exception); }
    }

    private static string StrictUtf8String(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
}
