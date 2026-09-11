using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// Immutable station policy for assigning fine-grained human permissions and
/// requiring fresh Step-Up Reauthentication. Role bundles are only convenient
/// assignment sets; authorization remains a Permission-level decision.
/// </summary>
public sealed class AuthorizationPolicy
{
    /// <summary>
    /// Fixed resource bound for the in-memory and protected local identity
    /// state. It is an implementation limit, not a security recommendation.
    /// </summary>
    public const int MaxHumanAccounts = 16;

    private static readonly HumanRoleBundle[] RoleOrder =
    {
        HumanRoleBundle.Operator,
        HumanRoleBundle.Technician,
        HumanRoleBundle.Administrator
    };

    private static readonly Permission[] AllPermissions = Enum.GetValues<Permission>()
        .Where(permission => permission != Permission.None)
        .OrderBy(permission => permission)
        .ToArray();

    private static readonly Permission[] MandatoryStepUpPermissions = AllPermissions
        .Where(permission => permission is not Permission.ArmProduction and not Permission.ActivateRecipe and
            not Permission.AcknowledgeAlarm and not Permission.EditRecipeDraft and not Permission.RunCalibration and
            not Permission.ManageCalibrationAcceptancePolicy and
            not Permission.RecordPhysicalCalibrationVerification and not Permission.RunPreview and
            not Permission.RunManualInspection and not Permission.ImportRecipe and not Permission.ExportRecipe and
            not Permission.AbandonRecipeDraft)
        .ToArray();

    private readonly ReadOnlyDictionary<HumanRoleBundle, IReadOnlyList<Permission>> _roleBundles;
    private readonly ReadOnlyCollection<Permission> _stepUpPermissions;

    /// <summary>
    /// Creates a policy from explicit role assignments. The input collections
    /// are copied immediately and never retained as mutable policy state.
    /// </summary>
    public AuthorizationPolicy(
        string id,
        string version,
        IReadOnlyDictionary<HumanRoleBundle, IEnumerable<Permission>> roleBundles,
        IEnumerable<Permission>? stepUpPermissions = null)
    {
        ValidateIdentifier(id, nameof(id));
        ValidateIdentifier(version, nameof(version));
        ArgumentNullException.ThrowIfNull(roleBundles);

        var copiedRoles = new Dictionary<HumanRoleBundle, IReadOnlyList<Permission>>();
        foreach (var role in roleBundles)
        {
            ValidateRole(role.Key, nameof(roleBundles));
            if (role.Value is null)
                throw new ArgumentException("AuthorizationRolePermissionsInvalid", nameof(roleBundles));

            var permissions = MaterializePermissions(role.Value, nameof(roleBundles));
            copiedRoles.Add(role.Key, new ReadOnlyCollection<Permission>(permissions));
        }

        if (copiedRoles.Count != RoleOrder.Length ||
            RoleOrder.Any(role => !copiedRoles.ContainsKey(role)))
            throw new ArgumentException("AuthorizationRoleBundlesIncomplete", nameof(roleBundles));

        var requestedStepUp = MaterializePermissions(
            stepUpPermissions ?? Array.Empty<Permission>(), nameof(stepUpPermissions));
        var explicitlyAssignedCalibrationStepUp = copiedRoles.Values
            .SelectMany(permissions => permissions)
            .Where(permission => permission is Permission.RunCalibration or
                Permission.ManageCalibrationAcceptancePolicy or
                Permission.RecordPhysicalCalibrationVerification)
            .Distinct()
            .ToArray();
        var allStepUp = MandatoryStepUpPermissions
            .Concat(explicitlyAssignedCalibrationStepUp)
            // Preserve the explicit policy override contract. A caller that
            // deliberately requests a new Step-Up permission is creating a
            // policy whose bytes must record that request.
            .Concat(requestedStepUp)
            .Distinct()
            .OrderBy(permission => permission)
            .ToArray();

        Id = id;
        Version = version;
        _roleBundles = new ReadOnlyDictionary<HumanRoleBundle, IReadOnlyList<Permission>>(copiedRoles);
        _stepUpPermissions = new ReadOnlyCollection<Permission>(allStepUp);
        ContentHash = ComputeContentHash();
    }

    /// <summary>Explicit policy identity used in audit and authorization context.</summary>
    public string Id { get; }

    /// <summary>Explicit policy version used in audit and authorization context.</summary>
    public string Version { get; }

    /// <summary>Defensively copied role bundles in stable role-key order.</summary>
    public IReadOnlyDictionary<HumanRoleBundle, IReadOnlyList<Permission>> RoleBundles => _roleBundles;

    /// <summary>
    /// The complete effective Step-Up set. Mandatory high-risk permissions are
    /// always present; callers may add ArmProduction or ActivateRecipe.
    /// </summary>
    public IReadOnlyList<Permission> StepUpPermissions => _stepUpPermissions;

    /// <summary>Stable uppercase SHA-256 hash of the complete policy behavior.</summary>
    public string ContentHash { get; }

    /// <summary>
    /// Explicit development station policy. Its values are development
    /// defaults and do not claim to be a production role design.
    /// </summary>
    public static AuthorizationPolicy Development { get; } = CreateDevelopment();

    /// <summary>Returns the copied permission set for one explicit role bundle.</summary>
    public IReadOnlyList<Permission> GetPermissions(HumanRoleBundle role)
    {
        ValidateRole(role, nameof(role));
        return _roleBundles[role];
    }

    /// <summary>
    /// Returns whether the requested permission requires fresh Step-Up. The
    /// mandatory set cannot be disabled by a project policy.
    /// </summary>
    public bool RequiresStepUp(Permission permission)
    {
        ValidatePermission(permission, nameof(permission));
        // Newly assigned calibration-governance permissions always require
        // Step-Up, even under an older explicit role policy. Do not rewrite
        // that policy's bytes merely because the account gains the permission.
        return (permission is Permission.RunCalibration or
            Permission.ManageCalibrationAcceptancePolicy or
            Permission.RecordPhysicalCalibrationVerification or Permission.AbandonRecipeDraft) ||
            _stepUpPermissions.Contains(permission);
    }

    /// <summary>Validates the immutable policy boundary and all enum values.</summary>
    public void Validate()
    {
        ValidateIdentifier(Id, nameof(Id));
        ValidateIdentifier(Version, nameof(Version));

        if (_roleBundles.Count != RoleOrder.Length ||
            RoleOrder.Any(role => !_roleBundles.ContainsKey(role)))
            throw new ArgumentException("AuthorizationRoleBundlesIncomplete", nameof(RoleBundles));

        foreach (var role in RoleOrder)
            ValidatePermissions(_roleBundles[role], nameof(RoleBundles));

        ValidatePermissions(_stepUpPermissions, nameof(StepUpPermissions));
        foreach (var permission in MandatoryStepUpPermissions)
        {
            if (!_stepUpPermissions.Contains(permission))
                throw new InvalidOperationException("MandatoryPermissionStepUpMissing");
        }

        var expectedHash = ComputeContentHash();
        if (!string.Equals(expectedHash, ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("AuthorizationPolicyHashMismatch");
    }

    private static AuthorizationPolicy CreateDevelopment()
    {
        var operatorPermissions = new[]
        {
            Permission.ArmProduction,
            Permission.ActivateRecipe,
            Permission.AcknowledgeAlarm
        };
        var technicianPermissions = operatorPermissions
            .Concat(new[]
            {
                Permission.ReleaseRecipe,
                Permission.RunDiagnostics,
                Permission.PublishCalibration,
                Permission.ManageCameraBindings,
                Permission.ResetAlarm
            })
            .ToArray();

        // Keep the original Development policy contract stable when new
        // permissions are added. Draft authoring is an explicit schema-9
        // policy choice and must not silently change an existing store's
        // role bundles or content hash.
        var developmentAdministratorPermissions = AllPermissions
            .Where(permission => permission is not Permission.EditRecipeDraft and not Permission.RunCalibration and
                not Permission.ManageCalibrationAcceptancePolicy and
                not Permission.RecordPhysicalCalibrationVerification and not Permission.RunPreview and
                not Permission.RunManualInspection and not Permission.ImportRecipe and not Permission.ExportRecipe and
                not Permission.AbandonRecipeDraft)
            .ToArray();

        var roleBundles = new Dictionary<HumanRoleBundle, IEnumerable<Permission>>
        {
            [HumanRoleBundle.Operator] = operatorPermissions,
            [HumanRoleBundle.Technician] = technicianPermissions,
            [HumanRoleBundle.Administrator] = developmentAdministratorPermissions
        };
        return new AuthorizationPolicy("development", "development-2026-09", roleBundles);
    }

    private string ComputeContentHash()
    {
        var fields = new List<string?>
        {
            Id,
            Version,
            MaxHumanAccounts.ToString(CultureInfo.InvariantCulture),
            "role-bundles"
        };

        foreach (var role in RoleOrder)
        {
            var permissions = _roleBundles[role];
            fields.Add(((int)role).ToString(CultureInfo.InvariantCulture));
            fields.Add(permissions.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var permission in permissions)
                fields.Add(((int)permission).ToString(CultureInfo.InvariantCulture));
        }

        fields.Add("step-up");
        fields.Add(_stepUpPermissions.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var permission in _stepUpPermissions)
            fields.Add(((int)permission).ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "authorization-policy", fields.ToArray())));
    }

    private static Permission[] MaterializePermissions(
        IEnumerable<Permission> permissions, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var values = permissions.ToArray();
        ValidatePermissions(values, parameterName);
        if (values.Distinct().Count() != values.Length)
            throw new ArgumentException("AuthorizationPermissionsDuplicate", parameterName);
        return values.OrderBy(permission => permission).ToArray();
    }

    private static void ValidatePermissions(
        IEnumerable<Permission> permissions, string parameterName)
    {
        foreach (var permission in permissions)
            ValidatePermission(permission, parameterName);
    }

    private static void ValidatePermission(Permission permission, string parameterName)
    {
        if (!AllPermissions.Contains(permission))
            throw new ArgumentException("AuthorizationPermissionInvalid", parameterName);
    }

    private static void ValidateRole(HumanRoleBundle role, string parameterName)
    {
        if (!RoleOrder.Contains(role))
            throw new ArgumentException("AuthorizationRoleBundleInvalid", parameterName);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        AuditIntegrityPolicy.ValidateIdentifier(value, parameterName, required: true);
    }
}
