using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class DiagnosticCapturePipelineTests
{
    internal static LoggingDiagnosticsPolicy Policy(int events = 1000, int queueSize = 16, LoggingDiagnosticsPolicy? baseline = null)
    {
        var old = baseline ?? DiagnosticPipelineTests.Policy(events: events, normal: queueSize);
        var debug = new DiagnosticEventContract("Runtime.Detail", 1, "Runtime", DiagnosticLevel.Debug, false,
            new[] { new DiagnosticFieldContract("State", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe,
                true, null, null, new[] { "Idle", "Busy" }),
                new DiagnosticFieldContract("VendorState", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Protected,
                    false, null, null, new[] { "Known" }) });
        return new(old.Id, old.Version, old.ApprovalReference, old.TracePolicyVersion, old.TracePolicySnapshotHash,
            old.Baseline, old.Contracts.Concat(new[] { debug }), old.Producers, old.SafeQueue, old.ProtectedQueue,
            old.ForwardedQueue, old.SafeFiles, old.ProtectedFiles, old.MaximumDropsPerHealthWindow, old.HealthWindow,
            old.FlushTimeout, old.MaximumQueryRecords, old.MaximumQueryBytes, old.MaximumCaptureDuration, old.MaximumCaptureEvents);
    }
    private static DiagnosticSupportAuthority Authority() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
    private static DiagnosticCaptureLease Capture(LoggingDiagnosticsPolicy policy, int cap = 10, Func<long>? clock = null) =>
        new(Guid.NewGuid(), new(policy.ContentHash, new[] { "Runtime" }, DiagnosticLevel.Debug,
            TimeSpan.FromSeconds(5), cap), Authority(), clock);
    private static DiagnosticEmissionRequest Request() => new("Runtime.Detail", 1, new DiagnosticPropertyRequest("State", "Idle"));

    [Fact, Trait("VerificationId", "V158_P01")]
    public async Task V158_P01_OnlyMatchingElevationEnablesDebugAndNthAttemptReturnsToBaseline()
    {
        var policy = Policy(); var records = new ConcurrentQueue<DiagnosticEnvelope>();
        await using var pipeline = new DiagnosticPipeline(policy, Guid.NewGuid(),
            item => { records.Enqueue(item); return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask);
        Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(Request()));
        var capture = Capture(policy, 2); Assert.True(pipeline.BeginCapture(capture));
        Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(Request()));
        Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(Request()));
        Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(Request()));
        Assert.True(await pipeline.FlushAsync()); Assert.Equal(2, capture.Attempts);
        Assert.Equal(DiagnosticCaptureEnd.EventLimit, capture.End); Assert.True(capture.Drained);
        Assert.Equal(2, records.Count);
        foreach (var envelope in records)
        {
            Assert.Equal(capture.Id, envelope.Record.CaptureSessionId);
            var decoded = DiagnosticJson.Decode(Encoding.UTF8.GetString(envelope.Line), policy, false);
            Assert.NotNull(decoded); Assert.Equal(DiagnosticLevel.Debug, decoded.Level);
            Assert.Equal(capture.Profile.ContentHash, decoded.CaptureProfileHash);
        }
    }

    [Fact, Trait("VerificationId", "V158_P02")]
    public async Task V158_P02_SecretCanariesConsumeAttemptWithoutReachingAnySink()
    {
        var policy = Policy(); var safe = new ConcurrentQueue<DiagnosticEnvelope>();
        var protectedRecords = new ConcurrentQueue<DiagnosticEnvelope>();
        await using var pipeline = new DiagnosticPipeline(policy, Guid.NewGuid(),
            item => { safe.Enqueue(item); return ValueTask.CompletedTask; },
            item => { protectedRecords.Enqueue(item); return ValueTask.CompletedTask; });
        var capture = Capture(policy, 3); Assert.True(pipeline.BeginCapture(capture));
        foreach (var request in new[] {
            new DiagnosticEmissionRequest("Runtime.Detail", 1, new DiagnosticPropertyRequest("State", "SECRET-CANARY")),
            new DiagnosticEmissionRequest("Runtime.Detail", 1, new DiagnosticPropertyRequest("Password", "SECRET-CANARY")),
            new DiagnosticEmissionRequest("Runtime.Detail", 1, new DiagnosticPropertyRequest("State", "Idle"),
                new DiagnosticPropertyRequest("VendorState", "SECRET-CANARY")) })
            Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(request));
        Assert.Equal(3, capture.Attempts); Assert.Equal(DiagnosticCaptureEnd.EventLimit, capture.End);
        Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(Request()));
        Assert.True(await pipeline.FlushAsync()); Assert.Empty(safe); Assert.Empty(protectedRecords);
    }

    [Fact, Trait("VerificationId", "V158_P03")]
    public void V158_P03_MonotonicDeadlineAndSynchronousAuthorityRevocationStopAdmissions()
    {
        var clock = 100L; var capture = Capture(Policy(), clock: () => clock);
        Assert.True(capture.TryEnter()); capture.ReleaseReference();
        clock += Stopwatch.Frequency * 5;
        Assert.False(capture.TryEnter()); Assert.Equal(DiagnosticCaptureEnd.TimeLimit, capture.End);
        var locked = Capture(Policy()); locked.Authority.Revoke();
        Assert.False(locked.TryEnter()); Assert.Equal(DiagnosticCaptureEnd.AuthorityLost, locked.End);
        Assert.True(locked.Drained);
    }

    [Fact, Trait("VerificationId", "V158_P04")]
    public void V158_P04_ConcurrentAttemptsCannotExceedTheSessionCap()
    {
        var capture = Capture(Policy(), 99); var accepted = 0;
        Parallel.For(0, 1000, _ => { if (capture.TryEnter()) { Interlocked.Increment(ref accepted); capture.ReleaseReference(); } });
        Assert.Equal(99, accepted); Assert.Equal(99, capture.Attempts); Assert.True(capture.Drained);
    }

    [Fact, Trait("VerificationId", "V158_P05")]
    public async Task V158_P05_StoppedCaptureCannotBeReplacedUntilPhysicalSinkRetires()
    {
        var policy = Policy(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        await using var pipeline = new DiagnosticPipeline(policy, Guid.NewGuid(),
            _ => { entered.Set(); release.Wait(); return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask);
        try
        {
            var capture = Capture(policy); Assert.True(pipeline.BeginCapture(capture));
            Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(Request()));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            pipeline.EndCapture(DiagnosticCaptureEnd.Stopped);
            Assert.False(capture.Drained); Assert.False(pipeline.BeginCapture(Capture(policy)));
            Assert.False(await pipeline.FlushAsync()); Assert.False(capture.Drained);
            release.Set(); Assert.True(SpinWait.SpinUntil(() => capture.Drained, TimeSpan.FromSeconds(2)));
            Assert.True(pipeline.BeginCapture(Capture(policy)));
        }
        finally { release.Set(); }
    }

    [Fact, Trait("VerificationId", "V158_P06")]
    public async Task V158_P06_ElevationDoesNotResetExistingProducerBudget()
    {
        var policy = Policy(events: 1); var records = new ConcurrentQueue<DiagnosticEnvelope>();
        await using var pipeline = new DiagnosticPipeline(policy, Guid.NewGuid(),
            item => { records.Enqueue(item); return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask);
        Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(Request())); // baseline rejection still charges its producer
        var capture = Capture(policy); Assert.True(pipeline.BeginCapture(capture));
        Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(Request()));
        Assert.Equal(1, capture.Attempts); Assert.True(await pipeline.FlushAsync()); Assert.Empty(records);
    }

    [Fact, Trait("VerificationId", "V158_P07")]
    public async Task V158_P07_PhysicalSinkFailureImmediatelyRevokesCaptureAndPermanentlyRefusesReplacement()
    {
        var policy = Policy();
        await using var pipeline = new DiagnosticPipeline(policy, Guid.NewGuid(),
            _ => throw new IOException("unclassified-private-error"), _ => ValueTask.CompletedTask);
        var capture = Capture(policy); Assert.True(pipeline.BeginCapture(capture));
        Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(Request()));
        Assert.True(SpinWait.SpinUntil(() => capture.End == DiagnosticCaptureEnd.Fault, TimeSpan.FromSeconds(2)));
        Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(Request()));
        Assert.True(SpinWait.SpinUntil(() => capture.Drained, TimeSpan.FromSeconds(2)));
        Assert.False(pipeline.BeginCapture(Capture(policy)));
    }

    [Fact, Trait("VerificationId", "V158_P09")]
    public async Task V158_P09_ClosedAlgorithmScopeStillConsumesCaptureAttemptsWithoutWriting()
    {
        var policy = Policy(); var records = new ConcurrentQueue<DiagnosticEnvelope>();
        await using var pipeline = new DiagnosticPipeline(policy, Guid.NewGuid(),
            item => { records.Enqueue(item); return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask);
        var capture = new DiagnosticCaptureLease(Guid.NewGuid(), new(policy.ContentHash,
            new[] { "Algorithm" }, DiagnosticLevel.Debug, TimeSpan.FromSeconds(5), 2), Authority());
        Assert.True(pipeline.BeginCapture(capture));
        var scope = pipeline.OpenScope(new(ExecutionKind.Manual, Guid.NewGuid()), new());
        scope.Seal();
        var value = new AlgorithmDiagnosticEvent("Algorithm.Measurement", new[] {
            new AlgorithmDiagnosticField("Result", AlgorithmScalarValue.FromEnum("Pass")) });
        for (var index = 0; index < 3; index++)
            Assert.Equal(AlgorithmDiagnosticEmission.Dropped, scope.TryEmit(value));
        Assert.Equal(2, capture.Attempts); Assert.Equal(DiagnosticCaptureEnd.EventLimit, capture.End);
        Assert.True(capture.Drained); Assert.True(await pipeline.FlushAsync()); Assert.Empty(records);
    }

    [Fact, Trait("VerificationId", "V158_P08")]
    public void V158_P08_ProducerPausedAcrossDeadlineCannotUseEarlierClockSample()
    {
        var samples = 0;
        var capture = Capture(Policy(), clock: () => Interlocked.Increment(ref samples) < 3 ? 0 : Stopwatch.Frequency * 5);
        Assert.False(capture.TryEnter()); Assert.Equal(DiagnosticCaptureEnd.TimeLimit, capture.End);
        Assert.Equal(0, capture.PhysicalReferences); Assert.True(capture.Drained);
    }
}
