using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

public enum TraceStoragePreflightGate : byte
{
    Policy = 1, RouteInventory = 2, StoragePath = 3, SqliteProfile = 4,
    StorageReserve = 5, WalCapacity = 6, Checkpoint = 7, ImageBacklog = 8,
    RequiredRouteBacklog = 9, EvidenceReconciliation = 10, Scrubber = 11,
    OtherDeploymentPolicies = 12, ProductionCycle = 13
}

public enum TraceStoragePreflightStatus : byte
{
    Passed = 1, Missing = 2, NotConfigured = 3, Mismatch = 4, Failed = 5, NotImplemented = 6
}

/// <summary>A bounded observation, not an authorization or deletion permit.</summary>
public sealed record TraceStoragePreflightRow(TraceStoragePreflightGate Gate,
    TraceStoragePreflightStatus Status, string ReasonCode, string? Expected = null, string? Observed = null);

/// <summary>Runtime-owned observations of the current policy and actual local store.</summary>
public sealed class TraceStoragePreflightReport
{
    internal TraceStoragePreflightReport(DateTimeOffset observedAtUtc, long? policyVersion,
        string? policySnapshotHash, IEnumerable<TraceStoragePreflightRow> rows)
    {
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("TraceStoragePreflightTimeInvalid", nameof(observedAtUtc));
        var copied = AlgorithmContractValidation.Copy(rows, nameof(rows), 13).OrderBy(row => row.Gate).ToArray();
        if (copied.Length != 13 || copied.Select(row => row.Gate).Distinct().Count() != 13 ||
            copied.Any(row => !Enum.IsDefined(row.Gate) || !Enum.IsDefined(row.Status)))
            throw new ArgumentException("TraceStoragePreflightGateSetInvalid", nameof(rows));
        foreach (var row in copied)
        {
            AlgorithmConfigurationValidation.Identifier(row.ReasonCode, nameof(rows));
            if (row.Expected?.Length > 256 || row.Observed?.Length > 256)
                throw new ArgumentException("TraceStoragePreflightObservationTooLong", nameof(rows));
        }
        if (policyVersion is < 1 || (policyVersion is null) != (policySnapshotHash is null))
            throw new ArgumentException("TraceStoragePreflightPolicyReferenceInvalid");
        if (policyVersion is null && copied.Single(row => row.Gate == TraceStoragePreflightGate.Policy).Status ==
            TraceStoragePreflightStatus.Passed)
            throw new ArgumentException("TraceStoragePreflightPassedPolicyReferenceMissing");
        ObservedAtUtc = observedAtUtc;
        PolicyVersion = policyVersion;
        PolicySnapshotHash = policySnapshotHash is null ? null :
            AlgorithmConfigurationValidation.Hash(policySnapshotHash, nameof(policySnapshotHash)).ToUpperInvariant();
        Rows = Array.AsReadOnly(copied);
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-trace-storage-preflight-v1", ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            PolicyVersion?.ToString(CultureInfo.InvariantCulture), PolicySnapshotHash
        }.Concat(copied.SelectMany(row => new[] { row.Gate.ToString(), row.Status.ToString(),
            row.ReasonCode, row.Expected, row.Observed })));
    }

    public DateTimeOffset ObservedAtUtc { get; }
    public long? PolicyVersion { get; }
    public string? PolicySnapshotHash { get; }
    public ReadOnlyCollection<TraceStoragePreflightRow> Rows { get; }
    public string ContentHash { get; }
    public bool PolicyValid => PolicyVersion is not null && PolicySnapshotHash is not null &&
        Rows.Single(row => row.Gate == TraceStoragePreflightGate.Policy).Status ==
        TraceStoragePreflightStatus.Passed;
    public bool CanAdmitProduction => PolicyValid && Rows.All(row => row.Status == TraceStoragePreflightStatus.Passed);
}
