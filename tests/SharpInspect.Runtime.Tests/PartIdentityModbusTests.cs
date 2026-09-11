using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Qualification;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PartIdentityModbusTests
{
    [Fact]
    public async Task V143_P01_StableRequestCarriesExactUtf8IdentityAndReadProof()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var plan = new ModbusPartIdentityReadPlan("PartCode.Plc", "1", 600, 64);
        var profile = peer.CreateProductionProfile(partIdentity: plan);
        peer.SetPartIdentity(2, 41, 7, " A-001 ");
        peer.RaiseTrigger(41, 7);
        await using var channel = new ModbusQualificationChannel(profile, readPartIdentity: () => true);
        await channel.ConnectAsync();
        var signals = await channel.ReadAsync();
        var identity = Assert.IsType<ModbusPartIdentitySnapshot>(signals.PartIdentity);
        Assert.Null(identity.FailureReason);
        Assert.Equal(" A-001 ", identity.Value);
        Assert.Equal((uint)2, identity.Revision);
        Assert.Equal(signals.ControllerEpoch, identity.ControllerEpoch);
        Assert.Equal(signals.CycleSequence, identity.CycleSequence);
        Assert.Equal(3, peer.PartIdentityReadCount);
        Assert.True(identity.ReceivedMonotonicTimestamp > 0);
        Assert.Equal(64, identity.ContentHash.Length);
        peer.SetPartIdentity(4, 41, 7, "B-002");
        Assert.Equal(" A-001 ", identity.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V143_P02_ConcurrentSourceOrCycleUpdateRejectsSnapshot(bool changeController)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var plan = new ModbusPartIdentityReadPlan("PartCode.Plc", "1", 600, 32);
        var profile = peer.CreateProductionProfile(partIdentity: plan);
        peer.SetPartIdentity(2, 41, 7, "A001");
        peer.RaiseTrigger(41, 7);
        peer.AfterPartIdentityBlockRead = () =>
        {
            if (changeController) peer.RaiseTrigger(42, 8);
            else peer.SetPartIdentity(4, 41, 7, "B002");
        };
        await using var channel = new ModbusQualificationChannel(profile, readPartIdentity: () => true);
        await channel.ConnectAsync();
        var signals = await channel.ReadAsync();
        Assert.Equal("PartIdentityPlcSnapshotUnstable", signals.PartIdentity?.FailureReason);
        Assert.Null(signals.PartIdentity?.Value);
    }

    [Fact]
    public async Task V143_P03_NoneReadPolicyDoesNotReadIdentityRegisters()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile(partIdentity: new("PartCode.Plc", "1", 600, 32));
        peer.RaiseTrigger(41, 7);
        await using var channel = new ModbusQualificationChannel(profile, readPartIdentity: () => false);
        await channel.ConnectAsync();
        var signals = await channel.ReadAsync();
        Assert.Null(signals.PartIdentity);
        Assert.Equal(0, peer.PartIdentityReadCount);
    }

    [Fact]
    public async Task V143_P04_IdentityReadPlanChangesProfileAndCannotOverlapControlOrHeartbeat()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var plan = new ModbusPartIdentityReadPlan("PartCode.Plc", "1", 600, 32);
        var original = peer.CreateProductionProfile();
        var extended = peer.CreateProductionProfile(partIdentity: plan);
        Assert.Equal(original.EndpointBindingHash, extended.EndpointBindingHash);
        Assert.NotEqual(original.ContentHash, extended.ContentHash);
        Assert.Throws<ArgumentException>(() => peer.CreateProductionProfile(partIdentity:
            new("PartCode.Plc", "1", original.ControllerStartAddress, 32)));
        Assert.Throws<ArgumentException>(() => peer.CreateProductionProfile(partIdentity:
            new("PartCode.Plc", "1", original.CommunicationBinding.RuntimeStartAddress, 32)));
        Assert.NotEqual(plan.ContentHash, new ModbusPartIdentityReadPlan("PartCode.Plc", "1", 601, 32).ContentHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusPartIdentityReadPlan("P", "1", 65535, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModbusPartIdentityReadPlan("P", "1", 600, 235));
    }

    [Theory]
    [InlineData(2, 41, 7, null, null, true)]
    [InlineData(3, 41, 7, null, "PartIdentityAmbiguous", false)]
    [InlineData(1, 41, 7, "", "PartIdentityPlcValueInvalid", false)]
    [InlineData(1, 41, 8, "A001", "PartIdentityPlcCycleMismatch", false)]
    public async Task V143_P05_MissingIsDistinctFromAmbiguousMalformedOrWrongCycle(int state,
        uint epoch, uint cycle, string? value, string? reason, bool missing)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile(partIdentity: new("PartCode.Plc", "1", 600, 32));
        peer.SetPartIdentity(2, epoch, cycle, value, checked((ushort)state));
        peer.RaiseTrigger(41, 7);
        await using var channel = new ModbusQualificationChannel(profile, readPartIdentity: () => true);
        await channel.ConnectAsync();
        var observed = Assert.IsType<ModbusPartIdentitySnapshot>((await channel.ReadAsync()).PartIdentity);
        Assert.Equal(reason, observed.FailureReason);
        Assert.Equal(missing, observed.Missing);
        Assert.Null(observed.Value);
    }

    [Theory]
    [InlineData("zero-revision", "PartIdentityPlcSnapshotUnstable")]
    [InlineData("odd-revision", "PartIdentityPlcSnapshotUnstable")]
    [InlineData("padding", "PartIdentityPlcPaddingInvalid")]
    [InlineData("utf8", "PartIdentityPlcUtf8Invalid")]
    public async Task V143_P06_RawPlcDecoderRejectsUnstableOrMalformedIdentity(string mutation, string reason)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile(partIdentity: new("PartCode.Plc", "1", 600, 32));
        peer.SetPartIdentity(2, 41, 7, "A001");
        peer.MutatePartIdentityBlock(block =>
        {
            switch (mutation)
            {
                case "zero-revision": Array.Clear(block, 0, 4); break;
                case "odd-revision": block[3] = 3; break;
                case "padding": block[^1] = 1; break;
                case "utf8": block[16] = 0xff; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        peer.RaiseTrigger(41, 7);
        await using var channel = new ModbusQualificationChannel(profile, readPartIdentity: () => true);
        await channel.ConnectAsync();
        var observed = Assert.IsType<ModbusPartIdentitySnapshot>((await channel.ReadAsync()).PartIdentity);
        Assert.Equal(reason, observed.FailureReason);
        Assert.Null(observed.Value);
        Assert.Equal(3, peer.PartIdentityReadCount);
    }

    [Fact]
    public void V143_P07_PreIdentityProfileConstructorRetainsPublishedV1Hash()
    {
        // Frozen by executing the published T42 DLLs before any T43 build:
        // Runtime SHA256 5618C5606BFEF7B74CBFD6AC56C829C466E195B5261478F6579F195288B874AA.
        var policy = new PlcCommunicationPolicy("V143.Legacy.Policy", "1",
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100),
            3, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
        var binding = new ModbusCommunicationBinding(policy, 100, 200);
        var profile = new ModbusProductionProfile("V143.Legacy.Profile", "1", "127.0.0.1", 502, 1,
            0, 10, binding, TimeSpan.FromMilliseconds(500));
        Assert.Null(profile.PartIdentity);
        Assert.Equal("CA5C222F277000670327BDB357CAA9EDFEE140FEB57192F65FC9ED529D0A259F", profile.ContentHash);
        Assert.Equal("88FF78F974BF1400FBBEB9F41D9133E5024DA2CEF3A47393AA9C882606F63932", profile.EndpointBindingHash);
    }
}
