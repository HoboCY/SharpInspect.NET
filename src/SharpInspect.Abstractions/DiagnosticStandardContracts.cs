namespace SharpInspect.Abstractions;

/// <summary>Mandatory closed Runtime events. No exception message, stack, path or arbitrary type name.</summary>
public static class DiagnosticStandardContracts
{
    private static readonly string[] Owners = { "AlgorithmExecution", "ManagedHost" };
    private static readonly string[] Categories = { "Cancellation", "Contract", "IO", "Timeout", "Unknown" };
    /// <summary>Groups approved fault categories; it never hashes exception text or secret material.</summary>
    public static string Fingerprint(string owner, string category)
    {
        if (!Owners.Contains(owner, StringComparer.Ordinal) || !Categories.Contains(category, StringComparer.Ordinal))
            throw new ArgumentException("DiagnosticFaultCategoryInvalid");
        return AlgorithmContractValidation.HashParts(new[] { "diagnostic-fault-fingerprint-v1", owner, category });
    }
    public static DiagnosticEventContract OwnerFault { get; } = new("Runtime.OwnerFault", 1, "Runtime",
        DiagnosticLevel.Error, false, new[]
        {
            new DiagnosticFieldContract("Owner", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true,
                null, null, new[] { "AlgorithmExecution", "ManagedHost" }),
            new DiagnosticFieldContract("Category", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true,
                null, null, new[] { "Cancellation", "Contract", "IO", "Timeout", "Unknown" }),
            new DiagnosticFieldContract("Fingerprint", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true,
                null, null, Owners.SelectMany(owner => Categories.Select(category => Fingerprint(owner, category)))),
            new DiagnosticFieldContract("HResult", DiagnosticScalarKind.Int64, DiagnosticDataClass.Protected, true,
                int.MinValue, int.MaxValue, null)
        });
    public static DiagnosticEventContract Drops { get; } = new("Runtime.DiagnosticDrops", 1, "Runtime",
        DiagnosticLevel.Warning, false, new[]
        {
            new DiagnosticFieldContract("Count", DiagnosticScalarKind.Int64, DiagnosticDataClass.Safe, true,
                1, int.MaxValue, null)
        });
    public static DiagnosticEventContract AlgorithmOutcome { get; } = new("Runtime.AlgorithmOutcome", 1, "Runtime",
        DiagnosticLevel.Information, false, new[]
        {
            new DiagnosticFieldContract("Status", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true,
                null, null, Enum.GetNames<ExecutionStatus>()),
            new DiagnosticFieldContract("Decision", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true,
                null, null, Enum.GetNames<InspectionDecision>())
        });
    public static IReadOnlyList<DiagnosticEventContract> All { get; } = Array.AsReadOnly(new[] { OwnerFault, Drops, AlgorithmOutcome });
}
