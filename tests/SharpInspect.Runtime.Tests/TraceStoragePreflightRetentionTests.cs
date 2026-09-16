using SharpInspect.Abstractions;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class TraceStoragePreflightRetentionTests
{
    [Theory, Trait("VerificationId", "V155_P02")]
    [InlineData(TraceCheckpointStatus.Completed, true, TraceStoragePreflightStatus.Passed)]
    [InlineData(TraceCheckpointStatus.Completed, false, TraceStoragePreflightStatus.Mismatch)]
    [InlineData(TraceCheckpointStatus.Awaiting, true, TraceStoragePreflightStatus.Missing)]
    [InlineData(TraceCheckpointStatus.BudgetExceeded, true, TraceStoragePreflightStatus.Failed)]
    [InlineData(TraceCheckpointStatus.Unknown, true, TraceStoragePreflightStatus.Failed)]
    public void V155_P02_PreflightDistinguishesApprovedCheckpointFromUnknownOrPriorPolicy(
        TraceCheckpointStatus status, bool matches, TraceStoragePreflightStatus expected)
    {
        var current = Current();
        var checkpoint = new TraceCheckpointObservation(status, "ControlledCheckpoint", DateTimeOffset.UtcNow,
            matches ? current.Snapshot!.ContentHash : new string('F', 64), null, null, null, null);
        var report = Evaluate(current, checkpoint, new(1, 0, 0, null));
        var row = Assert.Single(report.Rows, row => row.Gate == TraceStoragePreflightGate.Checkpoint);
        Assert.Equal(expected, row.Status);
        Assert.NotEqual(TraceStoragePreflightStatus.NotImplemented, row.Status);
        Assert.False(report.CanAdmitProduction); // Unrelated unimplemented qualification gates stay closed.
    }

    [Theory, Trait("VerificationId", "V155_P03")]
    [InlineData(-1, TraceStoragePreflightStatus.Missing)]
    [InlineData(0, TraceStoragePreflightStatus.Passed)]
    [InlineData(1, TraceStoragePreflightStatus.Failed)]
    public void V155_P03_PreflightShowsActualImageBacklogAndRejectsUnknownOrAtLimit(
        int state, TraceStoragePreflightStatus expected)
    {
        var current = Current();
        var count = state == 1 ? current.Publication!.Policy.ImageBacklog.MaximumItems : state;
        var images = new ImageBacklogSnapshot(1, count, 0, count > 0 ? DateTimeOffset.UtcNow.AddSeconds(-1) : null);
        var report = Evaluate(current, null, images);
        var row = Assert.Single(report.Rows, row => row.Gate == TraceStoragePreflightGate.ImageBacklog);
        Assert.Equal(expected, row.Status);
        if (state < 0) Assert.Null(row.Observed);
        Assert.False(report.CanAdmitProduction);
    }

    private static TraceStoragePreflightReport Evaluate(TraceStoragePolicyReadResult current,
        TraceCheckpointObservation? checkpoint, ImageBacklogSnapshot images) =>
        TraceStoragePreflightEvaluator.Evaluate(current, new("preflight", "1", Array.Empty<TraceStorageRouteIdentity>()),
            new(true, "VolumeObserved", TotalBytes: 100L << 30, FreeBytes: 50L << 30, WalBytes: 0),
            null, DateTimeOffset.UtcNow, retentionConfigured: true, checkpoint: checkpoint,
            imageConfigured: true, imageBacklog: images);

    private static TraceStoragePolicyReadResult Current()
    {
        var publication = new TraceStoragePolicyPublication(1, TraceStoragePolicyRuntimeTests.Policy(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        return new(true, "Published", publication, new(publication));
    }
}
