using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private ProductionDeploymentObservation? _productionDeploymentObservation;

    internal async ValueTask<RecipeActivationDeploymentEvidence?> CaptureRecipeActivationDeploymentEvidenceAsync(
        RecipeActivationSnapshot candidate, CancellationToken token)
    {
        IProductionInspectionQualificationEvidenceProvider? provider;
        StationStateSnapshot state;
        bool reconciled;
        bool writerAvailable;
        lock (_sync)
        {
            provider = _productionInspectionQualificationEvidenceProvider;
            state = _snapshot;
            reconciled = ProductionStartupDependenciesReconciledLocked() && _productionInspectionStartupVerified &&
                !_productionInspectionRecoveryBlocked && ProductionOutboxConfiguredLocked();
            writerAvailable = _audit is SqliteCommandStore { ProductionInspectionEnabled: true } &&
                _productionInspectionExecutionOptions is not null && _productionInspectionClock is not null &&
                _frameBufferPool is not null;
        }
        if (provider is null || _productionInspectionOptions is null || _productionInspectionStoreOptions is null) return null;
        var observed = await CaptureProductionDeploymentAsync(candidate, token).ConfigureAwait(false);
        if (observed is null) return null;
        var qualification = await provider.CaptureAsync(observed.Configuration, state, candidate, token).ConfigureAwait(false);
        var policy = await ReadProductionInspectionPolicyAsync(token).ConfigureAwait(false);
        return new(observed.Configuration, qualification, _productionInspectionOptions,
            _productionInspectionStoreOptions, policy, writerAvailable, reconciled, observed.PartIdentity,
            _audit is SqliteCommandStore { PartIdentityEnabled: true });
    }

    private bool ProductionStartupDependenciesReconciledLocked() => _storeReady && !_auditFault &&
        !_calibrationStartupBlocked && !_activationStartupPending && !_activationStartupBlocked &&
        !_activationRecoveryBlocked && !_previewStartupPending && !_previewRecoveryBlocked &&
        !_manualStartupPending && !_manualRecoveryBlocked && !_stationQualificationStartupPending &&
        !_stationQualificationRecoveryBlocked && ProductionImageBacklogReadyLocked(_snapshot) &&
        ProductionOutboxStartupReadyLocked() &&
        _snapshot.Evidence.PendingDeliveries == 0;

    private async ValueTask<ProductionDeploymentObservation?> CaptureProductionDeploymentAsync(
        RecipeActivationSnapshot? activation, CancellationToken token)
    {
        var options = _productionInspectionOptions;
        var storeOptions = _productionInspectionStoreOptions;
        if (options is null || storeOptions is null || _audit is not SqliteCommandStore store) return null;
        var policy = await new SqliteTraceStoragePolicyQuery(storeOptions).ReadAsync(null, token).ConfigureAwait(false);
        var identity = await store.ReadIdentityAsync(token).ConfigureAwait(false);
        var providerHash = activation?.CameraSetup.Binding is { } binding
            ? _cameraSetupRuntime.ProductionProviderBinaryHash(binding.Target.Provider) : null;
        var algorithmHash = activation is not null && _recipeActivations is RecipeActivationService activationService
            ? activationService.ProductionPreparedBinaryHash(activation.PreparedAlgorithmInstanceId) : null;
        var partIdentity = _partIdentityRegistry is null ? null :
            (await _partIdentityRegistry.CaptureAsync(activation?.Release.Source.Content.PartIdentityRequirement, token)
                .ConfigureAwait(false)).Observation;
        var configuration = ProductionConfigurationBuilder.Build(options, storeOptions, options.Deployment,
            activation, policy.Snapshot, identity.InstallationKeyId, providerHash, algorithmHash, partIdentity);
        var outboxBacklog = storeOptions.Outbox is { } outbox ? Outbox.ProductionOutboxBinding.CompleteBacklog(outbox,
            await new SqliteProductionOutboxQuery(storeOptions).ReadBacklogAsync(token).ConfigureAwait(false)) : null;
        var preflight = TraceStoragePreflightEvaluator.Evaluate(policy, storeOptions.TraceStoragePolicies?.DeploymentScope,
            TraceStoragePreflightEvaluator.Observe(storeOptions), store.VerifiedProfile, DateTimeOffset.UtcNow, outboxBacklog);
        var capacityGates = new[] { TraceStoragePreflightGate.Policy, TraceStoragePreflightGate.RouteInventory,
            TraceStoragePreflightGate.StoragePath, TraceStoragePreflightGate.SqliteProfile,
            TraceStoragePreflightGate.StorageReserve, TraceStoragePreflightGate.WalCapacity };
        var capacityFailure = capacityGates.Select(gate => preflight.Rows.Single(row => row.Gate == gate))
            .FirstOrDefault(row => row.Status != TraceStoragePreflightStatus.Passed)?.ReasonCode;
        var inspectionCapacity = await store.ReadProductionInspectionCapacityAsync(token).ConfigureAwait(false);
        if (!inspectionCapacity.Available || !inspectionCapacity.CanAdmit)
            capacityFailure ??= inspectionCapacity.ReasonCode;
        var identityReady = identity.KitState == RecoveryKitState.Available &&
            identity.EnumerateAccounts().Any(IdentityAuthorityState.IsUsableAdministrator) &&
            identity.RecoveryCodes.Any(code => !code.Consumed && !code.Revoked);
        var policyExact = options.Deployment is not null && policy.Snapshot is { } snapshot &&
            snapshot.ContentHash == options.TracePolicySnapshotHash && snapshot.Version == options.TracePolicyVersion &&
            options.StationId == identity.StationId && storeOptions.LocalIdentity?.StationId == options.StationId &&
            storeOptions.AlarmPolicy is not null;
        return new(configuration, activation?.ContentHash, policyExact,
            configuration.MissingBindings.Count == 0, identityReady, capacityFailure, DateTimeOffset.UtcNow, partIdentity);
    }

    private ProductionAdmissionGateResult ProductionDeploymentGate(ProductionAdmissionGate gate)
    {
        var observation = _productionDeploymentObservation;
        var fresh = observation is not null && observation.ActivationHash == _activeActivation?.Snapshot.ContentHash &&
            DateTimeOffset.UtcNow - observation.ObservedAtUtc < TimeSpan.FromSeconds(10);
        var passed = fresh && gate switch
        {
            ProductionAdmissionGate.DeploymentPolicies => observation!.PoliciesExact,
            ProductionAdmissionGate.VersionPolicy => observation!.VersionsComplete,
            ProductionAdmissionGate.IdentityRecovery => observation!.IdentityRecoveryAvailable,
            ProductionAdmissionGate.StoreCapacity => observation!.CapacityFailure is null,
            _ => false
        };
        return new(gate, passed ? ProductionAdmissionGateStatus.Passed : ProductionAdmissionGateStatus.NotConfigured,
            passed ? gate + "Verified" : gate == ProductionAdmissionGate.StoreCapacity && fresh
                ? observation!.CapacityFailure ?? "ProductionStoreCapacityUnavailable" : ProductionAdmissionEngine.MissingReason(gate),
            observedFingerprint: passed ? observation!.Configuration.Get(gate switch
            {
                ProductionAdmissionGate.DeploymentPolicies => ProductionConfigurationBinding.DeploymentPolicy,
                ProductionAdmissionGate.VersionPolicy => ProductionConfigurationBinding.FrameworkCandidate,
                ProductionAdmissionGate.IdentityRecovery => ProductionConfigurationBinding.IdentityPolicy,
                _ => ProductionConfigurationBinding.StoreProfile
            }) : null);
    }

    private sealed record ProductionDeploymentObservation(ProductionConfiguration Configuration,
        string? ActivationHash, bool PoliciesExact, bool VersionsComplete, bool IdentityRecoveryAvailable,
        string? CapacityFailure, DateTimeOffset ObservedAtUtc, PartIdentityBindingObservation? PartIdentity = null);
}
