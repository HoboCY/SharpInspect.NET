using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V132_G07_RealPendingHistoryCannotBeHiddenByPublicQueryOnRestart(bool cameraAvailable)
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var command = await harness.AuthorizedActivationCommand();
        RecipeActivationRecord admitted;
        using (var reservation = await harness.Runtime.ReserveRecipeActivationAsync(command.CorrelationId, CancellationToken.None))
        {
            Assert.True(reservation.Available, reservation.Failure);
            var checks = new RecipeActivationChecks();
            checks.Observe(2, true, "RecipeActivationQuiescenceReserved");
            var admission = await harness.Authorization.AdmitRecipeActivationAsync(command, reservation.RuntimeEpoch,
                Guid.NewGuid(), RecipeActivationEvidenceKind.LocalAuthority, checks.Snapshot(), null,
                reservation.GetBlocker, new StoreDeadline(harness.Options.CommitTimeout), CancellationToken.None);
            Assert.Equal(AuditPersistence.Persisted, admission.Outcome.Audit);
            admitted = Assert.IsType<RecipeActivationRecord>(admission.Record);
            Assert.Equal(RecipeActivationOutcomeState.Admitted, admitted.Outcome.State);
            // End only this in-memory owner. The real SQLite admission intentionally
            // has no terminal, representing the process boundary under test.
            reservation.PublishTerminal("V132RestartFixtureBoundary", false);
        }
        await harness.WaitForVerifiedAsync();
        await harness.Runtime.DisposeAsync();

        var hidden = new EmptyActivationHistory();
        var provider = new RestartCameraProvider(cameraAvailable);
        await using var restarted = new StationRuntime(harness.Store, TimeSpan.FromMilliseconds(50),
            sessions: harness.Sessions, authorization: harness.Authorization, frameBufferPool: harness.FramePool,
            cameraProviders: new[] { provider }, cameraSetupOptions: new CameraSetupOptions
            { OperationTimeout = TimeSpan.FromSeconds(2), ShutdownTimeout = TimeSpan.FromSeconds(2) },
            productionStoreOptions: harness.Options);
        var service = new RecipeActivationService(harness.Drafts, harness.ReleaseHistory, harness.ContractHistory,
            hidden, harness.Authorization, harness.Store, harness.Options, harness.Preparation,
            harness.PreparationOptions, harness.FramePool,
            (correlation, token) => restarted.ReserveRecipeActivationAsync(correlation, token),
            () => restarted.GetSnapshotAsync());
        restarted.ConfigureRecipeActivationService(service);
        await restarted.WaitForRecipeActivationStartupAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await harness.WaitForVerifiedAsync();
        var page = await harness.ActivationHistory.QueryAsync(new(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(0, hidden.Calls);
        Assert.Equal(1, provider.OpenCalls);
        var snapshot = await restarted.GetSnapshotAsync();
        Assert.Null(snapshot.ActiveRecipe);
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
        if (cameraAvailable)
        {
            Assert.True(!snapshot.AdmissionBlockers.Contains("RecipeActivationStartupRecoveryRequired"),
                string.Join(", ", snapshot.AdmissionBlockers));
            var terminal = Assert.Single(page.Records, record => record.IsTerminal);
            Assert.Equal("RecipeActivationInterruptedByRestart", terminal.Outcome.ReasonCode);
            Assert.Equal(admitted.Reference, terminal.AdmissionReference);
            Assert.Equal(admitted.ActorPrincipalId, terminal.ActorPrincipalId);
            Assert.Equal(admitted.ActorSessionId, terminal.ActorSessionId);
            Assert.Equal(RecipeActivationRestorationState.NoPreviousBaselineClosed, terminal.Restoration.State);
            Assert.Empty(page.PendingAdmissions!);
            Assert.Equal(1, provider.StopCalls);
            Assert.Equal(1, provider.DisposeCalls);
            Assert.DoesNotContain("RecipeActivationStartupRecoveryRequired", snapshot.AdmissionBlockers);
        }
        else
        {
            Assert.Single(page.Records);
            Assert.Equal(admitted.Reference, Assert.Single(page.PendingAdmissions!).Reference);
            Assert.Contains("RecipeActivationStartupRecoveryRequired", snapshot.AdmissionBlockers);
            using var retry = await restarted.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.False(retry.Available);
            Assert.Equal("RecipeActivationStartupRecoveryRequired", retry.Failure);
        }
    }

    private sealed class EmptyActivationHistory : IRecipeActivationQuery
    {
        internal int Calls { get; private set; }
        public ValueTask<RecipeActivationReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new RecipeActivationReadResult(true, "PretendNoActive")); }
        public ValueTask<RecipeActivationReadResult> ReadAsync(RecipeActivationReference reference,
            CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new RecipeActivationReadResult(true, "PretendNoRecord")); }
        public ValueTask<RecipeActivationPage> QueryAsync(RecipeActivationFilter filter,
            CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new RecipeActivationPage(true, "PretendEmpty", Array.Empty<RecipeActivationRecord>(),
            0, null, Array.Empty<RecipeActivationRecord>())); }
    }

    private sealed class RestartCameraProvider : ICameraProvider
    {
        private readonly bool _available;
        private readonly VirtualCameraProvider _inner = VirtualCameraProvider.Create();
        internal RestartCameraProvider(bool available) => _available = available;
        public CameraProviderIdentity Identity => _inner.Identity;
        internal int OpenCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) =>
            _inner.DiscoverAsync(cancellationToken);
        public async ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity, CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            if (!_available) return CameraOpenResult.Failure("V132ColdCameraUnavailable");
            var opened = await _inner.OpenAsync(stableDeviceIdentity, cancellationToken);
            return opened.Device is { } device ? CameraOpenResult.Success(new RestartDevice(this, device)) : opened;
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        private sealed class RestartDevice : ICameraDevice
        {
            private readonly RestartCameraProvider _owner;
            private readonly ICameraDevice _inner;
            internal RestartDevice(RestartCameraProvider owner, ICameraDevice inner) { _owner = owner; _inner = inner; }
            public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
            public CameraCapabilities Capabilities => _inner.Capabilities;
            public CameraHealthSnapshot GetHealthSnapshot() => _inner.GetHealthSnapshot();
            public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(RequestedCameraConfiguration requested,
                CancellationToken cancellationToken = default) => _inner.ApplyConfigurationAsync(requested, cancellationToken);
            public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);
            public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
                CancellationToken cancellationToken = default) => _inner.AcquireAsync(request, cancellationToken);
            public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default)
            { _owner.StopCalls++; return _inner.StopAsync(cancellationToken); }
            public ValueTask DisposeAsync() { _owner.DisposeCalls++; return _inner.DisposeAsync(); }
        }
    }
}
