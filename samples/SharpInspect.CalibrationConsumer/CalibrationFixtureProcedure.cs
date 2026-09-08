using System.Buffers.Binary;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.CalibrationConsumer;

/// <summary>
/// The typed input used by the development fixture.  It is intentionally small
/// and explicit so the procedure boundary demonstrates a canonical input codec;
/// it is not a production calibration model.
/// </summary>
public sealed record CalibrationFixtureInput(int PatternSeed, int MinimumFeatures);

public sealed class CalibrationFixtureInputCodec : ICalibrationInputCodec<CalibrationFixtureInput>
{
    public static RecipeContractReference Contract { get; } = new(
        "SharpInspect.CalibrationConsumer.Input", "1", new string('B', 64));

    public RecipeContractReference InputContract => Contract;

    public CalibrationFixtureInput Decode(ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.Length != sizeof(int) * 2)
            throw new ArgumentException("CalibrationFixtureInputLengthInvalid", nameof(canonicalBytes));

        var bytes = canonicalBytes.Span;
        var input = new CalibrationFixtureInput(
            BinaryPrimitives.ReadInt32LittleEndian(bytes),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[sizeof(int)..]));
        Validate(input);
        return input;
    }

    public ReadOnlyMemory<byte> Encode(CalibrationFixtureInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        var bytes = new byte[sizeof(int) * 2];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, input.PatternSeed);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeof(int)), input.MinimumFeatures);
        return bytes;
    }

    private static void Validate(CalibrationFixtureInput input)
    {
        if (input.MinimumFeatures is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(input.MinimumFeatures),
                "CalibrationFixtureMinimumFeaturesInvalid");
    }
}

/// <summary>
/// Deterministic corner-feature fixture.  Runtime owns the borrowed frame,
/// observation identity, candidate identity, evidence persistence and all
/// production authority decisions.
/// </summary>
public sealed class DeterministicCalibrationProcedure : ICalibrationProcedure<CalibrationFixtureInput>
{
    public static readonly RecipeContractReference ProcedureContract = new(
        "SharpInspect.CalibrationConsumer.Procedure", "1", new string('D', 64));

    public static readonly RecipeContractReference CoefficientContract = new(
        "SharpInspect.CalibrationConsumer.Coefficients", "1", new string('C', 64));

    public static readonly RecipeContractReference AcceptanceContract = new(
        "SharpInspect.CalibrationConsumer.Acceptance", "1", new string('A', 64));

    private readonly CalibrationFixtureInputCodec _codec = new();

    public DeterministicCalibrationProcedure()
    {
        Descriptor = new CalibrationProcedureDescriptor(ProcedureContract,
            CalibrationFixtureInputCodec.Contract, CalibrationKind.Intrinsic);
    }

    public CalibrationProcedureDescriptor Descriptor { get; }
    public ICalibrationInputCodec<CalibrationFixtureInput> InputCodec => _codec;

    public ValueTask<CalibrationExtractionResult> ExtractAsync(
        CalibrationExtractionContext<CalibrationFixtureInput> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var right = context.Frame.Width - 1;
        var bottom = context.Frame.Height - 1;
        var features = new[]
        {
            new CalibrationImageFeature("fixture-top-left", 0, 0),
            new CalibrationImageFeature("fixture-top-right", right, 0),
            new CalibrationImageFeature("fixture-bottom-left", 0, bottom),
            new CalibrationImageFeature("fixture-bottom-right", right, bottom)
        };
        return ValueTask.FromResult(new CalibrationExtractionResult(features,
            new[]
            {
                new CalibrationProcedureDiagnostic("fixture", "deterministic-corners-v1"),
                new CalibrationProcedureDiagnostic("pattern-seed",
                    context.Input.PatternSeed.ToString(System.Globalization.CultureInfo.InvariantCulture))
            }));
    }

    public ValueTask<CalibrationProcedureComputationResult> ComputeAsync(
        CalibrationComputationContext<CalibrationFixtureInput> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Observations.Count < 1)
            throw new InvalidOperationException("CalibrationFixtureObservationsRequired");

        var featureCount = context.Observations.Sum(item => item.Features.Count);
        if (context.Observations.Any(item => item.Features.Count < context.Input.MinimumFeatures))
            throw new InvalidOperationException("CalibrationFixtureFeaturesInsufficient");

        var payload = Encoding.UTF8.GetBytes(
            $"fixture-intrinsic-v1;seed={context.Input.PatternSeed};frames={context.Observations.Count};features={featureCount}");
        return ValueTask.FromResult(new CalibrationProcedureComputationResult(
            new CalibrationCoefficientPayload(CoefficientContract, payload),
            new[]
            {
                new CalibrationQualityMetric("frame-count", context.Observations.Count),
                new CalibrationQualityMetric("feature-count", featureCount),
                new CalibrationQualityMetric("coverage", 0.96, "fraction")
            },
            new[]
            {
                new CalibrationProcedureDiagnostic("fixture", "development-only"),
                new CalibrationProcedureDiagnostic("publication", "forbidden")
            }));
    }
}
