using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// The exact imported profile and its local provenance records selected from one
/// verified SQLite snapshot.  The imported record remains a development artifact;
/// this type does not grant activation authority.
/// </summary>
internal sealed record CalibrationImportedProfileResolution(
    PublishedImportedCalibrationProfile Profile,
    ImportedCalibrationCandidate Candidate,
    ImportedCalibrationEvaluation Evaluation,
    ImportedCalibrationPhysicalVerification? PhysicalVerification)
{
    internal CalibrationProfileReference Reference => Profile.Reference;
}

/// <summary>
/// A read-only calibration authority view.  Governance and import tails describe
/// the same SQLite transaction and are retained for a later activation CAS check.
/// </summary>
internal sealed record CalibrationImportActivationCatalogSnapshot(
    bool Available,
    string ReasonCode,
    IReadOnlyList<object> GovernanceRecords,
    IReadOnlyList<CalibrationImportRecord> ImportRecords,
    ImagingSetupRevision? Imaging,
    IReadOnlyDictionary<CalibrationProfileReference, CalibrationImportedProfileResolution> ImportedProfiles,
    long GovernancePosition,
    string? GovernanceContentHash,
    long ImportPosition,
    string? ImportContentHash)
{
    internal static CalibrationImportActivationCatalogSnapshot Unavailable(string reasonCode) =>
        new(false, reasonCode, Array.Empty<object>(), Array.Empty<CalibrationImportRecord>(), null,
            new ReadOnlyDictionary<CalibrationProfileReference, CalibrationImportedProfileResolution>(
                new Dictionary<CalibrationProfileReference, CalibrationImportedProfileResolution>()),
            0, null, 0, null);

    internal bool TryGetImportedProfile(CalibrationProfileReference reference,
        out CalibrationImportedProfileResolution? resolution)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (ImportedProfiles.TryGetValue(reference, out var value))
        {
            resolution = value;
            return true;
        }

        resolution = null;
        return false;
    }

    internal bool TailsMatch(long governancePosition, string? governanceContentHash,
        long importPosition, string? importContentHash) =>
        GovernancePosition == governancePosition &&
        string.Equals(GovernanceContentHash, governanceContentHash, StringComparison.Ordinal) &&
        ImportPosition == importPosition &&
        string.Equals(ImportContentHash, importContentHash, StringComparison.Ordinal);
}

/// <summary>Builds an exact imported-profile catalog from already verified ledger rows.</summary>
internal static class CalibrationImportActivationCatalog
{
    internal static CalibrationImportActivationCatalogSnapshot Create(
        IReadOnlyList<object> governanceRecords,
        IReadOnlyList<CalibrationImportRecord> importRecords,
        ImagingSetupRevision? imaging,
        IReadOnlyList<CalibrationProfileReference> selectedProfiles,
        long governancePosition,
        string? governanceContentHash,
        long importPosition,
        string? importContentHash)
    {
        ArgumentNullException.ThrowIfNull(governanceRecords);
        ArgumentNullException.ThrowIfNull(importRecords);
        ArgumentNullException.ThrowIfNull(selectedProfiles);
        if (selectedProfiles.Count > 8)
            throw new ArgumentOutOfRangeException(nameof(selectedProfiles));
        if (governancePosition < 0 || importPosition < 0)
            throw new ArgumentOutOfRangeException(nameof(governancePosition));

        var candidates = importRecords.OfType<ImportedCalibrationCandidate>()
            .GroupBy(value => value.Reference).ToArray();
        var evaluations = importRecords.OfType<ImportedCalibrationEvaluation>()
            .GroupBy(value => value.Reference).ToArray();
        var physical = importRecords.OfType<ImportedCalibrationPhysicalVerification>()
            .GroupBy(value => value.Reference).ToArray();
        var publications = importRecords.OfType<PublishedImportedCalibrationProfile>()
            .GroupBy(value => value.Reference).ToArray();

        RequireNoDuplicate(candidates, "CalibrationImportActivationCandidateDuplicate");
        RequireNoDuplicate(evaluations, "CalibrationImportActivationEvaluationDuplicate");
        RequireNoDuplicate(physical, "CalibrationImportActivationPhysicalDuplicate");
        RequireNoDuplicate(publications, "CalibrationImportActivationProfileDuplicate");

        var candidateByReference = candidates.ToDictionary(value => value.Key, value => value.Single());
        var evaluationByReference = evaluations.ToDictionary(value => value.Key, value => value.Single());
        var physicalByReference = physical.ToDictionary(value => value.Key, value => value.Single());
        var publicationByReference = publications.ToDictionary(value => value.Key, value => value.Single());
        var selected = new Dictionary<CalibrationProfileReference, CalibrationImportedProfileResolution>();

        foreach (var reference in selectedProfiles)
        {
            ArgumentNullException.ThrowIfNull(reference);
            if (!publicationByReference.TryGetValue(reference, out var profile))
                continue;

            if (!candidateByReference.TryGetValue(profile.Candidate, out var candidate))
                throw new InvalidOperationException("CalibrationImportActivationCandidateMissing");
            if (!evaluationByReference.TryGetValue(profile.Evaluation, out var evaluation) ||
                evaluation.Candidate != profile.Candidate)
                throw new InvalidOperationException("CalibrationImportActivationEvaluationBindingMismatch");

            ImportedCalibrationPhysicalVerification? verification = null;
            if (profile.PhysicalVerification is { } physicalReference)
            {
                if (!physicalByReference.TryGetValue(physicalReference, out verification) ||
                    verification.Candidate != profile.Candidate || verification.Evaluation != profile.Evaluation)
                    throw new InvalidOperationException("CalibrationImportActivationPhysicalBindingMismatch");
            }

            if (!selected.TryAdd(reference,
                    new CalibrationImportedProfileResolution(profile, candidate, evaluation, verification)))
                throw new InvalidOperationException("CalibrationImportActivationProfileSelectionDuplicate");
        }

        return new(true, "CalibrationImportActivationCatalogAvailable",
            new ReadOnlyCollection<object>(governanceRecords.ToArray()),
            new ReadOnlyCollection<CalibrationImportRecord>(importRecords.ToArray()), imaging,
            new ReadOnlyDictionary<CalibrationProfileReference, CalibrationImportedProfileResolution>(selected),
            governancePosition, governanceContentHash, importPosition, importContentHash);
    }

    private static void RequireNoDuplicate<TKey, T>(IEnumerable<IGrouping<TKey, T>> groups,
        string reason)
    {
        if (groups.Any(value => value.Count() != 1))
            throw new InvalidOperationException(reason);
    }
}
