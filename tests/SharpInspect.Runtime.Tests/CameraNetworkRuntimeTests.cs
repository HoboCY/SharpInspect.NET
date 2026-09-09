using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused Runtime tests for the governed camera-network maintenance seam.  The
/// doubles deliberately expose completion points for provider calls so these
/// tests prove ownership and bounded caller behavior without touching a NIC.
/// </summary>
public sealed class CameraNetworkRuntimeTests
{
    [Fact]
    public async Task V120_R01_SuccessAdmitsBeforeApplyAndReprovesIdentityReadback()
    {
        await using var harness = Create();

        var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal("CameraNetworkChangedRecipeActivationRequired", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(CameraNetworkMaintenanceState.Succeeded, result.Snapshot!.State);
        Assert.Equal(harness.Target, result.Snapshot!.Target);
        Assert.Equal(harness.Requested, result.Snapshot!.Requested);
        Assert.Equal(harness.Requested, result.Snapshot!.Observed);
        Assert.True(result.Snapshot!.IdentityVerified);
        Assert.Equal(1, harness.NetworkProvider!.BeginCalls);
        Assert.Equal(1, harness.NetworkProvider!.DiscoverCalls);
        Assert.Equal(2, harness.Session!.ReadCalls);
        Assert.Equal(1, harness.Session!.ApplyCalls);
        Assert.Equal(1, harness.Persistence.AdmissionCalls);
        Assert.Equal(1, harness.Persistence.TerminalCalls);
        Assert.True(harness.Log.IndexOf("admission") < harness.Log.IndexOf("session.apply"));
        Assert.Equal(0, harness.SetupPublishCount);
        Assert.False(harness.Station.Ready);
        Assert.Equal(1, harness.ReleaseCount);
    }

    [Fact]
    public async Task V120_R02_StationAndRegisteredOwnerBarriersRejectBeforeBegin()
    {
        await AssertRejectedBeforeBeginAsync(
            station => station with { Ready = true },
            "CameraNetworkRequiresDisarmedStation");
        await AssertRejectedBeforeBeginAsync(
            station => station with { Busy = true },
            "CameraNetworkInspectionConflict");
        await AssertRejectedBeforeBeginAsync(
            station => station with { EvidencePendingDeliveries = 1 },
            "CameraNetworkDeliveryConflict");

        await using var owner = Create();
        owner.ReserveReason = "CameraProductionOwnerRegistered";
        var result = await owner.Runtime.ChangeNetworkConfigurationAsync(owner.NewRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("CameraProductionOwnerRegistered", result.ReasonCode);
        Assert.Equal(0, owner.NetworkProvider!.BeginCalls);
        Assert.Equal(1, owner.ReserveCount);
    }

    [Fact]
    public async Task V120_R03_UnsupportedProviderIsRejectedBeforeAnyNetworkBegin()
    {
        var provider = new FakeCoreProvider(ProviderIdentity());
        await using var harness = Create(provider);

        var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("CameraNetworkMaintenanceUnsupported", result.ReasonCode);
        Assert.Equal(0, provider.OpenCalls);
        Assert.Equal(0, provider.DiscoverCalls);
        Assert.Equal(0, harness.ReserveCount);
    }

    [Fact]
    public async Task V120_R04_ConflictIsAdmittedButApplyNeverRuns()
    {
        await using var harness = Create();
        var session = harness.Session!;
        session.ConflictResult = new(CameraNetworkConflictState.Conflict,
            "CameraNetworkAddressConflict");

        var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("CameraNetworkAddressConflict", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Equal(1, harness.Persistence.AdmissionCalls);
        Assert.Equal(1, session.ConflictCalls);
        Assert.Equal(0, session.ApplyCalls);
        Assert.Equal(CameraNetworkMaintenanceState.Failed, result.Snapshot!.State);
        Assert.False(result.Snapshot!.IdentityVerified);
        Assert.Equal(harness.Previous, result.Snapshot!.Observed);
        Assert.Equal(0, harness.NetworkProvider!.DiscoverCalls);
    }

    [Fact]
    public async Task V120_R05_WrongRediscoveryReadbackOrSessionIdentityBecomesUnknown()
    {
        await using (var rediscovery = Create())
        {
            rediscovery.NetworkProvider!.DiscoverHandler = _ => Task.FromResult(
                CameraDiscoveryResult.Success(new[] {
                    new CameraDeviceDescriptor(rediscovery.Target.Provider, "Serial:Other", "other")
                }));

            var result = await rediscovery.Runtime.ChangeNetworkConfigurationAsync(
                rediscovery.NewRequest());

            Assert.False(result.Succeeded);
            Assert.Equal("CameraNetworkRediscoveryIdentityMismatch", result.ReasonCode);
            Assert.Equal(CameraNetworkMaintenanceState.Unknown, result.Snapshot!.State);
            Assert.False(result.Snapshot!.IdentityVerified);
            Assert.Equal(1, rediscovery.Session!.ApplyCalls);
        }

        await using (var readback = Create())
        {
            readback.Session!.ReadTargetAfterFirst =
                new CameraBindingTarget(readback.Target.Provider, "Serial:ReadbackOther");

            var result = await readback.Runtime.ChangeNetworkConfigurationAsync(
                readback.NewRequest());

            Assert.False(result.Succeeded);
            Assert.Equal("CameraNetworkReadbackIdentityMismatch", result.ReasonCode);
            Assert.Equal(CameraNetworkMaintenanceState.Unknown, result.Snapshot!.State);
            Assert.False(result.Snapshot!.IdentityVerified);
            Assert.Equal(2, readback.Session!.ReadCalls);
        }

        await using (var previous = Create())
        {
            var wrongTarget = new CameraBindingTarget(previous.Target.Provider, "Serial:PreviousOther");
            var wrongSession = new FakeNetworkSession(wrongTarget, previous.Previous)
            {
                Log = previous.Log
            };
            previous.NetworkProvider!.Session = wrongSession;

            var result = await previous.Runtime.ChangeNetworkConfigurationAsync(
                previous.NewRequest());

            Assert.False(result.Succeeded);
            Assert.Equal("CameraNetworkIdentityMismatch", result.ReasonCode);
            Assert.Equal(CameraNetworkMaintenanceState.Failed, result.Snapshot!.State);
            Assert.Equal(0, wrongSession.ReadCalls);
            Assert.Equal(0, wrongSession.ApplyCalls);
        }
    }

    [Fact]
    public async Task V120_R06_ApplyFailureAfterPhysicalChangeRetainsObservedValue()
    {
        await using var harness = Create();
        var session = harness.Session!;
        session.ApplyHandler = (_, requested, _) =>
        {
            session.Current = requested;
            return Task.FromResult(new CameraNetworkApplyResult(false,
                "CameraNetworkApplyFailed"));
        };

        var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("CameraNetworkApplyFailed", result.ReasonCode);
        Assert.Equal(CameraNetworkMaintenanceState.Failed, result.Snapshot!.State);
        Assert.True(result.Snapshot!.IdentityVerified);
        Assert.Equal(harness.Requested, result.Snapshot!.Observed);
        Assert.Equal(1, session.ApplyCalls);
        Assert.Equal(2, session.ReadCalls);
    }

    [Fact]
    public async Task V120_R07_ApplyTimeoutRetainsSessionAndBlocksSecondOperationUntilCompletion()
    {
        await using var harness = Create(operationTimeout: TimeSpan.FromMilliseconds(150));
        var apply = new TaskCompletionSource<CameraNetworkApplyResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = harness.Session!;
        session.ApplyHandler = (_, _, _) => apply.Task;

        var firstTask = harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest()).AsTask();
        await session.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = await firstTask;

        Assert.False(first.Succeeded);
        Assert.Equal("CameraNetworkOperationTimeout", first.ReasonCode);
        Assert.Equal(CameraNetworkMaintenanceState.Unknown, first.Snapshot!.State);
        Assert.True(session.ApplyStarted.Task.IsCompleted);
        Assert.Equal(0, session.DisposeCalls);

        var second = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(second.Succeeded);
        Assert.Equal("CameraNetworkMaintenanceInProgress", second.ReasonCode);
        Assert.Equal(1, harness.NetworkProvider!.BeginCalls);

        apply.TrySetResult(new CameraNetworkApplyResult(true, "CameraNetworkApplied"));
        await EventuallyAsync(() => session.DisposeCalls == 1 && harness.ReleaseCount == 1);
    }

    [Fact]
    public async Task V120_R08_DisposeTimeoutRetainsStationReservationAndSetupGate()
    {
        await using var harness = Create(operationTimeout: TimeSpan.FromMilliseconds(150));
        var dispose = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = harness.Session!;
        session.DisposeHandler = () => dispose.Task;

        var firstTask = harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest()).AsTask();
        await session.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = await firstTask;

        Assert.False(first.Succeeded);
        Assert.Equal("CameraNetworkOperationTimeout", first.ReasonCode);
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(0, harness.ReleaseCount);

        var second = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(second.Succeeded);
        Assert.Equal("CameraNetworkMaintenanceInProgress", second.ReasonCode);
        Assert.Equal(1, harness.NetworkProvider!.BeginCalls);

        dispose.TrySetResult(true);
        await EventuallyAsync(() => harness.ReleaseCount == 1);
    }

    [Fact]
    public async Task V120_R09_BeginTimeoutDisposesLateSessionWithoutReadOrApply()
    {
        await using var harness = Create(operationTimeout: TimeSpan.FromMilliseconds(150));
        var begin = new TaskCompletionSource<CameraNetworkMaintenanceLeaseResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = harness.NetworkProvider!;
        provider.BeginHandler = _ => begin.Task;

        var resultTask = harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest()).AsTask();
        await provider.BeginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await resultTask;

        Assert.False(result.Succeeded);
        Assert.Equal("CameraNetworkOperationTimeout", result.ReasonCode);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(0, harness.ReleaseCount);

        var lateSession = new FakeNetworkSession(harness.Target, harness.Previous)
        {
            Log = harness.Log
        };
        begin.TrySetResult(CameraNetworkMaintenanceLeaseResult.Success(lateSession));

        await EventuallyAsync(() => lateSession.DisposeCalls == 1 && harness.ReleaseCount == 1);
        Assert.Equal(0, lateSession.ReadCalls);
        Assert.Equal(0, lateSession.ApplyCalls);
    }

    [Fact]
    public async Task V120_R10_TerminalWriteFailureProducesUnknownAndGlobalReconciliationBarrier()
    {
        await using var harness = Create();
        harness.Persistence.TerminalCommitted = false;

        var first = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(first.Succeeded);
        Assert.Equal("CameraNetworkAuditUnavailable", first.ReasonCode);
        Assert.Equal(AuditPersistence.Unavailable, first.Audit);
        Assert.Equal(CameraNetworkMaintenanceState.Unknown, first.Snapshot!.State);
        Assert.Equal(1, harness.Persistence.TerminalCalls);

        var second = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(second.Succeeded);
        Assert.Equal("CameraNetworkReconciliationRequired", second.ReasonCode);
        Assert.Equal(1, harness.NetworkProvider!.BeginCalls);
        Assert.Equal(1, harness.Persistence.RejectedCalls);
    }

    [Fact]
    public async Task V120_R11_RestartUnresolvedBlocksNetworkRebindAndApply()
    {
        await using var harness = Create();
        harness.Persistence.HasUnresolved = true;

        var network = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(network.Succeeded);
        Assert.Equal("CameraNetworkReconciliationRequired", network.ReasonCode);
        Assert.Equal(0, harness.NetworkProvider!.BeginCalls);

        var rebind = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.NewInvocation(), "Primary", 0, null,
            harness.Target, "restart-rebind"));
        var apply = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(),
                "Primary", 0, null, SampleConfiguration(), "restart-apply"));

        Assert.False(rebind.Succeeded);
        Assert.Equal("CameraNetworkReconciliationRequired", rebind.ReasonCode);
        Assert.False(apply.Succeeded);
        Assert.Equal("CameraNetworkReconciliationRequired", apply.ReasonCode);
        Assert.Equal(0, harness.NetworkProvider!.OpenCalls);
    }

    [Fact]
    public async Task V120_R12_UnauthorizedAndInvalidStationAddressesRejectBeforeBegin()
    {
        var unauthorized = new FakeAuthorizer(Guid.NewGuid(), Guid.NewGuid(), 4)
        {
            AllowMutations = false,
            DenialReason = "PermissionDenied"
        };
        await using (var harness = Create(authorizer: unauthorized))
        {
            var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

            Assert.False(result.Succeeded);
            Assert.Equal("PermissionDenied", result.ReasonCode);
            Assert.Equal(0, harness.NetworkProvider!.BeginCalls);
        }

        await using (var mismatch = Create())
        {
            var result = await mismatch.Runtime.ChangeNetworkConfigurationAsync(mismatch.NewRequest(
                new CameraIpv4Configuration("192.168.5.20", 24)));

            Assert.False(result.Succeeded);
            Assert.Equal("CameraStationNetworkMismatch", result.ReasonCode);
            Assert.Equal(0, mismatch.NetworkProvider!.BeginCalls);
        }

        await using (var self = Create())
        {
            var result = await self.Runtime.ChangeNetworkConfigurationAsync(self.NewRequest(
                new CameraIpv4Configuration("192.168.4.2", 24)));

            Assert.False(result.Succeeded);
            Assert.Equal("CameraNetworkStationAddressConflict", result.ReasonCode);
            Assert.Equal(0, self.NetworkProvider!.BeginCalls);
        }
    }

    private static async Task AssertRejectedBeforeBeginAsync(
        Func<CameraSetupRuntime.CameraStationContext, CameraSetupRuntime.CameraStationContext> mutate,
        string reason)
    {
        await using var harness = Create(stationMutator: mutate);
        var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(0, harness.NetworkProvider!.BeginCalls);
        Assert.Equal(0, harness.Persistence.AdmissionCalls);
    }

    [Fact]
    public async Task V120_R13_SlowSdkCancellationCallbackCannotBlockDeadlineOrRaceSessionCleanup()
    {
        await using var harness = Create(operationTimeout: TimeSpan.FromMilliseconds(150));
        var callbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyRelease = new TaskCompletionSource<CameraNetworkApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = harness.Session!;
        CancellationTokenRegistration registration = default;
        session.ApplyHandler = (_, _, token) =>
        {
            registration = token.Register(() =>
            {
                callbackStarted.TrySetResult(true);
                callbackRelease.Task.GetAwaiter().GetResult();
            });
            return applyRelease.Task;
        };
        try
        {
            var call = harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest()).AsTask();
            await session.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var returned = await call.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(CameraNetworkMaintenanceState.Unknown, returned.Snapshot!.State);
            Assert.Equal(AuditPersistence.NotAttempted, returned.Audit);
            Assert.Equal(0, session.DisposeCalls);
            Assert.Equal(0, harness.ReleaseCount);

            applyRelease.TrySetResult(new(true, "CameraNetworkApplied"));
            await Task.Delay(100);
            Assert.Equal(0, session.DisposeCalls);
            Assert.Equal(0, harness.ReleaseCount);
            var busy = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());
            Assert.Equal("CameraNetworkMaintenanceInProgress", busy.ReasonCode);
            callbackRelease.TrySetResult(true);
            await EventuallyAsync(() => session.DisposeCalls == 1 && harness.ReleaseCount == 1);
        }
        finally
        {
            applyRelease.TrySetResult(new(true, "CameraNetworkApplied"));
            callbackRelease.TrySetResult(true);
            registration.Dispose();
        }
    }

    [Fact]
    public async Task V120_R14_EarlyRejectionsCannotPublishStationDisarmOrRecipeInvalidation()
    {
        var denied = new FakeAuthorizer(Guid.NewGuid(), Guid.NewGuid(), 1) { AllowMutations = false };
        await using (var harness = Create(authorizer: denied,
            stationMutator: state => state with { Ready = true, ArmState = ProductionArmState.Armed }))
        {
            var before = harness.Station;
            var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());
            Assert.False(result.Succeeded);
            Assert.Equal("PermissionDenied", result.ReasonCode);
            Assert.Equal(0, harness.ReserveCount);
            Assert.Equal(0, harness.Snapshots.Count);
            Assert.Equal(before, harness.Station);
        }
        await using (var harness = Create(stationMutator: state => state with { Ready = true }))
        {
            var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());
            Assert.False(result.Succeeded);
            Assert.Equal("CameraNetworkRequiresDisarmedStation", result.ReasonCode);
            Assert.Equal(0, harness.ReserveCount);
            Assert.Equal(0, harness.Snapshots.Count);
        }
        await using (var harness = Create())
        {
            harness.Session!.ConflictResult = new(CameraNetworkConflictState.Conflict, "CameraNetworkAddressConflict");
            var result = await harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest());
            Assert.False(result.Succeeded);
            Assert.Equal(0, harness.Session.ApplyCalls);
            Assert.Equal(0, harness.Snapshots.Count);
        }
    }

    [Fact]
    public async Task V120_R15_ShutdownDoesNotRunBlockingSdkCancellationOnItsCaller()
    {
        await using var harness = Create(operationTimeout: TimeSpan.FromSeconds(5));
        var callbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyRelease = new TaskCompletionSource<CameraNetworkApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = harness.Session!;
        CancellationTokenRegistration registration = default;
        session.ApplyHandler = (_, _, token) =>
        {
            registration = token.Register(() =>
            {
                callbackStarted.TrySetResult(true);
                callbackRelease.Task.GetAwaiter().GetResult();
            });
            return applyRelease.Task;
        };
        try
        {
            var call = harness.Runtime.ChangeNetworkConfigurationAsync(harness.NewRequest()).AsTask();
            await session.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var shutdown = Task.Run(() => harness.Runtime.DisposeAsync().AsTask());
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, session.DisposeCalls);
            Assert.Equal(0, harness.ReleaseCount);
            applyRelease.TrySetResult(new(true, "CameraNetworkApplied"));
            await Task.Delay(100);
            Assert.Equal(0, session.DisposeCalls);
            callbackRelease.TrySetResult(true);
            await EventuallyAsync(() => session.DisposeCalls == 1 && harness.ReleaseCount == 1);
            Assert.False((await call.WaitAsync(TimeSpan.FromSeconds(2))).Succeeded);
        }
        finally
        {
            applyRelease.TrySetResult(new(true, "CameraNetworkApplied"));
            callbackRelease.TrySetResult(true);
            registration.Dispose();
        }
    }

    [Fact]
    public async Task V120_R16_CallerTimeoutCannotOverwriteAnAlreadyPublishedDurableTerminal()
    {
        await using var harness = Create(operationTimeout: TimeSpan.FromMilliseconds(150));
        var terminalPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Snapshots.OnAdded = snapshot =>
        {
            if (snapshot.State != CameraNetworkMaintenanceState.Succeeded) return;
            terminalPublished.TrySetResult(true);
            releasePublication.Task.GetAwaiter().GetResult();
        };
        try
        {
            var request = harness.NewRequest();
            var call = harness.Runtime.ChangeNetworkConfigurationAsync(request).AsTask();
            await terminalPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var timedOut = await call.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(CameraNetworkMaintenanceState.Unknown, timedOut.Snapshot!.State);
            Assert.Equal(AuditPersistence.NotAttempted, timedOut.Audit);
            var observed = await harness.Runtime.GetNetworkMaintenanceAsync(harness.Target, request.Invocation);
            Assert.Equal(CameraNetworkMaintenanceState.Succeeded, observed.Snapshot!.State);
            Assert.Equal(harness.Persistence.LastTerminalSnapshot, observed.Snapshot);
        }
        finally { releasePublication.TrySetResult(true); }
        await EventuallyAsync(() => harness.ReleaseCount == 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V120_R17_QueryCapacityCoversAuthorizationAndRetainsActualReadAfterDeadline(bool blockAuthorization)
    {
        await using var harness = Create(operationTimeout: TimeSpan.FromMilliseconds(500));
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (blockAuthorization)
            harness.Authorizer.ReadHandler = async _ =>
            {
                await release.Task;
                return new(true, "Authorized", harness.Authorizer.PrincipalId,
                    harness.Authorizer.SessionId, harness.Authorizer.Revision, null);
            };
        else
            harness.Persistence.ReadHandler = async _ => { await release.Task; return null; };
        var calls = new List<Task<CameraNetworkQueryResult>>();
        try
        {
            for (var index = 0; index < 16; index++)
                calls.Add(harness.Runtime.GetNetworkMaintenanceAsync(harness.Target, harness.NewInvocation()).AsTask());
            await EventuallyAsync(() => (blockAuthorization ? harness.Authorizer.ReadCalls : harness.Persistence.ReadCalls) >= 4);
            var excess = await harness.Runtime.GetNetworkMaintenanceAsync(harness.Target, harness.NewInvocation())
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.Equal("CameraNetworkQueryCapacityExceeded", excess.ReasonCode);
            var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.All(results, result => Assert.Equal("CameraNetworkQueryDeadlineExceeded", result.ReasonCode));
            Assert.Equal(4, harness.Authorizer.ReadCalls);
            Assert.Equal(blockAuthorization ? 0 : 4, harness.Persistence.ReadCalls);
            // The four actual calls ignore cancellation. A later request must
            // wait for their real completion instead of opening a fifth read.
            var queued = await harness.Runtime.GetNetworkMaintenanceAsync(harness.Target, harness.NewInvocation());
            Assert.Contains(queued.ReasonCode, new[]
                { "CameraNetworkQueryDeadlineExceeded", "CameraNetworkQueryCapacityExceeded" });
            Assert.Equal(4, harness.Authorizer.ReadCalls);
        }
        finally { release.TrySetResult(true); }
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(2));
        // The public calls above may have returned their caller deadline while
        // the owned workers are still retiring.  Observe recovery within one
        // fixed bound instead of assuming that Task.WhenAll(calls) includes
        // those internal workers.  Capacity/deadline results are the only
        // expected transient states while their permits drain.
        var recoveryDeadline = Stopwatch.StartNew();
        CameraNetworkQueryResult recovered;
        while (true)
        {
            recovered = await harness.Runtime.GetNetworkMaintenanceAsync(
                harness.Target, harness.NewInvocation());
            if (recovered.Available) break;
            Assert.Contains(recovered.ReasonCode, new[]
                { "CameraNetworkQueryCapacityExceeded", "CameraNetworkQueryDeadlineExceeded" });
            if (recoveryDeadline.Elapsed >= TimeSpan.FromSeconds(2)) break;
            await Task.Delay(10);
        }
        Assert.True(recovered.Available, recovered.ReasonCode);
        Assert.Equal("CameraNetworkMaintenanceNotRecorded", recovered.ReasonCode);
    }

    private static NetworkHarness Create(ICameraProvider? provider = null,
        FakeAuthorizer? authorizer = null,
        Func<CameraSetupRuntime.CameraStationContext, CameraSetupRuntime.CameraStationContext>? stationMutator = null,
        TimeSpan? operationTimeout = null)
    {
        var identity = ProviderIdentity();
        var target = new CameraBindingTarget(identity, "Serial:One");
        var effectiveProvider = provider ?? new FakeNetworkProvider(identity);
        var networkProvider = effectiveProvider as FakeNetworkProvider;
        var previous = PreviousConfiguration();
        if (networkProvider is not null)
            networkProvider.Session = new FakeNetworkSession(target, previous);

        var effectiveAuthorizer = authorizer ??
            new FakeAuthorizer(Guid.NewGuid(), Guid.NewGuid(), revision: 4);
        var persistence = new FakeNetworkPersistence();
        var log = new BoundedCallLog();
        var counters = new CallbackCounters();
        var stationBox = new StationBox(DefaultStation());
        if (stationMutator is not null) stationBox.Value = stationMutator(stationBox.Value);
        var reserveReason = new TextBox();
        var snapshots = new BoundedSnapshotLog();

        if (networkProvider is not null)
        {
            networkProvider.Log = log;
            networkProvider.Session!.Log = log;
        }
        persistence.Log = log;

        var options = new CameraSetupOptions
        {
            OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(1),
            ShutdownTimeout = TimeSpan.FromSeconds(1),
            StationNetwork = StationNetwork()
        };
        var runtime = new CameraSetupRuntime(new[] { effectiveProvider }, options,
            audit: null, sessions: new FakeSessions(new InteractiveSession(InteractiveSessionState.Authenticated,
                effectiveAuthorizer.PrincipalId.ToString("D"), effectiveAuthorizer.SessionId)),
            identityQuery: new FakeIdentityQuery(),
            readStation: () => stationBox.Value,
            publishSetup: (_, _) => Interlocked.Increment(ref counters.SetupPublish),
            cameraAuthorizer: effectiveAuthorizer,
            persistence: null,
            networkPersistence: persistence);
        runtime.ConfigureNetworkMaintenance(
            _ =>
            {
                Interlocked.Increment(ref counters.Reserve);
                return ValueTask.FromResult<string?>(reserveReason.Value);
            },
            () => Interlocked.Increment(ref counters.Release),
            snapshots.Add);

        return new NetworkHarness(runtime, effectiveProvider, networkProvider, effectiveAuthorizer,
            persistence, target, previous, stationBox, reserveReason, counters, log, snapshots);
    }

    private static CameraSetupRuntime.CameraStationContext DefaultStation() =>
        new(Guid.NewGuid(), false, ProductionArmState.Disarmed, false, null, 0,
            HandshakePhase.Idle, ExclusiveMode.None, RecoveryState.None, false,
            null, false, false);

    private static CameraStationNetwork StationNetwork() =>
        new("station-eth0", new CameraIpv4Configuration("192.168.4.2", 24));

    private static CameraProviderIdentity ProviderIdentity() =>
        new("Test.Camera.Provider", "1", "Test.Camera.Package", "1");

    private static CameraIpv4Configuration PreviousConfiguration() =>
        new("192.168.4.10", 24, "192.168.4.1");

    private static RequestedCameraConfiguration SampleConfiguration() =>
        new(ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
            new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8,
            validBits: null, acquisitionTimeoutMs: 100, triggerDelayUs: 0,
            whiteBalanceRgb: null);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(2))
                throw new TimeoutException("network operation did not release its owned resources");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private sealed class NetworkHarness : IAsyncDisposable
    {
        private readonly StationBox _station;
        private readonly TextBox _reserveReason;
        private readonly CallbackCounters _counters;

        internal NetworkHarness(CameraSetupRuntime runtime, ICameraProvider provider,
            FakeNetworkProvider? networkProvider, FakeAuthorizer authorizer,
            FakeNetworkPersistence persistence, CameraBindingTarget target,
            CameraIpv4Configuration previous, StationBox station, TextBox reserveReason,
            CallbackCounters counters, BoundedCallLog log, BoundedSnapshotLog snapshots)
        {
            Runtime = runtime;
            Provider = provider;
            NetworkProvider = networkProvider;
            Authorizer = authorizer;
            Persistence = persistence;
            Target = target;
            Previous = previous;
            Requested = new CameraIpv4Configuration("192.168.4.20", 24);
            _station = station;
            _reserveReason = reserveReason;
            _counters = counters;
            Log = log;
            Snapshots = snapshots;
        }

        internal CameraSetupRuntime Runtime { get; }
        internal ICameraProvider Provider { get; }
        internal FakeNetworkProvider? NetworkProvider { get; }
        internal FakeNetworkSession? Session => NetworkProvider?.Session;
        internal FakeAuthorizer Authorizer { get; }
        internal FakeNetworkPersistence Persistence { get; }
        internal CameraBindingTarget Target { get; }
        internal CameraIpv4Configuration Previous { get; }
        internal CameraIpv4Configuration Requested { get; }
        internal BoundedCallLog Log { get; }
        internal BoundedSnapshotLog Snapshots { get; }
        internal CameraSetupRuntime.CameraStationContext Station
        {
            get => _station.Value;
            set => _station.Value = value;
        }
        internal string? ReserveReason
        {
            get => _reserveReason.Value;
            set => _reserveReason.Value = value;
        }
        internal int ReserveCount => Volatile.Read(ref _counters.Reserve);
        internal int ReleaseCount => Volatile.Read(ref _counters.Release);
        internal int SetupPublishCount => Volatile.Read(ref _counters.SetupPublish);

        internal CommandInvocation NewInvocation() =>
            new(CommandSource.PhysicalConsole, Authorizer.PrincipalId.ToString("D"),
                Authorizer.SessionId);

        internal CameraNetworkChangeRequest NewRequest(
            CameraIpv4Configuration? requested = null, CameraBindingTarget? target = null) =>
            new(Guid.NewGuid(), NewInvocation(), target ?? Target, requested ?? Requested,
                "network-runtime-test");

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class FakeNetworkProvider : ICameraProvider, ICameraNetworkConfigurator
    {
        private int _beginCalls;
        private int _discoverCalls;
        private int _openCalls;

        internal FakeNetworkProvider(CameraProviderIdentity identity) => Identity = identity;

        public CameraProviderIdentity Identity { get; }
        internal FakeNetworkSession? Session { get; set; }
        internal BoundedCallLog? Log { get; set; }
        internal TaskCompletionSource<bool> BeginStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<CancellationToken, Task<CameraNetworkMaintenanceLeaseResult>>? BeginHandler { get; set; }
        internal Func<CancellationToken, Task<CameraDiscoveryResult>>? DiscoverHandler { get; set; }
        internal int BeginCalls => Volatile.Read(ref _beginCalls);
        internal int DiscoverCalls => Volatile.Read(ref _discoverCalls);
        internal int OpenCalls => Volatile.Read(ref _openCalls);

        public ValueTask<CameraNetworkMaintenanceLeaseResult> TryBeginMaintenanceAsync(
            string stableDeviceIdentity, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _beginCalls);
            Log?.Add("provider.begin");
            BeginStarted.TrySetResult(true);
            var handler = BeginHandler;
            if (handler is not null)
                return new ValueTask<CameraNetworkMaintenanceLeaseResult>(handler(cancellationToken));
            var session = Session;
            return ValueTask.FromResult(session is null
                ? CameraNetworkMaintenanceLeaseResult.Failure("CameraNetworkMaintenanceUnavailable")
                : CameraNetworkMaintenanceLeaseResult.Success(session));
        }

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _discoverCalls);
            Log?.Add("provider.discover");
            var handler = DiscoverHandler;
            if (handler is not null)
                return new ValueTask<CameraDiscoveryResult>(handler(cancellationToken));
            return ValueTask.FromResult(CameraDiscoveryResult.Success(new[] {
                new CameraDeviceDescriptor(Identity, "Serial:One", "fake-camera")
            }));
        }

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCalls);
            Log?.Add("provider.open");
            return ValueTask.FromResult(CameraOpenResult.Failure("NotUsed"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCoreProvider : ICameraProvider
    {
        private int _openCalls;
        private int _discoverCalls;

        internal FakeCoreProvider(CameraProviderIdentity identity) => Identity = identity;

        public CameraProviderIdentity Identity { get; }
        internal int OpenCalls => Volatile.Read(ref _openCalls);
        internal int DiscoverCalls => Volatile.Read(ref _discoverCalls);

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _discoverCalls);
            return ValueTask.FromResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));
        }

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCalls);
            return ValueTask.FromResult(CameraOpenResult.Failure("NotUsed"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeNetworkSession : ICameraNetworkMaintenanceSession
    {
        private int _readCalls;
        private int _conflictCalls;
        private int _applyCalls;
        private int _disposeCalls;

        internal FakeNetworkSession(CameraBindingTarget target, CameraIpv4Configuration current)
        {
            Target = target;
            Current = current;
        }

        public CameraBindingTarget Target { get; }
        internal CameraIpv4Configuration Current { get; set; }
        internal BoundedCallLog? Log { get; set; }
        internal CameraBindingTarget? ReadTargetAfterFirst { get; set; }
        internal CameraNetworkConflictResult ConflictResult { get; set; } =
            new(CameraNetworkConflictState.Clear, "CameraNetworkConflictClear");
        internal Func<CancellationToken, Task<CameraNetworkReadResult>>? ReadHandler { get; set; }
        internal Func<CameraIpv4Configuration, CameraStationNetwork, CancellationToken,
            Task<CameraNetworkConflictResult>>? ConflictHandler { get; set; }
        internal Func<CameraIpv4Configuration, CameraIpv4Configuration, CancellationToken,
            Task<CameraNetworkApplyResult>>? ApplyHandler { get; set; }
        internal Func<Task>? DisposeHandler { get; set; }
        internal TaskCompletionSource<bool> DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal int ConflictCalls => Volatile.Read(ref _conflictCalls);
        internal int ApplyCalls => Volatile.Read(ref _applyCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public ValueTask<CameraNetworkReadResult> ReadCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _readCalls);
            Log?.Add("session.read");
            var handler = ReadHandler;
            if (handler is not null)
                return new ValueTask<CameraNetworkReadResult>(handler(cancellationToken));
            var target = Target;
            if (call > 1 && ReadTargetAfterFirst is { } readbackTarget)
                target = readbackTarget;
            return ValueTask.FromResult(new CameraNetworkReadResult(true,
                "CameraNetworkReadSucceeded", new CameraNetworkDeviceState(target, Current)));
        }

        public ValueTask<CameraNetworkConflictResult> DetectConflictAsync(
            CameraIpv4Configuration requested, CameraStationNetwork stationNetwork,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _conflictCalls);
            Log?.Add("session.conflict");
            var handler = ConflictHandler;
            if (handler is not null)
                return new ValueTask<CameraNetworkConflictResult>(
                    handler(requested, stationNetwork, cancellationToken));
            return ValueTask.FromResult(ConflictResult);
        }

        public ValueTask<CameraNetworkApplyResult> ApplyAsync(
            CameraIpv4Configuration expectedPrevious, CameraIpv4Configuration requested,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _applyCalls);
            Log?.Add("session.apply");
            ApplyStarted.TrySetResult(true);
            var handler = ApplyHandler;
            if (handler is not null)
                return new ValueTask<CameraNetworkApplyResult>(
                    handler(expectedPrevious, requested, cancellationToken));
            Current = requested;
            return ValueTask.FromResult(new CameraNetworkApplyResult(true,
                "CameraNetworkApplied"));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            Log?.Add("session.dispose");
            DisposeStarted.TrySetResult(true);
            var handler = DisposeHandler;
            return handler is null
                ? ValueTask.CompletedTask
                : new ValueTask(handler());
        }
    }

    private sealed class FakeNetworkPersistence : ICameraNetworkPersistence
    {
        private int _admissionCalls;
        private int _terminalCalls;
        private int _rejectedCalls;
        private int _unresolvedCalls;
        private int _readCalls;

        internal BoundedCallLog? Log { get; set; }
        internal bool AdmissionCommitted { get; set; } = true;
        internal bool TerminalCommitted { get; set; } = true;
        internal bool HasUnresolved { get; set; }
        internal CameraNetworkSnapshot? LastTerminalSnapshot { get; private set; }
        internal int AdmissionCalls => Volatile.Read(ref _admissionCalls);
        internal int TerminalCalls => Volatile.Read(ref _terminalCalls);
        internal int RejectedCalls => Volatile.Read(ref _rejectedCalls);
        internal int UnresolvedCalls => Volatile.Read(ref _unresolvedCalls);
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal Func<CancellationToken, Task<CameraNetworkSnapshot?>>? ReadHandler { get; set; }

        public ValueTask<CameraNetworkSnapshot?> ReadLatestAsync(CameraBindingTarget target,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            return ReadHandler is { } handler ? new(handler(cancellationToken)) :
                ValueTask.FromResult(LastTerminalSnapshot?.Target == target ? LastTerminalSnapshot : null);
        }

        public ValueTask<bool> HasUnresolvedAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _unresolvedCalls);
            Log?.Add("persistence.unresolved");
            return ValueTask.FromResult(HasUnresolved);
        }

        public ValueTask<CameraNetworkAdmissionResult> AppendAdmissionAsync(
            CameraNetworkChangeRequest request, CameraStationNetwork stationNetwork,
            CameraIpv4Configuration actualPrevious, CameraSetupAuthorization authorization,
            Guid runtimeEpoch, StoreDeadline deadline,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _admissionCalls);
            Log?.Add("admission");
            if (!AdmissionCommitted)
                return ValueTask.FromResult(new CameraNetworkAdmissionResult(
                    new StoreWriteResult(false, "CameraNetworkAdmissionWriteFailed"), null));

            var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                runtimeEpoch, DateTimeOffset.UtcNow,
                AuditedCommandKind.ChangeCameraNetworkConfiguration, request.Invocation.Source,
                request.Invocation.PrincipalId, request.Invocation.SessionId,
                request.Invocation.StepUpGrantId, CommandAuditPhase.Outcome,
                CommandDisposition.Accepted, "CameraNetworkChangeAdmitted");
            var admission = new CameraNetworkAdmission(fact, request, stationNetwork,
                actualPrevious, authorization, runtimeEpoch);
            return ValueTask.FromResult(new CameraNetworkAdmissionResult(
                new StoreWriteResult(true, "CameraNetworkAdmissionPersisted", fact), admission));
        }

        public ValueTask<StoreWriteResult> AppendTerminalAsync(CameraNetworkAdmission admission,
            CameraNetworkSnapshot snapshot, StoreDeadline deadline,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _terminalCalls);
            Log?.Add("terminal");
            LastTerminalSnapshot = snapshot;
            return ValueTask.FromResult(TerminalCommitted
                ? new StoreWriteResult(true, "CameraNetworkTerminalPersisted")
                : new StoreWriteResult(false, "CameraNetworkTerminalWriteFailed"));
        }

        public ValueTask<StoreWriteResult> RecordRejectedAsync(CameraNetworkChangeRequest request,
            CameraStationNetwork? stationNetwork, CameraIpv4Configuration? actualPrevious,
            CameraSetupAuthorization? authorization, Guid runtimeEpoch, string reason,
            StoreDeadline deadline, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _rejectedCalls);
            Log?.Add("rejected");
            return ValueTask.FromResult(new StoreWriteResult(true, "CameraNetworkRejectedPersisted"));
        }
    }

    private sealed class FakeAuthorizer : ICameraSetupAuthorizer
    {
        private int _mutationCalls;
        private int _readCalls;

        internal FakeAuthorizer(Guid principalId, Guid sessionId, long revision)
        {
            PrincipalId = principalId;
            SessionId = sessionId;
            Revision = revision;
        }

        internal Guid PrincipalId { get; }
        internal Guid SessionId { get; }
        internal long Revision { get; }
        internal bool AllowMutations { get; set; } = true;
        internal string DenialReason { get; set; } = "PermissionDenied";
        internal int MutationCalls => Volatile.Read(ref _mutationCalls);
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal Func<CancellationToken, Task<CameraSetupAuthorization>>? ReadHandler { get; set; }

        public ValueTask<CameraSetupAuthorization> AuthorizeCameraSetupAsync(
            CommandInvocation invocation, bool readOnly, Guid operationId, string targetId,
            AuditedCommandKind commandKind, CancellationToken cancellationToken = default)
        {
            if (readOnly)
            {
                Interlocked.Increment(ref _readCalls);
                if (ReadHandler is { } handler) return new(handler(cancellationToken));
                return ValueTask.FromResult(new CameraSetupAuthorization(true, "Authorized",
                    PrincipalId, SessionId, Revision, null));
            }
            Interlocked.Increment(ref _mutationCalls);
            if (!AllowMutations)
                return ValueTask.FromResult(CameraSetupAuthorization.Denied(DenialReason));

            var reservation = CameraSetupAuthorizationReservation.Create(
                commit: static () => { }, rollback: static () => { });
            return ValueTask.FromResult(new CameraSetupAuthorization(true, "Authorized",
                PrincipalId, SessionId, Revision, reservation));
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        internal FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed { add { } remove { } }
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeIdentityQuery : IIdentityAdministrationQuery
    {
        public ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BoundedCallLog
    {
        private readonly object _sync = new();
        private readonly string[] _items = new string[64];
        private int _count;

        internal void Add(string item)
        {
            lock (_sync)
            {
                if (_count < _items.Length) _items[_count++] = item;
            }
        }

        internal int IndexOf(string item)
        {
            lock (_sync)
            {
                for (var index = 0; index < _count; index++)
                    if (string.Equals(_items[index], item, StringComparison.Ordinal)) return index;
                return -1;
            }
        }
    }

    private sealed class BoundedSnapshotLog
    {
        private readonly object _sync = new();
        private readonly CameraNetworkSnapshot?[] _items = new CameraNetworkSnapshot?[32];
        private int _count;
        internal int Count { get { lock (_sync) return _count; } }
        internal Action<CameraNetworkSnapshot>? OnAdded { get; set; }

        internal void Add(CameraNetworkSnapshot snapshot)
        {
            lock (_sync)
            {
                if (_count < _items.Length) _items[_count++] = snapshot;
            }
            OnAdded?.Invoke(snapshot);
        }
    }

    private sealed class CallbackCounters
    {
        internal int Reserve;
        internal int Release;
        internal int SetupPublish;
    }

    private sealed class StationBox
    {
        internal StationBox(CameraSetupRuntime.CameraStationContext value) => Value = value;
        internal CameraSetupRuntime.CameraStationContext Value;
    }

    private sealed class TextBox
    {
        internal string? Value;
    }
}
