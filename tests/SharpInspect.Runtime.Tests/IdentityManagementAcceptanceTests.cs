using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V106 management-boundary acceptance tests.  The fixture bootstraps through a controlled
/// physical-console authority and then exercises the public DI graph, so command acceptance
/// is observed through the same session, Step-Up, store and runtime seams as the host.
/// </summary>
public sealed class IdentityManagementAcceptanceTests
{
    [Fact]
    public async Task V106_M01_CreatePersonalAccountProducesCompletedAuditForAuthenticatedHuman()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var target = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var command = CreateCommand(fixture, actor, correlation, target, "V106 create account secret 2026!", HumanRoleBundle.Technician);
        var grant = await IssueGrantAsync(fixture, actor, command, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, target);
        var accepted = await SubmitAsync(fixture, WithGrant(command, grant));

        Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        Assert.Equal("ManagementCompleted", accepted.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, accepted.Audit);
        Assert.NotNull(accepted.AttemptId);

        var state = await ReadStateAsync(fixture);
        var account = Assert.Single(state.EnumerateAccounts(), account => account.PrincipalId == target);
        Assert.Equal("mgmt-m01", account.UserName);
        Assert.Equal(HumanRoleBundle.Technician, account.RoleBundle);
        Assert.True(account.Enabled);

        var facts = await FactsAsync(fixture, correlation);
        Assert.Equal(2, facts.Length);
        Assert.Equal(new[] { CommandAuditPhase.Outcome, CommandAuditPhase.Completed },
            facts.Select(fact => fact.Phase));
        Assert.All(facts, fact =>
        {
            Assert.Equal(accepted.AttemptId!.Value, fact.AttemptId);
            Assert.Equal(correlation, fact.CorrelationId);
            Assert.Equal(actor.PrincipalId.ToString("D"), fact.AuthenticatedHumanPrincipalId);
        });
    }

    [Fact]
    public async Task V106_M02_MissingGrantAndPermissionRejectWithoutCreatingState()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var administrator = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);

        var missingGrantTarget = Guid.NewGuid();
        var missingGrant = CreateCommand(fixture, administrator, Guid.NewGuid(), missingGrantTarget,
            "V106 missing grant account secret!", HumanRoleBundle.Operator);
        var noGrant = await SubmitAsync(fixture, missingGrant);
        Assert.Equal(CommandDisposition.Rejected, noGrant.Disposition);
        Assert.Equal("StepUpRequired", noGrant.ReasonCode);
        Assert.DoesNotContain((await ReadStateAsync(fixture)).EnumerateAccounts(),
            account => account.PrincipalId == missingGrantTarget);

        var operatorTarget = Guid.NewGuid();
        var operatorCreate = CreateCommand(fixture, administrator, Guid.NewGuid(), operatorTarget,
            "V106 operator account secret 2026!", HumanRoleBundle.Operator, "mgmt-m02-operator");
        var operatorGrant = await IssueGrantAsync(fixture, administrator, operatorCreate, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, operatorTarget);
        var operatorCreated = await SubmitAsync(fixture, WithGrant(operatorCreate, operatorGrant));
        Assert.Equal(CommandDisposition.Accepted, operatorCreated.Disposition);

        var manageAccountsOnly = new SetHumanPermissionsCommand(Guid.NewGuid(), Invocation(administrator), operatorTarget,
            new[] { Permission.ManageAccounts }, IdentityManagementReason.AccessChange);
        var permissionGrant = await IssueGrantAsync(fixture, administrator, manageAccountsOnly,
            fixture.BootstrapPassword, Permission.ManagePermissions,
            AuditedCommandKind.SetHumanPermissions, operatorTarget);
        var permissionChanged = await SubmitAsync(fixture, WithGrant(manageAccountsOnly, permissionGrant));
        Assert.Equal(CommandDisposition.Accepted, permissionChanged.Disposition);

        await LogoutAsync(fixture, administrator);
        var operatorSession = await SignInAsync(fixture, "mgmt-m02-operator", "V106 operator account secret 2026!");
        var permissionTarget = Guid.NewGuid();
        var permissionDenied = CreateCommand(fixture, operatorSession, Guid.NewGuid(), permissionTarget,
            "V106 permission denied account secret!", HumanRoleBundle.Administrator, "mgmt-m02-denied");
        var operatorGrantForCreate = await IssueGrantAsync(fixture, operatorSession, permissionDenied,
            "V106 operator account secret 2026!", Permission.ManageAccounts,
            AuditedCommandKind.CreateHumanAccount, permissionTarget);
        var denied = await SubmitAsync(fixture, WithGrant(permissionDenied, operatorGrantForCreate));
        Assert.Equal(CommandDisposition.Rejected, denied.Disposition);
        Assert.Equal("PermissionAssignmentDenied", denied.ReasonCode);
        var finalState = await ReadStateAsync(fixture);
        Assert.DoesNotContain(finalState.EnumerateAccounts(), account => account.PrincipalId == permissionTarget);
        Assert.Equal(2, finalState.EnumerateAccounts().Count());
    }

    [Fact]
    public async Task V106_M03_CreateAndDisableCannotReuseSamePermissionGrantForDifferentAction()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var target = Guid.NewGuid();
        var create = CreateCommand(fixture, actor, Guid.NewGuid(), target,
            "V106 action replacement account secret!", HumanRoleBundle.Operator);
        var createGrant = await IssueGrantAsync(fixture, actor, create, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, target);
        Assert.Equal(CommandDisposition.Accepted, (await SubmitAsync(fixture, WithGrant(create, createGrant))).Disposition);

        var replacementCorrelation = Guid.NewGuid();
        var createBindingCommand = CreateCommand(fixture, actor, replacementCorrelation, target,
            "V106 unused create password secret!", HumanRoleBundle.Operator);
        var replacementGrant = await IssueGrantAsync(fixture, actor, createBindingCommand, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, target);
        var disable = new DisableHumanCredentialCommand(replacementCorrelation,
            Invocation(actor, replacementGrant), target, IdentityManagementReason.PersonnelDeparture);
        var rejected = await SubmitAsync(fixture, disable);

        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("StepUpInvalid", rejected.ReasonCode);
        var account = (await ReadStateAsync(fixture)).EnumerateAccounts().Single(a => a.PrincipalId == target);
        Assert.True(account.Enabled);
    }

    [Fact]
    public async Task V106_M04_TargetAndCorrelationArePartOfTheStepUpBinding()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var existingTarget = Guid.NewGuid();
        var create = CreateCommand(fixture, actor, Guid.NewGuid(), existingTarget,
            "V106 binding target account secret!", HumanRoleBundle.Operator);
        var createGrant = await IssueGrantAsync(fixture, actor, create, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, existingTarget);
        Assert.Equal(CommandDisposition.Accepted, (await SubmitAsync(fixture, WithGrant(create, createGrant))).Disposition);

        var targetCorrelation = Guid.NewGuid();
        var targetBinding = new DisableHumanCredentialCommand(targetCorrelation, Invocation(actor), existingTarget,
            IdentityManagementReason.PersonnelDeparture);
        var targetGrant = await IssueGrantAsync(fixture, actor, targetBinding, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.DisableHumanCredential, existingTarget);
        var wrongTarget = new DisableHumanCredentialCommand(targetCorrelation, Invocation(actor), Guid.NewGuid(),
            IdentityManagementReason.PersonnelDeparture) with
        { Invocation = Invocation(actor, targetGrant) };
        var targetRejected = await SubmitAsync(fixture, wrongTarget);
        Assert.Equal(CommandDisposition.Rejected, targetRejected.Disposition);
        Assert.Equal("StepUpInvalid", targetRejected.ReasonCode);

        var correlation = Guid.NewGuid();
        var correlationBinding = new DisableHumanCredentialCommand(correlation, Invocation(actor), existingTarget,
            IdentityManagementReason.PersonnelDeparture);
        var correlationGrant = await IssueGrantAsync(fixture, actor, correlationBinding, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.DisableHumanCredential, existingTarget);
        var wrongCorrelation = new DisableHumanCredentialCommand(Guid.NewGuid(), Invocation(actor), existingTarget,
            IdentityManagementReason.PersonnelDeparture) with
        { Invocation = Invocation(actor, correlationGrant) };
        var correlationRejected = await SubmitAsync(fixture, wrongCorrelation);
        Assert.Equal(CommandDisposition.Rejected, correlationRejected.Disposition);
        Assert.Equal("StepUpInvalid", correlationRejected.ReasonCode);
        Assert.True((await ReadStateAsync(fixture)).EnumerateAccounts().Single(a => a.PrincipalId == existingTarget).Enabled);
    }

    [Fact]
    public async Task V106_M05_GrantIsSingleUseAndAcceptedCorrelationCannotBeReplayed()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var target = Guid.NewGuid();
        var command = CreateCommand(fixture, actor, Guid.NewGuid(), target,
            "V106 single use account secret!", HumanRoleBundle.Operator);
        var grant = await IssueGrantAsync(fixture, actor, command, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, target);
        Assert.Equal(CommandDisposition.Accepted, (await SubmitAsync(fixture, WithGrant(command, grant))).Disposition);

        var replay = await SubmitAsync(fixture, WithGrant(command, grant));
        Assert.Equal(CommandDisposition.Rejected, replay.Disposition);
        Assert.Equal("DuplicateCorrelationId", replay.ReasonCode);

        var secondTarget = Guid.NewGuid();
        var reused = CreateCommand(fixture, actor, Guid.NewGuid(), secondTarget,
            "V106 reused grant account secret!", HumanRoleBundle.Operator) with
        { Invocation = Invocation(actor, grant) };
        var reusedResult = await SubmitAsync(fixture, reused);
        Assert.Equal(CommandDisposition.Rejected, reusedResult.Disposition);
        Assert.Equal("StepUpInvalid", reusedResult.ReasonCode);
        Assert.DoesNotContain((await ReadStateAsync(fixture)).EnumerateAccounts(), account => account.PrincipalId == secondTarget);
    }

    [Fact]
    public async Task V106_M06_SessionLockInvalidatesPreviouslyIssuedGrant()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var target = Guid.NewGuid();
        var command = CreateCommand(fixture, actor, Guid.NewGuid(), target,
            "V106 locked session account secret!", HumanRoleBundle.Operator);
        var grant = await IssueGrantAsync(fixture, actor, command, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, target);
        var locked = await fixture.Sessions.LockAsync(actor.SessionId, SessionLockReason.UserRequested);
        Assert.True(locked.Succeeded, locked.ReasonCode);
        await fixture.WaitVerifiedAsync();
        Assert.Equal(InteractiveSessionState.Locked, fixture.Sessions.Current.State);

        var rejected = await SubmitAsync(fixture, WithGrant(command, grant));
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("SessionMismatch", rejected.ReasonCode);
        Assert.DoesNotContain((await ReadStateAsync(fixture)).EnumerateAccounts(), account => account.PrincipalId == target);
    }

    [Fact]
    public async Task V106_M07_DualAdministratorsAllowChangesButProtectTheLastUsableAdministrator()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var first = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var secondId = Guid.NewGuid();
        const string secondUserName = "mgmt-m07-second";
        const string secondPassword = "V106 second administrator secret!";
        var createSecond = CreateCommand(fixture, first, Guid.NewGuid(), secondId, secondPassword,
            HumanRoleBundle.Administrator, secondUserName);
        var createGrant = await IssueGrantAsync(fixture, first, createSecond, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, secondId);
        Assert.Equal(CommandDisposition.Accepted, (await SubmitAsync(fixture, WithGrant(createSecond, createGrant))).Disposition);

        var disableFirst = new DisableHumanCredentialCommand(Guid.NewGuid(), Invocation(first), first.PrincipalId,
            IdentityManagementReason.PersonnelDeparture);
        var disableFirstGrant = await IssueGrantAsync(fixture, first, disableFirst, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.DisableHumanCredential, first.PrincipalId);
        Assert.Equal(CommandDisposition.Accepted,
            (await SubmitAsync(fixture, WithGrant(disableFirst, disableFirstGrant))).Disposition);

        var second = await SignInAsync(fixture, secondUserName, secondPassword);
        var unlockFirst = new UnlockHumanCredentialCommand(Guid.NewGuid(), Invocation(second), first.PrincipalId,
            IdentityManagementReason.CredentialLockout);
        var unlockGrant = await IssueGrantAsync(fixture, second, unlockFirst, secondPassword,
            Permission.UnlockCredential, AuditedCommandKind.UnlockHumanCredential, first.PrincipalId);
        Assert.Equal(CommandDisposition.Accepted,
            (await SubmitAsync(fixture, WithGrant(unlockFirst, unlockGrant))).Disposition);

        var setSecondOperator = new SetHumanPermissionsCommand(Guid.NewGuid(), Invocation(second), second.PrincipalId,
            fixture.IdentityOptions.AuthorizationPolicy.GetPermissions(HumanRoleBundle.Operator),
            IdentityManagementReason.AccessChange);
        var setGrant = await IssueGrantAsync(fixture, second, setSecondOperator, secondPassword,
            Permission.ManagePermissions, AuditedCommandKind.SetHumanPermissions, second.PrincipalId);
        Assert.Equal(CommandDisposition.Accepted,
            (await SubmitAsync(fixture, WithGrant(setSecondOperator, setGrant))).Disposition);
        Assert.False(IdentityAuthorityState.IsUsableAdministrator(
            (await ReadStateAsync(fixture)).EnumerateAccounts().Single(account => account.PrincipalId == secondId)));

        await LogoutAsync(fixture, second);
        var firstAgain = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var disableSecond = new DisableHumanCredentialCommand(Guid.NewGuid(), Invocation(firstAgain), secondId,
            IdentityManagementReason.PersonnelDeparture);
        var disableSecondGrant = await IssueGrantAsync(fixture, firstAgain, disableSecond, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.DisableHumanCredential, secondId);
        Assert.Equal(CommandDisposition.Accepted,
            (await SubmitAsync(fixture, WithGrant(disableSecond, disableSecondGrant))).Disposition);

        var disableLast = new DisableHumanCredentialCommand(Guid.NewGuid(), Invocation(firstAgain), firstAgain.PrincipalId,
            IdentityManagementReason.PersonnelDeparture);
        var lastGrant = await IssueGrantAsync(fixture, firstAgain, disableLast, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.DisableHumanCredential, firstAgain.PrincipalId);
        var lastRejected = await SubmitAsync(fixture, WithGrant(disableLast, lastGrant));
        Assert.Equal(CommandDisposition.Rejected, lastRejected.Disposition);
        Assert.Equal("LastAdministratorRequired", lastRejected.ReasonCode);
        Assert.True((await ReadStateAsync(fixture)).EnumerateAccounts().Single(account => account.PrincipalId == firstAgain.PrincipalId).Enabled);
    }

    [Fact]
    public async Task V106_M08_OtherAdministratorCanUnlockAndRebindOldPasswordStopsWorking()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var first = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var secondId = Guid.NewGuid();
        const string secondUserName = "mgmt-m08-second";
        const string oldPassword = "V106 old administrator secret!";
        const string newPassword = "V106 rebound administrator secret!";
        var create = CreateCommand(fixture, first, Guid.NewGuid(), secondId, oldPassword,
            HumanRoleBundle.Administrator, secondUserName);
        var createGrant = await IssueGrantAsync(fixture, first, create, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, secondId);
        Assert.Equal(CommandDisposition.Accepted, (await SubmitAsync(fixture, WithGrant(create, createGrant))).Disposition);

        var disable = new DisableHumanCredentialCommand(Guid.NewGuid(), Invocation(first), secondId,
            IdentityManagementReason.PersonnelDeparture);
        var disableGrant = await IssueGrantAsync(fixture, first, disable, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.DisableHumanCredential, secondId);
        Assert.Equal(CommandDisposition.Accepted, (await SubmitAsync(fixture, WithGrant(disable, disableGrant))).Disposition);

        var disabledSignIn = await fixture.Sessions.SignInAsync(new PasswordSignInRequest(secondUserName, oldPassword));
        Assert.False(disabledSignIn.Succeeded);
        Assert.Equal("AuthenticationRejected", disabledSignIn.ReasonCode);
        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        var firstAfterDisabledAttempt = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);

        var unlock = new UnlockHumanCredentialCommand(Guid.NewGuid(), Invocation(firstAfterDisabledAttempt), secondId,
            IdentityManagementReason.CredentialLockout);
        var unlockGrant = await IssueGrantAsync(fixture, firstAfterDisabledAttempt, unlock, fixture.BootstrapPassword,
            Permission.UnlockCredential, AuditedCommandKind.UnlockHumanCredential, secondId);
        Assert.Equal(CommandDisposition.Accepted, (await SubmitAsync(fixture, WithGrant(unlock, unlockGrant))).Disposition);

        await LogoutAsync(fixture, firstAfterDisabledAttempt);
        var second = await SignInAsync(fixture, secondUserName, oldPassword);
        await LogoutAsync(fixture, second);
        var firstAgain = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var rebind = new RebindHumanCredentialCommand(Guid.NewGuid(), Invocation(firstAgain), secondId,
            newPassword, IdentityManagementReason.CredentialCompromise);
        var rebindGrant = await IssueGrantAsync(fixture, firstAgain, rebind, fixture.BootstrapPassword,
            Permission.RebindCredential, AuditedCommandKind.RebindHumanCredential, secondId);
        Assert.Equal(CommandDisposition.Accepted,
            (await SubmitAsync(fixture, WithGrant(rebind, rebindGrant))).Disposition);

        await LogoutAsync(fixture, firstAgain);
        var oldAfterRebind = await fixture.Sessions.SignInAsync(new PasswordSignInRequest(secondUserName, oldPassword));
        Assert.False(oldAfterRebind.Succeeded);
        Assert.Equal("AuthenticationRejected", oldAfterRebind.ReasonCode);
        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        var newAfterRebind = await SignInAsync(fixture, secondUserName, newPassword);
        Assert.Equal(secondId, newAfterRebind.PrincipalId);
    }

    [Fact]
    public async Task V106_M09_CommandFactsTriggerRollsBackAccountAuditAndLeavesGrantRetryable()
    {
        RequireWindows();
        await using var fixture = await IdentityManagementAcceptanceFixture.CreateAsync();
        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var target = Guid.NewGuid();
        var command = CreateCommand(fixture, actor, Guid.NewGuid(), target,
            "V106 trigger rollback account secret!", HumanRoleBundle.Operator);
        var grant = await IssueGrantAsync(fixture, actor, command, fixture.BootstrapPassword,
            Permission.ManageAccounts, AuditedCommandKind.CreateHumanAccount, target);
        var stateBefore = await ReadStateAsync(fixture);
        var auditBefore = CountRows(fixture.DatabasePath, "audit_entries");
        var factsBefore = CountRows(fixture.DatabasePath, "command_facts");
        var trigger = "v106_block_command_facts_" + Guid.NewGuid().ToString("N");
        CreateTrigger(fixture.DatabasePath, trigger);
        try
        {
            var failed = await SubmitAsync(fixture, WithGrant(command, grant));
            Assert.Equal(CommandDisposition.Rejected, failed.Disposition);
            Assert.Equal(AuditPersistence.Unavailable, failed.Audit);
            Assert.Equal("TraceAuditUnavailable", failed.ReasonCode);

            var afterRollback = await ReadStateAsync(fixture);
            Assert.Equal(stateBefore.Revision, afterRollback.Revision);
            Assert.DoesNotContain(afterRollback.EnumerateAccounts(), account => account.PrincipalId == target);
            Assert.Equal(auditBefore, CountRows(fixture.DatabasePath, "audit_entries"));
            Assert.Equal(factsBefore, CountRows(fixture.DatabasePath, "command_facts"));
        }
        finally
        {
            DropTrigger(fixture.DatabasePath, trigger);
        }

        await fixture.WaitVerifiedAsync();
        var retried = await SubmitAsync(fixture, WithGrant(command, grant));
        Assert.Equal(CommandDisposition.Accepted, retried.Disposition);
        Assert.Contains((await ReadStateAsync(fixture)).EnumerateAccounts(), account => account.PrincipalId == target);
        Assert.Equal(factsBefore + 2, CountRows(fixture.DatabasePath, "command_facts"));
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Identity management acceptance requires Windows DPAPI machine protection.");
    }

    private static async Task<SessionContext> SignInAsync(IdentityManagementAcceptanceFixture fixture,
        string userName, string password)
    {
        var result = await fixture.Sessions.SignInAsync(new PasswordSignInRequest(userName, password));
        // A committed self-disable clears the session before its bounded Logout evidence finishes.
        // Pending revocation explicitly admits no provider attempt; wait only for that reported state.
        using var pendingDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!result.Succeeded && result.ReasonCode == "SessionRevocationPending")
        {
            await Task.Delay(20, pendingDeadline.Token);
            result = await fixture.Sessions.SignInAsync(new PasswordSignInRequest(userName, password), pendingDeadline.Token);
        }
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.NotNull(result.Identity);
        Assert.True(result.Session.SessionId.HasValue);
        return new(result.Identity!.PrincipalId, result.Session.SessionId!.Value, userName, password);
    }

    private static async Task LogoutAsync(IdentityManagementAcceptanceFixture fixture, SessionContext session)
    {
        var result = await fixture.Sessions.LogoutAsync(session.SessionId);
        Assert.True(result.Succeeded, result.ReasonCode);
        await fixture.WaitVerifiedAsync();
    }

    private static CommandInvocation Invocation(SessionContext actor, Guid? grant = null) =>
        new(CommandSource.PhysicalConsole, actor.PrincipalId.ToString("D"), actor.SessionId, grant);

    private static CreateHumanAccountCommand CreateCommand(IdentityManagementAcceptanceFixture fixture,
        SessionContext actor, Guid correlation, Guid target, string password, HumanRoleBundle role,
        string userName = "mgmt-m01") =>
        new(correlation, Invocation(actor), target, userName,
            "V106 Managed " + userName, password, role);

    private static async Task<Guid> IssueGrantAsync(IdentityManagementAcceptanceFixture fixture,
        SessionContext actor, RuntimeCommand command, string password, Permission permission,
        AuditedCommandKind kind, Guid target)
    {
        var request = new StepUpRequest(command.CorrelationId, command.Invocation,
            new StepUpBinding(permission, command.CorrelationId, target.ToString("D"), kind), password);
        var result = await fixture.StepUp.ReauthenticateAsync(request);
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.True(result.GrantId.HasValue);
        Assert.Equal(actor.PrincipalId, Guid.ParseExact(command.Invocation.PrincipalId!, "D"));
        await fixture.WaitVerifiedAsync();
        return result.GrantId!.Value;
    }

    private static RuntimeCommand WithGrant(RuntimeCommand command, Guid grant) =>
        command with { Invocation = command.Invocation with { StepUpGrantId = grant } };

    private static async Task<RuntimeCommandOutcome> SubmitAsync(IdentityManagementAcceptanceFixture fixture,
        RuntimeCommand command)
    {
        var outcome = await fixture.Runtime.SubmitAsync(command);
        await fixture.WaitVerifiedAsync();
        return outcome;
    }

    private static async Task<IdentityAuthorityState> ReadStateAsync(IdentityManagementAcceptanceFixture fixture)
    {
        await fixture.WaitVerifiedAsync();
        return await fixture.Store.ReadIdentityAsync(CancellationToken.None);
    }

    private static async Task<CommandTraceRecord[]> FactsAsync(IdentityManagementAcceptanceFixture fixture,
        Guid correlation)
    {
        var page = await fixture.TraceQuery.QueryAsync(new CommandTraceFilter(CorrelationId: correlation));
        return page.Records.ToArray();
    }

    private static long CountRows(string databasePath, string table)
    {
        Assert.Contains(table, new[] { "audit_entries", "command_facts" });
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return (long)command.ExecuteScalar()!;
    }

    private static void CreateTrigger(string databasePath, string name)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER {name} BEFORE INSERT ON command_facts BEGIN SELECT RAISE(ABORT, 'V106CommandFactsBlocked'); END;";
        command.ExecuteNonQuery();
    }

    private static void DropTrigger(string databasePath, string name)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER IF EXISTS " + name + ";";
        command.ExecuteNonQuery();
    }

    private sealed record SessionContext(Guid PrincipalId, Guid SessionId, string UserName, string Password);
}

/// <summary>
/// Reusable internal V106 management fixture. It deliberately exposes the real DI instances
/// so focused Step-Up and runtime tests can share the same bootstrap and integrity contract.
/// </summary>
internal sealed class IdentityManagementAcceptanceFixture : IAsyncDisposable
{
    private readonly string _auditKeyPath;
    private ServiceProvider? _provider;

    private IdentityManagementAcceptanceFixture(string directoryPath, string databasePath,
        string stationId, string bootstrapUserName, string bootstrapPassword,
        AuditIntegrityPolicy auditPolicy, LocalIdentityOptions identityOptions,
        ProductionStoreOptions options)
    {
        DirectoryPath = directoryPath;
        DatabasePath = databasePath;
        StationId = stationId;
        BootstrapUserName = bootstrapUserName;
        BootstrapPassword = bootstrapPassword;
        AuditPolicy = auditPolicy;
        IdentityOptions = identityOptions;
        Options = options;
        _auditKeyPath = WindowsMachineAuditKey.GetKeyPath(auditPolicy);
    }

    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public string StationId { get; }
    public string BootstrapUserName { get; }
    public string BootstrapPassword { get; }
    public AuditIntegrityPolicy AuditPolicy { get; }
    public LocalIdentityOptions IdentityOptions { get; }
    public ProductionStoreOptions Options { get; }
    public SqliteCommandStore Store { get; private set; } = null!;
    public IIdentityProvider Identity { get; private set; } = null!;
    public IInteractiveSessionService Sessions { get; private set; } = null!;
    public IStepUpAuthentication StepUp { get; private set; } = null!;
    public LocalAuthorizationService Authorization { get; private set; } = null!;
    public IStationRuntime Runtime { get; private set; } = null!;
    public ICommandTraceQuery TraceQuery { get; private set; } = null!;
    public Guid BootstrapAdminPrincipalId { get; private set; }

    public static async Task<IdentityManagementAcceptanceFixture> CreateAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Identity management acceptance requires Windows DPAPI machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V106-IdentityManagement",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var station = "V106ManagementStation";
        var policy = new AuditIntegrityPolicy(station, "v1", "SharpInspect.Test.V106.Management." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "audit-keys"),
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identityOptions = new LocalIdentityOptions(station,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v106-management-blocklist", "v1",
                    new[] { "known-compromised-value" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development);
        var options = new ProductionStoreOptions(Path.Combine(directory, "identity.sqlite"))
        {
            AuditIntegrityPolicy = policy,
            LocalIdentity = identityOptions,
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 8
        };
        var fixture = new IdentityManagementAcceptanceFixture(directory, options.DatabasePath, station,
            "mgmt-bootstrap", "V106 bootstrap administrator secret!", policy, identityOptions, options);
        await fixture.InitializeAsync();
        return fixture;
    }

    private async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(Options, TimeSpan.FromMilliseconds(20));
        _provider = services.BuildServiceProvider();
        Store = _provider.GetRequiredService<SqliteCommandStore>();
        var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await WaitVerifiedAsync();

        // Bootstrap is the only operation bound to the test-controlled console. All later
        // authentication, sessions, Step-Up and management commands use the DI graph below.
        var bootstrapIdentity = new LocalIdentityService(Store, IdentityOptions, new ControlledConsoleAuthority());
        var tokenResult = await bootstrapIdentity.ProvisionBootstrapTokenAsync();
        Assert.True(tokenResult.Succeeded, tokenResult.ReasonCode);
        var token = tokenResult.Token!.TakeForDisplay();
        await WaitVerifiedAsync();
        var created = await bootstrapIdentity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
            StationId, token, BootstrapUserName, "V106 Bootstrap Administrator", BootstrapPassword));
        Assert.True(created.Succeeded, created.ReasonCode);
        BootstrapAdminPrincipalId = created.Identity!.PrincipalId;
        created.RecoveryKit?.Dispose();
        await WaitVerifiedAsync();

        Identity = _provider.GetRequiredService<IIdentityProvider>();
        Sessions = _provider.GetRequiredService<IInteractiveSessionService>();
        StepUp = _provider.GetRequiredService<IStepUpAuthentication>();
        Authorization = _provider.GetRequiredService<LocalAuthorizationService>();
        Runtime = _provider.GetRequiredService<IStationRuntime>();
        TraceQuery = _provider.GetRequiredService<ICommandTraceQuery>();
    }

    public string PasswordFor(string suffix) => "V106 fixture " + suffix + " managed secret!";

    public async Task<AuditIntegrityReport> WaitVerifiedAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            var report = Store.Integrity;
            if (report is { State: AuditIntegrityState.Verified }) return report;
            if (report?.State == AuditIntegrityState.Faulted)
                throw new XunitException($"Audit integrity faulted: {report.ReasonCode}");
            await Task.Delay(25);
        }

        throw new XunitException($"Audit integrity did not become Verified. Last state: {Store.Integrity?.State}, " +
            $"reason: {Store.Integrity?.ReasonCode}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        try
        {
            if (File.Exists(_auditKeyPath)) File.Delete(_auditKeyPath);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private sealed class ControlledConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V106-MANAGEMENT-TEST");
    }
}

#pragma warning restore CA1416
