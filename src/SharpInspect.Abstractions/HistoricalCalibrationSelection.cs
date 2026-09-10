using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// The only source currently permitted for a historical calibration selection.
/// Imported calibration provenance is a separate record and never becomes this
/// selection source by convention or by caller supplied text.
/// </summary>
public static class HistoricalCalibrationSelectionSources
{
    public const string LocalProfileHistory = "LocalProfileHistory";
}

/// <summary>
/// Immutable, explicitly authorized intent to select a historical calibration
/// profile as part of a new recipe activation.  The selected profile and
/// requirement are carried by <see cref="ActivateRecipeCommand.CalibrationSelections"/>;
/// this value binds the source, prior exact profile and operator reason.
/// </summary>
public sealed record HistoricalCalibrationSelectionIntent
{
    public HistoricalCalibrationSelectionIntent(string source,
        CalibrationProfileReference? previousExactProfile, string reason)
    {
        if (!string.Equals(source, HistoricalCalibrationSelectionSources.LocalProfileHistory,
                StringComparison.Ordinal))
            throw new ArgumentException("HistoricalCalibrationSelectionSourceInvalid", nameof(source));
        Source = source;
        PreviousExactProfile = previousExactProfile is null ? null :
            new CalibrationProfileReference(previousExactProfile.ProfileId,
                previousExactProfile.Version, previousExactProfile.ContentHash);
        Reason = RecipeActivationValidation.Reason(reason, nameof(reason));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-historical-calibration-selection-intent-v1", Source,
            PreviousExactProfile?.ProfileId.ToString("D"),
            PreviousExactProfile?.Version.ToString(CultureInfo.InvariantCulture),
            PreviousExactProfile?.ContentHash, Reason
        });
    }

    public string Source { get; }
    public CalibrationProfileReference? PreviousExactProfile { get; }
    public string Reason { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Explicit historical-profile selection.  It derives from the existing
/// activation command so the original preparation, physical staging, rollback,
/// commit CAS and query paths remain the only Active state machine.
/// </summary>
public sealed record SelectHistoricalCalibrationCommand : ActivateRecipeCommand
{
    public SelectHistoricalCalibrationCommand(Guid correlationId, CommandInvocation invocation,
        RecipeReference candidate, Guid releaseId, string releaseRecordContentHash,
        RecipeActivationReference? expectedActive,
        IEnumerable<CalibrationProfileSelection>? calibrationSelections,
        HistoricalCalibrationSelectionIntent historicalSelection,
        Guid? operationId = null)
        : base(correlationId, invocation, candidate, releaseId, releaseRecordContentHash,
            expectedActive, calibrationSelections, RequireIntent(historicalSelection).Reason,
            RequireIntent(historicalSelection), operationId)
    {
    }

    private static HistoricalCalibrationSelectionIntent RequireIntent(
        HistoricalCalibrationSelectionIntent? value) => value ??
        throw new ArgumentNullException(nameof(value));
}
