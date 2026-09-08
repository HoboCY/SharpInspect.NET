using System.Collections.ObjectModel;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused tests for the public imaging declaration coordinator.  The camera
/// binding is supplied by a setup persistence projection, while the imaging
/// persistence fixture records successful coordinator writes and can hold one
/// append to exercise the operation gate.  This keeps the tests deterministic
/// while still exercising the Runtime's authorization, binding CAS, operation
/// gate and read-back paths.
/// </summary>
public sealed class ImagingSetupRuntimeTests
{
    private const string Role = "TopCamera";

    [Fact]
    public async Task V123_G01_MissingOrWrongStepUpIsRejectedWithoutAnImagingRevision()
    {
        await using var fixture = Fixture.Create();
        var operationId = Guid.NewGuid();
        var request = fixture.Request(operationId, null, Fixture.Definition("Lens-A"),
            "Declare initial setup");
        fixture.Authorizer.AllowedTarget = request.AuthorizationTarget;
        fixture.Authorizer.RequiredGrant = Guid.NewGuid();

        var missing = await fixture.Runtime.DeclareImagingSetupAsync(request);
        Assert.False(missing.Succeeded);
        Assert.Equal("StepUpRequired", missing.ReasonCode);
        Assert.Equal(0, fixture.Imaging.AppendCalls);
        Assert.Empty(fixture.Imaging.Revisions);

        var wrong = await fixture.Runtime.DeclareImagingSetupAsync(
            fixture.Request(operationId, Guid.NewGuid(), request.Definition, request.ChangeReason));
        Assert.False(wrong.Succeeded);
        Assert.Equal("StepUpInvalid", wrong.ReasonCode);
        Assert.Equal(0, fixture.Imaging.AppendCalls);
        Assert.Empty(fixture.Imaging.Revisions);
    }

    [Fact]
    public async Task V123_G02_BindingCasIsRejectedBeforeImagingPersistenceAppend()
    {
        await using var fixture = Fixture.Create();
        var operationId = Guid.NewGuid();
        var grant = Guid.NewGuid();
        var firstRequest = fixture.Request(operationId, grant, Fixture.Definition("Lens-A"),
            "Declare initial setup");
        fixture.Authorizer.Allow(firstRequest, grant);

        var first = await fixture.Runtime.DeclareImagingSetupAsync(firstRequest);
        Assert.True(first.Succeeded, first.ReasonCode);
        Assert.NotNull(first.Revision);
        Assert.Equal(1, first.Revision!.Revision);
        Assert.Equal(operationId, first.Revision.RevisionId);
        Assert.Single(fixture.Imaging.Revisions);

        var wrongBinding = fixture.Request(Guid.NewGuid(), Guid.NewGuid(), Fixture.Definition("Lens-B"),
            "Wrong binding CAS", expectedBindingRevision: 2, expectedBindingRevisionHash: new string('B', 64));
        fixture.Authorizer.Allow(wrongBinding, wrongBinding.Invocation.StepUpGrantId!.Value);
        var bindingConflict = await fixture.Runtime.DeclareImagingSetupAsync(wrongBinding);
        Assert.False(bindingConflict.Succeeded);
        Assert.Equal("CameraBindingRevisionConflict", bindingConflict.ReasonCode);
        Assert.Equal(1, fixture.Imaging.AppendCalls);
        Assert.Single(fixture.Imaging.Revisions);
    }

    [Fact]
    public async Task V123_G04_OperationGateRejectsASecondDeclarationWhileTheFirstIsQueued()
    {
        await using var fixture = Fixture.Create(TimeSpan.FromMilliseconds(150));
        fixture.Imaging.BlockAppend = true;

        var first = fixture.Request(Guid.NewGuid(), Guid.NewGuid(), Fixture.Definition("Lens-A"),
            "First queued declaration");
        fixture.Authorizer.Allow(first, first.Invocation.StepUpGrantId!.Value);
        var firstTask = fixture.Runtime.DeclareImagingSetupAsync(first).AsTask();
        await fixture.Imaging.AppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = fixture.Request(Guid.NewGuid(), Guid.NewGuid(), Fixture.Definition("Lens-B"),
            "Second concurrent declaration");
        fixture.Authorizer.Allow(second, second.Invocation.StepUpGrantId!.Value);
        var secondResult = await fixture.Runtime.DeclareImagingSetupAsync(second)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(secondResult.Succeeded);
        Assert.Equal("CameraSetupBusy", secondResult.ReasonCode);
        Assert.Empty(fixture.Imaging.Revisions);
        Assert.Equal(1, fixture.Imaging.AppendCalls);

        fixture.Imaging.ReleaseAppend();
        var firstResult = await firstTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(firstResult.Succeeded, firstResult.ReasonCode);
        Assert.Equal(1, firstResult.Revision?.Revision);
        Assert.Single(fixture.Imaging.Revisions);
    }

    [Fact]
    public void V123_G05_TamperedRevisionPayloadAndPositionAreRejectedByTheStorageCodec()
    {
        var binding = Fixture.CreateBinding(Guid.NewGuid(), Guid.NewGuid());
        var revision = new ImagingSetupRevision(1, Role, 1, Guid.NewGuid(), null, binding,
            Fixture.Definition("Lens-A"), ImagingSetupChangeOrigin.OperatorDeclared,
            binding.AuthorPrincipalId, binding.AuthorSessionId, binding.AuthorAuthorizationRevision,
            "Declared setup", DateTimeOffset.UnixEpoch);
        var payload = ImagingSetupRevisionStorageCodec.Encode(revision);

        var tamperedText = Encoding.UTF8.GetString(payload);
        var changedFirstHashCharacter = revision.RevisionHash[0] == '0' ? '1' : '0';
        var tampered = Encoding.UTF8.GetBytes(tamperedText.Replace(revision.RevisionHash,
            changedFirstHashCharacter + revision.RevisionHash[1..], StringComparison.Ordinal));
        var hashFailure = Assert.Throws<InvalidOperationException>(() =>
            ImagingSetupRevisionStorageCodec.Decode(tampered, expectedPosition: 1));
        Assert.Equal("ImagingSetupRevisionHashMismatch", hashFailure.Message);

        var positionFailure = Assert.Throws<InvalidOperationException>(() =>
            ImagingSetupRevisionStorageCodec.Decode(payload, expectedPosition: 2));
        Assert.Equal("ImagingSetupRevisionShapeInvalid", positionFailure.Message);

        var roundTrip = ImagingSetupRevisionStorageCodec.Decode(payload, expectedPosition: 1);
        Assert.Equal(revision.RevisionHash, roundTrip.RevisionHash);
        Assert.Null(roundTrip.PreviousRevisionHash);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(CameraSetupRuntime runtime, TestAuthorizer authorizer,
            FakeImagingPersistence imaging, CameraBindingRevision binding)
        {
            Runtime = runtime;
            Authorizer = authorizer;
            Imaging = imaging;
            Binding = binding;
        }

        internal CameraSetupRuntime Runtime { get; }
        internal TestAuthorizer Authorizer { get; }
        internal FakeImagingPersistence Imaging { get; }
        internal CameraBindingRevision Binding { get; }

        internal static Fixture Create(TimeSpan? operationTimeout = null)
        {
            var providerIdentity = new CameraProviderIdentity(
                "Test.Imaging.Provider", "1", "Test.Imaging.Adapter", "1");
            var principal = Guid.NewGuid();
            var session = Guid.NewGuid();
            var binding = CreateBinding(principal, session, providerIdentity);
            var cameraPersistence = new FakeCameraSetupPersistence(binding);
            var imaging = new FakeImagingPersistence(binding);
            var authorizer = new TestAuthorizer(principal, session, authorizationRevision: 3);
            var sessions = new StaticSessions(new InteractiveSession(
                InteractiveSessionState.Authenticated, principal.ToString("D"), session));
            var identity = new StaticIdentityQuery();
            var options = new CameraSetupOptions
            {
                OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(2),
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            };
            var station = new CameraSetupRuntime.CameraStationContext(Guid.NewGuid(), false,
                ProductionArmState.Disarmed, false, null, 0, HandshakePhase.Idle,
                ExclusiveMode.None, RecoveryState.None, false, null, false, false);
            var runtime = new CameraSetupRuntime(new[] { new NoOpProvider(providerIdentity) }, options,
                audit: null, sessions: sessions, identityQuery: identity, readStation: () => station,
                publishSetup: (_, _) => { }, cameraAuthorizer: authorizer,
                persistence: cameraPersistence, networkPersistence: null, imagingPersistence: imaging);
            return new Fixture(runtime, authorizer, imaging, binding);
        }

        internal CommandInvocation Invocation(Guid? grant = null) =>
            new(CommandSource.PhysicalConsole, Binding.AuthorPrincipalId.ToString("D"),
                Binding.AuthorSessionId, grant);

        internal ImagingSetupChangeRequest Request(Guid operationId, Guid? grant,
            ImagingSetupDefinition definition, string reason, long expectedBindingRevision = 1,
            string? expectedBindingRevisionHash = null, long expectedRevision = 0,
            string? expectedRevisionHash = null)
        {
            expectedBindingRevisionHash ??= Binding.RevisionHash;
            return new ImagingSetupChangeRequest(operationId, Invocation(grant), Role,
                expectedBindingRevision, expectedBindingRevisionHash, expectedRevision,
                expectedRevisionHash, definition, reason);
        }

        internal static CameraBindingRevision CreateBinding(Guid principal, Guid session,
            CameraProviderIdentity? provider = null) =>
            new(1, Role, 1, Guid.NewGuid(), null, new string('A', 64),
                new(provider ?? new CameraProviderIdentity("Test.Imaging.Provider", "1",
                    "Test.Imaging.Adapter", "1"), "Camera:One"), principal, session, 3,
                "Bind test camera", DateTimeOffset.UnixEpoch);

        internal static ImagingSetupDefinition Definition(string lens) =>
            new(lens, "Focus-100", "Mount-Top", 250, "SensorUp");

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class TestAuthorizer : ICameraSetupAuthorizer
    {
        internal TestAuthorizer(Guid principalId, Guid sessionId, long authorizationRevision)
        {
            PrincipalId = principalId;
            SessionId = sessionId;
            AuthorizationRevision = authorizationRevision;
        }

        internal Guid PrincipalId { get; }
        internal Guid SessionId { get; }
        internal long AuthorizationRevision { get; }
        internal Guid RequiredGrant { get; set; }
        internal string? AllowedTarget { get; set; }
        internal bool AllowMutations { get; set; } = true;

        internal void Allow(ImagingSetupChangeRequest request, Guid grant)
        {
            RequiredGrant = grant;
            AllowedTarget = request.AuthorizationTarget;
            AllowMutations = true;
        }

        public ValueTask<CameraSetupAuthorization> AuthorizeCameraSetupAsync(
            CommandInvocation invocation, bool readOnly, Guid operationId, string targetId,
            AuditedCommandKind commandKind, CancellationToken cancellationToken = default)
        {
            if (readOnly)
                return ValueTask.FromResult(new CameraSetupAuthorization(true, "Authorized",
                    PrincipalId, SessionId, AuthorizationRevision, null));
            if (!AllowMutations || invocation.StepUpGrantId is null)
                return ValueTask.FromResult(CameraSetupAuthorization.Denied(
                    invocation.StepUpGrantId is null ? "StepUpRequired" : "StepUpInvalid"));
            if (invocation.StepUpGrantId != RequiredGrant ||
                (AllowedTarget is not null && !string.Equals(targetId, AllowedTarget, StringComparison.Ordinal)))
                return ValueTask.FromResult(CameraSetupAuthorization.Denied("StepUpInvalid"));
            var reservation = CameraSetupAuthorizationReservation.Create(() => { }, () => { });
            return ValueTask.FromResult(new CameraSetupAuthorization(true, "Authorized",
                PrincipalId, SessionId, AuthorizationRevision, reservation));
        }
    }

    private sealed class FakeCameraSetupPersistence : ICameraSetupPersistence
    {
        private readonly CameraSetupPersistentState _state;

        internal FakeCameraSetupPersistence(CameraBindingRevision binding)
        {
            var health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
                CameraAcquisitionState.Stopped, new FrameTimePoint(DateTimeOffset.UnixEpoch, 0));
            _state = new CameraSetupPersistentState(new CameraSetupSnapshot(Role, binding, health), false,
                "CameraSetupSeeded");
        }

        public ValueTask<CameraSetupPersistentState> ReadCameraSetupAsync(string logicalRole,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_state);

        public ValueTask<StoreWriteResult> AppendCameraSetupAsync(CameraSetupPersistenceRequest request,
            StoreDeadline deadline, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StoreWriteResult(false, "UnexpectedCameraSetupWrite"));
    }

    private sealed class FakeImagingPersistence : IImagingSetupRevisionPersistence
    {
        private readonly object _sync = new();
        private readonly CameraBindingRevision _binding;
        private readonly List<ImagingSetupRevision> _revisions = new();
        private TaskCompletionSource<bool> _release = NewSignal();

        internal FakeImagingPersistence(CameraBindingRevision binding)
        {
            _binding = binding;
        }

        private int _appendCalls;
        internal int AppendCalls => Volatile.Read(ref _appendCalls);
        internal bool BlockAppend { get; set; }
        internal TaskCompletionSource<bool> AppendStarted { get; } = NewSignal();
        internal IReadOnlyList<ImagingSetupRevision> Revisions
        {
            get { lock (_sync) return new ReadOnlyCollection<ImagingSetupRevision>(_revisions.ToArray()); }
        }

        public ValueTask<ImagingSetupStoreSnapshot> ReadAsync(string logicalCameraRole,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
                return ValueTask.FromResult(new ImagingSetupStoreSnapshot(logicalCameraRole,
                    new ReadOnlyCollection<ImagingSetupRevision>(_revisions.ToArray())));
        }

        public ValueTask<ImagingSetupHistoryResult> QueryHistoryAsync(string logicalCameraRole,
            long afterPosition, long? throughPosition, int pageSize,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                var through = throughPosition ?? (_revisions.Count == 0 ? 0 : _revisions[^1].Position);
                var page = _revisions.Where(item => item.Position > afterPosition && item.Position <= through)
                    .Take(pageSize).ToArray();
                var next = page.Length == pageSize && page.Length > 0 &&
                    _revisions.Any(item => item.Position > page[^1].Position && item.Position <= through)
                    ? (long?)page[^1].Position : null;
                return ValueTask.FromResult(new ImagingSetupHistoryResult(true,
                    "ImagingSetupHistoryAvailable", page, through, next));
            }
        }

        public async ValueTask<StoreWriteResult> AppendAsync(ImagingSetupPersistenceRequest request,
            StoreDeadline deadline, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _appendCalls);
            AppendStarted.TrySetResult(true);
            if (BlockAppend)
                await _release.Task.ConfigureAwait(false);

            lock (_sync)
            {
                var current = _revisions.Count == 0 ? null : _revisions[^1];
                var revisionNumber = checked(_revisions.Count + 1L);
                var revision = new ImagingSetupRevision(revisionNumber, Role, revisionNumber,
                    request.OperationId, current?.RevisionHash, _binding, request.Change.Definition,
                    ImagingSetupChangeOrigin.OperatorDeclared, request.Authorization.PrincipalId,
                    request.Authorization.SessionId, request.Authorization.AuthorizationRevision,
                    request.Change.ChangeReason, DateTimeOffset.UnixEpoch.AddSeconds(revisionNumber));
                _revisions.Add(revision);
                return new StoreWriteResult(true, "ImagingSetupPersisted", request.CommandFact);
            }
        }

        internal void ReleaseAppend() => _release.TrySetResult(true);
        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class NoOpProvider : ICameraProvider
    {
        internal NoOpProvider(CameraProviderIdentity identity) => Identity = identity;
        public CameraProviderIdentity Identity { get; }
        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));
        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticSessions : IInteractiveSessionService
    {
        internal StaticSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
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

    private sealed class StaticIdentityQuery : IIdentityAdministrationQuery
    {
        public ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HumanAuthorizationSnapshot(false, "Unused", null, null));
        public ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HumanDirectorySnapshot(false, "Unused"));
    }
}
