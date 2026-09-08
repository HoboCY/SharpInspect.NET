using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime.Cameras;

namespace SharpInspect.SampleHost;

/// <summary>
/// Independent public-interface consumer for the bounded controlled-acquisition
/// capability.  Every scenario uses Qualification identities and a virtual clock;
/// this sample does not open a physical device or assert production readiness.
/// </summary>
public static class CameraAcquisitionDemo
{
    private const string EvidenceVersion = "sharpinspect-controlled-camera-consumer-v1";
    private const string ScenarioVersion = "1";
    private const string LogicalRole = "Primary";
    private const int AcquisitionTimeoutMilliseconds = 10;
    private static readonly TimeSpan StateWaitTimeout = TimeSpan.FromSeconds(5);
    private const uint Seed = 0x1180_C0DE;
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static int Run(string artifactDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(artifactDirectory))
                throw new ArgumentException("CameraAcquisitionArtifactDirectoryInvalid");

            RunAsync(Path.GetFullPath(artifactDirectory))
                .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Console.WriteLine("V118-N01 camera-acquisition-consumer PASS software=true hardware=true " +
                "deadlineBoundary=true timeoutUnknown=true identityIsolation=true leasesReturned=true productionReady=false");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("V118-N01 camera-acquisition-consumer FAIL reason=CameraAcquisitionConsumerCheckFailed");
            return 1;
        }
    }

    private static async Task RunAsync(string artifactDirectory)
    {
        Directory.CreateDirectory(artifactDirectory);
        var collector = new EvidenceCollector();

        await RunSoftwareScenarioAsync(collector).ConfigureAwait(false);
        await RunHardwareScenarioAsync(collector).ConfigureAwait(false);
        await RunNoPulseScenarioAsync(collector).ConfigureAwait(false);
        await RunCancellationIsolationScenarioAsync(collector).ConfigureAwait(false);

        Require(collector.SuccessfulFrames == 4);
        Require(collector.AcceptedAttempts == 6);
        Require(collector.AllLeasesReturned);
        Require(collector.OutstandingLeases == 0);
        Require(collector.InfrastructureFailures == 0);
        Require(collector.ProtocolFacts.Any(item =>
            item.Kind == CameraProtocolViolationKind.LateFrame.ToString()));

        var consumerSha256 = ReadConsumerSha256();
        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            WriteIndented = true
        };

        var evidence = new ConsumerEvidence(
            EvidenceVersion,
            consumerSha256,
            FormatUtc(InitialUtc),
            collector.Scenarios,
            collector.Operations,
            collector.ProtocolFacts,
            collector.SuccessfulFrames,
            collector.AcceptedAttempts,
            collector.AllLeasesReturned,
            collector.OutstandingLeases,
            collector.InfrastructureFailures);
        var replay = new ReplayEvidence(
            EvidenceVersion,
            ScenarioVersion,
            FormatUtc(InitialUtc),
            VirtualCameraProvider.ProviderId,
            VirtualCameraProvider.ProviderVersion,
            VirtualCameraProvider.AdapterPackageId,
            VirtualCameraProvider.AdapterPackageVersion,
            collector.Scenarios.Select(item => item.ToReplay()).ToArray(),
            collector.Operations.Select(item => item.ToReplay()).ToArray(),
            collector.ProtocolFacts.Select(item => item.ToReplay(collector.Operations)).ToArray(),
            collector.SuccessfulFrames,
            collector.AcceptedAttempts,
            collector.AllLeasesReturned,
            collector.OutstandingLeases,
            collector.InfrastructureFailures);
        var summary = new SummaryEvidence(
            "Pass",
            false,
            "NotRun",
            "NotRun",
            "NotRun",
            "NotRun",
            "NotRun",
            "Pass",
            consumerSha256,
            collector.Scenarios.Count,
            collector.AcceptedAttempts,
            collector.SuccessfulFrames,
            collector.AllLeasesReturned,
            collector.OutstandingLeases,
            collector.InfrastructureFailures);

        WriteJson(Path.Combine(artifactDirectory, "evidence.json"), evidence, options);
        WriteJson(Path.Combine(artifactDirectory, "summary.json"), summary, options);
        WriteJson(Path.Combine(artifactDirectory, "replay-evidence.json"), replay, options);
    }

    private static async Task RunSoftwareScenarioAsync(EvidenceCollector collector)
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var image = VirtualCameraImage.CreateSynthetic(
            "software-frame", 1, 1, VisionPixelFormat.Mono8, null, Seed + 1);
        var scenario = CreateScenario(
            "V118-software-boundary", "software-camera", image,
            new[]
            {
                new VirtualCameraAcquisitionPlan(new[]
                {
                    FrameSignal(image.Id, 1)
                }),
                new VirtualCameraAcquisitionPlan(new[]
                {
                    FrameSignal(image.Id, AcquisitionTimeoutMilliseconds)
                })
            });
        collector.AddScenario(scenario, "SoftwareTrigger", image);

        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        var (device, effective) = await OpenAndConfigureAsync(provider,
            ProductionAcquisitionMode.SoftwareTrigger).ConfigureAwait(false);
        await using var service = new CameraAcquisitionService(device, effective, clock,
            AcquisitionOptions());

        var first = await AcquireSoftwareAsync(service, clock, collector,
            "V118-N01-S01").ConfigureAwait(false);
        Require(first.Attempt.Accepted && first.Attempt.Outcome is { Succeeded: true });
        Require(first.BusyObserved && first.Attempt.Outcome!.ExecutionStatus == ExecutionStatus.Success);
        ConsumeFrame(first.Attempt.Outcome!, first.CorrelationOrdinal, "V118-N01-S01", collector);
        await WaitUntilQuiescentAsync(service).ConfigureAwait(false);

        var boundaryTask = service.AcquireAsync(ExecutionKind.Qualification, LogicalRole).AsTask();
        var boundaryBusy = await WaitForBusyAsync(service, boundaryTask).ConfigureAwait(false);
        Require(boundaryBusy.Start is not null);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(AcquisitionTimeoutMilliseconds));
        var boundary = await boundaryTask.ConfigureAwait(false);
        Require(boundary.Accepted && boundary.Outcome is { Succeeded: true });
        Require(boundary.Outcome!.ExecutionStatus == ExecutionStatus.Success &&
            boundary.Outcome.Decision == InspectionDecision.Unknown);
        var boundaryOrdinal = collector.NextCorrelationOrdinal;
        collector.AddOperation(boundaryOrdinal, "V118-N01-S02", boundary,
            busyObserved: true, pulseAfterBusy: false);
        ConsumeFrame(boundary.Outcome!, boundaryOrdinal, "V118-N01-S02", collector);
        await WaitUntilQuiescentAsync(service).ConfigureAwait(false);

        await service.DisposeAsync().ConfigureAwait(false);
        var diagnostics = provider.GetDiagnostics();
        Require(diagnostics.InfrastructureFailures == 0 &&
            diagnostics.Devices.All(item => item.OutstandingLeases == 0));
        collector.AddDiagnostics(diagnostics);
    }

    private static async Task RunHardwareScenarioAsync(EvidenceCollector collector)
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var image = VirtualCameraImage.CreateSynthetic(
            "hardware-frame", 1, 1, VisionPixelFormat.Mono8, null, Seed + 2);
        var scenario = CreateScenario(
            "V118-hardware-pulse", "hardware-camera", image,
            new[]
            {
                new VirtualCameraAcquisitionPlan(new[]
                {
                    FrameSignal(image.Id, 1)
                })
            });
        collector.AddScenario(scenario, "HardwareTrigger", image);

        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        var (device, effective) = await OpenAndConfigureAsync(provider,
            ProductionAcquisitionMode.HardwareTrigger).ConfigureAwait(false);
        await using var service = new CameraAcquisitionService(device, effective, clock,
            AcquisitionOptions());

        var acquireTask = service.AcquireAsync(ExecutionKind.Qualification, LogicalRole).AsTask();
        var busy = await WaitForBusyAsync(service, acquireTask).ConfigureAwait(false);
        Require(busy.IsBusy && busy.Start is not null);
        var pulse = provider.PulseHardwareTrigger(scenario.StableDeviceIdentity, busy.Correlation);
        Require(pulse.Succeeded && pulse.ReasonCode == "VirtualCameraPulseAccepted");
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var attempt = await acquireTask.ConfigureAwait(false);
        Require(attempt.Accepted && attempt.Outcome is { Succeeded: true });
        Require(attempt.Outcome!.ExecutionStatus == ExecutionStatus.Success);
        var ordinal = collector.NextCorrelationOrdinal;
        collector.AddOperation(ordinal, "V118-N01-H01", attempt,
            busyObserved: true, pulseAfterBusy: true);
        ConsumeFrame(attempt.Outcome!, ordinal, "V118-N01-H01", collector);
        await WaitUntilQuiescentAsync(service).ConfigureAwait(false);

        await service.DisposeAsync().ConfigureAwait(false);
        var diagnostics = provider.GetDiagnostics();
        Require(diagnostics.InfrastructureFailures == 0 &&
            diagnostics.Devices.All(item => item.OutstandingLeases == 0));
        collector.AddDiagnostics(diagnostics);
    }

    private static async Task RunNoPulseScenarioAsync(EvidenceCollector collector)
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var image = VirtualCameraImage.CreateSynthetic(
            "hardware-timeout", 1, 1, VisionPixelFormat.Mono8, null, Seed + 3);
        var scenario = CreateScenario(
            "V118-hardware-no-pulse", "hardware-timeout-camera", image,
            // The frame is deliberately one tick after the adapter deadline.
            // This leaves a real late-frame protocol observation after the
            // no-pulse attempt has already become Timeout + Unknown.
            new[] { new VirtualCameraAcquisitionPlan(new[]
            {
                FrameSignal(image.Id, AcquisitionTimeoutMilliseconds + 1)
            }) });
        collector.AddScenario(scenario, "HardwareTriggerNoPulse", image);

        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        var (device, effective) = await OpenAndConfigureAsync(provider,
            ProductionAcquisitionMode.HardwareTrigger).ConfigureAwait(false);
        await using var service = new CameraAcquisitionService(device, effective, clock,
            AcquisitionOptions());

        var acquireTask = service.AcquireAsync(ExecutionKind.Qualification, LogicalRole).AsTask();
        var busy = await WaitForBusyAsync(service, acquireTask).ConfigureAwait(false);
        Require(busy.IsBusy && busy.Start is not null);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(AcquisitionTimeoutMilliseconds));
        var attempt = await acquireTask.ConfigureAwait(false);
        Require(attempt.Accepted && attempt.Outcome is { Succeeded: false, Lease: null });
        Require(attempt.Outcome!.FailureKind == CameraAcquisitionFailureKind.TimedOut &&
            attempt.Outcome.ExecutionStatus == ExecutionStatus.Timeout &&
            attempt.Outcome.Decision == InspectionDecision.Unknown &&
            (attempt.Outcome.ReasonCode == "CameraAcquisitionTimeout" ||
             attempt.Outcome.ReasonCode == "VirtualCameraAcquisitionTimeout"));
        var ordinal = collector.NextCorrelationOrdinal;
        collector.AddOperation(ordinal, "V118-N01-H02-no-pulse-timeout", attempt,
            busyObserved: true, pulseAfterBusy: false);
        await WaitUntilQuiescentAsync(service).ConfigureAwait(false);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        await service.RefreshProtocolObservationsAsync().ConfigureAwait(false);
        var protocol = service.ReadProtocolObservations(0, 64);
        Require(protocol.Observations.Any(item => item.Kind == CameraProtocolViolationKind.LateFrame));
        collector.AddProtocol(protocol.Observations);

        await service.DisposeAsync().ConfigureAwait(false);
        var diagnostics = provider.GetDiagnostics();
        Require(diagnostics.InfrastructureFailures == 0 &&
            diagnostics.Devices.All(item => item.OutstandingLeases == 0));
        collector.AddDiagnostics(diagnostics);
    }

    private static async Task RunCancellationIsolationScenarioAsync(EvidenceCollector collector)
    {
        using var clock = new VirtualCameraClock(InitialUtc);
        var image = VirtualCameraImage.CreateSynthetic(
            "cancel-isolation", 1, 1, VisionPixelFormat.Mono8, null, Seed + 4);
        var scenario = CreateScenario(
            "V118-cancel-late-isolation", "cancel-late-camera", image,
            new[]
            {
                new VirtualCameraAcquisitionPlan(new[]
                {
                    FrameSignal(image.Id, 5)
                }),
                new VirtualCameraAcquisitionPlan(new[]
                {
                    FrameSignal(image.Id, 6)
                })
            });
        collector.AddScenario(scenario, "CancellationLateIsolation", image);

        await using var provider = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        var (device, effective) = await OpenAndConfigureAsync(provider,
            ProductionAcquisitionMode.SoftwareTrigger).ConfigureAwait(false);
        await using var service = new CameraAcquisitionService(device, effective, clock,
            AcquisitionOptions());

        using var cancellation = new CancellationTokenSource();
        var firstTask = service.AcquireAsync(ExecutionKind.Qualification, LogicalRole,
            cancellation.Token).AsTask();
        var firstBusy = await WaitForBusyAsync(service, firstTask).ConfigureAwait(false);
        Require(firstBusy.IsBusy && firstBusy.Start is not null);
        var first = firstTask;
        cancellation.Cancel();
        var cancelled = await first.ConfigureAwait(false);
        Require(cancelled.Accepted && cancelled.Outcome is { Succeeded: false, Lease: null });
        Require(cancelled.Outcome!.FailureKind == CameraAcquisitionFailureKind.Cancelled &&
            cancelled.Outcome.ExecutionStatus == ExecutionStatus.Cancelled &&
            cancelled.Outcome.Decision == InspectionDecision.Unknown);
        var firstOrdinal = collector.NextCorrelationOrdinal;
        collector.AddOperation(firstOrdinal, "V118-N01-C01-cancel", cancelled,
            busyObserved: true, pulseAfterBusy: false);

        await WaitUntilQuiescentAsync(service).ConfigureAwait(false);
        var secondTask = service.AcquireAsync(ExecutionKind.Qualification, LogicalRole).AsTask();
        var secondBusy = await WaitForBusyAsync(service, secondTask).ConfigureAwait(false);
        Require(secondBusy.IsBusy && secondBusy.Start is not null);
        // A's retired callback is observed while B owns the pending request.
        // Its old identity remains protocol evidence and cannot complete B.
        clock.AdvanceBy(TimeSpan.FromMilliseconds(5));
        Require(!secondTask.IsCompleted && service.Busy?.Correlation == secondBusy.Correlation);
        await service.RefreshProtocolObservationsAsync().ConfigureAwait(false);
        var protocol = service.ReadProtocolObservations(0, 64);
        Require(protocol.Observations.Any(item => item.Kind == CameraProtocolViolationKind.LateFrame &&
            item.Correlation == cancelled.Correlation));
        Require(!protocol.Observations.Any(item => item.Kind == CameraProtocolViolationKind.EarlyFrame));
        collector.AddProtocol(protocol.Observations);

        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var second = await secondTask.ConfigureAwait(false);
        Require(second.Accepted && second.Outcome is { Succeeded: true });
        Require(cancelled.Correlation is not null && second.Correlation is not null &&
            cancelled.Correlation != second.Correlation);
        var secondOrdinal = collector.NextCorrelationOrdinal;
        collector.AddOperation(secondOrdinal, "V118-N01-C02-next", second,
            busyObserved: true, pulseAfterBusy: false);
        ConsumeFrame(second.Outcome!, secondOrdinal, "V118-N01-C02-next", collector);
        await WaitUntilQuiescentAsync(service).ConfigureAwait(false);

        await service.DisposeAsync().ConfigureAwait(false);
        var diagnostics = provider.GetDiagnostics();
        Require(diagnostics.InfrastructureFailures == 0 &&
            diagnostics.Devices.All(item => item.OutstandingLeases == 0));
        collector.AddDiagnostics(diagnostics);
    }

    private static async Task<(IControlledCameraDevice Device, EffectiveCameraConfiguration Effective)>
        OpenAndConfigureAsync(VirtualCameraProvider provider,
            ProductionAcquisitionMode mode)
    {
        var discovery = await provider.DiscoverAsync().ConfigureAwait(false);
        Require(discovery.Succeeded && discovery.Devices.Count == 1);
        var opened = await provider.OpenAsync(discovery.Devices[0].StableDeviceIdentity)
            .ConfigureAwait(false);
        Require(opened.Succeeded && opened.Device is IControlledCameraDevice);
        var device = (IControlledCameraDevice)opened.Device!;
        var requested = Configuration(mode);
        var configured = await device.ApplyConfigurationAsync(requested).ConfigureAwait(false);
        Require(configured.Succeeded && configured.Effective is not null);
        Require((await device.StartAsync().ConfigureAwait(false)).Succeeded);
        return (device, configured.Effective!);
    }

    private static async Task<BusyOperation> AcquireSoftwareAsync(
        CameraAcquisitionService service, VirtualCameraClock clock,
        EvidenceCollector collector, string caseId)
    {
        var task = service.AcquireAsync(ExecutionKind.Qualification, LogicalRole).AsTask();
        var busy = await WaitForBusyAsync(service, task).ConfigureAwait(false);
        Require(busy.IsBusy && busy.Start is not null);
        // The software scenario's first signal is one millisecond after Busy.
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        var attempt = await task.ConfigureAwait(false);
        Require(attempt.Accepted && attempt.Correlation is not null && attempt.Outcome is not null);
        var ordinal = collector.NextCorrelationOrdinal;
        collector.AddOperation(ordinal, caseId, attempt, busyObserved: true, pulseAfterBusy: false);
        return new BusyOperation(attempt, ordinal, true);
    }

    private static async Task<CameraAcquisitionBusySnapshot> WaitForBusyAsync(
        CameraAcquisitionService service, Task<CameraAcquisitionAttempt> attemptTask)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StateWaitTimeout)
        {
            if (service.Busy is { IsBusy: true, Start: not null } busy)
                return busy;
            if (attemptTask.IsCompleted)
            {
                var attempt = await attemptTask.ConfigureAwait(false);
                throw new InvalidOperationException(attempt.ReasonCode);
            }
            await Task.Delay(1).ConfigureAwait(false);
        }
        throw new TimeoutException("CameraAcquisitionBusyNotObserved");
    }

    private static async Task WaitUntilQuiescentAsync(CameraAcquisitionService service)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StateWaitTimeout)
        {
            if (service.Busy is null) return;
            await Task.Delay(1).ConfigureAwait(false);
        }
        throw new TimeoutException("CameraAcquisitionCleanupNotObserved");
    }

    private static VirtualCameraScenario CreateScenario(string id, string stableDeviceIdentity,
        VirtualCameraImage image, IEnumerable<VirtualCameraAcquisitionPlan> acquisitions)
    {
        return new VirtualCameraScenario(id, ScenarioVersion, Seed, stableDeviceIdentity,
            Capabilities(), new[] { image }, acquisitions,
            new[] { new VirtualCameraConfigurationPlan(
                VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero) },
            new[] { VirtualCameraOpenOutcome.Success });
    }

    private static VirtualCameraSignal FrameSignal(string imageId, int milliseconds) =>
        new(TimeSpan.FromMilliseconds(milliseconds), VirtualCameraSignalKind.Frame, imageId);

    private static RequestedCameraConfiguration Configuration(ProductionAcquisitionMode mode) =>
        new(mode, 20, 0, new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8,
            null, AcquisitionTimeoutMilliseconds, 0, null);

    private static CameraCapabilities Capabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger, ProductionAcquisitionMode.HardwareTrigger },
        new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
        new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
        new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
            new(1, 1, 1), new(1, 1, 1)));

    private static CameraAcquisitionOptions AcquisitionOptions() =>
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), protocolCapacity: 64);

    private static void ConsumeFrame(CameraAcquisitionOutcome outcome, int ordinal,
        string caseId, EvidenceCollector collector)
    {
        var lease = outcome.TakeFrame() ??
            throw new InvalidOperationException("CameraAcquisitionFrameMissing");
        Require(!lease.IsReturned);
        var frame = lease.Frame;
        var provenance = lease.Provenance;
        Require(frame.Correlation.Kind == ExecutionKind.Qualification);
        Require(frame.Correlation == outcome.Correlation && provenance.Correlation == outcome.Correlation);
        Require(frame.LogicalCameraRole == LogicalRole && frame.Width == 1 && frame.Height == 1 &&
            frame.PixelFormat == VisionPixelFormat.Mono8 && frame.ValidBits is null);
        Require(provenance.Milestones.NormalizedFrameReady is not null);
        var pixelHash = HashRows(frame);
        var ready = provenance.Milestones.NormalizedFrameReady!;
        var counter = provenance.FrameCounter;
        lease.Dispose();
        Require(lease.IsReturned);
        collector.AddFrame(new FrameEvidence(
            caseId, ordinal, outcome.Correlation.Kind.ToString(),
            frame.Correlation.Value.ToString("D"),
            pixelHash, frame.Width, frame.Height, frame.StrideBytes, frame.PixelFormat.ToString(),
            frame.ValidBits, FormatUtc(frame.HostCaptureUtc), ready.MonotonicTimestamp,
            counter, true, true));
    }

    private static string HashRows(VisionFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var row = 0; row < frame.Height; row++)
            hash.AppendData(frame.GetRowSpan(row));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ReadConsumerSha256()
    {
        var location = typeof(CameraAcquisitionDemo).Assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new InvalidOperationException("CameraAcquisitionConsumerAssemblyUnavailable");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location)));
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(value, options);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("CameraAcquisitionConsumerCheckFailed");
    }

    private sealed record BusyOperation(CameraAcquisitionAttempt Attempt, int CorrelationOrdinal,
        bool BusyObserved);

    private sealed record ScenarioEvidence(string Id, string Version, uint Seed,
        string StableDeviceIdentity, string Mode, string ContentHash, string ImageId,
        string ImageContentHash)
    {
        internal ReplayScenario ToReplay() => new(Id, Version, Seed, StableDeviceIdentity,
            Mode, ContentHash, ImageId, ImageContentHash);
    }

    private sealed record FrameEvidence(string CaseId, int CorrelationOrdinal,
        string CorrelationKind, string CorrelationId, string PixelDataHash, int Width,
        int Height, int StrideBytes, string PixelFormat, int? ValidBits,
        string HostCaptureUtc, long NormalizedFrameTimestamp, ulong? FrameCounter,
        bool CorrelationMatched, bool LeaseReturned);

    private sealed record OperationEvidence(string CaseId, int CorrelationOrdinal,
        string CorrelationKind, string CorrelationId, bool Accepted, bool Succeeded,
        string ReasonCode, string? FailureKind, string ExecutionStatus,
        string Decision, bool BusyObserved, bool PulseAfterBusy, IReadOnlyList<FrameEvidence> Frames)
    {
        internal ReplayOperation ToReplay() => new(CaseId, CorrelationOrdinal,
            CorrelationKind, Accepted, Succeeded, ReasonCode, FailureKind,
            ExecutionStatus, Decision, BusyObserved, PulseAfterBusy,
            Frames.Select(frame => new ReplayFrame(frame.CaseId, frame.CorrelationOrdinal,
                frame.CorrelationKind, frame.PixelDataHash, frame.Width, frame.Height,
                frame.StrideBytes, frame.PixelFormat, frame.ValidBits,
                frame.HostCaptureUtc, frame.NormalizedFrameTimestamp, frame.FrameCounter,
                frame.CorrelationMatched, frame.LeaseReturned)).ToArray());
    }

    private sealed record ProtocolEvidence(string Kind, string ReasonCode,
        string? CorrelationKind, int? CorrelationOrdinal, long MonotonicTimestamp)
    {
        internal ReplayProtocol ToReplay(IReadOnlyList<OperationEvidence> operations) =>
            new(Kind, ReasonCode, CorrelationKind, CorrelationOrdinal, MonotonicTimestamp);
    }

    private sealed record ConsumerEvidence(string ContractVersion, string ConsumerSha256,
        string InitialUtc, IReadOnlyList<ScenarioEvidence> Scenarios,
        IReadOnlyList<OperationEvidence> Operations,
        IReadOnlyList<ProtocolEvidence> ProtocolFacts, int SuccessfulFrames,
        int AcceptedAttempts, bool LeasesReturned, int OutstandingLeases,
        long InfrastructureFailures);

    private sealed record ReplayScenario(string Id, string Version, uint Seed,
        string StableDeviceIdentity, string Mode, string ContentHash, string ImageId,
        string ImageContentHash);

    private sealed record ReplayFrame(string CaseId, int CorrelationOrdinal,
        string CorrelationKind, string PixelDataHash, int Width, int Height,
        int StrideBytes, string PixelFormat, int? ValidBits, string HostCaptureUtc,
        long NormalizedFrameTimestamp, ulong? FrameCounter, bool CorrelationMatched,
        bool LeaseReturned);

    private sealed record ReplayOperation(string CaseId, int CorrelationOrdinal,
        string CorrelationKind, bool Accepted, bool Succeeded, string ReasonCode,
        string? FailureKind, string ExecutionStatus, string Decision,
        bool BusyObserved, bool PulseAfterBusy, IReadOnlyList<ReplayFrame> Frames);

    private sealed record ReplayProtocol(string Kind, string ReasonCode,
        string? CorrelationKind, int? CorrelationOrdinal, long MonotonicTimestamp);

    private sealed record ReplayEvidence(string ContractVersion, string ScenarioVersion,
        string InitialUtc, string ProviderId, string ProviderVersion,
        string AdapterPackageId, string AdapterPackageVersion,
        IReadOnlyList<ReplayScenario> Scenarios,
        IReadOnlyList<ReplayOperation> Operations,
        IReadOnlyList<ReplayProtocol> ProtocolFacts, int SuccessfulFrames,
        int AcceptedAttempts, bool LeasesReturned, int OutstandingLeases,
        long InfrastructureFailures);

    private sealed record SummaryEvidence(string Result, bool ProductionReady,
        string PhysicalDevices, string ProviderQualification, string StationAcceptance,
        string ProductionCycle, string AlgorithmExecution, string RuntimeComponent,
        string ConsumerSha256, int Scenarios, int AcceptedAttempts, int SuccessfulFrames,
        bool LeasesReturned, int OutstandingLeases, long InfrastructureFailures);

    private sealed class EvidenceCollector
    {
        internal readonly List<ScenarioEvidence> Scenarios = new();
        internal readonly List<OperationEvidence> Operations = new();
        internal readonly List<ProtocolEvidence> ProtocolFacts = new();
        internal readonly List<FrameEvidence> Frames = new();
        internal int SuccessfulFrames => Frames.Count;
        internal int AcceptedAttempts => Operations.Count(item => item.Accepted);
        internal bool AllLeasesReturned => Frames.All(item => item.LeaseReturned);
        internal int OutstandingLeases { get; private set; }
        internal long InfrastructureFailures { get; private set; }
        internal int NextCorrelationOrdinal => Operations.Count + 1;

        internal void AddScenario(VirtualCameraScenario scenario, string mode,
            VirtualCameraImage image) => Scenarios.Add(new ScenarioEvidence(
                scenario.Id, scenario.Version, scenario.Seed, scenario.StableDeviceIdentity,
                mode, scenario.ContentHash, image.Id, image.ContentHash));

        internal void AddOperation(int ordinal, string caseId,
            CameraAcquisitionAttempt attempt, bool busyObserved, bool pulseAfterBusy)
        {
            var outcome = attempt.Outcome ??
                throw new InvalidOperationException("CameraAcquisitionOutcomeMissing");
            Operations.Add(new OperationEvidence(
                caseId, ordinal, attempt.Correlation?.Kind.ToString() ?? "Qualification",
                attempt.Correlation?.Value.ToString("D") ?? "", attempt.Accepted,
                outcome.Succeeded, outcome.ReasonCode, outcome.FailureKind?.ToString(),
                outcome.ExecutionStatus.ToString(), outcome.Decision.ToString(),
                busyObserved, pulseAfterBusy, Array.Empty<FrameEvidence>()));
        }

        internal void AddFrame(FrameEvidence frame)
        {
            Frames.Add(frame);
            var index = Operations.FindLastIndex(item =>
                item.CorrelationOrdinal == frame.CorrelationOrdinal);
            Require(index >= 0);
            var operation = Operations[index];
            Operations[index] = operation with { Frames = operation.Frames.Concat(new[] { frame }).ToArray() };
        }

        internal void AddProtocol(IEnumerable<CameraProtocolObservation> observations)
        {
            foreach (var observation in observations)
            {
                var matching = Operations.FirstOrDefault(item =>
                    item.CorrelationId.Length != 0 && observation.Correlation is not null &&
                    item.CorrelationId == observation.Correlation.Value.ToString("D"));
                ProtocolFacts.Add(new ProtocolEvidence(observation.Kind.ToString(),
                    observation.ReasonCode, observation.Correlation?.Kind.ToString(),
                    matching?.CorrelationOrdinal, observation.ObservedAt.MonotonicTimestamp));
            }
        }

        internal void AddDiagnostics(VirtualCameraProviderDiagnostics diagnostics)
        {
            InfrastructureFailures += diagnostics.InfrastructureFailures;
            OutstandingLeases += diagnostics.Devices.Sum(item => item.OutstandingLeases);
        }
    }
}
