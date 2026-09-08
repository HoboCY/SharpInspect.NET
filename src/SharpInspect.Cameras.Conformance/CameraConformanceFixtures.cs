using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Conformance;

public enum CameraConformanceCase
{
    DiscoveryIdentity, ConfigurationReadBack, ConfigurationFailure,
    SoftwareAcquisition, HardwareAcquisition, CanonicalMemory,
    Cancellation, ExtraFrame, Timeout, DisconnectRecovery,
    LeaseLifetime, PoolExhaustion, CallbackIsolation, AdapterCallbackBudget,
    NativeAllocationBounds
}

public enum CameraConformanceStimulusKind
{
    AdvanceOrWait, HardwarePulse, Disconnect, RestoreSameDevice, EmitBurst, CallbackPressure
}

public enum CameraConformanceStimulusStatus { Delivered, Unavailable }

/// <summary>Fixture facts frozen before execution; none is a conformance verdict.</summary>
public sealed class CameraConformanceFixtureDeclaration
{
    private readonly byte[] _canonicalBytes;

    public CameraConformanceFixtureDeclaration(string fixtureId, string fixtureVersion,
        CameraProviderIdentity provider, string stableDeviceIdentity,
        IEnumerable<RequestedCameraConfiguration> configurations,
        IEnumerable<string> canonicalFrameHashes, int poolCapacity,
        uint seed, TimeSpan frameDelay, string publicFixtureData,
        IEnumerable<string>? optionalExtensions = null,
        IEnumerable<string>? challengeFrameHashes = null)
    {
        FixtureId = Identifier(fixtureId, nameof(fixtureId));
        FixtureVersion = Identifier(fixtureVersion, nameof(fixtureVersion));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        StableDeviceIdentity = Identifier(stableDeviceIdentity, nameof(stableDeviceIdentity));
        ArgumentNullException.ThrowIfNull(configurations);
        var copied = new List<RequestedCameraConfiguration>();
        foreach (var configuration in configurations)
        {
            if (configuration is null || copied.Count == 10)
                throw new ArgumentException("CameraConformanceConfigurationLimit", nameof(configurations));
            if (copied.Contains(configuration))
                throw new ArgumentException("CameraConformanceConfigurationDuplicate", nameof(configurations));
            copied.Add(configuration);
        }
        if (copied.Count == 0) throw new ArgumentException("CameraConformanceConfigurationRequired", nameof(configurations));
        Configurations = new ReadOnlyCollection<RequestedCameraConfiguration>(copied);
        ArgumentNullException.ThrowIfNull(canonicalFrameHashes);
        var hashes = new List<string>();
        foreach (var hash in canonicalFrameHashes)
        {
            if (hash is null || hash.Length != 64 || hashes.Count == 10 ||
                hash.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                throw new ArgumentException("CameraConformancePixelHashInvalid", nameof(canonicalFrameHashes));
            hashes.Add(hash);
        }
        if (hashes.Count != copied.Count)
            throw new ArgumentException("CameraConformancePixelHashMappingIncomplete", nameof(canonicalFrameHashes));
        CanonicalFrameHashes = new ReadOnlyCollection<string>(hashes);
        var challengeHashes = (challengeFrameHashes ?? hashes).Take(11).ToArray();
        if (challengeHashes.Length != copied.Count || challengeHashes.Any(hash => hash is null || hash.Length != 64 ||
            hash.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F'))))
            throw new ArgumentException("CameraConformanceChallengeHashInvalid", nameof(challengeFrameHashes));
        ChallengeFrameHashes = Array.AsReadOnly(challengeHashes);
        if (poolCapacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(poolCapacity));
        if (frameDelay <= TimeSpan.Zero || frameDelay > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(frameDelay));
        if (publicFixtureData is null || Encoding.UTF8.GetByteCount(publicFixtureData) > 8192)
            throw new ArgumentException("CameraConformanceFixtureDataLimit", nameof(publicFixtureData));
        PoolCapacity = poolCapacity;
        Seed = seed;
        FrameDelay = frameDelay;
        PublicFixtureData = publicFixtureData;
        var extensions = (optionalExtensions ?? Array.Empty<string>()).Take(17)
            .Select(value => Identifier(value, nameof(optionalExtensions))).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (extensions.Length > 16 || extensions.Distinct(StringComparer.Ordinal).Count() != extensions.Length)
            throw new ArgumentException("CameraConformanceExtensionDeclarationInvalid", nameof(optionalExtensions));
        OptionalExtensions = Array.AsReadOnly(extensions);
        _canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "camera-conformance-fixture-v1", FixtureId, FixtureVersion,
            Provider, StableDeviceIdentity, Configurations, CanonicalFrameHashes, ChallengeFrameHashes, PoolCapacity, Seed,
            frameDelayTicks = FrameDelay.Ticks, PublicFixtureData, OptionalExtensions
        });
        if (_canonicalBytes.Length > 24 * 1024)
            throw new ArgumentException("CameraConformanceFixtureDeclarationLimit");
        ContentHash = Convert.ToHexString(SHA256.HashData(_canonicalBytes));
    }

    public string FixtureId { get; }
    public string FixtureVersion { get; }
    public CameraProviderIdentity Provider { get; }
    public string StableDeviceIdentity { get; }
    public IReadOnlyList<RequestedCameraConfiguration> Configurations { get; }
    /// <summary>SHA-256 of concatenated valid canonical rows, in Configurations order; excludes padding.</summary>
    public IReadOnlyList<string> CanonicalFrameHashes { get; }
    /// <summary>Second, different image for cancellation, extra-frame and retained-lease integrity checks.</summary>
    public IReadOnlyList<string> ChallengeFrameHashes { get; }
    public int PoolCapacity { get; }
    public uint Seed { get; }
    public TimeSpan FrameDelay { get; }
    public string PublicFixtureData { get; }
    public IReadOnlyList<string> OptionalExtensions { get; }
    public string ContentHash { get; }
    public byte[] GetCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    private static string Identifier(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => char.IsControl(character)))
            throw new ArgumentException("CameraConformanceIdentifierInvalid", parameter);
        return value;
    }
}

public sealed record CameraConformanceFixtureRequest(CameraConformanceCase Case,
    RequestedCameraConfiguration Configuration, int AcquisitionCount);

public sealed record CameraConformanceStimulus(CameraConformanceStimulusKind Kind,
    TimeSpan Elapsed, ExecutionCorrelationId? Correlation = null);

/// <summary>Delivery acknowledgement only. Product observations come from public camera/Runtime APIs.</summary>
public sealed record CameraConformanceStimulusReceipt(CameraConformanceStimulusStatus Status,
    string ReasonCode, FrameTimePoint ObservedAt);

public interface ICameraConformanceFixtureFactory
{
    CameraConformanceFixtureDeclaration Declaration { get; }
    ValueTask<ICameraConformanceFixture> CreateAsync(CameraConformanceFixtureRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Trusted external test rig. It may use public simulator stimuli or physical rig
/// controls, never private provider methods/fields. It cannot supply a test verdict.
/// The factory retains resources until CleanupCompletion confirms actual retirement;
/// a timeout or a caller-facing Dispose result must not permit overlapping owners.
/// </summary>
public interface ICameraConformanceFixture : IAsyncDisposable
{
    ICameraProvider Provider { get; }
    IFrameAcquisitionClock Clock { get; }
    string StableDeviceIdentity { get; }
    string LogicalCameraRole { get; }
    RequestedCameraConfiguration Configuration { get; }
    Task CleanupCompletion { get; }
    ValueTask<CameraConformanceStimulusReceipt> StimulateAsync(CameraConformanceStimulus stimulus,
        CancellationToken cancellationToken = default);
}
