using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;

namespace SharpInspect.SampleHost;

/// <summary>
/// Public-interface consumer smoke for the development-only virtual camera.
/// The output is intentionally separate from the production Runtime and never
/// claims a physical camera, provider qualification, or production readiness.
/// </summary>
public static class VirtualCameraDemo
{
    private const string EvidenceVersion = "sharpinspect-virtual-camera-consumer-v1";
    private const string ScenarioVersion = "1";
    private const uint Seed = 0x1160_C0DE;
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static int Run(string artifactDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(artifactDirectory))
                throw new ArgumentException("VirtualCameraArtifactDirectoryInvalid");

            RunAsync(Path.GetFullPath(artifactDirectory))
                .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Console.WriteLine("V116-N01 virtual-camera-consumer PASS formats=5 replay=true leasesReturned=true productionReady=false");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("V116-N01 virtual-camera-consumer FAIL reason=VirtualCameraConsumerCheckFailed");
            return 1;
        }
    }

    private static async Task RunAsync(string artifactDirectory)
    {
        Directory.CreateDirectory(artifactDirectory);
        var collector = new EvidenceCollector();
        using var clock = new VirtualCameraClock(InitialUtc);

        var recorded = CreateRecordedImage(artifactDirectory);
        var formatSpecs = CreateFormatSpecs(recorded);

        await RunFormatScenarioAsync(clock, formatSpecs, collector);
        await RunControlScenarioAsync(clock, collector);
        await RunProtocolScenarioAsync(clock, collector);

        Require(collector.Formats == 5);
        Require(collector.AllLeasesReturned);
        Require(collector.OutstandingLeases == 0);
        Require(collector.InfrastructureFailures == 0);
        Require(collector.FramesProduced >= 7);

        var replay = new ReplayEvidence(
            EvidenceVersion,
            ScenarioVersion,
            Seed,
            FormatUtc(InitialUtc),
            VirtualCameraProvider.ProviderId,
            VirtualCameraProvider.ProviderVersion,
            collector.Scenarios,
            collector.Frames,
            collector.Health,
            collector.Operations,
            collector.ProtocolObservations,
            collector.FramesProduced,
            collector.FramesDropped);
        var summary = new SummaryEvidence(
            "Pass",
            false,
            "NotRun",
            "NotRun",
            "NotRun",
            "NotRun",
            "NotRun",
            "NotRun",
            collector.OutstandingLeases,
            collector.InfrastructureFailures,
            collector.Formats,
            collector.AllLeasesReturned,
            true,
            collector.FramesProduced,
            collector.FramesDropped);

        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        WriteJson(Path.Combine(artifactDirectory, "replay-evidence.json"), replay, options);
        WriteJson(Path.Combine(artifactDirectory, "summary.json"), summary, options);
    }

    private static async Task RunFormatScenarioAsync(VirtualCameraClock clock,
        IReadOnlyList<FormatSpec> specs, EvidenceCollector collector)
    {
        var scenario = new VirtualCameraScenario(
            "V116-format-replay", ScenarioVersion, Seed, "virtual-format-camera",
            CreateCapabilities(), specs.Select(spec => spec.Image),
            specs.Select(spec => new VirtualCameraAcquisitionPlan(new[]
            {
                new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                    VirtualCameraSignalKind.Frame, spec.Image.Id)
            })),
            new[]
            {
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                    TimeSpan.FromMilliseconds(2)),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero)
            },
            new[] { VirtualCameraOpenOutcome.Success });
        collector.AddScenario(scenario, "formats", specs.Select(spec => spec.Image).ToArray());

        var providerConcrete = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        ICameraProvider provider = providerConcrete;
        ICameraDevice? device = null;
        try
        {
            device = await OpenOnlyDeviceAsync(provider);
            for (var index = 0; index < specs.Count; index++)
            {
                var spec = specs[index];
                if (index != 0)
                {
                    var stopped = await device.StopAsync();
                    Require(stopped.Succeeded);
                }

                var configurationTask = device.ApplyConfigurationAsync(
                    RequestFor(spec.PixelFormat, spec.ValidBits)).AsTask();
                if (index == 0)
                    clock.AdvanceBy(TimeSpan.FromMilliseconds(2));
                var configuration = await configurationTask.ConfigureAwait(false);
                Require(configuration.Succeeded && configuration.Effective is not null);

                var started = await device.StartAsync();
                Require(started.Succeeded);

                var correlation = new FrameAcquisitionRequest(spec.Correlation, "Primary");
                var requestStart = clock.GetTimePoint();
                var acquisitionTask = device.AcquireAsync(correlation).AsTask();
                if (index == 0)
                    clock.SetUtc(InitialUtc.AddHours(1));
                clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
                var acquired = await acquisitionTask.ConfigureAwait(false);
                collector.Frames.Add(CaptureFrame(
                    $"V116-N01-{spec.Label}", scenario, spec.Image, spec.Correlation, acquired,
                    requestStart));
                collector.Health.Add(HealthEvidence.From(
                    $"V116-N01-{spec.Label}", device.GetHealthSnapshot()));
            }

            var stoppedAtEnd = await device.StopAsync();
            Require(stoppedAtEnd.Succeeded);
        }
        finally
        {
            if (device is not null)
                await device.DisposeAsync().ConfigureAwait(false);
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        AddDiagnostics(collector, providerConcrete.GetDiagnostics());
    }

    private static async Task RunControlScenarioAsync(VirtualCameraClock clock,
        EvidenceCollector collector)
    {
        var image = VirtualCameraImage.CreateSynthetic(
            "control", 4, 3, VisionPixelFormat.Mono8, null, Seed ^ 0x10u, 1);
        var scenario = new VirtualCameraScenario(
            "V116-control-replay", ScenarioVersion, Seed ^ 0x100u, "virtual-control-camera",
            CreateCapabilities(), new[] { image },
            new[]
            {
                new VirtualCameraAcquisitionPlan(Array.Empty<VirtualCameraSignal>()),
                SingleFramePlan(image.Id, 2),
                SingleFramePlan(image.Id, 2),
                new VirtualCameraAcquisitionPlan(new[]
                {
                    new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                        VirtualCameraSignalKind.Disconnect)
                }),
                SingleFramePlan(image.Id, 1)
            },
            new[]
            {
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.WriteFailure, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.ReadBackFailure, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero),
                new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero)
            },
            new[]
            {
                VirtualCameraOpenOutcome.Success, VirtualCameraOpenOutcome.Success,
                VirtualCameraOpenOutcome.Success, VirtualCameraOpenOutcome.Success,
                VirtualCameraOpenOutcome.Success
            });
        collector.AddScenario(scenario, "controls", new[] { image });

        var providerConcrete = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        ICameraProvider provider = providerConcrete;
        ICameraDevice? device = null;
        try
        {
            device = await OpenOnlyDeviceAsync(provider);
            var initialConfiguration = await device.ApplyConfigurationAsync(
                RequestFor(VisionPixelFormat.Mono8, null));
            Require(initialConfiguration.Succeeded);
            Require((await device.StartAsync()).Succeeded);

            var timeoutRequest = new FrameAcquisitionRequest(
                Correlation("00000000-0000-0000-0000-000000001101"), "Primary");
            var timeoutTask = device.AcquireAsync(timeoutRequest).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(20));
            var timeout = await timeoutTask.ConfigureAwait(false);
            Require(!timeout.Succeeded && timeout.Failure?.Kind == CameraAcquisitionFailureKind.TimedOut);
            collector.Operations.Add(OperationEvidence.From("V116-N02-timeout", timeout,
                clock.Timestamp));
            collector.Health.Add(HealthEvidence.From("V116-N02-timeout",
                device.GetHealthSnapshot()));
            Require((await device.StopAsync()).Succeeded);

            Require((await device.StartAsync()).Succeeded);
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelledRequest = new FrameAcquisitionRequest(
                    Correlation("00000000-0000-0000-0000-000000001102"), "Primary");
                var cancelledTask = device.AcquireAsync(cancelledRequest, cancellation.Token).AsTask();
                cancellation.Cancel();
                var cancelled = await cancelledTask.ConfigureAwait(false);
                Require(!cancelled.Succeeded && cancelled.Failure?.Kind == CameraAcquisitionFailureKind.Cancelled);
                collector.Operations.Add(OperationEvidence.From("V116-N03-cancel", cancelled,
                    clock.Timestamp));
                collector.Health.Add(HealthEvidence.From("V116-N03-cancel",
                    device.GetHealthSnapshot()));
                clock.AdvanceBy(TimeSpan.FromMilliseconds(2));
            }

            Require((await device.StartAsync()).Succeeded);
            var stopRequest = new FrameAcquisitionRequest(
                Correlation("00000000-0000-0000-0000-000000001103"), "Primary");
            var stoppedTask = device.AcquireAsync(stopRequest).AsTask();
            var stop = await device.StopAsync().ConfigureAwait(false);
            var stopped = await stoppedTask.ConfigureAwait(false);
            Require(stop.Succeeded && !stopped.Succeeded &&
                stopped.Failure?.ReasonCode == "VirtualCameraStopped");
            collector.Operations.Add(OperationEvidence.From("V116-N04-stop", stopped,
                clock.Timestamp));
            collector.Health.Add(HealthEvidence.From("V116-N04-stop",
                device.GetHealthSnapshot()));
            clock.AdvanceBy(TimeSpan.FromMilliseconds(2));

            var writeFailure = await device.ApplyConfigurationAsync(
                RequestFor(VisionPixelFormat.Mono8, null));
            Require(!writeFailure.Succeeded &&
                writeFailure.ReasonCode == "VirtualCameraConfigurationWriteFailed");
            collector.Operations.Add(new OperationEvidence(
                "V116-N05-config-write-failure", false, writeFailure.ReasonCode, clock.Timestamp));
            collector.Health.Add(HealthEvidence.From("V116-N05-config-write-failure",
                device.GetHealthSnapshot()));
            await device.DisposeAsync().ConfigureAwait(false);
            device = null;

            device = await OpenOnlyDeviceAsync(provider);
            var readBackFailure = await device.ApplyConfigurationAsync(
                RequestFor(VisionPixelFormat.Mono8, null));
            Require(!readBackFailure.Succeeded &&
                readBackFailure.ReasonCode == "VirtualCameraConfigurationReadBackFailed");
            collector.Operations.Add(new OperationEvidence(
                "V116-N06-config-readback-failure", false, readBackFailure.ReasonCode, clock.Timestamp));
            collector.Health.Add(HealthEvidence.From("V116-N06-config-readback-failure",
                device.GetHealthSnapshot()));
            await device.DisposeAsync().ConfigureAwait(false);
            device = null;

            device = await OpenOnlyDeviceAsync(provider);
            Require((await device.ApplyConfigurationAsync(
                RequestFor(VisionPixelFormat.Mono8, null))).Succeeded);
            Require((await device.StartAsync()).Succeeded);
            var disconnectRequest = new FrameAcquisitionRequest(
                Correlation("00000000-0000-0000-0000-000000001104"), "Primary");
            var disconnectedTask = device.AcquireAsync(disconnectRequest).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            var disconnected = await disconnectedTask.ConfigureAwait(false);
            Require(!disconnected.Succeeded &&
                disconnected.Failure?.Kind == CameraAcquisitionFailureKind.Disconnected);
            collector.Operations.Add(OperationEvidence.From("V116-N07-disconnect", disconnected,
                clock.Timestamp));
            collector.Health.Add(HealthEvidence.From("V116-N07-disconnect",
                device.GetHealthSnapshot()));
            await device.DisposeAsync().ConfigureAwait(false);
            device = null;

            device = await OpenOnlyDeviceAsync(provider);
            Require((await device.ApplyConfigurationAsync(
                RequestFor(VisionPixelFormat.Mono8, null))).Succeeded);
            Require((await device.StartAsync()).Succeeded);
            var recoveredCorrelation = Correlation("00000000-0000-0000-0000-000000001105");
            var recoveredRequest = new FrameAcquisitionRequest(recoveredCorrelation, "Primary");
            var recoveredTask = device.AcquireAsync(recoveredRequest).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            var recovered = await recoveredTask.ConfigureAwait(false);
            collector.Frames.Add(CaptureFrame(
                "V116-N08-explicit-reopen", scenario, image, recoveredCorrelation, recovered));
            collector.Health.Add(HealthEvidence.From("V116-N08-explicit-reopen",
                device.GetHealthSnapshot()));

            var exhausted = await device.AcquireAsync(new FrameAcquisitionRequest(
                Correlation("00000000-0000-0000-0000-000000001106"), "Primary"));
            Require(!exhausted.Succeeded &&
                exhausted.Failure?.ReasonCode == "VirtualCameraAcquisitionPlanExhausted");
            collector.Operations.Add(OperationEvidence.From("V116-N12-no-auto-loop",
                exhausted, clock.Timestamp));
            collector.Health.Add(HealthEvidence.From("V116-N12-no-auto-loop",
                device.GetHealthSnapshot()));
            Require((await device.StopAsync()).Succeeded);
        }
        finally
        {
            if (device is not null)
                await device.DisposeAsync().ConfigureAwait(false);
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        AddDiagnostics(collector, providerConcrete.GetDiagnostics());
    }

    private static async Task RunProtocolScenarioAsync(VirtualCameraClock clock,
        EvidenceCollector collector)
    {
        var image = VirtualCameraImage.CreateSynthetic(
            "protocol", 4, 3, VisionPixelFormat.Mono8, null, Seed ^ 0x20u, 1);
        var scenario = new VirtualCameraScenario(
            "V116-protocol-replay", ScenarioVersion, Seed ^ 0x200u, "virtual-protocol-camera",
            CreateCapabilities(), new[] { image },
            new[]
            {
                new VirtualCameraAcquisitionPlan(new[]
                {
                    new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                        VirtualCameraSignalKind.Frame, image.Id, VirtualFrameAssociation.CurrentRequest),
                    new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                        VirtualCameraSignalKind.Frame, image.Id, VirtualFrameAssociation.PreviousRequest)
                }),
                new VirtualCameraAcquisitionPlan(new[]
                {
                    new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                        VirtualCameraSignalKind.Frame, image.Id),
                    new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                        VirtualCameraSignalKind.Frame, image.Id)
                }),
                new VirtualCameraAcquisitionPlan(new[]
                {
                    new VirtualCameraSignal(TimeSpan.FromMilliseconds(1),
                        VirtualCameraSignalKind.Frame, image.Id, VirtualFrameAssociation.PreviousRequest)
                })
            },
            new[] { new VirtualCameraConfigurationPlan(
                VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero) },
            new[] { VirtualCameraOpenOutcome.Success },
            new[] { new VirtualCameraSignal(TimeSpan.Zero, VirtualCameraSignalKind.Frame, image.Id,
                VirtualFrameAssociation.Uncorrelated) });
        collector.AddScenario(scenario, "protocol", new[] { image });

        var providerConcrete = new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
        ICameraProvider provider = providerConcrete;
        ICameraDevice? device = null;
        try
        {
            device = await OpenOnlyDeviceAsync(provider);
            clock.AdvanceBy(TimeSpan.Zero);
            Require((await device.ApplyConfigurationAsync(
                RequestFor(VisionPixelFormat.Mono8, null))).Succeeded);
            Require((await device.StartAsync()).Succeeded);

            var extraCorrelation = Correlation("00000000-0000-0000-0000-000000001201");
            var extraTask = device.AcquireAsync(new FrameAcquisitionRequest(
                extraCorrelation, "Primary")).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            var extra = await extraTask.ConfigureAwait(false);
            collector.Frames.Add(CaptureFrame(
                "V116-N09-extra-old-frame-isolated", scenario, image, extraCorrelation, extra));
            collector.Health.Add(HealthEvidence.From("V116-N09-extra-old-frame-isolated",
                device.GetHealthSnapshot()));

            var ambiguousCorrelation = Correlation("00000000-0000-0000-0000-000000001202");
            var ambiguousTask = device.AcquireAsync(new FrameAcquisitionRequest(
                ambiguousCorrelation, "Primary")).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
            var ambiguous = await ambiguousTask.ConfigureAwait(false);
            Require(!ambiguous.Succeeded &&
                ambiguous.Failure?.ReasonCode == "VirtualCameraFrameAmbiguous");
            collector.ProtocolObservations.Add(OperationEvidence.From(
                "V116-N10-ambiguous-frame", ambiguous, clock.Timestamp));
            collector.Health.Add(HealthEvidence.From("V116-N10-ambiguous-frame",
                device.GetHealthSnapshot()));

            var oldCorrelation = Correlation("00000000-0000-0000-0000-000000001203");
            var oldTask = device.AcquireAsync(new FrameAcquisitionRequest(
                oldCorrelation, "Primary")).AsTask();
            clock.AdvanceBy(TimeSpan.FromMilliseconds(20));
            var old = await oldTask.ConfigureAwait(false);
            Require(!old.Succeeded && old.Failure?.Kind == CameraAcquisitionFailureKind.TimedOut);
            collector.ProtocolObservations.Add(OperationEvidence.From(
                "V116-N11-old-late-frame-isolated", old, clock.Timestamp));
            collector.Health.Add(HealthEvidence.From("V116-N11-old-late-frame-isolated",
                device.GetHealthSnapshot()));
            Require((await device.StopAsync()).Succeeded);
        }
        finally
        {
            if (device is not null)
                await device.DisposeAsync().ConfigureAwait(false);
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        var diagnostics = providerConcrete.GetDiagnostics();
        var deviceDiagnostics = diagnostics.Devices.Single();
        Require(deviceDiagnostics.FramesDropped >= 5);
        collector.FramesProduced += deviceDiagnostics.FramesProduced;
        collector.FramesDropped += deviceDiagnostics.FramesDropped;
        collector.InfrastructureFailures += diagnostics.InfrastructureFailures;
        collector.OutstandingLeases += deviceDiagnostics.OutstandingLeases;
    }

    private static async Task<ICameraDevice> OpenOnlyDeviceAsync(ICameraProvider provider)
    {
        var discovery = await provider.DiscoverAsync().ConfigureAwait(false);
        Require(discovery.Succeeded && discovery.Devices.Count == 1);
        var opened = await provider.OpenAsync(
            discovery.Devices[0].StableDeviceIdentity).ConfigureAwait(false);
        Require(opened.Succeeded && opened.Device is not null);
        return opened.Device!;
    }

    private static FrameEvidence CaptureFrame(string caseId, VirtualCameraScenario scenario,
        VirtualCameraImage expected, ExecutionCorrelationId correlation,
        FrameAcquisitionResult result, FrameTimePoint? expectedStart = null)
    {
        Require(result.Succeeded && result.Lease is not null && result.Failure is null);
        using var lease = result.Lease!;
        var frame = lease.Frame;
        var provenance = lease.Provenance;
        Require(!lease.IsReturned);
        Require(frame.Correlation == correlation && provenance.Correlation == correlation);
        Require(frame.LogicalCameraRole == "Primary");
        Require(frame.PixelFormat == expected.PixelFormat && frame.ValidBits == expected.ValidBits);
        Require(frame.Width == expected.Width && frame.Height == expected.Height);
        Require(provenance.ProviderId == VirtualCameraProvider.ProviderId);
        Require(provenance.ProviderVersion == VirtualCameraProvider.ProviderVersion);
        Require(provenance.StableDeviceIdentity == scenario.StableDeviceIdentity);
        Require(provenance.NormalizationDetails.Contains(
            "scenarioHash=" + scenario.ContentHash, StringComparison.Ordinal));
        var sourceToken = expected.SourceDataHash ?? "memory";
        Require(provenance.NormalizationDetails.Contains(
            "source=" + sourceToken, StringComparison.Ordinal));

        var milestones = provenance.Milestones;
        Require(milestones.MonotonicFrequency == VirtualCameraClock.Frequency);
        Require(milestones.TriggerAccepted is not null &&
            milestones.AcquisitionStarted is not null &&
            milestones.NativeFrameReceived is not null &&
            milestones.NormalizedFrameReady is not null);
        Require(milestones.TriggerAccepted!.MonotonicTimestamp <=
            milestones.AcquisitionStarted!.MonotonicTimestamp);
        Require(milestones.AcquisitionStarted.MonotonicTimestamp <=
            milestones.NativeFrameReceived!.MonotonicTimestamp);
        Require(milestones.NativeFrameReceived.MonotonicTimestamp <=
            milestones.NormalizedFrameReady!.MonotonicTimestamp);
        if (expectedStart is not null)
        {
            Require(milestones.TriggerAccepted!.MonotonicTimestamp ==
                expectedStart.MonotonicTimestamp);
            Require(milestones.TriggerAccepted.HostObservedAtUtc ==
                expectedStart.HostObservedAtUtc);
            Require(milestones.NormalizedFrameReady.HostObservedAtUtc !=
                expectedStart.HostObservedAtUtc);
        }

        var expectedRowsHash = HashRows(expected);
        var actualRowsHash = HashRows(frame);
        Require(actualRowsHash == expectedRowsHash);
        for (var row = 0; row < expected.Height; row++)
            Require(expected.GetRowSpan(row).SequenceEqual(frame.GetRowSpan(row)));

        var copyEvidence = provenance.PoolCopyEvidence;
        Require(copyEvidence is not null);
        var shouldAlign = expected.PixelFormat == VisionPixelFormat.Mono16 &&
            (expected.StrideBytes & 1) != 0;
        Require(copyEvidence!.StrideChanged == shouldAlign);
        if (shouldAlign)
            Require(copyEvidence.DestinationStrideBytes == expected.StrideBytes + 1);

        var frameCounter = provenance.FrameCounter;
        Require(frameCounter.HasValue && frameCounter.Value > 0);
        var frameCounterValue = frameCounter.GetValueOrDefault();
        var observedUtc = FormatUtc(frame.Metadata.HostCaptureUtc);
        var observedTimestamp = frame.Metadata.HostCaptureUtc ==
            milestones.NormalizedFrameReady.HostObservedAtUtc
            ? milestones.NormalizedFrameReady.MonotonicTimestamp : -1;
        Require(observedTimestamp >= 0);

        lease.Dispose();
        Require(lease.IsReturned);
        return new FrameEvidence(
            caseId,
            correlation.Kind.ToString(),
            correlation.Value.ToString("D"),
            scenario.Id,
            scenario.Version,
            scenario.ContentHash,
            expected.Id,
            expected.ContentHash,
            expected.PixelDataHash,
            expected.SourceDataHash,
            expectedRowsHash,
            actualRowsHash,
            expected.Width,
            expected.Height,
            expected.StrideBytes,
            frame.StrideBytes,
            expected.PixelFormat.ToString(),
            expected.ValidBits,
            observedUtc,
            observedTimestamp,
            frameCounterValue,
            true,
            true,
            true);
    }

    private static FormatSpec[] CreateFormatSpecs(VirtualCameraImage recorded)
    {
        return new[]
        {
            new FormatSpec("Mono8", VisionPixelFormat.Mono8, null, recorded,
                Correlation("00000000-0000-0000-0000-000000001001")),
            new FormatSpec("Mono16-10", VisionPixelFormat.Mono16, 10,
                VirtualCameraImage.CreateSynthetic("mono10", 4, 3,
                    VisionPixelFormat.Mono16, 10, Seed ^ 0x10u, 1),
                Correlation("00000000-0000-0000-0000-000000001002")),
            new FormatSpec("Mono16-12", VisionPixelFormat.Mono16, 12,
                VirtualCameraImage.CreateSynthetic("mono12", 4, 3,
                    VisionPixelFormat.Mono16, 12, Seed ^ 0x12u, 1),
                Correlation("00000000-0000-0000-0000-000000001003")),
            new FormatSpec("Mono16-16", VisionPixelFormat.Mono16, 16,
                VirtualCameraImage.CreateSynthetic("mono16", 4, 3,
                    VisionPixelFormat.Mono16, 16, Seed ^ 0x16u, 1),
                Correlation("00000000-0000-0000-0000-000000001004")),
            new FormatSpec("Bgr24", VisionPixelFormat.Bgr24, null,
                VirtualCameraImage.CreateSynthetic("bgr24", 4, 3,
                    VisionPixelFormat.Bgr24, null, Seed ^ 0x24u, 1),
                Correlation("00000000-0000-0000-0000-000000001005"))
        };
    }

    private static VirtualCameraImage CreateRecordedImage(string artifactDirectory)
    {
        var raw = new byte[]
        {
            1, 2, 3, 4, 0,
            5, 6, 7, 8, 0,
            9, 10, 11, 12
        };
        var path = Path.Combine(artifactDirectory, "recorded-mono8.raw");
        File.WriteAllBytes(path, raw);
        var sourceHash = Sha256(raw);
        var image = VirtualCameraImage.LoadRecordedRaw(
            "recorded-mono8", Path.GetFullPath(path), 4, 3, 5,
            VisionPixelFormat.Mono8, null, sourceHash);
        Require(image.SourceDataHash == sourceHash);
        return image;
    }

    private static VirtualCameraAcquisitionPlan SingleFramePlan(string imageId, int milliseconds,
        VirtualFrameAssociation association = VirtualFrameAssociation.CurrentRequest) =>
        new(new[] { new VirtualCameraSignal(TimeSpan.FromMilliseconds(milliseconds),
            VirtualCameraSignalKind.Frame, imageId, association) });

    private static RequestedCameraConfiguration RequestFor(VisionPixelFormat format, int? validBits) =>
        new(ProductionAcquisitionMode.SoftwareTrigger, 20, 0,
            new RegionOfInterest(0, 0, 4, 3), format, validBits, 20, 0,
            format == VisionPixelFormat.Bgr24 ? new WhiteBalanceRgb(1, 1.5, 2) : null);

    private static CameraCapabilities CreateCapabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { VisionPixelFormat.Mono8, VisionPixelFormat.Mono16, VisionPixelFormat.Bgr24 },
        new[] { 10, 12, 16 },
        new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
        new CameraRoiCapabilities(8, 8,
            new CameraIntCapability(0, 7, 1), new CameraIntCapability(0, 7, 1),
            new CameraIntCapability(1, 8, 1), new CameraIntCapability(1, 8, 1)),
        new CameraWhiteBalanceCapabilities(
            new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact)));

    private static ExecutionCorrelationId Correlation(string value) =>
        new(ExecutionKind.Qualification, Guid.Parse(value));

    private static string HashRows(VirtualCameraImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var row = 0; row < image.Height; row++)
            hash.AppendData(image.GetRowSpan(row));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashRows(VisionFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var row = 0; row < frame.Height; row++)
            hash.AppendData(frame.GetRowSpan(row));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(value, options);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void AddDiagnostics(EvidenceCollector collector,
        VirtualCameraProviderDiagnostics diagnostics)
    {
        collector.InfrastructureFailures += diagnostics.InfrastructureFailures;
        foreach (var device in diagnostics.Devices)
        {
            collector.FramesProduced += device.FramesProduced;
            collector.FramesDropped += device.FramesDropped;
            collector.OutstandingLeases += device.OutstandingLeases;
        }
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("VirtualCameraConsumerCheckFailed");
    }

    private sealed record FormatSpec(string Label, VisionPixelFormat PixelFormat,
        int? ValidBits, VirtualCameraImage Image, ExecutionCorrelationId Correlation);

    private sealed record ScenarioEvidence(string CaseGroup, string Id, string Version,
        string ContentHash, uint Seed, IReadOnlyList<string> ImageIds,
        IReadOnlyList<string> ImageContentHashes, IReadOnlyList<string> SourceDataHashes);

    private sealed record FrameEvidence(string CaseId, string CorrelationKind,
        string CorrelationId, string ScenarioId, string ScenarioVersion, string ScenarioHash,
        string ImageId, string ImageContentHash, string PixelDataHash, string? SourceDataHash,
        string ExpectedValidRowsSha256, string ActualValidRowsSha256, int Width, int Height,
        int SourceStrideBytes, int DestinationStrideBytes, string PixelFormat, int? ValidBits,
        string HostCaptureUtc, long MonotonicTimestamp, ulong FrameCounter,
        bool CorrelationMatched, bool ProvenanceMatched, bool LeaseReturned);

    private sealed record HealthEvidence(string CaseId, string ProviderAvailability,
        string Connection, string Configuration, string Acquisition, string? FaultReasonCode,
        string ObservedUtc, long MonotonicTimestamp)
    {
        internal static HealthEvidence From(string caseId, CameraHealthSnapshot snapshot) =>
            new(caseId, snapshot.ProviderAvailability.ToString(), snapshot.Connection.ToString(),
                snapshot.Configuration.ToString(), snapshot.Acquisition.ToString(),
                snapshot.LastFault?.ReasonCode, FormatUtc(snapshot.ObservedAt.HostObservedAtUtc),
                snapshot.ObservedAt.MonotonicTimestamp);
    }

    private sealed record OperationEvidence(string CaseId, bool Succeeded,
        string ReasonCode, long ClockTimestamp)
    {
        internal static OperationEvidence From(string caseId, FrameAcquisitionResult result,
            long clockTimestamp) =>
            new(caseId, result.Succeeded, result.ReasonCode, clockTimestamp);
    }

    private sealed record ReplayEvidence(string ContractVersion, string ScenarioVersion,
        uint Seed, string InitialUtc, string ProviderId, string ProviderVersion,
        IReadOnlyList<ScenarioEvidence> Scenarios, IReadOnlyList<FrameEvidence> Frames,
        IReadOnlyList<HealthEvidence> Health, IReadOnlyList<OperationEvidence> Operations,
        IReadOnlyList<OperationEvidence> ProtocolObservations, long FramesProduced,
        long FramesDropped);

    private sealed record SummaryEvidence(string Result, bool ProductionReady,
        string PhysicalDevices, string RealCamera, string RuntimeAcceptance,
        string RuntimeProductionAcceptance, string ProviderQualification,
        string FormalQualification, int OutstandingLeases, long InfrastructureFailures,
        int Formats, bool LeasesReturned, bool Replay, long FramesProduced,
        long FramesDropped);

    private sealed class EvidenceCollector
    {
        internal readonly List<ScenarioEvidence> Scenarios = new();
        internal readonly List<FrameEvidence> Frames = new();
        internal readonly List<HealthEvidence> Health = new();
        internal readonly List<OperationEvidence> Operations = new();
        internal readonly List<OperationEvidence> ProtocolObservations = new();
        internal bool AllLeasesReturned { get; private set; } = true;
        internal int Formats => Frames.Count(frame => frame.CaseId.StartsWith(
            "V116-N01-", StringComparison.Ordinal));
        internal int OutstandingLeases { get; set; }
        internal long InfrastructureFailures { get; set; }
        internal long FramesProduced { get; set; }
        internal long FramesDropped { get; set; }

        internal void AddScenario(VirtualCameraScenario scenario, string group,
            IReadOnlyList<VirtualCameraImage> images)
        {
            Scenarios.Add(new ScenarioEvidence(group, scenario.Id, scenario.Version,
                scenario.ContentHash, scenario.Seed,
                images.Select(image => image.Id).ToArray(),
                images.Select(image => image.ContentHash).ToArray(),
                images.Select(image => image.SourceDataHash ?? "synthetic").ToArray()));
        }
    }
}
