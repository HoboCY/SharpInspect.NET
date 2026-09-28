using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit schema-41 station-maintenance handoff ledger. Existing stores require the bundled
/// 40-to-41 startup migration. The option binds the deployed maintenance policy, the validated
/// installation binding hash and the running application hash; presence of this option alone
/// authorizes nothing and performs no shutdown.
/// </summary>
public sealed class StationMaintenanceStoreOptions
{
    internal const int SchemaVersion = 41;
    internal const string ExecutionProfile = "SharpInspect.StationMaintenance.StartupMaintenance.v1";
    internal const int MaximumOperationFactsHardLimit = 3;
    /// <summary>One admitted operation still owes exactly this many future facts.</summary>
    internal const int AdmittedTerminalReserve = 1;
    internal const int MaximumPayloadBytesHardLimit = 64 * 1024;
    internal const int MaximumOperationsHardLimit = 100_000;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const string ActivationKind = "StationMaintenanceActivated";
    internal const string OperationAuditKind = "StationMaintenanceEvent";

    public StationMaintenanceStoreOptions(StationMaintenancePolicy policy, string installationBindingHash,
        string runtimeApplicationHash)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        InstallationBindingHash = Hash(installationBindingHash, nameof(installationBindingHash));
        RuntimeApplicationHash = Hash(runtimeApplicationHash, nameof(runtimeApplicationHash));
        Validate();
    }

    public StationMaintenancePolicy Policy { get; }
    /// <summary>The validated identity binding of the installed Runtime and its configuration root.</summary>
    public string InstallationBindingHash { get; }
    /// <summary>The running application hash this maintenance handoff binds.</summary>
    public string RuntimeApplicationHash { get; }
    /// <summary>Inclusive planning bound of durable operations kept in this ledger.</summary>
    public int MaximumOperations { get; init; } = 4096;
    /// <summary>Total serialized payload byte budget of this ledger.</summary>
    public long MaximumTotalBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Option-level binding of the policy, installation, application and planning bounds.</summary>
    public string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (Policy.ContentHash is not { Length: 64 })
            throw new ArgumentException("StationMaintenancePolicyBindingInvalid");
        if (MaximumOperations is < MaximumOperationFactsHardLimit or > MaximumOperationsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumOperations));
        if (MaximumTotalBytes < MaximumPayloadBytesHardLimit * 2 ||
            MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("StationMaintenanceStoreOptionsV1", Policy.ContentHash,
            InstallationBindingHash, RuntimeApplicationHash, N(MaximumOperations), N(MaximumTotalBytes));
    }

    /// <summary>
    /// Requires the complete previous generation this ledger extends: the schema-40 diagnostic
    /// support ledger (and its own retention/trace/logging chain), local identity and central
    /// audit integrity. The installation ownership configuration root is a separate slice.
    /// </summary>
    internal void ValidateProfile(ProductionStoreOptions store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Validate();
        if (store.AuditIntegrityPolicy is null)
            throw new ArgumentException("StationMaintenanceRequiresAudit");
        store.AuditIntegrityPolicy.Validate();
        if (store.LocalIdentity is null)
            throw new ArgumentException("StationMaintenanceRequiresIdentity");
        if (store.DiagnosticSupport is null)
            throw new ArgumentException("StationMaintenanceRequiresDiagnosticSupport");
        store.DiagnosticSupport.ValidateProfile(store);
    }

    private static string Hash(string value, string parameterName) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit)
            ? value.ToUpperInvariant()
            : throw new ArgumentException("StationMaintenanceBindingHashInvalid", parameterName);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
