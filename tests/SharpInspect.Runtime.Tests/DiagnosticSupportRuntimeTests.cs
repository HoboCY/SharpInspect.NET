using System.Reflection;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ProductionOutboxMigrationTests
{
    [Fact, Trait("VerificationId", "V158_R01")]
    public async Task V158_R01_IndependentPermissionAndFreshGrantPrecedeAnyCapture()
    {
        await using var fixture = await SupportRuntimeFixture.CreateAsync(grantSupport: false);
        var denied = await fixture.Runtime.SubmitAsync(fixture.Start());
        Assert.Equal("PermissionDenied", denied.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, denied.Audit);
        Assert.False(fixture.Runtime.ReadCapture().Elevated);
        await fixture.SetPermissionsAsync(true);
        var noGrant = await fixture.Runtime.SubmitAsync(fixture.Start());
        Assert.Equal("StepUpRequired", noGrant.ReasonCode);
        Assert.Empty((await fixture.Store.ReadDiagnosticSupportStateAsync()).Operations);
        var start = await fixture.AuthorizeAsync(fixture.Start(events: 2));
        var admittedOutcome = await fixture.Runtime.SubmitAsync(start);
        Assert.True(admittedOutcome.Disposition == CommandDisposition.Accepted, admittedOutcome.ReasonCode + ":" + fixture.Describe());
        var admitted = await fixture.Store.ReadDiagnosticOperationAsync(start.OperationId);
        Assert.Equal(DiagnosticOperationPhase.Admitted, admitted!.Phase);
        Assert.Equal(start.Invocation.StepUpGrantId, admitted.Payload.StepUpGrantId);
        Assert.Equal(DiagnosticEmission.Accepted, fixture.Emit());
        Assert.Equal(DiagnosticEmission.Accepted, fixture.Emit());
        Assert.False(fixture.Runtime.ReadCapture().Elevated);
        await fixture.WaitCaptureAsync();
        var complete = await fixture.Store.ReadDiagnosticOperationAsync(start.OperationId);
        Assert.Equal(DiagnosticOperationPhase.Completed, complete!.Phase);
        Assert.Equal(2, complete.Payload.ObservedEvents);
        Assert.Equal("DiagnosticCaptureEventLimit", complete.ReasonCode);
        Assert.Equal(DiagnosticEmission.Dropped, fixture.Emit());
        Assert.Equal(0, Reserve(fixture.Options));
    }

    [Fact, Trait("VerificationId", "V158_R02")]
    public async Task V158_R02_ExpiryAndFreshStopHaveDistinctDurableTerminalEvidence()
    {
        await using var fixture = await SupportRuntimeFixture.CreateAsync();
        var timed = await fixture.AuthorizeAsync(fixture.Start(duration: TimeSpan.FromSeconds(1)));
        Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(timed)).Disposition);
        await fixture.WaitCaptureAsync();
        Assert.Equal("DiagnosticCaptureTimeLimit", fixture.Runtime.ReadCapture().ReasonCode);
        var start = await fixture.AuthorizeAsync(fixture.Start());
        Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(start)).Disposition);
        var stop = new StopDiagnosticCaptureCommand(Guid.NewGuid(), fixture.Invocation(), start.OperationId,
            DiagnosticSupportReason.FaultInvestigation);
        var authorizedStop = await fixture.AuthorizeAsync(stop);
        Assert.NotEqual(start.Invocation.StepUpGrantId, authorizedStop.Invocation.StepUpGrantId);
        Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(authorizedStop)).Disposition);
        await fixture.WaitCaptureAsync();
        var terminal = await fixture.Store.ReadDiagnosticOperationAsync(start.OperationId);
        Assert.Equal(DiagnosticOperationPhase.Completed, terminal!.Phase);
        Assert.Equal(DiagnosticSupportReason.FaultInvestigation, terminal.Payload.StopReason);
        Assert.NotNull(terminal.Payload.StopCommand);
        Assert.NotNull(terminal.Payload.StopAuthorization);
    }

    [Theory, InlineData("cancel"), InlineData("lock"), InlineData("permission"), Trait("VerificationId", "V158_R03")]
    public async Task V158_R03_AuthorityLossImmediatelyReturnsToBaseline(string cause)
    {
        await using var fixture = await SupportRuntimeFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var command = await fixture.AuthorizeAsync(fixture.Start());
        Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(command, cancellation.Token)).Disposition);
        Assert.True(fixture.Runtime.ReadCapture().Elevated);
        if (cause == "cancel") cancellation.Cancel();
        else if (cause == "lock") await fixture.Sessions.LockAsync(fixture.Sessions.Current.SessionId, SessionLockReason.OperatingSystemLock);
        else await fixture.SetPermissionsAsync(false);
        Assert.False(fixture.Runtime.ReadCapture().Elevated);
        Assert.Equal(DiagnosticEmission.Dropped, fixture.Emit());
        await fixture.WaitCaptureAsync();
        var terminal = await fixture.Store.ReadDiagnosticOperationAsync(command.OperationId);
        Assert.Equal(DiagnosticOperationPhase.Interrupted, terminal!.Phase);
        Assert.Equal(0, Reserve(fixture.Options));
    }

    [Fact, Trait("VerificationId", "V158_R04")]
    public async Task V158_R04_CompletedBundleHasVerifiedBytesAndLosesDeliveryOnLock()
    {
        await using var fixture = await SupportRuntimeFixture.CreateAsync();
        var capture = await fixture.AuthorizeAsync(fixture.Start(events: 1));
        Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(capture)).Disposition);
        Assert.Equal(DiagnosticEmission.Accepted, fixture.Emit());
        await fixture.WaitCaptureAsync();
        var bundle = await fixture.AuthorizeAsync(fixture.Bundle());
        Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(bundle)).Disposition);
        await fixture.WaitAsync(() => fixture.Runtime.ReadBundle().Phase is SupportBundlePhase.Completed or SupportBundlePhase.Interrupted,
            "bundle retirement");
        var status = fixture.Runtime.ReadBundle();
        Assert.Equal(SupportBundlePhase.Completed, status.Phase);
        var read = await fixture.Runtime.ReadAsync(new(status.BundleId!.Value, fixture.Invocation()));
        Assert.True(read.Available, read.ReasonCode);
        Assert.Equal(status.ContentHash, Convert.ToHexString(SHA256.HashData(read.Bytes.Span)));
        var text = System.Text.Encoding.UTF8.GetString(read.Bytes.Span);
        Assert.Contains("Runtime.Detail", text);
        Assert.DoesNotContain("VendorState", text);
        var invocation = fixture.Invocation();
        await fixture.Sessions.LockAsync(invocation.SessionId, SessionLockReason.OperatingSystemLock);
        var denied = await fixture.Runtime.ReadAsync(new(status.BundleId.Value, invocation));
        Assert.False(denied.Available); Assert.True(denied.Bytes.IsEmpty);
    }

    [Fact, Trait("VerificationId", "V158_R05")]
    public async Task V158_R05_CancelledExportKeepsPhysicalReservationUntilActualReadRetires()
    {
        await using var fixture = await SupportRuntimeFixture.CreateAsync();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancelled = new CancellationTokenSource();
        fixture.SetSafeStoreHook(() => { entered.Set(); release.Wait(); });
        try
        {
            var command = await fixture.AuthorizeAsync(fixture.Bundle());
            Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(command, cancelled.Token)).Disposition);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            cancelled.Cancel();
            await fixture.WaitAsync(() => fixture.Runtime.ReadBundle().ReasonCode == "SupportBundlePhysicalRetirementPending", "cancelled physical read remains owned");
            Assert.Equal("DiagnosticSupportBusy", (await fixture.Runtime.SubmitAsync(fixture.Start())).ReasonCode);
            Assert.Contains("DiagnosticSupportInProgress", (await fixture.Runtime.GetSnapshotAsync()).AdmissionBlockers);
            Assert.Equal(DiagnosticOperationPhase.Admitted, (await fixture.Store.ReadDiagnosticOperationAsync(command.OperationId))!.Phase);
            Assert.Empty(Directory.GetFiles(fixture.Options.LoggingDiagnostics!.SupportBundles!.Files.Directory, "*.sib"));
            release.Set(); fixture.SetSafeStoreHook(null);
            await fixture.WaitAsync(() => !(fixture.Runtime.GetSnapshotAsync().Result.AdmissionBlockers.Contains("DiagnosticSupportInProgress")), "actual export retirement");
            Assert.Equal(DiagnosticOperationPhase.Interrupted, (await fixture.Store.ReadDiagnosticOperationAsync(command.OperationId))!.Phase);
            Assert.Null(fixture.Runtime.ReadBundle().ContentHash);
        }
        finally { release.Set(); fixture.SetSafeStoreHook(null); }
    }

    [Theory, InlineData(true), InlineData(false), Trait("VerificationId", "V158_R06")]
    public async Task V158_R06_UnqualifiedReadyOrArmedProjectionRefusesSupportWork(bool ready)
    {
        await using var fixture = await SupportRuntimeFixture.CreateAsync();
        var runtime = fixture.Runtime;
        var snapshotField = typeof(StationRuntime).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sync = typeof(StationRuntime).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        ValueTask<RuntimeCommandOutcome> startOutcome;
        ValueTask<RuntimeCommandOutcome> bundleOutcome;
        lock (sync)
        {
            var original = (StationStateSnapshot)snapshotField.GetValue(runtime)!;
            try
            {
                snapshotField.SetValue(runtime, original with { Ready = ready,
                    ArmState = ready ? ProductionArmState.Disarmed : ProductionArmState.Armed });
                startOutcome = runtime.SubmitAsync(fixture.Start());
                bundleOutcome = runtime.SubmitAsync(fixture.Bundle());
                Assert.True(startOutcome.IsCompletedSuccessfully); Assert.True(bundleOutcome.IsCompletedSuccessfully);
            }
            finally { snapshotField.SetValue(runtime, original); }
        }
        Assert.Equal("DiagnosticSupportLoadNotQualified", (await startOutcome).ReasonCode);
        Assert.Equal("DiagnosticSupportLoadNotQualified", (await bundleOutcome).ReasonCode);
        Assert.Empty((await fixture.Store.ReadDiagnosticSupportStateAsync()).Operations);
    }

    [Fact, Trait("VerificationId", "V158_R07")]
    public async Task V158_R07_AcceptedStopRetainsItsAuditLinksWhenAuthorityIsLostBeforeContinuation()
    {
        await using var fixture = await SupportRuntimeFixture.CreateAsync();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var start = await fixture.AuthorizeAsync(fixture.Start());
        Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(start)).Disposition);
        var stop = await fixture.AuthorizeAsync(new StopDiagnosticCaptureCommand(Guid.NewGuid(), fixture.Invocation(), start.OperationId,
            DiagnosticSupportReason.FaultInvestigation));
        fixture.Runtime.AfterDiagnosticStopAuthorizationForTesting = () => { entered.Set(); release.Wait(); };
        Task<RuntimeCommandOutcome>? stopping = null;
        try
        {
            stopping = Task.Run(async () => await fixture.Runtime.SubmitAsync(stop));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            await fixture.Sessions.LockAsync(fixture.Sessions.Current.SessionId, SessionLockReason.OperatingSystemLock);
            Assert.False(fixture.Runtime.ReadCapture().Elevated);
            release.Set(); Assert.Equal(CommandDisposition.Accepted, (await stopping).Disposition);
            await fixture.WaitCaptureAsync();
            var terminal = (await fixture.Store.ReadDiagnosticOperationAsync(start.OperationId))!;
            Assert.Equal(DiagnosticOperationPhase.Interrupted, terminal.Phase);
            Assert.NotNull(terminal.Payload.StopCommand); Assert.NotNull(terminal.Payload.StopAuthorization);
            Assert.Equal(DiagnosticSupportReason.FaultInvestigation, terminal.Payload.StopReason);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        }
        finally { release.Set(); if (stopping is not null) await stopping; fixture.Runtime.AfterDiagnosticStopAuthorizationForTesting = null; }
    }

    [Fact, Trait("VerificationId", "V158_R08")]
    public async Task V158_R08_LocalStopRevokesPausedCompletedBundleDeliveryWithoutEarlySlotRelease()
    {
        await using var fixture = await SupportRuntimeFixture.CreateAsync();
        var command = await fixture.AuthorizeAsync(fixture.Bundle());
        Assert.Equal(CommandDisposition.Accepted, (await fixture.Runtime.SubmitAsync(command)).Disposition);
        await fixture.WaitAsync(() => fixture.Runtime.ReadBundle().Phase == SupportBundlePhase.Completed, "bundle completion");
        var bundle = fixture.Runtime.ReadBundle();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        fixture.Runtime.BeforeSupportBundleDeliveryForTesting = () => { entered.Set(); release.Wait(); };
        Task<SupportBundleReadResult>? delivery = null;
        try
        {
            delivery = Task.Run(async () => await fixture.Runtime.ReadAsync(new(bundle.BundleId!.Value, fixture.Invocation())));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var stop = await fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole)));
            Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
            Assert.Contains("DiagnosticSupportInProgress", (await fixture.Runtime.GetSnapshotAsync()).AdmissionBlockers);
            release.Set(); var result = await delivery;
            Assert.False(result.Available); Assert.True(result.Bytes.IsEmpty);
        }
        finally { release.Set(); if (delivery is not null) await delivery; fixture.Runtime.BeforeSupportBundleDeliveryForTesting = null; }
    }

    private sealed class SupportRuntimeFixture : IAsyncDisposable
    {
        private const string User = "diagnostic.support.fixture";
        private const string Password = "V158 fixture only password 2026!";
        private readonly string _directory;
        internal ProductionStoreOptions Options { get; private set; }
        internal SqliteCommandStore Store { get; private set; } = null!;
        internal InteractiveSessionService Sessions { get; private set; } = null!;
        internal LocalAuthorizationService Authorization { get; private set; } = null!;
        internal StationRuntime Runtime { get; private set; } = null!;
        private SupportRuntimeFixture(string directory, ProductionStoreOptions options)
        { _directory = directory; Options = options; }
        internal CommandInvocation Invocation(Guid? grant = null) => new(CommandSource.PhysicalConsole,
            Sessions.Current.PrincipalId, Sessions.Current.SessionId, grant);
        internal StartDiagnosticCaptureCommand Start(int events = 100, TimeSpan? duration = null) => new(Guid.NewGuid(),
            Invocation(), Guid.NewGuid(), new(Options.LoggingDiagnostics!.Policy.ContentHash, new[] { "Runtime" },
                DiagnosticLevel.Debug, duration ?? TimeSpan.FromSeconds(10), events), DiagnosticSupportReason.MaintenanceInvestigation);
        internal CreateSupportBundleCommand Bundle() => new(Guid.NewGuid(), Invocation(), Guid.NewGuid(),
            new(Runtime.ReadCapture().RuntimeEpoch, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(1),
                Array.Empty<ExecutionCorrelationId>(), Array.Empty<Guid>(), Array.Empty<Guid>()),
            Options.DiagnosticSupport!.Policy.ContentHash, DiagnosticSupportReason.MaintenanceInvestigation);
        internal async Task<T> AuthorizeAsync<T>(T command) where T : DiagnosticSupportCommand
        {
            var kind = command switch { StartDiagnosticCaptureCommand => AuditedCommandKind.StartDiagnosticCapture,
                StopDiagnosticCaptureCommand => AuditedCommandKind.StopDiagnosticCapture, _ => AuditedCommandKind.CreateSupportBundle };
            var grant = await Authorization.ReauthenticateAsync(new(command.CorrelationId, command.Invocation,
                new(command is CreateSupportBundleCommand ? Permission.ExportSupportBundle : Permission.StartDiagnosticCapture,
                    command.CorrelationId, command.AuthorizationTarget, kind), Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            return (T)(command with { Invocation = Invocation(grant.GrantId) });
        }
        internal string Describe() => string.Join(",", new[] { "_storeReady", "_auditFault", "_activationStartupPending", "_activationStartupBlocked" }
            .Select(name => name + "=" + typeof(StationRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Runtime))) +
            "/" + System.Text.Json.JsonSerializer.Serialize(Store.Integrity);
        internal DiagnosticEmission Emit()
        {
            var service = (RuntimeDiagnosticService)typeof(StationRuntime).GetField("_diagnostics",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Runtime)!;
            return service.Pipeline!.TryEmit(new("Runtime.Detail", 1,
                new DiagnosticPropertyRequest("State", "Idle"), new("VendorState", "Known")));
        }
        internal void SetSafeStoreHook(Action? hook)
        {
            var service = typeof(StationRuntime).GetField("_diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Runtime)!;
            var safe = typeof(RuntimeDiagnosticService).GetField("_safe", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            typeof(LocalDiagnosticStore).GetField("_beforeInstallationVerificationForTesting", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(safe, hook);
        }
        internal Task WaitCaptureAsync() => WaitAsync(() => Runtime.ReadCapture().Phase is
            DiagnosticCapturePhase.Completed or DiagnosticCapturePhase.Interrupted, "capture retirement");
        internal async Task WaitAsync(Func<bool> condition, string reason)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            Assert.True(condition(), reason + ": " + Runtime.ReadCapture().ReasonCode + "/" + Runtime.ReadBundle().ReasonCode + "/" +
                System.Text.Json.JsonSerializer.Serialize(Runtime.DiagnosticHealthQuery.ReadHealth()));
        }
        internal async Task SetPermissionsAsync(bool enabled)
        {
            var principal = Guid.Parse(Sessions.Current.PrincipalId!);
            var permissions = Options.LocalIdentity!.AuthorizationPolicy.RoleBundles[HumanRoleBundle.Administrator]
                .Concat(enabled ? new[] { Permission.StartDiagnosticCapture, Permission.ExportSupportBundle } : Array.Empty<Permission>()).Distinct();
            var id = Guid.NewGuid();
            var grant = await Authorization.ReauthenticateAsync(new(id, Invocation(),
                new(Permission.ManagePermissions, id, principal.ToString("D"), AuditedCommandKind.SetHumanPermissions), Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            var outcome = await Runtime.SubmitAsync(new SetHumanPermissionsCommand(id, Invocation(grant.GrantId), principal, permissions));
            Assert.Equal(CommandDisposition.Accepted, outcome.Disposition);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(Store);
        }
        internal static async Task<SupportRuntimeFixture> CreateAsync(bool grantSupport = true)
        {
            var directory = ImageEvidenceFixture.NewDirectory("V158Runtime");
            var source = WithRetention(WithReconciliation(SourceProfile(directory, "V158Runtime", 35,
                TracePolicies("V158Runtime")), directory));
            // Authority database, logs, images and support artifacts are distinct sibling roots.
            var databaseDirectory = Path.Combine(directory, "database"); Directory.CreateDirectory(databaseDirectory);
            var isolated = new ProductionStoreOptions(Path.Combine(databaseDirectory, "station.sqlite"));
            foreach (var property in typeof(ProductionStoreOptions).GetProperties())
                if (property.Name != nameof(ProductionStoreOptions.DatabasePath)) property.SetValue(isolated, property.GetValue(source));
            source = isolated;
            var fixture = new SupportRuntimeFixture(directory, source);
            try
            {
                fixture.Store = new(source);
                Assert.True((await fixture.Store.Initialization).Committed);
                await fixture.ConnectIdentityAsync(bootstrap: true);
                var template = TraceStoragePolicyRuntimeTests.Policy(days: 1);
                var tracePolicy = new TraceStoragePolicyDefinition(template.PolicyId, template.Version, template.ApprovalReference,
                    template.ApprovalVersion, template.Rationale, template.RetentionRules, template.MinimumReserveBytes,
                    template.MinimumReservePercent, template.RequiredRoutes, template.ImageBacklog, template.EvidenceStageTimeout,
                    template.TraceCommitTimeout, template.Scrubber,
                    new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 128L << 20, 100000), template.MaximumWalBytes);
                var command = new PublishTraceStoragePolicyCommand(Guid.NewGuid(), fixture.Invocation(), 0,
                    tracePolicy, "Fixture approval");
                var grant = await fixture.Authorization.ReauthenticateAsync(new(command.CorrelationId, command.Invocation,
                    new(Permission.ManageProductionPolicy, command.CorrelationId, command.AuthorizationTarget,
                        AuditedCommandKind.PublishTraceStoragePolicy), Password));
                Assert.True(grant.Succeeded, grant.ReasonCode);
                var policyService = new TraceStoragePolicyService(source, fixture.Authorization, fixture.Store, new SqliteTraceStoragePolicyQuery(source));
                var published = await policyService.PublishAsync(new(command.CorrelationId, fixture.Invocation(grant.GrantId), 0, command.Policy, command.Reason));
                Assert.True(published.Succeeded, published.Outcome.ReasonCode);
                await fixture.Sessions.DisposeAsync(); fixture.Authorization.Dispose(); await fixture.Store.DisposeAsync();
                var baseline = DiagnosticPipelineTests.Policy(traceHash: published.Snapshot!.ContentHash, traceVersion: published.Snapshot.Policy.Version);
                fixture.Options = DiagnosticTarget(source, logging: Logging(directory, DiagnosticCapturePipelineTests.Policy(baseline: baseline)));
                var open = await OpenAsync(fixture.Options); Assert.True(open.Available, open.Status.ReasonCode);
                await using (var migration = open.Session!) Assert.True((await StoreStartupMaintenanceTests.Finish(migration)).Completed);
                fixture.Store = await OpenStoreAsync(fixture.Options);
                await fixture.ConnectIdentityAsync(bootstrap: false);
                fixture.Runtime = new(fixture.Store, TimeSpan.FromMilliseconds(20), fixture.Sessions, fixture.Authorization,
                    productionStoreOptions: fixture.Options);
                await fixture.WaitAsync(() => fixture.Runtime.DiagnosticHealthQuery.ReadHealth() is { Configured: true, Unhealthy: false }, "diagnostic activation");
                if (grantSupport) await fixture.SetPermissionsAsync(true);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        private async Task ConnectIdentityAsync(bool bootstrap)
        {
            var identity = new LocalIdentityService(Store, Options.LocalIdentity!, new RecipeDraftStorageTests.Fixture.FixtureConsole());
            if (bootstrap)
            {
                var token = await identity.ProvisionBootstrapTokenAsync(); Assert.True(token.Succeeded, token.ReasonCode);
                using var display = token.Token!;
                var created = await identity.CreateFirstAdministratorAsync(new(Options.LocalIdentity!.StationId,
                    display.TakeForDisplay(), User, "Support Fixture", Password));
                Assert.True(created.Succeeded, created.ReasonCode); created.RecoveryKit?.Dispose();
            }
            Sessions = new(identity, Options.LocalIdentity!.AuthenticationPolicy, identity.PersistSessionEventAsync);
            var login = await Sessions.SignInAsync(new(User, Password)); Assert.True(login.Succeeded, login.ReasonCode);
            Authorization = new(Store, Options.LocalIdentity, identity, Sessions);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(Store);
        }
        public async ValueTask DisposeAsync()
        {
            if (Runtime is not null) await Runtime.DisposeAsync();
            if (Sessions is not null) await Sessions.DisposeAsync();
            Authorization?.Dispose();
            if (Store is not null) await Store.DisposeAsync();
            Remove(_directory);
        }
    }
}
