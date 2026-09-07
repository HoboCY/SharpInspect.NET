using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class IdentityViewModelTests
{
    [Fact]
    public async Task V104_U01_MissingIdentityServicesStayExplicitlyUnconfigured()
    {
        await using var viewModel = new IdentityViewModel(null, null, "Station-01");

        await viewModel.RefreshAsync();
        var authentication = await viewModel.AuthenticateAsync("alice", "password");

        Assert.False(viewModel.IsConfigured);
        Assert.Equal("未配置", viewModel.StatusLabel);
        Assert.Contains("未配置", viewModel.StatusMessage);
        Assert.False(viewModel.CanRefresh);
        Assert.False(viewModel.CanBootstrap);
        Assert.False(viewModel.CanAuthenticate);
        Assert.Null(authentication);
        Assert.False(viewModel.HasIdentity);
        Assert.False(viewModel.HasRecoveryKit);
    }

    [Fact]
    public async Task V104_U02_BootstrapUsesProvidedFieldsAndConsumesRecoveryKitOnce()
    {
        var bootstrap = new RecordingBootstrap
        {
            Status = new StationIdentityStatus("Station-01", true, 0, 0, "BootstrapRequired"),
            CreateResult = new BootstrapAdministratorResult(true, "AdministratorCreated",
                Identity("alice", "Alice"), RecoveryKitId: Guid.NewGuid(),
                RecoveryKit: new OneTimeSecret("recovery-secret"))
        };
        await using var viewModel = new IdentityViewModel(bootstrap, null, "Station-01");

        await viewModel.RefreshAsync();
        var result = await viewModel.CreateFirstAdministratorAsync(
            "installer-token", "alice", "Alice", "admin-password");

        Assert.True(result?.Succeeded);
        Assert.Equal("installer-token", bootstrap.Request!.BootstrapToken);
        Assert.Equal("admin-password", bootstrap.Request.Password);
        Assert.Equal("Station-01", bootstrap.Request.StationId);
        Assert.Equal(0, bootstrap.ProvisionCalls);
        Assert.False(viewModel.BootstrapRequired);
        Assert.True(viewModel.HasIdentity);
        Assert.True(viewModel.HasRecoveryKit);

        Assert.Equal("recovery-secret", viewModel.RevealRecoveryKit());
        Assert.Null(viewModel.RevealRecoveryKit());
        Assert.False(viewModel.HasRecoveryKit);
        Assert.DoesNotContain("installer-token", viewModel.StatusMessage);
        Assert.DoesNotContain("admin-password", viewModel.StatusMessage);
    }

    [Fact]
    public async Task V104_U03_BootstrapFailureUsesStableReasonAndCanRetry()
    {
        var bootstrap = new RecordingBootstrap
        {
            Status = new StationIdentityStatus("Station-01", true, 0, 0, "BootstrapRequired"),
            ThrowOnCreate = true
        };
        await using var viewModel = new IdentityViewModel(bootstrap, null, "Station-01");
        await viewModel.RefreshAsync();

        var failed = await viewModel.CreateFirstAdministratorAsync(
            "installer-token", "alice", "Alice", "admin-password");

        Assert.Null(failed);
        Assert.Equal("IdentityBootstrapFailed", viewModel.ErrorMessage);
        Assert.DoesNotContain("secret", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);

        bootstrap.ThrowOnCreate = false;
        bootstrap.CreateResult = new BootstrapAdministratorResult(true, "AdministratorCreated",
            Identity("alice", "Alice"), RecoveryKit: new OneTimeSecret("retry-recovery"));
        var retried = await viewModel.CreateFirstAdministratorAsync(
            "installer-token", "alice", "Alice", "admin-password");

        Assert.True(retried?.Succeeded);
        Assert.Null(viewModel.ErrorMessage);
        Assert.True(viewModel.HasRecoveryKit);
    }

    [Fact]
    public async Task V104_U04_AuthenticationFailureClearsPreviousIdentityAndUsesReasonCode()
    {
        var provider = new RecordingIdentityProvider
        {
            Result = new AuthenticationResult(true, "Authenticated", Identity("alice", "Alice"))
        };
        await using var viewModel = new IdentityViewModel(null, provider, "Station-01");

        var success = await viewModel.AuthenticateAsync("alice", "first-password");
        Assert.True(success?.Succeeded);
        Assert.True(viewModel.HasIdentity);

        provider.Result = new AuthenticationResult(false, "InvalidCredentials");
        var rejected = await viewModel.AuthenticateAsync("alice", "second-password");

        Assert.False(rejected?.Succeeded);
        Assert.False(viewModel.HasIdentity);
        Assert.Equal("InvalidCredentials", viewModel.ErrorMessage);
        Assert.DoesNotContain("first-password", viewModel.StatusMessage);
        Assert.DoesNotContain("second-password", viewModel.StatusMessage);
        Assert.Equal("second-password", provider.Request!.Password);
    }

    [Fact]
    public async Task V104_U05_DuplicateSubmissionIsIgnoredAndOlderResponseCannotOverrideNewerOne()
    {
        var provider = new BlockingIdentityProvider();
        await using var viewModel = new IdentityViewModel(null, provider, "Station-01");

        var older = viewModel.AuthenticateAsync("old-user", "old-password");
        await provider.WaitForCallAsync(1);
        var duplicate = await viewModel.AuthenticateAsync("duplicate-user", "duplicate-password");
        Assert.Null(duplicate);
        Assert.Single(provider.Requests);

        viewModel.CancelPendingOperation();
        var newer = viewModel.AuthenticateAsync("new-user", "new-password");
        await provider.WaitForCallAsync(2);
        provider.Complete(1, new AuthenticationResult(true, "Authenticated", Identity("new-user", "New User")));
        provider.Complete(0, new AuthenticationResult(true, "Authenticated", Identity("old-user", "Old User")));

        await newer;
        await older;

        Assert.Equal("new-user", viewModel.CurrentIdentity?.UserName);
        Assert.DoesNotContain("Old User", viewModel.IdentitySummary);
    }

    [Fact]
    public async Task V104_U06_ExternalCancellationClearsBusyStateAndAllowsRetry()
    {
        var provider = new BlockingIdentityProvider();
        await using var viewModel = new IdentityViewModel(null, provider, "Station-01");
        using var cancellation = new CancellationTokenSource();

        var pending = viewModel.AuthenticateAsync("alice", "cancelled-password", cancellation.Token);
        await provider.WaitForCallAsync(1);
        cancellation.Cancel();
        provider.Complete(0, new AuthenticationResult(true, "Authenticated", Identity("alice", "Alice")));
        await pending;

        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.HasIdentity);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Contains("取消", viewModel.StatusMessage);

        provider.ImmediateResponses.Enqueue(new AuthenticationResult(true, "Authenticated", Identity("alice", "Alice")));
        var retry = await viewModel.AuthenticateAsync("alice", "retry-password");
        Assert.True(retry?.Succeeded);
        Assert.True(viewModel.HasIdentity);
    }

    [Fact]
    public async Task V104_U07_WhitespacePasswordIsPassedIntactToAuthoritativePolicy()
    {
        var provider = new RecordingIdentityProvider();
        await using var viewModel = new IdentityViewModel(null, provider, "Station-01");
        var password = new string(' ', 15);

        await viewModel.AuthenticateAsync("alice", password);

        Assert.Equal(password, provider.Request?.Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V104_U08_CancelledBootstrapPreservesRecoveryKitReturnedAfterCommit(bool pageCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var secret = new OneTimeSecret("late-recovery-secret");
        var bootstrap = new RecordingBootstrap
        {
            CreateResult = new BootstrapAdministratorResult(true, "AdministratorCreated",
                Identity("alice", "Alice"), RecoveryKit: secret)
        };
        await using var viewModel = new IdentityViewModel(bootstrap, null, "Station-01");
        bootstrap.BeforeReturn = pageCancellation ? viewModel.CancelPendingOperation : cancellation.Cancel;

        var result = await viewModel.CreateFirstAdministratorAsync("token", "alice", "Alice",
            "password", cancellation.Token);

        Assert.True(result?.Succeeded);
        Assert.True(viewModel.HasRecoveryKit);
        Assert.True(viewModel.HasIdentity);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("late-recovery-secret", viewModel.RevealRecoveryKit());
        Assert.Null(viewModel.RevealRecoveryKit());
    }

    private static HumanIdentity Identity(string userName, string displayName) =>
        new(Guid.NewGuid(), userName, displayName);

    private sealed class RecordingBootstrap : ILocalAdministratorBootstrap
    {
        public StationIdentityStatus Status { get; set; } =
            new("Station-01", false, 1, 1, "IdentityReady");
        public BootstrapAdministratorResult CreateResult { get; set; } =
            new(false, "NotConfigured");
        public BootstrapAdministratorRequest? Request { get; private set; }
        public bool ThrowOnCreate { get; set; }
        public Action? BeforeReturn { get; set; }
        public int ProvisionCalls { get; private set; }

        public ValueTask<BootstrapTokenResult> ProvisionBootstrapTokenAsync(
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
            return ValueTask.FromResult(new BootstrapTokenResult(false, "MustNotBeCalled"));
        }

        public ValueTask<BootstrapAdministratorResult> CreateFirstAdministratorAsync(
            BootstrapAdministratorRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (ThrowOnCreate) throw new InvalidOperationException("private failure details");
            BeforeReturn?.Invoke();
            return ValueTask.FromResult(CreateResult);
        }

        public ValueTask<StationIdentityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Status);
    }

    private sealed class RecordingIdentityProvider : IIdentityProvider
    {
        public AuthenticationResult Result { get; set; } = new(false, "NotConfigured");
        public PasswordSignInRequest? Request { get; private set; }

        public ValueTask<AuthenticationResult> AuthenticateAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class BlockingIdentityProvider : IIdentityProvider
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource<AuthenticationResult>> _pending = new();
        public List<PasswordSignInRequest> Requests { get; } = new();
        public Queue<AuthenticationResult> ImmediateResponses { get; } = new();

        public ValueTask<AuthenticationResult> AuthenticateAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Requests.Add(request);
                if (ImmediateResponses.Count != 0)
                    return ValueTask.FromResult(ImmediateResponses.Dequeue());
                var completion = new TaskCompletionSource<AuthenticationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(completion);
                return new ValueTask<AuthenticationResult>(completion.Task);
            }
        }

        public async Task WaitForCallAsync(int count)
        {
            while (true)
            {
                lock (_sync)
                {
                    if (_pending.Count >= count) return;
                }

                await Task.Delay(1);
            }
        }

        public void Complete(int index, AuthenticationResult result)
        {
            lock (_sync) _pending[index].TrySetResult(result);
        }
    }
}
