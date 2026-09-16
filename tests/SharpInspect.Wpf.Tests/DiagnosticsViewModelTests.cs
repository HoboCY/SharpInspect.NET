using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class DiagnosticsViewModelTests
{
    [Fact, Trait("VerificationId", "V156_U03")]
    public async Task V156_U03_FailedRefreshCannotRetainAPreviousHealthyObservation()
    {
        var count = 0;
        var history = new History(_ => ++count == 1 ? Task.FromResult(Page()) : Task.FromException<DiagnosticHistoryPage>(new Exception("SECRET")));
        await using var model = new DiagnosticsViewModel(new Health(true), history, new Sessions(), 10, 4096, new InlineUiDispatcher());
        await model.RefreshAsync(); Assert.Contains("诊断可用", model.HealthText);
        await model.RefreshAsync(); Assert.Contains("未知", model.HealthText);
        Assert.Empty(model.Rows); Assert.DoesNotContain("SECRET", model.StatusText);
    }

    [Theory, InlineData("lock"), InlineData("channel"), InlineData("navigate"), InlineData("dispose"), Trait("VerificationId", "V156_U01")]
    public async Task V156_U01_LatePageCannotRestoreProtectedDataAfterContextChange(string change)
    {
        var finish = new TaskCompletionSource<DiagnosticHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new Sessions(); var service = new History(_ => finish.Task);
        await using var model = new DiagnosticsViewModel(new Health(), service, sessions, 10, 4096, new InlineUiDispatcher());
        model.ProtectedChannel = true;
        var read = model.RefreshAsync();
        Assert.NotNull(service.Last); Assert.True(service.Last!.Protected);
        if (change == "lock") sessions.Lock();
        if (change == "channel") model.ProtectedChannel = false;
        if (change == "navigate") model.Deactivate();
        if (change == "dispose") await model.DisposeAsync();
        await model.RefreshAsync(); Assert.Equal(1, service.Calls); // cancellation does not free physical UI read ownership
        finish.SetResult(Page()); await read;
        Assert.Empty(model.Rows); Assert.DoesNotContain("HResult", model.StatusText);
    }

    [Fact, Trait("VerificationId", "V156_U02")]
    public async Task V156_U02_QueryCarriesCurrentSessionAndExplicitLimitsAndClearsOnLogout()
    {
        var sessions = new Sessions(); var service = new History(_ => Task.FromResult(Page()));
        await using var model = new DiagnosticsViewModel(new Health(), service, sessions, 10, 4096, new InlineUiDispatcher());
        await model.RefreshAsync();
        Assert.Equal(sessions.Current.SessionId, service.Last!.Invocation.SessionId);
        Assert.Equal(10, service.Last.MaximumRecords); Assert.Equal(4096, service.Last.MaximumBytes);
        Assert.Single(model.Rows); Assert.Contains("未知", model.HealthText);
        sessions.Lock(); Assert.Empty(model.Rows); await model.RefreshAsync(); Assert.Equal(1, service.Calls);
    }

    private static DiagnosticHistoryPage Page() => new(true, "Available", new[]
    {
        new DiagnosticRecord(Guid.NewGuid(), "Runtime.OwnerFault", 1, DiagnosticLevel.Error, "Runtime", Guid.NewGuid(),
            DateTimeOffset.UtcNow, null, null, new string('A', 64), new[] { new DiagnosticProperty("HResult", DiagnosticScalar.FromInt64(7)) })
    }, 0);
    private sealed class Health : IDiagnosticPipelineHealthQuery
    {
        private readonly bool _configured;
        internal Health(bool configured = false) => _configured = configured;
        public DiagnosticPipelineHealthSnapshot ReadHealth() => new(_configured, Guid.NewGuid(), null, DateTimeOffset.UtcNow,
            0, 0, 0, 0, 0, 0, 0, null, null, null, false, "NotConfigured");
    }
    private sealed class History : IDiagnosticHistoryQuery
    {
        private readonly Func<DiagnosticHistoryRequest, Task<DiagnosticHistoryPage>> _read;
        internal History(Func<DiagnosticHistoryRequest, Task<DiagnosticHistoryPage>> read) => _read = read;
        internal DiagnosticHistoryRequest? Last; internal int Calls;
        public async ValueTask<DiagnosticHistoryPage> ReadAsync(DiagnosticHistoryRequest request, CancellationToken cancellationToken = default)
        { Last = request; Calls++; return await _read(request); }
    }
    private sealed class Sessions : IInteractiveSessionService
    {
        public InteractiveSession Current { get; private set; } = new(InteractiveSessionState.Authenticated, Guid.NewGuid().ToString("D"), Guid.NewGuid());
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
