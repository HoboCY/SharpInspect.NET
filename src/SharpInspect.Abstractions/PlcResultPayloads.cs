using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>A stable, observable binding invariant; successful proofs are retained with the binding.</summary>
public sealed class PlcResultValidationCheck
{
    internal PlcResultValidationCheck(string validationId, string subject, bool passed, string reasonCode)
    {
        ValidationId = AlgorithmContractValidation.Identifier(validationId, nameof(validationId));
        Subject = AlgorithmContractValidation.BoundedText(subject, nameof(subject), 512);
        Passed = passed;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-result-check-v1", ValidationId, Subject, passed ? "1" : "0", ReasonCode });
    }
    public string ValidationId { get; }
    public string Subject { get; }
    public bool Passed { get; }
    public string ReasonCode { get; }
    public string ContentHash { get; }
}

/// <summary>Framework proof for one full immutable schema; it does not grant production admission.</summary>
public sealed class PlcResultSchemaValidation
{
    public const string ValidatorVersion = "sharpinspect-plc-result-binder-v1";
    internal PlcResultSchemaValidation(PlcResultContract contract, AlgorithmResultSchema schema,
        IEnumerable<PlcResultValidationCheck> checks)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Checks = AlgorithmContractValidation.Copy(checks, nameof(checks), 4096);
        if (Checks.Count == 0 || Checks.Any(value => !value.Passed))
            throw new ArgumentException("PlcResultSchemaProofRequired");
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-result-schema-validation-v1", ValidatorVersion, contract.ContentHash,
            schema.Id, schema.Version, schema.ContentHash, Checks.Count.ToString(CultureInfo.InvariantCulture) }
            .Concat(Checks.Select(value => value.ContentHash)));
    }
    public PlcResultContract Contract { get; }
    public AlgorithmResultSchema Schema { get; }
    public ReadOnlyCollection<PlcResultValidationCheck> Checks { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Produced only after the fixed framework validator proves the complete domain; callers cannot
/// construct or alter the proof. This review binding is not a deployment revision or publication permit.
/// </summary>
public sealed class PlcResultContractBinding
{
    internal PlcResultContractBinding(RecipeReference recipe, AlgorithmIdentity algorithm,
        PlcResultSchemaValidation validation)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        _ = new RecipeContractReference(recipe.Id, recipe.Version, recipe.ContentHash);
        Recipe = recipe;
        Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-result-binding-v1", recipe.Id, recipe.Version, recipe.ContentHash,
            algorithm.Id, algorithm.Version, validation.ContentHash });
    }
    public RecipeReference Recipe { get; }
    public AlgorithmIdentity Algorithm { get; }
    public PlcResultSchemaValidation Validation { get; }
    public PlcResultContract Contract => Validation.Contract;
    public AlgorithmResultSchema ResultSchema => Validation.Schema;
    public string ContentHash { get; }
}

public sealed record PlcResultSchemaValidationResult(bool Valid, string ReasonCode,
    PlcResultSchemaValidation? Validation, IReadOnlyList<PlcResultValidationCheck> Checks);
public sealed record PlcResultBindingResult(bool Bound, string ReasonCode,
    PlcResultContractBinding? Binding, IReadOnlyList<PlcResultValidationCheck> Checks);

/// <summary>Latched PLC-owned cycle identity; ResultSequence echoes CycleSequence without host allocation.</summary>
public sealed record PlcControllerCycle(uint ControllerEpoch, uint CycleSequence);

/// <summary>Ascending-address PDU register bytes, already ordered by the contract. No MBAP header or gap bytes.</summary>
public sealed class PlcRegisterSegment
{
    internal PlcRegisterSegment(int startRegister, IEnumerable<byte> registerBytes)
    {
        if (startRegister is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(startRegister));
        RegisterBytes = AlgorithmContractValidation.Copy(registerBytes, nameof(registerBytes), 131072);
        if (RegisterBytes.Count < 2 || RegisterBytes.Count % 2 != 0 ||
            startRegister + RegisterBytes.Count / 2 > 65536)
            throw new ArgumentException("PlcRegisterSegmentInvalid");
        StartRegister = startRegister;
    }
    public int StartRegister { get; }
    public ReadOnlyCollection<byte> RegisterBytes { get; }
    public int RegisterCount => RegisterBytes.Count / 2;
}

/// <summary>Complete frozen result bytes. This value alone is not a durable publication permit or ResultValid write.</summary>
public sealed class PlcResultPayloadSnapshot
{
    public const string WireFormatVersion = "sharpinspect-plc-register-image-v1";
    internal PlcResultPayloadSnapshot(PlcResultContractBinding binding, Guid inspectionId, PlcControllerCycle cycle,
        ExecutionStatus executionStatus, InspectionDecision decision, string? reasonCode,
        IEnumerable<PlcRegisterSegment> segments)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (inspectionId == Guid.Empty) throw new ArgumentException("PlcInspectionIdRequired");
        InspectionId = inspectionId;
        Cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        ExecutionStatus = executionStatus; Decision = decision; ReasonCode = reasonCode;
        Segments = AlgorithmContractValidation.Copy(segments.OrderBy(value => value.StartRegister), nameof(segments), 2048);
        if (Segments.Count == 0) throw new ArgumentException("PlcCompletePayloadRequired");
        for (var index = 1; index < Segments.Count; index++)
            if (Segments[index - 1].StartRegister + Segments[index - 1].RegisterCount > Segments[index].StartRegister)
                throw new ArgumentException("PlcRegisterOverlap");
        WireContentHash = ComputeWireHash(Segments);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-result-snapshot-v1", binding.ContentHash, inspectionId.ToString("D"),
            cycle.ControllerEpoch.ToString(CultureInfo.InvariantCulture), cycle.CycleSequence.ToString(CultureInfo.InvariantCulture),
            ((int)executionStatus).ToString(CultureInfo.InvariantCulture), ((int)decision).ToString(CultureInfo.InvariantCulture),
            reasonCode, WireContentHash });
    }

    internal static string ComputeWireHash(IReadOnlyList<PlcRegisterSegment> segments)
    {
        var wire = new List<string?> { WireFormatVersion, segments.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var segment in segments)
        {
            wire.Add(segment.StartRegister.ToString(CultureInfo.InvariantCulture));
            wire.Add(segment.RegisterBytes.Count.ToString(CultureInfo.InvariantCulture));
            wire.Add(Convert.ToHexString(segment.RegisterBytes.ToArray()));
        }
        return AlgorithmContractValidation.HashParts(wire);
    }
    public PlcResultContractBinding Binding { get; }
    public RecipeContractReference Contract => new(Binding.Contract.Id, Binding.Contract.Version, Binding.Contract.ContentHash);
    public Guid InspectionId { get; }
    public PlcControllerCycle Cycle { get; }
    public ExecutionStatus ExecutionStatus { get; }
    public InspectionDecision Decision { get; }
    public string? ReasonCode { get; }
    public ReadOnlyCollection<PlcRegisterSegment> Segments { get; }
    public int PayloadBytes => Segments.Sum(value => value.RegisterBytes.Count);
    public string WireContentHash { get; }
    public string ContentHash { get; }
}

public sealed record PlcResultEncodingResult(bool Succeeded, string ReasonCode,
    PlcResultPayloadSnapshot? Snapshot, string? DetailReasonCode = null);

/// <summary>
/// Development byte preview from an actual Manual computation. This is a different CLR type
/// from a production snapshot, has no InspectionId and cannot be submitted for publication.
/// Controller values are sample input, not evidence of a latched PLC cycle.
/// </summary>
public sealed class PlcResultPayloadPreview
{
    internal PlcResultPayloadPreview(PlcResultContractBinding binding, ExecutionCorrelationId sourceExecution,
        uint sampleControllerEpoch, uint sampleCycleSequence, ExecutionStatus executionStatus,
        InspectionDecision decision, string? reasonCode, IEnumerable<PlcRegisterSegment> segments)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ArgumentNullException.ThrowIfNull(sourceExecution);
        if (sourceExecution.Kind != ExecutionKind.Manual || sourceExecution.Value == Guid.Empty)
            throw new ArgumentException("PlcResultPreviewManualCorrelationRequired");
        SourceExecution = sourceExecution;
        SampleControllerEpoch = sampleControllerEpoch; SampleCycleSequence = sampleCycleSequence;
        ExecutionStatus = executionStatus; Decision = decision; ReasonCode = reasonCode;
        Segments = AlgorithmContractValidation.Copy(segments.OrderBy(value => value.StartRegister), nameof(segments), 2048);
        if (Segments.Count == 0) throw new ArgumentException("PlcCompletePayloadRequired");
        for (var index = 1; index < Segments.Count; index++)
            if (Segments[index - 1].StartRegister + Segments[index - 1].RegisterCount > Segments[index].StartRegister)
                throw new ArgumentException("PlcRegisterOverlap");
        WireContentHash = PlcResultPayloadSnapshot.ComputeWireHash(Segments);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-result-preview-v1", binding.ContentHash, sourceExecution.Kind.ToString(), sourceExecution.Value.ToString("D"),
            sampleControllerEpoch.ToString(CultureInfo.InvariantCulture), sampleCycleSequence.ToString(CultureInfo.InvariantCulture),
            ((int)executionStatus).ToString(CultureInfo.InvariantCulture), ((int)decision).ToString(CultureInfo.InvariantCulture),
            reasonCode, WireContentHash });
    }
    public PlcResultContractBinding Binding { get; }
    public RecipeContractReference Contract => new(Binding.Contract.Id, Binding.Contract.Version, Binding.Contract.ContentHash);
    public ExecutionCorrelationId SourceExecution { get; }
    public uint SampleControllerEpoch { get; }
    public uint SampleCycleSequence { get; }
    public ExecutionStatus ExecutionStatus { get; }
    public InspectionDecision Decision { get; }
    public string? ReasonCode { get; }
    public ReadOnlyCollection<PlcRegisterSegment> Segments { get; }
    public int PayloadBytes => Segments.Sum(value => value.RegisterBytes.Count);
    public string WireContentHash { get; }
    public string ContentHash { get; }
}

public sealed record PlcResultPreviewResult(bool Succeeded, string ReasonCode,
    PlcResultPayloadPreview? Preview, string? DetailReasonCode = null);
