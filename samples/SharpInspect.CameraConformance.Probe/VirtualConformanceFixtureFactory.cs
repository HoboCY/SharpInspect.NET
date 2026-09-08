using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Conformance;
using SharpInspect.Cameras.Virtual;

namespace SharpInspect.CameraConformance.Probe;

/// <summary>
/// Creates one deterministic Virtual Camera session for the public camera
/// conformance consumer. The factory is deliberately single-owner: the next
/// fixture is admitted only after the previous provider and clock have retired.
/// </summary>
public sealed class VirtualConformanceFixtureFactory : ICameraConformanceFixtureFactory
{
    private const string FixtureId = "virtual-camera-conformance";
    private const string FixtureVersion = "1";
    private const string FixtureScriptVersion = "virtual-conformance-script-v1";
    private const string StableDeviceIdentity = "virtual-conformance-camera";
    private const uint Seed = 0x22_23_C0DEu;
    private const int PoolCapacity = 2;
    private static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(10);
    private static readonly DateTimeOffset InitialUtc = new(2026, 1, 1, 0, 0, 0,
        TimeSpan.Zero);
    private static readonly CameraProviderIdentity ProviderIdentity = new(
        VirtualCameraProvider.ProviderId,
        VirtualCameraProvider.ProviderVersion,
        VirtualCameraProvider.AdapterPackageId,
        VirtualCameraProvider.AdapterPackageVersion);

    private readonly object _gate = new();
    private readonly IReadOnlyList<FixtureImage> _images;
    private readonly IReadOnlyList<RequestedCameraConfiguration> _configurations;
    private VirtualConformanceFixture? _activeFixture;

    public VirtualConformanceFixtureFactory()
    {
        var definitions = CreateDefinitions();
        _images = definitions;
        _configurations = definitions.Select(definition => definition.Configuration)
            .ToArray();

        Declaration = new CameraConformanceFixtureDeclaration(
            FixtureId,
            FixtureVersion,
            ProviderIdentity,
            StableDeviceIdentity,
            _configurations,
            definitions.Select(definition => definition.CanonicalFrameHash),
            PoolCapacity,
            Seed,
            FrameDelay,
            CreatePublicFixtureData(definitions),
            challengeFrameHashes: definitions.Select(definition => HashValidRows(definition.ChallengeImage)));
    }

    public CameraConformanceFixtureDeclaration Declaration { get; }

    public ValueTask<ICameraConformanceFixture> CreateAsync(
        CameraConformanceFixtureRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<ICameraConformanceFixture>(cancellationToken);
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var definition = ValidateRequest(request);
        lock (_gate)
        {
            if (_activeFixture is not null)
            {
                if (_activeFixture.CleanupCompletion.IsCompletedSuccessfully)
                {
                    _activeFixture = null;
                }
                else if (_activeFixture.CleanupCompletion.IsCompleted)
                {
                    throw new InvalidOperationException("VirtualConformanceFixtureCleanupFailed");
                }
                else
                {
                    throw new InvalidOperationException("VirtualConformanceFixtureCleanupPending");
                }
            }

            var clock = new VirtualCameraClock(InitialUtc);
            VirtualCameraProvider? provider = null;
            try
            {
                var scenario = CreateScenario(request, definition);
                provider = new VirtualCameraProvider(new[] { scenario }, clock, PoolCapacity);

                VirtualConformanceFixture? fixture = null;
                fixture = new VirtualConformanceFixture(
                    provider,
                    clock,
                    scenario,
                    request.Configuration,
                    request.Case,
                    () => ReleaseFixture(fixture!));
                _activeFixture = fixture;
                return ValueTask.FromResult<ICameraConformanceFixture>(fixture);
            }
            catch
            {
                if (provider is not null)
                    provider.DisposeAsync().GetAwaiter().GetResult();
                clock.Dispose();
                throw;
            }
        }
    }

    private FixtureImage ValidateRequest(CameraConformanceFixtureRequest request)
    {
        if (!Enum.IsDefined(typeof(CameraConformanceCase), request.Case))
            throw new ArgumentException("VirtualConformanceFixtureCaseInvalid", nameof(request));
        if (request.AcquisitionCount is < 0 or > VirtualCameraScenario.MaximumAcquisitions)
            throw new ArgumentOutOfRangeException(nameof(request),
                "VirtualConformanceFixtureAcquisitionCountInvalid");
        if (request.Configuration is null)
            throw new ArgumentException("VirtualConformanceFixtureConfigurationRequired",
                nameof(request));

        for (var index = 0; index < _configurations.Count; index++)
        {
            if (Equals(_configurations[index], request.Configuration))
            {
                var mode = request.Configuration.ProductionAcquisitionMode;
                if (request.Case == CameraConformanceCase.SoftwareAcquisition &&
                    mode != ProductionAcquisitionMode.SoftwareTrigger)
                    throw new ArgumentException(
                        "VirtualConformanceFixtureCaseConfigurationMismatch", nameof(request));
                if (request.Case == CameraConformanceCase.HardwareAcquisition &&
                    mode != ProductionAcquisitionMode.HardwareTrigger)
                    throw new ArgumentException(
                        "VirtualConformanceFixtureCaseConfigurationMismatch", nameof(request));
                return _images[index];
            }
        }

        throw new ArgumentException("VirtualConformanceFixtureConfigurationMismatch",
            nameof(request));
    }

    private VirtualCameraScenario CreateScenario(CameraConformanceFixtureRequest request,
        FixtureImage definition)
    {
        var acquisitionPlans = CreateAcquisitionPlans(request.Case,
            request.AcquisitionCount, definition.Image.Id);
        var distinctChallenge = request.Case is CameraConformanceCase.LeaseLifetime or CameraConformanceCase.Cancellation or CameraConformanceCase.ExtraFrame;
        if (distinctChallenge)
            acquisitionPlans = acquisitionPlans.Select((plan, index) => index == 0 ? plan :
                new VirtualCameraAcquisitionPlan(new[] { new VirtualCameraSignal(FrameDelay,
                    VirtualCameraSignalKind.Frame, definition.ChallengeImage.Id) })).ToArray();
        var configurationPlans = CreateConfigurationPlans(request.Case,
            request.AcquisitionCount);
        var openingOutcomes = CreateOpeningOutcomes(request.Case,
            request.AcquisitionCount);
        var unsolicitedSignals = request.Case == CameraConformanceCase.CallbackIsolation
            ? new[]
            {
                new VirtualCameraSignal(TimeSpan.Zero, VirtualCameraSignalKind.Frame,
                    definition.Image.Id, VirtualFrameAssociation.Uncorrelated)
            }
            : Array.Empty<VirtualCameraSignal>();

        var scenarioId = $"virtual-conformance-{request.Case}-{definition.Image.Id}";
        return new VirtualCameraScenario(
            scenarioId,
            VirtualCameraScenario.SimulatorVersion,
            Seed,
            StableDeviceIdentity,
            CreateCapabilities(),
            distinctChallenge ? new[] { definition.Image, definition.ChallengeImage } : new[] { definition.Image },
            acquisitionPlans,
            configurationPlans,
            openingOutcomes,
            unsolicitedSignals,
            "Virtual Conformance Camera");
    }

    private static IReadOnlyList<VirtualCameraAcquisitionPlan> CreateAcquisitionPlans(
        CameraConformanceCase testCase, int requestedCount, string imageId)
    {
        var count = testCase switch
        {
            CameraConformanceCase.DisconnectRecovery => Math.Max(2, requestedCount),
            CameraConformanceCase.ExtraFrame or CameraConformanceCase.CallbackIsolation or
                CameraConformanceCase.AdapterCallbackBudget =>
                Math.Max(1, requestedCount),
            CameraConformanceCase.PoolExhaustion => Math.Max(PoolCapacity + 1, requestedCount),
            _ => requestedCount
        };
        count = Math.Min(count, VirtualCameraScenario.MaximumAcquisitions);

        var plans = new List<VirtualCameraAcquisitionPlan>(count);
        var signalBudget = VirtualCameraScenario.MaximumSignals -
            (testCase == CameraConformanceCase.CallbackIsolation ? 1 : 0);
        var emittedSignals = 0;
        for (var index = 0; index < count; index++)
        {
            if (testCase == CameraConformanceCase.Timeout && index == 0)
            {
                plans.Add(new VirtualCameraAcquisitionPlan(
                    Array.Empty<VirtualCameraSignal>()));
                continue;
            }

            if (testCase == CameraConformanceCase.DisconnectRecovery && index == 0)
            {
                plans.Add(new VirtualCameraAcquisitionPlan(new[]
                {
                    new VirtualCameraSignal(FrameDelay, VirtualCameraSignalKind.Disconnect)
                }));
                continue;
            }

            if ((testCase == CameraConformanceCase.ExtraFrame ||
                    testCase == CameraConformanceCase.CallbackIsolation ||
                    testCase == CameraConformanceCase.AdapterCallbackBudget) && index == 0)
            {
                if (emittedSignals + 2 <= signalBudget)
                {
                    plans.Add(new VirtualCameraAcquisitionPlan(new[]
                    {
                        new VirtualCameraSignal(FrameDelay, VirtualCameraSignalKind.Frame,
                            imageId, VirtualFrameAssociation.CurrentRequest),
                        new VirtualCameraSignal(FrameDelay, VirtualCameraSignalKind.Frame,
                            imageId, VirtualFrameAssociation.PreviousRequest)
                    }));
                    emittedSignals += 2;
                }
                else
                {
                    plans.Add(new VirtualCameraAcquisitionPlan(
                        Array.Empty<VirtualCameraSignal>()));
                }
                continue;
            }

            if (emittedSignals < signalBudget)
            {
                plans.Add(new VirtualCameraAcquisitionPlan(new[]
                {
                    new VirtualCameraSignal(FrameDelay, VirtualCameraSignalKind.Frame, imageId)
                }));
                emittedSignals++;
            }
            else
            {
                plans.Add(new VirtualCameraAcquisitionPlan(
                    Array.Empty<VirtualCameraSignal>()));
            }
        }

        return plans.AsReadOnly();
    }

    private static IReadOnlyList<VirtualCameraConfigurationPlan> CreateConfigurationPlans(
        CameraConformanceCase testCase, int requestedCount)
    {
        if (testCase == CameraConformanceCase.ConfigurationFailure)
        {
            return new[]
            {
                new VirtualCameraConfigurationPlan(
                    VirtualCameraConfigurationOutcome.ReadBackFailure, TimeSpan.Zero)
            };
        }

        var count = Math.Min(64, Math.Max(2, requestedCount + 1));
        return Enumerable.Repeat(
                new VirtualCameraConfigurationPlan(
                    VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero), count)
            .ToArray();
    }

    private static IReadOnlyList<VirtualCameraOpenOutcome> CreateOpeningOutcomes(
        CameraConformanceCase testCase, int requestedCount)
    {
        var count = Math.Min(64, Math.Max(2, requestedCount + 1));
        return Enumerable.Repeat(VirtualCameraOpenOutcome.Success, count).ToArray();
    }

    private void ReleaseFixture(VirtualConformanceFixture fixture)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeFixture, fixture))
                _activeFixture = null;
        }
    }

    private static IReadOnlyList<FixtureImage> CreateDefinitions()
    {
        var capabilities = new[]
        {
            Definition(VisionPixelFormat.Mono8, null, ProductionAcquisitionMode.SoftwareTrigger,
                "mono8", 0x08u, 1),
            Definition(VisionPixelFormat.Mono16, 10, ProductionAcquisitionMode.SoftwareTrigger,
                "mono16-10", 0x10u, 1),
            Definition(VisionPixelFormat.Mono16, 12, ProductionAcquisitionMode.SoftwareTrigger,
                "mono16-12", 0x12u, 1),
            Definition(VisionPixelFormat.Mono16, 16, ProductionAcquisitionMode.SoftwareTrigger,
                "mono16-16", 0x16u, 1),
            Definition(VisionPixelFormat.Bgr24, null, ProductionAcquisitionMode.HardwareTrigger,
                "bgr24", 0x24u, 2)
        };
        return capabilities;
    }

    private static FixtureImage Definition(VisionPixelFormat format, int? validBits,
        ProductionAcquisitionMode mode, string imageId, uint seedOffset, int rowPadding)
    {
        var image = VirtualCameraImage.CreateSynthetic(
            imageId,
            2,
            2,
            format,
            validBits,
            Seed ^ seedOffset,
            rowPadding);
        var configuration = new RequestedCameraConfiguration(
            mode,
            20,
            0,
            new RegionOfInterest(0, 0, 2, 2),
            format,
            validBits,
            20,
            0,
            format == VisionPixelFormat.Bgr24 ? new WhiteBalanceRgb(1, 1.5, 2) : null);
        var challenge = VirtualCameraImage.CreateSynthetic(imageId + "-challenge", 2, 2,
            format, validBits, Seed ^ seedOffset ^ 0x01010101u, rowPadding);
        return new FixtureImage(image, challenge, configuration, HashValidRows(image), Seed ^ seedOffset);
    }

    private static string CreatePublicFixtureData(IReadOnlyList<FixtureImage> definitions)
    {
        return JsonSerializer.Serialize(new
        {
            schema = "virtual-conformance-fixture-data-v1",
            scriptVersion = FixtureScriptVersion,
            seed = Seed,
            images = definitions.Select(definition => new
            {
                imageId = definition.Image.Id,
                imageSeed = definition.ImageSeed,
                contentHash = definition.Image.ContentHash,
                pixelDataHash = definition.Image.PixelDataHash,
                canonicalFrameHash = definition.CanonicalFrameHash,
                challengeContentHash = definition.ChallengeImage.ContentHash,
                challengeFrameHash = HashValidRows(definition.ChallengeImage)
            }).ToArray()
        });
    }

    private static string HashValidRows(VirtualCameraImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var row = 0; row < image.Height; row++)
            hash.AppendData(image.GetRowSpan(row));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static CameraCapabilities CreateCapabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger,
            ProductionAcquisitionMode.HardwareTrigger },
        new[] { VisionPixelFormat.Mono8, VisionPixelFormat.Mono16, VisionPixelFormat.Bgr24 },
        new[] { 10, 12, 16 },
        new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
        new CameraRoiCapabilities(
            8,
            8,
            new CameraIntCapability(0, 7, 1),
            new CameraIntCapability(0, 7, 1),
            new CameraIntCapability(1, 8, 1),
            new CameraIntCapability(1, 8, 1)),
        new CameraWhiteBalanceCapabilities(
            new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0.5, 2, 0.5, CameraQuantizationMode.Exact)));

    private sealed record FixtureImage(
        VirtualCameraImage Image,
        VirtualCameraImage ChallengeImage,
        RequestedCameraConfiguration Configuration,
        string CanonicalFrameHash,
        uint ImageSeed);
}

internal sealed class VirtualConformanceFixture : ICameraConformanceFixture
{
    private readonly object _lifecycleGate = new();
    private readonly VirtualCameraProvider _provider;
    private readonly VirtualCameraClock _clock;
    private readonly VirtualCameraScenario _scenario;
    private readonly Action _release;
    private readonly bool _hasDisconnect;
    private readonly bool _hasBurst;
    private readonly bool _hasRestore;
    private readonly TaskCompletionSource<object?> _cleanupCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _cleanupTask;
    private bool _disposeRequested;

    internal VirtualConformanceFixture(VirtualCameraProvider provider,
        VirtualCameraClock clock, VirtualCameraScenario scenario,
        RequestedCameraConfiguration configuration, CameraConformanceCase testCase,
        Action release)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _hasDisconnect = scenario.Acquisitions.Any(plan => plan.Signals.Any(signal =>
            signal.Kind == VirtualCameraSignalKind.Disconnect));
        _hasBurst = testCase is CameraConformanceCase.ExtraFrame or
            CameraConformanceCase.CallbackIsolation or CameraConformanceCase.AdapterCallbackBudget;
        _hasRestore = testCase == CameraConformanceCase.DisconnectRecovery;
    }

    public ICameraProvider Provider => _provider;
    public IFrameAcquisitionClock Clock => _clock;
    public string StableDeviceIdentity => _scenario.StableDeviceIdentity;
    public string LogicalCameraRole => "Primary";
    public RequestedCameraConfiguration Configuration { get; }
    public Task CleanupCompletion => _cleanupCompletion.Task;

    public ValueTask<CameraConformanceStimulusReceipt> StimulateAsync(
        CameraConformanceStimulus stimulus,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CameraConformanceStimulusReceipt>(cancellationToken);
        if (stimulus is null)
            throw new ArgumentNullException(nameof(stimulus));
        if (stimulus.Elapsed < TimeSpan.Zero || stimulus.Elapsed > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(stimulus),
                "VirtualConformanceStimulusElapsedInvalid");

        lock (_lifecycleGate)
        {
            EnsureNotDisposed();
            var receipt = StimulateCore(stimulus);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(receipt);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_cleanupTask is null)
            {
                _disposeRequested = true;
                _cleanupTask = DisposeCoreAsync();
            }

            return new ValueTask(_cleanupTask!);
        }
    }

    private CameraConformanceStimulusReceipt StimulateCore(
        CameraConformanceStimulus stimulus)
    {
        switch (stimulus.Kind)
        {
            case CameraConformanceStimulusKind.AdvanceOrWait:
                _clock.AdvanceBy(stimulus.Elapsed);
                return Delivered("VirtualClockAdvanced");

            case CameraConformanceStimulusKind.HardwarePulse:
                if (Configuration.ProductionAcquisitionMode !=
                    ProductionAcquisitionMode.HardwareTrigger)
                    return Unavailable("VirtualHardwareTriggerNotDeclared");
                if (stimulus.Correlation is null)
                    return Unavailable("VirtualHardwarePulseCorrelationRequired");

                var pulse = _provider.PulseHardwareTrigger(
                    StableDeviceIdentity, stimulus.Correlation);
                _clock.AdvanceBy(stimulus.Elapsed);
                return pulse.Succeeded
                    ? Delivered("VirtualHardwarePulseDelivered")
                    : Unavailable(pulse.ReasonCode);

            case CameraConformanceStimulusKind.Disconnect:
                if (!_hasDisconnect)
                    return Unavailable("VirtualDisconnectNotPlanned");
                _clock.AdvanceBy(stimulus.Elapsed);
                return Delivered("VirtualDisconnectDelivered");

            case CameraConformanceStimulusKind.RestoreSameDevice:
                if (!_hasRestore)
                    return Unavailable("VirtualRestoreNotPlanned");
                _clock.AdvanceBy(stimulus.Elapsed);
                return Delivered("VirtualRestoreDelivered");

            case CameraConformanceStimulusKind.EmitBurst:
                if (!_hasBurst)
                    return Unavailable("VirtualBurstNotPlanned");
                _clock.AdvanceBy(stimulus.Elapsed);
                return Delivered("VirtualBurstDelivered");

            case CameraConformanceStimulusKind.CallbackPressure:
                return Unavailable("VirtualCallbackPressureUnavailable");

            default:
                return Unavailable("VirtualStimulusKindUnsupported");
        }
    }

    private CameraConformanceStimulusReceipt Delivered(string reasonCode) =>
        new(CameraConformanceStimulusStatus.Delivered, reasonCode, _clock.GetTimePoint());

    private CameraConformanceStimulusReceipt Unavailable(string reasonCode) =>
        new(CameraConformanceStimulusStatus.Unavailable, reasonCode, _clock.GetTimePoint());

    private void EnsureNotDisposed()
    {
        if (_disposeRequested)
            throw new ObjectDisposedException(nameof(VirtualConformanceFixture));
    }

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _clock.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is null)
        {
            _cleanupCompletion.TrySetResult(null);
            _release();
        }
        else
        {
            _cleanupCompletion.TrySetException(failure);
        }
    }
}
