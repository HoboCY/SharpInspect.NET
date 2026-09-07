#pragma warning disable CA1416

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V108 acceptance evidence for the administrator recovery authority.  These tests use the
/// real signed SQLite store, LocalIdentityService, and InteractiveSessionService; only the
/// physical-console and runtime safety observations are controlled test seams.
/// </summary>
public sealed class AdministratorRecoveryAcceptanceTests
{
    [Fact]
    public async Task V108_A01_ExistingAdministratorCannotUseRecoveryCode()
    {
        RequireWindows();
        await using var harness = await RecoveryHarness.CreateAsync(disableInitialAdministrator: false);

        var status = await harness.Recovery.GetRecoveryStatusAsync();
        Assert.False(status.RecoveryAvailable);
        Assert.Equal("UsableAdministratorPresent", status.ReasonCode);
        var before = await ReadFunctionalStateAsync(harness.Store);

        var result = await harness.Recovery.RecoverAdministratorAsync(new RecoverAdministratorRequest(
            Guid.NewGuid(), harness.Context.StationId, harness.Bootstrap.Codes[0],
            harness.Context.UserName, harness.Context.DisplayName, harness.Context.NewPassword));

        Assert.False(result.Succeeded);
        Assert.Equal("UsableAdministratorPresent", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Equal(before, await ReadFunctionalStateAsync(harness.Store));
    }

    [Fact]
    public async Task V108_A02_DisabledAdministratorRecoversSamePrincipalAndRecoverEventHasNoActor()
    {
        RequireWindows();
        await using var harness = await RecoveryHarness.CreateAsync();
        var principal = harness.Bootstrap.PrincipalId;

        var result = await harness.Recovery.RecoverAdministratorAsync(new RecoverAdministratorRequest(
            Guid.NewGuid(), harness.Context.StationId, harness.Bootstrap.Codes[0],
            harness.Context.UserName, harness.Context.DisplayName, harness.Context.NewPassword));

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal(principal, result.Identity!.PrincipalId);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        await WaitForVerifiedAsync(harness.Store);

        var status = await harness.Recovery.GetRecoveryStatusAsync();
        Assert.Equal(RecoveryKitState.RotationRequired, status.KitState);
        Assert.Equal(principal, status.RecoveredPrincipalId);
        Assert.False(status.ProductionIdentityPrerequisitesMet);
        Assert.Equal(0, status.ValidRecoveryCodeCount);

        var oldPassword = await harness.Identity.AuthenticateAsync(
            new PasswordSignInRequest(harness.Context.UserName, harness.Context.Password));
        Assert.False(oldPassword.Succeeded);
        harness.Context.Advance(TimeSpan.FromSeconds(2));
        var newPassword = await harness.Identity.AuthenticateAsync(
            new PasswordSignInRequest(harness.Context.UserName, harness.Context.NewPassword));
        Assert.True(newPassword.Succeeded, newPassword.ReasonCode);
        Assert.Equal(principal, newPassword.Identity!.PrincipalId);

        var recovered = ReadIdentityEvents(harness.Context.DatabasePath)
            .Last(fields => fields[2] == "AdministratorRecovered" &&
                fields[42] == result.OperationId.ToString("D"));
        Assert.Equal(principal.ToString("D"), recovered[5]);
        Assert.Null(recovered[30]);
        Assert.Equal(principal.ToString("D"), recovered[34]);
    }

    [Fact]
    public async Task V108_A03_RecoveryCodeIsConsumedAndStationBound()
    {
        RequireWindows();
        await using var harness = await RecoveryHarness.CreateAsync();
        var code = harness.Bootstrap.Codes[0];
        var recovered = await RecoverAsync(harness, code);
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        var afterRecovery = await ReadFunctionalStateAsync(harness.Store);

        var wrongStation = await harness.Recovery.RecoverAdministratorAsync(new RecoverAdministratorRequest(
            Guid.NewGuid(), "OtherStation", code, "other-person", "Other Person", harness.Context.NewPassword));
        Assert.False(wrongStation.Succeeded);
        Assert.Equal("RecoveryStationMismatch", wrongStation.ReasonCode);

        var duplicate = await harness.Recovery.RecoverAdministratorAsync(new RecoverAdministratorRequest(
            Guid.NewGuid(), harness.Context.StationId, code, "other-person", "Other Person", harness.Context.NewPassword));
        Assert.False(duplicate.Succeeded);
        // The usable recovered administrator check is deliberately evaluated before the
        // revoked-code/workflow check.  The stable security property is no rebind, not a
        // particular ordering of those two rejection reasons.
        Assert.Equal("UsableAdministratorPresent", duplicate.ReasonCode);
        Assert.Equal(afterRecovery, await ReadFunctionalStateAsync(harness.Store));
    }

    [Fact]
    public async Task V108_A04_RotationRequiresOwnerSessionAndReauthenticationAndAttributesActor()
    {
        RequireWindows();
        await using var harness = await RecoveryHarness.CreateAsync();
        var recovered = await RecoverAsync(harness, harness.Bootstrap.Codes[0]);
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        harness.Context.Advance(TimeSpan.FromSeconds(2));

        var signIn = await harness.Sessions.SignInAsync(new(
            harness.Context.UserName, harness.Context.NewPassword));
        Assert.True(signIn.Succeeded, signIn.ReasonCode);
        var sessionId = signIn.Session.SessionId!.Value;
        var principal = signIn.Identity!.PrincipalId;
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            principal.ToString("D"), sessionId);
        var before = await ReadFunctionalStateAsync(harness.Store);

        var forgedSession = await harness.Recovery.RotateRecoveryKitAsync(new RotateRecoveryKitRequest(
            Guid.NewGuid(), harness.Context.StationId,
            invocation with { SessionId = Guid.NewGuid() }, harness.Context.NewPassword));
        Assert.False(forgedSession.Succeeded);
        Assert.Equal("RecoverySessionMismatch", forgedSession.ReasonCode);

        harness.Context.Advance(TimeSpan.FromSeconds(2));
        var wrongPassword = await harness.Recovery.RotateRecoveryKitAsync(new RotateRecoveryKitRequest(
            Guid.NewGuid(), harness.Context.StationId, invocation, harness.Context.Password));
        Assert.False(wrongPassword.Succeeded);
        Assert.Equal("ReauthenticationRejected", wrongPassword.ReasonCode);
        Assert.Equal(before, await ReadFunctionalStateAsync(harness.Store));

        harness.Context.Advance(TimeSpan.FromSeconds(2));
        var rotated = await harness.Recovery.RotateRecoveryKitAsync(new RotateRecoveryKitRequest(
            Guid.NewGuid(), harness.Context.StationId, invocation, harness.Context.NewPassword));
        Assert.True(rotated.Succeeded, rotated.ReasonCode);
        Assert.NotNull(rotated.RecoveryKit);
        var delivery = rotated.RecoveryKit!.TakeForDisplay();
        Assert.Throws<InvalidOperationException>(() => rotated.RecoveryKit.TakeForDisplay());
        rotated.RecoveryKit.Dispose();
        Assert.Equal(rotated.KitId, ParseKitId(delivery));

        var rotation = ReadIdentityEvents(harness.Context.DatabasePath)
            .Last(fields => fields[2] == "RecoveryKitRotated" &&
                fields[42] == rotated.OperationId.ToString("D"));
        Assert.Equal(principal.ToString("D"), rotation[5]);
        Assert.Equal(principal.ToString("D"), rotation[30]);
        Assert.Equal(principal.ToString("D"), rotation[34]);
        Assert.Equal(sessionId.ToString("D"), rotation[25]);
    }

    [Fact]
    public async Task V108_A05_RotationOperationCannotExportAnotherKit()
    {
        RequireWindows();
        await using var harness = await RecoveryHarness.CreateAsync();
        var recovered = await RecoverAsync(harness, harness.Bootstrap.Codes[0]);
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        harness.Context.Advance(TimeSpan.FromSeconds(2));
        var signIn = await harness.Sessions.SignInAsync(new(
            harness.Context.UserName, harness.Context.NewPassword));
        Assert.True(signIn.Succeeded, signIn.ReasonCode);
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            signIn.Identity!.PrincipalId.ToString("D"), signIn.Session.SessionId);
        var operationId = Guid.NewGuid();

        var first = await harness.Recovery.RotateRecoveryKitAsync(new RotateRecoveryKitRequest(
            operationId, harness.Context.StationId, invocation, harness.Context.NewPassword));
        Assert.True(first.Succeeded, first.ReasonCode);
        var firstKit = first.RecoveryKit!.TakeForDisplay();
        first.RecoveryKit.Dispose();

        var retry = await harness.Recovery.RotateRecoveryKitAsync(new RotateRecoveryKitRequest(
            operationId, harness.Context.StationId, invocation, harness.Context.NewPassword));
        Assert.False(retry.Succeeded);
        Assert.Equal("OperationAlreadyCompleted", retry.ReasonCode);
        Assert.Null(retry.RecoveryKit);
        Assert.Equal(ParseKitId(firstKit), (await harness.Recovery.GetRecoveryStatusAsync()).KitId);
    }

    [Fact]
    public async Task V108_A06_CustodyConfirmationConsumesOneNewCodeAndRestoresPrerequisites()
    {
        RequireWindows();
        await using var harness = await RecoveryHarness.CreateAsync();
        var recovered = await RecoverAsync(harness, harness.Bootstrap.Codes[0]);
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        harness.Context.Advance(TimeSpan.FromSeconds(2));
        var signIn = await harness.Sessions.SignInAsync(new(
            harness.Context.UserName, harness.Context.NewPassword));
        Assert.True(signIn.Succeeded, signIn.ReasonCode);
        var principal = signIn.Identity!.PrincipalId;
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            principal.ToString("D"), signIn.Session.SessionId);
        var rotation = await harness.Recovery.RotateRecoveryKitAsync(new RotateRecoveryKitRequest(
            Guid.NewGuid(), harness.Context.StationId, invocation, harness.Context.NewPassword));
        Assert.True(rotation.Succeeded, rotation.ReasonCode);
        var kit = rotation.RecoveryKit!.TakeForDisplay();
        rotation.RecoveryKit.Dispose();
        var codes = ParseCodes(kit);
        Assert.Equal(harness.Context.RecoveryCodeCount, codes.Length);

        var pending = await harness.Recovery.GetRecoveryStatusAsync();
        Assert.False(pending.RecoveryAvailable);
        Assert.Equal("RecoveryKitCustodyConfirmationRequired", pending.ReasonCode);
        Assert.Equal(RecoveryKitState.CustodyConfirmationRequired, pending.KitState);
        Assert.False(pending.ProductionIdentityPrerequisitesMet);

        var confirmation = await harness.Recovery.ConfirmRecoveryKitCustodyAsync(
            new ConfirmRecoveryKitCustodyRequest(Guid.NewGuid(), harness.Context.StationId,
                rotation.KitId!.Value, invocation, codes[0]));
        Assert.True(confirmation.Succeeded, confirmation.ReasonCode);
        var available = await harness.Recovery.GetRecoveryStatusAsync();
        Assert.False(available.RecoveryAvailable);
        Assert.Equal("UsableAdministratorPresent", available.ReasonCode);
        Assert.Equal(RecoveryKitState.Available, available.KitState);
        Assert.Equal(harness.Context.RecoveryCodeCount - 1, available.ValidRecoveryCodeCount);
        Assert.True(available.ProductionIdentityPrerequisitesMet);
        await harness.Sessions.LogoutAsync(signIn.Session.SessionId);
        available = await harness.Recovery.GetRecoveryStatusAsync();
        Assert.False(available.RecoveryAvailable);
        Assert.Equal("UsableAdministratorPresent", available.ReasonCode);

        var confirmed = ReadIdentityEvents(harness.Context.DatabasePath)
            .Last(fields => fields[2] == "RecoveryKitCustodyConfirmed" &&
                fields[42] == confirmation.OperationId.ToString("D"));
        Assert.Equal(principal.ToString("D"), confirmed[5]);
        Assert.Equal(principal.ToString("D"), confirmed[30]);
        Assert.Equal(principal.ToString("D"), confirmed[34]);
        Assert.Equal(signIn.Session.SessionId!.Value.ToString("D"), confirmed[25]);
    }

    [Fact]
    public async Task V108_A07_RestartPreservesPendingKitAndAllowsFreshOwnerSessionToConfirm()
    {
        RequireWindows();
        await using var context = TestContext.Create();
        Guid kitId;
        string[] rotatedCodes;
        string staleRecoveryCode;

        await using (var store = await context.OpenStoreAsync())
        {
            var console = new FakeConsoleAuthority(true, true);
            var clock = context.Clock;
            var identity = context.Identity(store, console);
            var bootstrap = await BootstrapAsync(context, identity, store);
            staleRecoveryCode = bootstrap.Codes[1];
            await DisableInitialAdministratorAsync(context, identity, store);
            await using var sessions = context.Sessions(identity);
            var gate = new FakeRecoveryGate();
            var recovery = new LocalAdministratorRecoveryService(store, context.IdentityOptions(), identity,
                sessions, gate, console, () => clock.UtcNow);
            var restored = await RecoverAsync(context, recovery, bootstrap.Codes[0]);
            Assert.True(restored.Succeeded, restored.ReasonCode);
            context.Advance(TimeSpan.FromSeconds(2));
            var signIn = await sessions.SignInAsync(new(context.UserName, context.NewPassword));
            Assert.True(signIn.Succeeded, signIn.ReasonCode);
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                signIn.Identity!.PrincipalId.ToString("D"), signIn.Session.SessionId);
            var rotation = await recovery.RotateRecoveryKitAsync(new RotateRecoveryKitRequest(
                Guid.NewGuid(), context.StationId, invocation, context.NewPassword));
            Assert.True(rotation.Succeeded, rotation.ReasonCode);
            kitId = rotation.KitId!.Value;
            rotatedCodes = ParseCodes(rotation.RecoveryKit!.TakeForDisplay());
            rotation.RecoveryKit.Dispose();
        }

        await using (var restartedStore = await context.OpenStoreAsync())
        {
            var console = new FakeConsoleAuthority(true, true);
            var identity = context.Identity(restartedStore, console);
            await using var sessions = context.Sessions(identity);
            var gate = new FakeRecoveryGate();
            var recovery = new LocalAdministratorRecoveryService(restartedStore, context.IdentityOptions(), identity,
                sessions, gate, console, () => context.Clock.UtcNow);
            var pending = await recovery.GetRecoveryStatusAsync();
            Assert.Equal(RecoveryKitState.CustodyConfirmationRequired, pending.KitState);
            Assert.False(pending.ProductionIdentityPrerequisitesMet);

            context.Advance(TimeSpan.FromSeconds(2));
            var signIn = await sessions.SignInAsync(new(context.UserName, context.NewPassword));
            Assert.True(signIn.Succeeded, signIn.ReasonCode);
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                signIn.Identity!.PrincipalId.ToString("D"), signIn.Session.SessionId);
            var staleCode = await recovery.ConfirmRecoveryKitCustodyAsync(
                new ConfirmRecoveryKitCustodyRequest(Guid.NewGuid(), context.StationId, kitId,
                    invocation, staleRecoveryCode));
            Assert.False(staleCode.Succeeded);
            Assert.Equal("RecoveryConfirmationCodeInvalid", staleCode.ReasonCode);
            var confirmed = await recovery.ConfirmRecoveryKitCustodyAsync(
                new ConfirmRecoveryKitCustodyRequest(Guid.NewGuid(), context.StationId, kitId,
                    invocation, rotatedCodes[0]));
            Assert.True(confirmed.Succeeded, confirmed.ReasonCode);
            var available = await recovery.GetRecoveryStatusAsync();
            Assert.Equal(RecoveryKitState.Available, available.KitState);
            Assert.True(available.ProductionIdentityPrerequisitesMet);
        }
    }

    [Fact]
    public async Task V108_A08_FinalTransactionChecksRecheckConsoleRuntimeAndCancellation()
    {
        RequireWindows();
        await using var harness = await RecoveryHarness.CreateAsync();
        var before = await ReadFunctionalStateAsync(harness.Store);

        harness.Recovery.BeforeTransactionChecks = () => harness.Console.PhysicalConsole = false;
        var consoleChanged = await RecoverAsync(harness, harness.Bootstrap.Codes[0]);
        Assert.False(consoleChanged.Succeeded);
        Assert.Equal("PhysicalConsoleRequired", consoleChanged.ReasonCode);
        Assert.Equal(before, await ReadFunctionalStateAsync(harness.Store));

        harness.Console.PhysicalConsole = true;
        harness.Gate.Blocker = "RecoveryRuntimeChanged";
        harness.Recovery.BeforeTransactionChecks = null;
        var runtimeChanged = await RecoverAsync(harness, harness.Bootstrap.Codes[0]);
        Assert.False(runtimeChanged.Succeeded);
        Assert.Equal("RecoveryRuntimeChanged", runtimeChanged.ReasonCode);
        Assert.Equal(before, await ReadFunctionalStateAsync(harness.Store));

        harness.Gate.Blocker = null;
        using var cancellation = new CancellationTokenSource();
        harness.Recovery.BeforeTransactionChecks = cancellation.Cancel;
        var cancelled = await harness.Recovery.RecoverAdministratorAsync(new RecoverAdministratorRequest(
            Guid.NewGuid(), harness.Context.StationId, harness.Bootstrap.Codes[0],
            harness.Context.UserName, harness.Context.DisplayName, harness.Context.NewPassword), cancellation.Token);
        Assert.False(cancelled.Succeeded);
        Assert.Equal("RecoveryCancelled", cancelled.ReasonCode);
        Assert.Equal(before, await ReadFunctionalStateAsync(harness.Store));
    }

    [Fact]
    public async Task V108_A09_AuditInsertAbortRollsBackRecoveryWithoutHalfWrittenIdentity()
    {
        RequireWindows();
        await using var harness = await RecoveryHarness.CreateAsync();
        var before = await ReadFunctionalStateAsync(harness.Store);
        var beforeAudit = CountRows(harness.Context.DatabasePath, "audit_entries");
        var trigger = "v108_recovery_abort_" + Guid.NewGuid().ToString("N");
        Execute(harness.Context.DatabasePath, $@"
            CREATE TRIGGER {trigger}
            BEFORE INSERT ON audit_entries
            BEGIN SELECT RAISE(ABORT, 'V108RecoveryAbort'); END;");
        try
        {
            var failed = await RecoverAsync(harness, harness.Bootstrap.Codes[0]);
            Assert.False(failed.Succeeded);
            Assert.Equal(AuditPersistence.Unavailable, failed.Audit);
        }
        finally
        {
            Execute(harness.Context.DatabasePath, $"DROP TRIGGER {trigger};");
        }

        Assert.Equal(before, await ReadFunctionalStateAsync(harness.Store));
        Assert.Equal(beforeAudit, CountRows(harness.Context.DatabasePath, "audit_entries"));
        var retry = await RecoverAsync(harness, harness.Bootstrap.Codes[0]);
        Assert.True(retry.Succeeded, retry.ReasonCode);
    }

    [Fact]
    public async Task V108_A10_RecoveryCodeVerifierIsBoundToItsStationAndInstallation()
    {
        RequireWindows();
        await using var contextA = TestContext.Create();
        await using var contextB = TestContext.Create();
        await using var storeA = await contextA.OpenStoreAsync();
        await using var storeB = await contextB.OpenStoreAsync();

        var consoleA = new FakeConsoleAuthority(true, true);
        var identityA = contextA.Identity(storeA, consoleA);
        var bootstrapA = await BootstrapAsync(contextA, identityA, storeA);
        await DisableInitialAdministratorAsync(contextA, identityA, storeA);

        var consoleB = new FakeConsoleAuthority(true, true);
        var identityB = contextB.Identity(storeB, consoleB);
        var bootstrapB = await BootstrapAsync(contextB, identityB, storeB);
        await DisableInitialAdministratorAsync(contextB, identityB, storeB);
        await using var sessionsB = contextB.Sessions(identityB);
        var gateB = new FakeRecoveryGate();
        var recoveryB = new LocalAdministratorRecoveryService(storeB, contextB.IdentityOptions(), identityB,
            sessionsB, gateB, consoleB, () => contextB.Clock.UtcNow);

        var beforeA = await ReadFunctionalStateAsync(storeA);
        var beforeB = await ReadFunctionalStateAsync(storeB);
        var crossStation = await recoveryB.RecoverAdministratorAsync(new RecoverAdministratorRequest(
            Guid.NewGuid(), contextB.StationId, bootstrapA.Codes[0], contextB.UserName,
            contextB.DisplayName, contextB.NewPassword));

        Assert.False(crossStation.Succeeded);
        Assert.Equal("RecoveryCodeInvalid", crossStation.ReasonCode);
        Assert.Equal(beforeA, await ReadFunctionalStateAsync(storeA));
        Assert.Equal(beforeB, await ReadFunctionalStateAsync(storeB));
        Assert.NotEqual(contextA.Policy.SigningKeyName, contextB.Policy.SigningKeyName);
        Assert.NotEqual(contextA.StationId, contextB.StationId);
        Assert.NotEqual(bootstrapA.PrincipalId, bootstrapB.PrincipalId);
    }

    private static async Task<AdministratorRecoveryResult> RecoverAsync(
        RecoveryHarness harness, string code) =>
        await RecoverAsync(harness.Context, harness.Recovery, code);

    private static async Task<AdministratorRecoveryResult> RecoverAsync(
        TestContext context, LocalAdministratorRecoveryService recovery, string code) =>
        await recovery.RecoverAdministratorAsync(new RecoverAdministratorRequest(
            Guid.NewGuid(), context.StationId, code, context.UserName, context.DisplayName, context.NewPassword));

    private static async Task<BootstrapEvidence> BootstrapAsync(
        TestContext context, LocalIdentityService identity, SqliteCommandStore store)
    {
        var issued = await identity.ProvisionBootstrapTokenAsync();
        Assert.True(issued.Succeeded, issued.ReasonCode);
        var token = issued.Token!.TakeForDisplay();
        await WaitForVerifiedAsync(store);
        var created = await identity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
            context.StationId, token, context.UserName, context.DisplayName, context.Password));
        Assert.True(created.Succeeded, created.ReasonCode);
        Assert.NotNull(created.Identity);
        var recoveryKit = created.RecoveryKit!.TakeForDisplay();
        var codes = ParseCodes(recoveryKit);
        Assert.Equal(context.RecoveryCodeCount, codes.Length);
        await WaitForVerifiedAsync(store);
        return new(created.Identity!.PrincipalId, codes);
    }

    private static async Task DisableInitialAdministratorAsync(
        TestContext context, LocalIdentityService identity, SqliteCommandStore store)
    {
        for (var attempt = 0; attempt < context.AuthenticationPolicy.AccountFailureLimit; attempt++)
        {
            var failed = await identity.AuthenticateAsync(new PasswordSignInRequest(
                context.UserName, context.Password + " wrong"));
            Assert.False(failed.Succeeded);
            await WaitForVerifiedAsync(store);
            context.Advance(TimeSpan.FromSeconds(2));
        }

        await WaitForVerifiedAsync(store);
        var state = await store.ReadIdentityAsync(CancellationToken.None);
        Assert.NotNull(state.Administrator);
        Assert.False(state.Administrator!.Enabled);
    }

    private static string[] ParseCodes(string kit)
    {
        var codes = kit.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => LocalIdentityService.ParseCode(line) is not null)
            .ToArray();
        return codes;
    }

    private static Guid ParseKitId(string kit)
    {
        var line = kit.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith("Recovery Kit: ", StringComparison.Ordinal));
        Assert.True(Guid.TryParse(line[14..], out var id));
        return id;
    }

    private sealed record BootstrapEvidence(Guid PrincipalId, string[] Codes);

    private sealed record FunctionalState(Guid? KitId, RecoveryKitState KitState,
        Guid? RecoveredPrincipalId, Guid? RecoveryOwnerPrincipalId, int ValidCodeCount, string Accounts);

    private static async Task<FunctionalState> ReadFunctionalStateAsync(SqliteCommandStore store)
    {
        var state = await store.ReadIdentityAsync(CancellationToken.None);
        var accounts = string.Join("|", state.EnumerateAccounts().OrderBy(account => account.PrincipalId)
            .Select(account => string.Join(":", account.PrincipalId.ToString("D"), account.Enabled,
                account.CredentialRevision, account.AuthorizationRevision)));
        return new(state.RecoveryKitId, state.KitState, state.RecoveredPrincipalId,
            state.RecoveryOwnerPrincipalId, state.RecoveryCodes.Count(code => !code.Consumed && !code.Revoked), accounts);
    }

    private static IReadOnlyList<string?[]> ReadIdentityEvents(string path)
    {
        using var connection = Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM audit_entries WHERE IdentityPosition IS NOT NULL ORDER BY IdentityPosition;";
        using var reader = command.ExecuteReader();
        var result = new List<string?[]>();
        while (reader.Read())
        {
            var payload = Convert.FromBase64String(reader.GetString(0));
            using var input = new MemoryStream(payload, writable: false);
            using var binary = new BinaryReader(input, new UTF8Encoding(false, true));
            Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(binary.ReadBytes(4)));
            Assert.Equal("IdentityEvent", ReadValue(binary));
            var count = BinaryPrimitives.ReadInt32BigEndian(binary.ReadBytes(4));
            var fields = Enumerable.Range(0, count).Select(_ => ReadValue(binary)).ToArray();
            Assert.Equal(input.Length, input.Position);
            result.Add(fields);
        }

        return result;
    }

    private static string? ReadValue(BinaryReader reader)
    {
        var marker = reader.ReadByte();
        if (marker == 0) return null;
        Assert.Equal(1, marker);
        var lengthBytes = reader.ReadBytes(4);
        Assert.Equal(4, lengthBytes.Length);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        Assert.InRange(length, 0, 4096);
        var bytes = reader.ReadBytes(length);
        Assert.Equal(length, bytes.Length);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static int CountRows(string path, string table)
    {
        Assert.Contains(table, new[] { "audit_entries", "identity_authority", "recovery_operations" });
        using var connection = Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path, readOnly: false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task<AuditIntegrityReport> WaitForVerifiedAsync(
        SqliteCommandStore store, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var report = store.Integrity;
            if (report is { State: AuditIntegrityState.Verified }) return report;
            if (report?.State == AuditIntegrityState.Faulted)
                throw new XunitException($"Audit integrity faulted: {report.ReasonCode}");
            await Task.Delay(25);
        }

        throw new XunitException($"Audit integrity did not become Verified. Last state: {store.Integrity?.State}, " +
            $"reason: {store.Integrity?.ReasonCode}");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Administrator recovery acceptance requires Windows DPAPI machine protection.");
    }

    private sealed class FakeConsoleAuthority : IPhysicalConsoleAuthority
    {
        internal FakeConsoleAuthority(bool physicalConsole, bool windowsAdministrator)
        { PhysicalConsole = physicalConsole; WindowsAdministrator = windowsAdministrator; }
        internal bool PhysicalConsole { get; set; }
        internal bool WindowsAdministrator { get; set; }
        internal string? WindowsSid { get; set; } = "S-1-5-21-V108-TEST";
        public ConsoleAuthority Observe() => new(PhysicalConsole, WindowsAdministrator, WindowsSid);
    }

    private sealed class FakeRecoveryGate : IAdministratorRecoveryRuntimeGate
    {
        private string? _blocker;
        internal string? Blocker { get => Volatile.Read(ref _blocker); set => Volatile.Write(ref _blocker, value); }
        public string? GetBlocker() => Blocker;
        public ValueTask<AdministratorRecoveryRuntimeLease> EnterAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AdministratorRecoveryRuntimeLease(Guid.NewGuid(), GetBlocker));
        }
    }

    private sealed class RecoveryHarness : IAsyncDisposable
    {
        private RecoveryHarness(TestContext context, SqliteCommandStore store, LocalIdentityService identity,
            InteractiveSessionService sessions, LocalAdministratorRecoveryService recovery,
            FakeConsoleAuthority console, FakeRecoveryGate gate, BootstrapEvidence bootstrap)
        { Context = context; Store = store; Identity = identity; Sessions = sessions; Recovery = recovery;
            Console = console; Gate = gate; Bootstrap = bootstrap; }

        internal TestContext Context { get; }
        internal SqliteCommandStore Store { get; }
        internal LocalIdentityService Identity { get; }
        internal InteractiveSessionService Sessions { get; }
        internal LocalAdministratorRecoveryService Recovery { get; }
        internal FakeConsoleAuthority Console { get; }
        internal FakeRecoveryGate Gate { get; }
        internal BootstrapEvidence Bootstrap { get; }

        internal static async Task<RecoveryHarness> CreateAsync(bool disableInitialAdministrator = true)
        {
            RequireWindows();
            var context = TestContext.Create();
            SqliteCommandStore? store = null;
            InteractiveSessionService? sessions = null;
            try
            {
                store = await context.OpenStoreAsync();
                var console = new FakeConsoleAuthority(true, true);
                var identity = context.Identity(store, console);
                var bootstrap = await BootstrapAsync(context, identity, store);
                if (disableInitialAdministrator)
                    await DisableInitialAdministratorAsync(context, identity, store);
                sessions = context.Sessions(identity);
                var gate = new FakeRecoveryGate();
                var recovery = new LocalAdministratorRecoveryService(store, context.IdentityOptions(), identity,
                    sessions, gate, console, () => context.Clock.UtcNow);
                return new RecoveryHarness(context, store, identity, sessions, recovery, console, gate, bootstrap);
            }
            catch
            {
                if (sessions is not null) await sessions.DisposeAsync();
                if (store is not null) await store.DisposeAsync();
                await context.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            await Store.DisposeAsync();
            await Context.DisposeAsync();
        }
    }

    private sealed class MutableClock
    {
        internal MutableClock(DateTimeOffset value) => UtcNow = value;
        internal DateTimeOffset UtcNow { get; private set; }
        internal void Advance(TimeSpan value) => UtcNow = UtcNow.Add(value);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private TestContext(string directoryPath, string databasePath, string stationId,
            string userName, string displayName, string password, string newPassword,
            int recoveryCodeCount, AuditIntegrityPolicy policy, AuthenticationPolicy authenticationPolicy,
            MutableClock clock)
        { DirectoryPath = directoryPath; DatabasePath = databasePath; StationId = stationId; UserName = userName;
            DisplayName = displayName; Password = password; NewPassword = newPassword; RecoveryCodeCount = recoveryCodeCount;
            Policy = policy; AuthenticationPolicy = authenticationPolicy; Clock = clock; }

        internal string DirectoryPath { get; }
        internal string DatabasePath { get; }
        internal string StationId { get; }
        internal string UserName { get; }
        internal string DisplayName { get; }
        internal string Password { get; }
        internal string NewPassword { get; }
        internal int RecoveryCodeCount { get; }
        internal AuditIntegrityPolicy Policy { get; }
        internal AuthenticationPolicy AuthenticationPolicy { get; }
        internal MutableClock Clock { get; }

        internal static TestContext Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V108-AdministratorRecovery",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V108Station-" + Guid.NewGuid().ToString("N")[..8];
            var policy = new AuditIntegrityPolicy(station, "v1", "SharpInspect.Test.V108." + Guid.NewGuid().ToString("N"))
            { AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1) };
            var authentication = AuthenticationPolicy.Development with { AccountFailureLimit = 2 };
            return new TestContext(directory, Path.Combine(directory, "recovery.sqlite"), station,
                "alice-108", "Alice 108", "V108 initial administrator password 2026!",
                "V108 recovered administrator password 2026!", 8, policy, authentication,
                new MutableClock(DateTimeOffset.UtcNow));
        }

        internal LocalIdentityOptions IdentityOptions() => new(StationId,
            new LocalPasswordPolicy { Blocklist = PasswordBlocklist.Create(
                "v108-test-blocklist", "v1", new[] { "known-compromised-value" }) },
            new Pbkdf2PasswordHasher(), AuthenticationPolicy, AuthorizationPolicy.Development);

        internal ProductionStoreOptions Options() => new(DatabasePath)
        { AuditIntegrityPolicy = Policy, LocalIdentity = IdentityOptions(), CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2), QueueCapacity = 8 };

        internal async Task<SqliteCommandStore> OpenStoreAsync()
        {
            var store = new SqliteCommandStore(Options());
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            if (!initialized.Committed)
            { await store.DisposeAsync(); throw new XunitException($"Store initialization failed: {initialized.ReasonCode}"); }
            await WaitForVerifiedAsync(store);
            return store;
        }

        internal LocalIdentityService Identity(SqliteCommandStore store, FakeConsoleAuthority console) =>
            new(store, IdentityOptions(), console, () => Clock.UtcNow);

        internal InteractiveSessionService Sessions(LocalIdentityService identity) =>
            new(identity, AuthenticationPolicy, (_, _) => ValueTask.FromResult(true));

        internal void Advance(TimeSpan value) => Clock.Advance(value);

        public ValueTask DisposeAsync()
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(Policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA1416
