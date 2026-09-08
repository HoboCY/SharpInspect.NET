using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>One category of evidence evaluated by a Calibration Acceptance Policy.</summary>
public enum CalibrationAcceptanceGateCategory : byte
{
    Sample = 1,
    Coverage = 2,
    PoseDiversity = 3,
    MaximumPerImageResidual = 4,
    MaximumPerPointResidual = 5,
    InvalidObservation = 6
}

/// <summary>Whether a policy section or physical verification is required.</summary>
public enum CalibrationPolicyApplicability : byte
{
    Required = 1,
    NotApplicable = 2
}

/// <summary>How an actual calibration fact is compared with a policy threshold.</summary>
public enum CalibrationGateComparison : byte
{
    MinimumInclusive = 1,
    MaximumInclusive = 2
}

/// <summary>The closed set of facts that a calibration policy can name.</summary>
public enum CalibrationPolicyFactKind : byte
{
    IncludedFrameCount = 1,
    SufficientFeatureFrameCount = 2,
    SelectionImageCoverage = 3,
    ExcludedFrameCount = 4,
    ProcedureMetric = 5,
    PhysicalVerificationMetric = 6
}

/// <summary>One typed key and unit identity used by a calibration gate.</summary>
public sealed class CalibrationPolicyFactReference
{
    private CalibrationPolicyFactReference(CalibrationPolicyFactKind kind, string key, string? unit)
    {
        Kind = AlgorithmConfigurationValidation.Enum(kind, nameof(kind));
        Key = AlgorithmConfigurationValidation.Identifier(key, nameof(key));
        Unit = UnitText(unit, nameof(unit));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-policy-fact-v1", Kind.ToString(), Key, Unit
        });
    }

    /// <summary>Count of frames retained after whole-frame exclusions.</summary>
    public static CalibrationPolicyFactReference IncludedFrameCount { get; } =
        new(CalibrationPolicyFactKind.IncludedFrameCount, "IncludedFrameCount", "frames");

    /// <summary>Count of retained frames meeting the procedure feature minimum.</summary>
    public static CalibrationPolicyFactReference SufficientFeatureFrameCount { get; } =
        new(CalibrationPolicyFactKind.SufficientFeatureFrameCount, "SufficientFeatureFrameCount", "frames");

    /// <summary>Selection coverage expressed as a normalized image-area fraction.</summary>
    public static CalibrationPolicyFactReference SelectionImageCoverage { get; } =
        new(CalibrationPolicyFactKind.SelectionImageCoverage, "SelectionImageCoverage", "fraction");

    /// <summary>Count of whole frames explicitly excluded from the candidate.</summary>
    public static CalibrationPolicyFactReference ExcludedFrameCount { get; } =
        new(CalibrationPolicyFactKind.ExcludedFrameCount, "ExcludedFrameCount", "frames");

    /// <summary>References a quality metric emitted by the calibration procedure.</summary>
    public static CalibrationPolicyFactReference ProcedureMetric(string key, string? unit) =>
        new(CalibrationPolicyFactKind.ProcedureMetric, key, unit);

    /// <summary>References a metric emitted by independent physical verification.</summary>
    public static CalibrationPolicyFactReference PhysicalVerificationMetric(string key, string? unit) =>
        new(CalibrationPolicyFactKind.PhysicalVerificationMetric, key, unit);

    public CalibrationPolicyFactKind Kind { get; }
    public string Key { get; }
    public string? Unit { get; }
    public string ContentHash { get; }

    private static string? UnitText(string? value, string parameterName) => value is null
        ? null
        : AlgorithmContractValidation.BoundedText(value, parameterName, 64);
}

/// <summary>An immutable threshold over one exact calibration fact.</summary>
public sealed class CalibrationMetricGate
{
    public CalibrationMetricGate(string gateId, CalibrationPolicyFactReference fact,
        CalibrationGateComparison comparison, double threshold)
    {
        GateId = AlgorithmConfigurationValidation.Identifier(gateId, nameof(gateId));
        Fact = fact ?? throw new ArgumentNullException(nameof(fact));
        Comparison = AlgorithmConfigurationValidation.Enum(comparison, nameof(comparison));
        AlgorithmContractValidation.Finite(threshold, nameof(threshold));
        ValidateThreshold(Fact.Kind, threshold);
        Threshold = threshold == 0d ? 0d : threshold;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-metric-gate-v1", GateId, Fact.ContentHash,
            Comparison.ToString(), Threshold.ToString("R", CultureInfo.InvariantCulture)
        });
    }

    public string GateId { get; }
    public CalibrationPolicyFactReference Fact { get; }
    public CalibrationGateComparison Comparison { get; }
    public double Threshold { get; }
    public string ContentHash { get; }

    private static void ValidateThreshold(CalibrationPolicyFactKind kind, double threshold)
    {
        switch (kind)
        {
            case CalibrationPolicyFactKind.IncludedFrameCount:
            case CalibrationPolicyFactKind.SufficientFeatureFrameCount:
                if (threshold is < 1d or > 64d || threshold != Math.Truncate(threshold))
                    throw new ArgumentOutOfRangeException(nameof(threshold),
                        "CalibrationPolicyFrameCountThresholdInvalid");
                break;
            case CalibrationPolicyFactKind.SelectionImageCoverage:
                if (threshold is < 0d or > 1d)
                    throw new ArgumentOutOfRangeException(nameof(threshold),
                        "CalibrationPolicyCoverageThresholdInvalid");
                break;
            case CalibrationPolicyFactKind.ExcludedFrameCount:
                if (threshold is < 0d or > 64d || threshold != Math.Truncate(threshold))
                    throw new ArgumentOutOfRangeException(nameof(threshold),
                        "CalibrationPolicyExcludedFrameThresholdInvalid");
                break;
        }
    }
}

/// <summary>A category's required gates or an explicit, reasoned NotApplicable section.</summary>
public sealed class CalibrationGateSection
{
    public CalibrationGateSection(CalibrationAcceptanceGateCategory category,
        CalibrationPolicyApplicability applicability, IEnumerable<CalibrationMetricGate>? gates = null,
        string? notApplicableReason = null)
    {
        Category = AlgorithmConfigurationValidation.Enum(category, nameof(category));
        Applicability = AlgorithmConfigurationValidation.Enum(applicability, nameof(applicability));
        Gates = new ReadOnlyCollection<CalibrationMetricGate>(
            AlgorithmContractValidation.Copy(gates, nameof(gates), 8).ToArray());

        if (Applicability == CalibrationPolicyApplicability.Required)
        {
            if (Gates.Count is < 1 or > 8)
                throw new ArgumentException("CalibrationPolicyRequiredGatesInvalid", nameof(gates));
            if (notApplicableReason is not null)
                throw new ArgumentException("CalibrationPolicyNotApplicableReasonUnexpected",
                    nameof(notApplicableReason));
        }
        else
        {
            if (Category is not CalibrationAcceptanceGateCategory.PoseDiversity and
                not CalibrationAcceptanceGateCategory.MaximumPerImageResidual)
                throw new ArgumentException("CalibrationPolicyMandatorySectionCannotBeNotApplicable",
                    nameof(category));
            if (Gates.Count != 0)
                throw new ArgumentException("CalibrationPolicyNotApplicableGatesUnexpected", nameof(gates));
            if (string.IsNullOrWhiteSpace(notApplicableReason))
                throw new ArgumentException("CalibrationPolicyNotApplicableReasonRequired",
                    nameof(notApplicableReason));
        }

        NotApplicableReason = notApplicableReason is null
            ? null
            : AlgorithmContractValidation.BoundedText(notApplicableReason,
                nameof(notApplicableReason), 512);

        foreach (var gate in Gates)
        {
            if (!IsFactAllowed(Category, gate.Fact.Kind))
                throw new ArgumentException("CalibrationPolicyFactCategoryMismatch", nameof(gates));
            if (!IsComparisonAllowed(Category, gate.Comparison))
                throw new ArgumentException("CalibrationPolicyGateComparisonMismatch", nameof(gates));
        }

        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-gate-section-v1", Category.ToString(), Applicability.ToString(),
            NotApplicableReason, Gates.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Gates.Select(gate => gate.ContentHash)));
    }

    public CalibrationAcceptanceGateCategory Category { get; }
    public CalibrationPolicyApplicability Applicability { get; }
    public ReadOnlyCollection<CalibrationMetricGate> Gates { get; }
    public string? NotApplicableReason { get; }
    public string ContentHash { get; }

    private static bool IsFactAllowed(CalibrationAcceptanceGateCategory category,
        CalibrationPolicyFactKind kind) => category switch
        {
            CalibrationAcceptanceGateCategory.Sample => kind is
                CalibrationPolicyFactKind.IncludedFrameCount or
                CalibrationPolicyFactKind.SufficientFeatureFrameCount or
                CalibrationPolicyFactKind.ProcedureMetric,
            CalibrationAcceptanceGateCategory.Coverage => kind is
                CalibrationPolicyFactKind.SelectionImageCoverage or
                CalibrationPolicyFactKind.ProcedureMetric,
            CalibrationAcceptanceGateCategory.PoseDiversity => kind == CalibrationPolicyFactKind.ProcedureMetric,
            CalibrationAcceptanceGateCategory.MaximumPerImageResidual =>
                kind == CalibrationPolicyFactKind.ProcedureMetric,
            CalibrationAcceptanceGateCategory.MaximumPerPointResidual =>
                kind == CalibrationPolicyFactKind.ProcedureMetric,
            CalibrationAcceptanceGateCategory.InvalidObservation => kind is
                CalibrationPolicyFactKind.ExcludedFrameCount or
                CalibrationPolicyFactKind.ProcedureMetric,
            _ => false
        };

    private static bool IsComparisonAllowed(CalibrationAcceptanceGateCategory category,
        CalibrationGateComparison comparison)
    {
        var minimum = category is CalibrationAcceptanceGateCategory.Sample or
            CalibrationAcceptanceGateCategory.Coverage or
            CalibrationAcceptanceGateCategory.PoseDiversity;
        return minimum
            ? comparison == CalibrationGateComparison.MinimumInclusive
            : comparison == CalibrationGateComparison.MaximumInclusive;
    }
}

/// <summary>Physical verification gates and their bounded validity declaration.</summary>
public sealed class PhysicalCalibrationVerificationRequirement
{
    public PhysicalCalibrationVerificationRequirement(CalibrationPolicyApplicability applicability,
        RecipeContractReference? procedureContract, RecipeContractReference? evidenceContract,
        RecipeContractReference? independentReference, TimeSpan? validityInterval,
        IEnumerable<CalibrationMetricGate>? gates = null,
        string? notApplicableReason = null)
    {
        Applicability = AlgorithmConfigurationValidation.Enum(applicability, nameof(applicability));
        ProcedureContract = procedureContract;
        EvidenceContract = evidenceContract;
        IndependentReference = independentReference;
        ValidityInterval = validityInterval;
        Gates = new ReadOnlyCollection<CalibrationMetricGate>(
            AlgorithmContractValidation.Copy(gates, nameof(gates), 32).ToArray());

        if (Applicability == CalibrationPolicyApplicability.Required)
        {
            if (ProcedureContract is null || EvidenceContract is null || IndependentReference is null)
                throw new ArgumentException("CalibrationPhysicalVerificationContractsRequired");
            if (ValidityInterval is not { } interval || interval <= TimeSpan.Zero)
                throw new ArgumentException("CalibrationPhysicalVerificationValidityRequired",
                    nameof(validityInterval));
            if (Gates.Count is < 1 or > 32)
                throw new ArgumentException("CalibrationPhysicalVerificationGatesInvalid", nameof(gates));
            if (notApplicableReason is not null)
                throw new ArgumentException("CalibrationPolicyNotApplicableReasonUnexpected",
                    nameof(notApplicableReason));
        }
        else
        {
            if (ProcedureContract is not null || EvidenceContract is not null ||
                IndependentReference is not null ||
                ValidityInterval is not null || Gates.Count != 0)
                throw new ArgumentException("CalibrationPhysicalVerificationNotApplicableValuesUnexpected");
            if (string.IsNullOrWhiteSpace(notApplicableReason))
                throw new ArgumentException("CalibrationPolicyNotApplicableReasonRequired",
                    nameof(notApplicableReason));
        }

        NotApplicableReason = notApplicableReason is null
            ? null
            : AlgorithmContractValidation.BoundedText(notApplicableReason,
                nameof(notApplicableReason), 512);

        if (Gates.Select(gate => gate.GateId).Distinct(StringComparer.Ordinal).Count() != Gates.Count)
            throw new ArgumentException("CalibrationPolicyGateIdDuplicate", nameof(gates));
        foreach (var gate in Gates)
            if (gate.Fact.Kind != CalibrationPolicyFactKind.PhysicalVerificationMetric)
                throw new ArgumentException("CalibrationPhysicalVerificationMetricRequired", nameof(gates));

        var fields = new List<string?>
        {
            "sharpinspect-physical-calibration-verification-requirement-v1",
            Applicability.ToString(),
            ContractPart(ProcedureContract, "procedure"),
            ContractPart(EvidenceContract, "evidence"),
            ContractPart(IndependentReference, "independent-reference"),
            ValidityInterval?.Ticks.ToString(CultureInfo.InvariantCulture),
            NotApplicableReason,
            Gates.Count.ToString(CultureInfo.InvariantCulture)
        };
        fields.AddRange(Gates.Select(gate => gate.ContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(fields);
    }

    public CalibrationPolicyApplicability Applicability { get; }
    public RecipeContractReference? ProcedureContract { get; }
    public RecipeContractReference? EvidenceContract { get; }
    public RecipeContractReference? IndependentReference { get; }
    public TimeSpan? ValidityInterval { get; }
    public ReadOnlyCollection<CalibrationMetricGate> Gates { get; }
    public string? NotApplicableReason { get; }
    public string ContentHash { get; }

    private static string? ContractPart(RecipeContractReference? contract, string label) => contract is null
        ? null
        : string.Join("|", label, contract.Id, contract.Version, contract.ContentHash);
}

/// <summary>
/// Immutable project-specific acceptance policy for one calibration kind and purpose.
/// It defines evidence gates without granting production authority.
/// </summary>
public sealed class CalibrationAcceptancePolicy
{
    public CalibrationAcceptancePolicy(string id, string version, CalibrationKind kind,
        string logicalPurpose, RecipeContractReference procedureContract,
        RecipeContractReference inputContract, RecipeContractReference coefficientContract,
        RecipeContractReference extractionReceiptContract,
        RecipeContractReference computationEvidenceContract, CalibrationGateSection sample,
        CalibrationGateSection coverage, CalibrationGateSection poseDiversity,
        CalibrationGateSection maximumPerImageResidual,
        CalibrationGateSection maximumPerPointResidual, CalibrationGateSection invalidObservation,
        PhysicalCalibrationVerificationRequirement physicalVerification)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        Kind = AlgorithmConfigurationValidation.Enum(kind, nameof(kind));
        LogicalPurpose = AlgorithmConfigurationValidation.Identifier(logicalPurpose, nameof(logicalPurpose));
        ProcedureContract = procedureContract ?? throw new ArgumentNullException(nameof(procedureContract));
        InputContract = inputContract ?? throw new ArgumentNullException(nameof(inputContract));
        CoefficientContract = coefficientContract ?? throw new ArgumentNullException(nameof(coefficientContract));
        ExtractionReceiptContract = extractionReceiptContract ??
            throw new ArgumentNullException(nameof(extractionReceiptContract));
        ComputationEvidenceContract = computationEvidenceContract ??
            throw new ArgumentNullException(nameof(computationEvidenceContract));
        Sample = RequireSection(sample, CalibrationAcceptanceGateCategory.Sample, nameof(sample));
        Coverage = RequireSection(coverage, CalibrationAcceptanceGateCategory.Coverage, nameof(coverage));
        PoseDiversity = RequireSection(poseDiversity, CalibrationAcceptanceGateCategory.PoseDiversity,
            nameof(poseDiversity));
        MaximumPerImageResidual = RequireSection(maximumPerImageResidual,
            CalibrationAcceptanceGateCategory.MaximumPerImageResidual, nameof(maximumPerImageResidual));
        MaximumPerPointResidual = RequireSection(maximumPerPointResidual,
            CalibrationAcceptanceGateCategory.MaximumPerPointResidual, nameof(maximumPerPointResidual));
        InvalidObservation = RequireSection(invalidObservation,
            CalibrationAcceptanceGateCategory.InvalidObservation, nameof(invalidObservation));
        PhysicalVerification = physicalVerification ??
            throw new ArgumentNullException(nameof(physicalVerification));

        Sections = new ReadOnlyCollection<CalibrationGateSection>(new[]
        {
            Sample, Coverage, PoseDiversity, MaximumPerImageResidual,
            MaximumPerPointResidual, InvalidObservation
        });

        var gates = Sections.SelectMany(section => section.Gates).ToArray();
        if (gates.Length > 32)
            throw new ArgumentException("CalibrationPolicyGateCapacityExceeded", nameof(sample));
        if (gates.Select(gate => gate.GateId).Distinct(StringComparer.Ordinal).Count() != gates.Length)
            throw new ArgumentException("CalibrationPolicyGateIdDuplicate", nameof(sample));

        var fields = new List<string?>
        {
            "sharpinspect-calibration-acceptance-policy-v1", Id, Version, Kind.ToString(), LogicalPurpose,
            ContractPart(ProcedureContract), ContractPart(InputContract), ContractPart(CoefficientContract),
            ContractPart(ExtractionReceiptContract), ContractPart(ComputationEvidenceContract),
            Sections.Count.ToString(CultureInfo.InvariantCulture)
        };
        fields.AddRange(Sections.Select(section => section.ContentHash));
        fields.Add(PhysicalVerification.ContentHash);
        ContentHash = AlgorithmContractValidation.HashParts(fields);
        Reference = new RecipeContractReference(Id, Version, ContentHash);
    }

    public string Id { get; }
    public string Version { get; }
    public CalibrationKind Kind { get; }
    public string LogicalPurpose { get; }
    public RecipeContractReference ProcedureContract { get; }
    public RecipeContractReference InputContract { get; }
    public RecipeContractReference CoefficientContract { get; }
    public RecipeContractReference ExtractionReceiptContract { get; }
    public RecipeContractReference ComputationEvidenceContract { get; }
    public CalibrationGateSection Sample { get; }
    public CalibrationGateSection Coverage { get; }
    public CalibrationGateSection PoseDiversity { get; }
    public CalibrationGateSection MaximumPerImageResidual { get; }
    public CalibrationGateSection MaximumPerPointResidual { get; }
    public CalibrationGateSection InvalidObservation { get; }
    public PhysicalCalibrationVerificationRequirement PhysicalVerification { get; }
    public ReadOnlyCollection<CalibrationGateSection> Sections { get; }
    public string ContentHash { get; }
    public RecipeContractReference Reference { get; }

    private static CalibrationGateSection RequireSection(CalibrationGateSection? value,
        CalibrationAcceptanceGateCategory expected, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Category != expected)
            throw new ArgumentException("CalibrationPolicySectionCategoryMismatch", parameterName);
        return value;
    }

    private static string ContractPart(RecipeContractReference contract) =>
        string.Join("|", contract.Id, contract.Version, contract.ContentHash);
}
