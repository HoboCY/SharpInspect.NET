using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.PartIdentity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PartIdentityBindingTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task V143_B01_RegistryResolvesOneExactLogicalRoleAndFormat()
    {
        var provider = new StagedPartIdentityProvider(Binding());
        using var registry = new PartIdentityBindingRegistry(new[] { provider });

        var result = await registry.CaptureAsync(Required(), CancellationToken.None);

        Assert.Equal("PartIdentityBindingVerified", result.ReasonCode);
        Assert.NotNull(result.Observation);
        Assert.Same(provider, result.Observation!.Provider);
        Assert.Equal(provider.Binding.ContentHash, result.Observation.Binding.ContentHash);
        Assert.NotEmpty(result.Observation!.StaticHash);
        Assert.NotEmpty(result.Observation!.MaterialHash);
    }

    [Fact]
    public async Task V143_B02_DuplicateProvidersForOneRoleAreRejectedWithoutFallback()
    {
        var first = new StagedPartIdentityProvider(Binding("V143.FirstBinding"));
        var second = new StagedPartIdentityProvider(Binding("V143.SecondBinding"));
        using var registry = new PartIdentityBindingRegistry(new IPartIdentityProvider[] { first, second });

        var result = await registry.CaptureAsync(Required(), CancellationToken.None);

        Assert.Equal("PartIdentityBindingAmbiguous", result.ReasonCode);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task V143_B03_FormatReferenceMismatchIsRejected()
    {
        var provider = new StagedPartIdentityProvider(Binding(format: OtherFormat()));
        using var registry = new PartIdentityBindingRegistry(new[] { provider });

        var result = await registry.CaptureAsync(Required(), CancellationToken.None);

        Assert.Equal("PartIdentityFormatContractMismatch", result.ReasonCode);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task V143_B04_NoneDoesNotReadOrInvokeAProvider()
    {
        var provider = new CountingProvider(Binding());
        using var registry = new PartIdentityBindingRegistry(new IPartIdentityProvider[] { provider });

        var result = await registry.CaptureAsync(PartIdentityRequirement.None, CancellationToken.None);

        Assert.Equal("PartIdentityExplicitNone", result.ReasonCode);
        Assert.Null(result.Observation);
        Assert.Equal(0, provider.CapabilityCalls);
        Assert.Equal(0, provider.LatchCalls);
    }

    [Fact]
    public async Task V143_B05_StaticHashExcludesSourceMaterialButMaterialHashTracksRestart()
    {
        var provider = new StagedPartIdentityProvider(Binding());
        using var registry = new PartIdentityBindingRegistry(new[] { provider });
        var first = await registry.CaptureAsync(Required(), CancellationToken.None);

        Assert.True(provider.Stage(new PartIdentityStageRequest(Guid.NewGuid(), Cycle(), "P-ABC")).Accepted);
        var afterStage = await registry.CaptureAsync(Required(), CancellationToken.None);
        Assert.Equal(first.Observation!.StaticHash, afterStage.Observation!.StaticHash);
        Assert.Equal(first.Observation!.MaterialHash, afterStage.Observation!.MaterialHash);

        var beforeRevision = registry.Revision;
        provider.RestartSource();
        var afterRestart = await registry.CaptureAsync(Required(), CancellationToken.None);

        Assert.Equal(first.Observation!.StaticHash, afterRestart.Observation!.StaticHash);
        Assert.NotEqual(first.Observation!.MaterialHash, afterRestart.Observation!.MaterialHash);
        Assert.True(registry.Revision > beforeRevision);
    }

    [Fact]
    public async Task V143_B06_SourceChangedNotificationInvalidatesRegistryRevision()
    {
        var provider = new StagedPartIdentityProvider(Binding());
        using var registry = new PartIdentityBindingRegistry(new[] { provider });
        _ = await registry.CaptureAsync(Required(), CancellationToken.None);
        var notifications = 0;
        registry.SourceChanged += changed => { Assert.Same(provider, changed); notifications++; };
        var revision = registry.Revision;

        provider.RestartSource();

        Assert.Equal(1, notifications);
        Assert.True(registry.Revision > revision);
    }

    [Fact]
    public async Task V143_B07_SlowCapabilityReadUsesOnePendingOperationAfterTimeout()
    {
        var provider = new SlowCapabilityProvider(Binding(latchTimeout: TimeSpan.FromMilliseconds(100)));
        using var registry = new PartIdentityBindingRegistry(new IPartIdentityProvider[] { provider });

        var first = await registry.CaptureAsync(Required(), CancellationToken.None);
        var second = await registry.CaptureAsync(Required(), CancellationToken.None);

        Assert.Equal("PartIdentityCapabilityTimeout", first.ReasonCode);
        Assert.Equal("PartIdentityCapabilityTimeout", second.ReasonCode);
        Assert.Equal(1, provider.CapabilityCalls);
        provider.Release();
    }

    [Fact]
    public async Task V143_B08_PlcRegistrySecondArgumentBindsTheReadPlanContract()
    {
        var provider = new StableProvider(StableBinding());
        using var matching = new PartIdentityBindingRegistry(new[] { provider }, Hash);
        var accepted = await matching.CaptureAsync(Required(), CancellationToken.None);
        Assert.Equal("PartIdentityBindingVerified", accepted.ReasonCode);

        using var mismatched = new PartIdentityBindingRegistry(new[] { provider }, OtherHash);
        var rejected = await mismatched.CaptureAsync(Required(), CancellationToken.None);
        Assert.Equal("PartIdentityPlcReadPlanMismatch", rejected.ReasonCode);
    }

    [Fact]
    public async Task V143_B09_LateSuccessfulCapabilityReadCannotRestorePreviousSourceReadiness()
    {
        var provider = new LateCapabilityProvider(Binding(latchTimeout: TimeSpan.FromMilliseconds(30)));
        using var registry = new PartIdentityBindingRegistry(new[] { provider });
        var first = await registry.CaptureAsync(Required(), CancellationToken.None);
        Assert.Equal("PartIdentityCapabilityTimeout", first.ReasonCode);
        provider.ReleaseAndRestart();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        PartIdentityBindingReadResult? latest = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            latest = await registry.CaptureAsync(Required(), CancellationToken.None);
            if (latest.Observation is { } observed)
            {
                Assert.Equal(provider.Capabilities.SourceEpoch, observed.Capabilities.SourceEpoch);
                Assert.False(observed.Capabilities.Ready);
                break;
            }
            await Task.Yield();
        }
        Assert.NotNull(latest?.Observation);
        Assert.True(provider.Calls >= 2);
    }

    private static PartIdentityRequirement Required(PartIdentityFormat? format = null) =>
        new(PartIdentityRequirementMode.Required, "PartCode",
            (format ?? Format()).ToContractReference());

    private static PartIdentityFormat Format() => new("V143.PartIdentityFormat", "1", 3, 32,
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-", "P-");

    private static PartIdentityFormat OtherFormat() => new("V143.OtherFormat", "1", 3, 32,
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-", "P-");

    private static PartIdentityProviderBinding Binding(string id = "V143.PartIdentityBinding",
        PartIdentityFormat? format = null, TimeSpan? latchTimeout = null) =>
        new(id, "1", "PartCode", PartIdentityProviderSourceKind.Staged,
            id + ".Provider", "1", Hash, format ?? Format(), TimeSpan.FromSeconds(2),
            latchTimeout ?? TimeSpan.FromSeconds(1), 2);

    private static PartIdentityProviderBinding StableBinding() =>
        new("V143.StableBinding", "1", "PartCode", PartIdentityProviderSourceKind.StablePlc,
            "V143.StablePlc", "1", Hash, Format(), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1), 1);

    private static PartIdentityCycleBinding Cycle() => new(
        Guid.Parse("14314314-1431-4314-8314-143143143143"), Hash, 3, 61, 7);

    private const string OtherHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private sealed class CountingProvider : IPartIdentityProvider
    {
        public CountingProvider(PartIdentityProviderBinding binding)
        {
            Binding = binding;
            Capabilities = new PartIdentityProviderCapabilities(true, true, binding.SourceKind,
                binding.MaximumCallsPerCycle, binding.ContentHash,
                Guid.Parse("14314314-1431-4314-8314-143143143144"), 1);
        }

        public int CapabilityCalls { get; private set; }
        public int LatchCalls { get; private set; }
        public PartIdentityProviderBinding Binding { get; }
        public PartIdentityProviderCapabilities Capabilities { get; }
        public event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged
        {
            add { }
            remove { }
        }

        public ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            CapabilityCalls++;
            return ValueTask.FromResult(Capabilities);
        }

        public ValueTask<PartIdentityProviderObservation> TryLatchAsync(
            PartIdentityLatchRequest request, CancellationToken cancellationToken = default)
        {
            LatchCalls++;
            throw new InvalidOperationException("PartIdentityNoneProviderMustNotBeCalled");
        }
    }

    private sealed class SlowCapabilityProvider : IPartIdentityProvider
    {
        private readonly TaskCompletionSource<PartIdentityProviderCapabilities> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowCapabilityProvider(PartIdentityProviderBinding binding)
        {
            Binding = binding;
            Capabilities = new PartIdentityProviderCapabilities(true, true, binding.SourceKind,
                binding.MaximumCallsPerCycle, binding.ContentHash,
                Guid.Parse("14314314-1431-4314-8314-143143143145"), 1);
        }

        public int CapabilityCalls;
        public PartIdentityProviderBinding Binding { get; }
        public PartIdentityProviderCapabilities Capabilities { get; }
        public event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged
        {
            add { }
            remove { }
        }

        public async ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CapabilityCalls);
            return await _release.Task.ConfigureAwait(false);
        }

        public ValueTask<PartIdentityProviderObservation> TryLatchAsync(
            PartIdentityLatchRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PartIdentitySlowProviderLatchNotExpected");

        public void Release() => _release.TrySetResult(Capabilities);
    }

    private sealed class StableProvider : IPartIdentityProvider
    {
        public StableProvider(PartIdentityProviderBinding binding)
        {
            Binding = binding;
            Capabilities = new PartIdentityProviderCapabilities(true, true, binding.SourceKind,
                binding.MaximumCallsPerCycle, binding.ContentHash,
                Guid.Parse("14314314-1431-4314-8314-143143143146"), 1);
        }

        public PartIdentityProviderBinding Binding { get; }
        public PartIdentityProviderCapabilities Capabilities { get; }
        public event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged
        {
            add { }
            remove { }
        }

        public ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Capabilities);

        public ValueTask<PartIdentityProviderObservation> TryLatchAsync(
            PartIdentityLatchRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PartIdentityStableRegistryDoesNotLatch");
    }

    private sealed class LateCapabilityProvider : IPartIdentityProvider
    {
        private readonly TaskCompletionSource<PartIdentityProviderCapabilities> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PartIdentityProviderCapabilities _original;
        internal int Calls;
        internal LateCapabilityProvider(PartIdentityProviderBinding binding)
        {
            Binding = binding;
            Capabilities = _original = new(true, true, binding.SourceKind, binding.MaximumCallsPerCycle,
                binding.ContentHash, Guid.NewGuid(), 1);
        }
        public PartIdentityProviderBinding Binding { get; }
        public PartIdentityProviderCapabilities Capabilities { get; private set; }
        public event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged;
        public ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref Calls) == 1 ? new(_first.Task) : ValueTask.FromResult(Capabilities);
        public ValueTask<PartIdentityProviderObservation> TryLatchAsync(PartIdentityLatchRequest request,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("UnexpectedLatch");
        internal void ReleaseAndRestart()
        {
            _first.TrySetResult(_original);
            Capabilities = new(false, true, Binding.SourceKind, Binding.MaximumCallsPerCycle, Binding.ContentHash,
                Guid.NewGuid(), 2);
            SourceChanged?.Invoke(this, new(Capabilities));
        }
    }
}
