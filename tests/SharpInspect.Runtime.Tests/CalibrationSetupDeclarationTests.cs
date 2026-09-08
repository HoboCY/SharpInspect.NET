using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationSetupDeclarationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void V123_I01_PhysicalDeclarationRequiresBoundedExplicitFacts(double distance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImagingSetupDefinition("LensA", "Focused", "MountA", distance, "Up"));
        Assert.Throws<ArgumentException>(() => new ImagingSetupDefinition(" ", "Focused", "MountA", 100, "Up"));
        Assert.Throws<ArgumentException>(() => Request(Definition(), " "));
    }

    [Fact]
    public void V123_I02_StepUpTargetBindsEveryPhysicalChangeAndExpectedRevision()
    {
        var request = Request(Definition(), "Adjusted focus");
        var variants = new[]
        {
            Request(new("LensB", "Focused", "MountA", 100, "Up"), request.ChangeReason),
            Request(new("LensA", "Refocused", "MountA", 100, "Up"), request.ChangeReason),
            Request(new("LensA", "Focused", "MountB", 100, "Up"), request.ChangeReason),
            Request(new("LensA", "Focused", "MountA", 101, "Up"), request.ChangeReason),
            Request(new("LensA", "Focused", "MountA", 100, "Rotated"), request.ChangeReason),
            Request(Definition(), "Replaced lens"),
            Request(Definition(), request.ChangeReason, expectedBindingRevision: 2),
            Request(Definition(), request.ChangeReason, expectedRevision: 1, expectedHash: new string('B', 64))
        };
        Assert.All(variants, value => Assert.NotEqual(request.AuthorizationTarget, value.AuthorizationTarget));
        Assert.Equal(request.AuthorizationTarget, Request(Definition(), request.ChangeReason).AuthorizationTarget);
        Assert.Throws<ArgumentException>(() => Request(Definition(), request.ChangeReason, expectedRevision: 1));
        Assert.Throws<ArgumentException>(() => Request(Definition(), request.ChangeReason, expectedHash: new string('B', 64)));
    }

    [Fact]
    public void V123_I03_ImagingRevisionKeepsAttributionAndCannotClaimPhysicalDetection()
    {
        var operation = Guid.NewGuid(); var actor = Guid.NewGuid(); var session = Guid.NewGuid();
        var binding = new CameraBindingRevision(1, "TopCamera", 1, Guid.NewGuid(), null, new string('A', 64),
            new(new("Fixture.Provider", "1", "Fixture.Package", "1"), "DeviceA"), actor, session, 1,
            "Bound camera", DateTimeOffset.UnixEpoch);
        ImagingSetupRevision Revision(CameraBindingRevision selectedBinding, Guid author) => new(1,
            "TopCamera", 1, operation, null, selectedBinding, Definition(), ImagingSetupChangeOrigin.OperatorDeclared,
            author, session, 1, "Adjusted focus", DateTimeOffset.UnixEpoch);
        var original = Revision(binding, actor);
        Assert.Equal(operation, original.RevisionId);
        Assert.Equal(actor, original.ActorPrincipalId);
        Assert.False(original.AutomaticallyDetectsAllPhysicalChanges);
        Assert.False(original.ProductionReady);
        Assert.True(original.InvalidatesEarlierCalibrationProfiles);
        Assert.NotEqual(original.RevisionHash, Revision(binding with { Revision = 2 }, actor).RevisionHash);
        Assert.NotEqual(original.RevisionHash, Revision(binding, Guid.NewGuid()).RevisionHash);
    }

    private static ImagingSetupDefinition Definition() => new("LensA", "Focused", "MountA", 100, "Up");
    private static ImagingSetupChangeRequest Request(ImagingSetupDefinition definition, string reason,
        long expectedBindingRevision = 1, long expectedRevision = 0, string? expectedHash = null) =>
        new(Guid.NewGuid(), new(CommandSource.PhysicalConsole), "TopCamera", expectedBindingRevision,
            new string('A', 64), expectedRevision, expectedHash, definition, reason);
}
