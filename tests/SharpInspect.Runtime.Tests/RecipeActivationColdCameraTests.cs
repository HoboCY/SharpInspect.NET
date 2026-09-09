using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Cameras;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Restart recovery tests use a fresh CameraSetupRuntime and a read-only
/// persisted closed projection.  The projection is deliberately not treated as
/// proof that a provider handle is closed.
/// </summary>
public sealed partial class RecipeActivationColdCameraTests
{
    [Fact]
    public async Task V132_Z01_NoPreviousActiveActuallyOpensStopsAndClosesCandidate()
    {
        var providerIdentity = ProviderIdentity();
        var target = new CameraBindingTarget(providerIdentity, "Camera:Candidate");
        var binding = Binding("Primary", target, 1);
        var candidate = new FakeDevice(
            new CameraDeviceDescriptor(providerIdentity, target.StableDeviceIdentity, "candidate"),
            Capabilities());
        var provider = new FakeProvider(providerIdentity, candidate);
        var persisted = new FakePersistence(new Dictionary<string, CameraSetupSnapshot>
        {
            ["Primary"] = ClosedSnapshot("Primary", binding, Request(20))
        });
        var published = new ConcurrentQueue<CameraSetupSnapshot>();
        await using var runtime = CreateRuntime(provider, persisted, published);
        var release = Release("Candidate", "Primary", Request(20));
        var admitted = Admitted(release, previous: null);

        var result = await runtime.RecoverRecipeActivationAfterRestartAsync(
            admitted, release, previousSnapshot: null);

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal("NoPreviousBaselineClosed", result.ReasonCode);
        Assert.True(result.HardwareTouched);
        Assert.Equal(1, provider.OpenCount);
        Assert.Equal(1, candidate.HealthReadCount);
        Assert.Equal(1, candidate.StopCount);
        Assert.Equal(1, candidate.DisposeCount);
        var safeClosed = published.Last();
        Assert.Equal(CameraConnectionState.Closed, safeClosed.Health.Connection);
        Assert.Equal(CameraConfigurationState.Unconfigured, safeClosed.Health.Configuration);
        Assert.Null(safeClosed.Effective);
    }

    [Fact]
    public async Task V132_Z02_PreviousActiveReopensAppliesReadsBackAndKeepsCameraOpen()
    {
        var providerIdentity = ProviderIdentity();
        var target = new CameraBindingTarget(providerIdentity, "Camera:Primary");
        var binding = Binding("Primary", target, 1);
        var device = new FakeDevice(
            new CameraDeviceDescriptor(providerIdentity, target.StableDeviceIdentity, "primary"),
            Capabilities());
        var provider = new FakeProvider(providerIdentity, device);
        var request = Request(30);
        var release = Release("Previous", "Primary", request);
        var baseline = PreviousSnapshot(release, binding, request);
        var persisted = new FakePersistence(new Dictionary<string, CameraSetupSnapshot>
        {
            ["Primary"] = ClosedSnapshot("Primary", binding, request)
        });
        var published = new ConcurrentQueue<CameraSetupSnapshot>();
        await using var runtime = CreateRuntime(provider, persisted, published);
        var admitted = Admitted(release, baseline);

        var result = await runtime.RecoverRecipeActivationAfterRestartAsync(
            admitted, release, baseline);

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal("CameraActivationRestored", result.ReasonCode);
        Assert.True(result.HardwareTouched);
        Assert.Equal(1, provider.OpenCount);
        Assert.Equal(2, device.HealthReadCount);
        Assert.Equal(1, device.ApplyCount);
        Assert.Equal(0, device.StopCount);
        Assert.Equal(0, device.DisposeCount);
        Assert.Equal(request.ExposureTimeUs, result.Snapshot!.Effective!.ExposureTimeUs);
        Assert.Equal(CameraConfigurationState.Applied, result.Snapshot.Health.Configuration);
        Assert.False(device.Disposed);
    }

    [Fact]
    public async Task V132_Z03_DifferentRoleClosesCandidateBeforeRestoringPreviousRole()
    {
        var providerIdentity = ProviderIdentity();
        var candidateTarget = new CameraBindingTarget(providerIdentity, "Camera:Candidate");
        var previousTarget = new CameraBindingTarget(providerIdentity, "Camera:Previous");
        var candidateBinding = Binding("Candidate", candidateTarget, 1);
        var previousBinding = Binding("Previous", previousTarget, 1);
        var candidateDevice = new FakeDevice(
            new CameraDeviceDescriptor(providerIdentity, candidateTarget.StableDeviceIdentity, "candidate"),
            Capabilities());
        var previousDevice = new FakeDevice(
            new CameraDeviceDescriptor(providerIdentity, previousTarget.StableDeviceIdentity, "previous"),
            Capabilities());
        var provider = new FakeProvider(providerIdentity, candidateDevice, previousDevice);
        var candidateRelease = Release("Candidate", "Candidate", Request(30));
        var previousRelease = Release("Previous", "Previous", Request(20));
        var baseline = PreviousSnapshot(previousRelease, previousBinding, Request(20));
        var persisted = new FakePersistence(new Dictionary<string, CameraSetupSnapshot>
        {
            ["Candidate"] = ClosedSnapshot("Candidate", candidateBinding, Request(30)),
            ["Previous"] = ClosedSnapshot("Previous", previousBinding, Request(20))
        });
        var published = new ConcurrentQueue<CameraSetupSnapshot>();
        await using var runtime = CreateRuntime(provider, persisted, published);
        var admitted = Admitted(candidateRelease, baseline);

        var result = await runtime.RecoverRecipeActivationAfterRestartAsync(
            admitted, candidateRelease, baseline);

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal("CameraActivationRestored", result.ReasonCode);
        Assert.Equal(2, provider.OpenCount);
        Assert.Equal(1, candidateDevice.StopCount);
        Assert.Equal(1, candidateDevice.DisposeCount);
        Assert.Equal(1, previousDevice.ApplyCount);
        Assert.Equal(0, previousDevice.StopCount);
        Assert.Contains(published, value => value.LogicalRole == "Candidate" &&
            value.Health.Connection == CameraConnectionState.Closed);
        Assert.Contains(published, value => value.LogicalRole == "Previous" &&
            value.Health.Configuration == CameraConfigurationState.Applied);
    }

    [Fact]
    public async Task V132_Z04_CloseFailureLeavesUnknownAndDoesNotRestoreAnotherDevice()
    {
        var providerIdentity = ProviderIdentity();
        var target = new CameraBindingTarget(providerIdentity, "Camera:Candidate");
        var binding = Binding("Primary", target, 1);
        var candidate = new FakeDevice(
            new CameraDeviceDescriptor(providerIdentity, target.StableDeviceIdentity, "candidate"),
            Capabilities())
        {
            ThrowOnDispose = true
        };
        var unexpectedRestore = new FakeDevice(
            new CameraDeviceDescriptor(providerIdentity, target.StableDeviceIdentity, "restore"),
            Capabilities());
        var provider = new FakeProvider(providerIdentity, candidate, unexpectedRestore);
        var persisted = new FakePersistence(new Dictionary<string, CameraSetupSnapshot>
        {
            ["Primary"] = ClosedSnapshot("Primary", binding, Request(20))
        });
        var published = new ConcurrentQueue<CameraSetupSnapshot>();
        await using var runtime = CreateRuntime(provider, persisted, published,
            operationTimeout: TimeSpan.FromMilliseconds(250));
        var release = Release("Candidate", "Primary", Request(20));

        var result = await runtime.RecoverRecipeActivationAfterRestartAsync(
            Admitted(release, previous: null), release, previousSnapshot: null);

        Assert.False(result.Succeeded);
        Assert.Equal("CameraDeviceCleanupRequired", result.ReasonCode);
        Assert.True(result.HardwareTouched);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(unexpectedRestore.Disposed);
        var failed = published.Last();
        Assert.Equal(CameraConfigurationState.Unknown, failed.Health.Configuration);
    }

    private static CameraSetupRuntime CreateRuntime(FakeProvider provider,
        FakePersistence persistence, ConcurrentQueue<CameraSetupSnapshot> published,
        TimeSpan? operationTimeout = null)
    {
        var station = new CameraSetupRuntime.CameraStationContext(Guid.NewGuid(), false,
            ProductionArmState.Disarmed, false, null, 0, HandshakePhase.Idle,
            ExclusiveMode.None, RecoveryState.None, false, null, false, false);
        return new CameraSetupRuntime(new[] { provider }, new CameraSetupOptions
        {
            OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(2),
            ShutdownTimeout = TimeSpan.FromSeconds(2)
        }, audit: null, sessions: null, identityQuery: null, readStation: () => station,
            publishSetup: (_, snapshot) => published.Enqueue(snapshot), persistence: persistence);
    }

    private static RecipeActivationRecord Admitted(RecipeReleaseRecord release,
        RecipeActivationSnapshot? previous)
    {
        var activationId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var session = Guid.NewGuid();
        var previousReference = previous is null ? null :
            new RecipeActivationReference(1, Guid.NewGuid(), Hash('P'));
        var auth = new RecipeContractReference("Cold.Authorization", "1", Hash('A'));
        var recorded = DateTimeOffset.UtcNow;
        var admission = new RecipeActivationAdmission(1, activationId, attemptId, operationId,
            release.Recipe, release.ReleaseId, release.ContentHash, null,
            previousReference, previous?.Recipe,
            previous?.ContentHash, calibrationSelections: null, "cold-recovery", principal,
            session, 1, auth, Hash('T'), RecipeActivationEvidenceKind.InternalContractFixture,
            recorded);
        return new RecipeActivationRecord(1, activationId, attemptId, operationId,
            admissionReference: null, previousReference, previous?.Recipe, previous?.ContentHash,
            release.Recipe, release.ReleaseId, release.ContentHash, resultingRecipe: null,
            new RecipeActivationOutcome(RecipeActivationOutcomeState.Admitted,
                "RecipeActivationAdmitted"), Array.Empty<RecipeActivationCheck>(),
            new RecipeActivationRestoration(RecipeActivationRestorationState.NotRequired,
                "RecipeActivationHardwareUntouched"), successfulSnapshot: null,
            RecipeActivationEvidenceKind.InternalContractFixture, principal, session, 1, auth,
            "cold-recovery", Hash('T'), recorded, admission);
    }

    private static RecipeActivationSnapshot PreviousSnapshot(RecipeReleaseRecord release,
        CameraBindingRevision binding, RequestedCameraConfiguration request)
    {
        var camera = new CameraSetupSnapshot("" + binding.LogicalRole, binding,
            ConfiguredHealth(), request, Capabilities().ValidateConfiguration(request).Effective,
            Array.Empty<CameraConfigurationDifference>(), reasonCode: "CameraActivationRestored",
            capabilities: Capabilities());
        var execution = new AlgorithmExecutionPolicy("Cold.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
        var schema = new AlgorithmResultSchema("Cold.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(),
            new OverlayContract("Cold.Overlay", "1", 1, 1, 1));
        var plc = new PlcResultContract("Cold.Plc", "1", 1024, 1,
            Array.Empty<PlcFrameworkFieldMapping>(), new[]
            {
                new PlcResultSchemaMap(new RecipeContractReference(schema.Id, schema.Version,
                    schema.ContentHash))
            });
        var proof = new PlcResultSchemaValidation(plc, schema,
            new[] { new PlcResultValidationCheck("ColdProof", "schema", true, "Passed") });
        var plcBinding = new PlcResultContractBinding(release.Recipe,
            release.Source.Content.Algorithm.Algorithm, proof);
        return new RecipeActivationSnapshot(RecipeActivationEvidenceKind.InternalContractFixture,
            release, Guid.NewGuid(), execution, camera, plcBinding, null, 1, 1);
    }

    private static RecipeReleaseRecord Release(string suffix, string role,
        RequestedCameraConfiguration request)
    {
        var content = Content(suffix, role, request);
        var source = new RecipeDraftRevision(1, Guid.NewGuid(), 1, Guid.NewGuid(), null,
            Hash(suffix[0]), content, Guid.NewGuid(), Guid.NewGuid(), 1,
            "cold-source", DateTimeOffset.UtcNow);
        var policy = new RecipeGovernancePolicy("Cold.Release." + suffix, "1",
            RecipeGovernanceMode.SingleApproverRelease);
        var checks = new[]
        {
            new RecipeReleaseValidationCheck("Structure", "recipe", true, "Passed")
        };
        var changes = new[]
        {
            new RecipeReleaseChange("Camera/Configuration", "old", "new",
                source.AuthorPrincipalId, source.DraftId, source.Revision)
        };
        return new RecipeReleaseRecord(1, Guid.NewGuid(), Guid.NewGuid(), 1, source, policy,
            checks, changes, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            new RecipeContractReference("Cold.Authorization", "1", Hash('R')),
            "cold release", Hash('U'), DateTimeOffset.UtcNow);
    }

    private static RecipeDraftContent Content(string suffix, string role,
        RequestedCameraConfiguration request)
    {
        var configurationSchema = new AlgorithmConfigurationSchema("Cold.Config." + suffix, "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema,
            Array.Empty<AlgorithmConfigurationEntry>());
        var overlay = new OverlayContract("Cold.Content.Overlay." + suffix, "1", 1, 1, 1);
        var result = new AlgorithmResultSchema("Cold.Content.Result." + suffix, "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlay);
        var binding = new RecipeAlgorithmBinding(
            new AlgorithmIdentity("Cold.Content.Algorithm." + suffix, "1"),
            configurationSchema, new RecipeContractReference(result.Id, result.Version,
                result.ContentHash), new RecipeContractReference(overlay.Id, overlay.Version,
                overlay.ContentHash));
        return new RecipeDraftContent("Cold.Recipe." + suffix, "cold recipe " + suffix,
            binding, configuration, role, request, TimeSpan.FromMilliseconds(100), null, null);
    }

    private static CameraSetupSnapshot ClosedSnapshot(string role,
        CameraBindingRevision binding, RequestedCameraConfiguration request) =>
        new(role, binding, new CameraHealthSnapshot(CameraProviderAvailability.Available,
            CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
            CameraAcquisitionState.Stopped, Now()), request, reasonCode: "CameraSetupRestarted");

    private static CameraBindingRevision Binding(string role, CameraBindingTarget target,
        long revision) => new(1, role, revision, Guid.NewGuid(), null, Hash('B'), target,
        Guid.NewGuid(), Guid.NewGuid(), 1, "cold-binding", DateTimeOffset.UtcNow);

    private static RequestedCameraConfiguration Request(double exposure) => new(
        ProductionAcquisitionMode.SoftwareTrigger, exposure, 0,
        new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null, 100, 0, null);

    private static CameraHealthSnapshot ConfiguredHealth() => new(
        CameraProviderAvailability.Available, CameraConnectionState.Open,
        CameraConfigurationState.Applied, CameraAcquisitionState.Stopped, Now());

    private static FrameTimePoint Now() =>
        new(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp()));

    private static CameraCapabilities Capabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
        new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
        new CameraRoiCapabilities(640, 480,
            new CameraIntCapability(0, 636, 4), new CameraIntCapability(0, 476, 4),
            new CameraIntCapability(4, 640, 4), new CameraIntCapability(4, 480, 4)));

    private static CameraProviderIdentity ProviderIdentity() =>
        new("V133.Test.Camera.Provider", "1", "V133.Test.Camera.Package", "1");

    private static string Hash(char value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(new string(value, 64))));

    private sealed class FakePersistence : ICameraSetupPersistence
    {
        private readonly IReadOnlyDictionary<string, CameraSetupSnapshot> _snapshots;

        internal FakePersistence(IReadOnlyDictionary<string, CameraSetupSnapshot> snapshots) =>
            _snapshots = snapshots;

        public ValueTask<CameraSetupPersistentState> ReadCameraSetupAsync(string logicalRole,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_snapshots.TryGetValue(logicalRole, out var snapshot)
                ? new CameraSetupPersistentState(snapshot, false, "CameraSetupRestarted")
                : new CameraSetupPersistentState(null, false, "CameraSetupUnconfigured"));

        public ValueTask<StoreWriteResult> AppendCameraSetupAsync(
            CameraSetupPersistenceRequest request, StoreDeadline deadline,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProvider : ICameraProvider
    {
        private readonly Queue<ICameraDevice> _devices;
        private int _openCount;

        internal FakeProvider(CameraProviderIdentity identity, params ICameraDevice[] devices)
        { Identity = identity; _devices = new Queue<ICameraDevice>(devices); }

        public CameraProviderIdentity Identity { get; }
        internal int OpenCount => Volatile.Read(ref _openCount);
        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCount);
            return _devices.Count == 0
                ? ValueTask.FromResult(CameraOpenResult.Failure("NoTestDevice"))
                : ValueTask.FromResult(CameraOpenResult.Success(_devices.Dequeue()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDevice : ICameraDevice
    {
        private CameraConfigurationState _configuration = CameraConfigurationState.Unconfigured;
        private bool _stopped;
        private readonly CameraCapabilities _capabilities;

        internal FakeDevice(CameraDeviceDescriptor descriptor, CameraCapabilities capabilities)
        { Descriptor = descriptor; _capabilities = capabilities; }

        public CameraDeviceDescriptor Descriptor { get; }
        public CameraCapabilities Capabilities => ThrowOnCapabilitiesAfterConfiguredHealth && HealthReadCount >= 2
            ? throw new InvalidOperationException("SnapshotCapabilitiesUnavailable") : _capabilities;
        internal int HealthReadCount { get; private set; }
        internal int ApplyCount { get; private set; }
        internal int StopCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool ThrowOnDispose { get; set; }
        internal bool ThrowOnCapabilitiesAfterConfiguredHealth { get; set; }

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            HealthReadCount++;
            return new(CameraProviderAvailability.Available,
                _stopped ? CameraConnectionState.Closed : CameraConnectionState.Open,
                _stopped ? CameraConfigurationState.Unconfigured : _configuration,
                CameraAcquisitionState.Stopped, Now());
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            var expected = Capabilities.ValidateConfiguration(requested);
            if (!expected.Succeeded || expected.Effective is null)
                return ValueTask.FromResult(expected);
            _configuration = CameraConfigurationState.Applied;
            return ValueTask.FromResult(CameraConfigurationResult.Success(expected.Effective,
                expected.Differences));
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Success());

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.NotStarted, "NotUsed")));

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default)
        {
            StopCount++;
            _stopped = true;
            return ValueTask.FromResult(CameraOperationResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (ThrowOnDispose) throw new InvalidOperationException("DisposeFailed");
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
