using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeReleaseProjectionTests
{
    private static readonly Guid AuthorA = Guid.Parse("a1100000-0000-0000-0000-000000000001");
    private static readonly Guid AuthorB = Guid.Parse("b2200000-0000-0000-0000-000000000002");
    private static readonly Guid AuthorC = Guid.Parse("c3300000-0000-0000-0000-000000000003");

    [Fact]
    public void V130_A01_PolicyModeAndEveryReviewedTargetComponentAreBound()
    {
        var single = new RecipeGovernancePolicy("Local.Release", "1", RecipeGovernanceMode.SingleApproverRelease);
        var dual = new RecipeGovernancePolicy("Local.Release", "1", RecipeGovernanceMode.MakerCheckerRelease);
        Assert.NotEqual(single.ContentHash, dual.ContentHash);
        Assert.Throws<ArgumentException>(() => new RecipeGovernancePolicy("P", "1", (RecipeGovernanceMode)9));
        var id = Guid.NewGuid();
        var invocation = new CommandInvocation(CommandSource.Integration, AuthorA.ToString("D"), Guid.NewGuid());
        ReleaseRecipeCommand Command(long revision, string hash, RecipeContractReference policy, string reason) =>
            new(Guid.NewGuid(), invocation, id, revision, hash, policy, reason);
        var original = Command(1, Hash("first"), single.Reference, "reviewed");
        Assert.NotEqual(original.AuthorizationTarget, Command(2, Hash("first"), single.Reference, "reviewed").AuthorizationTarget);
        Assert.NotEqual(original.AuthorizationTarget, Command(1, Hash("other"), single.Reference, "reviewed").AuthorizationTarget);
        Assert.NotEqual(original.AuthorizationTarget, Command(1, Hash("first"), dual.Reference, "reviewed").AuthorizationTarget);
        Assert.NotEqual(original.AuthorizationTarget, Command(1, Hash("first"), single.Reference, "changed reason").AuthorizationTarget);
        Assert.Throws<ArgumentException>(() => Command(1, Hash("first"), single.Reference, "   "));
    }

    [Fact]
    public void V130_A02_OverwrittenAndNoOpAuthorsDoNotRemainContributors()
    {
        var first = Revision(Guid.NewGuid(), 1, 1, AuthorA, Content(5));
        var second = Revision(first.DraftId, 2, 2, AuthorB, Content(10), first);
        var third = Revision(first.DraftId, 3, 3, AuthorC, Content(20), second);
        var fourth = Revision(first.DraftId, 4, 4, AuthorB, Content(20), third);
        var changes = RecipeReleaseProjection.Contributions(new[] { first, second, third, fourth }, fourth);
        Assert.Equal(AuthorC, changes.Single(value => value.Path == "Configuration/count").AuthorPrincipalId);
        Assert.Equal(AuthorA, changes.Single(value => value.Path == "Camera/ExposureTimeUs").AuthorPrincipalId);
        Assert.Equal(new[] { AuthorA, AuthorC }, changes.Select(value => value.AuthorPrincipalId).Distinct().OrderBy(value => value));
        Assert.NotEqual(third.RevisionContentHash, fourth.RevisionContentHash);
    }

    [Fact]
    public void V130_A03_DeletionPersistsAndReadditionHasItsOwnAuthor()
    {
        var first = Revision(Guid.NewGuid(), 1, 1, AuthorA, Content(5, label: "original"));
        var deleted = Revision(first.DraftId, 2, 2, AuthorB, Content(5), first);
        var noOp = Revision(first.DraftId, 3, 3, AuthorC, Content(5), deleted);
        var changes = RecipeReleaseProjection.Contributions(new[] { first, deleted, noOp }, noOp);
        var deletion = changes.Single(value => value.Path == "Configuration/label");
        Assert.Equal(AuthorB, deletion.AuthorPrincipalId);
        Assert.NotNull(deletion.BeforeValue); Assert.Null(deletion.AfterValue);
        var restored = Revision(first.DraftId, 4, 4, AuthorC, Content(5, label: "original"), noOp);
        var restoration = RecipeReleaseProjection.Contributions(new[] { first, deleted, noOp, restored }, restored)
            .Single(value => value.Path == "Configuration/label");
        Assert.Equal(AuthorC, restoration.AuthorPrincipalId);
        Assert.Null(restoration.BeforeValue); Assert.NotNull(restoration.AfterValue);
    }

    [Fact]
    public void V130_A04_OriginChangeCountsEvenWhenConfigurationValueIsUnchanged()
    {
        var first = Revision(Guid.NewGuid(), 1, 1, AuthorA, Content(5, origin: RecipeDraftValueOrigin.AuthoringDefault));
        var second = Revision(first.DraftId, 2, 2, AuthorB, Content(5), first);
        var changes = RecipeReleaseProjection.Contributions(new[] { first, second }, second);
        Assert.Equal(first.Content.Configuration.ContentHash, second.Content.Configuration.ContentHash);
        Assert.Equal(AuthorA, changes.Single(value => value.Path == "Configuration/count").AuthorPrincipalId);
        Assert.Equal(AuthorB, changes.Single(value => value.Path == "ValueOrigin/count").AuthorPrincipalId);
    }

    [Fact]
    public void V130_A05_MigrationPreservesAuthorsOfUnchangedSourceFacets()
    {
        var source = Revision(Guid.NewGuid(), 1, 1, AuthorA, Content(5));
        var output = Content(10, version: "2");
        var sourceSchema = source.Content.Algorithm.ConfigurationSchema;
        var targetSchema = output.Algorithm.ConfigurationSchema;
        var migrator = new AlgorithmConfigurationMigrationDescriptor(new("Count.Migrator", "1", Hash("migrator")),
            source.Content.Algorithm.Algorithm, new(sourceSchema.Id, sourceSchema.Version, sourceSchema.ContentHash),
            output.Algorithm.Algorithm, new(targetSchema.Id, targetSchema.Version, targetSchema.ContentHash));
        var targetId = Guid.NewGuid();
        var plan = new RecipeDraftMigrationPlan(Guid.NewGuid(), new(source.DraftId, 1, source.RevisionContentHash),
            targetId, output.Algorithm.Algorithm, migrator.TargetSchema, migrator.Migrator, "explicit migration");
        var lineage = new RecipeDraftMigrationLineage(plan, migrator, source.Content.Configuration.ContentHash,
            output.Configuration.ContentHash, null);
        var target = Revision(targetId, 1, 2, AuthorB, Content(10, version: "2", lineage: lineage));
        var changes = RecipeReleaseProjection.Contributions(new[] { source, target }, target);
        Assert.Equal(AuthorA, changes.Single(value => value.Path == "Camera/ExposureTimeUs").AuthorPrincipalId);
        Assert.Equal(AuthorB, changes.Single(value => value.Path == "Configuration/count").AuthorPrincipalId);
        Assert.Equal(AuthorB, changes.Single(value => value.Path == "MigrationLineage").AuthorPrincipalId);
        Assert.Equal(new[] { AuthorA, AuthorB }, changes.Select(value => value.AuthorPrincipalId).Distinct().OrderBy(value => value));
        Assert.Throws<InvalidOperationException>(() => RecipeReleaseProjection.Contributions(new[] { target }, target));
    }

    [Fact]
    public void V130_A06_CalibrationDependencyRequiresExactPublishedPolicyWithoutStationQualification()
    {
        var fixture = CalibrationGovernanceCodecTests.CreateFixture();
        var policy = fixture.Policy;
        var execution = new AlgorithmExecutionPolicy("Execution", "1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var release = new RecipeReleaseStoreOptions(new("Release", "1", RecipeGovernanceMode.SingleApproverRelease));
        var draft = new RecipeDraftStoreOptions(execution);
        var original = Content(5);
        var requirement = new CalibrationRequirement("TopCamera", policy.Kind, policy.LogicalPurpose,
            policy.CoefficientContract, policy.Reference);
        RecipeDraftContent Candidate(CalibrationRequirement required) => new(original.RecipeKey, original.DisplayName,
            original.Algorithm, original.Configuration, original.CameraRole, original.Camera,
            original.AlgorithmExecutionTimeout, null,
            new[] { new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                new(execution.Id, execution.Version, execution.ContentHash)) },
            original.ValueOrigins, calibrationRequirements: new[] { required });
        var candidate = Candidate(requirement);
        var missing = RecipeReleaseProjection.ValidateDependencies(candidate, release, draft,
            Array.Empty<CalibrationAcceptancePolicyRevision>());
        Assert.Equal("RecipeReleaseCalibrationDependencyUnavailable", Assert.Single(missing, value => !value.Passed).ReasonCode);
        var valid = RecipeReleaseProjection.ValidateDependencies(candidate, release, draft, new[] { fixture.PolicyRevision });
        Assert.All(valid, value => Assert.True(value.Passed, value.ReasonCode));
        // A published portable acceptance policy suffices here: no current profile, camera,
        // physical verification or production qualification is provided to this release gate.
        Assert.Contains(valid, value => value.GateId == "CalibrationDependency" && value.Contract == policy.Reference);
        var incompatible = Candidate(new("TopCamera", policy.Kind, "DifferentPurpose",
            policy.CoefficientContract, policy.Reference));
        Assert.Contains(RecipeReleaseProjection.ValidateDependencies(incompatible, release, draft,
            new[] { fixture.PolicyRevision }), value => !value.Passed && value.GateId == "CalibrationDependency");
        var forged = Candidate(new("TopCamera", policy.Kind, policy.LogicalPurpose,
            policy.CoefficientContract, new(policy.Id, policy.Version, Hash("forged policy"))));
        Assert.Contains(RecipeReleaseProjection.ValidateDependencies(forged, release, draft,
            new[] { fixture.PolicyRevision }), value => !value.Passed && value.GateId == "CalibrationDependency");
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Duplicate")]
    [InlineData("Reordered")]
    public void V130_A07_ReplayRejectsIncompleteOrReorderedReviewEvidence(string mutation)
    {
        var execution = new AlgorithmExecutionPolicy("Execution", "1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var release = new RecipeReleaseStoreOptions(new("Release", "1", RecipeGovernanceMode.SingleApproverRelease));
        var draft = new RecipeDraftStoreOptions(execution);
        var original = Content(5);
        var content = new RecipeDraftContent(original.RecipeKey, original.DisplayName, original.Algorithm,
            original.Configuration, original.CameraRole, original.Camera, original.AlgorithmExecutionTimeout,
            null, new[] { new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                new(execution.Id, execution.Version, execution.ContentHash)) }, original.ValueOrigins);
        var source = Revision(Guid.NewGuid(), 1, 1, AuthorA, content);
        var history = new[] { source };
        var invocation = new CommandInvocation(CommandSource.Integration, AuthorA.ToString("D"), Guid.NewGuid(), Guid.NewGuid());
        var command = new ReleaseRecipeCommand(Guid.NewGuid(), invocation, source.DraftId, 1,
            source.RevisionContentHash, release.Policy.Reference, "reviewed");
        var checks = RecipeReleaseProjection.ValidationChecks(content).Concat(
            RecipeReleaseProjection.ValidateDependencies(content, release, draft, Array.Empty<CalibrationAcceptancePolicyRevision>())).ToArray();
        RecipeReleaseRecord Record(IEnumerable<RecipeReleaseValidationCheck> evidence) => new(1, Guid.NewGuid(),
            command.CorrelationId, 1, source, release.Policy, evidence,
            RecipeReleaseProjection.Contributions(history, source), AuthorA, invocation.SessionId!.Value, 1,
            invocation.StepUpGrantId!.Value, new("Authorization", "1", Hash("authorization")),
            command.ReleaseReason, command.AuthorizationTarget, source.RecordedAtUtc.AddSeconds(1));
        var valid = Record(checks);
        RecipeReleaseProjection.ValidateRecord(valid, history, release, draft,
            Array.Empty<CalibrationAcceptancePolicyRevision>());
        var authorization = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.RecipeReleased,
            valid.ReleasedAtUtc, "ReleaseStation", AuthorA, null, null, null, "RecipeReleased",
            AuthenticationPolicyId: "Authentication", AuthenticationPolicyVersion: "1",
            AuthenticationPolicyHash: Hash("authentication"),
            SessionId: valid.ApproverSessionId, AuthorizationPolicyId: valid.AuthorizationPolicy.Id,
            AuthorizationPolicyVersion: valid.AuthorizationPolicy.Version,
            AuthorizationPolicyHash: valid.AuthorizationPolicy.ContentHash, ActorPrincipalId: AuthorA,
            CommandCorrelationId: valid.OperationId, StepUpGrantId: valid.StepUpGrantId,
            RequiredPermission: Permission.ReleaseRecipe.ToString(), AuthorizationRevision: valid.ApproverAuthorizationRevision,
            ActionTargetId: valid.AuthorizationTarget, BoundCommandCorrelationId: valid.OperationId,
            ActionCommandKind: AuditedCommandKind.ReleaseRecipe.ToString(), OperationId: valid.OperationId);
        Assert.True(IdentityAuditEvent.MatchesRecipeReleaseAuthorization(authorization.Encode(1, 16), 1, "ReleaseStation", valid));
        var wrongCorrelation = authorization with { BoundCommandCorrelationId = Guid.NewGuid() };
        Assert.False(IdentityAuditEvent.MatchesRecipeReleaseAuthorization(wrongCorrelation.Encode(1, 16), 1, "ReleaseStation", valid));
        var modified = mutation switch
        {
            "Missing" => checks.Skip(1),
            "Duplicate" => checks.Concat(new[] { checks[0] }),
            _ => checks.Reverse()
        };
        Assert.Throws<InvalidOperationException>(() => RecipeReleaseProjection.ValidateRecord(Record(modified),
            history, release, draft, Array.Empty<CalibrationAcceptancePolicyRevision>()));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    public void V130_A08_LegacyAndReleaseSchemaIdentityEventsRemainReadable(int schema)
    {
        var configured = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.IdentityConfigured,
            DateTimeOffset.UnixEpoch, "ReleaseStation", null, null, null, null, "IdentityConfigured",
            AuthenticationPolicyId: "Authentication", AuthenticationPolicyVersion: "1",
            AuthenticationPolicyHash: Hash("authentication"), AuthorizationPolicyId: "Authorization",
            AuthorizationPolicyVersion: "1", AuthorizationPolicyHash: Hash("authorization"));
        Assert.Equal(0, IdentityAuditEvent.VerifyPayload(configured.Encode(1, schema), 1, "ReleaseStation", schema));
    }

    [Fact]
    public void V130_A09_LaterPolicyCannotRetroactivelySatisfyAHistoricalRelease()
    {
        var fixture = CalibrationGovernanceCodecTests.CreateFixture();
        var execution = new AlgorithmExecutionPolicy("Execution", "1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var release = new RecipeReleaseStoreOptions(new("Release", "1", RecipeGovernanceMode.SingleApproverRelease));
        var draft = new RecipeDraftStoreOptions(execution);
        var original = Content(5);
        var content = new RecipeDraftContent(original.RecipeKey, original.DisplayName, original.Algorithm,
            original.Configuration, original.CameraRole, original.Camera, original.AlgorithmExecutionTimeout,
            null, new[] { new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                new(execution.Id, execution.Version, execution.ContentHash)) }, original.ValueOrigins,
            calibrationRequirements: new[] { new CalibrationRequirement("TopCamera", fixture.Policy.Kind,
                fixture.Policy.LogicalPurpose, fixture.Policy.CoefficientContract, fixture.Policy.Reference) });
        var source = Revision(Guid.NewGuid(), 1, 1, AuthorA, content);
        var history = new[] { source };
        var invocation = new CommandInvocation(CommandSource.Integration, AuthorA.ToString("D"), Guid.NewGuid(), Guid.NewGuid());
        var command = new ReleaseRecipeCommand(Guid.NewGuid(), invocation, source.DraftId, 1,
            source.RevisionContentHash, release.Policy.Reference, "reviewed before policy existed");
        var checks = RecipeReleaseProjection.ValidationChecks(content).Concat(
            RecipeReleaseProjection.ValidateDependencies(content, release, draft, new[] { fixture.PolicyRevision }));
        var record = new RecipeReleaseRecord(1, Guid.NewGuid(), command.CorrelationId, 1, source, release.Policy,
            checks, RecipeReleaseProjection.Contributions(history, source), AuthorA, invocation.SessionId!.Value,
            1, invocation.StepUpGrantId!.Value, new("Authorization", "1", Hash("authorization")),
            command.ReleaseReason, command.AuthorizationTarget, source.RecordedAtUtc.AddSeconds(1));
        Assert.True(record.ReleasedAtUtc < fixture.PolicyRevision.RecordedAtUtc);
        Assert.Throws<InvalidOperationException>(() => RecipeReleaseProjection.ValidateRecord(record,
            history, release, draft, new[] { fixture.PolicyRevision }));
        var earlier = new CalibrationAcceptancePolicyRevision(1, fixture.PolicyRevision.OperationId,
            fixture.Policy, null, fixture.PolicyRevision.Actor, source.RecordedAtUtc);
        RecipeReleaseProjection.ValidateRecord(record, history, release, draft, new[] { earlier });
    }

    private static RecipeDraftContent Content(long count, string? label = null, string version = "1",
        RecipeDraftValueOrigin origin = RecipeDraftValueOrigin.Explicit, RecipeDraftMigrationLineage? lineage = null)
    {
        var schema = new AlgorithmConfigurationSchema("Count.Config", version, new[]
        {
            new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64, "items", true,
                authoringDefault: AlgorithmScalarValue.FromInt64(5)),
            new AlgorithmFieldDefinition("label", AlgorithmScalarType.String, "text", false)
        });
        var entries = new List<AlgorithmConfigurationEntry> { new("count", "items", AlgorithmScalarValue.FromInt64(count)) };
        if (label is not null) entries.Add(new("label", "text", AlgorithmScalarValue.FromString(label)));
        var contract = new RecipeContractReference("Result", "1", Hash("result"));
        return new(lineage, "Example", "Recipe", new(new("Count.Algorithm", version), schema, contract, contract),
            AlgorithmConfigurationSnapshot.Create(schema, entries), "TopCamera",
            new(ProductionAcquisitionMode.SoftwareTrigger, 100, 0, new(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 500, 0, null),
            TimeSpan.FromMilliseconds(100), null, null,
            entries.Select(value => new RecipeDraftFieldOrigin(value.Key, value.Key == "count" ? origin : RecipeDraftValueOrigin.Explicit)));
    }

    private static RecipeDraftRevision Revision(Guid id, long revision, long position, Guid author,
        RecipeDraftContent content, RecipeDraftRevision? previous = null) =>
        new(position, id, revision, Guid.NewGuid(), previous?.RevisionContentHash,
            Hash(id + "/" + revision + "/" + content.ContentHash + "/" + author), content,
            author, Guid.NewGuid(), 1, "observed change", DateTimeOffset.Parse("2026-09-09T00:00:00Z").AddSeconds(position));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
