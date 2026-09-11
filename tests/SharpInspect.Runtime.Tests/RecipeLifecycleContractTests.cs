using System.Reflection;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T48 checks for the public Recipe lifecycle contracts. They construct only
/// in-process immutable evidence and commands: no store, Runtime, device, PLC or
/// authorization fixture is opened and no lifecycle transition is claimed.
/// </summary>
public sealed class RecipeLifecycleContractTests
{
    private static readonly Guid TransitionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OperationId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid RuntimeEpoch = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid DraftId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ReleaseId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid ActivationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ActorPrincipalId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActorSessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid GrantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid SelectionRevisionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 12, 3, 30, 0, TimeSpan.Zero);

    [Fact]
    public void V148_A01_TransitionIdentityAndClosedStatesCarryExactWireValues()
    {
        Assert.Equal(1, (int)RecipeLifecycleKind.DraftAbandoned);
        Assert.Equal(2, (int)RecipeLifecycleKind.ReleasedRetired);
        Assert.Equal(1, (int)RecipeDraftLifecycleState.Open);
        Assert.Equal(2, (int)RecipeDraftLifecycleState.Abandoned);
        Assert.Equal(1, (int)ReleasedRecipeLifecycleState.Available);
        Assert.Equal(2, (int)ReleasedRecipeLifecycleState.Retired);

        var reference = new RecipeLifecycleReference(7, TransitionId, Hash("transition").ToLowerInvariant());
        Assert.Equal(7, reference.Position);
        Assert.Equal(TransitionId, reference.TransitionId);
        Assert.Equal(Hash("transition"), reference.ContentHash);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecipeLifecycleReference(0, TransitionId, Hash("transition")));
        Assert.Throws<ArgumentException>(() => new RecipeLifecycleReference(1, Guid.Empty, Hash("transition")));
        Assert.Throws<ArgumentException>(() => new RecipeLifecycleReference(1, TransitionId, "not-a-hash"));
    }

    [Fact]
    public void V148_A02_DraftAbandonmentPreservesSourceAndRejectsReleaseEvidence()
    {
        var record = Build();
        Assert.Equal(RecipeLifecycleKind.DraftAbandoned, record.Kind);
        Assert.Equal(DraftId, record.SourceDraft.DraftId);
        Assert.Equal(2, record.SourceDraft.Revision);
        Assert.Equal(Hash("draft-content"), record.SourceContentHash);
        Assert.Equal(1, record.Position);
        Assert.Null(record.PreviousHash);
        Assert.Null(record.Recipe);
        Assert.Null(record.ReleaseId);
        Assert.Null(record.ReleaseRecordContentHash);
        Assert.Null(record.ClearedActive);
        Assert.Empty(record.MapImpacts);
        Assert.Equal(record.Position, record.Reference.Position);
        Assert.Equal(record.TransitionId, record.Reference.TransitionId);
        Assert.Equal(record.ContentHash, record.Reference.ContentHash);

        // The canonical hash binds the preserved full source snapshot and the reason.
        Assert.NotEqual(record.ContentHash, Build(reason: "second lifecycle reason").ContentHash);
        Assert.NotEqual(record.ContentHash, Build(sourceContentHash: Hash("other-content")).ContentHash);
        Assert.NotEqual(record.ContentHash, Build(sourceDraft: DraftReference(revision: 3)).ContentHash);

        // An abandonment never claims release, Active-clearing or map evidence.
        Assert.Throws<ArgumentException>(() => Build(recipe: Recipe()));
        Assert.Throws<ArgumentException>(() => Build(releaseId: ReleaseId));
        Assert.Throws<ArgumentException>(() => Build(releaseRecordContentHash: Hash("release-record")));
        Assert.Throws<ArgumentException>(() => Build(clearedActive: Activation(), observedActive: Activation()));
        Assert.Throws<ArgumentException>(() => Build(mapImpacts: new[] { Impact(5) }));
    }

    [Fact]
    public void V148_A03_RetirementRequiresExactReleaseAndClearedActiveEqualsObserved()
    {
        var observed = Activation(position: 4);
        var record = BuildRetirement(observedActive: observed, clearedActive: observed);
        Assert.Equal(Recipe(), record.Recipe);
        Assert.Equal(ReleaseId, record.ReleaseId);
        Assert.Equal(Hash("release-record"), record.ReleaseRecordContentHash);
        Assert.Equal(observed, record.ObservedActive);
        Assert.Equal(observed, record.ClearedActive);

        // The exact release identity, id and release-record hash are all mandatory.
        Assert.Throws<ArgumentException>(() => Build(kind: RecipeLifecycleKind.ReleasedRetired,
            releaseId: ReleaseId, releaseRecordContentHash: Hash("release-record")));
        Assert.Throws<ArgumentException>(() => Build(kind: RecipeLifecycleKind.ReleasedRetired,
            recipe: Recipe(), releaseRecordContentHash: Hash("release-record")));
        Assert.Throws<ArgumentException>(() => Build(kind: RecipeLifecycleKind.ReleasedRetired,
            recipe: Recipe(), releaseId: ReleaseId));
        Assert.Throws<ArgumentException>(() => Build(kind: RecipeLifecycleKind.ReleasedRetired,
            recipe: Recipe(), releaseId: Guid.Empty, releaseRecordContentHash: Hash("release-record")));

        // A non-active retirement keeps the observed effective selection without clearing it.
        var nonActive = BuildRetirement();
        Assert.Null(nonActive.ObservedActive);
        Assert.Null(nonActive.ClearedActive);
        Assert.NotEqual(record.ContentHash, nonActive.ContentHash);
        var observedOnly = BuildRetirement(observedActive: observed);
        Assert.Equal(observed, observedOnly.ObservedActive);
        Assert.Null(observedOnly.ClearedActive);
        Assert.NotEqual(record.ContentHash, observedOnly.ContentHash);

        // Clearing is exactly the observed selection; a different or missing observation is rejected.
        Assert.Throws<ArgumentException>(() =>
            BuildRetirement(observedActive: observed, clearedActive: Activation(position: 9)));
        Assert.Throws<ArgumentException>(() => BuildRetirement(clearedActive: observed));
    }

    [Fact]
    public void V148_A04_MapImpactCopiesSortsDistinctNonzeroCodesAndBindsExactMap()
    {
        var codes = new List<uint> { 300, 5, 20 };
        var impact = new RecipeRetirementMapImpact(Selection(), Contract("Selection.Map"), codes);
        codes.Add(400);
        Assert.Equal(new uint[] { 5, 20, 300 }, impact.AffectedCodes);
        Assert.DoesNotContain(400u, impact.AffectedCodes);
        Assert.Equal(Selection(), impact.Selection);
        Assert.Equal(Contract("Selection.Map"), impact.Map);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecipeRetirementMapImpact(Selection(), Contract("Selection.Map"), new uint[] { 0 }));
        Assert.Throws<ArgumentException>(() =>
            new RecipeRetirementMapImpact(Selection(), Contract("Selection.Map"), new uint[] { 5, 5 }));
        Assert.Throws<ArgumentNullException>(() =>
            new RecipeRetirementMapImpact(Selection(), Contract("Selection.Map"), null!));

        var reordered = new RecipeRetirementMapImpact(Selection(), Contract("Selection.Map"),
            new uint[] { 20, 300, 5 });
        Assert.Equal(impact.ContentHash, reordered.ContentHash);
        var otherMap = new RecipeRetirementMapImpact(Selection(), Contract("Other.Map"), new uint[] { 5, 20, 300 });
        Assert.NotEqual(impact.ContentHash, otherMap.ContentHash);
        var otherSelection = new RecipeRetirementMapImpact(Selection(position: 3), Contract("Selection.Map"),
            new uint[] { 5, 20, 300 });
        Assert.NotEqual(impact.ContentHash, otherSelection.ContentHash);
        var otherCodes = new RecipeRetirementMapImpact(Selection(), Contract("Selection.Map"),
            new uint[] { 5, 21, 300 });
        Assert.NotEqual(impact.ContentHash, otherCodes.ContentHash);
    }

    [Fact]
    public void V148_A05_RecordBindsAuthenticatedActorAuthorizationAndAuditTime()
    {
        var record = Build(actorAuthorizationRevision: 12);
        Assert.Equal(ActorPrincipalId, record.ActorPrincipalId);
        Assert.Equal(ActorSessionId, record.ActorSessionId);
        Assert.Equal(12, record.ActorAuthorizationRevision);
        Assert.Equal(GrantId, record.StepUpGrantId);
        Assert.Equal(Contract("AuthorizationPolicy"), record.AuthorizationPolicy);
        Assert.Equal(Hash("authorization-target"), record.AuthorizationTarget);
        Assert.Equal(RecordedAt, record.RecordedAtUtc);
        Assert.Equal("recipe lifecycle transition", record.Reason);

        // Every attribution and authorization field participates in the canonical identity.
        Assert.NotEqual(record.ContentHash, Build(actorPrincipalId: Guid.NewGuid()).ContentHash);
        Assert.NotEqual(record.ContentHash, Build(actorSessionId: Guid.NewGuid()).ContentHash);
        Assert.NotEqual(record.ContentHash, Build(actorAuthorizationRevision: 13).ContentHash);
        Assert.NotEqual(record.ContentHash, Build(stepUpGrantId: Guid.NewGuid()).ContentHash);
        Assert.NotEqual(record.ContentHash, Build(authorizationTarget: Hash("other-target")).ContentHash);

        // No unauthenticated, incomplete, over-limit or fabricated evidence is accepted.
        Assert.Throws<ArgumentException>(() => Build(actorPrincipalId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Build(actorSessionId: Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(actorAuthorizationRevision: -1));
        Assert.Throws<ArgumentException>(() => Build(stepUpGrantId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Build(reason: " "));
        Assert.Throws<ArgumentException>(() => Build(reason: new string('x', 257)));
        Assert.Throws<ArgumentException>(() => Build(authorizationTarget: "not-a-hash"));
        Assert.Throws<ArgumentException>(() => Build(recordedAtUtc: default(DateTimeOffset)));
        Assert.Throws<ArgumentNullException>(() =>
            Raw(reason: null, authorizationPolicy: Contract("AuthorizationPolicy"), sourceDraft: DraftReference()));
        Assert.Throws<ArgumentNullException>(() =>
            Raw(reason: "reason", authorizationPolicy: null, sourceDraft: DraftReference()));
        Assert.Throws<ArgumentNullException>(() =>
            Raw(reason: "reason", authorizationPolicy: Contract("AuthorizationPolicy"), sourceDraft: null));

        var offset = Build(recordedAtUtc: RecordedAt.ToOffset(TimeSpan.FromHours(8)));
        Assert.Equal(TimeSpan.Zero, offset.RecordedAtUtc.Offset);
        Assert.Equal(RecordedAt, offset.RecordedAtUtc);
    }

    [Fact]
    public void V148_A06_OnlyFirstTransitionHasNoPreviousHashAndCallerListsNeverBecomeState()
    {
        Assert.Null(Build(position: 1, previousHash: null).PreviousHash);
        Assert.Throws<ArgumentException>(() => Build(position: 1, previousHash: Hash("previous")));
        Assert.Throws<ArgumentException>(() => Build(position: 2, previousHash: null));
        var chained = Build(position: 2, previousHash: Hash("previous"));
        Assert.Equal(Hash("previous"), chained.PreviousHash);
        Assert.NotEqual(chained.ContentHash,
            Build(position: 2, previousHash: Hash("other-previous")).ContentHash);

        var impacts = new List<RecipeRetirementMapImpact> { Impact(5) };
        var retired = BuildRetirement(mapImpacts: impacts);
        impacts.Add(Impact(6));
        Assert.Single(retired.MapImpacts);
        Assert.Equal(new uint[] { 5 }, retired.MapImpacts.Single().AffectedCodes);
        Assert.NotEqual(retired.ContentHash,
            BuildRetirement(mapImpacts: new[] { Impact(5), Impact(6) }).ContentHash);
        var shared = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => BuildRetirement(mapImpacts: new[]
        {
            Impact(5, shared), Impact(6, shared)
        }));
    }

    [Fact]
    public void V148_A07_RecordExposesNoPublicConstructorAndRejectsForeignContentHash()
    {
        Assert.Empty(typeof(RecipeLifecycleRecord).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.NotEmpty(typeof(RecipeLifecycleRecord).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));

        var computed = Build();
        var replayed = Build(contentHash: computed.ContentHash.ToLowerInvariant());
        Assert.Equal(computed.ContentHash, replayed.ContentHash);
        Assert.Equal(computed.ContentHash, replayed.Reference.ContentHash);
        Assert.Throws<ArgumentException>(() => Build(contentHash: Hash("foreign")));
    }

    [Fact]
    public void V148_A08_CommandsBindExactExpectedDraftHeadReleaseAndReason()
    {
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole, "principal-1", ActorSessionId, GrantId);
        var correlationId = Guid.NewGuid();
        const string abandonReason = "draft abandoned for a replacement";
        var abandon = new AbandonRecipeDraftCommand(correlationId, invocation, DraftId, 4, Hash("draft-head"),
            abandonReason);
        Assert.Equal(DraftId, abandon.DraftId);
        Assert.Equal(4, abandon.ExpectedRevision);
        Assert.Equal(Hash("draft-head"), abandon.ExpectedRevisionContentHash);
        Assert.Equal(abandon.AuthorizationTarget,
            new AbandonRecipeDraftCommand(correlationId, invocation, DraftId, 4, Hash("draft-head"),
                abandonReason).AuthorizationTarget);
        Assert.NotEqual(abandon.AuthorizationTarget,
            new AbandonRecipeDraftCommand(correlationId, invocation, DraftId, 4, Hash("draft-head"),
                "different abandonment reason").AuthorizationTarget);
        Assert.NotEqual(abandon.AuthorizationTarget,
            new AbandonRecipeDraftCommand(correlationId, invocation, DraftId, 5, Hash("draft-head"),
                abandonReason).AuthorizationTarget);
        Assert.Throws<ArgumentException>(() => new AbandonRecipeDraftCommand(Guid.Empty, invocation, DraftId, 4,
            Hash("draft-head"), abandonReason));
        Assert.Throws<ArgumentException>(() => new AbandonRecipeDraftCommand(correlationId, invocation, Guid.Empty, 4,
            Hash("draft-head"), abandonReason));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AbandonRecipeDraftCommand(correlationId, invocation,
            DraftId, 0, Hash("draft-head"), abandonReason));
        Assert.Throws<ArgumentException>(() => new AbandonRecipeDraftCommand(correlationId, invocation, DraftId, 4,
            "not-a-hash", abandonReason));
        Assert.Throws<ArgumentException>(() => new AbandonRecipeDraftCommand(correlationId, invocation, DraftId, 4,
            Hash("draft-head"), " "));

        var observed = Activation(position: 4);
        var retire = new RetireReleasedRecipeCommand(correlationId, invocation, Recipe(), ReleaseId,
            Hash("release-record"), observed, "release retired");
        Assert.Equal(Recipe(), retire.Recipe);
        Assert.Equal(ReleaseId, retire.ReleaseId);
        Assert.Equal(Hash("release-record"), retire.ReleaseRecordContentHash);
        Assert.Equal(observed, retire.ExpectedActive);
        Assert.Equal(retire.AuthorizationTarget, new RetireReleasedRecipeCommand(correlationId, invocation, Recipe(),
            ReleaseId, Hash("release-record"), observed, "release retired").AuthorizationTarget);
        Assert.NotEqual(retire.AuthorizationTarget, new RetireReleasedRecipeCommand(correlationId, invocation,
            Recipe(), ReleaseId, Hash("release-record"), null, "release retired").AuthorizationTarget);
        Assert.NotEqual(retire.AuthorizationTarget, new RetireReleasedRecipeCommand(correlationId, invocation,
            Recipe(), ReleaseId, Hash("release-record"), Activation(position: 5), "release retired")
            .AuthorizationTarget);
        Assert.NotEqual(retire.AuthorizationTarget, new RetireReleasedRecipeCommand(correlationId, invocation,
            Recipe(), Guid.NewGuid(), Hash("release-record"), observed, "release retired").AuthorizationTarget);
        Assert.Throws<ArgumentException>(() => new RetireReleasedRecipeCommand(correlationId, invocation, Recipe(),
            Guid.Empty, Hash("release-record"), observed, "release retired"));
        Assert.Throws<ArgumentException>(() => new RetireReleasedRecipeCommand(correlationId, invocation, Recipe(),
            ReleaseId, "not-a-hash", observed, "release retired"));
        Assert.Throws<ArgumentException>(() => new RetireReleasedRecipeCommand(correlationId, invocation, Recipe(),
            ReleaseId, Hash("release-record"), observed, " "));
    }

    private static RecipeLifecycleRecord Build(long position = 1,
        RecipeLifecycleKind kind = RecipeLifecycleKind.DraftAbandoned, string? previousHash = null,
        RecipeDraftRevisionReference? sourceDraft = null, string? sourceContentHash = null,
        RecipeReference? recipe = null, Guid? releaseId = null, string? releaseRecordContentHash = null,
        RecipeActivationReference? observedActive = null, RecipeActivationReference? clearedActive = null,
        IEnumerable<RecipeRetirementMapImpact>? mapImpacts = null, Guid? actorPrincipalId = null,
        Guid? actorSessionId = null, long actorAuthorizationRevision = 7, Guid? stepUpGrantId = null,
        string? authorizationTarget = null, string? reason = null, DateTimeOffset? recordedAtUtc = null,
        string? contentHash = null) => new(position, TransitionId, OperationId, RuntimeEpoch, kind, previousHash,
        sourceDraft ?? DraftReference(), sourceContentHash ?? Hash("draft-content"), recipe, releaseId,
        releaseRecordContentHash, observedActive, clearedActive, mapImpacts, actorPrincipalId ?? ActorPrincipalId,
        actorSessionId ?? ActorSessionId, actorAuthorizationRevision, stepUpGrantId ?? GrantId,
        Contract("AuthorizationPolicy"), authorizationTarget ?? Hash("authorization-target"),
        reason ?? "recipe lifecycle transition", recordedAtUtc ?? RecordedAt, contentHash);

    private static RecipeLifecycleRecord BuildRetirement(long position = 1, string? previousHash = null,
        RecipeActivationReference? observedActive = null, RecipeActivationReference? clearedActive = null,
        IEnumerable<RecipeRetirementMapImpact>? mapImpacts = null, RecipeReference? recipe = null,
        Guid? releaseId = null, string? releaseRecordContentHash = null, string? reason = null) =>
        Build(position, RecipeLifecycleKind.ReleasedRetired, previousHash, recipe: recipe ?? Recipe(),
            releaseId: releaseId ?? ReleaseId,
            releaseRecordContentHash: releaseRecordContentHash ?? Hash("release-record"),
            observedActive: observedActive, clearedActive: clearedActive, mapImpacts: mapImpacts, reason: reason);

    private static RecipeLifecycleRecord Raw(string? reason, RecipeContractReference? authorizationPolicy,
        RecipeDraftRevisionReference? sourceDraft) => new(1, TransitionId, OperationId, RuntimeEpoch,
        RecipeLifecycleKind.DraftAbandoned, null, sourceDraft!, Hash("draft-content"), null, null, null, null, null,
        null, ActorPrincipalId, ActorSessionId, 7, GrantId, authorizationPolicy!, Hash("authorization-target"),
        reason!, RecordedAt, null);

    private static string Hash(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static RecipeReference Recipe() => new("Recipe", "3", Hash("recipe-content"));

    private static RecipeContractReference Contract(string id) => new(id, "1", Hash(id + "-contract"));

    private static RecipeDraftRevisionReference DraftReference(long revision = 2) =>
        new(DraftId, revision, Hash("draft-revision"));

    private static RecipeSelectionReference Selection(long position = 2, Guid? revisionId = null) =>
        new(position, revisionId ?? SelectionRevisionId, Hash("selection-revision"));

    private static RecipeActivationReference Activation(long position = 4) =>
        new(position, ActivationId, Hash("activation"));

    private static RecipeRetirementMapImpact Impact(uint code, Guid? revisionId = null) =>
        new(Selection(2, revisionId ?? Guid.NewGuid()), Contract("Selection.Map"), new[] { code });
}
