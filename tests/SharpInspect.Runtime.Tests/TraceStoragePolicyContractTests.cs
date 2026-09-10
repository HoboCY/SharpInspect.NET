using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class TraceStoragePolicyContractTests
{
    [Fact]
    public void V139_C01_CompletePolicyCoversEveryRetentionClassAndCanonicalizesCollections()
    {
        var rules = Rules().Reverse().ToArray();
        var routes = Routes().Reverse().ToArray();
        var policy = Policy(rules, routes);
        var reordered = Policy(rules.Reverse(), routes.Reverse());

        Assert.Equal(11, policy.RetentionRules.Count);
        Assert.Equal(Enum.GetValues<TraceRetentionClass>(),
            policy.RetentionRules.Select(value => value.EvidenceClass));
        Assert.Equal(routes.Select(value => value.RouteId).OrderBy(value => value, StringComparer.Ordinal),
            policy.RequiredRoutes.Select(value => value.RouteId));
        Assert.Equal(policy.ContentHash, reordered.ContentHash);
        Assert.Matches("^[0-9A-F]{64}$", policy.ContentHash);
    }

    [Fact]
    public void V139_C02_MissingDuplicateAndDefaultValuesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Policy(Rules().Take(10), Routes()));
        var duplicateRules = Rules().ToArray();
        duplicateRules[1] = duplicateRules[0];
        Assert.Throws<ArgumentException>(() => Policy(duplicateRules, Routes()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TraceBacklogLimits(0, 1, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TraceStoragePolicyDefinition("id", "1", "approval", "1", "reason",
                Rules(), 1, 0m, Routes(), Backlog(), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1), Budget(), Budget(), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TraceRetentionRule((TraceRetentionClass)99, RetentionStartEvent.ArtifactCreated,
                TimeSpan.FromDays(1)));
    }

    [Fact]
    public void V139_C03_BoundsRejectOverflowAndImpossibleMaintenanceBudgets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TraceRetentionRule(TraceRetentionClass.AuthoritativeImage,
                RetentionStartEvent.ArtifactCreated, TimeSpan.FromDays(36500) + TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TraceStorageMaintenanceBudget(TimeSpan.FromHours(1), TimeSpan.FromHours(2), 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TraceStorageMaintenanceBudget(TimeSpan.FromDays(2), TimeSpan.FromHours(1), 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TraceStoragePolicyDefinition("id", "1", "approval", "1", "reason", Rules(),
                1, 1m, Routes(), Backlog(), TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1), Budget(), Budget(), 1));
        Assert.Throws<ArgumentException>(() =>
            new TraceStorageRouteLimit("route", "1", "not-a-hash", Backlog()));
    }

    [Fact]
    public void V139_C04_CollectionsAreDefensivelyCopied()
    {
        var rules = Rules().ToList();
        var routes = Routes().ToList();
        var policy = Policy(rules, routes);
        var hash = policy.ContentHash;
        rules.Clear();
        routes.Clear();

        Assert.Equal(11, policy.RetentionRules.Count);
        Assert.Equal(2, policy.RequiredRoutes.Count);
        Assert.Equal(hash, policy.ContentHash);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TraceRetentionRule>)policy.RetentionRules).Add(Rules().First()));
    }

    [Fact]
    public void V139_C05_PublishCommandTargetBindsVersionPolicyAndReason()
    {
        var policy = Policy();
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            "principal", Guid.NewGuid());
        var command = new PublishTraceStoragePolicyCommand(Guid.NewGuid(), invocation, 4,
            policy, "publish exact policy");
        var changedReason = new PublishTraceStoragePolicyCommand(command.CorrelationId,
            invocation, 4, policy, "publish different policy");
        var changedVersion = new PublishTraceStoragePolicyCommand(command.CorrelationId,
            invocation, 5, policy, command.Reason);

        Assert.NotEqual(command.AuthorizationTarget, changedReason.AuthorizationTarget);
        Assert.NotEqual(command.AuthorizationTarget, changedVersion.AuthorizationTarget);
        Assert.Equal(4, command.ExpectedVersion);
        Assert.Equal(policy.ContentHash, command.Policy.ContentHash);
    }

    [Fact]
    public void V139_C06_PublicationAndSnapshotFreezeWriterEvidenceAndRules()
    {
        var policy = Policy();
        var publication = new TraceStoragePolicyPublication(2, policy,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, Guid.NewGuid(),
            DateTimeOffset.UtcNow, null);
        var snapshot = new TraceStoragePolicySnapshot(publication);

        Assert.Equal(publication.Policy.ContentHash, snapshot.Policy.ContentHash);
        Assert.Equal(publication.Version, snapshot.Version);
        Assert.Equal(publication.Policy.RetentionRules.Select(value => value.ContentHash),
            snapshot.RetentionRules.Select(value => value.ContentHash));
        Assert.NotEqual(publication.ContentHash, snapshot.ContentHash);
        Assert.Throws<ArgumentException>(() => new TraceStoragePolicyPublication(0, policy,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(),
            DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void V139_C07_NewPolicyDoesNotChangePreviouslyFrozenSnapshot()
    {
        var first = Policy();
        var firstPublication = new TraceStoragePolicyPublication(1, first,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            DateTimeOffset.UtcNow, null);
        var firstSnapshot = new TraceStoragePolicySnapshot(firstPublication);
        var second = Policy(rationale: "a later approved rationale");

        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.Equal(first.ContentHash, firstSnapshot.Policy.ContentHash);
        Assert.Equal(firstPublication.ContentHash, firstSnapshot.Publication.ContentHash);
    }

    [Fact]
    public void V139_C08_ExtensionOnlyIncreasesDeadlineAndHoldReleaseBindsExactly()
    {
        var publication = new TraceStoragePolicyPublication(3, Policy(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 8, Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-11T00:00:00Z"), null);
        var snapshot = new TraceStoragePolicySnapshot(publication);
        var rule = snapshot.RetentionRules.First();
        var obligation = new TraceRetentionObligation(new string('A', 64), snapshot, rule,
            DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        var extension = new TraceRetentionExtension(obligation,
            obligation.RetainUntilUtc.AddDays(1), "legal hold extension",
            DateTimeOffset.Parse("2026-09-11T01:00:00Z"));
        var chained = new TraceRetentionExtension(extension,
            extension.ExtendedUntilUtc.AddDays(1), "second extension",
            DateTimeOffset.Parse("2026-09-11T01:30:00Z"));
        var hold = new TraceRetentionHold("hold-1", obligation, "investigation",
            DateTimeOffset.Parse("2026-09-11T02:00:00Z"));
        var release = new TraceRetentionHoldRelease(hold, hold.HoldId,
            obligation.ContentHash, "investigation complete",
            DateTimeOffset.Parse("2026-09-11T03:00:00Z"));

        Assert.True(extension.ExtendedUntilUtc > obligation.RetainUntilUtc);
        Assert.Equal(snapshot.ContentHash, obligation.SnapshotHash);
        Assert.Equal(snapshot.Publication.ContentHash, obligation.PublicationHash);
        Assert.Equal(extension.ExtendedUntilUtc, chained.PreviousEffectiveUntilUtc);
        Assert.Equal(obligation.ContentHash, release.ObligationContentHash);
        Assert.Throws<ArgumentException>(() => new TraceRetentionExtension(obligation,
            obligation.RetainUntilUtc, "shorten", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new TraceRetentionExtension(chained,
            obligation.RetainUntilUtc.AddDays(1.5), "shorten chained extension",
            DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new TraceRetentionHoldRelease(hold,
            hold.HoldId, new string('B', 64), "wrong obligation", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new TraceRetentionHoldRelease(hold,
            hold.HoldId, obligation.ContentHash, "released too early",
            hold.PlacedAtUtc.AddTicks(-1)));
    }

    [Fact]
    public void V139_C09_ObligationBindsExactSnapshotAndRejectsRuleFromAnotherSnapshot()
    {
        var first = new TraceStoragePolicySnapshot(new TraceStoragePolicyPublication(4,
            Policy(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 9, Guid.NewGuid(),
            DateTimeOffset.Parse("2026-09-11T00:00:00Z"), null));
        var obligation = new TraceRetentionObligation(new string('A', 64), first,
            first.RetentionRules.First(), DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        var otherRule = new TraceRetentionRule(first.RetentionRules.First().EvidenceClass,
            first.RetentionRules.First().StartsAt, TimeSpan.FromDays(31));

        Assert.Equal(first.ContentHash, obligation.SnapshotHash);
        Assert.Equal(first.Publication.ContentHash, obligation.PublicationHash);
        var second = new TraceStoragePolicySnapshot(new TraceStoragePolicyPublication(5,
            Policy(), Guid.NewGuid(), first.Publication.PrincipalId, first.Publication.SessionId,
            9, Guid.NewGuid(), first.Publication.PublishedAtUtc.AddMinutes(1), first.Publication.ContentHash));
        var sameArtifactAndRule = new TraceRetentionObligation(obligation.ArtifactContentHash, second,
            second.RetentionRules.First(), obligation.StartedAtUtc);
        Assert.Equal(obligation.Rule.ContentHash, sameArtifactAndRule.Rule.ContentHash);
        Assert.NotEqual(obligation.ContentHash, sameArtifactAndRule.ContentHash);
        Assert.Throws<ArgumentException>(() => new TraceRetentionObligation(
            new string('B', 64), first, otherRule,
            DateTimeOffset.Parse("2026-09-11T00:00:00Z")));
    }

    private static TraceStoragePolicyDefinition Policy(
        IEnumerable<TraceRetentionRule>? rules = null,
        IEnumerable<TraceStorageRouteLimit>? routes = null,
        string rationale = "approved storage governance") =>
        new("trace-policy", "2026.09.11.1", "change-40", "1", rationale,
            rules ?? Rules(), 10_000_000, 10m, routes ?? Routes(), Backlog(),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), Budget(), Budget(), 50_000_000);

    private static IReadOnlyList<TraceRetentionRule> Rules() =>
        Enum.GetValues<TraceRetentionClass>().Select(value => new TraceRetentionRule(value,
            value is TraceRetentionClass.CompletedExport ? RetentionStartEvent.ExportCompleted :
            value is TraceRetentionClass.QuarantineEvidence ? RetentionStartEvent.Quarantined :
            RetentionStartEvent.ArtifactCreated, TimeSpan.FromDays(30))).ToArray();

    private static IReadOnlyList<TraceStorageRouteLimit> Routes() => new[]
    {
        new TraceStorageRouteLimit("mes", "1", new string('B', 64), Backlog()),
        new TraceStorageRouteLimit("spc", "1", new string('C', 64), Backlog())
    };

    private static TraceBacklogLimits Backlog() =>
        new(1000, 100_000_000, TimeSpan.FromDays(7));

    private static TraceStorageMaintenanceBudget Budget() =>
        new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 10_000_000, 100);
}
