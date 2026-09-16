using SharpInspect.Abstractions;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class TraceStorageCapacityMonitorTests
{
    [Fact, Trait("VerificationId", "V155_C04")]
    public async Task V155_C04_TimedOutObserverRetainsPhysicalOwnerAndDiscardsLateObservation()
    {
        await using var fixture = await TraceCheckpointTests.CreateFixtureAsync(TimeSpan.FromMilliseconds(500));
        var published = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(
            await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 0, TraceCheckpointTests.Policy()));
        Assert.True(published.Succeeded, published.Outcome.ReasonCode);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var turns = 0;
        var changed = new SemaphoreSlim(0);
        var monitor = new TraceStorageCapacityMonitor(fixture.Options, Guid.NewGuid(), Task.CompletedTask,
            () => changed.Release(), (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref turns) == 2) { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); }
                return new(100, 0, new[] { new TraceStorageAreaObservation(TraceStorageArea.Database,
                    new string('A', 64), 100_000_000, 50_000_000, 1_048_576, 100, 1, null, null) });
            });
        try
        {
            Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(monitor.Current().Available, monitor.Current().ReasonCode);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(monitor.Current().Available);
            Assert.Equal("TraceStorageObservationDeadlineExceeded", monitor.Current().ReasonCode);
            Assert.False(await monitor.StopAsync());
            Assert.False(monitor.PhysicalCompletion.IsCompleted);
            Assert.Equal(2, Volatile.Read(ref turns));
            release.Set();
            await monitor.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(monitor.Current().Available);
            Assert.Equal(2, Volatile.Read(ref turns));
        }
        finally { release.Set(); await monitor.StopAsync(); await monitor.PhysicalCompletion; changed.Dispose(); }
    }
}
