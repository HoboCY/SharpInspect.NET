using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PlcCommunicationPolicyTests
{
    [Fact]
    public void V141_M01_PolicyIsImmutableVersionedAndHashBindsEveryTimingValue()
    {
        var first = Policy();
        var equivalent = Policy();
        var different = new PlcCommunicationPolicy(first.Id, first.Version,
            first.PollInterval, first.OperationTimeout,
            first.RuntimeHeartbeatInterval, first.ControllerHeartbeatInterval,
            first.ControllerHeartbeatStaleAfter, first.RuntimeHeartbeatStaleAfter,
            first.ReconnectInterval, first.MaximumReconnectAttempts + 1,
            first.SynchronizationStabilityWindow, first.SynchronizationTimeout);

        Assert.Equal(first.ContentHash, equivalent.ContentHash);
        Assert.NotEqual(first.ContentHash, different.ContentHash);
        Assert.Equal(3, first.MaximumReconnectAttempts);
    }

    [Fact]
    public void V141_M02_PolicyRejectsStaleOrUnboundedSynchronizationValues()
    {
        var valid = Policy();
        Assert.Throws<ArgumentException>(() => new PlcCommunicationPolicy(
            valid.Id, valid.Version, valid.PollInterval, valid.OperationTimeout,
            valid.RuntimeHeartbeatInterval, valid.ControllerHeartbeatInterval,
            valid.ControllerHeartbeatStaleAfter, valid.RuntimeHeartbeatStaleAfter,
            valid.ReconnectInterval, valid.MaximumReconnectAttempts,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5)));
        Assert.Throws<ArgumentException>(() => new PlcCommunicationPolicy(
            valid.Id, valid.Version, valid.PollInterval, valid.OperationTimeout,
            valid.RuntimeHeartbeatInterval, valid.ControllerHeartbeatInterval,
            valid.ControllerHeartbeatInterval, valid.RuntimeHeartbeatStaleAfter,
            valid.ReconnectInterval, valid.MaximumReconnectAttempts,
            valid.SynchronizationStabilityWindow, valid.SynchronizationTimeout));

        Assert.Throws<ArgumentException>(() => new PlcCommunicationPolicy(
            valid.Id, valid.Version, valid.PollInterval, valid.OperationTimeout,
            valid.RuntimeHeartbeatInterval, valid.ControllerHeartbeatInterval,
            valid.ControllerHeartbeatStaleAfter, valid.RuntimeHeartbeatStaleAfter,
            valid.ReconnectInterval, valid.MaximumReconnectAttempts,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2)));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PlcCommunicationPolicy(
            valid.Id, valid.Version, valid.PollInterval, valid.OperationTimeout,
            valid.RuntimeHeartbeatInterval, valid.ControllerHeartbeatInterval,
            valid.ControllerHeartbeatStaleAfter, valid.RuntimeHeartbeatStaleAfter,
            valid.ReconnectInterval, 0, valid.SynchronizationStabilityWindow,
            valid.SynchronizationTimeout));
    }

    [Fact]
    public void V141_M03_HealthDoesNotTreatTransportOrConnectionAloneAsHealthy()
    {
        var health = new PlcCommunicationHealth(true, true, true, 4, false,
            false, 0, 1, "PlcSynchronizationPending")
        {
            PolicyHash = Policy().ContentHash,
            RuntimeEpoch = Guid.NewGuid()
        };

        Assert.False(health.Healthy);
        Assert.Equal((uint)4, health.ControllerEpoch);
        Assert.Equal(1, health.ConnectionGeneration);

        var zeroEpoch = new PlcCommunicationHealth(true, true, true, 0, true,
            false, 0, 1, "PlcSynchronizationPending");
        Assert.False(zeroEpoch.Healthy);
    }

    internal static PlcCommunicationPolicy Policy() => new(
        "T141.PlcCommunication", "1", TimeSpan.FromMilliseconds(20),
        TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200), 3,
        TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
}
