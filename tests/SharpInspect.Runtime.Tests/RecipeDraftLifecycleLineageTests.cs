using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T48 checks for the canonical payload of a Draft derived from an abandoned
/// Draft or a retired Released Recipe. The lineage is copied preserved history, so these
/// cases prove exact refs, strict rejection and frozen legacy bytes only; they never
/// reopen, retire, release or activate anything.
/// </summary>
public sealed class RecipeDraftLifecycleLineageTests
{
    private static readonly Guid TransitionId = Guid.Parse("6a000000-0000-0000-0000-000000000001");
    private static readonly Guid OperationId = Guid.Parse("7b000000-0000-0000-0000-000000000002");
    private static readonly Guid RuntimeEpoch = Guid.Parse("8c000000-0000-0000-0000-000000000003");
    private static readonly Guid DraftId = Guid.Parse("5d000000-0000-0000-0000-000000000004");
    private static readonly Guid ReleaseId = Guid.Parse("9e000000-0000-0000-0000-000000000005");
    private static readonly Guid ActorPrincipalId = Guid.Parse("2a000000-0000-0000-0000-000000000007");
    private static readonly Guid ActorSessionId = Guid.Parse("3b000000-0000-0000-0000-000000000008");
    private static readonly Guid StepUpGrantId = Guid.Parse("4c000000-0000-0000-0000-000000000009");
    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 12, 3, 30, 0, TimeSpan.Zero);

    [Fact]
    [Trait("VerificationId", "V148_L01")]
    public void V148_L01_AbandonedSourceLineageRoundTripsExactPreservedEvidence()
    {
        var lineage = new RecipeDraftLifecycleLineage(AbandonmentRecord());
        Assert.Equal(RecipeLifecycleKind.DraftAbandoned, lineage.Kind);
        var content = Derived(lineage);

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.StartsWith("{\"FormatVersion\":7,", document!.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"CameraProviderExtension\":null", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"CalibrationRequirements\":[]", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"MigrationLineage\":null", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"PartIdentityRequirement\":null", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"LifecycleLineage\":{", document.PayloadJson, StringComparison.Ordinal);

        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
            out var decoded, out reason), reason);
        Assert.Equal(content.ContentHash, decoded!.ContentHash);
        var restored = decoded.LifecycleLineage!;
        Assert.Equal(lineage.Transition, restored.Transition);
        Assert.Equal(lineage.Kind, restored.Kind);
        Assert.Equal(lineage.SourceDraft, restored.SourceDraft);
        Assert.Equal(DraftId, restored.SourceDraft.DraftId);
        Assert.Equal(3L, restored.SourceDraft.Revision);
        Assert.Equal(lineage.SourceContentHash, restored.SourceContentHash);
        Assert.Null(restored.SourceRecipe);
        Assert.Null(restored.SourceReleaseId);
        Assert.Null(restored.SourceReleaseRecordContentHash);
        Assert.Equal(lineage.ContentHash, restored.ContentHash);

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(decoded, out var reencoded, out reason), reason);
        Assert.Equal(document.PayloadJson, reencoded!.PayloadJson);
        Assert.Equal(document.PayloadHash, reencoded.PayloadHash);

        // Provenance cannot silently disappear: the identical preserved snapshot without
        // the lifecycle copy is a different identity in an earlier format.
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(Derived(null), out var withoutLineage, out reason),
            reason);
        Assert.StartsWith("{\"FormatVersion\":1,", withoutLineage!.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("LifecycleLineage", withoutLineage.PayloadJson, StringComparison.Ordinal);
        Assert.NotEqual(document.PayloadHash, withoutLineage.PayloadHash);
        Assert.NotEqual(content.ContentHash, withoutLineage.Content.ContentHash);
    }

    [Fact]
    [Trait("VerificationId", "V148_L02")]
    public void V148_L02_RetiredReleaseLineageRoundTripsExactReleaseIdentity()
    {
        var record = RetirementRecord();
        var lineage = new RecipeDraftLifecycleLineage(record);
        Assert.Equal(record.Reference, lineage.Transition);
        Assert.Equal(RecipeLifecycleKind.ReleasedRetired, lineage.Kind);
        Assert.Equal(record.SourceDraft, lineage.SourceDraft);
        Assert.Equal(record.SourceContentHash, lineage.SourceContentHash);
        Assert.Equal(record.Recipe, lineage.SourceRecipe);
        Assert.Equal(record.ReleaseId, lineage.SourceReleaseId);
        Assert.Equal(record.ReleaseRecordContentHash, lineage.SourceReleaseRecordContentHash);
        var content = Derived(lineage);

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.StartsWith("{\"FormatVersion\":7,", document!.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"Kind\":\"ReleasedRetired\"", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"SourceRecipe\":{\"Id\":\"Lineage.Source.Recipe\",\"Version\":\"1\"",
            document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"SourceReleaseId\":\"" + ReleaseId.ToString("D") + "\"", document.PayloadJson,
            StringComparison.Ordinal);

        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
            out var decoded, out reason), reason);
        Assert.Equal(content.ContentHash, decoded!.ContentHash);
        var restored = decoded.LifecycleLineage!;
        Assert.Equal(record.Reference, restored.Transition);
        Assert.Equal(record.SourceDraft, restored.SourceDraft);
        Assert.Equal(record.Recipe, restored.SourceRecipe);
        Assert.Equal(record.ReleaseId, restored.SourceReleaseId);
        Assert.Equal(record.ReleaseRecordContentHash, restored.SourceReleaseRecordContentHash);
        Assert.Equal(lineage.ContentHash, restored.ContentHash);
        Assert.Equal(1L, restored.Transition.Position);

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(decoded, out var reencoded, out reason), reason);
        Assert.Equal(document.PayloadJson, reencoded!.PayloadJson);

        // The copied evidence is bound to the exact release: another release identity is
        // a different lineage and therefore a different derived Draft.
        var other = new RecipeDraftLifecycleLineage(RetirementRecord(
            Guid.Parse("9e000000-0000-0000-0000-0000000000aa")));
        Assert.NotEqual(lineage.ContentHash, other.ContentHash);
        Assert.NotEqual(Derived(lineage).ContentHash, Derived(other).ContentHash);
    }

    [Fact]
    [Trait("VerificationId", "V148_L03")]
    public void V148_L03_TamperUnknownMissingHashAndCanonicalMutationAreRejected()
    {
        var content = Derived(new RecipeDraftLifecycleLineage(AbandonmentRecord()));
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        var lineageJson = JsonNode.Parse(document!.PayloadJson)!.AsObject()["LifecycleLineage"]!.AsObject();
        var sourceContentHash = lineageJson["SourceContentHash"]!.GetValue<string>();
        var lineageHash = lineageJson["ContentHash"]!.GetValue<string>();

        var unknown = Mutate(document.PayloadJson, lineage => lineage["Forged"] = true);
        AssertRejected(unknown, "RecipeDraftLifecycleLineageUnknownProperty");

        var missingField = Mutate(document.PayloadJson,
            lineage => lineage["SourceDraft"]!.AsObject().Remove("RevisionContentHash"));
        AssertRejected(missingField, "RecipeDraftLifecycleSourceDraftPropertyMissing");

        var missingLineage = Mutate(document.PayloadJson,
            mutateRoot: payload => payload.Remove("LifecycleLineage"));
        AssertRejected(missingLineage, "RecipeDraftPayloadPropertyMissing");

        var missingKind = Mutate(document.PayloadJson, lineage => lineage.Remove("Kind"));
        AssertRejected(missingKind, "RecipeDraftLifecycleLineagePropertyMissing");

        var forgedSourceHash = Mutate(document.PayloadJson,
            lineage => lineage["SourceContentHash"] = Flip(sourceContentHash));
        AssertRejected(forgedSourceHash, "RecipeDraftLifecycleLineageHashMismatch");

        var forgedLineageHash = Mutate(document.PayloadJson,
            lineage => lineage["ContentHash"] = Flip(lineageHash));
        AssertRejected(forgedLineageHash, "RecipeDraftLifecycleLineageHashMismatch");

        var kindEscalation = Mutate(document.PayloadJson,
            lineage => lineage["Kind"] = "ReleasedRetired");
        AssertRejected(kindEscalation, "RecipeDraftLifecycleLineageSourceInvalid");

        var releaseSmuggling = Mutate(document.PayloadJson,
            lineage => lineage["SourceReleaseId"] = ReleaseId.ToString("D"));
        AssertRejected(releaseSmuggling, "RecipeDraftLifecycleLineageSourceInvalid");

        var unknownKind = Mutate(document.PayloadJson, lineage => lineage["Kind"] = "Reopened");
        AssertRejected(unknownKind, "RecipeDraftLifecycleKindInvalid");

        var zeroPosition = Mutate(document.PayloadJson,
            lineage => lineage["Transition"]!.AsObject()["Position"] = 0);
        AssertRejected(zeroPosition, "RecipeDraftLifecycleTransitionInvalid");

        var zeroRevision = Mutate(document.PayloadJson,
            lineage => lineage["SourceDraft"]!.AsObject()["Revision"] = 0);
        AssertRejected(zeroRevision, "RecipeDraftLifecycleSourceDraftInvalid");

        var contentTamper = Mutate(document.PayloadJson,
            mutateRoot: payload => payload["DisplayName"] = "Tampered recipe");
        AssertRejected(contentTamper, "RecipeDraftContentHashMismatch");

        var downgrade = Mutate(document.PayloadJson, mutateRoot: payload => payload["FormatVersion"] = 6);
        AssertRejected(downgrade, "RecipeDraftPayloadUnknownProperty");

        // The lineage field is mandatory in format 7: an explicit null can never decode
        // as undeclared provenance.
        AssertRejected(NullLineage(document.PayloadJson), "RecipeDraftLifecycleLineageRequired");

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(Derived(null), out var format1, out reason), reason);
        var futureVersion = JsonNode.Parse(format1!.PayloadJson)!.AsObject();
        futureVersion["FormatVersion"] = 8;
        AssertRejected(futureVersion.ToJsonString(), "RecipeDraftPayloadVersionUnsupported");

        var smuggled = JsonNode.Parse(format1.PayloadJson)!.AsObject();
        smuggled.Add("LifecycleLineage", JsonNode.Parse(JsonNode.Parse(document.PayloadJson)!
            .AsObject()["LifecycleLineage"]!.ToJsonString()));
        AssertRejected(smuggled.ToJsonString(), "RecipeDraftPayloadUnknownProperty");

        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, new string('0', 64),
            out _, out var hashReason));
        Assert.Equal("RecipeDraftPayloadHashMismatch", hashReason);

        var spaced = document.PayloadJson.Replace("\"Kind\":\"DraftAbandoned\"", "\"Kind\": \"DraftAbandoned\"",
            StringComparison.Ordinal);
        Assert.NotEqual(document.PayloadJson, spaced);
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(spaced, Hash(spaced), out _, out var canonicalReason));
        Assert.Equal("RecipeDraftPayloadCanonicalMismatch", canonicalReason);
    }

    [Theory]
    [Trait("VerificationId", "V148_L04")]
    [InlineData(2, "2C2883A59ABF77B0342D0BC9B11D9C6FEF87121F191B45A87C1874DDFBA0F85B",
        "7845C14670B8EF3817628D5B98E81F854BE15A8C0BAE62795182C38653BAE726")]
    [InlineData(3, "D6E97B5CACA8F840DDF8EE09FE0373993B8A8EE25D7A28662876896AAFC1C4CE",
        "F2174C9A71B927929A6BCF7109825C8C9F0F94606AE4531CA8E9843C8C804877")]
    [InlineData(4, "321C1C2AD5062CA6A1A21B491D2C9A675FA7A1376288B24B130E53E8B606C812",
        "D99234690DFCD42B51775C1EE06B254FC6495B14B786C24E8BA1504969E08E26")]
    public void V148_L04_FrozenLegacyPayloadsKeepTheirExactBytesAndFormat(int format, string payloadHash,
        string contentHash)
    {
        var assembly = typeof(RecipeDraftStorageCodecTests).Assembly;
        using var stream = assembly.GetManifestResourceStream("RecipeDraftLegacyT31.format-" + format + ".json")!;
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var frozen = buffer.ToArray();
        var payload = Encoding.UTF8.GetString(frozen);
        Assert.Equal(payloadHash, Convert.ToHexString(SHA256.HashData(frozen)));

        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(payload, payloadHash, out var decoded, out var reason),
            reason);
        Assert.Equal(contentHash, decoded!.ContentHash);
        Assert.Null(decoded.LifecycleLineage);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(decoded, out var encoded, out reason), reason);
        Assert.Equal(payloadHash, encoded!.PayloadHash);
        Assert.Equal(frozen, Encoding.UTF8.GetBytes(encoded.PayloadJson));
        Assert.StartsWith("{\"FormatVersion\":" + format + ",", encoded.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("LifecycleLineage", encoded.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("VerificationId", "V148_L05")]
    public void V148_L05_LifecycleCoexistsWithMigrationPartIdentityAndClassification()
    {
        var (content, migration, partIdentity) = CoexistContent();

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.StartsWith("{\"FormatVersion\":7,", document!.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"TransferClassification\":\"PortableRecipeData\"", document.PayloadJson,
            StringComparison.Ordinal);
        Assert.Contains("\"TransferClassification\":\"LocalOnly\"", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"CameraProviderExtension\":{\"Provider\":{\"Id\":\"Lineage.Provider\"",
            document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"CalibrationRequirements\":[{\"LogicalCameraRole\":\"TopCamera\"",
            document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"MigrationLineage\":{", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"PartIdentityRequirement\":{", document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"LifecycleLineage\":{", document.PayloadJson, StringComparison.Ordinal);

        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
            out var decoded, out reason), reason);
        Assert.Equal(content.ContentHash, decoded!.ContentHash);
        Assert.Equal(migration.ContentHash, decoded.MigrationLineage!.ContentHash);
        Assert.Equal(migration.Plan.ContentHash, decoded.MigrationLineage.Plan.ContentHash);
        Assert.Equal(migration.Descriptor.ContentHash, decoded.MigrationLineage.Descriptor.ContentHash);
        Assert.Equal(partIdentity, decoded.PartIdentityRequirement);
        Assert.Equal(content.CameraProviderExtension, decoded.CameraProviderExtension);
        Assert.Equal(content.CalibrationRequirements.Select(item => item.ContentHash),
            decoded.CalibrationRequirements.Select(item => item.ContentHash));
        Assert.Equal(RecipeLifecycleKind.ReleasedRetired, decoded.LifecycleLineage!.Kind);
        Assert.Equal(AlgorithmConfigurationTransferClassification.PortableRecipeData,
            decoded.Algorithm.ConfigurationSchema.Fields.Single(field => field.Key == "count")
                .TransferClassification);
        Assert.Equal(AlgorithmConfigurationTransferClassification.LocalOnly,
            decoded.Algorithm.ConfigurationSchema.Fields.Single(field => field.Key == "label")
                .TransferClassification);

        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(decoded, out var reencoded, out reason), reason);
        Assert.Equal(document.PayloadJson, reencoded!.PayloadJson);
        Assert.Equal(document.PayloadHash, reencoded.PayloadHash);

        // The identical snapshot without the lifecycle copy falls back to format 6 and
        // keeps migration, PartIdentity and classification evidence unchanged.
        var withoutLineage = new RecipeDraftContent(null, migration, content.RecipeKey, content.DisplayName,
            content.Algorithm, content.Configuration, content.CameraRole, content.Camera,
            content.AlgorithmExecutionTimeout, content.AssetRequirements, content.PolicyRequirements,
            content.ValueOrigins, content.CameraProviderExtension, content.CalibrationRequirements, partIdentity);
        Assert.NotEqual(content.ContentHash, withoutLineage.ContentHash);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(withoutLineage, out var format6, out reason), reason);
        Assert.StartsWith("{\"FormatVersion\":6,", format6!.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("LifecycleLineage", format6.PayloadJson, StringComparison.Ordinal);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(format6.PayloadJson, format6.PayloadHash,
            out var decoded6, out reason), reason);
        Assert.Equal(withoutLineage.ContentHash, decoded6!.ContentHash);
        Assert.Equal(migration.ContentHash, decoded6.MigrationLineage!.ContentHash);
        Assert.Equal(partIdentity, decoded6.PartIdentityRequirement);
        Assert.Equal(AlgorithmConfigurationTransferClassification.PortableRecipeData,
            decoded6.Algorithm.ConfigurationSchema.Fields.Single(field => field.Key == "count")
                .TransferClassification);
    }

    [Fact]
    [Trait("VerificationId", "V148_L06")]
    public void V148_L06_LineageFreeDraftsKeepEarlierFormatsAndBothOlderConstructors()
    {
        var source = CalibrationRequirementTests.Content(Array.Empty<CalibrationRequirement>());
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(source, out var format1, out var reason), reason);
        Assert.StartsWith("{\"FormatVersion\":1,", format1!.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("LifecycleLineage", format1.PayloadJson, StringComparison.Ordinal);

        var partIdentity = new PartIdentityRequirement(PartIdentityRequirementMode.Required, "PartCode",
            new RecipeContractReference("PartCode.Format", "1", new string('D', 64)));
        var withPartIdentity = new RecipeDraftContent(null, source.RecipeKey, source.DisplayName, source.Algorithm,
            source.Configuration, source.CameraRole, source.Camera, source.AlgorithmExecutionTimeout,
            source.AssetRequirements, source.PolicyRequirements, source.ValueOrigins,
            source.CameraProviderExtension, source.CalibrationRequirements, partIdentity);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(withPartIdentity, out var format5, out reason), reason);
        Assert.StartsWith("{\"FormatVersion\":5,", format5!.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("LifecycleLineage", format5.PayloadJson, StringComparison.Ordinal);

        // The third public constructor with no lifecycle lineage reproduces the exact
        // identity and bytes of the preserved migration-only constructor.
        var (content, migration, identity) = CoexistContent();
        var preservedConstructor = new RecipeDraftContent(migration, content.RecipeKey, content.DisplayName,
            content.Algorithm, content.Configuration, content.CameraRole, content.Camera,
            content.AlgorithmExecutionTimeout, content.AssetRequirements, content.PolicyRequirements,
            content.ValueOrigins, content.CameraProviderExtension, content.CalibrationRequirements, identity);
        var lineageAwareConstructor = new RecipeDraftContent(null, migration, content.RecipeKey, content.DisplayName,
            content.Algorithm, content.Configuration, content.CameraRole, content.Camera,
            content.AlgorithmExecutionTimeout, content.AssetRequirements, content.PolicyRequirements,
            content.ValueOrigins, content.CameraProviderExtension, content.CalibrationRequirements, identity);
        Assert.Equal(preservedConstructor.ContentHash, lineageAwareConstructor.ContentHash);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(preservedConstructor, out var preserved, out reason),
            reason);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(lineageAwareConstructor, out var aware, out reason),
            reason);
        Assert.Equal(preserved!.PayloadJson, aware!.PayloadJson);
        Assert.Equal(preserved.PayloadHash, aware.PayloadHash);
        Assert.StartsWith("{\"FormatVersion\":6,", preserved.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("LifecycleLineage", preserved.PayloadJson, StringComparison.Ordinal);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(preserved.PayloadJson, preserved.PayloadHash,
            out var decoded, out reason), reason);
        Assert.Null(decoded!.LifecycleLineage);
        Assert.Equal(preserved.Content.ContentHash, decoded.ContentHash);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(decoded, out var again, out reason), reason);
        Assert.Equal(preserved.PayloadJson, again!.PayloadJson);
        Assert.NotEqual(content.ContentHash, preserved.Content.ContentHash);
    }

    private static RecipeDraftContent Derived(RecipeDraftLifecycleLineage? lineage,
        RecipeDraftMigrationLineage? migration = null, PartIdentityRequirement? partIdentity = null)
    {
        var source = CalibrationRequirementTests.Content(Array.Empty<CalibrationRequirement>());
        return new RecipeDraftContent(lineage, migration ?? source.MigrationLineage, source.RecipeKey,
            source.DisplayName, source.Algorithm, source.Configuration, source.CameraRole, source.Camera,
            source.AlgorithmExecutionTimeout, source.AssetRequirements, source.PolicyRequirements,
            source.ValueOrigins, source.CameraProviderExtension, source.CalibrationRequirements, partIdentity);
    }

    private static (RecipeDraftContent Content, RecipeDraftMigrationLineage Migration,
        PartIdentityRequirement PartIdentity) CoexistContent()
    {
        var schema = new AlgorithmConfigurationSchema("Lineage.Config", "1", new[]
        {
            new AlgorithmFieldDefinition(AlgorithmConfigurationTransferClassification.PortableRecipeData, "count",
                AlgorithmScalarType.Int64, "items", true),
            new AlgorithmFieldDefinition("label", AlgorithmScalarType.String, "text", false)
        });
        var snapshot = AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            new AlgorithmConfigurationEntry("count", "items", AlgorithmScalarValue.FromInt64(7)),
            new AlgorithmConfigurationEntry("label", "text", AlgorithmScalarValue.FromString("sample"))
        });
        var targetSchema = new RecipeContractReference(schema.Id, schema.Version, schema.ContentHash);
        var migrator = new RecipeContractReference("Lineage.Migrator", "1", new string('C', 64));
        var plan = new RecipeDraftMigrationPlan(Guid.NewGuid(),
            new RecipeDraftRevisionReference(Guid.NewGuid(), 2, Hash("migration-source-revision")),
            Guid.NewGuid(), new AlgorithmIdentity("Lineage.Algorithm", "1"), targetSchema, migrator,
            "preserve migration lineage");
        var descriptor = new AlgorithmConfigurationMigrationDescriptor(migrator,
            new AlgorithmIdentity("Lineage.Source.Algorithm", "1"),
            new RecipeContractReference("Lineage.Source.Config", "1", Hash("source-schema")),
            new AlgorithmIdentity("Lineage.Algorithm", "1"), targetSchema);
        var migration = new RecipeDraftMigrationLineage(plan, descriptor, Hash("input-configuration"),
            snapshot.ContentHash, new[] { new AlgorithmValidationIssue("LineageMigrated", "count") });
        var partIdentity = new PartIdentityRequirement(PartIdentityRequirementMode.Required, "PartCode",
            new RecipeContractReference("PartCode.Format", "1", new string('D', 64)));
        var extension = new CameraProviderExtensionRequirement(
            new CameraProviderIdentity("Lineage.Provider", "1", "Lineage.Package", "2"),
            "Lineage.Extension", "1", new string('E', 64));
        var calibrations = new[]
        {
            CalibrationRequirementTests.Requirement(CalibrationKind.Intrinsic, "Undistortion")
        };
        var content = new RecipeDraftContent(new RecipeDraftLifecycleLineage(RetirementRecord()), migration,
            "Lineage.Recipe", "Lineage recipe", Binding(schema), snapshot, "TopCamera", Camera(),
            TimeSpan.FromMilliseconds(100), Array.Empty<RecipeAssetRequirement>(),
            Array.Empty<RecipePolicyRequirement>(), null, extension, calibrations, partIdentity);
        return (content, migration, partIdentity);
    }

    private static RecipeAlgorithmBinding Binding(AlgorithmConfigurationSchema schema)
    {
        var overlay = new OverlayContract("Lineage.Overlay", "1", 8, 64, 16);
        var result = new AlgorithmResultSchema("Lineage.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            new[] { "NoDefect" }, overlay);
        return new RecipeAlgorithmBinding(new AlgorithmIdentity("Lineage.Algorithm", "1"), schema,
            new RecipeContractReference(result.Id, result.Version, result.ContentHash),
            new RecipeContractReference(overlay.Id, overlay.Version, overlay.ContentHash));
    }

    private static RequestedCameraConfiguration Camera() => new(ProductionAcquisitionMode.SoftwareTrigger,
        1000, 1.5, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 1000, 0, null);

    private static RecipeLifecycleRecord AbandonmentRecord() => new(4, TransitionId, OperationId, RuntimeEpoch,
        RecipeLifecycleKind.DraftAbandoned, Hash("previous-transition"), SourceRevision(), Hash("source-content"),
        null, null, null, null, null, null, ActorPrincipalId, ActorSessionId, 7, StepUpGrantId,
        AuthorizationPolicy(), Hash("authorization-target"), "abandon preserved draft", RecordedAt);

    private static RecipeLifecycleRecord RetirementRecord(Guid? releaseId = null) => new(1, TransitionId,
        OperationId, RuntimeEpoch, RecipeLifecycleKind.ReleasedRetired, null, SourceRevision(),
        Hash("source-content"), new RecipeReference("Lineage.Source.Recipe", "1", Hash("released-content")),
        releaseId ?? ReleaseId, Hash("release-record"), null, null, null, ActorPrincipalId, ActorSessionId, 7,
        StepUpGrantId, AuthorizationPolicy(), Hash("authorization-target"), "retire preserved release", RecordedAt);

    private static RecipeDraftRevisionReference SourceRevision() => new(DraftId, 3, Hash("source-draft-revision"));

    private static RecipeContractReference AuthorizationPolicy() =>
        new("Lineage.Authorization", "1", Hash("authorization-policy"));

    private static string Mutate(string payloadJson, Action<JsonObject>? mutateLineage = null,
        Action<JsonObject>? mutateRoot = null)
    {
        var root = JsonNode.Parse(payloadJson)!.AsObject();
        var lineage = root["LifecycleLineage"]!.AsObject();
        if (mutateLineage is not null) mutateLineage(lineage);
        if (mutateRoot is not null) mutateRoot(root);
        return root.ToJsonString();
    }

    /// <summary>Replaces the canonical lineage object with an explicit JSON null.</summary>
    private static string NullLineage(string payloadJson)
    {
        const string marker = "\"LifecycleLineage\":{";
        const string nextProperty = "},\"AlgorithmExecutionTimeoutTicks\"";
        var start = payloadJson.IndexOf(marker, StringComparison.Ordinal);
        var end = payloadJson.IndexOf(nextProperty, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return payloadJson[..start] + "\"LifecycleLineage\":null" + payloadJson[(end + 1)..];
    }

    private static void AssertRejected(string payload, string expectedReason)
    {
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(payload, Hash(payload), out var decoded, out var reason));
        Assert.Null(decoded);
        Assert.Equal(expectedReason, reason);
    }

    private static string Flip(string value) => (value[0] == 'A' ? 'B' : 'A') + value[1..];

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
