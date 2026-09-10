using System.Runtime.InteropServices;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class IdentityClipboardSmokeTests
{
    [Fact]
    public async Task V134_W01_TransientClipboardOwnershipContentionCanRecover()
    {
        var calls = 0;
        await IdentityPanel.RetryClipboardSmokeAsync(() =>
        {
            if (++calls < 3) throw new COMException("fixture clipboard busy", unchecked((int)0x800401D0));
        });
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task V134_W02_PersistentContentionRemainsAnObservableFailure()
    {
        var calls = 0;
        await Assert.ThrowsAsync<COMException>(() => IdentityPanel.RetryClipboardSmokeAsync(() =>
        {
            calls++;
            throw new COMException("fixture clipboard busy", unchecked((int)0x800401D0));
        }));
        Assert.InRange(calls, 2, 20);
    }

    [Fact]
    public async Task V134_W03_UnrelatedComFailureIsNeverRetried()
    {
        var calls = 0;
        var failure = new COMException("fixture unrelated failure", unchecked((int)0x80004005));
        var observed = await Assert.ThrowsAsync<COMException>(() => IdentityPanel.RetryClipboardSmokeAsync(() =>
        {
            calls++;
            throw failure;
        }));
        Assert.Same(failure, observed);
        Assert.Equal(1, calls);
    }
}
