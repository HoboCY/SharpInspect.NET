using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class StationQualificationContractTests
{
    [Fact]
    public void V137_C01_ControllerBytesAndPlanCollectionsAreDefensive()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var configuration = new QualificationControllerConfiguration(Hash('A'), Hash('B'), bytes);
        bytes[0] = 99;
        var firstRead = configuration.GetConfigurationBytes();
        firstRead[1] = 99;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, configuration.GetConfigurationBytes());

        var destinations = Destinations();
        var scenarios = new List<string> { "Scenario.B", "Scenario.A" };
        var plan = CreatePlan(configuration, configuration, destinations, scenarios);
        destinations[0] = new QualificationDestinationBinding(
            QualificationDestinationKind.ProductionPlcOutput, "changed", Hash('F'));
        scenarios[0] = "changed";

        Assert.Equal("Scenario.A", Assert.Single(plan.ScenarioIds, value => value == "Scenario.A"));
        Assert.Equal(5, plan.DestinationBindings.Count);
        Assert.Equal("plc", plan.DestinationBindings[0].DestinationId);
        Assert.NotEqual(Hash('F'), plan.DestinationBindings[0].TargetBindingHash);
    }

    [Fact]
    public void V137_C02_TargetAndTransientContextRemainIndependent()
    {
        var targetConfiguration = new QualificationControllerConfiguration(Hash('A'), Hash('B'), new byte[] { 7 });
        var transientConfiguration = new QualificationControllerConfiguration(Hash('E'), Hash('F'), new byte[] { 8 });
        var first = CreatePlan(targetConfiguration, transientConfiguration, Destinations(), new[] { "Scenario.A" });
        var changedContext = new StationQualificationPlan(first.TargetActivation,
            first.TargetStationFingerprint, first.ReleaseCandidateFingerprint, first.ProfileHash,
            Hash('C'), first.QualificationHarnessIdentity, first.TargetControllerConfiguration,
            first.TransientControllerConfiguration,
            first.DestinationBindings, first.ScenarioIds);

        Assert.NotEqual(first.ContentHash, changedContext.ContentHash);
        Assert.Equal(Hash('A'), first.TargetStationFingerprint);
        Assert.Equal(Hash('D'), first.QualificationContextHash);
        Assert.Equal(Hash('C'), changedContext.QualificationContextHash);
        Assert.NotEqual(first.TargetStationFingerprint, changedContext.QualificationContextHash);

        var changedTarget = new StationQualificationPlan(first.TargetActivation, Hash('C'),
            first.ReleaseCandidateFingerprint, first.ProfileHash, first.QualificationContextHash,
            first.QualificationHarnessIdentity,
            new QualificationControllerConfiguration(Hash('1'), Hash('2'), new byte[] { 9 }),
            first.TransientControllerConfiguration, first.DestinationBindings, first.ScenarioIds);
        Assert.NotEqual(first.ContentHash, changedTarget.ContentHash);
        Assert.Equal(first.QualificationContextHash, changedTarget.QualificationContextHash);
    }

    [Fact]
    public void V137_C03_PlanRequiresEachProductionDestinationExactlyOnce()
    {
        var configuration = new QualificationControllerConfiguration(Hash('A'), Hash('B'), new byte[] { 1 });
        var missing = Destinations().Take(4).ToArray();
        Assert.Throws<ArgumentException>(() => CreatePlan(configuration, configuration, missing, new[] { "Scenario.A" }));

        var duplicate = Destinations().ToArray();
        duplicate[4] = new QualificationDestinationBinding(
            QualificationDestinationKind.ProductionPlcOutput, "yield", Hash('E'));
        Assert.Throws<ArgumentException>(() => CreatePlan(configuration, configuration, duplicate, new[] { "Scenario.A" }));

        var tooMany = Destinations().Append(new QualificationDestinationBinding(
            QualificationDestinationKind.Yield, "extra", Hash('E')));
        Assert.Throws<ArgumentException>(() => CreatePlan(configuration, configuration, tooMany, new[] { "Scenario.A" }));
    }

    [Fact]
    public void V137_C04_CommandTargetsBindExactPlanAndExitIntent()
    {
        var plan = CreatePlan(new QualificationControllerConfiguration(Hash('A'), Hash('B'), new byte[] { 1 }),
            new QualificationControllerConfiguration(Hash('C'), Hash('D'), new byte[] { 2 }),
            Destinations(), new[] { "Scenario.A" });
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole);
        var first = new StartStationQualificationSessionCommand(Guid.NewGuid(), invocation, plan,
            "qualification-start");
        var changedPlan = new StationQualificationPlan(plan.TargetActivation, Hash('C'),
            plan.ReleaseCandidateFingerprint, plan.ProfileHash, plan.QualificationContextHash,
            plan.QualificationHarnessIdentity, plan.TargetControllerConfiguration,
            plan.TransientControllerConfiguration, plan.DestinationBindings,
            plan.ScenarioIds);
        var second = new StartStationQualificationSessionCommand(Guid.NewGuid(), invocation, changedPlan,
            "qualification-start");
        var normalExit = new ExitStationQualificationSessionCommand(Guid.NewGuid(), invocation,
            Guid.NewGuid(), abort: false, "qualification-exit");
        var abortExit = new ExitStationQualificationSessionCommand(Guid.NewGuid(), invocation,
            normalExit.SessionId, abort: true, "qualification-exit");

        Assert.NotEqual(first.AuthorizationTarget, second.AuthorizationTarget);
        Assert.NotEqual(normalExit.AuthorizationTarget, abortExit.AuthorizationTarget);
        Assert.Equal("qualification-start", first.Reason);
    }

    [Fact]
    public void V137_C05_RunIdentityIsRuntimeOnlyAndSnapshotsStayNonProduction()
    {
        Assert.Empty(typeof(QualificationRunId).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance));

        var run = new QualificationRunId(Guid.NewGuid());
        var snapshot = new StationQualificationSessionSnapshot(Guid.NewGuid(), 1, Guid.NewGuid(),
            StationQualificationSessionPhase.Running, "qualification-running", DateTimeOffset.UtcNow,
            Guid.NewGuid(), Guid.NewGuid(), null, run, run,
            StationQualificationRestorationState.Pending, false, false, Guid.NewGuid());

        Assert.Equal(ExecutionKind.Qualification, run.Correlation.Kind);
        Assert.False(snapshot.Ready);
        Assert.False(snapshot.ProductionAuthority);
        Assert.False(snapshot.CanIssueQualification);
    }

    [Fact]
    public void V137_C06_FacilityObservationAndStimulusBindLeaseAndRejectDuplicates()
    {
        var session = Guid.NewGuid();
        var nonce = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var observations = new[]
        {
            new QualificationDestinationObservation(QualificationDestinationKind.ProductionPlcOutput,
                "plc", Hash('A'), QualificationDestinationRoute.IsolatedTest, false)
        };
        var identity = new QualificationHarnessIdentity("Fixture.Harness", "1", Hash('E'), Hash('F'), true);
        var observation = new QualificationFacilityObservation(identity, session, nonce, epoch, 1,
            DateTimeOffset.UtcNow, connected: true, lineStopped: true, Hash('B'), observations);
        var stimulus = new QualificationFacilityStimulus(session, nonce, 1, "Scenario.A", Hash('C'), 1, 1);

        Assert.Equal("Fixture.Harness", observation.Identity.Id);
        Assert.Equal(session, observation.SessionId);
        Assert.Equal(nonce, observation.LeaseNonce);
        Assert.False(observation.Destinations[0].OutputEnabled);
        Assert.Equal(session, stimulus.SessionId);
        Assert.Equal(nonce, stimulus.LeaseNonce);
        Assert.Equal("Scenario.A", stimulus.ScenarioId);

        var duplicate = observations.Append(new QualificationDestinationObservation(
            QualificationDestinationKind.ProductionPlcOutput, "plc-2", Hash('D'),
            QualificationDestinationRoute.Unknown, false));
        Assert.Throws<ArgumentException>(() => new QualificationFacilityObservation(identity,
            session, nonce, epoch, 2, DateTimeOffset.UtcNow, true, true, Hash('B'), duplicate));
    }

    [Fact]
    public void V137_C07_OptionsAreBoundedAndDoNotOfferProductionFlags()
    {
        var options = new StationQualificationSessionOptions();
        options.Validate();
        Assert.Equal(64, options.MaxRuns);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StationQualificationSessionOptions
        {
            MaximumRuns = 0
        }.Validate());
        Assert.DoesNotContain(typeof(IStationQualificationSessionService).GetMethods(),
            method => method.Name.Contains("Run", StringComparison.Ordinal));
    }

    private static StationQualificationPlan CreatePlan(
        QualificationControllerConfiguration targetConfiguration,
        QualificationControllerConfiguration transientConfiguration,
        IEnumerable<QualificationDestinationBinding> destinations, IEnumerable<string> scenarios) =>
        new(new RecipeActivationReference(1, Guid.NewGuid(), Hash('9')), Hash('A'), Hash('B'), Hash('C'),
            Hash('D'), new QualificationHarnessIdentity("Fixture.Harness", "1", Hash('E'), Hash('F'), true),
            targetConfiguration, transientConfiguration, destinations, scenarios);

    private static List<QualificationDestinationBinding> Destinations() => new()
    {
        new(QualificationDestinationKind.ProductionPlcOutput, "plc", Hash('1')),
        new(QualificationDestinationKind.OrdinaryOutbox, "outbox", Hash('2')),
        new(QualificationDestinationKind.Mes, "mes", Hash('3')),
        new(QualificationDestinationKind.Spc, "spc", Hash('4')),
        new(QualificationDestinationKind.Yield, "yield", Hash('5'))
    };

    private static string Hash(char value) => new(value, 64);
}
