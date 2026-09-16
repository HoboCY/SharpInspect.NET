using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit schema-40 diagnostic-operation ledger. Existing stores require the bundled
/// 39-to-40 startup migration. The option binds the deployed support policy, the validated
/// support-root identity, the installed logging diagnostics binding and the previous
/// schema-39 retention configuration; presence of this option alone enables no capture.
/// </summary>
public sealed class DiagnosticSupportStoreOptions
{
    internal const int SchemaVersion = 40;
    internal const string ExecutionProfile = "SharpInspect.DiagnosticSupport.StartupMaintenance.v1";
    internal const int MaximumOperationFactsHardLimit = 4;
    internal const int AdmittedTerminalReserve = 3;
    internal const int MaximumPayloadBytesHardLimit = 64 * 1024;
    internal const int MaximumOperationsHardLimit = 100_000;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const string ActivationKind = "DiagnosticSupportActivated";
    internal const string OperationAuditKind = "DiagnosticOperationEvent";

    public DiagnosticSupportStoreOptions(DiagnosticSupportPolicy policy, string supportRootBindingHash)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        SupportRootBindingHash = Hash(supportRootBindingHash, nameof(supportRootBindingHash));
        Validate();
    }

    public DiagnosticSupportPolicy Policy { get; }
    /// <summary>The validated identity binding of the installed Runtime support root.</summary>
    public string SupportRootBindingHash { get; }
    /// <summary>Inclusive planning bound of durable operations kept in this ledger.</summary>
    public int MaximumOperations { get; init; } = 4096;
    /// <summary>Total serialized payload byte budget of this ledger.</summary>
    public long MaximumTotalBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Option-level binding of the policy, support root and planning bounds.</summary>
    public string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (Policy.LoggingPolicyHash is not { Length: 64 } ||
            Policy.TracePolicySnapshotHash is not { Length: 64 })
            throw new ArgumentException("DiagnosticSupportPolicyBindingInvalid");
        if (MaximumOperations is < MaximumOperationFactsHardLimit or > MaximumOperationsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumOperations));
        if (MaximumTotalBytes < MaximumPayloadBytesHardLimit || MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("DiagnosticSupportStoreOptionsV1", Policy.ContentHash,
            SupportRootBindingHash, N(MaximumOperations), N(MaximumTotalBytes));
    }

    /// <summary>
    /// Requires the complete previous generation the diagnostic ledger extends: the governed
    /// retention configuration, the trace-storage policy store, local identity, central audit
    /// integrity and the installed logging diagnostics binding the support policy names.
    /// </summary>
    internal void ValidateProfile(ProductionStoreOptions store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Validate();
        if (store.AuditIntegrityPolicy is null)
            throw new ArgumentException("DiagnosticSupportRequiresAudit");
        if (store.LocalIdentity is null)
            throw new ArgumentException("DiagnosticSupportRequiresIdentity");
        if (store.TraceStoragePolicies is null)
            throw new ArgumentException("DiagnosticSupportRequiresTraceStoragePolicies");
        if (store.StorageRetention is null)
            throw new ArgumentException("DiagnosticSupportRequiresStorageRetention");
        store.StorageRetention!.ValidateProfile(store);
        if (store.LoggingDiagnostics is null)
            throw new ArgumentException("DiagnosticSupportRequiresLoggingDiagnostics");
        if (store.LoggingDiagnostics!.BindingHash is not { Length: 64 })
            throw new ArgumentException("DiagnosticSupportLoggingBindingInvalid");
        if (!string.Equals(store.LoggingDiagnostics.Policy.ContentHash, Policy.LoggingPolicyHash,
                StringComparison.Ordinal))
            throw new ArgumentException("DiagnosticSupportLoggingPolicyMismatch");
        if (store.LoggingDiagnostics.Policy.TracePolicySnapshotHash is not { Length: 64 })
            throw new ArgumentException("DiagnosticSupportLoggingPolicyInvalid");
        if (store.LoggingDiagnostics.SupportBundles is not { } bundle ||
            bundle.BindingHash != SupportRootBindingHash || bundle.Policy.ContentHash != Policy.ContentHash ||
            Policy.TracePolicySnapshotHash != store.LoggingDiagnostics.Policy.TracePolicySnapshotHash)
            throw new ArgumentException("DiagnosticSupportInstalledBundleBindingMismatch");
        if (Policy.MaximumSourceRecords > store.LoggingDiagnostics.Policy.MaximumQueryRecords ||
            Policy.MaximumSourceBytes > store.LoggingDiagnostics.Policy.MaximumQueryBytes)
            throw new ArgumentException("DiagnosticSupportSourceBudgetExceedsLoggingBudget");
    }

    private static string Hash(string value, string parameterName) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit)
            ? value.ToUpperInvariant()
            : throw new ArgumentException("DiagnosticSupportBindingHashInvalid", parameterName);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
