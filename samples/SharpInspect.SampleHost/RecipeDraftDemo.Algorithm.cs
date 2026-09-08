using SharpInspect.Abstractions;

namespace SharpInspect.SampleHost;

internal static partial class RecipeDraftDemo
{
    internal static AlgorithmExecutionPolicy ExecutionPolicy { get; } = new("Sample.DraftExecution", "1",
        TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

    internal static IVisionAlgorithmFactory CreateFactory() => new DraftOnlyFactory();

    /// <summary>Consumer-owned semantic validation. This authoring example cannot create or run an algorithm.</summary>
    private sealed class DraftOnlyFactory : IVisionAlgorithmFactory
    {
        public AlgorithmDescriptor Descriptor { get; } = new(new("Sample.DraftParameters", "1"),
            new AlgorithmConfigurationSchema("Sample.DraftConfig", "1", new[]
            {
                new AlgorithmFieldDefinition("enabled", AlgorithmScalarType.Boolean, "none", true,
                    authoringDefault: AlgorithmScalarValue.FromBoolean(true), helpText: "启用此工艺参数。"),
                new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64, "items", true,
                    new(minInt64: 1, maxInt64: 100), AlgorithmScalarValue.FromInt64(20), "数量上限，必须大于阈值。"),
                new AlgorithmFieldDefinition("threshold", AlgorithmScalarType.Float64, "mm", true,
                    new(minFloat64: 0, maxFloat64: 100), AlgorithmScalarValue.FromFloat64(10.25), "阈值（毫米）。"),
                new AlgorithmFieldDefinition("label", AlgorithmScalarType.String, "text", true,
                    new(minLength: 0, maxLength: 24), AlgorithmScalarValue.FromString("工艺草稿"), "仅作为算法参数的文字。"),
                new AlgorithmFieldDefinition("mode", AlgorithmScalarType.Enum, "none", true,
                    new(allowedValues: new[] { "fast", "accurate" }), AlgorithmScalarValue.FromEnum("fast"), "选取算法声明的模式。"),
                new AlgorithmFieldDefinition("tag", AlgorithmScalarType.Enum, "none", false,
                    helpText: "未声明候选列表的可选枚举文本。"),
                new AlgorithmFieldDefinition("optionalFlag", AlgorithmScalarType.Boolean, "none", false,
                    helpText: "未设置与 false 是不同的配置。")
            }), new AlgorithmResultSchema("Sample.DraftResult", "1", Array.Empty<AlgorithmFieldDefinition>(),
                Array.Empty<string>(), new OverlayContract("Sample.DraftOverlay", "1", 0, 0, 0)));

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var structural = configuration.Validate(Descriptor.ConfigurationSchema);
            if (structural.Count != 0) return ValueTask.FromResult(structural);
            var count = configuration.Values.Single(entry => entry.Key == "count").Value.AsInt64();
            var threshold = configuration.Values.Single(entry => entry.Key == "threshold").Value.AsFloat64();
            IReadOnlyList<AlgorithmValidationIssue> issues = threshold < count
                ? Array.Empty<AlgorithmValidationIssue>()
                : new[] { new AlgorithmValidationIssue("SampleThresholdMustBeBelowCount", "threshold") };
            return ValueTask.FromResult(issues);
        }

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DraftAuthoringMustNotCreateAnAlgorithm");
    }
}
