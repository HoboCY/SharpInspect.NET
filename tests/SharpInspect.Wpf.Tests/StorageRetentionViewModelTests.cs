using System.Globalization;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class StorageRetentionViewModelTests
{
    private static readonly string Hash = new('A', 64);
    private static readonly DateTimeOffset Time = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory, InlineData("hold"), InlineData("release"), InlineData("extend"), Trait("VerificationId", "V155_U01")]
    public async Task V155_U01_ChangesBindTheExactObservedOwnerRevisionAndFreshStepUp(string action)
    {
        var subject = Subject(held: action == "release");
        var service = new Service(_ => Task.FromResult(Page(subject)));
        var auth = new Authentication();
        var sessions = new Sessions();
        await using var model = Model(service, sessions, auth);
        await model.RefreshAsync();
        model.SelectedSubject = Assert.Single(model.Subjects);
        model.ReasonText = "复核证据后调整保留锁";
        if (action == "release") model.SelectedHoldId = subject.ActiveHolds.Single();
        if (action == "extend") model.ExtendedUntilText = subject.EffectiveUntilUtc.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        var result = action switch
        {
            "hold" => await model.PlaceHoldWithStepUpAsync("ephemeral-test-password"),
            "release" => await model.ReleaseHoldWithStepUpAsync("ephemeral-test-password"),
            _ => await model.ExtendWithStepUpAsync("ephemeral-test-password")
        };
        Assert.NotNull(result);
        var command = Assert.IsType<ChangeEvidenceRetentionCommand>(service.Last);
        Assert.Equal(subject.Obligation.Owner, command.Owner);
        Assert.Equal(subject.Revision, command.ExpectedRevision);
        Assert.Equal("复核证据后调整保留锁", command.Reason);
        Assert.Equal(auth.Grant, command.Invocation.StepUpGrantId);
        Assert.Equal(new StepUpBinding(Permission.DeleteEvidence, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.ChangeEvidenceRetention), auth.Binding);
        Assert.Equal(sessions.Current.SessionId, command.Invocation.SessionId);
        if (action == "hold") Assert.NotEqual(Guid.Empty, command.HoldId);
        if (action == "release") Assert.Equal(subject.ActiveHolds.Single(), command.HoldId);
        if (action == "extend") Assert.Equal(subject.EffectiveUntilUtc.AddDays(1), command.ExtendedUntilUtc);
        Assert.Null(model.SelectedSubject);
        Assert.False(model.CanPlaceHold);
        Assert.DoesNotContain("ephemeral-test-password", model.StatusText);
    }

    [Theory, InlineData("reason"), InlineData("selection"), InlineData("session"), InlineData("deactivate"),
     Trait("VerificationId", "V155_U02")]
    public async Task V155_U02_ChangingContextDuringStepUpNeverDispatchesTheOldCommand(string change)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Service(_ => Task.FromResult(Page(Subject())));
        var sessions = new Sessions();
        var auth = new Authentication(_ => { entered.SetResult(true); return finish.Task; });
        await using var model = Model(service, sessions, auth);
        await model.RefreshAsync();
        model.SelectedSubject = Assert.Single(model.Subjects);
        model.ReasonText = "保护待复核证据";
        var operation = model.PlaceHoldWithStepUpAsync("ephemeral-test-password");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (change == "reason") model.ReasonText = "已修改操作目的";
            if (change == "selection") model.SelectedSubject = null;
            if (change == "session") sessions.Lock();
            if (change == "deactivate") model.Deactivate();
            finish.TrySetResult(new(true, "Authenticated", auth.Grant));
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Null(service.Last);
            if (change is "session" or "deactivate")
            {
                Assert.Empty(model.Subjects);
                Assert.Empty(model.History);
                Assert.Empty(model.ReasonText);
            }
        }
        finally { finish.TrySetResult(new(false, "Rejected")); }
    }

    [Theory, InlineData(false), InlineData(true), Trait("VerificationId", "V155_U03")]
    public async Task V155_U03_LateReadCannotRestoreEvidenceAfterDeactivationOrDisposal(bool dispose)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<EvidenceRetentionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Service(_ => { entered.SetResult(true); return finish.Task; });
        await using var model = Model(service, new Sessions(), new Authentication());
        var reading = model.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (dispose) await model.DisposeAsync(); else model.Deactivate();
        try
        {
            // The query ignores cancellation. The UI must retire its logical
            // wait and remain usable without waiting for that physical result.
            await reading.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { finish.TrySetResult(Page(Subject())); }
        Assert.Empty(model.Subjects);
        Assert.Empty(model.History);
        Assert.False(model.IsLoading);
        Assert.False(model.HasNextPage);
    }

    [Theory, InlineData("negative"), InlineData("enum"), InlineData("duplicate"), InlineData("cursor"),
     Trait("VerificationId", "V155_U04")]
    public async Task V155_U04_MalformedHistoryDoesNotBecomeActionableEvidence(string invalid)
    {
        var subject = Subject();
        var page = Page(subject);
        if (invalid == "negative") page = page with { ActiveHolds = -1 };
        if (invalid == "enum") page = page with { Subjects = new[] { subject with { Disposition = (EvidenceRetentionDisposition)255 } } };
        if (invalid == "duplicate") page = page with { Records = new[] { page.Records[0], page.Records[0] } };
        if (invalid == "cursor") page = page with { NextAfterPosition = 90 };
        var service = new Service(_ => Task.FromResult(page));
        await using var model = Model(service, new Sessions(), new Authentication());
        await model.RefreshAsync();
        Assert.Empty(model.History);
        Assert.Empty(model.Subjects);
        Assert.False(model.CanPlaceHold);
    }

    [Theory, InlineData(false), InlineData(true), Trait("VerificationId", "V155_U05")]
    public async Task V155_U05_MissingPermissionOrRejectedAuthenticationCannotChangeRetention(bool deniedAuthentication)
    {
        var service = new Service(_ => Task.FromResult(Page(Subject()))) { Allowed = deniedAuthentication };
        var auth = new Authentication(_ => Task.FromResult(new StepUpResult(false, "private-auth-error")));
        await using var model = Model(service, new Sessions(), auth);
        await model.RefreshAsync();
        model.SelectedSubject = model.Subjects.SingleOrDefault();
        model.ReasonText = "复核保留";
        await model.PlaceHoldWithStepUpAsync("ephemeral-test-password");
        Assert.Null(service.Last);
        Assert.DoesNotContain("private-auth-error", model.StatusText);
    }

    [Theory, InlineData(false), InlineData(true), Trait("VerificationId", "V155_U06")]
    public async Task V155_U06_PaginationUsesTheFrozenUpperBoundAndReplacesRows(bool changedUpperBound)
    {
        var first = Subject(); var second = Subject();
        var service = new Service(filter => Task.FromResult(filter.AfterPosition == 0 ?
            Page(first) with { ThroughPosition = 2, NextAfterPosition = 1 } :
            Page(second, position: 2) with { ThroughPosition = changedUpperBound ? 3 : 2 }));
        await using var model = new StorageRetentionViewModel(new Capacity(), service, new Sessions(),
            new Authentication(), new InlineUiDispatcher(), 1);
        await model.RefreshAsync();
        Assert.True(model.HasNextPage);
        await model.NextPageAsync();
        Assert.Equal(1, service.Filter!.AfterPosition);
        Assert.Equal(2, service.Filter.ThroughPosition);
        if (changedUpperBound)
        {
            Assert.Empty(model.Subjects);
            Assert.Empty(model.History);
        }
        else
        {
            Assert.Equal(second.Obligation.Owner, Assert.Single(model.Subjects).Obligation.Owner);
            Assert.Single(model.History);
        }
        Assert.False(model.HasNextPage);
    }

    private static StorageRetentionViewModel Model(Service service, Sessions sessions, Authentication authentication) =>
        new(new Capacity(), service, sessions, authentication, new InlineUiDispatcher());

    private static EvidenceRetentionStatus Subject(bool held = false)
    {
        var obligation = new EvidenceRetentionObligation(new(EvidenceRetentionOwnerKind.QuarantinedFile, Guid.NewGuid()),
            TraceRetentionClass.QuarantineEvidence, null, Hash, 1, Hash, Hash, Hash, Hash,
            RetentionStartEvent.Quarantined, Time.AddDays(-2), Time.AddDays(2), Hash, "retained.quarantine", 3, Hash);
        return new(obligation, held ? 2 : 1, Hash, held ? EvidenceRetentionDisposition.Held : EvidenceRetentionDisposition.Retained,
            obligation.RetainUntilUtc, held ? new[] { Guid.NewGuid() } : Array.Empty<Guid>(), null, null, "Retained", 0);
    }

    private static EvidenceRetentionSnapshot Page(EvidenceRetentionStatus subject, long position = 1)
    {
        var held = subject.ActiveHolds.Count != 0;
        var record = new EvidenceRetentionRecord(position, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            held ? EvidenceRetentionEventKind.HoldPlaced : EvidenceRetentionEventKind.ObligationEstablished,
            subject.Obligation.Owner, subject.Revision, held ? Hash : null, Hash, Time, "Recorded",
            SystemPrincipalId.RetentionCleanup, held ? Guid.NewGuid() : null, held ? subject.ActiveHolds[0] : null,
            null, null, position + 10, Hash, held ? "已复核" : null, held ? Guid.NewGuid() : null, held ? Guid.NewGuid() : null);
        var execution = new TraceRetentionExecutionPolicy("ui-test", "1", "fixture", "Explicit UI fixture",
            new[] { TraceRetentionClass.QuarantineEvidence }, new(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2), 1024, 4), 2);
        return new(true, "HistoryAvailable", new[] { record }, new[] { subject }, position, null,
            subject.ActiveHolds.Count, 0, 0, 0, 0, execution);
    }

    private sealed class Capacity : ITraceStorageCapacityQuery
    {
        public ValueTask<TraceStorageCapacitySnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TraceStorageCapacitySnapshot(true, "WithinPolicy", Guid.NewGuid(), 1, Time, 1, Hash,
                TraceStorageHealth.Healthy, new[] { new TraceStorageAreaObservation(TraceStorageArea.Database, Hash,
                    1000, 500, 100, 100, 1, null, null) }, 100, 0, 1024,
                new(TraceCheckpointStatus.Awaiting, "AwaitingMaintenance", null, null, null, null, null, null),
                null, null, Array.Empty<string>()));
    }

    private sealed class Service : IEvidenceRetentionService
    {
        private readonly Func<EvidenceRetentionFilter, Task<EvidenceRetentionSnapshot>> _read;
        internal Service(Func<EvidenceRetentionFilter, Task<EvidenceRetentionSnapshot>> read) => _read = read;
        internal bool Allowed = true;
        internal ChangeEvidenceRetentionCommand? Last;
        internal EvidenceRetentionFilter? Filter;
        public async ValueTask<EvidenceRetentionSnapshot> ReadAsync(EvidenceRetentionFilter filter, CancellationToken cancellationToken = default)
        { Filter = filter; return await _read(filter); }
        public ValueTask<EvidenceRetentionAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EvidenceRetentionAccess(Allowed, "ControlledAccess", true));
        public ValueTask<EvidenceRetentionResult> ChangeAsync(ChangeEvidenceRetentionCommand command, CancellationToken cancellationToken = default)
        {
            Last = command;
            return ValueTask.FromResult(new EvidenceRetentionResult(new(command.CorrelationId, CommandDisposition.Accepted,
                "Recorded", AuditPersistence.Persisted), null));
        }
    }

    private sealed class Authentication : IStepUpAuthentication
    {
        private readonly Func<StepUpRequest, Task<StepUpResult>>? _respond;
        internal Authentication(Func<StepUpRequest, Task<StepUpResult>>? respond = null) => _respond = respond;
        internal Guid Grant = Guid.NewGuid();
        internal StepUpBinding? Binding;
        public async ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request, CancellationToken cancellationToken = default)
        { Binding = request.Binding; return _respond is null ? new(true, "Authenticated", Grant) : await _respond(request); }
    }

    private sealed class Sessions : IInteractiveSessionService
    {
        public InteractiveSession Current { get; private set; } = new(InteractiveSessionState.Authenticated,
            Guid.NewGuid().ToString("D"), Guid.NewGuid());
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;
        internal void Lock() { Current = new(InteractiveSessionState.Locked, null, null); Changed?.Invoke(this, new(Current)); }
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
