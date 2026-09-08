using System.Reflection;
using System.Text.Json;

using SharpInspect.Abstractions;
using SharpInspect.Cameras.Hikrobot;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;

using Xunit;

namespace SharpInspect.Cameras.Hikrobot.Tests;

public sealed class HikrobotDependencyTests
{
    [Fact]
    public void V121_D01_MissingRuntimeIsDiagnosedWithoutNativeLoading()
    {
        var path = Path.Combine(Path.GetTempPath(), "sharpinspect-hikrobot-missing-" +
            Guid.NewGuid().ToString("N"), "MvCameraControl.dll");

        var report = HikrobotDependencies.Inspect(new[] { path }, "X64", "Windows");

        Assert.Equal("HikrobotDependencyMissing", report.ReasonCode);
        Assert.Equal(CameraProviderAvailability.DependencyMissing, report.Availability);
        Assert.False(report.ProductionCompatible);
        Assert.Equal("NotVerified", report.DriverServiceStatus);
        Assert.Single(report.CandidatePaths);
        Assert.True(Path.IsPathRooted(report.CandidatePaths[0]));
        Assert.All(report.SdkComponentsPresent, item => Assert.False(item.Value));
    }

    [Fact]
    public void V121_D02_ParseableRuntimeRemainsUnqualified()
    {
        var directory = CreateRuntimeFixture();
        try
        {
            var report = HikrobotDependencies.Inspect(new[]
                {
                    Path.Combine(directory, "MvCameraControl.dll")
                }, "X64", "Windows");

            Assert.Equal("HikrobotDependencyUnqualified", report.ReasonCode);
            Assert.Equal(CameraProviderAvailability.Incompatible, report.Availability);
            Assert.False(report.ProductionCompatible);
            Assert.NotNull(report.DetectedFileVersion);
            Assert.NotNull(report.PeMachine);
            Assert.All(report.SdkComponentsPresent, item => Assert.True(item.Value));

        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task V121_D03_ProductionCompatibilityCatalogIsEmptyAndDiscoveryFailsClosed()
    {
        await using var provider = new HikrobotCameraProvider(new TestClock(), PoolOptions());
        var discovery = await provider.DiscoverAsync();
        var open = await provider.OpenAsync("hikrobot:missing");

        Assert.False(discovery.Succeeded);
        Assert.Equal("HikrobotCompatibilityCatalogEmpty", discovery.ReasonCode);
        Assert.False(open.Succeeded);
        Assert.Equal("HikrobotCompatibilityCatalogEmpty", open.ReasonCode);
        Assert.False(provider.DependencyReport.ProductionCompatible);
    }

    [Fact]
    public void V121_D04_PublicProviderHasNoSdkOrProductionOverrideSurface()
    {
        var constructors = typeof(HikrobotCameraProvider).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);
        var constructor = Assert.Single(constructors);
        Assert.Equal(new[] { typeof(IFrameAcquisitionClock), typeof(FrameBufferPoolOptions) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));

        Assert.DoesNotContain(typeof(HikrobotCameraProvider).GetProperties(
            BindingFlags.Public | BindingFlags.Instance), property =>
            property.Name.Contains("Override", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Mode", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("RuntimePath", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(typeof(HikrobotDependencyReport).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void V121_D05_InvalidPeIsReportedAsStableCorruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpinspect-hikrobot-invalid-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "MvCameraControl.dll");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x01 });

            var report = HikrobotDependencies.Inspect(new[] { path }, "X64", "Windows");

            Assert.Equal("HikrobotDependencyCorrupt", report.ReasonCode);
            Assert.Equal(CameraProviderAvailability.Faulted, report.Availability);
            Assert.Null(report.DetectedFileVersion);
            Assert.Null(report.PeMachine);
            Assert.False(report.ProductionCompatible);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void V121_D08_ManagedPeIsRejectedAsNativeDependency()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpinspect-hikrobot-managed-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "MvCameraControl.dll");
        try
        {
            File.Copy(typeof(HikrobotCameraProvider).Assembly.Location, path);
            MakePortableFixtureMachine(path, 0x8664);

            var report = HikrobotDependencies.Inspect(new[] { path }, "X64", "Windows");

            Assert.Equal("HikrobotDependencyNativeRequired", report.ReasonCode);
            Assert.Equal(CameraProviderAvailability.Faulted, report.Availability);
            Assert.NotNull(report.DetectedFileVersion);
            Assert.Equal("Amd64", report.PeMachine, ignoreCase: true);
            Assert.False(report.ProductionCompatible);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task V121_D06_DiscoveryRejectsDuplicatesAndMoreThanSixtyFourDevices()
    {
        var duplicateRuntime = new TestRuntime(() => new[]
        {
            new HikrobotSdkDescriptor("cam-1", "model", "firmware"),
            new HikrobotSdkDescriptor("cam-1", "model", "firmware")
        });
        await using (var provider = new HikrobotCameraProvider(duplicateRuntime,
            new TestClock(), PoolOptions()))
        {
            var duplicate = await provider.DiscoverAsync();
            Assert.False(duplicate.Succeeded);
            Assert.Equal("HikrobotDiscoveryDuplicateIdentity", duplicate.ReasonCode);
        }

        var boundedRuntime = new TestRuntime(() => Enumerable.Range(0, 65)
            .Select(index => new HikrobotSdkDescriptor("cam-" + index, "model", null))
            .ToArray());
        await using var bounded = new HikrobotCameraProvider(boundedRuntime,
            new TestClock(), PoolOptions());
        var tooMany = await bounded.DiscoverAsync();
        Assert.False(tooMany.Succeeded);
        Assert.Equal("HikrobotDiscoveryCapacityExceeded", tooMany.ReasonCode);
    }

    [Fact]
    public async Task V121_D07_CancellationDoesNotReleaseSdkBeforeActualOperationRetires()
    {
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var runtime = new TestRuntime(() =>
        {
            started.Set();
            release.Wait();
            return Array.Empty<HikrobotSdkDescriptor>();
        });
        await using var provider = new HikrobotCameraProvider(runtime,
            new TestClock(), PoolOptions());

        using var cancellation = new CancellationTokenSource();
        var discover = provider.DiscoverAsync(cancellation.Token).AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await discover);

        var disposing = provider.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposing.IsCompleted);
        release.Set();
        await disposing;
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public void V121_D09_Arm64PeIsReportedAsArchitectureMismatch()
    {
        var directory = CreateMachineFixture(0xAA64, includeComponents: true);
        try
        {
            var path = Path.Combine(directory, "MvCameraControl.dll");
            var report = HikrobotDependencies.Inspect(new[] { path }, "X64", "Windows");

            Assert.Equal("HikrobotDependencyArchitectureMismatch", report.ReasonCode);
            Assert.Equal("ARM64", report.PeMachine, ignoreCase: true);
            Assert.Equal(CameraProviderAvailability.Incompatible, report.Availability);
            Assert.False(report.ProductionCompatible);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void V121_D10_X86CandidateBeforeAmd64SelectsTheAmd64Runtime()
    {
        var x86Directory = CreateMachineFixture(0x014C, includeComponents: true);
        var amd64Directory = CreateMachineFixture(0x8664, includeComponents: true);
        try
        {
            var report = HikrobotDependencies.Inspect(new[]
            {
                Path.Combine(x86Directory, "MvCameraControl.dll"),
                Path.Combine(amd64Directory, "MvCameraControl.dll")
            }, "X64", "Windows");

            Assert.Equal("Amd64", report.PeMachine, ignoreCase: true);
            Assert.Equal("HikrobotDependencyUnqualified", report.ReasonCode);
            Assert.All(report.SdkComponentsPresent, item => Assert.True(item.Value));
        }
        finally
        {
            TryDeleteDirectory(x86Directory);
            TryDeleteDirectory(amd64Directory);
        }
    }

    [Fact]
    public void V121_D11_ComponentsFromAnotherInstallDirectoryAreNotCombined()
    {
        var firstDirectory = CreateMachineFixture(0x8664, includeComponents: false,
            components: new[] { "MVGigEVisionSDK.dll" });
        var secondDirectory = CreateMachineFixture(0x8664, includeComponents: false,
            components: new[] { "MvUsb3vTL.dll" });
        try
        {
            var report = HikrobotDependencies.Inspect(new[]
            {
                Path.Combine(firstDirectory, "MvCameraControl.dll"),
                Path.Combine(secondDirectory, "MvCameraControl.dll")
            }, "X64", "Windows");

            Assert.Equal("HikrobotDependencyComponentsMissing", report.ReasonCode);
            Assert.True(report.SdkComponentsPresent["MvCameraControl.dll"]);
            Assert.True(report.SdkComponentsPresent["MVGigEVisionSDK.dll"]);
            Assert.False(report.SdkComponentsPresent["MvUsb3vTL.dll"]);
        }
        finally
        {
            TryDeleteDirectory(firstDirectory);
            TryDeleteDirectory(secondDirectory);
        }
    }

    [Fact]
    public void V121_D12_ComponentFilesMustAlsoBeNativeAmd64()
    {
        var directory = CreateMixedComponentFixture();
        try
        {
            var report = HikrobotDependencies.Inspect(new[]
            {
                Path.Combine(directory, "MvCameraControl.dll")
            }, "X64", "Windows");

            Assert.Equal("HikrobotDependencyComponentsInvalid", report.ReasonCode);
            Assert.Equal(CameraProviderAvailability.Incompatible, report.Availability);
            Assert.All(report.SdkComponentsPresent, item => Assert.True(item.Value));
            Assert.False(report.ProductionCompatible);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void V121_D13_PublicDependencyJsonDoesNotExposeAbsoluteCandidatePaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpinspect-hikrobot-json-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "MvCameraControl.dll");
        try
        {
            var report = HikrobotDependencies.Inspect(new[] { path }, "X64", "Windows");
            var json = JsonSerializer.Serialize(report);

            Assert.Contains("CandidateLocationCount", json, StringComparison.Ordinal);
            Assert.DoesNotContain("CandidatePaths", json, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task V121_D14_OpenCancellationWaitsForLateOpenRetirement()
    {
        var openEntered = new ManualResetEventSlim();
        var releaseOpen = new ManualResetEventSlim();
        var lateDevice = new BlockingDisposeSdkDevice("GigE:late-open",
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8));
        var runtime = new TestRuntime(() => Array.Empty<HikrobotSdkDescriptor>(), _ =>
        {
            openEntered.Set();
            releaseOpen.Wait();
            return lateDevice;
        });
        var provider = new HikrobotCameraProvider(runtime,
            new TestClock(), PoolOptions());
        Task? disposing = null;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var open = provider.OpenAsync("GigE:late-open", cancellation.Token).AsTask();
            Assert.True(openEntered.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await open.WaitAsync(TimeSpan.FromSeconds(5)));

            var disposingTask = provider.DisposeAsync().AsTask();
            disposing = disposingTask;
            await Task.Delay(50);
            Assert.False(disposingTask.IsCompleted);
            releaseOpen.Set();
            Assert.True(lateDevice.DisposeEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(disposingTask.IsCompleted);
            lateDevice.DisposeRelease.Set();
            await disposingTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, runtime.OpenCount);
            Assert.Equal(1, lateDevice.DisposeCalls);
            Assert.Equal(1, runtime.DisposeCount);
        }
        finally
        {
            // Any failed assertion above must still release both gates before
            // waiting for the provider's late-open cleanup.
            releaseOpen.Set();
            lateDevice.DisposeRelease.Set();
            if (disposing is not null)
            {
                try { await disposing.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task V121_D15_DisconnectedHealthDoesNotReleaseSlotBeforeDeviceRetirement()
    {
        var firstDevice = new BlockingDisposeSdkDevice("GigE:first",
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8));
        var secondDevice = new BlockingDisposeSdkDevice("GigE:second",
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8));
        var runtime = new TestRuntime(() => new[]
        {
            new HikrobotSdkDescriptor("GigE:first", "model", "firmware"),
            new HikrobotSdkDescriptor("GigE:second", "model", "firmware")
        }, identity => identity == "GigE:first" ? firstDevice : secondDevice);
        var provider = new HikrobotCameraProvider(runtime,
            new TestClock(), PoolOptions());
        Task? firstDispose = null;
        Task? secondDispose = null;
        try
        {
            var first = await provider.OpenAsync("GigE:first").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(first.Succeeded);
            var firstDisposeTask = first.Device!.DisposeAsync().AsTask();
            firstDispose = firstDisposeTask;
            Assert.True(firstDevice.DisposeEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(CameraConnectionState.Disconnected,
                first.Device!.GetHealthSnapshot().Connection);

            var blocked = await provider.OpenAsync("GigE:second").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.Succeeded);
            Assert.Equal("HikrobotDeviceAlreadyOpen", blocked.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            firstDevice.DisposeRelease.Set();
            await firstDisposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            var second = await provider.OpenAsync("GigE:second").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(second.Succeeded);
            Assert.Equal(2, runtime.OpenCount);
            secondDevice.DisposeRelease.Set();
            var secondDisposeTask = second.Device!.DisposeAsync().AsTask();
            secondDispose = secondDisposeTask;
            await secondDisposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // Release every fake SDK gate before awaiting either device or
            // provider teardown, including when an assertion fails midway.
            firstDevice.DisposeRelease.Set();
            secondDevice.DisposeRelease.Set();
            if (firstDispose is not null)
            {
                try { await firstDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            if (secondDispose is not null)
            {
                try { await secondDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task V121_D16_CancelledOpenRetiresLateDeviceAndAllowsReopen()
    {
        var openEntered = new ManualResetEventSlim();
        var releaseOpen = new ManualResetEventSlim();
        var lateDevice = new BlockingDisposeSdkDevice("GigE:late-open-alive",
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8));
        var reopenedDevice = new BlockingDisposeSdkDevice("GigE:reopened",
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8));
        reopenedDevice.DisposeRelease.Set();
        var runtime = new TestRuntime(() => Array.Empty<HikrobotSdkDescriptor>(), identity =>
        {
            if (identity == "GigE:late-open-alive")
            {
                openEntered.Set();
                releaseOpen.Wait();
                return lateDevice;
            }
            return reopenedDevice;
        });
        var provider = new HikrobotCameraProvider(runtime,
            new TestClock(), PoolOptions());
        Task? reopenedDispose = null;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var lateOpen = provider.OpenAsync("GigE:late-open-alive", cancellation.Token).AsTask();
            Assert.True(openEntered.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await lateOpen.WaitAsync(TimeSpan.FromSeconds(5)));

            var whileOpen = await provider.OpenAsync("GigE:reopened").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(whileOpen.Succeeded);
            Assert.Equal("HikrobotOpenInProgress", whileOpen.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            releaseOpen.Set();
            Assert.True(lateDevice.DisposeEntered.Wait(TimeSpan.FromSeconds(5)));
            var whileDisposing = await provider.OpenAsync("GigE:reopened").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(whileDisposing.Succeeded);
            Assert.Equal("HikrobotDeviceAlreadyOpen", whileDisposing.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            lateDevice.DisposeRelease.Set();
            Assert.True(lateDevice.DisposeCompleted.Wait(TimeSpan.FromSeconds(5)));

            CameraOpenResult? reopened = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                reopened = await provider.OpenAsync("GigE:reopened").AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (reopened.Succeeded) break;
                await Task.Delay(10);
            }

            Assert.NotNull(reopened);
            Assert.True(reopened!.Succeeded);
            Assert.Equal(2, runtime.OpenCount);
            var disposeTask = reopened.Device!.DisposeAsync().AsTask();
            reopenedDispose = disposeTask;
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseOpen.Set();
            lateDevice.DisposeRelease.Set();
            reopenedDevice.DisposeRelease.Set();
            if (reopenedDispose is not null)
            {
                try { await reopenedDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task V121_D18_ProviderDisposeRetriesFailedDeviceRetirement()
    {
        var firstDevice = new BlockingDisposeSdkDevice("GigE:dispose-retry",
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8));
        firstDevice.DisposeFailuresRemaining = 1;
        firstDevice.DisposeRelease.Set();
        var runtime = new TestRuntime(() => Array.Empty<HikrobotSdkDescriptor>(),
            _ => firstDevice);
        var provider = new HikrobotCameraProvider(runtime,
            new TestClock(), PoolOptions());
        Task? firstProviderDispose = null;
        Task? secondProviderDispose = null;
        try
        {
            var opened = await provider.OpenAsync("GigE:dispose-retry").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(opened.Succeeded);

            firstProviderDispose = provider.DisposeAsync().AsTask();
            var firstFailure = await Assert.ThrowsAsync<HikrobotSdkException>(async () =>
                await firstProviderDispose.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("HikrobotDeviceRetirementIncomplete", firstFailure.ReasonCode);

            // The failed native Dispose still owns the managed device and the
            // provider's SDK semaphore. Runtime teardown must not be reported
            // until a later DisposeAsync retries the same physical owner.
            Assert.Equal(1, firstDevice.DisposeCalls);
            Assert.Equal(0, runtime.DisposeCount);
            var blocked = await provider.OpenAsync("GigE:dispose-retry").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.Succeeded);
            Assert.Equal("HikrobotProviderDisposed", blocked.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            secondProviderDispose = provider.DisposeAsync().AsTask();
            await secondProviderDispose.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, firstDevice.DisposeCalls);
            Assert.Equal(1, runtime.DisposeCount);
        }
        finally
        {
            firstDevice.DisposeRelease.Set();
            if (firstProviderDispose is not null)
            {
                try { await firstProviderDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            if (secondProviderDispose is not null)
            {
                try { await secondProviderDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task V121_D19_CancelledLateOpenKeepsFailedOwnerUntilProviderRetry()
    {
        var openEntered = new ManualResetEventSlim();
        var releaseOpen = new ManualResetEventSlim();
        var lateDevice = new BlockingDisposeSdkDevice("GigE:late-open-retry",
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8));
        lateDevice.DisposeFailuresRemaining = 1;
        lateDevice.DisposeRelease.Set();
        var runtime = new TestRuntime(() => Array.Empty<HikrobotSdkDescriptor>(), _ =>
        {
            openEntered.Set();
            releaseOpen.Wait();
            return lateDevice;
        });
        var provider = new HikrobotCameraProvider(runtime,
            new TestClock(), PoolOptions());
        Task? providerDispose = null;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var lateOpen = provider.OpenAsync("GigE:late-open-retry",
                cancellation.Token).AsTask();
            Assert.True(openEntered.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await lateOpen.WaitAsync(TimeSpan.FromSeconds(5)));

            releaseOpen.Set();
            Assert.True(lateDevice.DisposeEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(lateDevice.DisposeCompleted.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, lateDevice.DisposeCalls);

            // The first late cleanup failed. The exact late owner therefore
            // reserves the slot and a new generation cannot enter the SDK.
            var blocked = await provider.OpenAsync("GigE:next-generation").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.Succeeded);
            Assert.Equal("HikrobotDeviceAlreadyOpen", blocked.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            providerDispose = provider.DisposeAsync().AsTask();
            await providerDispose.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, lateDevice.DisposeCalls);
            Assert.Equal(1, runtime.DisposeCount);
        }
        finally
        {
            releaseOpen.Set();
            lateDevice.DisposeRelease.Set();
            if (providerDispose is not null)
            {
                try { await providerDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task V121_D20_RawOpenCleanupFailureRetainsOwnerForProviderDispose()
    {
        var rawDevice = new BlockingDisposeSdkDevice("GigE:raw-cleanup",
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8));
        rawDevice.ThrowDescriptor = true;
        rawDevice.DisposeFailuresRemaining = 1;
        rawDevice.DisposeRelease.Set();
        var runtime = new TestRuntime(() => Array.Empty<HikrobotSdkDescriptor>(),
            _ => rawDevice);
        var provider = new HikrobotCameraProvider(runtime,
            new TestClock(), PoolOptions());
        Task? providerDispose = null;
        try
        {
            var open = await provider.OpenAsync("GigE:raw-cleanup").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(open.Succeeded);
            Assert.Equal("HikrobotFixtureDescriptorFailed", open.ReasonCode);
            Assert.True(rawDevice.DisposeCompleted.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, rawDevice.DisposeCalls);

            // The first cleanup failed after Open had obtained a raw handle;
            // a later Open must remain blocked by that exact raw owner.
            var blocked = await provider.OpenAsync("GigE:next-generation").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.Succeeded);
            Assert.Equal("HikrobotDeviceAlreadyOpen", blocked.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            providerDispose = provider.DisposeAsync().AsTask();
            await providerDispose.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, rawDevice.DisposeCalls);
            Assert.Equal(1, runtime.DisposeCount);
        }
        finally
        {
            rawDevice.DisposeRelease.Set();
            if (providerDispose is not null)
            {
                try { await providerDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task V121_D21_ConsumedFrameKeepsPhysicalOwnerUntilSuccessfulDispose()
    {
        var firstSdk = new HikrobotTestSdk(
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8), "GigE:session-01")
        {
            TriggerBytes = new byte[] { 7 }
        };
        var secondSdk = new HikrobotTestSdk(
            HikrobotTestData.Capabilities(VisionPixelFormat.Mono8), "GigE:session-02")
        {
            TriggerBytes = new byte[] { 99 }
        };
        var runtime = new TestRuntime(() => Array.Empty<HikrobotSdkDescriptor>(), identity =>
            identity == "GigE:session-01" ? firstSdk : secondSdk);
        var clock = new HikrobotTestClock();
        var provider = new HikrobotCameraProvider(runtime, clock,
            HikrobotTestData.Pool());
        CameraAcquisitionService? firstService = null;
        CameraAcquisitionService? secondService = null;
        Task? firstDispose = null;
        Task? firstRetry = null;
        Task? providerDispose = null;
        try
        {
            var opened = await provider.OpenAsync("GigE:session-01").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(opened.Succeeded);
            var firstDevice = Assert.IsType<HikrobotCameraDevice>(opened.Device);
            var requested = HikrobotTestData.Requested(VisionPixelFormat.Mono8);
            var effective = firstDevice.Capabilities.ValidateConfiguration(requested)
                .Effective!;
            var configured = await firstDevice.ApplyConfigurationAsync(requested)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(configured.Succeeded, configured.ReasonCode);
            var started = await firstDevice.StartAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(started.Succeeded, started.ReasonCode);

            firstService = new CameraAcquisitionService(firstDevice, effective, clock,
                new CameraAcquisitionOptions(TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1)));
            var firstAttempt = await firstService.AcquireAsync(
                ExecutionKind.Qualification, "Primary").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(firstAttempt.Accepted);
            Assert.True(firstAttempt.Outcome!.Succeeded, firstAttempt.ReasonCode);
            var firstFrame = firstAttempt.Outcome.TakeFrame();
            Assert.NotNull(firstFrame);
            Assert.Equal(new byte[] { 7 }, firstFrame!.Frame.GetRowSpan(0).ToArray());
            firstFrame.Dispose();

            var oldCallback = (Action<HikrobotNativeFrame>)typeof(HikrobotTestSdk)
                .GetField("_lastCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(firstSdk)!;
            var blockedBeforeDispose = await provider.OpenAsync("GigE:session-02")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blockedBeforeDispose.Succeeded);
            Assert.Equal("HikrobotDeviceAlreadyOpen", blockedBeforeDispose.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            // Consuming the one frame does not transfer the physical handle to
            // another provider generation. Hold native Dispose behind a gate and
            // fail it once so both the pending and failed states reserve the slot.
            firstSdk.FailDispose = true;
            firstSdk.DisposeRelease.Reset();
            firstDispose = firstDevice.DisposeAsync().AsTask();
            Assert.True(firstSdk.DisposeEntered.Wait(TimeSpan.FromSeconds(5)));
            var blockedWhileDisposing = await provider.OpenAsync("GigE:session-02")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blockedWhileDisposing.Succeeded);
            Assert.Equal("HikrobotDeviceAlreadyOpen", blockedWhileDisposing.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            firstSdk.DisposeRelease.Set();
            await firstDispose.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(firstSdk.IsDisposed);
            var blockedAfterFailure = await provider.OpenAsync("GigE:session-02")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blockedAfterFailure.Succeeded);
            Assert.Equal("HikrobotDeviceAlreadyOpen", blockedAfterFailure.ReasonCode);
            Assert.Equal(1, runtime.OpenCount);

            firstSdk.FailDispose = false;
            firstRetry = firstDevice.DisposeAsync().AsTask();
            await firstRetry.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(firstSdk.IsDisposed);

            var reopened = await provider.OpenAsync("GigE:session-02").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(reopened.Succeeded);
            Assert.Equal(2, runtime.OpenCount);
            var secondDevice = Assert.IsType<HikrobotCameraDevice>(reopened.Device);
            var secondRequested = HikrobotTestData.Requested(VisionPixelFormat.Mono8);
            var secondEffective = secondDevice.Capabilities
                .ValidateConfiguration(secondRequested).Effective!;
            var secondConfigured = await secondDevice.ApplyConfigurationAsync(secondRequested)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(secondConfigured.Succeeded, secondConfigured.ReasonCode);
            var secondStarted = await secondDevice.StartAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(secondStarted.Succeeded, secondStarted.ReasonCode);

            secondSdk.EmitOnTrigger = false;
            secondService = new CameraAcquisitionService(secondDevice, secondEffective,
                clock, new CameraAcquisitionOptions(TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1)));
            var secondAcquisition = secondService.AcquireAsync(
                ExecutionKind.Qualification, "Primary").AsTask();
            Assert.True(secondSdk.TriggerEntered.Wait(TimeSpan.FromSeconds(5)));
            // Explicitly invoke the retained old delegate while the new request
            // is Busy; the fake's normal Dispose has already cleared its fields.
            oldCallback(default);
            Assert.False(secondAcquisition.IsCompleted);
            secondSdk.Emit(HikrobotNativePixelFormat.Mono8, 1, 1, 1,
                new byte[] { 99 }, 10, 10);
            var secondAttempt = await secondAcquisition.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(secondAttempt.Accepted);
            Assert.True(secondAttempt.Outcome!.Succeeded, secondAttempt.ReasonCode);
            var secondFrame = secondAttempt.Outcome.TakeFrame();
            Assert.NotNull(secondFrame);
            Assert.Equal(new byte[] { 99 }, secondFrame!.Frame.GetRowSpan(0).ToArray());
            secondFrame.Dispose();
            Assert.Equal(1, firstSdk.TriggerCalls);
            Assert.Equal(1, secondSdk.TriggerCalls);
        }
        finally
        {
            firstSdk.FailDispose = false;
            firstSdk.StopRelease.Set();
            firstSdk.DisposeRelease.Set();
            secondSdk.StopRelease.Set();
            secondSdk.DisposeRelease.Set();
            if (firstDispose is not null)
            {
                try { await firstDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            if (firstRetry is not null)
            {
                try { await firstRetry.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            if (firstService is not null)
            {
                try { await firstService.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            if (secondService is not null)
            {
                try { await secondService.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            if (providerDispose is not null)
            {
                try { await providerDispose.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            try
            {
                providerDispose = provider.DisposeAsync().AsTask();
                await providerDispose.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task V121_D17_DiscoveryCoalescesConcurrentAndCancelledWaiters()
    {
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var runtime = new TestRuntime(() =>
        {
            started.Set();
            release.Wait();
            return new[] { new HikrobotSdkDescriptor("cam-shared", "model", "firmware") };
        });
        var provider = new HikrobotCameraProvider(runtime,
            new TestClock(), PoolOptions());
        try
        {
            var first = provider.DiscoverAsync().AsTask();
            var second = provider.DiscoverAsync().AsTask();
            using var cancellation = new CancellationTokenSource();
            var cancelled = provider.DiscoverAsync(cancellation.Token).AsTask();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, runtime.DiscoverCount);

            release.Set();
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
            var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(firstResult, secondResult);
            Assert.True(firstResult.Succeeded);
            Assert.Equal(1, runtime.DiscoverCount);

            var fresh = await provider.DiscoverAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fresh.Succeeded);
            Assert.Equal(2, runtime.DiscoverCount);
        }
        finally
        {
            release.Set();
            try
            {
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) { }
        }
    }

    private static string CreateRuntimeFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpinspect-hikrobot-runtime-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = typeof(HikrobotCameraProvider).Assembly.Location;
        foreach (var component in new[] { "MvCameraControl.dll", "MVGigEVisionSDK.dll",
                     "MvUsb3vTL.dll" })
        {
            var destination = Path.Combine(directory, component);
            File.Copy(source, destination);
            MakePortableFixtureNative(destination, 0x8664);
        }
        return directory;
    }

    private static string CreateMachineFixture(ushort machine, bool includeComponents,
        IEnumerable<string>? components = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpinspect-hikrobot-machine-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = typeof(HikrobotCameraProvider).Assembly.Location;
        var main = Path.Combine(directory, "MvCameraControl.dll");
        File.Copy(source, main);
        MakePortableFixtureNative(main, machine);

        var names = includeComponents
            ? new[] { "MVGigEVisionSDK.dll", "MvUsb3vTL.dll" }
            : components ?? Array.Empty<string>();
        foreach (var component in names)
        {
            var destination = Path.Combine(directory, component);
            File.Copy(source, destination);
            MakePortableFixtureNative(destination, machine);
        }
        return directory;
    }

    private static string CreateMixedComponentFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpinspect-hikrobot-mixed-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = typeof(HikrobotCameraProvider).Assembly.Location;

        var main = Path.Combine(directory, "MvCameraControl.dll");
        File.Copy(source, main);
        MakePortableFixtureNative(main, 0x8664);

        var managed = Path.Combine(directory, "MVGigEVisionSDK.dll");
        File.Copy(source, managed);
        MakePortableFixtureMachine(managed, 0x8664);

        var x86 = Path.Combine(directory, "MvUsb3vTL.dll");
        File.Copy(source, x86);
        MakePortableFixtureNative(x86, 0x014C);
        return directory;
    }

    private static void MakePortableFixtureNative(string path, ushort machine)
    {
        MakePortableFixtureMachine(path, machine);
        ClearComDescriptorDirectory(path);
    }

    private static void MakePortableFixtureMachine(string path, ushort machine)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8,
            leaveOpen: true);
        stream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        stream.Position = peOffset + 4;
        stream.WriteByte((byte)(machine & 0xFF));
        stream.WriteByte((byte)(machine >> 8));
    }

    private static void ClearComDescriptorDirectory(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8,
            leaveOpen: true);

        stream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        stream.Position = peOffset + 24;
        var optionalHeaderMagic = reader.ReadUInt16();
        var dataDirectoryOffset = optionalHeaderMagic switch
        {
            0x10B => 96,
            0x20B => 112,
            _ => throw new InvalidDataException("FixtureOptionalHeaderInvalid")
        };

        // IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR is directory index 14.  A zero
        // RVA/size makes PEReader treat the file as a native image while the
        // assembly's file-version resource remains available for inspection.
        stream.Position = peOffset + 24 + dataDirectoryOffset + (14 * 8);
        stream.Write(new byte[8]);
    }

    private static FrameBufferPoolOptions PoolOptions() =>
        new(1, 1024, TimeSpan.FromSeconds(1));

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class TestClock : IFrameAcquisitionClock
    {
        public long Frequency => 1_000_000;
        public FrameTimePoint GetTimePoint() => new(DateTimeOffset.UtcNow, 0);
        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
            Action callback) => throw new NotSupportedException();
    }

    private sealed class TestRuntime : IHikrobotSdkRuntime
    {
        private readonly Func<IReadOnlyList<HikrobotSdkDescriptor>> _discover;
        private readonly Func<string, IHikrobotSdkDevice>? _open;
        private int _disposeCount;
        private int _discoverCount;
        private int _openCount;

        internal TestRuntime(Func<IReadOnlyList<HikrobotSdkDescriptor>> discover,
            Func<string, IHikrobotSdkDevice>? open = null)
        {
            _discover = discover;
            _open = open;
        }

        public string RuntimeVersion => "4.8.1.2";
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int DiscoverCount => Volatile.Read(ref _discoverCount);
        public int OpenCount => Volatile.Read(ref _openCount);
        public IReadOnlyList<HikrobotSdkDescriptor> Discover()
        {
            Interlocked.Increment(ref _discoverCount);
            return _discover();
        }
        public IHikrobotSdkDevice Open(string stableDeviceIdentity)
        {
            Interlocked.Increment(ref _openCount);
            return _open?.Invoke(stableDeviceIdentity) ?? throw new NotSupportedException();
        }
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class BlockingDisposeSdkDevice : IHikrobotSdkDevice
    {
        private readonly CameraCapabilities _capabilities;
        private readonly HikrobotSdkDescriptor _descriptor;
        private int _disposeCalls;
        private int _disposeFailuresRemaining;

        internal BlockingDisposeSdkDevice(string stableIdentity,
            CameraCapabilities capabilities)
        {
            _descriptor = new HikrobotSdkDescriptor(stableIdentity, "model", "firmware");
            _capabilities = capabilities;
        }

        internal ManualResetEventSlim DisposeEntered { get; } = new();
        internal ManualResetEventSlim DisposeCompleted { get; } = new();
        internal ManualResetEventSlim DisposeRelease { get; } = new();
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal int DisposeFailuresRemaining
        {
            get => Volatile.Read(ref _disposeFailuresRemaining);
            set => Volatile.Write(ref _disposeFailuresRemaining, value);
        }
        internal bool ThrowDescriptor { get; set; }
        public HikrobotSdkDescriptor Descriptor => ThrowDescriptor
            ? throw new HikrobotSdkException("HikrobotFixtureDescriptorFailed")
            : _descriptor;
        public CameraCapabilities Capabilities => _capabilities;

        public EffectiveCameraConfiguration Apply(RequestedCameraConfiguration requested) =>
            _capabilities.ValidateConfiguration(requested).Effective ??
            throw new InvalidOperationException("FixtureConfigurationInvalid");

        public void Start(Action<HikrobotNativeFrame> callback) { }
        public void TriggerSoftware() { }
        public void Stop() { }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeEntered.Set();
            DisposeRelease.Wait();
            DisposeCompleted.Set();
            if (Interlocked.Decrement(ref _disposeFailuresRemaining) >= 0)
                throw new HikrobotSdkException("HikrobotFixtureDisposeFailed");
        }
    }
}
