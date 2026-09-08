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
    CameraRecoveryCycleStartFailed
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
    string? CameraRecoveryReasonCode = null)
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

        if (schemaVersion is < 3 or > CameraNetworkStoreOptions.SchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        return AuditCanonical.Encode("IdentityEvent", fields.ToArray());
    }

    internal static long VerifyPayload(byte[] payload, long ordinal, string stationId, int schemaVersion = 6)
    {
        if (schemaVersion is < 3 or > CameraNetworkStoreOptions.SchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));

        using var input = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        int ReadInteger()
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            return BinaryPrimitives.ReadInt32BigEndian(bytes);
        }
        string? ReadValue()
        {
            var marker = reader.ReadByte();
            if (marker == 0) return null;
            AuditChainDatabase.Require(marker == 1, "AuditIdentityPayloadInvalid");
            var length = ReadInteger();
            AuditChainDatabase.Require(length is >= 0 and <= 1024, "AuditIdentityPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            AuditChainDatabase.Require(bytes.Length == length, "AuditIdentityPayloadInvalid");
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        try
        {
            var expectedCount = schemaVersion switch { 3 => 18, 4 => 27, 5 => 42, 6 or 7 or 8 or 9 or 10 => 46, 11 or 12 => 49, _ => 0 };
            AuditChainDatabase.Require(ReadInteger() == AuditCanonical.CanonicalizationVersion &&
                ReadValue() == "IdentityEvent" && ReadInteger() == expectedCount,
                "AuditIdentityPayloadInvalid");
            var fields = Enumerable.Range(0, expectedCount).Select(_ => ReadValue()).ToArray();
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
                     (schemaVersion >= 9 || actionKind != AuditedCommandKind.SaveRecipeDraft) &&
                     (schemaVersion >= 10 || (actionKind != AuditedCommandKind.RebindCamera &&
                        actionKind != AuditedCommandKind.ApplyCameraDebugConfiguration)) &&
                    (schemaVersion >= CameraRecoveryStoreOptions.SchemaVersion ||
                        actionKind != AuditedCommandKind.StartCameraRecoveryCycle) &&
                    (schemaVersion >= CameraNetworkStoreOptions.SchemaVersion ||
                        actionKind != AuditedCommandKind.ChangeCameraNetworkConfiguration) &&
                     fields[39] == actionKind.ToString()),
                    "AuditAuthorizationPayloadInvalid");
                // Permission 31 is part of the current default role bundle even
                // for identity-only/alarm schema 7/8 stores. It is a capability
                // carried by the signed permission list; the draft mutation/event
                // itself remains schema-9 gated below and in the store dispatcher.
                var maximumPermissions = schemaVersion >= 7 ? 31 : 28;
                AuditChainDatabase.Require(IsPermissionSet(fields[40], maximumPermissions) &&
                    IsPermissionSet(fields[41], maximumPermissions),
                    "AuditAuthorizationPayloadInvalid");
            }

            if (schemaVersion >= 6)
            {
                foreach (var index in new[] { 42, 43, 44 })
                    AuditChainDatabase.Require(fields[index] is null ||
                        Guid.TryParseExact(fields[index], "D", out _), "AuditRecoveryPayloadInvalid");
                AuditChainDatabase.Require(fields[45] is null || IsSafeIdentifier(fields[45]),
                    "AuditRecoveryPayloadInvalid");
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
        Guid? stepUpGrantId)
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
                fields[37] == draftId.ToString("D") && fields[38] == operationId.ToString("D") &&
                fields[39] == AuditedCommandKind.SaveRecipeDraft.ToString() &&
                fields[9] == "RecipeDraftAuthorized";
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or
            InvalidOperationException or FormatException)
        { return false; }
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
        if (count is not (46 or 49)) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        var fields = new string?[count];
        for (var index = 0; index < fields.Length; index++)
        {
            var valueMarker = reader.ReadByte();
            if (valueMarker == 0) continue;
            if (valueMarker != 1) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            var lengthBytes = reader.ReadBytes(4);
            if (lengthBytes.Length != 4) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 0 or > 1024) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            fields[index] = new UTF8Encoding(false, true).GetString(bytes);
        }
        if (input.Position != input.Length) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        return fields;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

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
