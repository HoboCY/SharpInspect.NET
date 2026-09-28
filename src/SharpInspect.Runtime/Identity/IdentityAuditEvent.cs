using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal enum IdentityEventKind
{
    IdentityConfigured,
    BootstrapIssued,
    BootstrapRejected,
    BootstrapExpired,
    AdministratorCreated,
    RecoveryKitIssued,
    AuthenticationSucceeded,
    AuthenticationRejected,
    PasswordVerifierUpgraded,
    AuthenticationThrottled,
    CredentialDisabled,
    SessionStarted,
    SessionLocked,
    SessionLoggedOut,
    SessionSignInCancelled,
    HumanAccountCreated,
    HumanPermissionsChanged,
    CredentialUnlocked,
    CredentialRebound,
    StepUpIssued,
    StepUpRejected,
    StepUpCancelled,
    StepUpConsumed,
    ManagementRejected,
    AdministratorRecovered,
    RecoveryRejected,
    RecoveryKitRotated,
    RecoveryKitRotationRejected,
    RecoveryKitCustodyConfirmed,
    RecoveryKitCustodyRejected,
    AlarmActionAuthorized,
    RecipeDraftSaved,
    CameraSetupActionAuthorized,
    CameraSetupOperationCompleted,
    CameraRecoveryCycleStartAuthorized,
    CameraRecoveryCycleStartCompleted,
    CameraRecoveryCycleStartFailed,
    CalibrationSessionStartAuthorized,
    CalibrationSessionActionAuthorized,
    CalibrationGovernanceActionAuthorized,
    RecipeReleased,
    PlcResultContractChanged,
    RecipeActivationAdmitted,
    RecipeActivationCompleted,
    RecipeActivationFailed,
    RecipeActivationCancelled,
    PreviewSessionStartAuthorized,
    PreviewSessionActionAuthorized,
    PreviewSessionCompleted,
    PreviewSessionFailed,
    ManualInspectionSessionStartAuthorized,
    ManualInspectionSessionActionAuthorized,
    ManualInspectionSessionCompleted,
    ManualInspectionSessionFailed,
    ProductionAdmissionArmAuthorized,
    ProductionAdmissionCompleted,
    ProductionAdmissionFailed,
    StationQualificationAuthorized,
    RecipeTransferAuthorized,
    TraceStoragePolicyAuthorized,
    ProductionInspectionAdmitted,
    ProductionInspectionCoreCommitted,
    ProductionInspectionFailed,
    PartIdentityRejectedTriggerRecorded,
    PartIdentityCorrectionAuthorized,
    ProductionRecoveryAuthorized,
    ProductionRecoveryCompleted,
    ProductionRecoveryFailed,
    RecipeSelectionChanged,
    RecipeDraftAbandoned,
    RecipeRetired
}

/// <summary>Closed, non-secret identity evidence. Credential material never belongs in this type.</summary>
internal sealed record IdentityAuditEvent(Guid EventId, IdentityEventKind Kind, DateTimeOffset OccurredAtUtc,
    string StationId, Guid? PrincipalId, Guid? CredentialId, Guid? TokenId, Guid? RecoveryKitId,
    string ReasonCode, string? WindowsSid = null, string? PasswordPolicyVersion = null,
    string? BlocklistId = null, string? BlocklistVersion = null, string? HashBaselineVersion = null, long StateRevision = 0,
    int HashTargetCost = 0, int RecordCost = 0, string? AuthenticationPolicyId = null,
    string? AuthenticationPolicyVersion = null, string? AuthenticationPolicyHash = null,
    string? ProtectedAttemptIdentifier = null, int AccountFailures = 0, int StationFailures = 0,
    DateTimeOffset? NextAllowedAtUtc = null, Guid? SessionId = null, long DelayTicks = 0,
    string? AuthorizationPolicyId = null, string? AuthorizationPolicyVersion = null,
    string? AuthorizationPolicyHash = null, Guid? ActorPrincipalId = null,
    Guid? CommandCorrelationId = null, Guid? StepUpGrantId = null,
    string? RequiredPermission = null, Guid? TargetPrincipalId = null,
    long AuthorizationRevision = 0, string? ManagementReason = null, string? ActionTargetId = null,
    Guid? BoundCommandCorrelationId = null, string? ActionCommandKind = null,
    string? PreviousPermissions = null, string? ResultingPermissions = null,
    Guid? OperationId = null, Guid? RecoveryCodeId = null, Guid? PreviousRecoveryKitId = null,
    string? RecoverySafetyEvidence = null,
    Guid? CameraRecoveryExpectedCycleId = null, string? CameraRecoveryLogicalRole = null,
    string? CameraRecoveryReasonCode = null, string? PlcRecipeActivationEvidence = null)
{
    internal byte[] Encode(long ordinal, int schemaVersion = 6)
    {
        var fields = new List<string?>
        {
            ordinal.ToString(CultureInfo.InvariantCulture), EventId.ToString("D"), Kind.ToString(),
            OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture), StationId, PrincipalId?.ToString("D"),
            CredentialId?.ToString("D"), TokenId?.ToString("D"), RecoveryKitId?.ToString("D"), ReasonCode,
            WindowsSid, PasswordPolicyVersion, BlocklistId, BlocklistVersion, HashBaselineVersion,
            StateRevision.ToString(CultureInfo.InvariantCulture), HashTargetCost.ToString(CultureInfo.InvariantCulture),
            RecordCost.ToString(CultureInfo.InvariantCulture)
        };

        if (schemaVersion >= 4)
        {
            fields.AddRange(new string?[]
            {
                AuthenticationPolicyId, AuthenticationPolicyVersion, AuthenticationPolicyHash,
                ProtectedAttemptIdentifier, AccountFailures.ToString(CultureInfo.InvariantCulture),
                StationFailures.ToString(CultureInfo.InvariantCulture),
                NextAllowedAtUtc?.ToString("O", CultureInfo.InvariantCulture), SessionId?.ToString("D"),
                DelayTicks.ToString(CultureInfo.InvariantCulture)
            });
        }

        if (schemaVersion >= 5)
        {
            fields.AddRange(new string?[]
            {
                AuthorizationPolicyId, AuthorizationPolicyVersion, AuthorizationPolicyHash,
                ActorPrincipalId?.ToString("D"), CommandCorrelationId?.ToString("D"),
                StepUpGrantId?.ToString("D"), RequiredPermission, TargetPrincipalId?.ToString("D"),
                AuthorizationRevision.ToString(CultureInfo.InvariantCulture), ManagementReason, ActionTargetId,
                BoundCommandCorrelationId?.ToString("D"), ActionCommandKind,
                PreviousPermissions, ResultingPermissions
            });
        }

        if (schemaVersion >= 6)
        {
            fields.AddRange(new string?[]
            {
                OperationId?.ToString("D"), RecoveryCodeId?.ToString("D"),
                PreviousRecoveryKitId?.ToString("D"), RecoverySafetyEvidence
            });
        }

        if (schemaVersion >= CameraRecoveryStoreOptions.SchemaVersion)
        {
            fields.AddRange(new string?[]
            {
                CameraRecoveryExpectedCycleId?.ToString("D"), CameraRecoveryLogicalRole,
                CameraRecoveryReasonCode
            });
        }

        schemaVersion = AuditChainDatabase.EnvelopeGeneration(schemaVersion);
        if (schemaVersion is < 3 or > RecipeLifecycleStoreOptions.SchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (PlcRecipeActivationEvidence is not null)
        {
            // The optional 50th field exists only on the schema-31 envelope, so a
            // store that cannot carry the evidence never silently drops it.
            if (schemaVersion < RecipeSelectionStoreOptions.SchemaVersion)
                throw new InvalidOperationException("AuditPlcRecipeActivationEvidenceUnsupported");
            fields.Add(PlcRecipeActivationEvidence);
        }

        return AuditCanonical.Encode("IdentityEvent", fields.ToArray());
    }

    internal static long VerifyPayload(byte[] payload, long ordinal, string stationId, int schemaVersion = 6)
    {
        schemaVersion = AuditChainDatabase.EnvelopeGeneration(schemaVersion);
        if (schemaVersion is < 3 or > RecipeLifecycleStoreOptions.SchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));

        using var input = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        int ReadInteger()
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            return BinaryPrimitives.ReadInt32BigEndian(bytes);
        }
        string? ReadValue(int maximumBytes = 1024)
        {
            var marker = reader.ReadByte();
            if (marker == 0) return null;
            AuditChainDatabase.Require(marker == 1, "AuditIdentityPayloadInvalid");
            var length = ReadInteger();
            AuditChainDatabase.Require(length >= 0 && length <= maximumBytes, "AuditIdentityPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            AuditChainDatabase.Require(bytes.Length == length, "AuditIdentityPayloadInvalid");
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        try
        {
            var expectedCount = schemaVersion switch { 3 => 18, 4 => 27, 5 => 42, 6 or 7 or 8 or 9 or 10 => 46, >= 11 and <= ProductionRecoveryStoreOptions.SchemaVersion => 49, RecipeSelectionStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion => 49, _ => 0 };
            // Schema 31 may append the one optional PLC evidence field; every other
            // schema keeps exactly its own length, so existing rows stay 49 fields.
            var maximumCount = schemaVersion >= RecipeSelectionStoreOptions.SchemaVersion
                ? expectedCount + 1 : expectedCount;
            var version = ReadInteger();
            var label = ReadValue();
            var declaredCount = ReadInteger();
            AuditChainDatabase.Require(version == AuditCanonical.CanonicalizationVersion &&
                label == "IdentityEvent" && declaredCount >= expectedCount && declaredCount <= maximumCount,
                "AuditIdentityPayloadInvalid");
            var fields = Enumerable.Range(0, declaredCount).Select(index => ReadValue(
                index == 49 ? PlcRecipeActivationIdentityCodec.MaximumEvidenceLength :
                schemaVersion >= ProductionRecoveryStoreOptions.SchemaVersion && index == 45 ? 2048 : 1024)).ToArray();
            AuditChainDatabase.Require(input.Position == input.Length &&
                fields[0] == ordinal.ToString(CultureInfo.InvariantCulture) && fields[4] == stationId &&
                Guid.TryParseExact(fields[1], "D", out var eventId) && eventId != Guid.Empty &&
                Enum.TryParse<IdentityEventKind>(fields[2], out var kind) && Enum.IsDefined(kind) &&
                fields[2] == kind.ToString() &&
                DateTimeOffset.TryParseExact(fields[3], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var occurred) && occurred != default &&
                fields[9] is { Length: > 0 and <= 128 }, "AuditIdentityPayloadInvalid");
            if (Enum.TryParse<IdentityEventKind>(fields[2], out var legacyKind))
            {
                AuditChainDatabase.Require(schemaVersion >= 5
                    ? schemaVersion >= 6 || (int)legacyKind < (int)IdentityEventKind.AdministratorRecovered
                    : (int)legacyKind <= (int)IdentityEventKind.SessionSignInCancelled,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= 7 || legacyKind != IdentityEventKind.AlarmActionAuthorized,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= 9 || legacyKind != IdentityEventKind.RecipeDraftSaved,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= 10 ||
                    legacyKind is not IdentityEventKind.CameraSetupActionAuthorized and
                    not IdentityEventKind.CameraSetupOperationCompleted,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= CameraRecoveryStoreOptions.SchemaVersion ||
                    legacyKind is not IdentityEventKind.CameraRecoveryCycleStartAuthorized and
                    not IdentityEventKind.CameraRecoveryCycleStartCompleted and
                    not IdentityEventKind.CameraRecoveryCycleStartFailed,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion < 5 || schemaVersion >= ImagingSetupStoreOptions.SchemaVersion ||
                    fields[39] != AuditedCommandKind.DeclareImagingSetup.ToString(),
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= CalibrationSessionStoreOptions.SchemaVersion ||
                    legacyKind is not IdentityEventKind.CalibrationSessionStartAuthorized and
                    not IdentityEventKind.CalibrationSessionActionAuthorized,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= CalibrationGovernanceStoreOptions.SchemaVersion ||
                    legacyKind != IdentityEventKind.CalibrationGovernanceActionAuthorized,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= RecipeReleaseStoreOptions.SchemaVersion ||
                    legacyKind != IdentityEventKind.RecipeReleased, "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= PlcResultContractStoreOptions.SchemaVersion ||
                    legacyKind != IdentityEventKind.PlcResultContractChanged, "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= RecipeActivationStoreOptions.SchemaVersion ||
                    legacyKind is not (IdentityEventKind.RecipeActivationAdmitted or
                        IdentityEventKind.RecipeActivationCompleted or
                        IdentityEventKind.RecipeActivationFailed or
                        IdentityEventKind.RecipeActivationCancelled), "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= PreviewSessionStoreOptions.SchemaVersion ||
                    legacyKind is not (IdentityEventKind.PreviewSessionStartAuthorized or
                        IdentityEventKind.PreviewSessionActionAuthorized or
                        IdentityEventKind.PreviewSessionCompleted or
                        IdentityEventKind.PreviewSessionFailed), "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= ManualInspectionStoreOptions.SchemaVersion ||
                    legacyKind is not (IdentityEventKind.ManualInspectionSessionStartAuthorized or
                        IdentityEventKind.ManualInspectionSessionActionAuthorized or
                        IdentityEventKind.ManualInspectionSessionCompleted or
                        IdentityEventKind.ManualInspectionSessionFailed), "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= ProductionAdmissionStoreOptions.SchemaVersion ||
                    legacyKind is not (IdentityEventKind.ProductionAdmissionArmAuthorized or
                        IdentityEventKind.ProductionAdmissionCompleted or IdentityEventKind.ProductionAdmissionFailed),
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= StationQualificationStoreOptions.SchemaVersion ||
                    legacyKind != IdentityEventKind.StationQualificationAuthorized,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= RecipeTransferStoreOptions.SchemaVersion ||
                    legacyKind != IdentityEventKind.RecipeTransferAuthorized, "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= TraceStoragePolicyStoreOptions.SchemaVersion ||
                    legacyKind != IdentityEventKind.TraceStoragePolicyAuthorized, "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= ProductionInspectionStoreOptions.SchemaVersion ||
                    legacyKind is not (IdentityEventKind.ProductionInspectionAdmitted or
                        IdentityEventKind.ProductionInspectionCoreCommitted or
                        IdentityEventKind.ProductionInspectionFailed), "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= PartIdentityStoreOptions.SchemaVersion ||
                    legacyKind is not (IdentityEventKind.PartIdentityRejectedTriggerRecorded or
                        IdentityEventKind.PartIdentityCorrectionAuthorized), "AuditIdentityPayloadInvalid");
                // Recovery uses the existing opaque envelope, gated by schema 30 command authorization.
                AuditChainDatabase.Require(schemaVersion >= ProductionRecoveryStoreOptions.SchemaVersion ||
                    legacyKind is not (IdentityEventKind.ProductionRecoveryAuthorized or
                        IdentityEventKind.ProductionRecoveryCompleted or
                        IdentityEventKind.ProductionRecoveryFailed), "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= RecipeSelectionStoreOptions.SchemaVersion ||
                    legacyKind != IdentityEventKind.RecipeSelectionChanged, "AuditIdentityPayloadInvalid");
            }
            for (var index = 5; index <= 8; index++)
                AuditChainDatabase.Require(fields[index] is null ||
                    Guid.TryParseExact(fields[index], "D", out _), "AuditIdentityPayloadInvalid");
            AuditChainDatabase.Require(long.TryParse(fields[15], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var revision) && revision >= 0 &&
                fields[15] == revision.ToString(CultureInfo.InvariantCulture),
                "AuditIdentityPayloadInvalid");
            for (var index = 16; index <= 17; index++)
                AuditChainDatabase.Require(int.TryParse(fields[index], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var cost) &&
                    cost is >= 0 and <= PasswordHashBaseline.MaximumSupportedIterations,
                    "AuditIdentityPayloadInvalid");

            if (schemaVersion >= 4)
            {
                AuditChainDatabase.Require(fields[18] is { Length: > 0 and <= 128 } &&
                    fields[19] is { Length: > 0 and <= 128 } && IsHash(fields[20]) &&
                    (fields[21] is null || IsHash(fields[21])), "AuditAuthenticationPayloadInvalid");
                for (var index = 22; index <= 23; index++)
                    AuditChainDatabase.Require(int.TryParse(fields[index], NumberStyles.None,
                            CultureInfo.InvariantCulture, out var failures) &&
                        failures is >= 0 and <= AuthenticationPolicy.ReleaseFailureLimitCeiling,
                        "AuditAuthenticationPayloadInvalid");
                AuditChainDatabase.Require((fields[24] is null ||
                        DateTimeOffset.TryParseExact(fields[24], "O", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out _)) &&
                    (fields[25] is null || Guid.TryParseExact(fields[25], "D", out _)) &&
                    long.TryParse(fields[26], NumberStyles.None, CultureInfo.InvariantCulture,
                        out var delay) && delay >= 0 && delay <= TimeSpan.FromHours(1).Ticks,
                    "AuditAuthenticationPayloadInvalid");
            }

            if (schemaVersion >= 5)
            {
                AuditChainDatabase.Require(fields[27] is { Length: > 0 and <= 128 } &&
                    fields[28] is { Length: > 0 and <= 128 } && IsHash(fields[29]),
                    "AuditAuthorizationPayloadInvalid");
                foreach (var index in new[] { 30, 31, 32, 34 })
                    AuditChainDatabase.Require(fields[index] is null ||
                        Guid.TryParseExact(fields[index], "D", out _),
                        "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[33] is null || IsSafeIdentifier(fields[33]),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(long.TryParse(fields[35], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var authorizationRevision) &&
                    authorizationRevision >= 0 &&
                    fields[35] == authorizationRevision.ToString(CultureInfo.InvariantCulture),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[36] is null ||
                    (Enum.TryParse<IdentityManagementReason>(fields[36], out var managementReason) &&
                     Enum.IsDefined(managementReason) && fields[36] == managementReason.ToString()),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[37] is null || IsSafeIdentifier(fields[37]),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[38] is null ||
                    Guid.TryParseExact(fields[38], "D", out _), "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[39] is null ||
                    (Enum.TryParse<AuditedCommandKind>(fields[39], out var actionKind) &&
                     Enum.IsDefined(actionKind) && actionKind is not AuditedCommandKind.Unsupported and
                     not AuditedCommandKind.GracefulProductionStop &&
                     (schemaVersion >= 9 || actionKind is not (AuditedCommandKind.SaveRecipeDraft or
                         AuditedCommandKind.MigrateAlgorithmConfiguration)) &&
                     (schemaVersion >= 10 || (actionKind != AuditedCommandKind.RebindCamera &&
                        actionKind != AuditedCommandKind.ApplyCameraDebugConfiguration)) &&
                    (schemaVersion >= CameraRecoveryStoreOptions.SchemaVersion ||
                        actionKind != AuditedCommandKind.StartCameraRecoveryCycle) &&
                    (schemaVersion >= CameraNetworkStoreOptions.SchemaVersion ||
                        actionKind != AuditedCommandKind.ChangeCameraNetworkConfiguration) &&
                    (schemaVersion >= ImagingSetupStoreOptions.SchemaVersion ||
                        actionKind != AuditedCommandKind.DeclareImagingSetup) &&
                    (schemaVersion >= CalibrationSessionStoreOptions.SchemaVersion ||
                        actionKind is not (>= AuditedCommandKind.StartCalibrationSession and <= AuditedCommandKind.ExitCalibrationSession)) &&
                    (schemaVersion >= CalibrationGovernanceStoreOptions.SchemaVersion ||
                        actionKind is not (>= AuditedCommandKind.PublishCalibrationAcceptancePolicy and <= AuditedCommandKind.RecordPhysicalCalibrationVerification)) &&
                     (schemaVersion >= RecipeReleaseStoreOptions.SchemaVersion || actionKind != AuditedCommandKind.ReleaseRecipe) &&
                     (schemaVersion >= PlcResultContractStoreOptions.SchemaVersion || actionKind != AuditedCommandKind.ChangePlcResultContract) &&
                    (schemaVersion >= RecipeActivationStoreOptions.SchemaVersion ||
                        actionKind != AuditedCommandKind.ActivateRecipe) &&
                    (schemaVersion >= CalibrationImportStoreOptions.SchemaVersion ||
                        actionKind != AuditedCommandKind.SelectHistoricalCalibration) &&
                      (schemaVersion >= CalibrationImportStoreOptions.SchemaVersion || actionKind is not (>= AuditedCommandKind.ImportCalibrationPackage and <= AuditedCommandKind.PublishImportedCalibration)) &&
                    (schemaVersion >= ManualInspectionStoreOptions.SchemaVersion || actionKind is not (>= AuditedCommandKind.StartManualInspectionSession and <= AuditedCommandKind.ExitManualInspectionSession)) &&
                    (schemaVersion >= StationQualificationStoreOptions.SchemaVersion || actionKind is not (>= AuditedCommandKind.StartStationQualificationSession and <= AuditedCommandKind.ExitStationQualificationSession)) &&
                    (schemaVersion >= RecipeTransferStoreOptions.SchemaVersion || actionKind is not (>= AuditedCommandKind.ReplaceRecipeTrustStore and <= AuditedCommandKind.ImportRecipeTransfer)) &&
                    (schemaVersion >= TraceStoragePolicyStoreOptions.SchemaVersion || actionKind != AuditedCommandKind.PublishTraceStoragePolicy) &&
                    (schemaVersion >= ProductionRecoveryStoreOptions.SchemaVersion ||
                        actionKind != AuditedCommandKind.ManualProductionRecovery) &&
                     (schemaVersion >= RecipeSelectionStoreOptions.SchemaVersion ||
                         actionKind != AuditedCommandKind.ChangeRecipeSelection) &&
                      fields[39] == actionKind.ToString()),
                    "AuditAuthorizationPayloadInvalid");
                // Permission 31 is part of the current default role bundle even
                // for identity-only/alarm schema 7/8 stores. It is a capability
                // carried by the signed permission list; the draft mutation/event
                // itself remains schema-9 gated below and in the store dispatcher.
                var maximumPermissions = schemaVersion >= RecipeTransferStoreOptions.SchemaVersion ? 38 :
                    schemaVersion >= ManualInspectionStoreOptions.SchemaVersion ? 36 :
                    schemaVersion >= PreviewSessionStoreOptions.SchemaVersion ? 35 :
                    schemaVersion >= 15 ? 34 : schemaVersion >= 14 ? 32 : schemaVersion >= 7 ? 31 : 28;
                AuditChainDatabase.Require(IsPermissionSet(fields[40], maximumPermissions) &&
                    IsPermissionSet(fields[41], maximumPermissions),
                    "AuditAuthorizationPayloadInvalid");
            }

            if (schemaVersion >= 6)
            {
                foreach (var index in new[] { 42, 43, 44 })
                    AuditChainDatabase.Require(fields[index] is null ||
                        Guid.TryParseExact(fields[index], "D", out _), "AuditRecoveryPayloadInvalid");
                var productionRecoveryEvent = schemaVersion >= ProductionRecoveryStoreOptions.SchemaVersion &&
                    Enum.TryParse<IdentityEventKind>(fields[2], out var productionRecoveryKind) &&
                    productionRecoveryKind is IdentityEventKind.ProductionRecoveryAuthorized or
                        IdentityEventKind.ProductionRecoveryCompleted or
                        IdentityEventKind.ProductionRecoveryFailed;
                AuditChainDatabase.Require(fields[45] is null ||
                    (productionRecoveryEvent
                        ? IsProductionRecoverySafetyEvidence(fields[45])
                        : IsSafeIdentifier(fields[45])),
                    productionRecoveryEvent ? "AuditProductionRecoverySafetyEvidenceInvalid" :
                        "AuditRecoveryPayloadInvalid");
                if (productionRecoveryEvent)
                    AuditChainDatabase.Require(fields[45] is not null,
                        "AuditProductionRecoverySafetyEvidenceMissing");
            }

            if (schemaVersion >= CameraRecoveryStoreOptions.SchemaVersion)
            {
                var recoveryKind = Enum.Parse<IdentityEventKind>(fields[2]!);
                var recoveryEvent = recoveryKind is IdentityEventKind.CameraRecoveryCycleStartAuthorized or
                    IdentityEventKind.CameraRecoveryCycleStartCompleted or
                    IdentityEventKind.CameraRecoveryCycleStartFailed;
                var hasRecoveryFields = fields[46] is not null || fields[47] is not null || fields[48] is not null;
                AuditChainDatabase.Require(hasRecoveryFields == recoveryEvent,
                    "AuditCameraRecoveryPayloadInvalid");
                if (recoveryEvent)
                {
                    AuditChainDatabase.Require(Guid.TryParseExact(fields[46], "D", out var expectedCycle) &&
                        expectedCycle != Guid.Empty && IsStableAsciiIdentifier(fields[47]) &&
                        IsStableAsciiIdentifier(fields[48]), "AuditCameraRecoveryPayloadInvalid");
                }
            }

            if (fields.Length == 50)
            {
                // The optional 50th field exists only on the schema-31 envelope and
                // only for a true PLC Adapter system request; it is bounded evidence,
                // never authority.
                AuditChainDatabase.Require(fields[49] is not null, "AuditPlcRecipeActivationEvidenceMissing");
                AuditChainDatabase.Require(IsPlcRecipeActivationEvidenceRow(fields),
                    "AuditPlcRecipeActivationEvidenceInvalid");
            }

            return revision;
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        }
    }

    /// <summary>
    /// Replays must be authorized by the durable, signed RecipeDraftSaved event.
    /// The in-memory Step-Up grant is intentionally not consulted here: it may have
    /// been consumed, purged, or lost across a restart. Audit verification has
    /// already validated the complete envelope before this matcher is called.
    /// </summary>
    internal static bool MatchesRecipeDraftAuthorization(byte[] payload, long ordinal, string stationId,
        Guid principalId, Guid sessionId, long authorizationRevision, Guid operationId, Guid draftId,
        Guid? stepUpGrantId, RecipeDraftMigrationPlan? migrationPlan = null)
    {
        try
        {
            var fields = DecodeFields(payload);
            var grant = stepUpGrantId?.ToString("D");
            return fields.Length is 46 or 49 &&
                fields[0] == ordinal.ToString(CultureInfo.InvariantCulture) &&
                fields[2] == IdentityEventKind.RecipeDraftSaved.ToString() &&
                fields[4] == stationId && fields[5] == principalId.ToString("D") &&
                fields[25] == sessionId.ToString("D") && fields[30] == principalId.ToString("D") &&
                fields[31] == operationId.ToString("D") && fields[32] == grant &&
                fields[33] == Permission.EditRecipeDraft.ToString() &&
                fields[35] == authorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == (migrationPlan?.ContentHash ?? draftId.ToString("D")) && fields[38] == operationId.ToString("D") &&
                fields[39] == (migrationPlan is null ? AuditedCommandKind.SaveRecipeDraft :
                    AuditedCommandKind.MigrateAlgorithmConfiguration).ToString() &&
                fields[9] == (migrationPlan is null ? "RecipeDraftAuthorized" : "RecipeDraftMigrationAuthorized");
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
    }

    internal static bool MatchesRecipeReleaseAuthorization(byte[] payload, long ordinal, string stationId,
        RecipeReleaseRecord record)
    {
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, RecipeReleaseStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            return fields.Length == 49 && fields[2] == IdentityEventKind.RecipeReleased.ToString() &&
                fields[3] == record.ReleasedAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
                fields[5] == record.ApproverPrincipalId.ToString("D") && fields[9] == "RecipeReleased" &&
                fields[25] == record.ApproverSessionId.ToString("D") &&
                fields[27] == record.AuthorizationPolicy.Id && fields[28] == record.AuthorizationPolicy.Version &&
                fields[29] == record.AuthorizationPolicy.ContentHash &&
                fields[30] == record.ApproverPrincipalId.ToString("D") &&
                fields[31] == record.OperationId.ToString("D") && fields[32] == record.StepUpGrantId.ToString("D") &&
                fields[33] == Permission.ReleaseRecipe.ToString() &&
                fields[35] == record.ApproverAuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == record.AuthorizationTarget && fields[38] == record.OperationId.ToString("D") &&
                fields[39] == AuditedCommandKind.ReleaseRecipe.ToString() && fields[42] == record.OperationId.ToString("D");
        }
        catch (Exception exception) when (exception is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
    }

    internal static bool MatchesPlcResultContractAuthorization(byte[] payload, long ordinal, string stationId,
        PlcResultContractRevision revision)
    {
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, PlcResultContractStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            return fields.Length == 49 && fields[2] == IdentityEventKind.PlcResultContractChanged.ToString() &&
                fields[3] == revision.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
                fields[5] == revision.ActorPrincipalId.ToString("D") && fields[9] == "PlcResultContractChanged" &&
                fields[25] == revision.ActorSessionId.ToString("D") &&
                fields[27] == revision.AuthorizationPolicy.Id &&
                fields[28] == revision.AuthorizationPolicy.Version &&
                fields[29] == revision.AuthorizationPolicy.ContentHash &&
                fields[30] == revision.ActorPrincipalId.ToString("D") &&
                fields[31] == revision.OperationId.ToString("D") &&
                fields[32] == revision.StepUpGrantId.ToString("D") &&
                fields[33] == Permission.ManagePlcResultContract.ToString() &&
                fields[35] == revision.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == revision.AuthorizationTarget && fields[38] == revision.OperationId.ToString("D") &&
                fields[39] == AuditedCommandKind.ChangePlcResultContract.ToString() &&
                fields[42] == revision.OperationId.ToString("D");
        }
        catch (Exception exception) when (exception is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
    }

    /// <summary>Matches the signed identity event for one activation ledger event.</summary>
    internal static bool MatchesRecipeActivationAuthorization(byte[] payload, long ordinal,
        string stationId, RecipeActivationRecord record, int schemaVersion = RecipeActivationStoreOptions.SchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, schemaVersion);
            var fields = DecodeFields(payload);
            return record.Actor is { IsPlcAdapter: true }
                ? MatchesPlcRecipeActivationAuthorization(fields, record, stationId)
                : MatchesHumanRecipeActivationAuthorization(fields, record, stationId);
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        { return false; }
    }

    private static bool MatchesHumanRecipeActivationAuthorization(string?[] fields,
        RecipeActivationRecord record, string stationId)
    {
        var expectedPermission = record.HistoricalSelection is null ? Permission.ActivateRecipe :
            Permission.SelectHistoricalCalibration;
        var expectedCommandKind = record.HistoricalSelection is null ? AuditedCommandKind.ActivateRecipe :
            AuditedCommandKind.SelectHistoricalCalibration;
        if (fields.Length != 49 || fields[2] != ActivationKind(record.Outcome.State).ToString() ||
            fields[3] != record.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) ||
            fields[4] != stationId || fields[9] != record.Outcome.ReasonCode ||
            fields[33] != expectedPermission.ToString() ||
            fields[37] != record.AuthorizationTarget ||
            fields[38] is not { Length: 36 } || !Guid.TryParseExact(fields[38], "D", out var boundCorrelation) ||
            boundCorrelation != record.OperationId || fields[39] != expectedCommandKind.ToString() ||
            fields[42] != record.OperationId.ToString("D") ||
            // An unauthenticated pre-admission rejection has no policy on the
            // activation record.  The identity writer still binds the active
            // deployment policy to its signed event, so the absence of a
            // record policy must not require those payload fields to be null.
            // Authenticated records remain an exact policy match.
            (record.AuthorizationPolicy is not null &&
                (fields[27] != record.AuthorizationPolicy.Id || fields[28] != record.AuthorizationPolicy.Version ||
                 fields[29] != record.AuthorizationPolicy.ContentHash)) ||
            (record.ActorPrincipalId is null
                ? fields[5] is not null || fields[30] is not null
                : fields[5] != record.ActorPrincipalId.Value.ToString("D") ||
                  fields[30] != record.ActorPrincipalId.Value.ToString("D")) ||
            (record.ActorSessionId is null
                ? fields[25] is not null
                : fields[25] != record.ActorSessionId.Value.ToString("D")) ||
            (record.ActorAuthorizationRevision is null
                ? fields[35] != "0"
                : fields[35] != record.ActorAuthorizationRevision.Value.ToString(CultureInfo.InvariantCulture)))
            return false;
        return fields[31] == fields[38] && fields[34] == fields[5];
    }

    /// <summary>
    /// A PLC adapter record is bound to the schema-31 envelope: the exact request
    /// context, the exact correlated command and action target, the absence of every
    /// human field, and the accepted/rejected operation and reason shape the identity
    /// writer produced for that same event.
    /// </summary>
    private static bool MatchesPlcRecipeActivationAuthorization(string?[] fields,
        RecipeActivationRecord record, string stationId)
    {
        var actor = record.Actor;
        var evidence = fields.Length == 50 ? fields[49] : null;
        if (actor is null || evidence is null) return false;
        PlcRecipeActivationRequestContext context;
        try { context = PlcRecipeActivationIdentityCodec.Decode(evidence); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
            FormatException)
        { return false; }
        if (!actor.PlcRequestContext!.Matches(context)) return false;
        var accepted = record.Outcome.State is RecipeActivationOutcomeState.Admitted or
            RecipeActivationOutcomeState.Succeeded;
        if (fields[5] is not null || fields[25] is not null || fields[30] is not null || fields[32] is not null ||
            fields[33] is not null || fields[34] is not null || fields[35] != "0")
            return false;
        if (fields[2] != ActivationKind(record.Outcome.State).ToString() ||
            fields[3] != record.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) ||
            fields[4] != stationId || fields[9] != record.Outcome.ReasonCode ||
            fields[37] != record.AuthorizationTarget ||
            fields[39] != AuditedCommandKind.ActivateRecipe.ToString() ||
            fields[38] is not { Length: 36 } || !Guid.TryParseExact(fields[38], "D", out var boundCorrelation) ||
            boundCorrelation != record.OperationId)
            return false;
        // An accepted activation carries the operation correlation; a rejection may
        // record the correlated command without an operation of its own.
        if (accepted
            ? fields[31] != fields[38] || fields[42] != fields[38]
            : (fields[31] is not null && fields[31] != fields[38]) ||
                (fields[42] is not null && fields[42] != fields[38]))
            return false;
        return record.AuthorizationPolicy is null ||
            (fields[27] == record.AuthorizationPolicy.Id &&
             fields[28] == record.AuthorizationPolicy.Version &&
             fields[29] == record.AuthorizationPolicy.ContentHash);
    }

    internal static bool MatchesPreviewAuthorization(byte[] payload, long ordinal,
        string stationId, PreviewSessionEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, PreviewSessionStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            return fields.Length == 49 && fields[4] == stationId &&
                (fields[2] is nameof(IdentityEventKind.PreviewSessionStartAuthorized) or
                 nameof(IdentityEventKind.PreviewSessionActionAuthorized) or
                 nameof(IdentityEventKind.PreviewSessionCompleted) or
                 nameof(IdentityEventKind.PreviewSessionFailed)) &&
                fields[3] == value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
                fields[5] == value.ActorPrincipalId.ToString("D") &&
                fields[25] == value.ActorSessionId.ToString("D") &&
                fields[30] == value.ActorPrincipalId.ToString("D") &&
                fields[31] == value.CommandCorrelationId.ToString("D") &&
                fields[33] == Permission.RunPreview.ToString() &&
                fields[35] == value.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == value.AuthorizationTarget &&
                fields[38] == value.CommandCorrelationId.ToString("D") &&
                fields[39] == value.CommandKind.ToString() &&
                fields[42] == value.SessionId.ToString("D");
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        { return false; }
    }

    internal static bool MatchesManualInspectionAuthorization(byte[] payload, long ordinal,
        string stationId, ManualInspectionSessionEvent value,
        SharpInspect.Runtime.CommandAuditFact? accepted = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, ManualInspectionStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            return accepted is not null && fields.Length == 49 && fields[4] == stationId &&
                (fields[2] is nameof(IdentityEventKind.ManualInspectionSessionStartAuthorized) or
                 nameof(IdentityEventKind.ManualInspectionSessionActionAuthorized) or
                 nameof(IdentityEventKind.ManualInspectionSessionCompleted) or
                 nameof(IdentityEventKind.ManualInspectionSessionFailed)) &&
                fields[3] == value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
                fields[5] == value.ActorPrincipalId.ToString("D") &&
                fields[9] == value.ReasonCode &&
                fields[25] == value.ActorSessionId.ToString("D") &&
                fields[27] == value.Header.AuthorizationPolicy.Id &&
                fields[28] == value.Header.AuthorizationPolicy.Version &&
                fields[29] == value.Header.AuthorizationPolicy.ContentHash &&
                fields[30] == value.ActorPrincipalId.ToString("D") &&
                fields[31] == value.CommandCorrelationId.ToString("D") &&
                fields[32] == accepted.ClaimedStepUpGrantId?.ToString("D") &&
                fields[33] == Permission.RunManualInspection.ToString() &&
                fields[34] == value.ActorPrincipalId.ToString("D") &&
                fields[35] == value.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == value.AuthorizationTarget &&
                fields[38] == value.CommandCorrelationId.ToString("D") &&
                fields[39] == value.CommandKind.ToString() &&
                fields[42] == value.SessionId.ToString("D");
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        { return false; }
    }

    /// <summary>
    /// Matches the durable human authorization, exact command target and policy for one Recipe transfer operation.
    /// </summary>
    internal static bool MatchesRecipeTransferAuthorization(byte[] payload, long ordinal, string stationId,
        CommandAuditFact accepted, string authorizationTarget, Guid principalId, Guid sessionId,
        long authorizationRevision, RecipeContractReference authorizationPolicy)
    {
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, RecipeTransferStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            var permission = accepted.CommandKind switch
            {
                AuditedCommandKind.ReplaceRecipeTrustStore => Permission.ManageRecipeTrustStore,
                AuditedCommandKind.CreateRecipeSigningKey or AuditedCommandKind.RetireRecipeSigningKey => Permission.ManageRecipeSigningKeys,
                AuditedCommandKind.ImportRecipeTransfer => Permission.ImportRecipe,
                AuditedCommandKind.ExportRecipeTransfer => Permission.ExportRecipe,
                _ => Permission.None
            };
            return permission != Permission.None && fields.Length == 49 &&
                fields[2] == IdentityEventKind.RecipeTransferAuthorized.ToString() &&
                fields[3] == accepted.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
                fields[9] == accepted.ReasonCode && accepted.ReasonCode == "RecipeTransferAuthorized" &&
                accepted.Phase == CommandAuditPhase.Outcome && accepted.Disposition == CommandDisposition.Accepted &&
                accepted.Source is { } source && Enum.IsDefined(source) && fields[4] == stationId &&
                fields[5] == principalId.ToString("D") && fields[25] == sessionId.ToString("D") &&
                fields[27] == authorizationPolicy.Id && fields[28] == authorizationPolicy.Version &&
                fields[29] == authorizationPolicy.ContentHash && fields[30] == principalId.ToString("D") &&
                fields[31] == accepted.CorrelationId.ToString("D") &&
                fields[32] == accepted.ClaimedStepUpGrantId?.ToString("D") && fields[33] == permission.ToString() &&
                fields[34] is null && fields[35] == authorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == authorizationTarget && fields[38] == accepted.CorrelationId.ToString("D") &&
                fields[39] == accepted.CommandKind.ToString() && accepted.ClaimedPrincipalId == principalId.ToString("D") &&
                accepted.ClaimedSessionId == sessionId && accepted.AuthenticatedHumanPrincipalId == principalId.ToString("D") &&
                (permission is not (Permission.ManageRecipeTrustStore or Permission.ManageRecipeSigningKeys) ||
                    accepted.ClaimedStepUpGrantId is not null);
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        { return false; }
    }

    internal static bool MatchesTraceStoragePolicyAuthorization(byte[] payload, long ordinal, string stationId,
        CommandAuditFact accepted, string authorizationTarget, Guid principalId, Guid sessionId,
        long authorizationRevision, RecipeContractReference authorizationPolicy)
    {
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, TraceStoragePolicyStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            return fields.Length == 49 &&
                fields[2] == IdentityEventKind.TraceStoragePolicyAuthorized.ToString() &&
                fields[3] == accepted.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
                fields[9] == accepted.ReasonCode && accepted.ReasonCode == "TraceStoragePolicyAuthorized" &&
                accepted.CommandKind == AuditedCommandKind.PublishTraceStoragePolicy &&
                accepted.Phase == CommandAuditPhase.Outcome && accepted.Disposition == CommandDisposition.Accepted &&
                accepted.Source is { } source && Enum.IsDefined(source) && fields[4] == stationId &&
                fields[5] == principalId.ToString("D") && fields[25] == sessionId.ToString("D") &&
                fields[27] == authorizationPolicy.Id && fields[28] == authorizationPolicy.Version &&
                fields[29] == authorizationPolicy.ContentHash && fields[30] == principalId.ToString("D") &&
                fields[31] == accepted.CorrelationId.ToString("D") &&
                accepted.ClaimedStepUpGrantId is not null && fields[32] == accepted.ClaimedStepUpGrantId.Value.ToString("D") &&
                fields[33] == Permission.ManageProductionPolicy.ToString() && fields[34] is null &&
                fields[35] == authorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == authorizationTarget && fields[38] == accepted.CorrelationId.ToString("D") &&
                fields[39] == accepted.CommandKind.ToString() && accepted.ClaimedPrincipalId == principalId.ToString("D") &&
                accepted.ClaimedSessionId == sessionId && accepted.AuthenticatedHumanPrincipalId == principalId.ToString("D");
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        { return false; }
    }

    /// <summary>Matches the complete actor, policy, grant, command and session tuple for a qualification event.</summary>
    internal static bool MatchesStationQualificationAuthorization(byte[] payload, long ordinal,
        string stationId, StationQualificationSessionEvent value,
        SharpInspect.Runtime.CommandAuditFact accepted)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(accepted);
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, StationQualificationStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            return fields.Length == 49 &&
                fields[2] == IdentityEventKind.StationQualificationAuthorized.ToString() &&
                fields[3] == accepted.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
                fields[9] == accepted.ReasonCode &&
                accepted.Phase == CommandAuditPhase.Outcome &&
                accepted.Disposition == CommandDisposition.Accepted &&
                accepted.Source == CommandSource.PhysicalConsole &&
                fields[4] == stationId &&
                fields[5] == value.Header.ActorPrincipalId.ToString("D") &&
                fields[25] == value.Header.ActorSessionId.ToString("D") &&
                fields[27] == value.Header.AuthorizationPolicy.Id &&
                fields[28] == value.Header.AuthorizationPolicy.Version &&
                fields[29] == value.Header.AuthorizationPolicy.ContentHash &&
                fields[30] == value.Header.ActorPrincipalId.ToString("D") &&
                fields[31] == value.CommandCorrelationId.ToString("D") &&
                fields[32] == accepted.ClaimedStepUpGrantId?.ToString("D") &&
                fields[33] == Permission.RunStationQualification.ToString() &&
                fields[34] == value.Header.ActorPrincipalId.ToString("D") &&
                fields[35] == value.Header.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == value.CommandAuthorizationTarget &&
                fields[38] == value.CommandCorrelationId.ToString("D") &&
                fields[39] == value.CommandKind.ToString() &&
                fields[42] == value.SessionId.ToString("D") &&
                accepted.AttemptId == value.AttemptId &&
                accepted.CorrelationId == value.CommandCorrelationId &&
                accepted.RuntimeEpoch == value.RuntimeEpoch &&
                accepted.CommandKind == value.CommandKind &&
                accepted.ClaimedPrincipalId == value.Header.ActorPrincipalId.ToString("D") &&
                accepted.ClaimedSessionId == value.Header.ActorSessionId &&
                accepted.AuthenticatedHumanPrincipalId == value.Header.ActorPrincipalId.ToString("D") &&
                (value.CommandKind != AuditedCommandKind.StartStationQualificationSession ||
                    accepted.ClaimedStepUpGrantId == value.Header.StepUpGrantId);
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        { return false; }
    }

    /// <summary>
    /// Matches the signed authorization tuple for a historical PartIdentity
    /// correction.  The command and identity events have independent event
    /// identifiers; their correlation, attempt, actor, policy, grant and
    /// canonical target are the binding.  The current authorization policy is
    /// intentionally not reconstructed from the protected identity blob here;
    /// its signed id/version/hash tuple is required and can be checked against
    /// live configuration by the owning service.
    /// </summary>
    internal static bool MatchesPartIdentityCorrectionAuthorization(byte[] payload,
        long ordinal, string stationId, PartIdentityHistoryEvent value,
        SharpInspect.Runtime.CommandAuditFact accepted,
        RecipeContractReference? authorizationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(accepted);
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, PartIdentityStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            if (fields.Length != 49) return false;
            var policyMatches = fields[27] is { Length: > 0 } && fields[28] is { Length: > 0 } &&
                IsHash(fields[29]) && (authorizationPolicy is null ||
                    (fields[27] == authorizationPolicy.Id && fields[28] == authorizationPolicy.Version &&
                     fields[29] == authorizationPolicy.ContentHash));
            return policyMatches &&
                fields[1] != accepted.EventId.ToString("D") &&
                fields[2] == IdentityEventKind.PartIdentityCorrectionAuthorized.ToString() &&
                value.RecordedAtUtc == accepted.OccurredAtUtc &&
                fields[3] == accepted.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture) &&
                fields[4] == stationId && fields[5] == value.ActorPrincipalId?.ToString("D") &&
                fields[9] == "PartIdentityCorrectionAuthorized" &&
                fields[25] == value.ActorSessionId?.ToString("D") &&
                fields[30] == value.ActorPrincipalId?.ToString("D") &&
                fields[31] == value.CorrelationId.ToString("D") &&
                fields[32] == accepted.ClaimedStepUpGrantId?.ToString("D") &&
                fields[33] == Permission.CorrectHistoricalFact.ToString() && fields[34] is null &&
                fields[35] == value.AuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == value.AuthorizationTarget &&
                fields[38] == value.CorrelationId.ToString("D") &&
                fields[39] == AuditedCommandKind.CorrectHistoricalFact.ToString() &&
                fields[42] is null && accepted.Phase == CommandAuditPhase.Outcome &&
                accepted.Disposition == CommandDisposition.Accepted &&
                accepted.Source == CommandSource.PhysicalConsole &&
                accepted.CommandKind == AuditedCommandKind.CorrectHistoricalFact &&
                accepted.AttemptId == value.AttemptId && accepted.CorrelationId == value.CorrelationId &&
                accepted.ClaimedPrincipalId == value.ActorPrincipalId?.ToString("D") &&
                accepted.ClaimedSessionId == value.ActorSessionId &&
                accepted.AuthenticatedHumanPrincipalId == value.ActorPrincipalId?.ToString("D") &&
                accepted.ClaimedStepUpGrantId == value.StepUpGrantId;
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        { return false; }
    }

    /// <summary>
    /// Matches the existing Draft-14 authorization that is reused by a Preview
    /// save event. The draft writer predates command-fact lifecycles, so the
    /// immutable operation/correlation and author evidence are the binding here;
    /// the Preview writer creates its own durable SaveRecipeDraft command fact
    /// after this existing Draft authorization has been verified.
    /// </summary>
    internal static bool MatchesPreviewDraftAuthorization(byte[] payload, long ordinal,
        string stationId, PreviewSessionEvent value, Guid? expectedStepUpGrantId = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, PreviewSessionStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            var draft = value.SavedDraft ?? value.Header.Draft;
            return fields.Length == 49 && fields[2] == IdentityEventKind.RecipeDraftSaved.ToString() &&
                fields[4] == stationId && fields[5] == value.ActorPrincipalId.ToString("D") &&
                fields[9] == "RecipeDraftAuthorized" &&
                fields[25] == value.ActorSessionId.ToString("D") &&
                fields[30] == value.ActorPrincipalId.ToString("D") &&
                fields[31] == value.CommandCorrelationId.ToString("D") &&
                fields[32] == expectedStepUpGrantId?.ToString("D") &&
                fields[33] == Permission.EditRecipeDraft.ToString() &&
                fields[27] == value.Header.AuthorizationPolicy.Id &&
                fields[28] == value.Header.AuthorizationPolicy.Version &&
                fields[29] == value.Header.AuthorizationPolicy.ContentHash &&
                fields[35] == value.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == draft.DraftId.ToString("D") &&
                fields[38] == value.CommandCorrelationId.ToString("D") &&
                fields[39] == AuditedCommandKind.SaveRecipeDraft.ToString();
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        { return false; }
    }

    internal static string? DecodeEventId(byte[] payload) => DecodeFields(payload).ElementAtOrDefault(1);

    internal static bool TryReadCommandCorrelation(byte[] payload, out Guid correlationId)
    {
        correlationId = Guid.Empty;
        try
        {
            var fields = DecodeFields(payload);
            return Guid.TryParseExact(fields.ElementAtOrDefault(31), "D", out correlationId) &&
                correlationId != Guid.Empty;
        }
        catch (Exception exception) when (exception is EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException)
        {
            correlationId = Guid.Empty;
            return false;
        }
    }

    private static IdentityEventKind ActivationKind(RecipeActivationOutcomeState state) => state switch
    {
        RecipeActivationOutcomeState.Admitted => IdentityEventKind.RecipeActivationAdmitted,
        RecipeActivationOutcomeState.Succeeded => IdentityEventKind.RecipeActivationCompleted,
        RecipeActivationOutcomeState.Failed => IdentityEventKind.RecipeActivationFailed,
        RecipeActivationOutcomeState.Cancelled => IdentityEventKind.RecipeActivationCancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    /// <summary>Matches a schema-14 calibration authorization to its immutable session and exact command target.</summary>
    internal static bool MatchesCalibrationAuthorization(byte[] payload, long ordinal, string stationId,
        CalibrationSessionHeader header, AuditedCommandKind commandKind, Guid correlationId,
        string? actionTarget = null)
    {
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, CalibrationSessionStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            var start = commandKind == AuditedCommandKind.StartCalibrationSession;
            return (!start || correlationId == header.Command.CorrelationId) &&
                fields.Length == 49 && commandKind is >= AuditedCommandKind.StartCalibrationSession and
                <= AuditedCommandKind.ExitCalibrationSession &&
                fields[0] == ordinal.ToString(CultureInfo.InvariantCulture) && fields[4] == stationId &&
                fields[2] == (start ? IdentityEventKind.CalibrationSessionStartAuthorized :
                    IdentityEventKind.CalibrationSessionActionAuthorized).ToString() &&
                fields[5] == header.ActorPrincipalId.ToString("D") &&
                fields[25] == header.InteractiveSessionId.ToString("D") &&
                fields[30] == header.ActorPrincipalId.ToString("D") &&
                fields[31] == correlationId.ToString("D") && fields[38] == correlationId.ToString("D") &&
                fields[32] == (start ? header.Command.Invocation.StepUpGrantId?.ToString("D") : null) &&
                fields[33] == Permission.RunCalibration.ToString() &&
                fields[35] == header.AuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == (start ? header.Command.AuthorizationTarget : actionTarget) &&
                fields[39] == commandKind.ToString() && fields[42] == header.SessionId.ToString("D") &&
                fields[9] == (start ? "CalibrationSessionStartAuthorized" : "CalibrationSessionActionAuthorized");
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException) { return false; }
    }

    /// <summary>Reads the dedicated schema-11 camera recovery authorization envelope.</summary>
    internal static bool TryReadCameraRecoveryAuthorization(byte[] payload, long ordinal,
        string stationId, out CameraRecoveryAuthorizationAudit binding)
    {
        binding = null!;
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, CameraRecoveryStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            if (fields.Length != 49 || !Enum.TryParse<IdentityEventKind>(fields[2], out var kind) ||
                kind is not (IdentityEventKind.CameraRecoveryCycleStartAuthorized or
                    IdentityEventKind.CameraRecoveryCycleStartCompleted or
                    IdentityEventKind.CameraRecoveryCycleStartFailed) ||
                !Guid.TryParseExact(fields[5], "D", out var principal) || principal == Guid.Empty ||
                !Guid.TryParseExact(fields[25], "D", out var session) || session == Guid.Empty ||
                !Guid.TryParseExact(fields[30], "D", out var actor) || actor == Guid.Empty ||
                !Guid.TryParseExact(fields[31], "D", out var correlation) || correlation == Guid.Empty ||
                !Guid.TryParseExact(fields[38], "D", out var boundCorrelation) || boundCorrelation == Guid.Empty ||
                !Guid.TryParseExact(fields[46], "D", out var expectedCycle) || expectedCycle == Guid.Empty ||
                !long.TryParse(fields[35], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
                revision < 0 || fields[33] != Permission.ManageCameraBindings.ToString() ||
                fields[37] is not { Length: > 0 } || !IsStableAsciiIdentifier(fields[37]) ||
                fields[47] != fields[37] || !IsStableAsciiIdentifier(fields[48]) ||
                fields[39] != AuditedCommandKind.StartCameraRecoveryCycle.ToString() ||
                !Enum.TryParse<AuditedCommandKind>(fields[39], out var commandKind) ||
                commandKind != AuditedCommandKind.StartCameraRecoveryCycle)
                return false;
            Guid? grant = null;
            if (fields[32] is not null)
            {
                if (!Guid.TryParseExact(fields[32], "D", out var parsedGrant) || parsedGrant == Guid.Empty)
                    return false;
                grant = parsedGrant;
            }
            binding = new CameraRecoveryAuthorizationAudit(kind, principal, actor, correlation, grant,
                fields[33]!, fields[37]!, boundCorrelation, commandKind, expectedCycle, session,
                revision, fields[9]!, fields[48]!);
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
    }

    /// <summary>
    /// Extracts the signed authorization envelope used by camera setup history
    /// validation.  The caller must still compare every value with the typed
    /// camera event and command fact; this is deliberately a narrow decoder rather
    /// than a second mutable identity projection.
    /// </summary>
    internal static bool TryReadCameraAuthorization(byte[] payload, long ordinal, string stationId,
        int schemaVersion, out CameraAuthorizationAudit binding)
    {
        binding = null!;
        try
        {
            if (schemaVersion < 10) return false;
            _ = VerifyPayload(payload, ordinal, stationId, schemaVersion);
            var fields = DecodeFields(payload);
            if (!Enum.TryParse<IdentityEventKind>(fields[2], out var kind) ||
                kind is not (IdentityEventKind.CameraSetupActionAuthorized or
                    IdentityEventKind.CameraSetupOperationCompleted) ||
                !Guid.TryParseExact(fields[5], "D", out var principal) || principal == Guid.Empty ||
                !Guid.TryParseExact(fields[25], "D", out var session) || session == Guid.Empty ||
                !Guid.TryParseExact(fields[30], "D", out var actor) || actor == Guid.Empty ||
                !Guid.TryParseExact(fields[31], "D", out var correlation) || correlation == Guid.Empty ||
                !Guid.TryParseExact(fields[38], "D", out var boundCorrelation) || boundCorrelation == Guid.Empty ||
                !Guid.TryParseExact(fields[42], "D", out var operation) || operation == Guid.Empty ||
                !long.TryParse(fields[35], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
                revision < 0 || fields[33] is null || fields[37] is null || fields[39] is null ||
                !Enum.TryParse<AuditedCommandKind>(fields[39], out var commandKind) ||
                commandKind is not (AuditedCommandKind.RebindCamera or
                    AuditedCommandKind.ApplyCameraDebugConfiguration))
                return false;
            Guid? grant = null;
            if (fields[32] is not null)
            {
                if (!Guid.TryParseExact(fields[32], "D", out var parsedGrant) || parsedGrant == Guid.Empty)
                    return false;
                grant = parsedGrant;
            }
            var requiredPermission = fields[33]!;
            var actionTarget = fields[37]!;
            binding = new CameraAuthorizationAudit(kind, principal, actor, correlation, grant,
                requiredPermission, actionTarget, boundCorrelation, commandKind, operation, session, revision,
                fields[9]!);
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
    }

    /// <summary>Reads the schema-13 imaging setup authorization envelope.</summary>
    internal static bool TryReadImagingSetupAuthorization(byte[] payload, long ordinal,
        string stationId, out CameraAuthorizationAudit binding)
    {
        binding = null!;
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, ImagingSetupStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            if (fields.Length != 49 || !Enum.TryParse<IdentityEventKind>(fields[2], out var kind) ||
                kind is not (IdentityEventKind.CameraSetupActionAuthorized or
                    IdentityEventKind.CameraSetupOperationCompleted) ||
                !Guid.TryParseExact(fields[5], "D", out var principal) || principal == Guid.Empty ||
                !Guid.TryParseExact(fields[25], "D", out var session) || session == Guid.Empty ||
                !Guid.TryParseExact(fields[30], "D", out var actor) || actor == Guid.Empty ||
                !Guid.TryParseExact(fields[31], "D", out var correlation) || correlation == Guid.Empty ||
                !Guid.TryParseExact(fields[38], "D", out var boundCorrelation) || boundCorrelation == Guid.Empty ||
                !Guid.TryParseExact(fields[42], "D", out var operation) || operation == Guid.Empty ||
                !long.TryParse(fields[35], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
                revision < 0 || fields[33] != Permission.ManageCameraBindings.ToString() ||
                fields[37] is not { Length: 64 } || !IsHash(fields[37]) ||
                fields[39] != AuditedCommandKind.DeclareImagingSetup.ToString() ||
                !Enum.TryParse<AuditedCommandKind>(fields[39], out var commandKind) ||
                commandKind != AuditedCommandKind.DeclareImagingSetup)
                return false;
            Guid? grant = null;
            if (fields[32] is not null)
            {
                if (!Guid.TryParseExact(fields[32], "D", out var parsedGrant) || parsedGrant == Guid.Empty)
                    return false;
                grant = parsedGrant;
            }
            binding = new CameraAuthorizationAudit(kind, principal, actor, correlation, grant,
                fields[33]!, fields[37]!, boundCorrelation, commandKind, operation, session,
                revision, fields[9]!);
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
    }

    /// <summary>Reads the schema-12 network authorization envelope without changing
    /// the schema-10/11 camera parser or its accepted command set.</summary>
    internal static bool TryReadCameraNetworkAuthorization(byte[] payload, long ordinal,
        string stationId, out CameraAuthorizationAudit binding)
    {
        binding = null!;
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, CameraNetworkStoreOptions.SchemaVersion);
            var fields = DecodeFields(payload);
            if (fields.Length != 49 || !Enum.TryParse<IdentityEventKind>(fields[2], out var kind) ||
                kind is not (IdentityEventKind.CameraSetupActionAuthorized or
                    IdentityEventKind.CameraSetupOperationCompleted) ||
                !Guid.TryParseExact(fields[5], "D", out var principal) || principal == Guid.Empty ||
                !Guid.TryParseExact(fields[25], "D", out var session) || session == Guid.Empty ||
                !Guid.TryParseExact(fields[30], "D", out var actor) || actor == Guid.Empty ||
                !Guid.TryParseExact(fields[31], "D", out var correlation) || correlation == Guid.Empty ||
                !Guid.TryParseExact(fields[38], "D", out var boundCorrelation) || boundCorrelation == Guid.Empty ||
                !Guid.TryParseExact(fields[42], "D", out var operation) || operation == Guid.Empty ||
                !long.TryParse(fields[35], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
                revision < 0 || fields[33] != Permission.ManageCameraBindings.ToString() ||
                fields[37] is not { Length: 64 } || !IsHash(fields[37]) ||
                fields[39] != AuditedCommandKind.ChangeCameraNetworkConfiguration.ToString())
                return false;
            Guid? grant = null;
            if (fields[32] is not null)
            {
                if (!Guid.TryParseExact(fields[32], "D", out var parsedGrant) || parsedGrant == Guid.Empty)
                    return false;
                grant = parsedGrant;
            }
            binding = new CameraAuthorizationAudit(kind, principal, actor, correlation, grant,
                fields[33]!, fields[37]!, boundCorrelation, AuditedCommandKind.ChangeCameraNetworkConfiguration,
                operation, session, revision, fields[9]!);
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
    }

    internal static bool MatchesProductionAdmission(byte[] payload, ProductionAdmissionHistoryEvent value,
        string stationId)
    {
        try
        {
            var fields = DecodeFields(payload);
            var expectedKind = value.Kind switch
            {
                ProductionAdmissionEventKind.Admitted => IdentityEventKind.ProductionAdmissionArmAuthorized,
                ProductionAdmissionEventKind.Rejected => IdentityEventKind.ManagementRejected,
                ProductionAdmissionEventKind.Completed => IdentityEventKind.ProductionAdmissionCompleted,
                _ => IdentityEventKind.ProductionAdmissionFailed
            };
            return fields.Length == 49 && fields[2] == expectedKind.ToString() && fields[4] == stationId &&
                fields[5] == value.ActorPrincipalId.ToString("D") && fields[9] == value.ReasonCode &&
                fields[25] == value.ActorSessionId.ToString("D") && fields[27] == value.AuthorizationPolicy.Id &&
                fields[28] == value.AuthorizationPolicy.Version && fields[29] == value.AuthorizationPolicy.ContentHash &&
                fields[30] == value.ActorPrincipalId.ToString("D") && fields[31] == value.CorrelationId.ToString("D") &&
                fields[32] == value.StepUpGrantId?.ToString("D") && fields[33] == Permission.ArmProduction.ToString() &&
                fields[35] == value.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture) &&
                fields[37] == stationId && fields[38] == value.CorrelationId.ToString("D") &&
                fields[39] == AuditedCommandKind.ArmProduction.ToString() &&
                fields[42] == (value.Kind == ProductionAdmissionEventKind.Rejected ? null : value.CorrelationId.ToString("D"));
        }
        catch (Exception exception) when (exception is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException) { return false; }
    }

    /// <summary>
    /// Creates the bounded v3 safety/context reference carried by the existing
    /// schema-28/29 identity envelope. It preserves the attempt's reason and
    /// safety observation context; immutable disposition text is referenced by
    /// hash so a retry cannot borrow another attempt's evidence.
    /// </summary>
    internal static string CreateProductionRecoverySafetyEvidence(
        Guid attemptId, Guid correlationId, Guid inspectionId, string expectedEventHash, string reasonCode,
        string partDisposition, string? dispositionNote, ProductionRecoverySafetyCapture capture)
    {
        if (attemptId == Guid.Empty)
            throw new ArgumentException("ProductionRecoveryAttemptRequired", nameof(attemptId));
        if (inspectionId == Guid.Empty)
            throw new ArgumentException("ProductionRecoveryInspectionRequired", nameof(inspectionId));
        if (correlationId == Guid.Empty)
            throw new ArgumentException("ProductionRecoveryCorrelationRequired", nameof(correlationId));
        if (!IsHash(expectedEventHash))
            throw new ArgumentException("ProductionRecoveryExpectedEventHashInvalid", nameof(expectedEventHash));
        _ = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        if (!IsSafeIdentifier(partDisposition))
            throw new ArgumentException("ProductionRecoveryPartDispositionInvalid", nameof(partDisposition));
        if (dispositionNote is { } note)
            _ = AlgorithmContractValidation.BoundedText(note, nameof(dispositionNote), 1024);
        ArgumentNullException.ThrowIfNull(capture);

        if (!Enum.TryParse<PartDisposition>(partDisposition, out var disposition) ||
            !Enum.IsDefined(disposition))
            throw new ArgumentException("ProductionRecoveryPartDispositionInvalid", nameof(partDisposition));
        if (!IsHash(capture.Source.ContentHash) || !IsSafeIdentifier(capture.ReasonCode))
            throw new ArgumentException("ProductionRecoverySafetyCaptureInvalid", nameof(capture));
        if (capture.Source.SourceEpoch == Guid.Empty || capture.Source.SourceGeneration < 1)
            throw new ArgumentException("ProductionRecoverySafetySourceInvalid", nameof(capture));

        var noteHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-disposition-note-v1", dispositionNote
        });
        var targetHash = ManualProductionRecoveryCommand.ComputeAuthorizationTarget(
            correlationId, inspectionId, expectedEventHash, reasonCode, disposition, dispositionNote);
        return EncodeProductionRecoverySafetyEvidence(new ProductionRecoverySafetyAuditEvidence(
            3, attemptId, inspectionId, expectedEventHash, reasonCode, disposition,
            targetHash, noteHash, capture.RuntimeEpoch, capture.Available, capture.ReasonCode,
            capture.Revision, capture.Source.ContentHash, capture.Source.SourceEpoch,
            capture.Source.SourceGeneration, capture.Source.Available,
            capture.Observation is null ? null : capture.Observation.Binding.ContentHash,
            capture.Observation?.Status, capture.Observation?.ReasonCode,
            capture.Observation?.SourceEpoch, capture.Observation?.SourceGeneration,
            capture.Observation?.ObservedAtUtc, capture.Observation?.MonotonicTimestamp,
            capture.Observation?.MonotonicFrequency));
    }

    internal static bool IsProductionRecoverySafetyEvidence(string? value) =>
        TryDecodeProductionRecoverySafetyEvidence(value, out _);

    internal static bool TryDecodeProductionRecoverySafetyEvidence(string? value,
        out ProductionRecoverySafetyAuditEvidence evidence)
    {
        evidence = null!;
        if (value is not { Length: > 3 and <= 2048 } ||
            !value.StartsWith("v3.", StringComparison.Ordinal)) return false;
        try
        {
            var encoded = value[3..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var raw = Convert.FromBase64String(encoded);
            var canonical = "v3." + Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (!string.Equals(value, canonical, StringComparison.Ordinal)) return false;
            using var input = new MemoryStream(raw, writable: false);
            using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
            if (reader.ReadByte() != 3) return false;
            var attempt = new Guid(reader.ReadBytes(16));
            var inspection = new Guid(reader.ReadBytes(16));
            if (attempt == Guid.Empty || inspection == Guid.Empty) return false;
            var expectedHash = ReadEvidenceHash(reader);
            var reason = ReadRecoveryEvidenceReason(reader);
            var dispositionValue = reader.ReadByte();
            if (!Enum.IsDefined(typeof(PartDisposition), dispositionValue)) return false;
            var disposition = (PartDisposition)dispositionValue;
            var targetHash = ReadEvidenceHash(reader);
            var noteHash = ReadEvidenceHash(reader);
            var runtimeEpoch = new Guid(reader.ReadBytes(16));
            if (runtimeEpoch == Guid.Empty) return false;
            var captureAvailable = reader.ReadBoolean();
            var captureReason = ReadEvidenceIdentifier(reader);
            var captureRevision = reader.ReadInt64();
            if (captureRevision < 0) return false;
            var sourceContentHash = ReadEvidenceHash(reader);
            var sourceEpoch = new Guid(reader.ReadBytes(16));
            var sourceGeneration = reader.ReadInt64();
            if (sourceEpoch == Guid.Empty || sourceGeneration < 1) return false;
            var sourceAvailable = reader.ReadBoolean();
            var hasObservation = reader.ReadBoolean();
            ProductionRecoverySafetyObservationStatus? status = null;
            string? observationReason = null;
            Guid? observationSourceEpoch = null;
            long? observationSourceGeneration = null;
            DateTimeOffset? observedAt = null;
            long? monotonicTimestamp = null;
            long? monotonicFrequency = null;
            string? bindingHash = null;
            if (hasObservation)
            {
                bindingHash = ReadEvidenceHash(reader);
                var statusValue = reader.ReadByte();
                if (!Enum.IsDefined(typeof(ProductionRecoverySafetyObservationStatus), statusValue))
                    return false;
                status = (ProductionRecoverySafetyObservationStatus)statusValue;
                observationReason = ReadEvidenceIdentifier(reader);
                var parsedEpoch = new Guid(reader.ReadBytes(16));
                var parsedGeneration = reader.ReadInt64();
                var observedTicks = reader.ReadInt64();
                var parsedTimestamp = reader.ReadInt64();
                var parsedFrequency = reader.ReadInt64();
                if (parsedEpoch == Guid.Empty || parsedGeneration < 1 || observedTicks <= 0 ||
                    parsedTimestamp <= 0 || parsedFrequency <= 0) return false;
                try { observedAt = new DateTimeOffset(new DateTime(observedTicks, DateTimeKind.Utc)); }
                catch (ArgumentOutOfRangeException) { return false; }
                observationSourceEpoch = parsedEpoch;
                observationSourceGeneration = parsedGeneration;
                monotonicTimestamp = parsedTimestamp;
                monotonicFrequency = parsedFrequency;
            }
            if (input.Position != input.Length) return false;
            evidence = new ProductionRecoverySafetyAuditEvidence(3, attempt, inspection,
                expectedHash, reason, disposition, targetHash, noteHash, runtimeEpoch,
                captureAvailable, captureReason, captureRevision, sourceContentHash,
                sourceEpoch, sourceGeneration, sourceAvailable, bindingHash, status,
                observationReason, observationSourceEpoch, observationSourceGeneration,
                observedAt, monotonicTimestamp, monotonicFrequency);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
            EndOfStreamException or DecoderFallbackException or FormatException or
            IOException or OverflowException or InvalidOperationException)
        { return false; }
    }

    private static string EncodeProductionRecoverySafetyEvidence(
        ProductionRecoverySafetyAuditEvidence value)
    {
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write((byte)value.Version);
            writer.Write(value.AttemptId.ToByteArray());
            writer.Write(value.InspectionId.ToByteArray());
            WriteEvidenceHash(writer, value.ExpectedEventHash);
            WriteRecoveryEvidenceReason(writer, value.ReasonCode);
            writer.Write((byte)value.Disposition);
            WriteEvidenceHash(writer, value.AuthorizationTarget);
            WriteEvidenceHash(writer, value.DispositionNoteHash);
            writer.Write(value.RuntimeEpoch.ToByteArray());
            writer.Write(value.CaptureAvailable);
            WriteEvidenceIdentifier(writer, value.CaptureReasonCode);
            writer.Write(value.CaptureRevision);
            WriteEvidenceHash(writer, value.SourceContentHash);
            writer.Write(value.SourceEpoch.ToByteArray());
            writer.Write(value.SourceGeneration);
            writer.Write(value.SourceAvailable);
            writer.Write(value.ObservationStatus is not null);
            if (value.ObservationStatus is { } status)
            {
                WriteEvidenceHash(writer, value.ObservationBindingHash!);
                writer.Write((byte)status);
                WriteEvidenceIdentifier(writer, value.ObservationReasonCode!);
                writer.Write(value.ObservationSourceEpoch!.Value.ToByteArray());
                writer.Write(value.ObservationSourceGeneration!.Value);
                writer.Write(value.ObservationObservedAtUtc!.Value.UtcDateTime.Ticks);
                writer.Write(value.ObservationMonotonicTimestamp!.Value);
                writer.Write(value.ObservationMonotonicFrequency!.Value);
            }
        }
        var bytes = output.ToArray();
        var encoded = "v3." + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (encoded.Length > 2048)
            throw new ArgumentException("ProductionRecoverySafetyEvidenceTooLarge");
        return encoded;
    }

    private static void WriteEvidenceHash(BinaryWriter writer, string value)
    {
        if (!IsHash(value)) throw new ArgumentException("ProductionRecoveryEvidenceHashInvalid");
        writer.Write(Convert.FromHexString(value));
    }

    private static string ReadEvidenceHash(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(32);
        if (bytes.Length != 32) throw new InvalidOperationException("ProductionRecoveryEvidenceHashInvalid");
        return Convert.ToHexString(bytes);
    }

    private static void WriteRecoveryEvidenceReason(BinaryWriter writer, string reason)
    {
        _ = RecipeActivationValidation.Reason(reason, nameof(reason));
        var bytes = new UTF8Encoding(false, true).GetBytes(reason);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadRecoveryEvidenceReason(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        if (length is 0 or > 1024)
            throw new InvalidOperationException("ProductionRecoveryEvidenceReasonInvalid");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new InvalidOperationException("ProductionRecoveryEvidenceReasonInvalid");
        var reason = new UTF8Encoding(false, true).GetString(bytes);
        return RecipeActivationValidation.Reason(reason, nameof(reason));
    }

    private static void WriteEvidenceIdentifier(BinaryWriter writer, string value)
    {
        if (!IsSafeIdentifier(value)) throw new ArgumentException("ProductionRecoveryEvidenceIdentifierInvalid");
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        if (bytes.Length > 256) throw new ArgumentException("ProductionRecoveryEvidenceIdentifierInvalid");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadEvidenceIdentifier(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        if (length is 0 or > 256) throw new InvalidOperationException("ProductionRecoveryEvidenceIdentifierInvalid");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidOperationException("ProductionRecoveryEvidenceIdentifierInvalid");
        var value = new UTF8Encoding(false, true).GetString(bytes);
        if (!IsSafeIdentifier(value)) throw new InvalidOperationException("ProductionRecoveryEvidenceIdentifierInvalid");
        return value;
    }

    /// <summary>
    /// Reads the authorization envelope used by all three production-recovery
    /// identity markers. Storage still compares the returned tuple with its
    /// command fact and immutable recovery projection; this method only validates
    /// the signed envelope and its schema-30 command binding.
    /// </summary>
    internal static bool TryReadProductionRecoveryAuthorization(byte[] payload, long ordinal,
        string stationId, out ProductionRecoveryAuthorizationAudit binding,
        int schemaVersion = ProductionRecoveryStoreOptions.SchemaVersion)
    {
        binding = null!;
        try
        {
            _ = VerifyPayload(payload, ordinal, stationId, schemaVersion);
            var fields = DecodeFields(payload);
            if (fields.Length != 49 || !Enum.TryParse<IdentityEventKind>(fields[2], out var kind) ||
                kind is not (IdentityEventKind.ProductionRecoveryAuthorized or
                    IdentityEventKind.ProductionRecoveryCompleted or
                    IdentityEventKind.ProductionRecoveryFailed) ||
                !Guid.TryParseExact(fields[5], "D", out var principal) || principal == Guid.Empty ||
                !Guid.TryParseExact(fields[25], "D", out var session) || session == Guid.Empty ||
                !Guid.TryParseExact(fields[30], "D", out var actor) || actor == Guid.Empty ||
                !Guid.TryParseExact(fields[31], "D", out var correlation) || correlation == Guid.Empty ||
                !Guid.TryParseExact(fields[38], "D", out var boundCorrelation) ||
                boundCorrelation != correlation ||
                !Guid.TryParseExact(fields[42], "D", out var inspectionId) || inspectionId == Guid.Empty ||
                !long.TryParse(fields[35], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var revision) || revision < 0 ||
                fields[33] != Permission.ManualRecovery.ToString() ||
                fields[37] is not { Length: > 0 } || !IsSafeIdentifier(fields[37]) ||
                fields[39] != AuditedCommandKind.ManualProductionRecovery.ToString() ||
                !IsProductionRecoverySafetyEvidence(fields[45]) || fields[34] is not null)
                return false;

            Guid? grant = null;
            if (fields[32] is not null)
            {
                if (!Guid.TryParseExact(fields[32], "D", out var parsedGrant) || parsedGrant == Guid.Empty)
                    return false;
                grant = parsedGrant;
            }

            binding = new ProductionRecoveryAuthorizationAudit(kind, principal, actor,
                correlation, grant, fields[33]!, fields[37]!, boundCorrelation,
                AuditedCommandKind.ManualProductionRecovery, inspectionId, session,
                revision, fields[9]!, fields[45]!, new RecipeContractReference(fields[27]!, fields[28]!, fields[29]!));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or FormatException or
            IndexOutOfRangeException)
        { return false; }
    }

    internal static bool TryReadEventKind(byte[] payload, out IdentityEventKind kind)
    {
        kind = default;
        try
        {
            var fields = DecodeFields(payload);
            return Enum.TryParse(fields[2], out kind) && Enum.IsDefined(kind);
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
    }

    private static string?[] DecodeFields(byte[] payload)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        var versionBytes = reader.ReadBytes(4);
        if (versionBytes.Length != 4 || BinaryPrimitives.ReadInt32BigEndian(versionBytes) !=
            AuditCanonical.CanonicalizationVersion)
            throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        var marker = reader.ReadByte();
        if (marker != 1) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        var labelLengthBytes = reader.ReadBytes(4);
        if (labelLengthBytes.Length != 4) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        var labelLength = BinaryPrimitives.ReadInt32BigEndian(labelLengthBytes);
        if (labelLength is < 0 or > 1024) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        var labelBytes = reader.ReadBytes(labelLength);
        if (labelBytes.Length != labelLength) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        if (new UTF8Encoding(false, true).GetString(labelBytes) != "IdentityEvent")
            throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        var countBytes = reader.ReadBytes(4);
        if (countBytes.Length != 4) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        var count = BinaryPrimitives.ReadInt32BigEndian(countBytes);
        if (count is not (46 or 49 or 50)) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        var fields = new string?[count];
        for (var index = 0; index < fields.Length; index++)
        {
            var valueMarker = reader.ReadByte();
            if (valueMarker == 0) continue;
            if (valueMarker != 1) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            var lengthBytes = reader.ReadBytes(4);
            if (lengthBytes.Length != 4) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            var maximumBytes = index switch
            {
                49 => PlcRecipeActivationIdentityCodec.MaximumEvidenceLength,
                45 when fields[2] is nameof(IdentityEventKind.ProductionRecoveryAuthorized) or
                        nameof(IdentityEventKind.ProductionRecoveryCompleted) or
                        nameof(IdentityEventKind.ProductionRecoveryFailed) => 2048,
                _ => 1024
            };
            if (length < 0 || length > maximumBytes) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            fields[index] = new UTF8Encoding(false, true).GetString(bytes);
        }
        if (input.Position != input.Length) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        return fields;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>
    /// The schema-31 PLC envelope is a closed shape. Only one of the existing
    /// activation facts or a rejected ActivateRecipe management fact may carry it,
    /// and that row must describe a true PLC Adapter system request: no human
    /// principal, session, Step-Up grant, or human permission, a neutral
    /// authorization revision that keeps the outer format compatible, the exact
    /// correlated command, and a valid bounded request context.
    /// </summary>
    private static bool IsPlcRecipeActivationEvidenceRow(string?[] fields)
    {
        if (fields.Length != 50 || fields[49] is null ||
            !Enum.TryParse<IdentityEventKind>(fields[2], out var kind) ||
            kind is not (IdentityEventKind.RecipeActivationAdmitted or
                IdentityEventKind.RecipeActivationCompleted or IdentityEventKind.RecipeActivationFailed or
                IdentityEventKind.RecipeActivationCancelled or IdentityEventKind.ManagementRejected) ||
            fields[39] != AuditedCommandKind.ActivateRecipe.ToString() ||
            fields[5] is not null || fields[25] is not null || fields[30] is not null ||
            fields[32] is not null || fields[33] is not null || fields[34] is not null || fields[35] != "0" ||
            fields[37] is null || !IsSafeIdentifier(fields[37]) ||
            fields[27] is null || fields[28] is null || fields[29] is null)
            return false;
        // A PLC request has no credential, Windows identity, authentication attempt,
        // recovery token or camera-recovery action. Deployment policy/state fields
        // remain part of the common identity ledger envelope.
        if (new[] { 6, 7, 8, 10, 11, 12, 13, 14, 21, 24, 36, 40, 41, 43, 44, 45, 46, 47, 48 }
                .Any(index => fields[index] is not null) ||
            new[] { 16, 17, 22, 23, 26 }.Any(index => fields[index] != "0"))
            return false;
        // The bound command correlation is the exact correlated action. Only an
        // accepted admission or completion must also carry the operation id.
        if (fields[38] is not { Length: 36 } || !Guid.TryParseExact(fields[38], "D", out _)) return false;
        if (kind is IdentityEventKind.RecipeActivationAdmitted or IdentityEventKind.RecipeActivationCompleted)
        {
            if (fields[31] != fields[38] || fields[42] != fields[38]) return false;
        }
        else if ((fields[31] is not null && fields[31] != fields[38]) ||
            (fields[42] is not null && fields[42] != fields[38]))
        {
            return false;
        }

        try { _ = PlcRecipeActivationIdentityCodec.Decode(fields[49]!); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
            FormatException)
        { return false; }
        return true;
    }

    private static bool IsSafeIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.Trim() == value && !value.Any(char.IsControl);

    private static bool IsStableAsciiIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
            (>= '0' and <= '9') or '.' or '_' or '-');

    private static bool IsPermissionSet(string? value, int maximumPermissions)
    {
        if (value is null or { Length: 0 }) return true;
        if (value.Length > 1024 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)) return false;
        var previous = 0;
        var seen = new HashSet<Permission>();
        var names = value.Split(',', StringSplitOptions.None);
        if (names.Length is < 1 || names.Length > maximumPermissions) return false;
        foreach (var name in names)
        {
            if (!Enum.TryParse<Permission>(name, out var permission) || !Enum.IsDefined(permission) ||
                permission == Permission.None || name != permission.ToString() || !seen.Add(permission) ||
                (int)permission <= previous)
                return false;
            previous = (int)permission;
        }

        return true;
    }
}

internal sealed record CameraAuthorizationAudit(
    IdentityEventKind Kind, Guid PrincipalId, Guid ActorPrincipalId, Guid CommandCorrelationId,
    Guid? StepUpGrantId, string RequiredPermission, string ActionTargetId,
    Guid BoundCommandCorrelationId, AuditedCommandKind CommandKind, Guid OperationId,
    Guid SessionId, long AuthorizationRevision, string ReasonCode);

internal sealed record CameraRecoveryAuthorizationAudit(
    IdentityEventKind Kind, Guid PrincipalId, Guid ActorPrincipalId, Guid CommandCorrelationId,
    Guid? StepUpGrantId, string RequiredPermission, string ActionTargetId,
    Guid BoundCommandCorrelationId, AuditedCommandKind CommandKind, Guid ExpectedCycleId,
    Guid SessionId, long AuthorizationRevision, string ReasonCode, string RecoveryReasonCode);

internal sealed record ProductionRecoveryAuthorizationAudit(
    IdentityEventKind Kind, Guid PrincipalId, Guid ActorPrincipalId, Guid CommandCorrelationId,
    Guid? StepUpGrantId, string RequiredPermission, string ActionTargetId,
    Guid BoundCommandCorrelationId, AuditedCommandKind CommandKind, Guid InspectionId,
    Guid SessionId, long AuthorizationRevision, string ReasonCode, string SafetyEvidence,
    RecipeContractReference AuthorizationPolicy);

internal sealed record ProductionRecoverySafetyAuditEvidence(
    int Version, Guid AttemptId, Guid InspectionId, string ExpectedEventHash,
    string ReasonCode, PartDisposition Disposition, string AuthorizationTarget,
    string DispositionNoteHash, Guid RuntimeEpoch, bool CaptureAvailable,
    string CaptureReasonCode, long CaptureRevision, string SourceContentHash,
    Guid SourceEpoch, long SourceGeneration, bool SourceAvailable,
    string? ObservationBindingHash,
    ProductionRecoverySafetyObservationStatus? ObservationStatus,
    string? ObservationReasonCode, Guid? ObservationSourceEpoch,
    long? ObservationSourceGeneration, DateTimeOffset? ObservationObservedAtUtc,
    long? ObservationMonotonicTimestamp, long? ObservationMonotonicFrequency);
