using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    // This admission waterline is checked before a transaction; SQLite may grow the
    // WAL beyond it while committing. It is deliberately not a physical size guarantee.
    private void RequireStorageRecoveryCapacity(sqlite3 database, int facts, StoreDeadline deadline)
    {
        var budget = _options.StorageRetention?.RecoveryBudget ??
            throw new InvalidOperationException("IdentityStorageRecoveryNotConfigured");
        RequireConfiguredRetention(database, _options, deadline);
        var bytes = ReadStorageRecoveryWalBytes();
        if (bytes >= budget.ControlWalAdmissionBytes)
            throw new InvalidOperationException("IdentityStorageRecoveryWalLimit");
        var used = ReadStorageRecoveryControlFacts(database, deadline);
        if (facts <= 0 || used > budget.MaximumControlFacts - facts)
            throw new InvalidOperationException("IdentityStorageRecoveryFactLimit");
    }

    internal static long ReadStorageRecoveryControlFacts(sqlite3 database, StoreDeadline deadline)
    {
        var activation = AuditChainDatabase.Read(database,
            "SELECT Sequence FROM audit_entries WHERE Kind='EvidenceRetentionActivated' LIMIT 2;",
            deadline, row => SqliteNative.ColumnInt64(row, 0));
        AuditChainDatabase.Require(activation.Count == 1, "AuditRetentionActivationMismatch");
        return AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind IN ('IdentityEvent','TraceStoragePolicyEvent') AND Sequence>" +
            activation[0].ToString(System.Globalization.CultureInfo.InvariantCulture) + ";", deadline);
    }

    private long ReadStorageRecoveryWalBytes()
    {
        try
        {
            var bytes = _readWalLength(_databasePath! + "-wal");
            if (bytes >= 0) return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        throw new InvalidOperationException("IdentityStorageRecoveryWalUnavailable");
    }

    private bool IsStorageRecoveryIdentityUpdate(IReadOnlyList<IdentityAuditEvent> events)
    {
        if (events.All(IsStorageRecoveryIdentityEvent)) return true;
        // The authentication engine disables a credential in the same transaction
        // as its final wrong-password failure. Dropping this pair would roll back
        // the failure counter and weaken the existing lockout policy under pressure.
        if (events.Count != 2 || events[0] is not { Kind: IdentityEventKind.CredentialDisabled,
                ReasonCode: "CredentialFailureLimitReached", PrincipalId: not null, CredentialId: not null } disabled ||
            events[1] is not { Kind: IdentityEventKind.AuthenticationRejected } rejected) return false;
        return disabled.PrincipalId == rejected.PrincipalId && disabled.CredentialId == rejected.CredentialId &&
            disabled.OccurredAtUtc == rejected.OccurredAtUtc && disabled.StateRevision == rejected.StateRevision &&
            !string.IsNullOrEmpty(disabled.ProtectedAttemptIdentifier) &&
            disabled.ProtectedAttemptIdentifier == rejected.ProtectedAttemptIdentifier &&
            disabled.AccountFailures == rejected.AccountFailures &&
            disabled.AccountFailures >= _options.LocalIdentity!.AuthenticationPolicy.AccountFailureLimit;
    }

    private static bool IsStorageRecoveryIdentityEvent(IdentityAuditEvent item) => item.Kind switch
    {
        IdentityEventKind.AuthenticationSucceeded or IdentityEventKind.AuthenticationRejected or
        IdentityEventKind.AuthenticationThrottled or IdentityEventKind.PasswordVerifierUpgraded or
        IdentityEventKind.SessionStarted or IdentityEventKind.SessionLocked or
        IdentityEventKind.SessionLoggedOut or IdentityEventKind.SessionSignInCancelled => true,
        IdentityEventKind.StepUpIssued or IdentityEventKind.StepUpRejected or IdentityEventKind.StepUpCancelled =>
            item.RequiredPermission == Permission.ManageProductionPolicy.ToString() &&
            item.ActionCommandKind == AuditedCommandKind.PublishTraceStoragePolicy.ToString() &&
            !string.IsNullOrWhiteSpace(item.ActionTargetId) &&
            item.BoundCommandCorrelationId is { } correlation && correlation != Guid.Empty,
        _ => false
    };

    private void RequireStorageRecoveryPlan(sqlite3 database, TraceStoragePolicyDefinition policy,
        StoreDeadline deadline)
    {
        if (_options.StorageRetention is not { } retention) return;
        var budget = retention.RecoveryBudget;
        AuditChainDatabase.Require(policy.MaximumWalBytes < budget.ControlWalAdmissionBytes,
            "TraceStoragePolicyRecoveryWalRangeRequired");
        var planned = checked(ReadStorageRecoveryWalBytes() + budget.CheckpointPlanningReserveBytes);
        var pageSize = AuditChainDatabase.Scalar(database, "PRAGMA page_size;", deadline);
        AuditChainDatabase.Require(pageSize is >= 512 and <= 65536,
            "TraceStoragePolicyRecoveryPageSizeInvalid");
        var frames = planned <= 32 ? 0 : checked((planned - 32 + pageSize + 23) / (pageSize + 24));
        AuditChainDatabase.Require(policy.Checkpoint.MaximumBytes >= planned,
            "TraceStoragePolicyRecoveryCheckpointBytesInsufficient");
        AuditChainDatabase.Require(policy.Checkpoint.MaximumItems >= frames,
            "TraceStoragePolicyRecoveryCheckpointFramesInsufficient");
    }
}
