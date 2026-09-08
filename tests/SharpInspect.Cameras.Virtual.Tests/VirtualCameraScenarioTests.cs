using System.Security.Cryptography;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class VirtualCameraScenarioTests
{
    [Fact]
    public void V116_S01_ScenarioFreezesInputsAndCanonicalizesImageCatalogOrder()
    {
        var a = Image("a"); var b = Image("b");
        var images = new[] { a, b };
        var signals = new[] { Frame("a", 1), Frame("b", 2) };
        var plan = new VirtualCameraAcquisitionPlan(signals);
        var plans = new[] { plan };
        var first = Scenario(images: images, acquisitions: plans);
        var second = Scenario(images: new[] { b, a }, acquisitions: new[] { plan });
        images[0] = Image("replaced"); signals[0] = Frame("replaced", 1);
        plans[0] = new(Array.Empty<VirtualCameraSignal>());

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal("a", first.Images[0].Id);
        Assert.Equal("a", first.Acquisitions[0].Signals[0].ImageId);
        Assert.Equal(2, first.Acquisitions[0].Signals.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<VirtualCameraImage>)first.Images).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<VirtualCameraSignal>)plan.Signals).Clear());
    }

    [Fact]
    public void V116_S02_ContentHashBindsIdentitySeedCapabilitiesImageAndEveryScriptAxis()
    {
        var baseline = Scenario();
        var changed = new[]
        {
            Scenario(id: "other"), Scenario(version: "2"), Scenario(seed: 43),
            Scenario(device: "virtual:other"), Scenario(model: "相机模型"),
            Scenario(capabilities: Capabilities(exposureMaximum: 20)),
            Scenario(images: new[] { Image("a", seed: 2) }),
            Scenario(acquisitions: new[] { new VirtualCameraAcquisitionPlan(new[] { Frame("a", 2) }) }),
            Scenario(acquisitions: new[] { new VirtualCameraAcquisitionPlan(new[]
                { Frame("a", 1, VirtualFrameAssociation.Uncorrelated) }) }),
            Scenario(acquisitions: new[] { new VirtualCameraAcquisitionPlan(new[]
                { new VirtualCameraSignal(TimeSpan.FromMilliseconds(1), VirtualCameraSignalKind.Disconnect) }) }),
            Scenario(configurations: new[] { new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero) }),
            Scenario(configurations: new[] { new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.ReadBackFailure, TimeSpan.FromMilliseconds(1)) }),
            Scenario(opens: new[] { VirtualCameraOpenOutcome.Success }),
            Scenario(opens: new[] { VirtualCameraOpenOutcome.DeviceMissing }),
            Scenario(unsolicited: new[] { Frame("a", 1, VirtualFrameAssociation.Uncorrelated) })
        };
        Assert.Matches("^[0-9A-F]{64}$", baseline.ContentHash);
        Assert.Equal(baseline.ContentHash, Scenario().ContentHash);
        Assert.All(changed, scenario => Assert.NotEqual(baseline.ContentHash, scenario.ContentHash));
        Assert.Equal(changed.Length, changed.Select(scenario => scenario.ContentHash).Distinct().Count());
    }

    [Fact]
    public void V116_S03_RecordedSourceHashSurvivesCanonicalPixelEquivalence()
    {
        var path = Path.Combine(Path.GetTempPath(), "SharpInspect.Scenario." + Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            var bytes = new byte[] { 1, 2, 99, 3, 4, 99 };
            File.WriteAllBytes(path, bytes);
            var expected = Convert.ToHexString(SHA256.HashData(bytes));
            var recorded = VirtualCameraImage.LoadRecordedRaw("a", path, 2, 2, 3,
                VisionPixelFormat.Mono8, null, expected);
            var memory = new VirtualCameraImage("a", 2, 2, 3, VisionPixelFormat.Mono8, null, bytes);
            Assert.Equal(recorded.ContentHash, memory.ContentHash);
            Assert.Equal(recorded.PixelDataHash, memory.PixelDataHash);
            Assert.NotEqual(Scenario(images: new[] { recorded }).ContentHash,
                Scenario(images: new[] { memory }).ContentHash);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void V116_S04_MissingImagesAmbiguousIdsAndUnorderedSignalsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Scenario(images: Array.Empty<VirtualCameraImage>()));
        Assert.Throws<ArgumentException>(() => Scenario(images: new[] { Image("a"), Image("a") }));
        Assert.Throws<ArgumentException>(() => Scenario(acquisitions: new[]
            { new VirtualCameraAcquisitionPlan(new[] { Frame("missing", 0) }) }));
        Assert.Throws<ArgumentException>(() => new VirtualCameraAcquisitionPlan(new[] { Frame("a", 2), Frame("a", 1) }));
        Assert.Throws<ArgumentException>(() => Scenario(unsolicited: new[] { Frame("a", 1) }));
        Assert.Throws<ArgumentException>(() => Scenario(unsolicited: new[]
            { Frame("a", 2, VirtualFrameAssociation.Uncorrelated), Frame("a", 1, VirtualFrameAssociation.Uncorrelated) }));
        Assert.Throws<ArgumentException>(() => new VirtualCameraSignal(TimeSpan.Zero,
            VirtualCameraSignalKind.Disconnect, "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Frame("a", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Frame("a", 600001));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scenario(opens: new[] { (VirtualCameraOpenOutcome)99 }));
    }

    [Fact]
    public void V116_S05_CapacityChecksStopEnumerationAtFirstExcessItem()
    {
        static IEnumerable<T> OneTooMany<T>(T value, int maximum)
        {
            for (var index = 0; index <= maximum; index++) yield return value;
            throw new InvalidOperationException("MustNotEnumerateAfterBound");
        }
        var image = Image("a"); var signal = Frame("a", 0);
        var plan = new VirtualCameraAcquisitionPlan(new[] { signal });
        Assert.Throws<ArgumentException>(() => Scenario(images: OneTooMany(image, 64)));
        Assert.Throws<ArgumentException>(() => Scenario(acquisitions: OneTooMany(plan, 64)));
        Assert.Throws<ArgumentException>(() => new VirtualCameraAcquisitionPlan(OneTooMany(signal, 32)));
        Assert.Throws<ArgumentException>(() => Scenario(configurations: OneTooMany(
            new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success, TimeSpan.Zero), 64)));
        Assert.Throws<ArgumentException>(() => Scenario(opens: OneTooMany(VirtualCameraOpenOutcome.Success, 64)));
        Assert.Throws<ArgumentException>(() => Scenario(unsolicited: OneTooMany(
            Frame("a", 0, VirtualFrameAssociation.Uncorrelated), 16)));
        var largePlan = new VirtualCameraAcquisitionPlan(Enumerable.Repeat(signal, 32));
        Assert.Throws<ArgumentException>(() => Scenario(acquisitions: new[] { largePlan, largePlan, plan }));
    }

    [Fact]
    public void V116_S06_EmptyFiniteAcquisitionAndSameTimeSignalsRemainExplicit()
    {
        var empty = Scenario(acquisitions: Array.Empty<VirtualCameraAcquisitionPlan>());
        Assert.Empty(empty.Acquisitions);
        var timeout = new VirtualCameraAcquisitionPlan(Array.Empty<VirtualCameraSignal>());
        var simultaneous = new VirtualCameraAcquisitionPlan(new[] { Frame("a", 0), Frame("a", 0) });
        var scenario = Scenario(acquisitions: new[] { timeout, simultaneous });
        Assert.Empty(scenario.Acquisitions[0].Signals);
        Assert.Equal(2, scenario.Acquisitions[1].Signals.Count);
        Assert.NotEqual(empty.ContentHash, scenario.ContentHash);
        var swapped = Scenario(acquisitions: new[] { simultaneous, timeout });
        Assert.NotEqual(scenario.ContentHash, swapped.ContentHash);
    }

    [Fact]
    public void V116_S07_PreallocatedCapacityIncludesSharedPoolMono16Alignment()
    {
        var mono16 = VirtualCameraImage.CreateSynthetic("a", 3, 2, VisionPixelFormat.Mono16, 12, 7, 1);
        var scenario = Scenario(images: new[] { mono16 });
        Assert.Equal(14, scenario.ImageBytes);
        Assert.Equal(16, scenario.MaximumFrameBytes);
    }

    [Fact]
    public void V116_S08_ReportedModelFitsTheSharedProvenanceUtf8Boundary()
    {
        var scenario = Scenario(model: new string('机', 42));
        Assert.Equal(42, scenario.ReportedModel!.Length);
        Assert.Throws<ArgumentException>(() => Scenario(model: new string('机', 43)));
        Assert.Throws<ArgumentException>(() => Scenario(model: new string('a', 129)));
    }

    private static VirtualCameraImage Image(string id, uint seed = 1) =>
        VirtualCameraImage.CreateSynthetic(id, 2, 2, VisionPixelFormat.Mono8, null, seed, 1);
    private static VirtualCameraSignal Frame(string image, int delay,
        VirtualFrameAssociation association = VirtualFrameAssociation.CurrentRequest) =>
        new(TimeSpan.FromMilliseconds(delay), VirtualCameraSignalKind.Frame, image, association);
    private static CameraCapabilities Capabilities(double exposureMaximum = 10) => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { VisionPixelFormat.Mono8, VisionPixelFormat.Mono16 }, new[] { 10, 12, 16 },
        new(1, exposureMaximum, 1, CameraQuantizationMode.Exact),
        new(0, 10, 1, CameraQuantizationMode.Exact),
        new(0, 10, 1, CameraQuantizationMode.Exact),
        new(8, 8, new(0, 7, 1), new(0, 7, 1), new(1, 8, 1), new(1, 8, 1)));
    private static VirtualCameraScenario Scenario(string id = "fixture", string version = "1",
        uint seed = 42, string device = "virtual:one", string? model = null,
        CameraCapabilities? capabilities = null, IEnumerable<VirtualCameraImage>? images = null,
        IEnumerable<VirtualCameraAcquisitionPlan>? acquisitions = null,
        IEnumerable<VirtualCameraConfigurationPlan>? configurations = null,
        IEnumerable<VirtualCameraOpenOutcome>? opens = null,
        IEnumerable<VirtualCameraSignal>? unsolicited = null) => new(id, version, seed, device,
        capabilities ?? Capabilities(), images ?? new[] { Image("a") },
        acquisitions ?? new[] { new VirtualCameraAcquisitionPlan(new[] { Frame("a", 1) }) },
        configurations, opens, unsolicited, model);
}
