using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;

namespace SharpInspect.SampleHost;

/// <summary>Consumer supplied demonstration calculations; no production algorithm or device is supplied.</summary>
internal static class AlgorithmPreparationDemo
{
    public static int Run()
    {
        try { RunAsync().GetAwaiter().GetResult(); return 0; }
        catch { Console.Error.WriteLine("V110-P01 algorithm-preparation FAIL reason=AlgorithmConsumerCheckFailed"); return 1; }
    }

    private static async Task RunAsync()
    {
        var contrast = new ContrastFactory();
        var ratio = new BrightRatioFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IVisionAlgorithmFactory>(contrast);
        services.AddSingleton<IVisionAlgorithmFactory>(ratio);
        services.AddSharpInspectAlgorithmPreparation(new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5)));
        services.AddSharpInspectRuntime();
        await using var provider = services.BuildServiceProvider();
        var preparer = provider.GetRequiredService<AlgorithmPreparationService>();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var before = await runtime.GetSnapshotAsync();
        Require(preparer.Descriptors.Count == 2 && !before.Ready);
        foreach (var factory in new DemoFactory[] { contrast, ratio })
        {
            var configuration = AlgorithmConfigurationSnapshot.Create(factory.Descriptor.ConfigurationSchema, factory.SampleValues());
            var request = Request(factory.Descriptor, configuration);
            var prepared = await preparer.PrepareAsync(request);
            Require(prepared.Succeeded && prepared.Prepared is not null && factory.Created == 1 && factory.Warmed == 1);
            Require(prepared.Prepared!.Configuration.ContentHash == configuration.ContentHash);
            await prepared.Prepared.DisposeAsync();
            Require(factory.Disposed == 1 && prepared.Prepared.IsRetired);
            Console.WriteLine($"V110 prepared algorithm={factory.Descriptor.Identity.Id} schema={configuration.SchemaId}/{configuration.SchemaVersion} configHash={configuration.ContentHash} retired=true");
        }

        var valid = AlgorithmConfigurationSnapshot.Create(contrast.Descriptor.ConfigurationSchema, contrast.SampleValues());
        var validRequest = Request(contrast.Descriptor, valid);
        var badHash = new AlgorithmConfigurationSnapshot(valid.SchemaId, valid.SchemaVersion, valid.SchemaContentHash,
            valid.CanonicalizationVersion, new string('0', 64), valid.Values);
        Require((await preparer.PrepareAsync(validRequest with { Configuration = badHash })).ReasonCode == "AlgorithmConfigurationInvalid");
        Require((await preparer.PrepareAsync(validRequest with { ResultSchemaContentHash = new string('0', 64) })).ReasonCode == "AlgorithmResultBindingMismatch");
        var missing = new AlgorithmConfigurationSnapshot(valid.SchemaId, valid.SchemaVersion, valid.SchemaContentHash,
            valid.CanonicalizationVersion, valid.ContentHash, valid.Values.Take(1));
        Require((await preparer.PrepareAsync(validRequest with { Configuration = missing })).ReasonCode == "AlgorithmConfigurationInvalid");
        Require(contrast.Created == 1);

        var reversed = AlgorithmConfigurationSnapshot.Create(contrast.Descriptor.ConfigurationSchema, new[]
        {
            new AlgorithmConfigurationEntry("Lower", "intensity", AlgorithmScalarValue.FromFloat64(220)),
            new AlgorithmConfigurationEntry("Upper", "intensity", AlgorithmScalarValue.FromFloat64(20))
        });
        Require((await preparer.PrepareAsync(validRequest with { Configuration = reversed })).ReasonCode == "AlgorithmSemanticValidationFailed");
        Require(contrast.Created == 1);

        await using (var missingModel = new AlgorithmPreparationService(new[] { new ContrastFactory(missingDependency: true) },
            new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5))))
        {
            var failed = await missingModel.PrepareAsync(validRequest);
            Require(!failed.Succeeded && failed.Prepared is null && failed.ReasonCode == "AlgorithmCreationFailed");
        }
        var after = await runtime.GetSnapshotAsync();
        Require(!after.Ready && after.ActiveRecipe is null && after.ArmState == before.ArmState && after.Recovery == before.Recovery);
        Console.WriteLine("V110-P01 algorithm-preparation PASS factories=2 configurationNegatives=true preparationFailure=true ready=false frameExecution=NotRun productionAlgorithm=UserSupplied");
    }

    private static AlgorithmPreparationRequest Request(AlgorithmDescriptor descriptor, AlgorithmConfigurationSnapshot configuration) =>
        new(descriptor.Identity, configuration, descriptor.ResultSchema.Id, descriptor.ResultSchema.Version,
            descriptor.ResultSchema.ContentHash, descriptor.ResultSchema.OverlayContract.Id,
            descriptor.ResultSchema.OverlayContract.Version, descriptor.ResultSchema.OverlayContract.ContentHash,
            TimeSpan.FromSeconds(3));

    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("AlgorithmConsumerCheckFailed"); }

    private abstract class DemoFactory : IVisionAlgorithmFactory
    {
        private readonly bool _missingDependency;
        protected DemoFactory(string id, AlgorithmConfigurationSchema schema, string measurementUnit, bool missingDependency = false)
        {
            _missingDependency = missingDependency;
            var result = new AlgorithmResultSchema(id + ".Result", "v1", new[]
            { new AlgorithmFieldDefinition("Value", AlgorithmScalarType.Float64, measurementUnit, true) },
                new[] { "UnsupportedFrame", "NoPixels" }, new OverlayContract("Demo.FramePixel", "v1"));
            Descriptor = new(new(id, "v1"), schema, result);
        }
        public AlgorithmDescriptor Descriptor { get; }
        public int Created { get; private set; }
        public int Warmed { get; private set; }
        public int Disposed { get; private set; }
        public abstract IEnumerable<AlgorithmConfigurationEntry> SampleValues();
        public abstract ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default);
        protected abstract Func<double[], (InspectionDecision Decision, double Value)> BindComputation(AlgorithmConfigurationSnapshot configuration);
        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_missingDependency) throw new InvalidOperationException("Synthetic private computation dependency unavailable");
            // Resolve and prepare private computation dependencies once, outside ExecuteAsync.
            var compute = BindComputation(configuration);
            Created++;
            return ValueTask.FromResult<IVisionAlgorithm>(new DemoAlgorithm(Descriptor, compute,
                () => Warmed++, () => Disposed++));
        }
        protected static double Number(AlgorithmConfigurationSnapshot configuration, string key) =>
            configuration.Values.Single(item => item.Key == key).Value.AsFloat64();
    }

    private sealed class ContrastFactory : DemoFactory
    {
        public ContrastFactory(bool missingDependency = false) : base("Demo.ContrastBand", new("Demo.ContrastConfig", "v1", new[]
        {
            new AlgorithmFieldDefinition("Lower", AlgorithmScalarType.Float64, "intensity", true,
                new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 255), AlgorithmScalarValue.FromFloat64(20)),
            new AlgorithmFieldDefinition("Upper", AlgorithmScalarType.Float64, "intensity", true,
                new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 255), AlgorithmScalarValue.FromFloat64(220))
        }), "intensity", missingDependency) { }
        public override IEnumerable<AlgorithmConfigurationEntry> SampleValues() => new[]
        {
            new AlgorithmConfigurationEntry("Lower", "intensity", AlgorithmScalarValue.FromFloat64(20)),
            new AlgorithmConfigurationEntry("Upper", "intensity", AlgorithmScalarValue.FromFloat64(220))
        };
        public override ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Number(configuration, "Lower") < Number(configuration, "Upper")
                ? Array.Empty<AlgorithmValidationIssue>() : new[] { new AlgorithmValidationIssue("LowerMustBeBelowUpper", "Lower") });
        protected override Func<double[], (InspectionDecision, double)> BindComputation(AlgorithmConfigurationSnapshot configuration)
        {
            var lower = Number(configuration, "Lower"); var upper = Number(configuration, "Upper");
            return pixels => { var value = pixels.Max() - pixels.Min(); return (value >= lower && value <= upper ? InspectionDecision.Pass : InspectionDecision.Fail, value); };
        }
    }

    private sealed class BrightRatioFactory : DemoFactory
    {
        public BrightRatioFactory() : base("Demo.BrightRatio", new("Demo.RatioConfig", "v1", new[]
        {
            new AlgorithmFieldDefinition("MinimumRatio", AlgorithmScalarType.Float64, "ratio", true,
                new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 1))
        }), "ratio") { }
        public override IEnumerable<AlgorithmConfigurationEntry> SampleValues() => new[]
        { new AlgorithmConfigurationEntry("MinimumRatio", "ratio", AlgorithmScalarValue.FromFloat64(0.5)) };
        public override ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>());
        protected override Func<double[], (InspectionDecision, double)> BindComputation(AlgorithmConfigurationSnapshot configuration)
        {
            var minimum = Number(configuration, "MinimumRatio");
            return pixels => { var value = pixels.Count(pixel => pixel >= 128) / (double)pixels.Length; return (value >= minimum ? InspectionDecision.Pass : InspectionDecision.Fail, value); };
        }
    }

    private sealed class DemoAlgorithm : IVisionAlgorithm
    {
        private readonly AlgorithmDescriptor _descriptor;
        private readonly Func<double[], (InspectionDecision Decision, double Value)> _compute;
        private readonly Action _warmed;
        private readonly Action _disposedCallback;
        private bool _prepared;
        private bool _disposed;
        public DemoAlgorithm(AlgorithmDescriptor descriptor, Func<double[], (InspectionDecision, double)> compute, Action warmed, Action disposed)
        { _descriptor = descriptor; _compute = compute; _warmed = warmed; _disposedCallback = disposed; }
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = _compute(new double[] { 0, 64, 128, 255 });
            _prepared = true; _warmed(); return ValueTask.CompletedTask;
        }
        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (!_prepared || _disposed) throw new InvalidOperationException("DemonstrationAlgorithmNotPrepared");
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Frame.PixelFormat != VisionPixelFormat.Mono8)
                return ValueTask.FromResult(new AlgorithmResult(InspectionDecision.Unknown, "UnsupportedFrame", new[]
                { new AlgorithmMeasurement("Value", _descriptor.ResultSchema.Measurements[0].Unit, AlgorithmScalarValue.FromFloat64(0)) },
                    new OutputOverlaySet(_descriptor.ResultSchema.OverlayContract.Id, _descriptor.ResultSchema.OverlayContract.Version)));
            var pixels = context.Frame.GetRowSpan(0)[..context.Frame.Width].ToArray().Select(value => (double)value).ToArray();
            var result = _compute(pixels);
            return ValueTask.FromResult(new AlgorithmResult(result.Decision, null, new[]
            { new AlgorithmMeasurement("Value", _descriptor.ResultSchema.Measurements[0].Unit, AlgorithmScalarValue.FromFloat64(result.Value)) },
                new OutputOverlaySet(_descriptor.ResultSchema.OverlayContract.Id, _descriptor.ResultSchema.OverlayContract.Version)));
        }
        public ValueTask DisposeAsync()
        { if (!_disposed) { _disposed = true; _disposedCallback(); } return ValueTask.CompletedTask; }
    }
}
