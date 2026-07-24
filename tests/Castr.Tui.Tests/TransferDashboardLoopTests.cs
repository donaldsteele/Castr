using Castr.Core.Protocol;
using Castr.Tui;
using Spectre.Console.Testing;

namespace Castr.Tui.Tests;

/// <summary>
/// Exercises the live render loop directly (via the internal <c>RunLoopAsync</c> seam) with synthetic
/// progress events, proving it subscribes, refreshes on events, and terminates on completion/cancellation.
/// </summary>
public class TransferDashboardLoopTests
{
    private sealed class FakeProgressSource
    {
        public event Action<TransferProgress>? ProgressChanged;
        public bool Complete;
        public void Emit(TransferProgress progress) => ProgressChanged?.Invoke(progress);
    }

    private static TransferProgress Progress(TransferPhase phase, int completed, int total) =>
        new(TransferRole.Receiver, phase, "loop-transfer", 1, total, completed,
            total - completed, total, completed, 1);

    [Fact]
    public async Task Loop_Terminates_On_Completion_And_Reflects_Final_State()
    {
        var console = new TestConsole();
        var dashboard = new TransferDashboard(console, TimeSpan.FromMilliseconds(20));
        var source = new FakeProgressSource();

        var run = dashboard.RunLoopAsync(
            h => source.ProgressChanged += h,
            h => source.ProgressChanged -= h,
            () => source.Complete,
            CancellationToken.None);

        source.Emit(Progress(TransferPhase.Transferring, 30, 100));
        await Task.Delay(40);
        source.Complete = true;
        source.Emit(Progress(TransferPhase.Completed, 100, 100));

        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Completed", console.Output);
        Assert.Contains("100%", console.Output);
    }

    [Fact]
    public async Task Loop_Terminates_On_Cancellation_Without_Throwing()
    {
        var console = new TestConsole();
        var dashboard = new TransferDashboard(console, TimeSpan.FromMilliseconds(20));
        var source = new FakeProgressSource();
        using var cts = new CancellationTokenSource();

        var run = dashboard.RunLoopAsync(
            h => source.ProgressChanged += h,
            h => source.ProgressChanged -= h,
            () => false, // never completes on its own
            cts.Token);

        source.Emit(Progress(TransferPhase.Transferring, 10, 100));
        await Task.Delay(40);
        cts.Cancel();

        // Should complete normally (cancellation is a graceful stop, not a fault).
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Transferring", console.Output);
    }

    [Fact]
    public async Task Loop_Stops_On_TrustDenied_Snapshot()
    {
        var console = new TestConsole();
        var dashboard = new TransferDashboard(console, TimeSpan.FromMilliseconds(20));
        var source = new FakeProgressSource();

        var run = dashboard.RunLoopAsync(
            h => source.ProgressChanged += h,
            h => source.ProgressChanged -= h,
            () => false,
            CancellationToken.None);

        source.Emit(new TransferProgress(
            TransferRole.Receiver, TransferPhase.TrustDenied, "blocked", 1, 10, 0, 10, 100, 0, 0));

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Trust denied", console.Output);
    }
}
