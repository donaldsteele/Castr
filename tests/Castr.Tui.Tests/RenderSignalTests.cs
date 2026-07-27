using Castr.Tui;

namespace Castr.Tui.Tests;

/// <summary>
/// The render loop's wake-up. It used to be a maximum-less <c>SemaphoreSlim(0)</c> whose
/// <c>catch (SemaphoreFullException)</c> could never fire, so permits accumulated one per progress event —
/// and a receiver raises one per verified chunk.
/// </summary>
public class RenderSignalTests
{
    [Fact]
    public void ManySignalsWithNoWaiter_CollapseIntoOne()
    {
        // The defect, directly: a thousand chunks verified while the loop is rendering used to buy a thousand
        // immediate iterations and a thousand full re-renders of a terminal that can show maybe ten a second.
        using var signal = new RenderSignal();

        for (int i = 0; i < 1000; i++)
            signal.Signal();

        Assert.Equal(1, signal.PendingWakes);
    }

    [Fact]
    public async Task OneSignal_ReleasesExactlyOneWait()
    {
        using var signal = new RenderSignal();

        signal.Signal();
        signal.Signal();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
        Assert.Equal(0, signal.PendingWakes);
    }

    [Fact]
    public async Task ConcurrentSignals_NeverThrow_AndStayBounded()
    {
        // Signal() is called from session threads, so the coalescing path is genuinely concurrent.
        using var signal = new RenderSignal();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
                signal.Signal();
        })));

        Assert.Equal(1, signal.PendingWakes);
    }

    [Fact]
    public async Task WaitWithNoSignal_TimesOutRatherThanHanging()
    {
        using var signal = new RenderSignal();

        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }
}
