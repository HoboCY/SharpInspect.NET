using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class AsyncRelayCommandTests
{
    [Fact]
    public async Task TransportFailureIsReportedWithoutEscapingVoidCommandExecution()
    {
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("transport unavailable");
        var command = new AsyncRelayCommand(() => Task.FromException(expected));
        command.ExecutionFailed += (_, exception) => failure.TrySetResult(exception);

        command.Execute(null);

        var observed = await failure.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Same(expected, observed);
    }
}
