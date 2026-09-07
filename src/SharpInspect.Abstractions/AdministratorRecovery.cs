using System.Text.Json.Serialization;

namespace SharpInspect.Abstractions;

public enum RecoveryKitState { Unavailable, Available, RotationRequired, CustodyConfirmationRequired }

/// <summary>A projection only. Runtime rechecks physical, operational and identity authority on every mutation.</summary>
public sealed record AdministratorRecoveryStatus(string StationId, bool RecoveryAvailable,
    string ReasonCode, RecoveryKitState KitState, Guid? KitId, Guid? RecoveredPrincipalId,
    int UsableAdministratorCount, int ValidRecoveryCodeCount, bool ProductionIdentityPrerequisitesMet);

public sealed class RecoverAdministratorRequest
{
    public RecoverAdministratorRequest(Guid operationId, string stationId, string recoveryCode,
        string userName, string displayName, string newPassword)
    { OperationId = operationId; StationId = stationId; RecoveryCode = recoveryCode;
        UserName = userName; DisplayName = displayName; NewPassword = newPassword; }
    public Guid OperationId { get; }
    public string StationId { get; }
    [JsonIgnore] public string RecoveryCode { get; }
    public string UserName { get; }
    public string DisplayName { get; }
    [JsonIgnore] public string NewPassword { get; }
    public override string ToString() => nameof(RecoverAdministratorRequest);
}

/// <summary>The current administrator reauthenticates for this exact one-shot rotation operation.</summary>
public sealed class RotateRecoveryKitRequest
{
    public RotateRecoveryKitRequest(Guid operationId, string stationId, CommandInvocation invocation, string password)
    { OperationId = operationId; StationId = stationId; Invocation = invocation; Password = password; }
    public Guid OperationId { get; }
    public string StationId { get; }
    public CommandInvocation Invocation { get; }
    [JsonIgnore] public string Password { get; }
    public override string ToString() => nameof(RotateRecoveryKitRequest);
}

/// <summary>Proof of having received the new kit. The presented code is consumed on confirmation.</summary>
public sealed class ConfirmRecoveryKitCustodyRequest
{
    public ConfirmRecoveryKitCustodyRequest(Guid operationId, string stationId, Guid kitId,
        CommandInvocation invocation, string confirmationCode)
    { OperationId = operationId; StationId = stationId; KitId = kitId;
        Invocation = invocation; ConfirmationCode = confirmationCode; }
    public Guid OperationId { get; }
    public string StationId { get; }
    public Guid KitId { get; }
    public CommandInvocation Invocation { get; }
    [JsonIgnore] public string ConfirmationCode { get; }
    public override string ToString() => nameof(ConfirmRecoveryKitCustodyRequest);
}

public sealed record AdministratorRecoveryResult(bool Succeeded, string ReasonCode,
    Guid OperationId, AuditPersistence Audit, HumanIdentity? Identity = null);

public sealed record RecoveryKitRotationResult(bool Succeeded, string ReasonCode,
    Guid OperationId, AuditPersistence Audit, Guid? KitId = null,
    [property: JsonIgnore] OneTimeSecret? RecoveryKit = null);

public interface ILocalAdministratorRecovery
{
    ValueTask<AdministratorRecoveryStatus> GetRecoveryStatusAsync(CancellationToken cancellationToken = default);
    ValueTask<AdministratorRecoveryResult> RecoverAdministratorAsync(RecoverAdministratorRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<RecoveryKitRotationResult> RotateRecoveryKitAsync(RotateRecoveryKitRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<AdministratorRecoveryResult> ConfirmRecoveryKitCustodyAsync(ConfirmRecoveryKitCustodyRequest request,
        CancellationToken cancellationToken = default);
}
