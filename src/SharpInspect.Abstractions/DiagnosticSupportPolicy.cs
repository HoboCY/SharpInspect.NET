using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Explicit support-export limits. These are configuration bounds, not production qualification.</summary>
public sealed class DiagnosticSupportPolicy
{
    public DiagnosticSupportPolicy(string id, string version, string approvalReference,
        string loggingPolicyHash, string tracePolicySnapshotHash, TimeSpan maximumScope,
        int maximumSourceRecords, int maximumSourceBytes, int maximumBundleBytes,
        TimeSpan exportTimeout, TimeSpan authorizationCheckInterval, TimeSpan retention,
        int maximumOperationFacts, int maximumAuditPayloadBytes, long maximumAuditBytes)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        ApprovalReference = AlgorithmContractValidation.Identifier(approvalReference, nameof(approvalReference));
        LoggingPolicyHash = TraceStoragePolicyValidation.Hash(loggingPolicyHash, nameof(loggingPolicyHash));
        TracePolicySnapshotHash = TraceStoragePolicyValidation.Hash(tracePolicySnapshotHash, nameof(tracePolicySnapshotHash));
        if (maximumScope <= TimeSpan.Zero || maximumScope > TimeSpan.FromDays(31) ||
            maximumSourceRecords is < 1 or > 1000 || maximumSourceBytes is < 256 or > 4 * 1024 * 1024 ||
            maximumBundleBytes is < 4096 or > 16 * 1024 * 1024 ||
            exportTimeout < TimeSpan.FromSeconds(1) || exportTimeout > TimeSpan.FromMinutes(5) ||
            authorizationCheckInterval < TimeSpan.FromMilliseconds(10) || authorizationCheckInterval > TimeSpan.FromSeconds(1) ||
            retention < TimeSpan.FromSeconds(1) || retention > TimeSpan.FromDays(3650) ||
            maximumOperationFacts is < 16 or > 100_000 || maximumAuditPayloadBytes is < 4096 or > 64 * 1024 ||
            maximumAuditBytes < maximumAuditPayloadBytes || maximumAuditBytes > 512L * 1024 * 1024)
            throw new ArgumentException("DiagnosticSupportPolicyBoundsInvalid");
        MaximumScope = maximumScope; MaximumSourceRecords = maximumSourceRecords;
        MaximumSourceBytes = maximumSourceBytes; MaximumBundleBytes = maximumBundleBytes;
        ExportTimeout = exportTimeout; AuthorizationCheckInterval = authorizationCheckInterval; Retention = retention;
        MaximumOperationFacts = maximumOperationFacts; MaximumAuditPayloadBytes = maximumAuditPayloadBytes;
        MaximumAuditBytes = maximumAuditBytes;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "diagnostic-support-policy-v1", Id, Version,
            ApprovalReference, LoggingPolicyHash, TracePolicySnapshotHash,
            Number(maximumScope.Ticks), Number(maximumSourceRecords), Number(maximumSourceBytes), Number(maximumBundleBytes),
            Number(exportTimeout.Ticks), Number(authorizationCheckInterval.Ticks), Number(retention.Ticks),
            Number(maximumOperationFacts), Number(maximumAuditPayloadBytes), Number(maximumAuditBytes) });
    }
    public string Id { get; }
    public string Version { get; }
    public string ApprovalReference { get; }
    public string LoggingPolicyHash { get; }
    public string TracePolicySnapshotHash { get; }
    public TimeSpan MaximumScope { get; }
    public int MaximumSourceRecords { get; }
    public int MaximumSourceBytes { get; }
    public int MaximumBundleBytes { get; }
    public TimeSpan ExportTimeout { get; }
    public TimeSpan AuthorizationCheckInterval { get; }
    public TimeSpan Retention { get; }
    public int MaximumOperationFacts { get; }
    public int MaximumAuditPayloadBytes { get; }
    public long MaximumAuditBytes { get; }
    public string ContentHash { get; }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
