using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionRecoveryTransportTests
{
    [Fact]
    public async Task V144_T07_CompletionFenceContentionDoesNotReexecutePhysicalCleanup()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var completionBusy = true;
        await using var owner = new ProductionRecoveryTransport(peer.CreateProductionProfile(),
            Guid.NewGuid(), start => start(), () => null, _ => { }, _ => Task.CompletedTask,
            () => 1, completionAuthorityFailure: () => completionBusy
                ? "ProductionRecoveryCommitFenceBusy" : null);

        var observed = await owner.ClearAndSynchronizeAsync(CancellationToken.None);
        var writes = peer.Writes.Count;
        var connections = peer.ConnectionCount;

        Assert.True(observed.Communication.Healthy);
        Assert.Equal("ProductionRecoveryCommitFenceBusy", owner.CompletionFailure());
        completionBusy = false;
        Assert.Null(owner.CompletionFailure());
        Assert.Equal(writes, peer.Writes.Count);
        Assert.Equal(connections, peer.ConnectionCount);
    }

    [Fact]
    public async Task V144_T01_ManualCleanupWritesOnlyRuntimeStateAndHeartbeatThenObservesSynchronization()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile();
        peer.AutoAcknowledge = false;
        peer.SeedRetainedRuntimeState(true, true, true, true);
        var transitions = new List<PlcCommunicationTransition>();
        await using var owner = new ProductionRecoveryTransport(profile, Guid.NewGuid(), start => start(),
            () => null, _ => { }, value => { lock (transitions) transitions.Add(value); return Task.CompletedTask; }, () => 19);

        var observed = await owner.ClearAndSynchronizeAsync(CancellationToken.None);

        Assert.True(observed.Communication.Healthy);
        Assert.Equal(19, observed.Communication.ConnectionGeneration);
        Assert.Null(owner.CompletionFailure());
        Assert.True(peer.IsRuntimeClear);
        Assert.Equal(0, peer.ProductionReadyWriteCount);
        Assert.Equal(0, peer.ResultValidHighCount);
        Assert.False(peer.ControllerResultAck);
        Assert.All(peer.Writes, value => Assert.Contains(value.StartAddress,
            new[] { profile.RuntimeStartAddress, profile.CommunicationBinding.RuntimeStartAddress }));
        Assert.All(peer.StateWrites, value => Assert.False(value.Busy || value.ResultValid ||
            value.QualificationReady || value.ProductionReady || value.CycleFault || value.ProtocolViolation));
        Assert.Contains(transitions, value => value.Kind == "SynchronizationWindowObserved");
    }

    [Fact]
    public async Task V144_T02_MissingAuthorityDoesNotOpenARecoveryConnection()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var owner = new ProductionRecoveryTransport(peer.CreateProductionProfile(), Guid.NewGuid(),
            start => start(), () => "ProductionRecoveryAuthorizationMissing", _ => { },
            _ => Task.CompletedTask, () => 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.ClearAndSynchronizeAsync(CancellationToken.None));

        Assert.Equal("ProductionRecoveryAuthorizationMissing", error.Message);
        Assert.Equal(0, peer.ConnectionCount);
        Assert.Empty(peer.Writes);
    }

    [Fact]
    public async Task V144_T03_ClearOutputRequestCannotBypassRevokedSafetyAuthority()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile();
        peer.SeedRetainedRuntimeState(true, true, false, false);
        var fenced = 0;
        await using var channel = new ModbusQualificationChannel(profile, startOwnedRequest: _ =>
        {
            ++fenced;
            throw new PlcRequestRevokedException();
        });
        await channel.ConnectAsync();

        await Assert.ThrowsAsync<PlcRequestRevokedException>(() =>
            channel.ClearProductionRecoveryStateAsync(CancellationToken.None));

        Assert.Equal(1, fenced);
        Assert.Equal(0, peer.RequestCount);
        Assert.True(peer.RuntimeBusy);
        Assert.True(peer.RuntimeResultValid);
        Assert.Empty(peer.Writes);
    }

    [Fact]
    public async Task V144_T04_RetainedControllerAckKeepsManualCleanupUnconfirmed()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile();
        peer.AutoClearAcknowledge = false;
        peer.SetInitialControllerAck(true);
        await using var owner = new ProductionRecoveryTransport(profile, Guid.NewGuid(), start => start(),
            () => null, _ => { }, _ => Task.CompletedTask, () => 1);

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            owner.ClearAndSynchronizeAsync(CancellationToken.None));

        Assert.Equal("PlcSynchronizationTimedOut", error.Message);
        Assert.True(peer.ControllerResultAck);
        Assert.Equal(0, peer.AckLowCount);
        Assert.Equal(0, peer.ProductionReadyWriteCount);
        Assert.NotNull(owner.CompletionFailure());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V144_T05_BothHeartbeatDirectionsRemainRequiredAfterManualCleanup(bool controllerFrozen)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile();
        peer.FreezeControllerHeartbeat = controllerFrozen;
        peer.FreezeRuntimeEcho = !controllerFrozen;
        await using var owner = new ProductionRecoveryTransport(profile, Guid.NewGuid(), start => start(),
            () => null, _ => { }, _ => Task.CompletedTask, () => 1);

        await Assert.ThrowsAsync<TimeoutException>(() => owner.ClearAndSynchronizeAsync(CancellationToken.None));

        Assert.NotNull(owner.CompletionFailure());
        Assert.Equal(0, peer.ProductionReadyWriteCount);
        Assert.Equal(0, peer.ResultValidHighCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V144_T06_OneAuthorizationCannotExecuteAnotherCleanupAfterSuccessOrFailure(bool failSynchronization)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile();
        peer.FreezeControllerHeartbeat = failSynchronization;
        await using var owner = new ProductionRecoveryTransport(profile, Guid.NewGuid(), start => start(),
            () => null, _ => { }, _ => Task.CompletedTask, () => 1);
        if (failSynchronization)
            await Assert.ThrowsAsync<TimeoutException>(() => owner.ClearAndSynchronizeAsync(CancellationToken.None));
        else
            await owner.ClearAndSynchronizeAsync(CancellationToken.None);
        var connections = peer.ConnectionCount;
        var writes = peer.Writes.Count;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.ClearAndSynchronizeAsync(CancellationToken.None));

        Assert.Equal("ProductionRecoveryAttemptAlreadyStarted", error.Message);
        Assert.Equal(connections, peer.ConnectionCount);
        Assert.Equal(writes, peer.Writes.Count);
    }
}
