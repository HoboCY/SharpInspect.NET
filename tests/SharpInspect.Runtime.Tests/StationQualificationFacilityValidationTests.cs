using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class StationQualificationFacilityValidationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void V137_F01_ValidIsolatedObservationAcceptsTransientAndTargetControllers()
    {
        var request = CreateRequest();

        var transient = CreateObservation(request, isolated: true, targetController: false);
        var target = CreateObservation(request, isolated: true, targetController: true);

        Assert.Null(StationQualificationFacilityValidator.ValidateObservation(
            request, transient, previousSequence: 0, Now, TimeSpan.FromSeconds(1),
            isolated: true, targetController: false));
        Assert.Null(StationQualificationFacilityValidator.ValidateObservation(
            request, target, previousSequence: 0, Now, TimeSpan.FromSeconds(1),
            isolated: true, targetController: true));
    }

    [Fact]
    public void V137_F02_ObservationIdentitySessionFreshnessAndHealthAreRequired()
    {
        var request = CreateRequest();
        var valid = CreateObservation(request, isolated: true);

        var wrongIdentity = CreateObservation(request, isolated: true,
            identity: new QualificationHarnessIdentity("Fixture.Harness", "1", Hash('7'),
                Hash('F'), developmentOnly: true));
        Assert.Equal("QualificationObservationHarnessIdentityMismatch",
            StationQualificationFacilityValidator.ValidateObservation(
                request, wrongIdentity, 0, Now, TimeSpan.FromSeconds(1), true, false));

        var wrongSessionRequest = new QualificationFacilityRequest(Guid.NewGuid(),
            request.LeaseNonce, request.RuntimeEpoch, request.Plan);
        Assert.Equal("QualificationObservationSessionMismatch",
            StationQualificationFacilityValidator.ValidateObservation(
                wrongSessionRequest, valid, 0, Now, TimeSpan.FromSeconds(1), true, false));

        Assert.Equal("QualificationObservationSequenceNotIncreasing",
            StationQualificationFacilityValidator.ValidateObservation(
                request, valid, 1, Now, TimeSpan.FromSeconds(1), true, false));
        Assert.Equal("QualificationObservationStale",
            StationQualificationFacilityValidator.ValidateObservation(
                request, CreateObservation(request, true, observedAt: Now.AddSeconds(-1.1)),
                0, Now, TimeSpan.FromSeconds(1), true, false));
        Assert.Equal("QualificationObservationFuture",
            StationQualificationFacilityValidator.ValidateObservation(
                request, CreateObservation(request, true, observedAt: Now.AddMilliseconds(1)),
                0, Now, TimeSpan.FromSeconds(1), true, false));
        Assert.Equal("QualificationObservationNotConnected",
            StationQualificationFacilityValidator.ValidateObservation(
                request, CreateObservation(request, true, connected: false),
                0, Now, TimeSpan.FromSeconds(1), true, false));
        Assert.Equal("QualificationObservationLineNotStopped",
            StationQualificationFacilityValidator.ValidateObservation(
                request, CreateObservation(request, true, lineStopped: false),
                0, Now, TimeSpan.FromSeconds(1), true, false));
        Assert.Equal("QualificationObservationControllerMismatch",
            StationQualificationFacilityValidator.ValidateObservation(
                request, CreateObservation(request, true, controllerHash: Hash('0')),
                0, Now, TimeSpan.FromSeconds(1), true, false));
    }

    [Fact]
    public void V137_F03_IsolationRequiresExactDestinationsAndSafeOutputStates()
    {
        var request = CreateRequest();

        var missing = new QualificationFacilityObservation(
            request.Plan.QualificationHarnessIdentity, request.SessionId, request.LeaseNonce,
            request.RuntimeEpoch, 1, Now, connected: true, lineStopped: true,
            request.Plan.TransientControllerConfiguration.ContentHash,
            request.Plan.DestinationBindings.Take(4).Select(binding =>
                new QualificationDestinationObservation(binding.Kind, binding.DestinationId,
                    binding.TargetBindingHash, QualificationDestinationRoute.IsolatedTest, false)));
        Assert.Equal("QualificationObservationDestinationSetIncomplete",
            StationQualificationFacilityValidator.ValidateObservation(
                request, missing, 0, Now, TimeSpan.FromSeconds(1), true, false));

        var nonPlcOutput = CreateObservation(request, isolated: true, destinationFactory: binding =>
            new QualificationDestinationObservation(binding.Kind, binding.DestinationId,
                binding.TargetBindingHash, QualificationDestinationRoute.IsolatedTest,
                binding.Kind != QualificationDestinationKind.ProductionPlcOutput));
        Assert.Equal("QualificationObservationDestinationOutputEnabled",
            StationQualificationFacilityValidator.ValidateObservation(
                request, nonPlcOutput, 0, Now, TimeSpan.FromSeconds(1), true, false));

        var productionRoute = CreateObservation(request, isolated: true, destinationFactory: binding =>
            new QualificationDestinationObservation(binding.Kind, binding.DestinationId,
                binding.TargetBindingHash, QualificationDestinationRoute.Production, false));
        Assert.Equal("QualificationObservationDestinationNotIsolated",
            StationQualificationFacilityValidator.ValidateObservation(
                request, productionRoute, 0, Now, TimeSpan.FromSeconds(1), true, false));

        var disconnectedWithOutput = CreateObservation(request, isolated: true, destinationFactory: binding =>
            new QualificationDestinationObservation(binding.Kind, binding.DestinationId,
                binding.TargetBindingHash, binding.Kind == QualificationDestinationKind.ProductionPlcOutput
                    ? QualificationDestinationRoute.Disconnected : QualificationDestinationRoute.IsolatedTest,
                binding.Kind == QualificationDestinationKind.ProductionPlcOutput));
        Assert.Equal("QualificationObservationDestinationOutputEnabled",
            StationQualificationFacilityValidator.ValidateObservation(
                request, disconnectedWithOutput, 0, Now, TimeSpan.FromSeconds(1), true, false));

        var wrongBinding = CreateObservation(request, isolated: true, destinationFactory: binding =>
            new QualificationDestinationObservation(binding.Kind, binding.DestinationId, Hash('0'),
                QualificationDestinationRoute.IsolatedTest, false));
        Assert.Equal("QualificationObservationDestinationBindingMismatch",
            StationQualificationFacilityValidator.ValidateObservation(
                request, wrongBinding, 0, Now, TimeSpan.FromSeconds(1), true, false));
    }

    [Fact]
    public void V137_F04_ReleasedObservationRequiresProductionRoutesAndOutputsDisabled()
    {
        var request = CreateRequest();
        var valid = CreateObservation(request, isolated: false, targetController: true);
        Assert.Null(StationQualificationFacilityValidator.ValidateObservation(
            request, valid, 0, Now, TimeSpan.FromSeconds(1), isolated: false, targetController: true));

        var isolatedRoute = CreateObservation(request, isolated: false, targetController: true,
            destinationFactory: binding => new QualificationDestinationObservation(binding.Kind,
                binding.DestinationId, binding.TargetBindingHash,
                QualificationDestinationRoute.IsolatedTest, false));
        Assert.Equal("QualificationObservationDestinationNotRestored",
            StationQualificationFacilityValidator.ValidateObservation(
                request, isolatedRoute, 0, Now, TimeSpan.FromSeconds(1), false, true));

        var enabled = CreateObservation(request, isolated: false, targetController: true,
            destinationFactory: binding => new QualificationDestinationObservation(binding.Kind,
                binding.DestinationId, binding.TargetBindingHash, QualificationDestinationRoute.Production,
                true));
        Assert.Equal("QualificationObservationDestinationOutputEnabled",
            StationQualificationFacilityValidator.ValidateObservation(
                request, enabled, 0, Now, TimeSpan.FromSeconds(1), false, true));
    }

    [Fact]
    public void V137_F05_StimulusBindsLeasePlanScenarioAndMonotonicSequence()
    {
        var request = CreateRequest();
        var valid = new QualificationFacilityStimulus(request.SessionId, request.LeaseNonce,
            positiveSequence: 1, request.Plan.ScenarioIds[0], request.Plan.QualificationContextHash,
            controllerEpoch: 1, cycleSequence: 1);

        Assert.Null(StationQualificationFacilityValidator.ValidateStimulus(request, valid, 0));
        Assert.Equal("QualificationStimulusSequenceNotIncreasing",
            StationQualificationFacilityValidator.ValidateStimulus(request, valid, 1));
        Assert.Equal("QualificationStimulusScenarioMismatch",
            StationQualificationFacilityValidator.ValidateStimulus(request,
                new QualificationFacilityStimulus(request.SessionId, request.LeaseNonce, 2,
                    "Unknown.Scenario", request.Plan.QualificationContextHash, 1, 2), 1));
        Assert.Equal("QualificationStimulusContextMismatch",
            StationQualificationFacilityValidator.ValidateStimulus(request,
                new QualificationFacilityStimulus(request.SessionId, request.LeaseNonce, 2,
                    request.Plan.ScenarioIds[0], Hash('0'), 1, 2), 1));
        Assert.Equal("QualificationStimulusControllerEpochInvalid",
            StationQualificationFacilityValidator.ValidateStimulus(request,
                new QualificationFacilityStimulus(request.SessionId, request.LeaseNonce, 2,
                    request.Plan.ScenarioIds[0], request.Plan.QualificationContextHash, 0, 2), 1));
        Assert.Equal("QualificationStimulusCycleSequenceInvalid",
            StationQualificationFacilityValidator.ValidateStimulus(request,
                new QualificationFacilityStimulus(request.SessionId, request.LeaseNonce, 2,
                    request.Plan.ScenarioIds[0], request.Plan.QualificationContextHash, 1, 0), 1));

        var wrongSession = new QualificationFacilityStimulus(Guid.NewGuid(), request.LeaseNonce, 2,
            request.Plan.ScenarioIds[0], request.Plan.QualificationContextHash, 1, 2);
        Assert.Equal("QualificationStimulusSessionMismatch",
            StationQualificationFacilityValidator.ValidateStimulus(request, wrongSession, 1));
    }

    [Fact]
    public void V137_F06_InvalidFreshnessAndPreviousSequencesFailClosed()
    {
        var request = CreateRequest();
        var observation = CreateObservation(request, isolated: true);
        Assert.Equal("QualificationObservationFreshnessInvalid",
            StationQualificationFacilityValidator.ValidateObservation(
                request, observation, 0, Now, TimeSpan.Zero, true, false));
        Assert.Equal("QualificationObservationFreshnessInvalid",
            StationQualificationFacilityValidator.ValidateObservation(
                request, observation, 0, Now, TimeSpan.FromMinutes(6), true, false));
        Assert.Equal("QualificationObservationPreviousSequenceInvalid",
            StationQualificationFacilityValidator.ValidateObservation(
                request, observation, -1, Now, TimeSpan.FromSeconds(1), true, false));
        Assert.Equal("QualificationStimulusPreviousSequenceInvalid",
            StationQualificationFacilityValidator.ValidateStimulus(
                request, new QualificationFacilityStimulus(request.SessionId, request.LeaseNonce, 1,
                    request.Plan.ScenarioIds[0], request.Plan.QualificationContextHash, 1, 1), -1));
    }

    private static QualificationFacilityRequest CreateRequest() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CreatePlan());

    private static QualificationFacilityObservation CreateObservation(
        QualificationFacilityRequest request, bool isolated, bool targetController = false,
        long sequence = 1, DateTimeOffset? observedAt = null, bool connected = true,
        bool lineStopped = true, QualificationHarnessIdentity? identity = null,
        string? controllerHash = null,
        Func<QualificationDestinationBinding, QualificationDestinationObservation>? destinationFactory = null)
    {
        var plan = request.Plan;
        var effectiveControllerHash = controllerHash ?? (targetController
            ? plan.TargetControllerConfiguration.ContentHash
            : plan.TransientControllerConfiguration.ContentHash);
        var destinations = plan.DestinationBindings.Select(binding => destinationFactory is null
            ? new QualificationDestinationObservation(binding.Kind, binding.DestinationId,
                binding.TargetBindingHash,
                isolated ? QualificationDestinationRoute.IsolatedTest : QualificationDestinationRoute.Production,
                isolated && binding.Kind == QualificationDestinationKind.ProductionPlcOutput)
            : destinationFactory(binding)).ToArray();
        return new QualificationFacilityObservation(identity ?? plan.QualificationHarnessIdentity,
            request.SessionId, request.LeaseNonce, request.RuntimeEpoch, sequence,
            observedAt ?? Now, connected, lineStopped, effectiveControllerHash, destinations);
    }

    private static StationQualificationPlan CreatePlan()
    {
        var target = new QualificationControllerConfiguration(Hash('A'), Hash('B'), new byte[] { 1, 2 });
        var transient = new QualificationControllerConfiguration(Hash('C'), Hash('D'), new byte[] { 3, 4 });
        return new(new RecipeActivationReference(1, Guid.NewGuid(), Hash('9')), Hash('A'), Hash('B'),
            Hash('C'), Hash('D'), new QualificationHarnessIdentity("Fixture.Harness", "1",
                Hash('E'), Hash('F'), developmentOnly: true), target, transient, Bindings(),
            new[] { "Scenario.A", "Scenario.B" });
    }

    private static QualificationDestinationBinding[] Bindings() => new[]
    {
        new QualificationDestinationBinding(QualificationDestinationKind.ProductionPlcOutput,
            "plc", Hash('1')),
        new QualificationDestinationBinding(QualificationDestinationKind.OrdinaryOutbox,
            "outbox", Hash('2')),
        new QualificationDestinationBinding(QualificationDestinationKind.Mes, "mes", Hash('3')),
        new QualificationDestinationBinding(QualificationDestinationKind.Spc, "spc", Hash('4')),
        new QualificationDestinationBinding(QualificationDestinationKind.Yield, "yield", Hash('5'))
    };

    private static string Hash(char value) => new(value, 64);
}
