using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionInspectionOutputTests
{
    [Fact]
    public async Task V142_R08_PreRequestRevocationPreservesConfirmedOutputAndAllowsNextWrite()
    {
        var attempts = 0;
        var writes = new List<(bool Ready, bool Valid)>();
        var latch = new InspectionCycleOutputLatch((ready, busy, valid, fault, violation, token) =>
        {
            if (++attempts == 1) throw new PlcRequestRevokedException();
            writes.Add((ready, valid));
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<PlcRequestRevokedException>(() => latch.ChangeAsync(CancellationToken.None, ready: true));
        Assert.False(latch.ConfirmedResultValid);
        Assert.Empty(writes);
        await latch.ChangeAsync(CancellationToken.None, ready: false);
        await latch.ChangeAsync(CancellationToken.None, ready: true);
        Assert.Equal(new[] { (false, false), (true, false) }, writes);
    }

    [Fact]
    public async Task V142_R09_UncertainWriteSealsOutputAgainstLaterPartialChanges()
    {
        var attempts = 0;
        var latch = new InspectionCycleOutputLatch((ready, busy, valid, fault, violation, token) =>
        {
            ++attempts;
            throw new IOException("ResponseLost");
        });
        await Assert.ThrowsAsync<IOException>(() => latch.ChangeAsync(CancellationToken.None, valid: true));
        Assert.Null(latch.ConfirmedResultValid);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => latch.ChangeAsync(CancellationToken.None, ready: false));
        Assert.Equal("InspectionCycleOutputStateUncertain", error.Message);
        Assert.Equal(1, attempts);
    }
}
