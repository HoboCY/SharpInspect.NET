using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class DiagnosticPipelineTests
{
    [Fact, Trait("VerificationId", "V156_C05")]
    public void V156_C05_IntegerBoundsDoNotRoundLargeInputValuesIntoAnApprovedRange()
    {
        const long maximum = 9223372036854774784;
        var schema = new DiagnosticEventContract("Large.Integer", 1, "Runtime", DiagnosticLevel.Information, false,
            new[] { new DiagnosticFieldContract("Count", DiagnosticScalarKind.Int64, DiagnosticDataClass.Safe, true, 0, maximum, null) });
        var classifier = new DiagnosticClassifier(Policy());
        Assert.True(classifier.TryClassifyProjection(new("Large.Integer", 1, new DiagnosticPropertyRequest("Count", maximum)), schema, out _));
        Assert.False(classifier.TryClassifyProjection(new("Large.Integer", 1, new DiagnosticPropertyRequest("Count", maximum + 1)), schema, out _));
    }

    [Fact, Trait("VerificationId", "V156_B04")]
    public async Task V156_B04_HighSeverityHasReservedCapacityAndCancelledFlushReturnsFalse()
    {
        using var release = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        await using var pipeline = new DiagnosticPipeline(Policy(normal: 1, reserved: 1), Guid.NewGuid(),
            _ => { entered.Set(); release.Wait(); return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask);
        try
        {
            Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(Request()));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(Request()));
            Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(new("Runtime.DiagnosticDrops", 1, new DiagnosticPropertyRequest("Count", 1L))));
            Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(new("Runtime.DiagnosticDrops", 1, new DiagnosticPropertyRequest("Count", 2L))));
            Assert.Equal(2, pipeline.ReadHealth().Safe!.QueuedRecords);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            Assert.False(await pipeline.FlushAsync(cancel.Token));
        }
        finally { release.Set(); }
    }

    internal static LoggingDiagnosticsPolicy Policy(int events = 1000, long bytes = 1_000_000,
        int rate = 1000, int active = 4, int normal = 4, int reserved = 2, int maxRecord = 4096,
        TimeSpan? timeout = null, string? traceHash = null, string traceVersion = "v1",
        TimeSpan? safeRetention = null, TimeSpan? protectedRetention = null)
    {
        var producer = new DiagnosticProducerBudget(events, bytes, 32, 128, rate, TimeSpan.FromSeconds(10), active);
        var queue = new DiagnosticQueueBudget(normal, normal * 8192, reserved, reserved * 8192,
            DiagnosticLevel.Warning, timeout ?? TimeSpan.FromSeconds(2));
        var safe = new DiagnosticFileBudget(maxRecord, 65536, 4, 262144, TimeSpan.FromSeconds(1), safeRetention ?? TimeSpan.FromDays(7));
        var protectedFiles = new DiagnosticFileBudget(maxRecord, 65536, 4, 262144, TimeSpan.FromSeconds(1), protectedRetention ?? TimeSpan.FromDays(1));
        var sample = new DiagnosticEventContract("Algorithm.Measurement", 1, "Algorithm", DiagnosticLevel.Information, true, new[]
        {
            new DiagnosticFieldContract("Result", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true, null, null, new[] { "Pass", "Fail" }),
            new DiagnosticFieldContract("Count", DiagnosticScalarKind.Int64, DiagnosticDataClass.Safe, false, 0, 100, null),
            new DiagnosticFieldContract("VendorState", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Protected, false, null, null, new[] { "Idle", "Busy" }),
            new DiagnosticFieldContract("Forbidden", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Prohibited, false, null, null, new[] { "Bait" })
        });
        return new("test", "v1", "test-approval", traceVersion, traceHash ?? new string('A', 64), DiagnosticLevel.Information,
            DiagnosticStandardContracts.All.Concat(new[] { sample }), producer, queue, queue, queue, safe, protectedFiles,
            10, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), 100, 65536, TimeSpan.FromMinutes(5), 1000);
    }

    private static DiagnosticEmissionRequest Request(params DiagnosticPropertyRequest[] extra) =>
        new("Algorithm.Measurement", 1, new[] { new DiagnosticPropertyRequest("Result", "Pass") }.Concat(extra).ToArray());

    [Fact, Trait("VerificationId", "V156_C01")]
    public async Task V156_C01_UnknownObjectsSecretsAndHugeValuesNeverReachAnySerializerOrSink()
    {
        var safe = new ConcurrentQueue<DiagnosticEnvelope>(); var protectedRecords = new ConcurrentQueue<DiagnosticEnvelope>();
        await using var pipeline = new DiagnosticPipeline(Policy(), Guid.NewGuid(),
            value => { safe.Enqueue(value); return ValueTask.CompletedTask; },
            value => { protectedRecords.Enqueue(value); return ValueTask.CompletedTask; });
        var trap = new Trap();
        var rejected = new[]
        {
            Request(new DiagnosticPropertyRequest("Unknown", trap)), Request(new DiagnosticPropertyRequest("Result", trap)),
            new DiagnosticEmissionRequest("Algorithm.Measurement", 1, new DiagnosticPropertyRequest("Result", "PASSWORD-SECRET-BAIT")),
            new DiagnosticEmissionRequest("Algorithm.Measurement", 1, new DiagnosticPropertyRequest("Result", new string('S', 4_000_000))),
            Request(new DiagnosticPropertyRequest("VendorState", "TOKEN-BAIT")), Request(new DiagnosticPropertyRequest("Count", double.NaN)),
            Request(new DiagnosticPropertyRequest("Count", 101L)), Request(new DiagnosticPropertyRequest("Count", new byte[1024])), Request(new DiagnosticPropertyRequest("Forbidden", "Bait")),
            new DiagnosticEmissionRequest("Unknown.Event", 1, new DiagnosticPropertyRequest("Value", trap)),
            new DiagnosticEmissionRequest("Algorithm.Measurement", 2, new DiagnosticPropertyRequest("Result", "Pass"))
        };
        foreach (var request in rejected) Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(request));
        foreach (var key in new[] { "password", "Token", "recovery_code", "privateKey", "verifier", "rawPartIdentity",
            "imageBytes", "recipePayload", "databasePages", "memoryDump" })
            Assert.Equal(DiagnosticEmission.Dropped, pipeline.TryEmit(Request(new DiagnosticPropertyRequest(key, "SECRET-BAIT"))));
        Assert.True(await pipeline.FlushAsync());
        Assert.Empty(safe); Assert.Empty(protectedRecords); Assert.Equal(0, trap.Reads);
        Assert.True(pipeline.ReadHealth().Rejected >= rejected.Length);
        var classifier = new DiagnosticClassifier(Policy());
        foreach (var name in new[] { "VendorState", "ExceptionCategory", "NetworkAddress", "FilePath", "SerialNumber", "HResult", "AccessTokenHash" })
        {
            var schema = new DiagnosticEventContract("Unsafe.Policy", 1, "Runtime", DiagnosticLevel.Information, false,
                new[] { new DiagnosticFieldContract(name, DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true, null, null, new[] { "Bait" }) });
            Assert.False(classifier.TryClassifyProjection(new("Unsafe.Policy", 1, new DiagnosticPropertyRequest(name, "Bait")), schema, out _));
        }
    }

    [Fact, Trait("VerificationId", "V156_C02")]
    public async Task V156_C02_ClassificationPrecedesFanoutAndUsesRuntimeCorrelation()
    {
        var safe = new ConcurrentQueue<DiagnosticEnvelope>(); var protectedRecords = new ConcurrentQueue<DiagnosticEnvelope>();
        var forwarded = new RecordingSink(); var epoch = Guid.NewGuid(); var execution = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        await using var pipeline = new DiagnosticPipeline(Policy(), epoch,
            value => { safe.Enqueue(value); return ValueTask.CompletedTask; },
            value => { protectedRecords.Enqueue(value); return ValueTask.CompletedTask; }, forwarded);
        var counter = new DiagnosticDropCounter(); var scope = pipeline.OpenScope(execution, counter);
        Assert.Equal(AlgorithmDiagnosticEmission.Accepted, scope.TryEmit(new("Algorithm.Measurement", new[]
        {
            new AlgorithmDiagnosticField("Result", AlgorithmScalarValue.FromEnum("Pass")),
            new AlgorithmDiagnosticField("VendorState", AlgorithmScalarValue.FromString("Busy"))
        })));
        Assert.True(await pipeline.FlushAsync());
        var safeRecord = Assert.Single(safe); var protectedRecord = Assert.Single(protectedRecords);
        Assert.Equal(epoch, safeRecord.Record.RuntimeEpoch); Assert.Equal(execution, safeRecord.Record.Execution);
        Assert.Equal(safeRecord.Record.EventId, protectedRecord.Record.EventId);
        Assert.DoesNotContain("VendorState", Encoding.UTF8.GetString(safeRecord.Line));
        Assert.DoesNotContain("Busy", Encoding.UTF8.GetString(safeRecord.Line));
        Assert.Equal("VendorState", Assert.Single(protectedRecord.Record.Properties).Name);
        Assert.Equal("Result", Assert.Single(Assert.Single(forwarded.Records).Properties).Name);
        scope.Seal(); Assert.Equal(0, pipeline.ReadHealth().ActiveExecutionScopes);
        Assert.Equal(AlgorithmDiagnosticEmission.Dropped, scope.TryEmit(new("Algorithm.Measurement")));
        Assert.Equal(1, pipeline.ReadHealth().LateDropped); Assert.Equal(0, counter.Value);
        Assert.All(typeof(ExecutionDiagnosticScope).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => Assert.DoesNotContain("Attempt", field.FieldType.Name));
    }

    [Fact, Trait("VerificationId", "V156_C03")]
    public async Task V156_C03_OwnerObservesAnExceptionOnceWithoutReadingItsVirtualText()
    {
        var safe = new ConcurrentQueue<DiagnosticEnvelope>(); var protectedRecords = new ConcurrentQueue<DiagnosticEnvelope>();
        await using var pipeline = new DiagnosticPipeline(Policy(), Guid.NewGuid(),
            value => { safe.Enqueue(value); return ValueTask.CompletedTask; },
            value => { protectedRecords.Enqueue(value); return ValueTask.CompletedTask; });
        var fault = new TrapException(); var correlation = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
        pipeline.ObserveException(fault, "AlgorithmExecution", correlation);
        pipeline.ObserveException(fault, "ManagedHost");
        Assert.True(await pipeline.FlushAsync());
        var ordinary = Assert.Single(safe); var protectedRecord = Assert.Single(protectedRecords);
        Assert.Equal(correlation, ordinary.Record.Execution); Assert.Equal(0, fault.Reads);
        Assert.DoesNotContain("HResult", Encoding.UTF8.GetString(ordinary.Line));
        Assert.Equal("HResult", Assert.Single(protectedRecord.Record.Properties).Name);
        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(protectedRecord.Line));
    }

    [Fact, Trait("VerificationId", "V156_B01")]
    public async Task V156_B01_SynchronousHungSinkRetainsOnePhysicalSlotAndDoesNotBlockProducer()
    {
        using var release = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        var calls = 0;
        var pipeline = new DiagnosticPipeline(Policy(timeout: TimeSpan.FromMilliseconds(60)), Guid.NewGuid(),
            _ => { Interlocked.Increment(ref calls); entered.Set(); release.Wait(); return ValueTask.CompletedTask; },
            _ => ValueTask.CompletedTask);
        try
        {
            Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(Request()));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++) pipeline.TryEmit(Request());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            var health = pipeline.ReadHealth();
            Assert.Equal(DiagnosticSinkState.Unavailable, health.Safe!.State);
            Assert.Equal(1, health.Safe.PhysicalCalls); Assert.Equal(1, health.Safe.QueuedRecords);
            Assert.InRange(health.Safe.QueuedBytes, 1, 4096); Assert.Equal(1, calls);
            Assert.True(health.SaturationDropped + health.QuotaDropped > 0);
            watch.Restart(); await pipeline.DisposeAsync(); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(1, pipeline.ReadHealth().Safe!.PhysicalCalls);
        }
        finally { release.Set(); await pipeline.DisposeAsync(); }
    }

    [Fact, Trait("VerificationId", "V156_B02")]
    public async Task V156_B02_FaultingForwarderDoesNotPoisonTheIndependentLocalOutputs()
    {
        var safe = new ConcurrentQueue<DiagnosticEnvelope>(); var protectedRecords = new ConcurrentQueue<DiagnosticEnvelope>();
        await using var pipeline = new DiagnosticPipeline(Policy(), Guid.NewGuid(),
            item => { safe.Enqueue(item); return ValueTask.CompletedTask; },
            item => { protectedRecords.Enqueue(item); return ValueTask.CompletedTask; }, new ThrowingSink());
        Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(Request(new DiagnosticPropertyRequest("VendorState", "Busy"))));
        await pipeline.FlushAsync();
        Assert.Single(safe); Assert.Single(protectedRecords);
        Assert.Equal(DiagnosticSinkState.Healthy, pipeline.ReadHealth().Safe!.State);
        Assert.Equal(DiagnosticSinkState.Unavailable, pipeline.ReadHealth().Forwarded!.State);
        Assert.Equal(DiagnosticEmission.Accepted, pipeline.TryEmit(Request()));
    }

    [Fact, Trait("VerificationId", "V156_B03")]
    public async Task V156_B03_EventByteRateAndActiveScopeBudgetsAreIndependent()
    {
        foreach (var policy in new[] { Policy(events: 1), Policy(bytes: 1), Policy(rate: 1) })
        {
            await using var pipeline = new DiagnosticPipeline(policy, Guid.NewGuid(), _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            var scope = pipeline.OpenScope(new(ExecutionKind.Manual, Guid.NewGuid()), new());
            var value = new AlgorithmDiagnosticEvent("Algorithm.Measurement", new[] { new AlgorithmDiagnosticField("Result", AlgorithmScalarValue.FromEnum("Pass")) });
            scope.TryEmit(value); Assert.Equal(AlgorithmDiagnosticEmission.Dropped, scope.TryEmit(value));
            Assert.True(pipeline.ReadHealth().QuotaDropped > 0); scope.Seal();
        }
        await using var bounded = new DiagnosticPipeline(Policy(active: 1), Guid.NewGuid(), _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
        var first = bounded.OpenScope(new(ExecutionKind.Manual, Guid.NewGuid()), new());
        var refused = bounded.OpenScope(new(ExecutionKind.Manual, Guid.NewGuid()), new());
        Assert.Equal(1, bounded.ReadHealth().ActiveExecutionScopes); refused.Seal();
        Assert.Equal(1, bounded.ReadHealth().ActiveExecutionScopes); first.Seal();
        Assert.Equal(0, bounded.ReadHealth().ActiveExecutionScopes);
    }

    [Fact, Trait("VerificationId", "V156_C04")]
    public async Task V156_C04_QueryRevalidatesStoredSchemaAndChannelRatherThanReturningRawLines()
    {
        var items = new ConcurrentQueue<DiagnosticEnvelope>(); var policy = Policy();
        await using var pipeline = new DiagnosticPipeline(policy, Guid.NewGuid(), item => { items.Enqueue(item); return ValueTask.CompletedTask; }, _ => ValueTask.CompletedTask);
        pipeline.TryEmit(Request()); Assert.True(await pipeline.FlushAsync());
        var line = Encoding.UTF8.GetString(Assert.Single(items).Line);
        Assert.NotNull(DiagnosticJson.Decode(line, policy, false));
        Assert.Null(DiagnosticJson.Decode(line.Replace("\"Pass\"", "\"SECRET-BAIT\""), policy, false));
        Assert.Null(DiagnosticJson.Decode(line.Replace("\"Result\":", "\"token\":"), policy, false));
        Assert.Null(DiagnosticJson.Decode(line.Replace("\"properties\":{", "\"properties\":{\"Unknown\":\"SECRET\","), policy, false));
        Assert.Null(DiagnosticJson.Decode(line, policy, true));
    }

    private sealed class Trap
    {
        public int Reads;
        public string Secret { get { Reads++; throw new InvalidOperationException(); } }
        public override string ToString() { Reads++; throw new InvalidOperationException(); }
    }
    private sealed class TrapException : Exception
    {
        public int Reads;
        public override string Message { get { Reads++; return "SECRET"; } }
        public override string? StackTrace { get { Reads++; return "SECRET"; } }
        public override string ToString() { Reads++; return "SECRET"; }
    }
    private sealed class RecordingSink : ISafeDiagnosticSink
    {
        internal ConcurrentQueue<DiagnosticRecord> Records { get; } = new();
        public ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken)
        { Records.Enqueue(record); return ValueTask.CompletedTask; }
    }
    private sealed class ThrowingSink : ISafeDiagnosticSink
    {
        public ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken) => throw new Exception("SECRET-BAIT");
    }
}
