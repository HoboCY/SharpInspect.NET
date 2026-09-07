using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SharpInspect.Abstractions;

/// <summary>An exact intended action. A grant is not transferable to another command or target.</summary>
public sealed record StepUpBinding(Permission Permission, Guid CommandCorrelationId, string TargetId,
    AuditedCommandKind CommandKind);

public sealed class StepUpRequest
{
    public StepUpRequest(Guid correlationId, CommandInvocation invocation, StepUpBinding binding, string password)
    { CorrelationId = correlationId; Invocation = invocation; Binding = binding; Password = password; }
    public Guid CorrelationId { get; }
    public CommandInvocation Invocation { get; }
    public StepUpBinding Binding { get; }
    [JsonIgnore] public string Password { get; }
    public override string ToString() => nameof(StepUpRequest);
}

public sealed record StepUpResult(bool Succeeded, string ReasonCode, Guid? GrantId = null, DateTimeOffset? ExpiresAtUtc = null);

public interface IStepUpAuthentication
{
    ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request, CancellationToken cancellationToken = default);
}

public enum IdentityManagementReason { PersonnelOnboarding, AccessChange, PersonnelDeparture, CredentialLockout, CredentialCompromise }

/// <summary>All mutations are handled by IStationRuntime and recheck current authority.</summary>
public abstract record IdentityManagementCommand(Guid CorrelationId, CommandInvocation Invocation,
    Guid TargetPrincipalId, IdentityManagementReason Reason) : RuntimeCommand(CorrelationId, Invocation);

public sealed record CreateHumanAccountCommand : IdentityManagementCommand
{
    public CreateHumanAccountCommand(Guid correlationId, CommandInvocation invocation, Guid targetPrincipalId,
        string userName, string displayName, string password, HumanRoleBundle roleBundle)
        : base(correlationId, invocation, targetPrincipalId, IdentityManagementReason.PersonnelOnboarding)
    { UserName = userName; DisplayName = displayName; Password = password; RoleBundle = roleBundle; }
    public string UserName { get; }
    public string DisplayName { get; }
    [JsonIgnore] public string Password { get; }
    public HumanRoleBundle RoleBundle { get; }
    public override string ToString() => nameof(CreateHumanAccountCommand);
}

public sealed record DisableHumanCredentialCommand(Guid CorrelationId, CommandInvocation Invocation,
    Guid TargetPrincipalId, IdentityManagementReason Reason)
    : IdentityManagementCommand(CorrelationId, Invocation, TargetPrincipalId, Reason);

public sealed record UnlockHumanCredentialCommand(Guid CorrelationId, CommandInvocation Invocation,
    Guid TargetPrincipalId, IdentityManagementReason Reason)
    : IdentityManagementCommand(CorrelationId, Invocation, TargetPrincipalId, Reason);

public sealed record RebindHumanCredentialCommand : IdentityManagementCommand
{
    public RebindHumanCredentialCommand(Guid correlationId, CommandInvocation invocation, Guid targetPrincipalId,
        string newPassword, IdentityManagementReason reason)
        : base(correlationId, invocation, targetPrincipalId, reason) => NewPassword = newPassword;
    [JsonIgnore] public string NewPassword { get; }
    public override string ToString() => nameof(RebindHumanCredentialCommand);
}

public sealed record SetHumanPermissionsCommand : IdentityManagementCommand
{
    public SetHumanPermissionsCommand(Guid correlationId, CommandInvocation invocation, Guid targetPrincipalId,
        IEnumerable<Permission> permissions, IdentityManagementReason reason = IdentityManagementReason.AccessChange)
        : base(correlationId, invocation, targetPrincipalId, reason)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var bounded = permissions.Take(65).ToArray();
        if (bounded.Length > 64) throw new ArgumentException("PermissionSetCapacityExceeded", nameof(permissions));
        Permissions = new ReadOnlyCollection<Permission>(bounded);
    }
    public IReadOnlyList<Permission> Permissions { get; }
}

public sealed class HumanAccountSummary
{
    public HumanAccountSummary(Guid principalId, string userName, string displayName, bool credentialEnabled,
        long authorizationRevision, IEnumerable<Permission> permissions, HumanRoleBundle? roleBundle = null)
    {
        PrincipalId = principalId; UserName = userName; DisplayName = displayName; CredentialEnabled = credentialEnabled;
        AuthorizationRevision = authorizationRevision; RoleBundle = roleBundle;
        Permissions = new ReadOnlyCollection<Permission>(permissions.ToArray());
    }
    public Guid PrincipalId { get; }
    public string UserName { get; }
    public string DisplayName { get; }
    public bool CredentialEnabled { get; }
    public long AuthorizationRevision { get; }
    public HumanRoleBundle? RoleBundle { get; }
    public IReadOnlyList<Permission> Permissions { get; }
}

public sealed record HumanAuthorizationSnapshot(bool Available, string ReasonCode, Guid? SessionId,
    HumanAccountSummary? Account);

public sealed class HumanDirectorySnapshot
{
    public HumanDirectorySnapshot(bool available, string reasonCode, IEnumerable<HumanAccountSummary>? accounts = null)
    { Available = available; ReasonCode = reasonCode;
        Accounts = new ReadOnlyCollection<HumanAccountSummary>((accounts ?? Array.Empty<HumanAccountSummary>()).ToArray()); }
    public bool Available { get; }
    public string ReasonCode { get; }
    public IReadOnlyList<HumanAccountSummary> Accounts { get; }
}

/// <summary>Read-only account views. Possessing a query result never authorizes a later mutation.</summary>
public interface IIdentityAdministrationQuery
{
    ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId, CancellationToken cancellationToken = default);
    ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation, CancellationToken cancellationToken = default);
}
