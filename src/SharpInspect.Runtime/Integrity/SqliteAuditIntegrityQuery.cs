using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Integrity;

/// <summary>Read-only bounded verification against a machine key independent of database trust rows.</summary>
public sealed class SqliteAuditIntegrityQuery : IAuditIntegrityQuery
{
    private readonly ProductionStoreOptions _options;
    private readonly Func<AuditIntegrityPolicy, IAuditSigningKey> _openKey;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _waiting;

    public SqliteAuditIntegrityQuery(ProductionStoreOptions options) : this(options,
        policy => WindowsMachineAuditKey.Open(policy, false, out _)) { }

    internal SqliteAuditIntegrityQuery(ProductionStoreOptions options, Func<AuditIntegrityPolicy, IAuditSigningKey> openKey)
    { _options = options; _openKey = openKey; }

    public ValueTask<AuditIntegrityReport> VerifyAsync(AuditVerificationRequest request, CancellationToken cancellationToken = default) =>
        VerifyAsync(request, false, cancellationToken);

    internal async ValueTask<AuditIntegrityReport> VerifyAsync(AuditVerificationRequest request, bool startup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AfterSequence < 0 || request.MaximumEntries is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var policy = _options.AuditIntegrityPolicy;
        if (policy is null) return Report(null, AuditIntegrityState.NotConfigured, "AuditPolicyNotConfigured");
        policy.Validate();
        if (request.MaximumEntries > policy.MaximumVerificationEntries - policy.CheckpointEveryEntries)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (Interlocked.Increment(ref _waiting) > 32)
        { Interlocked.Decrement(ref _waiting); return Report(policy, AuditIntegrityState.Faulted, "AuditVerificationCapacityExceeded"); }
        var entered = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(_options.QueryTimeout);
        try
        {
            entered = await Slots.WaitAsync(_options.QueryTimeout, lifetime.Token).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("AuditVerificationDeadlineExceeded");
            var deadline = new StoreDeadline(_options.QueryTimeout);
            var result = await Task.Run(() =>
            {
                SqliteNative.EnsureDeadline(deadline, lifetime.Token);
                if (!StoragePathValidator.TryValidate(_options, out var path, out var reason)) throw new InvalidOperationException(reason);
                using var key = _openKey(policy);
                using var connection = SqliteNative.Open(path, readOnly: true);
                var db = connection.Handle!;
                SqliteNative.ConfigureSqliteLimit(db, _options);
                SqliteNative.Execute(db, "PRAGMA query_only=ON; BEGIN;", deadline, lifetime.Token);
                var schema = AuditChainDatabase.Scalar(db, "PRAGMA user_version;", deadline);
                SqliteNative.ConfigureSqliteLimit(db, _options, schema);
                StationQualificationReadGuard.RequireConfiguration(schema, _options);
                TraceStoragePolicyReadGuard.RequireConfiguration(schema, _options);
                if (_options.ProductionArming is null && schema == ProductionArmStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ProductionArmConfigurationRequired");
                if (_options.ProductionArming is not null && schema < ProductionArmStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ProductionArmGovernedMigrationRequired");
                if (schema == ProductionArmStoreOptions.SchemaVersion &&
                    (_options.ProductionAdmission is null || _options.LocalIdentity is null))
                    throw new InvalidOperationException("ProductionArmingRequiresAdmissionIdentityAndAudit");
                if (_options.CalibrationSessions is not null && schema < CalibrationSessionStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CalibrationGovernedMigrationRequired");
                if (_options.CalibrationSessions is null &&
                    (schema is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion))
                    throw new InvalidOperationException("CalibrationConfigurationRequired");
                if (_options.CalibrationGovernance is not null && schema < CalibrationGovernanceStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CalibrationGovernanceMigrationRequired");
                if (_options.CalibrationGovernance is null && schema == CalibrationGovernanceStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CalibrationGovernanceConfigurationRequired");
                if (_options.RecipeReleases is not null && schema < RecipeReleaseStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("RecipeReleaseGovernedMigrationRequired");
                if (_options.RecipeReleases is null &&
                    (schema is RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                        RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion))
                    throw new InvalidOperationException("RecipeReleaseConfigurationRequired");
                if (_options.PlcResultContracts is not null && schema < PlcResultContractStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("PlcResultContractGovernedMigrationRequired");
                if (_options.PlcResultContracts is null &&
                    (schema is PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                        PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion))
                    throw new InvalidOperationException("PlcResultContractConfigurationRequired");
                if ((schema is RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                    RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion) && _options.RecipeDrafts is null)
                    throw new InvalidOperationException("RecipeDraftConfigurationRequired");
                if (_options.LocalIdentity is not null && schema < 7)
                {
                    var migrationReason = schema switch
                    {
                        6 => "GovernedAlarmMigrationRequired",
                        5 => "IdentityRecoveryGovernedMigrationRequired",
                        4 => "IdentityAuthorizationGovernedMigrationRequired",
                        3 => "IdentityAuthenticationGovernedMigrationRequired",
                        _ => "AuditGovernedMigrationRequired"
                    };
                    throw new InvalidOperationException(migrationReason);
                }
                if (_options.AlgorithmResultArchive is null && schema == AlgorithmResultArchiveOptions.SchemaVersion)
                    throw new InvalidOperationException("AlgorithmResultArchiveConfigurationRequired");
                if (_options.AlgorithmResultArchive is not null && schema < AlgorithmResultArchiveOptions.SchemaVersion)
                    throw new InvalidOperationException("AlgorithmResultArchiveGovernedMigrationRequired");
                if (_options.RecipeDrafts is not null && schema < RecipeDraftStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("RecipeDraftGovernedMigrationRequired");
                if (_options.RecipeDrafts is null && schema == RecipeDraftStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("RecipeDraftConfigurationRequired");
                if (_options.CameraSetup is not null && schema < CameraSetupStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraSetupGovernedMigrationRequired");
                if (_options.CameraSetup is null && schema == CameraSetupStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraSetupConfigurationRequired");
                if (_options.CameraRecovery is not null && schema < CameraRecoveryStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraRecoveryGovernedMigrationRequired");
                if (_options.CameraRecovery is null && schema == CameraRecoveryStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
                if (_options.CameraNetwork is not null && schema < CameraNetworkStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraNetworkGovernedMigrationRequired");
                if (_options.CameraNetwork is null && schema == CameraNetworkStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraNetworkConfigurationRequired");
                if (_options.ImagingSetup is not null && schema < ImagingSetupStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ImagingSetupGovernedMigrationRequired");
                if (_options.ImagingSetup is null && schema == ImagingSetupStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ImagingSetupConfigurationRequired");
                if (_options.PreviewSessions is not null && schema < PreviewSessionStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("PreviewSessionGovernedMigrationRequired");
                if (_options.PreviewSessions is null &&
                    (schema == PreviewSessionStoreOptions.SchemaVersion || schema == CalibrationImportStoreOptions.SchemaVersion))
                    throw new InvalidOperationException("PreviewSessionConfigurationRequired");
                if (_options.CalibrationImports is not null && schema < CalibrationImportStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CalibrationImportGovernedMigrationRequired");
                if (_options.CalibrationImports is null && schema == CalibrationImportStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CalibrationImportConfigurationRequired");
                if (_options.TraceStoragePolicies is not null && schema < TraceStoragePolicyStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("TraceStoragePolicyGovernedMigrationRequired");
                if (_options.TraceStoragePolicies is null && schema == TraceStoragePolicyStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("TraceStoragePolicyConfigurationRequired");
                if (_options.QualificationCycles is not null && schema < QualificationCycleStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("QualificationCycleGovernedMigrationRequired");
                if (_options.QualificationCycles is null && schema == QualificationCycleStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("QualificationCycleConfigurationRequired");
                if (_options.PlcCommunication is not null && schema < PlcCommunicationStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("PlcCommunicationGovernedMigrationRequired");
                if (_options.PlcCommunication is null && schema == PlcCommunicationStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("PlcCommunicationConfigurationRequired");
                if (schema == PlcCommunicationStoreOptions.SchemaVersion && _options.LocalIdentity is null)
                    throw new InvalidOperationException("PlcCommunicationRequiresIdentityAndAudit");
                if (_options.PartIdentities is not null && schema < PartIdentityStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("PartIdentityGovernedMigrationRequired");
                if (_options.PartIdentities is null && schema == PartIdentityStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("PartIdentityConfigurationRequired");
                if (schema == PartIdentityStoreOptions.SchemaVersion && _options.LocalIdentity is null)
                    throw new InvalidOperationException("PartIdentityRequiresIdentityAndAudit");
                if (schema == QualificationCycleStoreOptions.SchemaVersion &&
                    (_options.StationQualifications is null || _options.TraceStoragePolicies is null))
                    throw new InvalidOperationException("QualificationCyclesRequiresStationQualificationPolicyIdentityAndAudit");
                if (schema == CalibrationImportStoreOptions.SchemaVersion &&
                    (_options.PreviewSessions is null || _options.CalibrationGovernance is null ||
                        _options.CalibrationSessions is null || _options.ImagingSetup is null))
                    throw new InvalidOperationException("CalibrationImportsRequiresPreviewGovernanceCalibrationAndImagingSetup");
                if (_options.ManualInspections is not null && schema < ManualInspectionStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ManualInspectionGovernedMigrationRequired");
                if (_options.ManualInspections is null && schema == ManualInspectionStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ManualInspectionConfigurationRequired");
                if ((schema == ManualInspectionStoreOptions.SchemaVersion ||
                     ((schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) && _options.ManualInspections is not null)) &&
                    _options.RecipeDrafts is null)
                    throw new InvalidOperationException("RecipeDraftConfigurationRequired");
                if ((schema == ManualInspectionStoreOptions.SchemaVersion ||
                     ((schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) && _options.ManualInspections is not null)) &&
                    _options.CameraSetup is null)
                    throw new InvalidOperationException("CameraSetupConfigurationRequired");
                if (_options.ProductionAdmission is not null && schema < ProductionAdmissionStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ProductionAdmissionGovernedMigrationRequired");
                if (_options.ProductionAdmission is null && schema == ProductionAdmissionStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ProductionAdmissionConfigurationRequired");
                if (schema == ProductionAdmissionStoreOptions.SchemaVersion && _options.LocalIdentity is null)
                    throw new InvalidOperationException("ProductionAdmissionRequiresIdentityAndAudit");
                AuditChainDatabase.Require(schema is 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32,
                    "AuditGovernedMigrationRequired");
                var archiveSchema = schema == AlgorithmResultArchiveOptions.SchemaVersion ||
                    schema >= RecipeDraftStoreOptions.SchemaVersion && _options.AlgorithmResultArchive is not null;
                var draftSchema = schema == RecipeDraftStoreOptions.SchemaVersion ||
                    (schema is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                        CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                        CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                        RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                        RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                         CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                         ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) &&
                         _options.RecipeDrafts is not null;
                var cameraSchema = (schema is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                    CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                    CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                    RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                    RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                     CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                     ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) && _options.CameraSetup is not null;
                var recoverySchema = schema == CameraRecoveryStoreOptions.SchemaVersion ||
                    (schema is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                        CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                        RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                        RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                         CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                         ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) &&
                     _options.CameraRecovery is not null;
                var networkSchema = (schema is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                    CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                    RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                    RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                         CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                         ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) &&
                     _options.CameraNetwork is not null;
                var imagingSchema = (schema is ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
                    CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or
                    PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                    PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                         ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) &&
                     _options.ImagingSetup is not null;
                var calibrationSchema = (schema is CalibrationSessionStoreOptions.SchemaVersion or
                    CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or
                    PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                    PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                         ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) &&
                     _options.CalibrationSessions is not null;
                var governanceSchema = (schema is CalibrationGovernanceStoreOptions.SchemaVersion or
                    RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
                    RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                     CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
                     ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) && _options.CalibrationGovernance is not null;
                var releaseSchema = schema is ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion
                    ? _options.RecipeReleases is not null
                    : schema is RecipeReleaseStoreOptions.SchemaVersion or
                         PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                         PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                         ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion;
                var contractSchema = schema is ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion
                    ? _options.PlcResultContracts is not null
                    : schema is PlcResultContractStoreOptions.SchemaVersion or
                         RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                         CalibrationImportStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion;
                var activationSchema = schema is ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion
                    ? _options.RecipeActivations is not null
                    : schema is RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion;
                var previewSchema = schema is ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion
                    ? _options.PreviewSessions is not null
                    : schema is PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
                         ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion;
                var importSchema = schema is ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion
                    ? _options.CalibrationImports is not null
                    : schema is CalibrationImportStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion;
                var manualSchema = schema == ManualInspectionStoreOptions.SchemaVersion ||
                    ((schema is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion) && _options.ManualInspections is not null);
                var productionAdmissionSchema = schema == ProductionAdmissionStoreOptions.SchemaVersion ||
                    (schema is StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion && _options.ProductionAdmission is not null);
                var traceSchema = _options.TraceStoragePolicies is not null && schema is
                    TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or
                    PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion;
                var qualificationCycleSchema = _options.QualificationCycles is not null && schema is
                    QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion;
                var plcSchema = _options.PlcCommunication is not null && schema is
                    PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion;
                var archiveVerification = archiveSchema;
                var draftVerification = draftSchema;
                var alarmStartup = schema >= 7 && startup && _options.AlarmPolicy is not null;
                var networkVerification = networkSchema;
                var fullVerification = archiveVerification || draftVerification || cameraSchema || recoverySchema || networkVerification ||
                    imagingSchema || calibrationSchema || governanceSchema || releaseSchema || contractSchema || activationSchema ||
                    previewSchema || importSchema || manualSchema || productionAdmissionSchema || traceSchema ||
                    qualificationCycleSchema || plcSchema || alarmStartup ||
                    _options.StationQualifications is not null || _options.ProductionInspections is not null ||
                    _options.PartIdentities is not null;
                var verificationRequest = fullVerification
                    ? new AuditVerificationRequest(0, policy.MaximumVerificationEntries)
                    : request;
                var report = AuditChainDatabase.Verify(db, policy, key.KeyId, key.PublicKeyBase64,
                    verificationRequest, fullVerification ? false : startup, deadline,
                    archiveOptions: archiveSchema ? _options.AlgorithmResultArchive : null,
                    recipeDraftOptions: draftSchema ? _options.RecipeDrafts : null,
                    cameraSetupOptions: cameraSchema ? _options.CameraSetup : null,
                    cameraRecoveryOptions: recoverySchema ? _options.CameraRecovery : null,
                    cameraNetworkOptions: networkSchema ? _options.CameraNetwork : null,
                    imagingSetupOptions: imagingSchema ? _options.ImagingSetup : null,
                    calibrationSessionOptions: calibrationSchema ? _options.CalibrationSessions : null,
                    governanceOptions: governanceSchema ? _options.CalibrationGovernance : null,
                    releaseOptions: releaseSchema ? _options.RecipeReleases : null,
                    contractOptions: contractSchema ? _options.PlcResultContracts : null,
                    activationOptions: activationSchema ? _options.RecipeActivations : null,
                     previewOptions: previewSchema ? _options.PreviewSessions : null,
                     importOptions: importSchema ? _options.CalibrationImports : null,
                      manualOptions: manualSchema ? _options.ManualInspections : null,
                      productionAdmissionOptions: productionAdmissionSchema ? _options.ProductionAdmission : null,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication,
                productionInspectionOptions: _options.ProductionInspections,
                productionRecoveryOptions: _options.ProductionRecovery,
                partIdentityOptions: _options.PartIdentities,
                productionArmOptions: _options.ProductionArming, recipeSelectionOptions: _options.RecipeSelections);
            RecipeTransferReadGuard.RequireVerified(db, report, deadline, _options);
            TraceStoragePolicyReadGuard.RequireVerified(db, report, deadline, _options);
            StationQualificationReadGuard.RequireVerified(db, report, deadline, _options);
                if (alarmStartup) AuditChainDatabase.RequireFullAlarmVerification(db, report, deadline);
                if (archiveSchema) AuditChainDatabase.RequireFullAlgorithmResultVerification(db, report, deadline);
                if (draftSchema) AuditChainDatabase.RequireFullRecipeDraftVerification(db, report, deadline,
                    _options.RecipeDrafts);
                if (cameraSchema) AuditChainDatabase.RequireFullCameraSetupVerification(db, report, deadline,
                    _options.CameraSetup);
                if (recoverySchema) AuditChainDatabase.RequireFullCameraRecoveryVerification(db, report, deadline,
                    _options.CameraRecovery);
                if (networkSchema) AuditChainDatabase.RequireFullCameraNetworkVerification(db, report, deadline,
                    _options.CameraNetwork);
                if (imagingSchema) AuditChainDatabase.RequireFullImagingSetupVerification(db, report, deadline,
                    _options.ImagingSetup);
                if (governanceSchema) AuditChainDatabase.RequireFullCalibrationGovernanceVerification(db, report, deadline,
                    _options.CalibrationGovernance);
                if (releaseSchema) AuditChainDatabase.RequireFullRecipeReleaseVerification(db, report, deadline,
                    _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
                if (contractSchema) AuditChainDatabase.RequireFullPlcResultContractVerification(db, report, deadline,
                    _options.PlcResultContracts);
                if (activationSchema) AuditChainDatabase.RequireFullRecipeActivationVerification(db, report, deadline,
                    _options.RecipeActivations, _options.RecipeReleases, _options.PlcResultContracts,
                    _options.CalibrationGovernance);
                if (previewSchema) AuditChainDatabase.RequireFullPreviewSessionVerification(db, report, deadline,
                    _options.PreviewSessions);
                if (importSchema) AuditChainDatabase.RequireFullCalibrationImportVerification(db, report, deadline,
                    _options.CalibrationImports);
                if (manualSchema) AuditChainDatabase.RequireFullManualInspectionVerification(db, report, deadline,
                    _options.ManualInspections);
                if (productionAdmissionSchema)
                    AuditChainDatabase.RequireFullProductionAdmissionVerification(db, report, deadline,
                        _options.ProductionAdmission);
                if (schema == ProductionArmStoreOptions.SchemaVersion)
                    AuditChainDatabase.RequireFullProductionArmVerification(db, report, deadline);
                if (qualificationCycleSchema)
                    AuditChainDatabase.RequireFullQualificationCycleVerification(db, report, deadline,
                        _options.QualificationCycles);
                if (plcSchema)
                    AuditChainDatabase.RequireFullPlcCommunicationVerification(db, report, deadline,
                        _options.PlcCommunication);
                if (_options.ProductionInspections is not null)
                    AuditChainDatabase.RequireFullProductionInspectionVerification(db, report, deadline,
                        _options.ProductionInspections);
                if (_options.PartIdentities is not null)
                    AuditChainDatabase.RequireFullPartIdentityVerification(db, report, deadline,
                        _options.PartIdentities);
                var checkpoint = AuditChainDatabase.LatestCheckpoint(db, deadline)!;
                SqliteNative.Execute(db, "COMMIT;", deadline, lifetime.Token);
                return (Report: report, Checkpoint: checkpoint);
            }, CancellationToken.None).ConfigureAwait(false);
            if (policy.RequireExternalAnchor)
            {
                var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
                var latest = await AuditAnchorClient.InvokeAsync(anchor, token => anchor.ReadLatestAsync(policy.StationId, token),
                    policy.AnchorTimeout, lifetime.Token).ConfigureAwait(false);
                AuditChainDatabase.Require(latest is not null && AuditChainDatabase.ReceiptMatches(policy, result.Checkpoint, latest),
                    "AuditExternalAnchorMismatch");
            }
            return result.Report;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = FaultReason(ex, "AuditVerificationUnavailable");
            return Report(policy, reason == "AuditAnchorBusy" ? AuditIntegrityState.Verifying : AuditIntegrityState.Faulted, reason);
        }
        finally { if (entered) Slots.Release(); Interlocked.Decrement(ref _waiting); }
    }

    internal static AuditIntegrityReport Report(AuditIntegrityPolicy? policy, AuditIntegrityState state, string reason) =>
        new(state, reason, policy?.StationId, policy?.Version, 0, 0, 0, null, null, DateTimeOffset.UtcNow);

    internal static string FaultReason(Exception exception, string unavailable)
    {
        if (exception is InvalidOperationException { Message: var armReason } &&
            armReason.StartsWith("ProductionArm", StringComparison.Ordinal))
            return armReason;
        if (exception is InvalidOperationException { Message: var selectionReason } &&
            (selectionReason.StartsWith("RecipeSelection", StringComparison.Ordinal) ||
             selectionReason.StartsWith("RecipeChange", StringComparison.Ordinal)))
            return selectionReason;
        if (exception is InvalidOperationException { Message: var cameraReason } &&
            cameraReason.StartsWith("CameraSetup", StringComparison.Ordinal))
            return cameraReason;
        if (exception is InvalidOperationException { Message: var recoveryReason } &&
            recoveryReason.StartsWith("CameraRecovery", StringComparison.Ordinal))
            return recoveryReason;
        if (exception is InvalidOperationException { Message: var networkReason } &&
            networkReason.StartsWith("CameraNetwork", StringComparison.Ordinal))
            return networkReason;
        if (exception is InvalidOperationException { Message: var imagingReason } &&
            imagingReason.StartsWith("ImagingSetup", StringComparison.Ordinal))
            return imagingReason;
        if (exception is InvalidOperationException { Message: var calibrationReason } &&
            calibrationReason.StartsWith("Calibration", StringComparison.Ordinal))
            return calibrationReason;
        if (exception is InvalidOperationException { Message: var releaseReason } &&
            releaseReason.StartsWith("RecipeRelease", StringComparison.Ordinal))
            return releaseReason;
        if (exception is InvalidOperationException { Message: var contractReason } &&
            contractReason.StartsWith("PlcResultContract", StringComparison.Ordinal))
            return contractReason;
        if (exception is InvalidOperationException { Message: var activationReason } &&
            activationReason.StartsWith("RecipeActivation", StringComparison.Ordinal))
            return activationReason;
        if (exception is InvalidOperationException { Message: var previewReason } &&
            previewReason.StartsWith("PreviewSession", StringComparison.Ordinal))
            return previewReason;
        if (exception is InvalidOperationException { Message: var importReason } &&
            importReason.StartsWith("CalibrationImport", StringComparison.Ordinal))
            return importReason;
        if (exception is InvalidOperationException { Message: var manualReason } &&
            manualReason.StartsWith("ManualInspection", StringComparison.Ordinal))
            return manualReason;
        if (exception is InvalidOperationException { Message: var productionAdmissionReason } &&
            productionAdmissionReason.StartsWith("ProductionAdmission", StringComparison.Ordinal))
            return productionAdmissionReason;
        if (exception is InvalidOperationException { Message: var productionInspectionReason } &&
            productionInspectionReason.StartsWith("ProductionInspection", StringComparison.Ordinal))
            return productionInspectionReason;
        if (exception is InvalidOperationException { Message: var qualificationReason } &&
            qualificationReason.StartsWith("StationQualification", StringComparison.Ordinal))
            return qualificationReason;
        if (exception is InvalidOperationException { Message: var plcReason } &&
            plcReason.StartsWith("PlcCommunication", StringComparison.Ordinal))
            return plcReason;
        if (exception is InvalidOperationException { Message: var transferReason } &&
            transferReason.StartsWith("RecipeTransfer", StringComparison.Ordinal))
            return transferReason;
        if (exception is InvalidOperationException { Message: var partIdentityReason } &&
            partIdentityReason.StartsWith("PartIdentity", StringComparison.Ordinal))
            return partIdentityReason;
        if (exception is InvalidOperationException { Message: "IdentityAuthenticationGovernedMigrationRequired" })
            return "IdentityAuthenticationGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "IdentityAuthorizationGovernedMigrationRequired" })
            return "IdentityAuthorizationGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "IdentityRecoveryGovernedMigrationRequired" })
            return "IdentityRecoveryGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "GovernedAlarmMigrationRequired" })
            return "GovernedAlarmMigrationRequired";
        if (exception is InvalidOperationException { Message: "AlgorithmResultArchiveConfigurationRequired" })
            return "AlgorithmResultArchiveConfigurationRequired";
        if (exception is InvalidOperationException { Message: "AlgorithmResultArchiveGovernedMigrationRequired" })
            return "AlgorithmResultArchiveGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "RecipeDraftConfigurationRequired" })
            return "RecipeDraftConfigurationRequired";
        if (exception is InvalidOperationException { Message: "RecipeDraftGovernedMigrationRequired" })
            return "RecipeDraftGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "RecipeDraftRequiresIdentityAndAudit" })
            return "RecipeDraftRequiresIdentityAndAudit";
        if (exception is InvalidOperationException { Message: "RecipeReleaseRequiresDraftIdentityAndAudit" })
            return "RecipeReleaseRequiresDraftIdentityAndAudit";
        if (exception is InvalidOperationException { Message: var draftReason } &&
            draftReason.StartsWith("RecipeDraft", StringComparison.Ordinal))
            return draftReason;
        if (exception is InvalidOperationException { Message: var archiveReason } &&
            archiveReason.StartsWith("AlgorithmResult", StringComparison.Ordinal))
            return archiveReason;
        if (exception is InvalidOperationException { Message: var message } &&
            (message.StartsWith("RecoveryOperation", StringComparison.Ordinal)))
            return message;
        if (exception is InvalidOperationException { Message: var reason } &&
            (reason.StartsWith("Audit", StringComparison.Ordinal) || reason.StartsWith("Alarm", StringComparison.Ordinal)))
            return reason;
        var sqliteCode = exception is SqliteNativeException native ? native.SqliteErrorCode & 255 :
            exception is Microsoft.Data.Sqlite.SqliteException managed ? managed.SqliteErrorCode : 0;
        return sqliteCode switch
        {
            SQLitePCL.raw.SQLITE_TOOBIG => "AuditEvidenceOversized",
            SQLitePCL.raw.SQLITE_CORRUPT or SQLitePCL.raw.SQLITE_NOTADB => "AuditEvidenceCorrupt",
            SQLitePCL.raw.SQLITE_ERROR or SQLitePCL.raw.SQLITE_SCHEMA => "AuditSchemaInvalid",
            _ => exception switch
            {
                TimeoutException or OperationCanceledException => "AuditVerificationDeadlineExceeded",
                JsonException or FormatException or ArgumentException or OverflowException => "AuditEvidenceMalformed",
                _ => unavailable
            }
        };
    }

}
