using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>Explicit installed root for restricted support artifacts; construction performs no IO.</summary>
public sealed class SupportBundleOptions
{
    public SupportBundleOptions(DiagnosticSupportPolicy policy, DiagnosticLocalStoreOptions files, string installationBinding)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Files = files ?? throw new ArgumentNullException(nameof(files));
        if (installationBinding is not { Length: 64 } || !installationBinding.All(Uri.IsHexDigit))
            throw new ArgumentException("SupportBundleInstallationBindingInvalid");
        if (!files.IsProtected || files.MaximumRecordBytes != policy.MaximumBundleBytes ||
            files.MaximumFileBytes != policy.MaximumBundleBytes || files.Retention != policy.Retention ||
            files.RollAfter != policy.ExportTimeout)
            throw new ArgumentException("SupportBundleFilePolicyMismatch");
        InstallationBinding = installationBinding.ToUpperInvariant();
        BindingHash = ProductionAdmissionCanonical.Hash("support-bundle-installation-v1",
            policy.ContentHash, files.BindingHash, InstallationBinding);
    }
    public DiagnosticSupportPolicy Policy { get; }
    public DiagnosticLocalStoreOptions Files { get; }
    public string InstallationBinding { get; }
    public string BindingHash { get; }
    internal bool Matches(TraceStoragePolicySnapshot? trace) => trace is not null &&
        trace.ContentHash == Policy.TracePolicySnapshotHash &&
        new[] { TraceRetentionClass.SupportBundle, TraceRetentionClass.CompletedExport }.All(kind =>
            trace.RetentionRules.SingleOrDefault(rule => rule.EvidenceClass == kind) is { } rule &&
            rule.StartsAt is RetentionStartEvent.ArtifactCreated or RetentionStartEvent.ExportCompleted &&
            Policy.Retention >= rule.MinimumRetention);
}
