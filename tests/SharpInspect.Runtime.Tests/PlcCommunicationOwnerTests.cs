using System.Collections.Concurrent;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PlcCommunicationOwnerTests
{
    [Fact]
    public async Task V141_C15_UnreachableConfiguredEndpointExhaustsWithoutTryingAnotherPeer()
    {
        await using var configured = ModbusQualificationTestServer.Start();
        var profile = configured.CreateCommunicationProfile();
        await using var alternative = ModbusQualificationTestServer.Start();
        _ = alternative.CreateCommunicationProfile();
        await configured.DisposeAsync();
        await using var channel = new ModbusQualificationChannel(profile);
        var facts = new ConcurrentQueue<PlcCommunicationTransition>();
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue, _ => { },
            value => { facts.Enqueue(value); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<Exception>(() => channel.ConnectAsync());
        owner.Fail("PlcCommunicationTransportLost");
        await owner.RecoverAsync(channel, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(profile.CommunicationPolicy!.MaximumReconnectAttempts,
            facts.Count(value => value.Kind == "ReconnectAttempt"));
        Assert.Contains(facts, value => value.Kind == "RecoveryExhausted");
        Assert.DoesNotContain(health, value => value.Healthy);
        Assert.Equal(0, alternative.ConnectionCount);
        Assert.Equal(0, alternative.RequestCount);
    }

    [Fact]
    public async Task V141_C14_TransientInputDuringAuditRequiresAnEntireNewStableWindow()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(),
            value => { if (value.Healthy) healthyAt.TrySetResult(System.Diagnostics.Stopwatch.GetTimestamp()); }, _ => { },
            value => { if (value.Kind != "SynchronizationWindowObserved") return Task.CompletedTask;
                entered.TrySetResult(true); return release.Task; });
        await channel.ConnectAsync();
        var synchronization = owner.SynchronizeAsync(channel, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        server.SetTrigger(true);
        await Task.Delay(40);
        server.SetTrigger(false);
        var releasedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        release.TrySetResult(true);
        await synchronization.WaitAsync(TimeSpan.FromSeconds(2));
        var elapsed = TimeSpan.FromSeconds(((await healthyAt.Task) - releasedAt) /
            (double)System.Diagnostics.Stopwatch.Frequency);
        Assert.True(elapsed >= profile.CommunicationBinding!.Policy.SynchronizationStabilityWindow, elapsed.ToString());
        Assert.Equal(0, server.ReadyWriteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task V141_C13_InputsChangedWhileSynchronizationAuditWaitsCannotPublishHealthy(int change)
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue, _ => { },
            value =>
            {
                if (value.Kind != "SynchronizationWindowObserved") return Task.CompletedTask;
                entered.TrySetResult(true); return release.Task;
            });
        await channel.ConnectAsync();
        var synchronization = owner.SynchronizeAsync(channel, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (change == 0) server.SetTrigger(true);
            else if (change == 1) server.SetInitialControllerAck(true);
            else { server.SetControllerCycle(62, 1); server.FreezeControllerHeartbeat = true; }
        }
        finally { release.TrySetResult(true); }
        await Assert.ThrowsAsync<TimeoutException>(() => synchronization);
        Assert.DoesNotContain(health, value => value.Healthy || value.Synchronized);
        Assert.Equal(0, server.ReadyWriteCount);
    }

    [Fact]
    public async Task V141_C12_ObservedWrongRuntimeEpochImmediatelyInvalidatesPreviousFreshEcho()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), _ => { },
            _ => { }, _ => Task.CompletedTask);
        await channel.ConnectAsync();
        await owner.SynchronizeAsync(channel, CancellationToken.None);
        server.RuntimeEchoEpochOverride = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReadAsync(channel, CancellationToken.None));
        Assert.Equal("PlcRuntimeHeartbeatUnobserved", error.Message);
    }

    [Fact]
    public async Task V141_C11_WatchdogFailureWhileOutputWaitsForTransportPreventsLateValidWrite()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        var revoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), _ => { },
            reason => revoked.TrySetResult(reason), _ => Task.CompletedTask);
        await using var channel = new ModbusQualificationChannel(profile, owner.StartCycleWrite);
        await channel.ConnectAsync();
        await owner.SynchronizeAsync(channel, CancellationToken.None);
        var output = new InspectionCycleOutputLatch(channel.WriteStateAsync);
        var transportGate = (SemaphoreSlim)typeof(ModbusQualificationChannel)
            .GetField("_transportGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(channel)!;
        await transportGate.WaitAsync();
        Task write;
        try
        {
            write = output.ChangeAsync(CancellationToken.None, valid: true);
            Assert.False(write.IsCompleted);
            await revoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(write.IsCompleted);
        }
        finally { transportGate.Release(); }
        await Assert.ThrowsAnyAsync<Exception>(() => write);
        Assert.Empty(server.StateWrites);
        Assert.Equal(0, server.ResultValidHighCount);
    }

    [Fact]
    public async Task V141_C10_EpochSwapBetweenReadsCannotAttachOldHeartbeatToNewController()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        server.ChangeEpochAfterNextHeartbeatRead = true;
        await using var channel = new ModbusQualificationChannel(profile);
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue,
            _ => { }, _ => Task.CompletedTask);
        await channel.ConnectAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => owner.SynchronizeAsync(channel, CancellationToken.None));
        Assert.DoesNotContain(health, value => value.Healthy || value.Synchronized);
        Assert.Equal((uint)62, health.Last().ControllerEpoch);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task V141_C09_RetainedRemoteWorkCannotBeSynchronizedAsCleanIdle(
        bool busy, bool valid, bool fault, bool violation)
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var facts = new ConcurrentQueue<PlcCommunicationTransition>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), _ => { },
            _ => { }, value => { facts.Enqueue(value); return Task.CompletedTask; });
        await channel.ConnectAsync();
        await owner.SynchronizeAsync(channel, CancellationToken.None);
        server.SeedRetainedRuntimeState(busy, valid, fault, violation);
        owner.Fail("PlcCommunicationTransportLost");
        await owner.RecoverAsync(channel, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.DoesNotContain(facts, value => value.Generation > 1 &&
            value.Kind is "SynchronizationWindowObserved" or "RecoveryCompleted");
        Assert.Contains(facts, value => value.Kind == "RecoveryExhausted");
        Assert.Equal(busy, server.RuntimeBusy);
        Assert.Equal(valid, server.RuntimeResultValid);
    }

    [Fact]
    public async Task V141_C01_RealMutualHeartbeatSynchronizesWithoutWritingAnyReady()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        var facts = new ConcurrentQueue<PlcCommunicationTransition>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue,
            _ => { }, value => { facts.Enqueue(value); return Task.CompletedTask; });
        await channel.ConnectAsync();
        Assert.Empty(health);
        await owner.SynchronizeAsync(channel, CancellationToken.None);
        Assert.True(health.Last().Healthy);
        Assert.Equal((uint)61, health.Last().ControllerEpoch);
        Assert.Contains(facts, value => value.Kind == "SynchronizationWindowObserved");
        Assert.Equal(0, server.ReadyWriteCount);
        Assert.Equal(0, server.ProductionReadyWriteCount);
        Assert.All(server.Writes, value => Assert.Equal(profile.CommunicationBinding!.RuntimeStartAddress, value.StartAddress));
    }

    [Theory]
    [InlineData(true, "PlcControllerHeartbeatStale")]
    [InlineData(false, "PlcRuntimeHeartbeatUnobserved")]
    public async Task V141_C02_EitherStoppedDirectionRevokesEvenWithSuccessfulReads(bool controller, string reason)
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var revoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), _ => { },
            value => revoked.TrySetResult(value), _ => Task.CompletedTask);
        await channel.ConnectAsync();
        await owner.SynchronizeAsync(channel, CancellationToken.None);
        server.FreezeControllerHeartbeat = controller;
        server.FreezeRuntimeEcho = !controller;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!revoked.Task.IsCompleted)
        {
            try { await owner.ReadAsync(channel, deadline.Token); }
            catch (InvalidOperationException) { }
            await Task.Delay(20, deadline.Token);
        }
        Assert.Equal(reason, await revoked.Task);
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(owner.RequireHealthy).Message);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task V141_C03_WrongRuntimeEpochEchoNeverSynchronizes()
    {
        await using var server = ModbusQualificationTestServer.Start();
        server.RuntimeEchoEpochOverride = Guid.NewGuid();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue,
            _ => { }, _ => Task.CompletedTask);
        await channel.ConnectAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => owner.SynchronizeAsync(channel, CancellationToken.None));
        Assert.DoesNotContain(health, value => value.Healthy || value.RuntimeHeartbeatObserved);
        Assert.Equal(0, server.ReadyWriteCount);
    }

    [Fact]
    public async Task V141_C04_ResidualHandshakeAndEpochJitterResetSynchronizationWindow()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        server.SetTrigger(true);
        await using var channel = new ModbusQualificationChannel(profile);
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue,
            _ => { }, _ => Task.CompletedTask);
        await channel.ConnectAsync();
        var synchronization = owner.SynchronizeAsync(channel, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(synchronization.IsCompleted);
        server.SetControllerCycle(62, 1);
        await Task.Delay(100);
        Assert.False(synchronization.IsCompleted);
        server.SetTrigger(false);
        await synchronization.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(health.Last().Healthy);
        Assert.Equal((uint)62, health.Last().ControllerEpoch);
        Assert.Equal(0, server.ReadyWriteCount);
    }

    [Fact]
    public async Task V141_C05_ConnectedButStaleReconnectAttemptsHaveOneFiniteBudget()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var facts = new ConcurrentQueue<PlcCommunicationTransition>();
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue,
            _ => { }, value => { facts.Enqueue(value); return Task.CompletedTask; });
        await channel.ConnectAsync();
        await owner.SynchronizeAsync(channel, CancellationToken.None);
        server.FreezeControllerHeartbeat = true;
        owner.Fail("PlcControllerHeartbeatStale");
        await owner.RecoverAsync(channel, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(2, facts.Count(value => value.Kind == "ReconnectAttempt"));
        Assert.Single(facts, value => value.Kind == "RecoveryCycleStarted");
        Assert.Single(facts, value => value.Kind == "RecoveryExhausted");
        Assert.Equal(3, server.ConnectionCount);
        Assert.Equal("PlcRecoveryAttemptsExhausted", health.Last().ReasonCode);
        await owner.RecoverAsync(channel, false, CancellationToken.None);
        Assert.Equal(3, server.ConnectionCount);
        Assert.Equal(0, server.ReadyWriteCount);
    }

    [Fact]
    public async Task V141_C06_IdleEpochChangeRevokesOldGenerationAndRequiresArmAfterResynchronization()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var facts = new ConcurrentQueue<PlcCommunicationTransition>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), _ => { },
            _ => { }, value => { facts.Enqueue(value); return Task.CompletedTask; });
        await channel.ConnectAsync();
        await owner.SynchronizeAsync(channel, CancellationToken.None);
        server.SetControllerCycle(62, 1);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ReadAsync(channel, CancellationToken.None));
        Assert.Equal("QualificationControllerEpochChanged", failure.Message);
        await owner.RecoverAsync(channel, false, CancellationToken.None);
        Assert.Contains(facts, value => value.Kind == "RecoveryCompleted" && value.ControllerEpoch == 62);
        Assert.Throws<InvalidOperationException>(owner.RequireHealthy);
        Assert.Equal(0, server.ReadyWriteCount);
        Assert.Equal(0, server.ResultValidHighCount);
    }

    [Fact]
    public async Task V141_C07_IndependentWatchdogRevokesWhenSamplingIsNotProgressing()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var revoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), _ => { },
            value => revoked.TrySetResult(value), _ => Task.CompletedTask);
        await channel.ConnectAsync();
        await owner.SynchronizeAsync(channel, CancellationToken.None);
        var reason = await revoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(reason, new[] { "PlcControllerHeartbeatStale", "PlcRuntimeHeartbeatUnobserved" });
        Assert.Throws<InvalidOperationException>(owner.RequireHealthy);
    }

    [Fact]
    public async Task V141_C08_AuditFailureCannotPublishSynchronizedHealth()
    {
        await using var server = ModbusQualificationTestServer.Start();
        var profile = server.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        var health = new ConcurrentQueue<PlcCommunicationHealth>();
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), health.Enqueue, _ => { },
            value => value.Kind == "SynchronizationWindowObserved" ? Task.FromException(new IOException("audit unavailable")) : Task.CompletedTask);
        await channel.ConnectAsync();
        await Assert.ThrowsAsync<IOException>(() => owner.SynchronizeAsync(channel, CancellationToken.None));
        Assert.DoesNotContain(health, value => value.Healthy);
        Assert.Equal(0, server.ReadyWriteCount);
    }
}
