using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class DiagnosticsIntegrationTests
{
    [Fact, Trait("VerificationId", "V156_R05")]
    public async Task V156_R05_BlockedDirectoryVerificationCannotBlockActivatedPolicyMatchingOrReplacePhysicalWorkers()
    {
        await using var fixture = await TraceStoragePolicyRuntimeTests.CreateFixtureAsync();
        var command = await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 0,
            TraceStoragePolicyRuntimeTests.Policy(days: 1, minimumReserveBytes: 1024, minimumReservePercent: .001m));
        var published = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(command);
        Assert.True(published.Succeeded, published.Outcome.ReasonCode); await fixture.WaitForVerifiedAsync();
        var root = Path.Combine(Path.GetTempPath(), "SharpInspect-T56-blocked-verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var release = new ManualResetEventSlim(); using var entered = new CountdownEvent(2);
        var armed = 0; var blockedCalls = 0;
        RuntimeDiagnosticService? service = null;
        try
        {
            var policy = DiagnosticPipelineTests.Policy(timeout: TimeSpan.FromMilliseconds(100),
                traceHash: published.Snapshot!.ContentHash, traceVersion: published.Snapshot.Policy.Version);
            var safe = Local(Path.Combine(root, "safe"), false, policy.SafeFiles);
            var protectedStore = Local(Path.Combine(root, "protected"), true, policy.ProtectedFiles);
            var logging = new LoggingDiagnosticsOptions(policy, safe, DiagnosticDirectoryInstallation.Install(safe),
                protectedStore, DiagnosticDirectoryInstallation.Install(protectedStore));
            var options = new ProductionStoreOptions(fixture.Options.DatabasePath)
            {
                AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy, LocalIdentity = fixture.Options.LocalIdentity,
                RecipeDrafts = fixture.Options.RecipeDrafts, TraceStoragePolicies = fixture.Options.TraceStoragePolicies,
                LoggingDiagnostics = logging
            };
            service = new(options, Guid.NewGuid(), Task.CompletedTask, fixture.Authorization,
                beforeInstallationVerificationForTesting: () =>
                {
                    if (Volatile.Read(ref armed) == 0) return;
                    if (Interlocked.Increment(ref blockedCalls) <= 2) entered.Signal();
                    release.Wait();
                });
            await service.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(service.Matches(published.Snapshot));
            Volatile.Write(ref armed, 1);
            service.Pipeline!.ObserveException(new InvalidOperationException("SECRET-BAIT"), "ManagedHost");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var matching = Task.Run(() => Enumerable.Range(0, 100).All(_ => service.Matches(published.Snapshot)));
            Assert.True(await matching.WaitAsync(TimeSpan.FromSeconds(2)));
            await Task.Delay(150);
            var health = service.ReadHealth();
            Assert.True(health.Unhealthy);
            Assert.Equal(DiagnosticSinkState.Unavailable, health.Safe!.State);
            Assert.Equal(DiagnosticSinkState.Unavailable, health.Protected!.State);
            Assert.Equal(1, health.Safe.PhysicalCalls); Assert.Equal(1, health.Protected.PhysicalCalls);
            Assert.Equal(2, Volatile.Read(ref blockedCalls));
            Assert.True(service.Matches(published.Snapshot)); // Alarm Policy governs ongoing sink failures.
            Volatile.Write(ref armed, 0); release.Set();
            await service.DisposeAsync();
            Assert.False(service.Matches(published.Snapshot));
        }
        finally
        {
            Volatile.Write(ref armed, 0); release.Set();
            if (service is not null) await service.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact, Trait("VerificationId", "V156_R01")]
    public async Task V156_R01_ExplicitPolicyAndActualInstallationEnableSeparateAuthorizedHistoryWithoutAuditWrites()
    {
        await using var fixture = await TraceStoragePolicyRuntimeTests.CreateFixtureAsync();
        var command = await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 0,
            TraceStoragePolicyRuntimeTests.Policy(days: 1, minimumReserveBytes: 1024, minimumReservePercent: .001m));
        var published = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(command);
        Assert.True(published.Succeeded, published.Outcome.ReasonCode); await fixture.WaitForVerifiedAsync();
        var root = Path.Combine(Path.GetTempPath(), "SharpInspect-T56-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = DiagnosticPipelineTests.Policy(traceHash: published.Snapshot!.ContentHash, traceVersion: published.Snapshot.Policy.Version);
            var safe = Local(Path.Combine(root, "safe"), false, policy.SafeFiles);
            var protectedStore = Local(Path.Combine(root, "protected"), true, policy.ProtectedFiles);
            var logging = new LoggingDiagnosticsOptions(policy, safe, DiagnosticDirectoryInstallation.Install(safe),
                protectedStore, DiagnosticDirectoryInstallation.Install(protectedStore));
            var options = new ProductionStoreOptions(fixture.Options.DatabasePath)
            {
                AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy, LocalIdentity = fixture.Options.LocalIdentity,
                RecipeDrafts = fixture.Options.RecipeDrafts, TraceStoragePolicies = fixture.Options.TraceStoragePolicies,
                LoggingDiagnostics = logging, QueryTimeout = TimeSpan.FromSeconds(3)
            };
            await using var service = new RuntimeDiagnosticService(options, Guid.NewGuid(), Task.CompletedTask, fixture.Authorization);
            await service.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(service.ReadHealth().Configured, service.ReadHealth().ReasonCode);
            Assert.True(service.Matches(published.Snapshot));
            Assert.False(logging.VerifyInstallation(options.DatabasePath, safe.Directory));
            Assert.False(logging.VerifyInstallation(options.DatabasePath, Path.Combine(protectedStore.Directory, "evidence")));
            var before = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
            service.Pipeline!.ObserveException(new InvalidOperationException("SECRET-BAIT"), "ManagedHost");
            Assert.True(await service.Pipeline.FlushAsync());
            var safePage = await service.ReadAsync(new(false, 10, 4096, fixture.Invocation()));
            Assert.True(safePage.Available, safePage.ReasonCode);
            Assert.DoesNotContain(Assert.Single(safePage.Records).Properties, property => property.Name == "HResult");
            var protectedPage = await service.ReadAsync(new(true, 10, 4096, fixture.Invocation()));
            Assert.True(protectedPage.Available, protectedPage.ReasonCode);
            Assert.Equal("HResult", Assert.Single(Assert.Single(protectedPage.Records).Properties).Name);
            var forged = await service.ReadAsync(new(true, 10, 4096, new(CommandSource.PhysicalConsole, Guid.NewGuid().ToString("D"), Guid.NewGuid())));
            Assert.False(forged.Available); Assert.Empty(forged.Records);
            Assert.False((await service.ReadAsync(new(false, 1001, 4096, fixture.Invocation()))).Available);
            Assert.Equal(before, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact, Trait("VerificationId", "V156_R02")]
    public async Task V156_R02_QueryDoesNotHoldASessionMonitorAcrossIoAndRechecksLogoutBeforeReturning()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<DiagnosticHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = fixture.Invocation();
        var operation = fixture.Authorization.ReadDiagnosticHistoryAsync(new(false, 10, 4096, invocation),
            async _ => { entered.SetResult(true); return await finish.Task.ConfigureAwait(false); }, CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var logout = await fixture.Sessions.LogoutAsync(invocation.SessionId).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(logout.Succeeded, logout.ReasonCode);
            finish.SetResult(new(true, "Available", Array.Empty<DiagnosticRecord>(), 0));
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(result.Available); Assert.Empty(result.Records);
        }
        finally { finish.TrySetResult(new(false, "Stopped", Array.Empty<DiagnosticRecord>(), 0)); }
    }

    [Fact, Trait("VerificationId", "V156_R03")]
    public async Task V156_R03_MissingPermissionNeverInvokesPhysicalHistoryReader()
    {
        var roles = RecipeDraftTestPolicies.Authoring.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Value.Where(permission => permission != Permission.RunDiagnostics));
        var policy = new AuthorizationPolicy("diagnostic-denial-test", "1", roles, RecipeDraftTestPolicies.Authoring.StepUpPermissions);
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(authorizationPolicy: policy);
        var calls = 0;
        var result = await fixture.Authorization.ReadDiagnosticHistoryAsync(new(false, 10, 4096, fixture.Invocation()),
            _ => { calls++; return ValueTask.FromResult(new DiagnosticHistoryPage(true, "Available", Array.Empty<DiagnosticRecord>(), 0)); }, CancellationToken.None);
        Assert.False(result.Available); Assert.Equal("PermissionDenied", result.ReasonCode); Assert.Equal(0, calls);
    }

    [Fact, Trait("VerificationId", "V156_R04")]
    public async Task V156_R04_MissingOrMismatchedPolicyRemainsUnconfigured()
    {
        await using var missing = new RuntimeDiagnosticService(null, Guid.NewGuid(), Task.CompletedTask, null);
        Assert.False(missing.ReadHealth().Configured); Assert.False(missing.Matches(null));
        Assert.False((await missing.ReadAsync(new(false, 10, 4096, new(CommandSource.PhysicalConsole)))).Available);
        var trace = new TraceStoragePolicySnapshot(new TraceStoragePolicyPublication(1,
            TraceStoragePolicyRuntimeTests.Policy(days: 30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            Guid.NewGuid(), DateTimeOffset.UtcNow, null));
        var policy = DiagnosticPipelineTests.Policy(traceHash: trace.ContentHash, traceVersion: trace.Policy.Version);
        var options = new LoggingDiagnosticsOptions(policy, Local(@"F:\t56-policy-safe", false, policy.SafeFiles), new string('A', 64),
            Local(@"F:\t56-policy-protected", true, policy.ProtectedFiles), new string('B', 64));
        Assert.False(options.Matches(trace)); // executable retention may not shorten the Trace minimum
        Assert.False(options.VerifyInstallation(@"F:\t56-policy-db\trace.db"));
    }

    private static DiagnosticLocalStoreOptions Local(string path, bool protectedChannel, DiagnosticFileBudget budget) =>
        new(path, protectedChannel, budget.MaximumRecordBytes, budget.MaximumFileBytes, budget.MaximumFiles,
            budget.MaximumTotalBytes, budget.RollAfter, budget.Retention);
}
