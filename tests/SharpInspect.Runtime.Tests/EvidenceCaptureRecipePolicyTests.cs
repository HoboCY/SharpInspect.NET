using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// One declared Evidence Capture policy requirement is resolved by exact identity against
/// the current deployment catalog. Historical Recipe bytes, release evidence and store
/// bindings stay unchanged, and no stage claims that an image is available.
/// </summary>
public sealed class EvidenceCaptureRecipePolicyTests
{
    private static readonly AlgorithmExecutionPolicy Execution = new("Evidence.Execution", "1",
        TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
    private static readonly RecipeGovernancePolicy Governance = new("Evidence.Release", "1",
        RecipeGovernanceMode.SingleApproverRelease);

    [Fact]
    [Trait("VerificationId", "V150_P04")]
    public void V150_P04_AppendedKindRoundTripsTheRecipeCodecAndTransferWithoutPaths()
    {
        var policy = Configured();
        var requirement = new RecipePolicyRequirement(RecipePolicyKind.EvidenceCapture, policy.Reference);
        var content = Content("Evidence.RoundTrip", requirement);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(content, out var document, out var reason), reason);
        Assert.Contains("\"Kind\":\"EvidenceCapture\"", document!.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(policy.Reference.Id, document.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(policy.Reference.ContentHash, document.PayloadJson, StringComparison.Ordinal);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
            out var decoded, out reason), reason);
        var archived = decoded!;
        Assert.Equal(content.ContentHash, archived.ContentHash);
        Assert.Equal(policy.Reference, archived.PolicyRequirements
            .Single(value => value.Kind == RecipePolicyKind.EvidenceCapture).Contract);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(archived, out var roundTrip, out reason), reason);
        Assert.Equal(document.PayloadJson, roundTrip!.PayloadJson);
        Assert.Equal(document.PayloadHash, roundTrip.PayloadHash);

        // The portable transfer package declares the same explicit identity and no path.
        var (descriptor, portable, transferPolicy) = RecipeTransferContentCodecTests.Fixture();
        var requirements = new List<RecipePolicyRequirement>(portable.PolicyRequirements)
        {
            new(RecipePolicyKind.AlgorithmExecution,
                new RecipeContractReference(Execution.Id, Execution.Version, Execution.ContentHash)),
            new(RecipePolicyKind.EvidenceCapture, policy.Reference)
        };
        var transferable = new RecipeDraftContent(portable.RecipeKey, portable.DisplayName, portable.Algorithm,
            portable.Configuration, portable.CameraRole, portable.Camera, portable.AlgorithmExecutionTimeout,
            portable.AssetRequirements, requirements, portable.ValueOrigins,
            calibrationRequirements: portable.CalibrationRequirements,
            partIdentityRequirement: portable.PartIdentityRequirement);
        var dependencies = RecipeTransferContentCodec.Dependencies(transferable);
        var declared = Assert.Single(dependencies, value => value.Kind == "EvidenceCapturePolicy");
        Assert.Equal(policy.Reference, declared.Contract);
        Assert.Contains(dependencies, value => value.Kind == "AlgorithmExecutionPolicy");
        Assert.True(RecipeTransferContentCodec.TryEncode(transferable, transferPolicy, out var bytes,
            out var projection, out reason), reason);
        Assert.NotNull(projection);
        var text = Encoding.UTF8.GetString(bytes!);
        Assert.Contains("\"Kind\":\"EvidenceCapture\"", text, StringComparison.Ordinal);
        Assert.Contains(policy.Reference.Id, text, StringComparison.Ordinal);
        Assert.Contains(policy.Reference.ContentHash, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", text, StringComparison.Ordinal);
        var target = Guid.NewGuid();
        Assert.True(RecipeTransferContentCodec.TryDecode(bytes!, new[] { descriptor }, transferPolicy, target,
            out var imported, out reason), reason);
        var importedContent = imported!.Content;
        Assert.Equal(policy.Reference, importedContent.PolicyRequirements
            .Single(value => value.Kind == RecipePolicyKind.EvidenceCapture).Contract);
        // The importing station makes no availability claim: without the exact local catalog
        // the imported Draft still fails release dependency resolution.
        var checks = RecipeReleaseProjection.ValidateDependencies(importedContent,
            new RecipeReleaseStoreOptions(Governance), new RecipeDraftStoreOptions(Execution),
            Array.Empty<CalibrationAcceptancePolicyRevision>());
        var failure = Assert.Single(checks, value => !value.Passed);
        Assert.Equal("RecipeReleasePolicyDependencyUnavailable", failure.ReasonCode);
        Assert.Equal(nameof(RecipePolicyKind.EvidenceCapture), failure.Subject);
    }

    [Fact]
    [Trait("VerificationId", "V150_P05")]
    public void V150_P05_ReleaseValidationAcceptsOnlyTheExactCatalogAndReplaysFailClosed()
    {
        var policy = Configured();
        var requirement = new RecipePolicyRequirement(RecipePolicyKind.EvidenceCapture, policy.Reference);
        var content = Content("Evidence.Release", requirement);
        var draft = new RecipeDraftStoreOptions(Execution);
        var absent = new RecipeReleaseStoreOptions(Governance);
        var wrongVersion = new RecipeReleaseStoreOptions(Governance)
        {
            EvidenceCapturePolicies = new EvidenceCapturePolicyCatalog(new[]
            {
                new EvidenceCapturePolicySnapshot(policy.Id, "2", policy.Mode)
            })
        };
        var wrongHash = new RecipeReleaseStoreOptions(Governance)
        {
            EvidenceCapturePolicies = new EvidenceCapturePolicyCatalog(new[]
            {
                new EvidenceCapturePolicySnapshot(policy.Id, policy.Version, EvidenceCaptureMode.None)
            })
        };
        // An absent, wrong-version or wrong-hash catalog rejects the declared requirement.
        foreach (var rejected in new[] { absent, wrongVersion, wrongHash })
        {
            var checks = RecipeReleaseProjection.ValidateDependencies(content, rejected, draft,
                Array.Empty<CalibrationAcceptancePolicyRevision>());
            var failure = Assert.Single(checks, value => !value.Passed);
            Assert.Equal("PolicyDependency", failure.GateId);
            Assert.Equal(nameof(RecipePolicyKind.EvidenceCapture), failure.Subject);
            Assert.Equal("RecipeReleasePolicyDependencyUnavailable", failure.ReasonCode);
            Assert.Equal(policy.Reference, failure.Contract);
        }
        var exact = new RecipeReleaseStoreOptions(Governance)
        {
            EvidenceCapturePolicies = new EvidenceCapturePolicyCatalog(new[] { policy })
        };
        var accepted = RecipeReleaseProjection.ValidateDependencies(content, exact, draft,
            Array.Empty<CalibrationAcceptancePolicyRevision>());
        Assert.All(accepted, value => Assert.True(value.Passed, value.ReasonCode));
        Assert.Contains(accepted, value => value.GateId == "PolicyDependency" &&
            value.Subject == nameof(RecipePolicyKind.EvidenceCapture) && value.Contract == policy.Reference);
        // A legacy recipe that declares no capture policy is unaffected by an absent catalog.
        var legacyChecks = RecipeReleaseProjection.ValidateDependencies(Content("Evidence.Legacy"), absent, draft,
            Array.Empty<CalibrationAcceptancePolicyRevision>());
        Assert.All(legacyChecks, value => Assert.True(value.Passed, value.ReasonCode));
        Assert.DoesNotContain(legacyChecks, value => value.Subject == nameof(RecipePolicyKind.EvidenceCapture));

        // Historical evidence remains valid after deployment removes an old policy.
        var source = Revision(Guid.NewGuid(), content);
        var history = new[] { source };
        var invocation = new CommandInvocation(CommandSource.Integration, source.AuthorPrincipalId.ToString("D"),
            Guid.NewGuid(), Guid.NewGuid());
        var command = new ReleaseRecipeCommand(Guid.NewGuid(), invocation, source.DraftId, source.Revision,
            source.RevisionContentHash, Governance.Reference, "reviewed evidence capture");
        var evidence = RecipeReleaseProjection.ValidationChecks(content).Concat(
            RecipeReleaseProjection.ValidateDependencies(content, exact, draft,
                Array.Empty<CalibrationAcceptancePolicyRevision>())).ToArray();
        var record = new RecipeReleaseRecord(1, Guid.NewGuid(), command.CorrelationId, 1, source, Governance,
            evidence, RecipeReleaseProjection.Contributions(history, source), source.AuthorPrincipalId,
            invocation.SessionId!.Value, 1, invocation.StepUpGrantId!.Value,
            new RecipeContractReference("Evidence.Authorization", "1", Hash("authorization")),
            command.ReleaseReason, command.AuthorizationTarget, source.RecordedAtUtc.AddSeconds(1));
        RecipeReleaseProjection.ValidateRecord(record, history, exact, draft,
            Array.Empty<CalibrationAcceptancePolicyRevision>());
        RecipeReleaseProjection.ValidateRecord(record, history, absent, draft,
            Array.Empty<CalibrationAcceptancePolicyRevision>());
    }

    [Fact]
    [Trait("VerificationId", "V150_P06")]
    public void V150_P06_SelectionAcceptsOnlyTheExactCatalogAndTheProofBindsTheDeclaredReference()
    {
        var policy = Configured();
        var requirement = new RecipePolicyRequirement(RecipePolicyKind.EvidenceCapture, policy.Reference);
        var content = Content("Evidence.Selection", requirement);
        var execution = new RecipeContractReference(Execution.Id, Execution.Version, Execution.ContentHash);
        var governance = new RecipeContractReference(Governance.Id, Governance.Version, Governance.ContentHash);
        var maximum = Execution.MaximumExecutionTimeout;
        Assert.True(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(content, execution, governance, maximum,
            new EvidenceCapturePolicyCatalog(new[] { policy })));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(content, execution, governance, maximum));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(content, execution, governance, maximum,
            new EvidenceCapturePolicyCatalog(Array.Empty<EvidenceCapturePolicySnapshot>())));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(content, execution, governance, maximum,
            new EvidenceCapturePolicyCatalog(new[]
            {
                new EvidenceCapturePolicySnapshot(policy.Id, policy.Version, EvidenceCaptureMode.None)
            })));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(content, execution, governance, maximum,
            new EvidenceCapturePolicyCatalog(new[]
            {
                new EvidenceCapturePolicySnapshot(policy.Id, "2", policy.Mode)
            })));
        // The four-parameter API is preserved and a legacy recipe stays acceptable without a catalog.
        Assert.True(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(Content("Evidence.Legacy"), execution,
            governance, maximum));

        // The stored proof binds the exact declared capture identity through the Recipe hash and
        // grants no additional authority to any deployment catalog.
        var schema = PlcResultPayloadTests.Schema();
        var contract = PlcResultPayloadTests.Contract(schema);
        var binding = PlcResultPayloadTests.Bind(schema, contract);
        var descriptor = Descriptor();
        var proof = RecipeSelectionService.ValidationProofHash(content, descriptor, execution, governance,
            contract.Reference, binding);
        var changed = Content("Evidence.Selection", new RecipePolicyRequirement(RecipePolicyKind.EvidenceCapture,
            new RecipeContractReference(policy.Id, policy.Version, Hash("other capture policy"))));
        Assert.NotEqual(content.ContentHash, changed.ContentHash);
        Assert.NotEqual(proof, RecipeSelectionService.ValidationProofHash(changed, descriptor, execution, governance,
            contract.Reference, binding));
    }

    [Fact]
    [Trait("VerificationId", "V150_P07")]
    public void V150_P07_LegacyStoreBindingAndArchivedRecipeBytesStayUnchangedWithoutACatalog()
    {
        var legacy = new RecipeReleaseStoreOptions(Governance);
        Assert.Null(legacy.EvidenceCapturePolicies);
        // The current store binding bytes are exactly the frozen pre-change encoding.
        var expectedBinding = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("RecipeReleaseStoreOptions",
            "1", Governance.Id, Governance.Version, Governance.Mode.ToString(), Governance.ContentHash,
            "10000", "8388608", "536870912", "64", "3")));
        Assert.Equal(expectedBinding, legacy.BindingHash);
        var configured = new RecipeReleaseStoreOptions(Governance)
        {
            EvidenceCapturePolicies = new EvidenceCapturePolicyCatalog(new[] { Configured() })
        };
        Assert.Equal(legacy.BindingHash, configured.BindingHash);
        Assert.Equal(legacy.EncodeBinding(), configured.EncodeBinding());

        // An archived pre-change Recipe payload still decodes and re-encodes byte-exactly.
        var assembly = typeof(EvidenceCaptureRecipePolicyTests).Assembly;
        using var stream = assembly.GetManifestResourceStream("RecipeDraftLegacyT31.format-2.json")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var frozen = buffer.ToArray();
        Assert.Equal("2C2883A59ABF77B0342D0BC9B11D9C6FEF87121F191B45A87C1874DDFBA0F85B",
            Convert.ToHexString(SHA256.HashData(frozen)));
        var payload = Encoding.UTF8.GetString(frozen);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(payload,
            "2C2883A59ABF77B0342D0BC9B11D9C6FEF87121F191B45A87C1874DDFBA0F85B", out var decoded, out var reason),
            reason);
        var archived = decoded!;
        Assert.Equal("7845C14670B8EF3817628D5B98E81F854BE15A8C0BAE62795182C38653BAE726", archived.ContentHash);
        Assert.DoesNotContain(archived.PolicyRequirements, value => value.Kind == RecipePolicyKind.EvidenceCapture);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(archived, out var reencoded, out reason), reason);
        Assert.Equal(payload, reencoded!.PayloadJson);
        Assert.Equal("2C2883A59ABF77B0342D0BC9B11D9C6FEF87121F191B45A87C1874DDFBA0F85B", reencoded.PayloadHash);
        // The appended kind cannot fabricate a capture requirement for a historical Recipe.
        var checks = RecipeReleaseProjection.ValidateDependencies(archived, legacy,
            new RecipeDraftStoreOptions(Execution), Array.Empty<CalibrationAcceptancePolicyRevision>());
        Assert.DoesNotContain(checks, value => value.Subject == nameof(RecipePolicyKind.EvidenceCapture));
    }

    private static EvidenceCapturePolicySnapshot Configured() =>
        new("Evidence.Production", "1", EvidenceCaptureMode.FailOrUnknown);

    private static RecipeDraftContent Content(string recipeKey, RecipePolicyRequirement? capture = null)
    {
        var (schema, result) = Fixture();
        var binding = new RecipeAlgorithmBinding(new AlgorithmIdentity("Evidence.Algorithm", "1"), schema,
            new RecipeContractReference(result.Id, result.Version, result.ContentHash),
            new RecipeContractReference(result.OverlayContract.Id, result.OverlayContract.Version,
                result.OverlayContract.ContentHash));
        var configuration = AlgorithmConfigurationSnapshot.Create(schema, new[]
        {
            new AlgorithmConfigurationEntry("count", "items", AlgorithmScalarValue.FromInt64(5))
        });
        var policies = new List<RecipePolicyRequirement>
        {
            new(RecipePolicyKind.AlgorithmExecution,
                new RecipeContractReference(Execution.Id, Execution.Version, Execution.ContentHash))
        };
        if (capture is not null) policies.Add(capture);
        return new RecipeDraftContent(recipeKey, "evidence recipe", binding, configuration, "TopCamera",
            new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 500, 0, null),
            Execution.MaximumExecutionTimeout, null, policies);
    }

    private static AlgorithmDescriptor Descriptor()
    {
        var (schema, result) = Fixture();
        return new AlgorithmDescriptor(new AlgorithmIdentity("Evidence.Algorithm", "1"), schema, result);
    }

    private static (AlgorithmConfigurationSchema Schema, AlgorithmResultSchema Result) Fixture()
    {
        var schema = new AlgorithmConfigurationSchema("Evidence.Config", "1", new[]
        {
            new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64, "items", true,
                authoringDefault: AlgorithmScalarValue.FromInt64(5))
        });
        var result = new AlgorithmResultSchema("Evidence.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            new[] { "NoDefect" }, new OverlayContract("Evidence.Overlay", "1", 0, 0, 0));
        return (schema, result);
    }

    private static RecipeDraftRevision Revision(Guid id, RecipeDraftContent content) =>
        new(1, id, 1, Guid.NewGuid(), null, Hash(id + "/1/" + content.ContentHash), content,
            Guid.NewGuid(), Guid.NewGuid(), 1, "observed change",
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
