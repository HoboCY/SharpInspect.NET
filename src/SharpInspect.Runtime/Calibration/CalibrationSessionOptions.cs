using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Calibration;

/// <summary>Bounds procedure calls; a deadline never releases a frame still borrowed by an actual call.</summary>
public sealed class CalibrationSessionOptions
{
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public DevelopmentCalibrationFixture? DevelopmentFixture { get; init; }
    internal void Validate()
    {
        if (OperationTimeout < TimeSpan.FromMilliseconds(50) || OperationTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));
    }
}

/// <summary>
/// Exact isolated Virtual Camera demonstration scope. Its evidence cannot assert PLC safety,
/// a released recipe, profile publication, qualification, production acceptance, or Ready.
/// </summary>
public sealed class DevelopmentCalibrationFixture
{
    public DevelopmentCalibrationFixture(string fixtureId, CameraBindingRevision binding,
        ImagingSetupRevisionReference imagingSetup, RequestedCameraConfiguration baselineRequested,
        EffectiveCameraConfiguration baselineEffective, CalibrationSessionPlan plan,
        ProductionStoreOptions isolatedStore)
    {
        FixtureId = AlgorithmContractValidation.BoundedText(fixtureId, nameof(fixtureId), 128);
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ImagingSetup = imagingSetup ?? throw new ArgumentNullException(nameof(imagingSetup));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentNullException.ThrowIfNull(isolatedStore);
        if (binding.Target.Provider.Id != "SharpInspect.Virtual" ||
            binding.Target.Provider.AdapterPackageId != "SharpInspect.NET.Cameras.Virtual" ||
            binding.LogicalRole != imagingSetup.LogicalCameraRole ||
            binding.LogicalRole != plan.Requirement.LogicalCameraRole ||
            isolatedStore.CalibrationSessions is null)
            throw new ArgumentException("CalibrationDevelopmentFixtureScopeInvalid");
        BaselineRequestedHash = CalibrationSessionContractHash.Configuration(baselineRequested);
        BaselineEffectiveHash = CalibrationSessionContractHash.Configuration(baselineEffective);
        DatabasePath = Path.GetFullPath(isolatedStore.DatabasePath);
        EvidenceRoot = Path.GetFullPath(isolatedStore.CalibrationSessions.EvidenceRoot);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-isolated-development-calibration-v1", FixtureId, binding.Target.ContentHash,
            binding.RevisionHash, imagingSetup.RevisionHash, plan.ContentHash, BaselineRequestedHash,
            BaselineEffectiveHash, DatabasePath, EvidenceRoot, "ProductionOutputsAbsent",
            "PlcSafetyNotApplicableToIsolatedFixture", "ReleasedRecipeNotApplicableToIsolatedFixture"
        });
    }
    public string FixtureId { get; }
    public CameraBindingRevision Binding { get; }
    public ImagingSetupRevisionReference ImagingSetup { get; }
    public CalibrationSessionPlan Plan { get; }
    public string BaselineRequestedHash { get; }
    public string BaselineEffectiveHash { get; }
    public string DatabasePath { get; }
    public string EvidenceRoot { get; }
    public string ContentHash { get; }
    public bool DevelopmentOnly => true;
    public bool ProductionAuthority => false;

    internal bool Matches(StartCalibrationSessionCommand command, CameraBindingRevision current,
        ImagingSetupRevisionReference imaging, RequestedCameraConfiguration requested,
        EffectiveCameraConfiguration effective, ProductionStoreOptions store) =>
        command.Plan.ContentHash == Plan.ContentHash && current.RevisionHash == Binding.RevisionHash &&
        current.Target.ContentHash == Binding.Target.ContentHash && current.LogicalRole == Binding.LogicalRole &&
        command.ExpectedBindingRevision == current.Revision && command.ExpectedBindingRevisionHash == current.RevisionHash &&
        imaging == ImagingSetup && command.ExpectedImagingSetup == imaging &&
        CalibrationSessionContractHash.Configuration(requested) == BaselineRequestedHash &&
        CalibrationSessionContractHash.Configuration(effective) == BaselineEffectiveHash &&
        Path.GetFullPath(store.DatabasePath) == DatabasePath && store.CalibrationSessions is not null &&
        Path.GetFullPath(store.CalibrationSessions.EvidenceRoot) == EvidenceRoot;
}
