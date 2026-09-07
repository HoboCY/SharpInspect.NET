using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>
/// Fine-grained permissions used by governed human actions. Numeric values are
/// stable contract values; display claims and role labels never confer one of
/// these permissions.
/// </summary>
public enum Permission : ushort
{
    None = 0,
    ManageAccounts = 1,
    ManagePermissions = 2,
    UnlockCredential = 3,
    RebindCredential = 4,
    ArmProduction = 5,
    ActivateRecipe = 6,
    ReleaseRecipe = 7,
    RetireRecipe = 8,
    ManageRecipeSelectionMap = 9,
    ManagePlcResultContract = 10,
    PublishCalibration = 11,
    SelectHistoricalCalibration = 12,
    ManageCameraBindings = 13,
    ManageProductionPolicy = 14,
    ManageRecipeTrustStore = 15,
    ManageRecipeSigningKeys = 16,
    ManageDeploymentTrustStore = 17,
    ManageBackupPolicy = 18,
    UpgradeDeployment = 19,
    RestoreStation = 20,
    RunStationQualification = 21,
    ApproveStationProductionAcceptance = 22,
    RunDiagnostics = 23,
    ExportProtectedDiagnostics = 24,
    ManualRecovery = 25,
    ManageAuditSigningKeys = 26,
    CorrectHistoricalFact = 27,
    DeleteEvidence = 28
}

/// <summary>
/// Convenient role bundles for assigning permissions. A bundle is not an
/// identity, an authorization result, or a substitute for a permission check.
/// </summary>
public enum HumanRoleBundle : byte
{
    Operator = 1,
    Technician = 2,
    Administrator = 3
}

/// <summary>
/// Capabilities of non-interactive trusted services. These values are
/// intentionally separate from <see cref="Permission"/> and cannot authorize
/// a human action or create an interactive session.
/// </summary>
public enum SystemPermission : byte
{
    None = 0,
    RecordCommand = 1,
    DeliverOutbox = 2,
    FinalizeEvidence = 3,
    ScrubEvidence = 4,
    CleanupRetention = 5
}

/// <summary>
/// Fixed identifiers for the bounded system-principal catalog. There is no
/// conversion from caller-supplied text to a system principal.
/// </summary>
public static class SystemPrincipalId
{
    public const string Runtime = "SharpInspect.Runtime";
    public const string Outbox = "SharpInspect.Outbox";
    public const string EvidenceFinalizer = "SharpInspect.EvidenceFinalizer";
    public const string EvidenceScrubber = "SharpInspect.EvidenceScrubber";
    public const string RetentionCleanup = "SharpInspect.RetentionCleanup";
}

/// <summary>One fixed system identity and its single responsibility.</summary>
public sealed class SystemPrincipalDescriptor
{
    private readonly ReadOnlyCollection<SystemPermission> _permissions;

    internal SystemPrincipalDescriptor(string id, string responsibility,
        IEnumerable<SystemPermission> permissions)
    {
        Id = id;
        Responsibility = responsibility;
        _permissions = new ReadOnlyCollection<SystemPermission>(permissions.ToArray());
    }

    public string Id { get; }
    public string Responsibility { get; }
    public IReadOnlyList<SystemPermission> Permissions => _permissions;
}

/// <summary>
/// The only public system-principal mapping. Runtime services use their fixed
/// descriptor; this catalog has no arbitrary identifier lookup or execution API.
/// </summary>
public static class SystemPrincipalCatalog
{
    public static SystemPrincipalDescriptor Runtime { get; } = new(
        SystemPrincipalId.Runtime, "Runtime command recording",
        new[] { SystemPermission.RecordCommand });

    public static SystemPrincipalDescriptor Outbox { get; } = new(
        SystemPrincipalId.Outbox, "Outbox delivery",
        new[] { SystemPermission.DeliverOutbox });

    public static SystemPrincipalDescriptor EvidenceFinalizer { get; } = new(
        SystemPrincipalId.EvidenceFinalizer, "Evidence finalization",
        new[] { SystemPermission.FinalizeEvidence });

    public static SystemPrincipalDescriptor EvidenceScrubber { get; } = new(
        SystemPrincipalId.EvidenceScrubber, "Evidence scrubbing",
        new[] { SystemPermission.ScrubEvidence });

    public static SystemPrincipalDescriptor RetentionCleanup { get; } = new(
        SystemPrincipalId.RetentionCleanup, "Retention cleanup",
        new[] { SystemPermission.CleanupRetention });

    private static readonly ReadOnlyCollection<SystemPrincipalDescriptor> s_all =
        new(new[] { Runtime, Outbox, EvidenceFinalizer, EvidenceScrubber, RetentionCleanup });

    public static IReadOnlyList<SystemPrincipalDescriptor> All => s_all;
}
