using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraNetworkContractTests
{
    [Theory]
    [InlineData("127.0.0.1", 24)]
    [InlineData("0.1.2.3", 24)]
    [InlineData("224.1.2.3", 24)]
    [InlineData("255.255.255.255", 24)]
    [InlineData("192.168.4.0", 24)]
    [InlineData("192.168.4.255", 24)]
    [InlineData("192.168.04.2", 24)]
    [InlineData("192.168.4.256", 24)]
    [InlineData("192.168.4", 24)]
    [InlineData("192.168.4.2 ", 24)]
    [InlineData("0xC0.168.4.2", 24)]
    [InlineData("::1", 24)]
    [InlineData("192.168.4.2", 0)]
    [InlineData("192.168.4.2", 31)]
    public void V120_C01_InvalidOrAmbiguousIpv4SettingsAreRejected(string address, int prefix) =>
        Assert.ThrowsAny<ArgumentException>(() => new CameraIpv4Configuration(address, prefix));

    [Theory]
    [InlineData("192.168.5.1")]
    [InlineData("192.168.4.20")]
    [InlineData("192.168.4.0")]
    [InlineData("192.168.4.255")]
    public void V120_C02_GatewayMustBeADifferentHostOnTheDeclaredSubnet(string gateway) =>
        Assert.ThrowsAny<ArgumentException>(() => new CameraIpv4Configuration("192.168.4.20", 24, gateway));

    [Fact]
    public void V120_C03_StationSubnetAndCompleteConfigurationEqualityAreExplicit()
    {
        var camera = new CameraIpv4Configuration("192.168.4.20", 24, "192.168.4.1");
        Assert.True(camera.IsInSameSubnet(new CameraIpv4Configuration("192.168.4.2", 24)));
        Assert.False(camera.IsInSameSubnet(new CameraIpv4Configuration("192.168.5.2", 24)));
        Assert.False(camera.IsInSameSubnet(new CameraIpv4Configuration("192.168.4.2", 16)));
        Assert.Equal(camera, new CameraIpv4Configuration("192.168.4.20", 24, "192.168.4.1"));
        Assert.NotEqual(camera, new CameraIpv4Configuration("192.168.4.20", 24));
    }

    [Fact]
    public void V120_C04_CoreProviderHasNoNetworkMutationAndMaintenanceNeverGrantsProduction()
    {
        Assert.Equal(new[] { "DiscoverAsync", "OpenAsync", "get_Identity" },
            typeof(ICameraProvider).GetMethods().Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.False(typeof(ICameraProvider).IsAssignableFrom(typeof(ICameraNetworkConfigurator)));
        var provider = new CameraProviderIdentity("Provider", "1", "Package", "1");
        var target = new CameraBindingTarget(provider, "Serial:One");
        var requested = new CameraIpv4Configuration("192.168.4.20", 24);
        var snapshot = new CameraNetworkSnapshot(Guid.NewGuid(), target,
            CameraNetworkMaintenanceState.Succeeded, new("192.168.4.10", 24), requested,
            requested, true, "Changed", DateTimeOffset.UtcNow);
        Assert.True(snapshot.RequiresRecipeActivation);
        Assert.False(snapshot.ProductionReady);
    }

    [Fact]
    public void V120_C05_AuthorizationTargetUsesStableProviderDeviceIdentityNotAddress()
    {
        var target = new CameraBindingTarget(new("Provider", "1", "Package", "1"), "Serial:One");
        var request = new CameraNetworkChangeRequest(Guid.NewGuid(),
            new(CommandSource.PhysicalConsole, Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            target, new("192.168.4.20", 24), "Maintenance");
        Assert.Equal(target.ContentHash, request.AuthorizationTargetId);
        Assert.Equal(request.AuthorizationTargetId,
            (request with { Requested = new("192.168.4.30", 24) }).AuthorizationTargetId);
        Assert.NotEqual(request.AuthorizationTargetId,
            (request with { Target = new(target.Provider, "Serial:Two") }).AuthorizationTargetId);
    }
}
