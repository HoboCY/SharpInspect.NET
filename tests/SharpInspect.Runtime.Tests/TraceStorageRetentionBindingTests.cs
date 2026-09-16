using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact, Trait("VerificationId", "V155_P01")]
    public void V155_P01_RetentionConfigurationChangesInvalidateDeploymentEvidenceAndLegacyProfileStaysStable()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var trace = V150BindingTrace();
        var options = V150BindingOptions(null, V150BindingProfile(), trace);
        var first = EvidenceRetentionStorageTests.Options(fixture);
        var changed = EvidenceRetentionStorageTests.Options(fixture, version: "2");
        var without = new ProductionStoreOptions(first.DatabasePath)
        {
            TraceStoragePolicies = first.TraceStoragePolicies,
            Outbox = first.Outbox,
            EvidenceReconciliation = first.EvidenceReconciliation
        };
        var legacy = ProductionImageEvidenceBinding.PolicyFingerprint(options, without, trace);
        var enabled = ProductionImageEvidenceBinding.PolicyFingerprint(options, first, trace);
        Assert.NotEqual(legacy, enabled);
        Assert.NotEqual(enabled, ProductionImageEvidenceBinding.PolicyFingerprint(options, changed, trace));
        var retention = first.StorageRetention!;
        var recoveryChanged = new ProductionStoreOptions(first.DatabasePath)
        {
            TraceStoragePolicies = first.TraceStoragePolicies,
            Outbox = first.Outbox,
            EvidenceReconciliation = first.EvidenceReconciliation,
            StorageRetention = new(retention.ExecutionPolicy, retention.ObservationInterval, retention.FileTimeout,
                retention.StartupTimeout, new(retention.RecoveryBudget.ControlWalAdmissionBytes,
                    retention.RecoveryBudget.MaximumControlFacts + 1, retention.RecoveryBudget.CheckpointPlanningReserveBytes))
        };
        Assert.NotEqual(enabled, ProductionImageEvidenceBinding.PolicyFingerprint(options, recoveryChanged, trace));
        Assert.Equal(enabled, ProductionImageEvidenceBinding.PolicyFingerprint(options,
            EvidenceRetentionStorageTests.Options(fixture), trace));
        Assert.Equal(legacy, ProductionImageEvidenceBinding.PolicyFingerprint(options, without, trace));
    }
}
