using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T46 selection governance checks for the new reservation lease, the exact
/// affected-target union and the portable validation proof binding. These checks use
/// in-process contracts only: they open no store, device or algorithm and claim no
/// PLC handshake, retirement or production evidence.
/// </summary>
public sealed class RecipeSelectionGovernanceTests
{
    private static readonly RecipeContractReference ExecutionPolicy =
        new("Selection.Execution", "1", Hash("selection-execution-policy"));
    private static readonly RecipeContractReference GovernancePolicy =
        new("Selection.Governance", "1", Hash("selection-governance-policy"));

    [Fact]
    public void V146_S01_UnavailableReservationLeaseIsFailClosed()
    {
        var blockerCalls = 0;
        using var lease = new RecipeSelectionRuntimeLease(Guid.NewGuid(),
            "RecipeSelectionRequiresDisarmedNotReady", () => { blockerCalls++; return null; });
        Assert.False(lease.Available);
        Assert.Equal("RecipeSelectionRequiresDisarmedNotReady", lease.GetBlocker());
        Assert.False(lease.Publish(Revision()));
        Assert.Equal(0, blockerCalls);
    }

    [Fact]
    public void V146_S02_ReservedReservationLeasePublishesOnlyWhileHeldAndReleasesOnce()
    {
        var releases = 0;
        var published = new List<RecipeSelectionRevision>();
        var revision = Revision();
        var lease = new RecipeSelectionRuntimeLease(Guid.NewGuid(), null,
            () => "RecipeSelectionRuntimeBusy", value => { published.Add(value); return true; },
            () => releases++);
        Assert.True(lease.Available);
        Assert.Equal("RecipeSelectionRuntimeBusy", lease.GetBlocker());
        Assert.True(lease.Publish(revision));
        Assert.Same(revision, Assert.Single(published));
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, releases);
        Assert.False(lease.Available);
        Assert.Equal("RecipeSelectionReservationUnavailable", lease.GetBlocker());
        Assert.False(lease.Publish(revision));
        Assert.Single(published);
    }

    [Fact]
    public void V146_S03_AffectedUnionKeepsRemovedTargetsAndResolvesExactIdentityOnly()
    {
        var kept = Release(1, "Kept");
        var removed = Release(2, "Removed");
        var previous = new RecipeSelectionMap("Selection.Map", "1", new[]
        {
            new RecipeSelectionMapEntry(1, kept.Recipe, kept.ReleaseId, kept.ContentHash),
            new RecipeSelectionMapEntry(2, removed.Recipe, removed.ReleaseId, removed.ContentHash)
        });
        var proposed = new RecipeSelectionMap("Selection.Map", "2", new[]
        {
            new RecipeSelectionMapEntry(1, kept.Recipe, kept.ReleaseId, kept.ContentHash)
        });
        var affected = RecipeSelectionService.AffectedEntries(previous, proposed);
        // The removed second entry stays in the union exactly like the retained one.
        Assert.Equal(new[] { 1u, 2u, 1u }, affected.Select(value => value.SelectionCode));
        var releases = new Dictionary<Guid, RecipeReleaseRecord>
        {
            [kept.ReleaseId] = kept,
            [removed.ReleaseId] = removed
        };
        Assert.Same(removed, RecipeSelectionService.ResolveAffectedRelease(releases, affected[1]));
        Assert.Null(RecipeSelectionService.ResolveAffectedRelease(releases,
            new RecipeSelectionMapEntry(3, removed.Recipe, removed.ReleaseId, Hash("selection-other-record"))));
        Assert.Null(RecipeSelectionService.ResolveAffectedRelease(releases,
            new RecipeSelectionMapEntry(4, new RecipeReference("Selection.Recipe.Other", "1",
                Hash("selection-other-recipe")), removed.ReleaseId, removed.ContentHash)));
        Assert.Null(RecipeSelectionService.ResolveAffectedRelease(releases,
            new RecipeSelectionMapEntry(5, kept.Recipe, Guid.NewGuid(), kept.ContentHash)));
    }

    [Fact]
    public void V146_S04_PortableValidationProofBindsEveryAuthorityInput()
    {
        var schema = PlcResultPayloadTests.Schema();
        var contract = PlcResultPayloadTests.Contract(schema);
        var binding = PlcResultPayloadTests.Bind(schema, contract);
        var content = Content(schema, "Selection.Recipe.A");
        var algorithm = Descriptor(schema, "1");
        var baseline = RecipeSelectionService.ValidationProofHash(content, algorithm, ExecutionPolicy,
            GovernancePolicy, contract.Reference, binding);
        Assert.Equal(baseline, RecipeSelectionService.ValidationProofHash(content, algorithm, ExecutionPolicy,
            GovernancePolicy, contract.Reference, binding));

        // Any changed algorithm, configuration/result/overlay reference, policy, PLC
        // contract or portable content must change the stored proof.
        Assert.NotEqual(baseline, RecipeSelectionService.ValidationProofHash(content, Descriptor(schema, "2"),
            ExecutionPolicy, GovernancePolicy, contract.Reference, binding));
        Assert.NotEqual(baseline, RecipeSelectionService.ValidationProofHash(content,
            new AlgorithmDescriptor(algorithm.Identity,
                new AlgorithmConfigurationSchema("Selection.Config", "2", Array.Empty<AlgorithmFieldDefinition>()),
                schema), ExecutionPolicy, GovernancePolicy, contract.Reference, binding));
        Assert.NotEqual(baseline, RecipeSelectionService.ValidationProofHash(content, algorithm,
            new RecipeContractReference(ExecutionPolicy.Id, "2", ExecutionPolicy.ContentHash),
            GovernancePolicy, contract.Reference, binding));
        Assert.NotEqual(baseline, RecipeSelectionService.ValidationProofHash(content, algorithm, ExecutionPolicy,
            new RecipeContractReference(GovernancePolicy.Id, "2", GovernancePolicy.ContentHash),
            contract.Reference, binding));
        var secondContract = PlcResultPayloadTests.Contract(schema, maximumBytes: 200);
        Assert.NotEqual(contract.Reference, secondContract.Reference);
        Assert.NotEqual(baseline, RecipeSelectionService.ValidationProofHash(content, algorithm, ExecutionPolicy,
            GovernancePolicy, secondContract.Reference, PlcResultPayloadTests.Bind(schema, secondContract)));
        Assert.NotEqual(baseline, RecipeSelectionService.ValidationProofHash(
            Content(schema, "Selection.Recipe.B"), algorithm, ExecutionPolicy, GovernancePolicy,
            contract.Reference, binding));
    }

    [Fact]
    public void V146_S05_CurrentPolicyRequirementsAreExact()
    {
        var schema = PlcResultPayloadTests.Schema();
        var timeout = TimeSpan.FromMilliseconds(100);
        var maximum = TimeSpan.FromMilliseconds(200);
        Assert.True(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(
            Content(schema, "Selection.Recipe.A", ExecutionPolicy, GovernancePolicy, timeout),
            ExecutionPolicy, GovernancePolicy, maximum));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(
            Content(schema, "Selection.Recipe.A", ExecutionPolicy,
                new RecipeContractReference(GovernancePolicy.Id, "2", GovernancePolicy.ContentHash), timeout),
            ExecutionPolicy, GovernancePolicy, maximum));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(
            Content(schema, "Selection.Recipe.A",
                new RecipeContractReference(ExecutionPolicy.Id, "2", ExecutionPolicy.ContentHash),
                GovernancePolicy, timeout), ExecutionPolicy, GovernancePolicy, maximum));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(
            Content(schema, "Selection.Recipe.A", ExecutionPolicy, GovernancePolicy, timeout,
                RecipePolicyKind.ImageAcquisition, new RecipeContractReference("Selection.Acquisition", "1",
                    Hash("selection-acquisition-policy"))),
            ExecutionPolicy, GovernancePolicy, maximum));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(
            Content(schema, "Selection.Recipe.A", ExecutionPolicy, GovernancePolicy, timeout),
            ExecutionPolicy, GovernancePolicy, TimeSpan.FromMilliseconds(50)));
        Assert.False(RecipeSelectionService.CurrentPolicyRequirementsSatisfied(
            Content(schema, "Selection.Recipe.A"), ExecutionPolicy, GovernancePolicy, maximum));
    }

    private static RecipeSelectionRevision Revision()
    {
        var policy = RecipeSelectionPolicy.Default;
        const string reason = "selection governance test";
        return new RecipeSelectionRevision(1, Guid.NewGuid(), Guid.NewGuid(), policy, null, null, 0,
            Array.Empty<RecipeSelectionValidatedRelease>(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            policy.Reference, reason, ChangeRecipeSelectionCommand.ComputeAuthorizationTarget(policy, null, null, reason),
            DateTimeOffset.UtcNow);
    }

    private static AlgorithmDescriptor Descriptor(AlgorithmResultSchema schema, string version) =>
        new(new AlgorithmIdentity("Selection.Algorithm", version),
            new AlgorithmConfigurationSchema("Selection.Config", "1", Array.Empty<AlgorithmFieldDefinition>()),
            schema);

    private static RecipeDraftContent Content(AlgorithmResultSchema schema, string recipeKey,
        RecipeContractReference? execution = null, RecipeContractReference? governance = null,
        TimeSpan? timeout = null, RecipePolicyKind? extraKind = null, RecipeContractReference? extra = null)
    {
        var configurationSchema = new AlgorithmConfigurationSchema("Selection.Config", "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var policies = new List<RecipePolicyRequirement>();
        if (execution is not null) policies.Add(new(RecipePolicyKind.AlgorithmExecution, execution));
        if (governance is not null) policies.Add(new(RecipePolicyKind.RecipeGovernance, governance));
        if (extraKind is not null) policies.Add(new(extraKind.Value, extra!));
        return new RecipeDraftContent(recipeKey, "selection recipe",
            new RecipeAlgorithmBinding(new AlgorithmIdentity("Selection.Algorithm", "1"), configurationSchema,
                new RecipeContractReference(schema.Id, schema.Version, schema.ContentHash),
                new RecipeContractReference(schema.OverlayContract.Id, schema.OverlayContract.Version,
                    schema.OverlayContract.ContentHash)),
            AlgorithmConfigurationSnapshot.Create(configurationSchema, Array.Empty<AlgorithmConfigurationEntry>()),
            "TopCamera", new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 500, 0, null),
            timeout ?? TimeSpan.FromMilliseconds(100), null, policies);
    }

    private static RecipeReleaseRecord Release(long position, string suffix)
    {
        var schema = PlcResultPayloadTests.Schema();
        var source = new RecipeDraftRevision(position, Guid.NewGuid(), 1, Guid.NewGuid(), null,
            Hash("selection-source-" + suffix), Content(schema, "Selection.Recipe." + suffix),
            Guid.NewGuid(), Guid.NewGuid(), 1, "selection source", DateTimeOffset.UtcNow);
        return new RecipeReleaseRecord(position, Guid.NewGuid(), Guid.NewGuid(), 1, source,
            new RecipeGovernancePolicy("Selection.Release", "1", RecipeGovernanceMode.SingleApproverRelease),
            new[] { new RecipeReleaseValidationCheck("Structure", "selection", true, "Passed") },
            new[] { new RecipeReleaseChange("Configuration/value", null, suffix, source.AuthorPrincipalId,
                source.DraftId, source.Revision) },
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            new RecipeContractReference("Selection.Authorization", "1", Hash("selection-authorization")),
            "selection release " + suffix, Hash("selection-release-target-" + suffix), DateTimeOffset.UtcNow);
    }

    private static string Hash(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
