using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    private static AuthorizationPolicy CreateCalibrationImportAuthorizationPolicy()
    {
        var source = CreatePreviewAuthorizationPolicy();
        var permissions = new[] { Permission.PublishCalibration, Permission.SelectHistoricalCalibration,
            Permission.ManageCalibrationAcceptancePolicy, Permission.RecordPhysicalCalibrationVerification,
            Permission.ManageCameraBindings };
        return new AuthorizationPolicy("v134-import", "1", source.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Value.Concat(permissions).Distinct()), source.StepUpPermissions.Concat(permissions).Distinct());
    }
}

/// <summary>Public procedure fixture: its coefficients identify the pixels actually read locally.</summary>
internal sealed class CalibrationImportProcedureFixture : ICalibrationProcedure<byte[]>
{
    internal CalibrationImportProcedureFixture(bool physicalRequired = false)
    {
        Policy = CalibrationExportPackageTests.ExportFixture.Create().Policy;
        if (physicalRequired)
        {
            var baseline = Policy;
            RecipeContractReference Contract(string id) => new(id, "1", new string('A', 64));
            Policy = new(baseline.Id, baseline.Version, baseline.Kind, baseline.LogicalPurpose,
                baseline.ProcedureContract, baseline.InputContract, baseline.CoefficientContract,
                baseline.ExtractionReceiptContract, baseline.ComputationEvidenceContract, baseline.Sample,
                baseline.Coverage, baseline.PoseDiversity, baseline.MaximumPerImageResidual,
                baseline.MaximumPerPointResidual, baseline.InvalidObservation,
                new(CalibrationPolicyApplicability.Required, Contract("physical-procedure"), Contract("physical-evidence"),
                    Contract("independent-gauge"), TimeSpan.FromHours(1), new[]
                    {
                        new CalibrationMetricGate("physical-scale", CalibrationPolicyFactReference.PhysicalVerificationMetric(
                            "Scale", "mm/pixel"), CalibrationGateComparison.MaximumInclusive, 1)
                    }));
        }
        Descriptor = new(Policy.ProcedureContract, Policy.InputContract, Policy.Kind);
        InputCodec = new Codec(Policy.InputContract);
    }
    internal CalibrationAcceptancePolicy Policy { get; }
    public CalibrationProcedureDescriptor Descriptor { get; }
    public ICalibrationInputCodec<byte[]> InputCodec { get; }
    internal int Extractions;
    internal int Computations;
    public ValueTask<CalibrationExtractionResult> ExtractAsync(CalibrationExtractionContext<byte[]> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Extractions);
        Assert.True(context.Frame.IsLoanActive);
        var pixels = ReadPixels(context.Frame);
        return ValueTask.FromResult(new CalibrationExtractionResult(new[] { new CalibrationImageFeature("corner", 1, 1) },
            Array.Empty<CalibrationProcedureDiagnostic>(), new CalibrationExtractionReceipt(Policy.ExtractionReceiptContract,
                SHA256.HashData(pixels))));
    }
    public ValueTask<CalibrationProcedureComputationResult> ComputeAsync(CalibrationComputationContext<byte[]> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Computations);
        var observation = Assert.Single(context.Observations);
        Assert.NotNull(observation.Receipt);
        return ValueTask.FromResult(ResultFor(ReadPixels(observation.Frame)));
    }
    internal CalibrationProcedureComputationResult ResultFor(byte[] pixels)
    {
        var coefficients = SHA256.HashData(pixels);
        return new(new CalibrationCoefficientPayload(Policy.CoefficientContract, coefficients),
            new[] { new CalibrationQualityMetric("PointResidual", 0.25, "pixels") },
            Array.Empty<CalibrationProcedureDiagnostic>(),
            new CalibrationComputationEvidencePayload(Policy.ComputationEvidenceContract, coefficients));
    }
    private static byte[] ReadPixels(VisionFrame frame)
    {
        using var bytes = new MemoryStream();
        for (var row = 0; row < frame.Metadata.Height; row++) bytes.Write(frame.GetRowSpan(row));
        return bytes.ToArray();
    }
    private sealed class Codec : ICalibrationInputCodec<byte[]>
    {
        internal Codec(RecipeContractReference inputContract) => InputContract = inputContract;
        public RecipeContractReference InputContract { get; }
        public byte[] Decode(ReadOnlyMemory<byte> canonicalBytes) => canonicalBytes.ToArray();
        public ReadOnlyMemory<byte> Encode(byte[] input) => (byte[])input.Clone();
    }
}

internal sealed class ImportPhysicalProcedureFixture : IImportedCalibrationPhysicalVerificationProcedure
{
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ImportPhysicalProcedureFixture(PhysicalCalibrationVerificationRequirement requirement) =>
        (ProcedureContract, EvidenceContract) = (requirement.ProcedureContract!, requirement.EvidenceContract!);
    public RecipeContractReference ProcedureContract { get; }
    public RecipeContractReference EvidenceContract { get; }
    internal bool Block { get; set; }
    internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<CalibrationQualityMetric> Evaluate(ImportedCalibrationPhysicalVerificationContext context,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Cancelled.TrySetResult(true));
        Entered.TrySetResult(true);
        if (Block) _release.Task.GetAwaiter().GetResult();
        var bytes = context.Evidence.GetBytes();
        if (bytes.Length != 1) throw new InvalidOperationException("FixtureIndependentEvidenceInvalid");
        return new[] { new CalibrationQualityMetric("Scale", bytes[0] / 100d, "mm/pixel") };
    }
    internal void Release() => _release.TrySetResult(true);
}
