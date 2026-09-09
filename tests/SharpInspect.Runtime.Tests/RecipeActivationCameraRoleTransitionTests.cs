using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationColdCameraTests
{
    [Fact]
    public async Task V132_Z07_UnexpectedColdSnapshotFailureRetiresTheUninstalledDevice()
    {
        var identity = ProviderIdentity();
        var target = new CameraBindingTarget(identity, "Camera:Previous");
        var binding = Binding("Primary", target, 1);
        var candidate = Release("Candidate", "Primary", Request(30));
        var prior = PreviousSnapshot(Release("Previous", "Primary", Request(20)), binding, Request(20));
        var opened = new FakeDevice(new(identity, target.StableDeviceIdentity, "restore"), Capabilities())
        { ThrowOnCapabilitiesAfterConfiguredHealth = true };
        var provider = new FakeProvider(identity, opened);
        var persistence = new FakePersistence(new Dictionary<string, CameraSetupSnapshot>
        { ["Primary"] = ClosedSnapshot("Primary", binding, Request(20)) });
        await using var camera = CreateRuntime(provider, persistence, new());
        var result = await camera.RecoverRecipeActivationAfterRestartAsync(Admitted(candidate, prior), candidate, prior);
        Assert.False(result.Succeeded);
        Assert.True(result.HardwareTouched);
        Assert.Equal(2, opened.HealthReadCount);
        Assert.Equal(1, opened.ApplyCount);
        Assert.Equal(1, opened.StopCount);
        Assert.Equal(1, opened.DisposeCount);
        Assert.True(opened.Disposed);
        Assert.True(camera.IsActivationRestorationBlocked("Primary"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V132_Z05_LiveRoleSwitchClosesPreviousBeforeCandidateAndCanRestoreIt(bool rollback)
    {
        var identity = ProviderIdentity();
        var priorTarget = new CameraBindingTarget(identity, "Camera:Previous");
        var candidateTarget = new CameraBindingTarget(identity, "Camera:Candidate");
        var priorBinding = Binding("Previous", priorTarget, 1);
        var candidateBinding = Binding("Candidate", candidateTarget, 1);
        var prior = new FakeDevice(new(identity, priorTarget.StableDeviceIdentity, "prior"), Capabilities());
        var candidate = new FakeDevice(new(identity, candidateTarget.StableDeviceIdentity, "candidate"), Capabilities());
        var replacement = new FakeDevice(new(identity, priorTarget.StableDeviceIdentity, "restored"), Capabilities());
        var provider = new FakeProvider(identity, prior, candidate, replacement);
        var persistence = new FakePersistence(new Dictionary<string, CameraSetupSnapshot>
        {
            ["Previous"] = ClosedSnapshot("Previous", priorBinding, Request(20)),
            ["Candidate"] = ClosedSnapshot("Candidate", candidateBinding, Request(30))
        });
        var published = new ConcurrentQueue<CameraSetupSnapshot>();
        await using var camera = CreateRuntime(provider, persistence, published);
        CameraSetupSnapshot baseline;
        await using (var initial = await camera.ReserveRecipeActivationAsync("Previous"))
        {
            var prepared = await initial.ApplyAsync(Request(20));
            Assert.True(prepared.Succeeded, prepared.ReasonCode);
            baseline = prepared.Snapshot!;
            Assert.True(initial.Commit());
        }
        await using var transition = await camera.ReserveRecipeActivationAsync("Candidate");
        var applied = await transition.ApplyAsync(Request(30), durableBaseline: baseline);
        Assert.True(applied.Succeeded, applied.ReasonCode);
        Assert.Equal(1, prior.StopCount);
        Assert.Equal(1, prior.DisposeCount);
        Assert.True(prior.Disposed);
        Assert.Equal(2, provider.OpenCount);
        Assert.Contains(published, value => value.LogicalRole == "Previous" &&
            value.Health.Connection == CameraConnectionState.Closed);
        if (!rollback)
        {
            Assert.True(transition.Commit());
            Assert.False(candidate.Disposed);
            Assert.Equal(0, replacement.ApplyCount);
            return;
        }
        var restored = await transition.RestoreAsync(baseline);
        Assert.True(restored.Succeeded, restored.ReasonCode);
        Assert.Equal("Previous", restored.Snapshot!.LogicalRole);
        Assert.Equal(baseline.Effective, restored.Snapshot.Effective);
        Assert.Equal(1, candidate.StopCount);
        Assert.Equal(1, candidate.DisposeCount);
        Assert.Equal(1, replacement.ApplyCount);
        Assert.Equal(2, replacement.HealthReadCount);
        Assert.False(replacement.Disposed);
        Assert.Equal(3, provider.OpenCount);
    }

    [Fact]
    public async Task V132_Z06_FailedPreviousRoleRetirementForbidsCandidateOpenAndCommit()
    {
        var identity = ProviderIdentity();
        var priorTarget = new CameraBindingTarget(identity, "Camera:Previous");
        var candidateTarget = new CameraBindingTarget(identity, "Camera:Candidate");
        var priorBinding = Binding("Previous", priorTarget, 1);
        var candidateBinding = Binding("Candidate", candidateTarget, 1);
        var prior = new FakeDevice(new(identity, priorTarget.StableDeviceIdentity, "prior"), Capabilities());
        var candidate = new FakeDevice(new(identity, candidateTarget.StableDeviceIdentity, "candidate"), Capabilities());
        var provider = new FakeProvider(identity, prior, candidate);
        var persistence = new FakePersistence(new Dictionary<string, CameraSetupSnapshot>
        {
            ["Previous"] = ClosedSnapshot("Previous", priorBinding, Request(20)),
            ["Candidate"] = ClosedSnapshot("Candidate", candidateBinding, Request(30))
        });
        await using var camera = CreateRuntime(provider, persistence, new(), TimeSpan.FromMilliseconds(250));
        CameraSetupSnapshot baseline;
        await using (var initial = await camera.ReserveRecipeActivationAsync("Previous"))
        {
            var prepared = await initial.ApplyAsync(Request(20));
            Assert.True(prepared.Succeeded, prepared.ReasonCode);
            baseline = prepared.Snapshot!;
            Assert.True(initial.Commit());
        }
        await using var transition = await camera.ReserveRecipeActivationAsync("Candidate");
        prior.ThrowOnDispose = true;
        try
        {
            var failed = await transition.ApplyAsync(Request(30), durableBaseline: baseline);
            Assert.False(failed.Succeeded);
            Assert.True(failed.HardwareTouched);
            Assert.False(transition.Commit());
            var restore = await transition.RestoreAsync(baseline);
            Assert.False(restore.Succeeded);
            Assert.Equal(1, provider.OpenCount);
            Assert.Equal(0, candidate.ApplyCount);
        }
        finally { prior.ThrowOnDispose = false; }
    }
}
