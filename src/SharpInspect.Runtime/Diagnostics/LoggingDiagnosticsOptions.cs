using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>Explicit installation bindings and policy activation. Construction performs no IO.</summary>
public sealed class LoggingDiagnosticsOptions
{
    public LoggingDiagnosticsOptions(LoggingDiagnosticsPolicy policy, DiagnosticLocalStoreOptions safe,
        string safeInstallationBinding, DiagnosticLocalStoreOptions protectedStore, string protectedInstallationBinding)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Safe = safe ?? throw new ArgumentNullException(nameof(safe));
        Protected = protectedStore ?? throw new ArgumentNullException(nameof(protectedStore));
        SafeInstallationBinding = Hash(safeInstallationBinding); ProtectedInstallationBinding = Hash(protectedInstallationBinding);
        if (safe.IsProtected || !protectedStore.IsProtected || Overlap(safe.Directory, protectedStore.Directory))
            throw new ArgumentException("DiagnosticChannelsMustBeSeparate");
        static bool Exact(DiagnosticLocalStoreOptions store, DiagnosticFileBudget budget) =>
            store.MaximumRecordBytes == budget.MaximumRecordBytes && store.MaximumFileBytes == budget.MaximumFileBytes &&
            store.MaximumFiles == budget.MaximumFiles && store.MaximumTotalBytes == budget.MaximumTotalBytes &&
            store.RollAfter == budget.RollAfter && store.Retention == budget.Retention;
        if (!Exact(safe, policy.SafeFiles) || !Exact(protectedStore, policy.ProtectedFiles))
            throw new ArgumentException("DiagnosticStorePolicyMismatch");
        if (DiagnosticStandardContracts.All.Any(required => !policy.Contracts.Any(value => value.ContentHash == required.ContentHash)) ||
            policy.Producers.MaximumProperties < 4 || policy.Producers.MaximumSymbolBytes < 64 ||
            policy.Baseline > DiagnosticLevel.Warning)
            throw new ArgumentException("DiagnosticStandardContractsRequired");
        if (policy.Contracts.SelectMany(value => value.Fields).Any(field =>
                DiagnosticClassifier.ForbiddenName(field.Name) && field.Classification != DiagnosticDataClass.Prohibited ||
                DiagnosticClassifier.ProtectedName(field.Name) && field.Classification == DiagnosticDataClass.Safe))
            throw new ArgumentException("DiagnosticProhibitedFieldCannotBeApproved");
        BindingHash = ProductionAdmissionCanonical.Hash("logging-diagnostics-installation-v1", policy.ContentHash,
            safe.BindingHash, SafeInstallationBinding, protectedStore.BindingHash, ProtectedInstallationBinding);
    }
    public LoggingDiagnosticsPolicy Policy { get; }
    public DiagnosticLocalStoreOptions Safe { get; }
    public string SafeInstallationBinding { get; }
    public DiagnosticLocalStoreOptions Protected { get; }
    public string ProtectedInstallationBinding { get; }
    public string BindingHash { get; }

    internal bool Matches(TraceStoragePolicySnapshot? trace) => trace is not null &&
        Policy.TracePolicyVersion == trace.Policy.Version && Policy.TracePolicySnapshotHash == trace.ContentHash &&
        RetentionMatches(trace, TraceRetentionClass.OperationalLog, Policy.SafeFiles.Retention) &&
        RetentionMatches(trace, TraceRetentionClass.ProtectedDiagnosticRecord, Policy.ProtectedFiles.Retention);

    private static bool RetentionMatches(TraceStoragePolicySnapshot trace, TraceRetentionClass kind, TimeSpan retention) =>
        trace.RetentionRules.SingleOrDefault(rule => rule.EvidenceClass == kind) is { } rule &&
        rule.StartsAt == RetentionStartEvent.ArtifactCreated && retention >= rule.MinimumRetention;

    internal bool VerifyInstallation(string databasePath, params string?[] evidenceRoots)
    {
        try
        {
            var authority = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
            if (Overlap(authority, Safe.Directory) || Overlap(authority, Protected.Directory)) return false;
            if (evidenceRoots.Any(root => !string.IsNullOrWhiteSpace(root) &&
                    (Overlap(root, Safe.Directory) || Overlap(root, Protected.Directory)))) return false;
            if (!string.Equals(Path.GetPathRoot(databasePath), Path.GetPathRoot(Safe.Directory), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetPathRoot(databasePath), Path.GetPathRoot(Protected.Directory), StringComparison.OrdinalIgnoreCase)) return false;
            return DiagnosticDirectoryInstallation.Verify(Safe, SafeInstallationBinding) &&
                DiagnosticDirectoryInstallation.Verify(Protected, ProtectedInstallationBinding);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private static string Hash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit)
        ? value.ToUpperInvariant() : throw new ArgumentException("DiagnosticInstallationBindingInvalid");
    private static bool Overlap(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)); b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
