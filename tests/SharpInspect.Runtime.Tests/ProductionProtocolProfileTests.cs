using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Qualification;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionProtocolProfileTests
{
    [Fact]
    public async Task V142_P01_ProductionReadyUsesItsOwnWireBitAndReadsBackFromTheRealPeer()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var profile = peer.CreateProductionProfile();
        await using var channel = new ModbusQualificationChannel(profile);
        await channel.ConnectAsync();
        await channel.WriteStateAsync(true, false, false, false, false);
        var observed = await channel.ReadRuntimeStateAsync();
        Assert.True(observed.ProductionReady);
        Assert.False(observed.QualificationReady);
        Assert.Equal(0, peer.ReadyWriteCount);
        Assert.Equal(1, peer.ProductionReadyWriteCount);
        Assert.All(peer.StateWrites, write => Assert.False(write.QualificationReady));
        await Assert.ThrowsAsync<ArgumentException>(() => channel.WriteSingleRegisterAsync(
            checked((ushort)(profile.RuntimeStartAddress + 5)), 1));
        await channel.WriteStateAsync(false, false, false, false, false);
        Assert.True(peer.IsRuntimeClear);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("ff02::1")]
    [InlineData("controller.example.invalid")]
    public void V142_P02_ProductionProfileRejectsNonUnicastOrUnresolvedAddresses(string address)
    {
        Assert.Throws<ArgumentException>(() => Profile(address: address));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(300, 200)]
    [InlineData(100, 400)]
    [InlineData(65531, 200)]
    public void V142_P03_ProductionProfileRejectsOverlappingOrOverflowingBlocks(int controller, int runtime)
    {
        Assert.ThrowsAny<ArgumentException>(() => Profile(controller: (ushort)controller, runtime: (ushort)runtime));
    }

    [Fact]
    public void V142_P04_ProductionEvidenceChoiceIsExplicitAndHashBound()
    {
        var profile = Profile();
        var none = new ProductionInspectionOptions("V142.IsolatedStation", ProductionEvidenceRequirement.None,
            profile, 1, new string('A', 64), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var required = new ProductionInspectionOptions("V142.IsolatedStation", ProductionEvidenceRequirement.Required,
            profile, 1, new string('A', 64), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        Assert.NotEqual(none.ContentHash, required.ContentHash);
        Assert.Equal(ProductionEvidenceRequirement.Required, required.EvidenceRequirement);
    }

    private static ModbusProductionProfile Profile(string address = "127.0.0.1",
        ushort controller = 100, ushort runtime = 200)
    {
        var policy = new PlcCommunicationPolicy("V142.Policy", "1", TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(40), 2,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        return new("V142.Production", "1", address, 1502, 1, controller, runtime,
            new ModbusCommunicationBinding(policy, 300, 400), TimeSpan.FromSeconds(2));
    }
}
