using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>Fixture provenance describes test data only; neither value represents a published local asset.</summary>
public enum CalibrationFixtureProvenance { Candidate = 0, Historical = 1 }

public sealed class CalibrationFixtureProfile
{
    public CalibrationFixtureProfile(Guid profileId, long version, CalibrationProfileContent content,
        CalibrationFixtureProvenance provenance)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (!Enum.IsDefined(typeof(CalibrationFixtureProvenance), provenance))
            throw new ArgumentOutOfRangeException(nameof(provenance));
        Reference = new(profileId, version, content.ContentHash);
        Provenance = provenance;
    }
    public CalibrationProfileReference Reference { get; }
    public CalibrationProfileContent Content { get; }
    public CalibrationFixtureProvenance Provenance { get; }
    public bool ProductionAuthority => false;
}

/// <summary>Explicit isolated test inputs. Lookup always uses the full profile identity; there is no filesystem search.</summary>
public sealed class CalibrationFixtureCatalog
{
    private readonly IReadOnlyDictionary<(Guid Id, long Version), CalibrationFixtureProfile> _profiles;
    public CalibrationFixtureCatalog(IEnumerable<CalibrationFixtureProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var copied = new Dictionary<(Guid, long), CalibrationFixtureProfile>();
        foreach (var profile in profiles)
        {
            if (copied.Count == 64 || profile is null ||
                !copied.TryAdd((profile.Reference.ProfileId, profile.Reference.Version), profile))
                throw new ArgumentException("CalibrationFixtureCatalogInvalid", nameof(profiles));
        }
        _profiles = copied;
    }
    internal CalibrationFixtureProfile? GetExact(CalibrationProfileReference reference, out string reason)
    {
        if (!_profiles.TryGetValue((reference.ProfileId, reference.Version), out var profile))
        { reason = "CalibrationProfileExactVersionMissing"; return null; }
        if (profile.Reference.ContentHash != reference.ContentHash)
        { reason = "CalibrationProfileContentHashMismatch"; return null; }
        reason = "CalibrationFixtureFound";
        return profile;
    }
}

/// <summary>
/// Checks exact compatibility using authorized current station projections and isolated fixture data.
/// It cannot provide production calibration authority; publication and current validity require a later trusted store.
/// </summary>
public sealed class CalibrationRequirementResolver : ICalibrationRequirementResolver
{
    private readonly ICameraSetupRuntime _cameras;
    private readonly IImagingSetupRuntime _imaging;
    private readonly CalibrationFixtureCatalog _fixtures;
    public CalibrationRequirementResolver(ICameraSetupRuntime cameras, IImagingSetupRuntime imaging,
        CalibrationFixtureCatalog? fixtures = null)
    {
        _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
        _imaging = imaging ?? throw new ArgumentNullException(nameof(imaging));
        _fixtures = fixtures ?? new CalibrationFixtureCatalog(Array.Empty<CalibrationFixtureProfile>());
    }

    public async ValueTask<CalibrationRequirementCheckResult> CheckAsync(RecipeDraftContent recipe,
        IEnumerable<CalibrationProfileSelection> selections, CommandInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        var hasRequirements = recipe.CalibrationRequirements.Count != 0 ||
            recipe.AssetRequirements.Any(item => item.Kind == RecipeAssetKind.Calibration) ||
            recipe.PolicyRequirements.Any(item => item.Kind == RecipePolicyKind.CalibrationAcceptance);
        CalibrationRequirementCheckResult Unavailable(string reason) =>
            new(false, hasRequirements, false, reason, Array.Empty<CalibrationRequirementObservation>());
        if (recipe.AssetRequirements.Any(item => item.Kind == RecipeAssetKind.Calibration))
            return Unavailable("RecipeLegacyCalibrationRequirementNeedsExplicitConversion");
        if (recipe.PolicyRequirements.Any(item => item.Kind == RecipePolicyKind.CalibrationAcceptance))
            return Unavailable("RecipeLegacyCalibrationPolicyNeedsExplicitConversion");
        var selected = new Dictionary<string, CalibrationProfileReference>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            if (selected.Count == 8 || selection is null ||
                !selected.TryAdd(selection.RequirementContentHash, selection.Profile))
                return Unavailable("CalibrationSelectionInvalid");
        }
        if (selected.Keys.Any(hash => !recipe.CalibrationRequirements.Any(value => value.ContentHash == hash)))
            return Unavailable("CalibrationSelectionRequirementMismatch");
        if (!hasRequirements)
            return new(true, false, true, "CalibrationNotRequired", Array.Empty<CalibrationRequirementObservation>());

        CameraSetupQueryResult camera;
        ImagingSetupQueryResult imaging;
        try
        {
            camera = await _cameras.GetSetupAsync(recipe.CameraRole, invocation, cancellationToken).ConfigureAwait(false);
            if (!camera.Available || camera.Snapshot is null)
                return Unavailable("CalibrationCameraSetupUnavailable");
            imaging = await _imaging.GetImagingSetupAsync(recipe.CameraRole, invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("CalibrationStationObservationUnavailable"); }
        if (!imaging.Available || imaging.Current is null) return Unavailable("ImagingSetupRevisionMissing");
        var setup = camera.Snapshot;
        var current = imaging.Current;
        if (setup.LogicalRole != recipe.CameraRole || current.LogicalCameraRole != recipe.CameraRole ||
            setup.Binding is null || setup.Binding.Revision != current.Binding.Revision ||
            setup.Binding.RevisionHash != current.Binding.RevisionHash || setup.Binding.Target != current.Binding.Target)
            return Unavailable("CalibrationCurrentBindingMismatch");
        if (setup.Requested is null || setup.Effective is null || setup.Health.Connection != CameraConnectionState.Open ||
            setup.Health.Configuration != CameraConfigurationState.Applied)
            return Unavailable("CalibrationEffectiveConfigurationUnavailable");

        var requested = CalibrationFrameGeometry.FromRequested(setup.Requested);
        if (requested != CalibrationFrameGeometry.FromRequested(recipe.Camera))
            return Unavailable("CalibrationCurrentRequestedGeometryMismatch");
        var effective = CalibrationFrameGeometry.FromEffective(setup.Effective);
        var revision = ImagingSetupRevisionReference.FromRevision(current);
        var observations = new List<CalibrationRequirementObservation>();
        foreach (var requirement in recipe.CalibrationRequirements.OrderBy(value => value.ContentHash, StringComparer.Ordinal))
        {
            if (!selected.TryGetValue(requirement.ContentHash, out var reference))
            {
                observations.Add(new(requirement.ContentHash, false, "CalibrationProfileSelectionRequired"));
                continue;
            }
            var fixture = _fixtures.GetExact(reference, out var reason);
            if (fixture is not null)
            {
                var content = fixture.Content;
                reason = content.Requirement.Kind != requirement.Kind ||
                    content.Requirement.LogicalCameraRole != requirement.LogicalCameraRole ||
                    content.Requirement.LogicalPurpose != requirement.LogicalPurpose
                    ? "CalibrationRequirementMismatch"
                    : content.Coefficients.Format != requirement.CoefficientContract ? "CalibrationCoefficientContractMismatch"
                    : content.Requirement.AcceptancePolicy != requirement.AcceptancePolicy ? "CalibrationAcceptancePolicyMismatch"
                    : content.Device != setup.Binding.Target ? "CalibrationDeviceIdentityMismatch"
                    : content.ImagingSetup != revision ? "CalibrationImagingSetupRevisionMismatch"
                    : content.RequestedGeometry != requested ? "CalibrationRequestedGeometryMismatch"
                    : content.EffectiveGeometry != effective ? "CalibrationEffectiveGeometryMismatch"
                    : "CalibrationCompatibleDevelopmentOnly";
            }
            observations.Add(new(requirement.ContentHash, reason == "CalibrationCompatibleDevelopmentOnly", reason, reference));
        }
        var compatible = observations.All(value => value.Compatible);
        return new(true, true, compatible, compatible ? "CalibrationProfileAuthorityUnavailable" :
            observations.First(value => !value.Compatible).ReasonCode, observations, revision);
    }
}
