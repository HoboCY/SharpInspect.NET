using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

internal sealed record RecipeActivationCalibrationObservation(string CheckId, string Subject,
    bool Passed, string ReasonCode, string? EvidenceHash = null);

internal sealed record RecipeActivationCalibrationEvaluation(bool Allowed, string ReasonCode,
    IReadOnlyList<RecipeActivationCalibrationObservation> Observations,
    IReadOnlyList<CalibrationRunProfileBinding> Bindings, long LedgerPosition = 0,
    string? LedgerContentHash = null, ImagingSetupRevisionReference? ImagingSetup = null,
    long ImportLedgerPosition = 0, string? ImportLedgerContentHash = null);

/// <summary>
/// Reads governed authority inside an already authorized Runtime activation. It never treats
/// a development profile, geometry match, or caller-supplied observation as activation authority.
/// </summary>
internal sealed class RecipeActivationCalibrationEvaluator
{
    private readonly SqliteCommandStore _store;

    internal RecipeActivationCalibrationEvaluator(SqliteCommandStore store) => _store = store;

    internal async ValueTask<RecipeActivationCalibrationEvaluation> EvaluateAsync(RecipeDraftContent recipe,
        IReadOnlyList<CalibrationProfileSelection> selections, CameraSetupSnapshot? camera,
        DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        if (CheckInput(recipe, selections) is { } failure) return Failure(failure);
        if (recipe.CalibrationRequirements.Count == 0)
            return new(true, "CalibrationNotRequired", Array.Empty<RecipeActivationCalibrationObservation>(),
                Array.Empty<CalibrationRunProfileBinding>());

        if (_store.CalibrationImportEnabled)
        {
            var selectedProfiles = selections.Select(value => value.Profile).Distinct().ToArray();
            var catalog = await _store.ReadCalibrationImportActivationCatalogAsync(recipe.CameraRole,
                selectedProfiles, cancellationToken).ConfigureAwait(false);
            if (!catalog.Available)
                return Failure(catalog.ReasonCode);
            return EvaluateRecords(recipe, selections, camera, catalog.Imaging, catalog.GovernanceRecords,
                atUtc, catalog.GovernancePosition, catalog.GovernanceContentHash, catalog);
        }

        var ledger = await _store.ReadCalibrationGovernanceAsync(cancellationToken).ConfigureAwait(false);
        if (!ledger.Available) return Failure(ledger.ReasonCode);
        ImagingSetupRevision? imaging;
        try
        {
            imaging = (await _store.ReadImagingSetupAsync(recipe.CameraRole, cancellationToken)
                .ConfigureAwait(false)).State.Current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Failure("CalibrationImagingSetupUnavailable"); }
        var after = await _store.ReadCalibrationGovernanceAsync(cancellationToken).ConfigureAwait(false);
        if (!after.Available || after.Records.Count != ledger.Records.Count ||
            after.Records.LastOrDefault()?.RecordContentHash != ledger.Records.LastOrDefault()?.RecordContentHash)
            return Failure("CalibrationGovernanceChangedDuringActivation");
        var records = ledger.Records.Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload)).ToArray();
        return EvaluateRecords(recipe, selections, camera, imaging, records, atUtc,
            ledger.Records.LastOrDefault()?.Position ?? 0, ledger.Records.LastOrDefault()?.RecordContentHash);
    }

    internal static RecipeActivationCalibrationEvaluation EvaluateRecords(RecipeDraftContent recipe,
        IReadOnlyList<CalibrationProfileSelection> selections, CameraSetupSnapshot? camera,
        ImagingSetupRevision? imaging, IReadOnlyList<object> records, DateTimeOffset atUtc,
        long ledgerPosition = 0, string? ledgerContentHash = null,
        CalibrationImportActivationCatalogSnapshot? importCatalog = null)
    {
        if (CheckInput(recipe, selections) is { } inputFailure) return Failure(inputFailure);
        ArgumentNullException.ThrowIfNull(records);
        if (recipe.CalibrationRequirements.Count == 0)
            return new(true, "CalibrationNotRequired", Array.Empty<RecipeActivationCalibrationObservation>(),
                Array.Empty<CalibrationRunProfileBinding>());
        var observations = new List<RecipeActivationCalibrationObservation>();
        var bindings = new List<CalibrationRunProfileBinding>();
        var now = atUtc.ToUniversalTime();
        foreach (var requirement in recipe.CalibrationRequirements.OrderBy(value => value.ContentHash, StringComparer.Ordinal))
        {
            var subject = requirement.ContentHash;
            void Observe(string id, bool passed, string passReason, string failureReason, string? hash = null) =>
                observations.Add(new(id, subject, passed, passed ? passReason : failureReason, hash));
            var selection = selections.SingleOrDefault(value => value.RequirementContentHash == subject);
            CalibrationImportedProfileResolution? imported = null;
            if (selection is not null && importCatalog is not null &&
                importCatalog.TryGetImportedProfile(selection.Profile, out var importedResolution))
                imported = importedResolution;
            var profile = imported is null && selection is not null
                ? CalibrationGovernanceProjection.Profile(records, selection.Profile) : null;
            Observe("V132.K01", profile is not null || imported is not null,
                "CalibrationProfileExactVersionResolved",
                selection is null ? "CalibrationProfileSelectionRequired" : "CalibrationProfileExactVersionMissing",
                profile?.ContentHash ?? imported?.Profile.ContentHash);
            if (profile is null && imported is null) continue;

            if (imported is not null)
            {
                var importedProfile = imported.Profile;
                var importedContent = importedProfile.Content;
                var importedRequirement = importedContent.Requirement;
                var importedCompatibleRequirement = importedRequirement.LogicalCameraRole == requirement.LogicalCameraRole &&
                    importedRequirement.Kind == requirement.Kind &&
                    importedRequirement.LogicalPurpose == requirement.LogicalPurpose &&
                    importedRequirement.CoefficientContract == requirement.CoefficientContract &&
                    importedContent.Coefficients.Format == requirement.CoefficientContract &&
                    importedRequirement.AcceptancePolicy == requirement.AcceptancePolicy;
                Observe("V132.K02", importedCompatibleRequirement, "CalibrationRequirementBound",
                    "CalibrationRequirementMismatch", importedContent.ContentHash);

                var importedPolicyHead = records.OfType<CalibrationAcceptancePolicyRevision>()
                    .Where(value => value.Policy.Reference.Id == requirement.AcceptancePolicy.Id)
                    .OrderBy(value => value.Position).LastOrDefault();
                var importedPolicy = importedPolicyHead?.Policy;
                var importedCurrentPolicy = importedPolicy is not null &&
                    importedPolicy.Reference == requirement.AcceptancePolicy &&
                    importedPolicy.Reference == importedRequirement.AcceptancePolicy &&
                    importedPolicyHead is not null && importedPolicyHead.RecordedAtUtc <= now;
                Observe("V132.K03", importedCurrentPolicy, "CalibrationCurrentPolicyBound",
                    "CalibrationPolicyHeadChanged", importedPolicy?.Reference.ContentHash);

                var importedEvaluation = imported.Evaluation;
                var importedEvaluationValid = importedEvaluation.Passed &&
                    importedEvaluation.Candidate == imported.Candidate.Reference &&
                    importedEvaluation.Content.Requirement.ContentHash == importedContent.Requirement.ContentHash &&
                    importedEvaluation.Content.Device == importedContent.Device &&
                    importedEvaluation.Content.ImagingSetup == importedContent.ImagingSetup &&
                    importedEvaluation.RecordedAtUtc <= importedProfile.RecordedAtUtc &&
                    importedProfile.RecordedAtUtc <= now;
                Observe("V132.K04", importedEvaluationValid, "CalibrationEvaluationBound",
                    "CalibrationProfileEvaluationMismatch", importedEvaluation.ContentHash);

                var physical = imported.PhysicalVerification;
                var importedPhysicalNotRequired = importedCurrentPolicy && importedPolicy!.PhysicalVerification.Applicability ==
                    CalibrationPolicyApplicability.NotApplicable;
                var importedPhysicalCurrent = importedPhysicalNotRequired || importedCurrentPolicy && physical is { Passed: true } &&
                    physical.Candidate == imported.Candidate.Reference &&
                    physical.Evaluation == imported.Evaluation.Reference &&
                    physical.RecordedAtUtc <= importedProfile.RecordedAtUtc &&
                    importedProfile.RecordedAtUtc <= now &&
                    physical.ValidUntilUtc is { } importedExpiry && now < importedExpiry;
                var importedPhysicalFailure = !importedCurrentPolicy ? "CalibrationPolicyHeadChanged" :
                    physical is null ? "CalibrationPhysicalVerificationMissing" :
                    physical.Candidate != imported.Candidate.Reference ||
                    physical.Evaluation != imported.Evaluation.Reference
                        ? "CalibrationPhysicalVerificationPolicyMismatch" :
                    !physical.Passed ? "CalibrationPhysicalVerificationFailed" :
                    physical.RecordedAtUtc > now ? "CalibrationPhysicalVerificationTimeInvalid" :
                    "CalibrationVerificationOverdue";
                Observe("V132.K05", importedPhysicalCurrent,
                    importedPhysicalNotRequired ? "CalibrationPhysicalVerificationNotRequired" :
                        "CalibrationPhysicalVerificationCurrent", importedPhysicalFailure,
                    physical?.ContentHash);

                var importedDeviceMatches = camera?.Binding?.Target == importedContent.Device &&
                    imported.Evaluation.Binding == camera?.Binding;
                Observe("V132.K06", importedDeviceMatches, "CalibrationDeviceBound",
                    "CalibrationDeviceIdentityMismatch", camera?.Binding?.RevisionHash);
                var importedImagingReference = imaging is null ? null : ImagingSetupRevisionReference.FromRevision(imaging);
                var importedImagingMatches = importedImagingReference == importedContent.ImagingSetup &&
                    imaging?.Binding == camera?.Binding;
                Observe("V132.K07", importedImagingMatches, "CalibrationImagingRevisionBound",
                    "CalibrationImagingSetupRevisionMismatch", importedImagingReference?.RevisionHash);
                var importedRequestedMatches = camera?.Requested is { } importedRequested &&
                    CalibrationFrameGeometry.FromRequested(importedRequested) == importedContent.RequestedGeometry &&
                    CalibrationFrameGeometry.FromRequested(recipe.Camera) == importedContent.RequestedGeometry;
                Observe("V132.K08", importedRequestedMatches, "CalibrationRequestedGeometryBound",
                    "CalibrationRequestedGeometryMismatch");
                var importedEffectiveMatches = camera is { Effective: not null } &&
                    camera.Health.Connection == CameraConnectionState.Open &&
                    camera.Health.Configuration == CameraConfigurationState.Applied &&
                    CalibrationFrameGeometry.FromEffective(camera.Effective) == importedContent.EffectiveGeometry;
                Observe("V132.K09", importedEffectiveMatches, "CalibrationEffectiveGeometryBound",
                    "CalibrationEffectiveGeometryMismatch");

                // Imported profiles are intentionally local development artifacts. Their
                // exact lineage is retained above, but it can never become production
                // activation authority through this evaluator.
                var importedAuthority = importedProfile.ProductionAuthority && importedProfile.CanActivate &&
                    !importedProfile.DevelopmentOnly;
                Observe("V132.K10", importedAuthority, "CalibrationProductionAuthorityVerified",
                    "CalibrationProfileNotQualified", importedProfile.ContentHash);
                continue;
            }

            if (profile is null) continue;

            var compatibleRequirement = profile.Content.Requirement.LogicalCameraRole == requirement.LogicalCameraRole &&
                profile.Content.Requirement.Kind == requirement.Kind &&
                profile.Content.Requirement.LogicalPurpose == requirement.LogicalPurpose &&
                profile.Content.Requirement.CoefficientContract == requirement.CoefficientContract &&
                profile.Content.Coefficients.Format == requirement.CoefficientContract &&
                profile.AcceptancePolicy == requirement.AcceptancePolicy;
            Observe("V132.K02", compatibleRequirement, "CalibrationRequirementBound",
                "CalibrationRequirementMismatch", profile.Content.ContentHash);
            var policyHead = records.OfType<CalibrationAcceptancePolicyRevision>()
                .Where(value => value.Policy.Reference.Id == requirement.AcceptancePolicy.Id)
                .OrderBy(value => value.Position).LastOrDefault();
            var policy = policyHead?.Policy;
            var currentPolicy = policy?.Reference == requirement.AcceptancePolicy &&
                policy.Reference == profile.AcceptancePolicy &&
                policyHead!.RecordedAtUtc <= now;
            Observe("V132.K03", currentPolicy, "CalibrationCurrentPolicyBound",
                "CalibrationPolicyHeadChanged", policy?.Reference.ContentHash);

            var evaluation = records.OfType<CalibrationPolicyEvaluationRecord>()
                .SingleOrDefault(value => value.Reference == profile.Evaluation);
            var evaluationValid = evaluation is { Passed: true } && evaluation.Policy == profile.AcceptancePolicy &&
                evaluation.Candidate == profile.SourceCandidate && evaluation.RecordedAtUtc <= profile.RecordedAtUtc &&
                profile.RecordedAtUtc <= now;
            Observe("V132.K04", evaluationValid, "CalibrationEvaluationBound",
                "CalibrationProfileEvaluationMismatch", evaluation?.ContentHash);

            var latest = records.OfType<PhysicalCalibrationVerificationRecord>()
                .Where(value => value.Profile == profile.Reference).OrderBy(value => value.Position).LastOrDefault();
            var physicalNotRequired = currentPolicy &&
                policy!.PhysicalVerification.Applicability == CalibrationPolicyApplicability.NotApplicable;
            var physicalCurrent = physicalNotRequired || currentPolicy && latest is { Passed: true } &&
                latest.Policy == profile.AcceptancePolicy && latest.RecordedAtUtc <= now &&
                latest.ValidUntilUtc is { } expiry && now < expiry;
            var physicalFailure = !currentPolicy ? "CalibrationPolicyHeadChanged" :
                latest is null ? "CalibrationPhysicalVerificationMissing" :
                latest.Policy != profile.AcceptancePolicy ? "CalibrationPhysicalVerificationPolicyMismatch" :
                !latest.Passed ? "CalibrationPhysicalVerificationFailed" :
                latest.RecordedAtUtc > now ? "CalibrationPhysicalVerificationTimeInvalid" : "CalibrationVerificationOverdue";
            Observe("V132.K05", physicalCurrent,
                physicalNotRequired ? "CalibrationPhysicalVerificationNotRequired" : "CalibrationPhysicalVerificationCurrent",
                physicalFailure, latest?.ContentHash);

            var deviceMatches = camera?.Binding?.Target == profile.Content.Device;
            Observe("V132.K06", deviceMatches, "CalibrationDeviceBound", "CalibrationDeviceIdentityMismatch",
                camera?.Binding?.RevisionHash);
            var imagingReference = imaging is null ? null : ImagingSetupRevisionReference.FromRevision(imaging);
            var imagingMatches = imagingReference == profile.Content.ImagingSetup &&
                imaging?.Binding == camera?.Binding;
            Observe("V132.K07", imagingMatches, "CalibrationImagingRevisionBound",
                "CalibrationImagingSetupRevisionMismatch", imagingReference?.RevisionHash);
            var requestedMatches = camera?.Requested is { } requested &&
                CalibrationFrameGeometry.FromRequested(requested) == profile.Content.RequestedGeometry &&
                CalibrationFrameGeometry.FromRequested(recipe.Camera) == profile.Content.RequestedGeometry;
            Observe("V132.K08", requestedMatches, "CalibrationRequestedGeometryBound",
                "CalibrationRequestedGeometryMismatch");
            var effectiveMatches = camera is { Effective: not null } &&
                camera.Health.Connection == CameraConnectionState.Open &&
                camera.Health.Configuration == CameraConfigurationState.Applied &&
                CalibrationFrameGeometry.FromEffective(camera.Effective) == profile.Content.EffectiveGeometry;
            Observe("V132.K09", effectiveMatches, "CalibrationEffectiveGeometryBound",
                "CalibrationEffectiveGeometryMismatch");

            // T27 records intentionally carry development provenance. Geometry/expiry checks
            // cannot turn them into a production-eligible calibration profile.
            var authority = profile.ProductionAuthority && profile.CanActivate && !profile.DevelopmentOnly;
            Observe("V132.K10", authority, "CalibrationProductionAuthorityVerified",
                "CalibrationProfileProductionAuthorityUnavailable", profile.ContentHash);
            if (observations.Where(value => value.Subject == subject).All(value => value.Passed))
                bindings.Add(new(subject, profile, latest?.Reference, physicalNotRequired ? null : latest?.ValidUntilUtc));
        }
        var failure = observations.FirstOrDefault(value => !value.Passed)?.ReasonCode;
        return new(failure is null, failure ?? "CalibrationActivationDependenciesVerified",
            observations.AsReadOnly(), bindings.AsReadOnly(), ledgerPosition, ledgerContentHash,
            imaging is null ? null : ImagingSetupRevisionReference.FromRevision(imaging),
            importCatalog?.ImportPosition ?? 0, importCatalog?.ImportContentHash);
    }

    private static RecipeActivationCalibrationEvaluation Failure(string reason) => new(false, reason,
        new[] { new RecipeActivationCalibrationObservation("V132.K00", "Calibration", false, reason) },
        Array.Empty<CalibrationRunProfileBinding>());

    private static string? CheckInput(RecipeDraftContent recipe, IReadOnlyList<CalibrationProfileSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(selections);
        if (recipe.AssetRequirements.Any(value => value.Kind == RecipeAssetKind.Calibration) ||
            recipe.PolicyRequirements.Any(value => value.Kind == RecipePolicyKind.CalibrationAcceptance))
            return "CalibrationLegacyRequirementUnavailable";
        if (selections.Count > 8 || selections.Any(value => value is null) ||
            selections.Select(value => value.RequirementContentHash).Distinct(StringComparer.Ordinal).Count() != selections.Count ||
            selections.Any(value => !recipe.CalibrationRequirements.Any(requirement =>
                requirement.ContentHash == value.RequirementContentHash)))
            return "CalibrationSelectionRequirementMismatch";
        return null;
    }
}
