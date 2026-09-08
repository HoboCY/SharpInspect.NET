using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class VirtualNetworkCameraProviderTests
{
    [Fact]
    public async Task V120_V01_OrdinaryVirtualProviderDoesNotAdvertiseNetworkMaintenance()
    {
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualCameraProvider(new[] { Scenario() }, clock);

        Assert.IsNotAssignableFrom<ICameraNetworkConfigurator>(provider);
    }

    [Fact]
    public async Task V120_V02_DiscoveryIsReadOnlyAndLeaseExcludesOpenWithoutClosingDevice()
    {
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(Scenario(), clock);

        var discovery = await provider.DiscoverAsync();
        Assert.True(discovery.Succeeded);
        var descriptor = Assert.Single(discovery.Devices);
        Assert.Same(provider.Identity, descriptor.Provider);

        var opened = await provider.OpenAsync(descriptor.StableDeviceIdentity);
        Assert.True(opened.Succeeded, opened.ReasonCode);
        var device = opened.Device!;

        var whileOpen = await provider.TryBeginMaintenanceAsync(
            descriptor.StableDeviceIdentity);
        Assert.False(whileOpen.Succeeded);
        Assert.Equal("VirtualNetworkDeviceOpen", whileOpen.ReasonCode);
        Assert.Equal(CameraConnectionState.Open, device.GetHealthSnapshot().Connection);

        await device.DisposeAsync();
        var reserved = await provider.TryBeginMaintenanceAsync(
            descriptor.StableDeviceIdentity);
        Assert.True(reserved.Succeeded, reserved.ReasonCode);
        var session = reserved.Session!;

        var discoveryWhileReserved = await provider.DiscoverAsync();
        Assert.True(discoveryWhileReserved.Succeeded);
        var blockedOpen = await provider.OpenAsync(descriptor.StableDeviceIdentity);
        Assert.False(blockedOpen.Succeeded);
        Assert.Equal("VirtualNetworkMaintenanceLeaseActive", blockedOpen.ReasonCode);

        await session.DisposeAsync();
        var reopened = await provider.OpenAsync(descriptor.StableDeviceIdentity);
        Assert.True(reopened.Succeeded, reopened.ReasonCode);
    }

    [Fact]
    public async Task V120_V03_ConflictDetectionIncludesOtherReservedAndForcedAddressesButIgnoresOwnAddress()
    {
        var current = new CameraIpv4Configuration("192.168.10.2", 24);
        var other = new CameraIpv4Configuration("192.168.10.4", 24);
        var reserved = new CameraIpv4Configuration("192.168.10.5", 24);
        var forced = new CameraIpv4Configuration("192.168.10.6", 24);
        var options = new VirtualNetworkCameraOptions(
            initialConfiguration: current,
            otherDeviceAddresses: new[] { other },
            reservedStationAddresses: new[] { reserved },
            forcedConflictAddresses: new[] { current, forced });
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(
            Scenario(), clock, options);
        var lease = (await provider.TryBeginMaintenanceAsync("virtual:network-one")).Session!;

        var clear = await lease.DetectConflictAsync(
            new CameraIpv4Configuration("192.168.10.3", 24), Station());
        Assert.Equal(CameraNetworkConflictState.Clear, clear.State);

        var own = await lease.DetectConflictAsync(current, Station());
        Assert.Equal(CameraNetworkConflictState.Clear, own.State);

        var conflicts = new[] { other, reserved, forced };
        foreach (var address in conflicts)
        {
            var result = await lease.DetectConflictAsync(address, Station());
            Assert.Equal(CameraNetworkConflictState.Conflict, result.State);
        }

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task V120_V04_ApplyUsesCompletePreviousValueCasAndFailureBeforeChangeIsObservable()
    {
        var current = new CameraIpv4Configuration("192.168.10.2", 24);
        var requested = new CameraIpv4Configuration("192.168.10.3", 24);
        var wrongExpected = new CameraIpv4Configuration("192.168.10.9", 24);
        var options = VirtualNetworkCameraOptions.ApplyFailureBeforeChange(
            initialConfiguration: current);
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(
            Scenario(), clock, options);
        var session = (await provider.TryBeginMaintenanceAsync("virtual:network-one")).Session!;

        var casRejected = await session.ApplyAsync(wrongExpected, requested);
        Assert.False(casRejected.Succeeded);
        Assert.Equal("VirtualNetworkExpectedPreviousMismatch", casRejected.ReasonCode);
        var afterCas = await session.ReadCurrentAsync();
        Assert.True(afterCas.Succeeded);
        Assert.Equal(current, afterCas.State!.Configuration);

        var failed = await session.ApplyAsync(current, requested);
        Assert.False(failed.Succeeded);
        Assert.Equal("VirtualNetworkApplyFailedBeforeChange", failed.ReasonCode);
        var afterFailure = await session.ReadCurrentAsync();
        Assert.True(afterFailure.Succeeded);
        Assert.Equal(current, afterFailure.State!.Configuration);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task V120_V05_FailureAfterChangeLeavesNewConfigurationObservable()
    {
        var current = new CameraIpv4Configuration("192.168.10.2", 24);
        var requested = new CameraIpv4Configuration("192.168.10.7", 24);
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(Scenario(), clock,
            VirtualNetworkCameraOptions.ApplyFailureAfterChange(current));
        var session = (await provider.TryBeginMaintenanceAsync("virtual:network-one")).Session!;

        var failed = await session.ApplyAsync(current, requested);
        Assert.False(failed.Succeeded);
        Assert.Equal("VirtualNetworkApplyFailedAfterChange", failed.ReasonCode);
        var observed = await session.ReadCurrentAsync();
        Assert.True(observed.Succeeded);
        Assert.Equal(requested, observed.State!.Configuration);
        Assert.Equal(1, provider.GetDiagnostics().Devices.Single().FailedApplyCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task V120_V06_WrongRediscoveredIdentityMakesMaintenanceStaleAfterObservableWrite()
    {
        var current = new CameraIpv4Configuration("192.168.10.2", 24);
        var requested = new CameraIpv4Configuration("192.168.10.8", 24);
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(Scenario(), clock,
            VirtualNetworkCameraOptions.WrongRediscoveredIdentity(current));
        var session = (await provider.TryBeginMaintenanceAsync("virtual:network-one")).Session!;

        var failed = await session.ApplyAsync(current, requested);
        Assert.False(failed.Succeeded);
        Assert.Equal("VirtualNetworkRediscoveredIdentityMismatch", failed.ReasonCode);
        var wrongDiscovery = await provider.DiscoverAsync();
        Assert.True(wrongDiscovery.Succeeded);
        Assert.Equal("virtual:wrong", Assert.Single(wrongDiscovery.Devices).StableDeviceIdentity);
        var staleRead = await session.ReadCurrentAsync();
        Assert.False(staleRead.Succeeded);
        Assert.Equal("VirtualNetworkMaintenanceStale", staleRead.ReasonCode);

        await session.DisposeAsync();
        var staleBegin = await provider.TryBeginMaintenanceAsync("virtual:network-one");
        Assert.False(staleBegin.Succeeded);
        Assert.Equal("VirtualNetworkMaintenanceStale", staleBegin.ReasonCode);
    }

    [Fact]
    public async Task V120_V07_WrongReadBackFailsAfterChangeWhileActualStateRemainsReadable()
    {
        var current = new CameraIpv4Configuration("192.168.10.2", 24);
        var requested = new CameraIpv4Configuration("192.168.10.10", 24);
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(Scenario(), clock,
            VirtualNetworkCameraOptions.WrongReadBack(current));
        var session = (await provider.TryBeginMaintenanceAsync("virtual:network-one")).Session!;

        var failed = await session.ApplyAsync(current, requested);
        Assert.False(failed.Succeeded);
        Assert.Equal("VirtualNetworkReadBackMismatch", failed.ReasonCode);
        var observed = await session.ReadCurrentAsync();
        Assert.True(observed.Succeeded);
        Assert.Equal(requested, observed.State!.Configuration);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task V120_V08_NoHardwareAndUnboundedFixturePlansAreRejectedClosed()
    {
        using var clock = new VirtualCameraClock(Utc());
        await using var noHardware = new VirtualNetworkCameraProvider(Scenario(), clock,
            VirtualNetworkCameraOptions.NoHardware());

        var discovery = await noHardware.DiscoverAsync();
        Assert.True(discovery.Succeeded);
        var open = await noHardware.OpenAsync("virtual:network-one");
        Assert.False(open.Succeeded);
        Assert.Equal("VirtualNetworkHardwareUnavailable", open.ReasonCode);
        var begin = await noHardware.TryBeginMaintenanceAsync("virtual:network-one");
        Assert.False(begin.Succeeded);
        Assert.Equal("VirtualNetworkHardwareUnavailable", begin.ReasonCode);

        var tooMany = Enumerable.Repeat(VirtualNetworkApplyOutcome.Success,
            VirtualNetworkCameraOptions.MaximumPlanEntries + 1);
        Assert.Throws<ArgumentException>(() => new VirtualNetworkCameraOptions(
            applyOutcomes: tooMany));
    }

    [Fact]
    public async Task V120_V09_ConflictDetectionSeesOtherFixtureCurrentAddressAndIgnoresOwnCurrent()
    {
        var own = new CameraIpv4Configuration("192.168.10.2", 24);
        var other = new CameraIpv4Configuration("192.168.10.20", 24);
        var fixtures = new[]
        {
            new VirtualNetworkCameraFixture(Scenario("virtual:network-a"),
                new VirtualNetworkCameraOptions(own, "virtual-network")),
            new VirtualNetworkCameraFixture(Scenario("virtual:network-b"),
                new VirtualNetworkCameraOptions(other, "virtual-network"))
        };
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(fixtures, clock);
        var session = (await provider.TryBeginMaintenanceAsync("virtual:network-a")).Session!;

        var ownResult = await session.DetectConflictAsync(own, Station());
        var otherResult = await session.DetectConflictAsync(other, Station());

        Assert.Equal(CameraNetworkConflictState.Clear, ownResult.State);
        Assert.Equal(CameraNetworkConflictState.Conflict, otherResult.State);
        Assert.Equal("VirtualNetworkProviderDeviceAddressConflict", otherResult.ReasonCode);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task V120_V10_ConflictDetectionSeesOtherFixtureAddressAfterThatFixtureChanges()
    {
        var own = new CameraIpv4Configuration("192.168.10.2", 24);
        var other = new CameraIpv4Configuration("192.168.10.20", 24);
        var changed = new CameraIpv4Configuration("192.168.10.21", 24);
        var fixtures = new[]
        {
            new VirtualNetworkCameraFixture(Scenario("virtual:network-a"),
                new VirtualNetworkCameraOptions(own, "virtual-network")),
            new VirtualNetworkCameraFixture(Scenario("virtual:network-b"),
                new VirtualNetworkCameraOptions(other, "virtual-network"))
        };
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(fixtures, clock);
        var otherSession = (await provider.TryBeginMaintenanceAsync("virtual:network-b")).Session!;

        var applied = await otherSession.ApplyAsync(other, changed);
        Assert.True(applied.Succeeded, applied.ReasonCode);
        await otherSession.DisposeAsync();

        var ownSession = (await provider.TryBeginMaintenanceAsync("virtual:network-a")).Session!;
        var conflict = await ownSession.DetectConflictAsync(changed, Station());

        Assert.Equal(CameraNetworkConflictState.Conflict, conflict.State);
        Assert.Equal("VirtualNetworkProviderDeviceAddressConflict", conflict.ReasonCode);

        await ownSession.DisposeAsync();
    }

    [Fact]
    public async Task V120_V11_ApplyRechecksOtherFixtureAddressAfterAPreviouslyClearDetect()
    {
        var own = new CameraIpv4Configuration("192.168.10.2", 24);
        var other = new CameraIpv4Configuration("192.168.10.20", 24);
        var requested = new CameraIpv4Configuration("192.168.10.30", 24);
        var fixtures = new[]
        {
            new VirtualNetworkCameraFixture(Scenario("virtual:network-a"),
                new VirtualNetworkCameraOptions(own, "virtual-network")),
            new VirtualNetworkCameraFixture(Scenario("virtual:network-b"),
                new VirtualNetworkCameraOptions(other, "virtual-network"))
        };
        using var clock = new VirtualCameraClock(Utc());
        await using var provider = new VirtualNetworkCameraProvider(fixtures, clock);
        var ownSession = (await provider.TryBeginMaintenanceAsync("virtual:network-a")).Session!;
        var otherSession = (await provider.TryBeginMaintenanceAsync("virtual:network-b")).Session!;

        var ownDetect = await ownSession.DetectConflictAsync(requested, Station());
        var otherDetect = await otherSession.DetectConflictAsync(requested, Station());
        Assert.Equal(CameraNetworkConflictState.Clear, ownDetect.State);
        Assert.Equal(CameraNetworkConflictState.Clear, otherDetect.State);

        // Both sessions observed Clear before either write. The provider gate
        // must serialize the CAS and conflict recheck so exactly one write can
        // claim the address.
        var otherApplyTask = Task.Run(() => otherSession.ApplyAsync(other, requested).AsTask());
        var ownApplyTask = Task.Run(() => ownSession.ApplyAsync(own, requested).AsTask());
        var applies = await Task.WhenAll(otherApplyTask, ownApplyTask);
        var ownRead = await ownSession.ReadCurrentAsync();
        var otherRead = await otherSession.ReadCurrentAsync();

        Assert.Single(applies, result => result.Succeeded);
        var failed = Assert.Single(applies, result => !result.Succeeded);
        Assert.Equal("VirtualNetworkProviderDeviceAddressConflict", failed.ReasonCode);
        Assert.True(ownRead.Succeeded);
        Assert.True(otherRead.Succeeded);
        var currentConfigurations = new[]
        {
            ownRead.State!.Configuration, otherRead.State!.Configuration
        };
        Assert.Single(currentConfigurations, configuration => configuration == requested);
        Assert.Equal(applies[1].Succeeded ? requested : own, ownRead.State!.Configuration);
        Assert.Equal(applies[0].Succeeded ? requested : other, otherRead.State!.Configuration);

        await ownSession.DisposeAsync();
        await otherSession.DisposeAsync();
    }

    private static VirtualCameraScenario Scenario(string device = "virtual:network-one")
    {
        var image = VirtualCameraImage.CreateSynthetic("frame", 2, 2,
            VisionPixelFormat.Mono8, null, 42, 1);
        var capabilities = new CameraCapabilities(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new(1, 10, 1, CameraQuantizationMode.Exact),
            new(0, 10, 1, CameraQuantizationMode.Exact),
            new(0, 10, 1, CameraQuantizationMode.Exact),
            new(8, 8, new(0, 7, 1), new(0, 7, 1), new(1, 8, 1), new(1, 8, 1)));
        return new VirtualCameraScenario("network-fixture", "1", 42, device,
            capabilities, new[] { image }, new[]
            {
                new VirtualCameraAcquisitionPlan(new[]
                {
                    new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                        VirtualCameraSignalKind.Frame, "frame")
                })
            });
    }

    private static CameraStationNetwork Station() => new("virtual-network",
        new CameraIpv4Configuration("192.168.10.1", 24));

    private static DateTimeOffset Utc() =>
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
}
