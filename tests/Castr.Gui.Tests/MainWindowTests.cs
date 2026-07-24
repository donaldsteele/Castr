using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Castr.Core.Protocol;
using Castr.Gui.ViewModels;
using Castr.Gui.Views;

namespace Castr.Gui.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindow_Launches_WithoutThrowing()
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();

        Assert.NotNull(window);
        Assert.Equal("Castr — LAN multicast file transfer", window.Title);
        // The Send tab is realized and its controls exist in the visual tree.
        Assert.NotEmpty(window.GetVisualDescendants().OfType<Button>());
    }

    [AvaloniaFact]
    public void ProgressDisplay_Reflects_PushedSnapshot()
    {
        var main = new MainViewModel();
        var window = new MainWindow { DataContext = main };
        window.Show();

        // Push a real TransferProgress snapshot through the Send flow's progress view-model (UI thread).
        var snapshot = new TransferProgress(
            TransferRole.Receiver, TransferPhase.Transferring, "demo.bin",
            TotalFiles: 1, TotalChunks: 10, CompletedChunks: 5, PendingChunks: 5,
            TotalBytes: 1000, CompletedBytes: 500, PeerCount: 2);
        main.Send.Progress.Update(snapshot);

        Dispatcher.UIThread.RunJobs();

        // View-model reflects the snapshot.
        Assert.True(main.Send.Progress.HasProgress);
        Assert.Equal(50.0, main.Send.Progress.Percent, 3);
        Assert.Equal("Transferring…", main.Send.Progress.PhaseText);

        // And the rendered control tree reflects it: a ProgressBar bound to Percent now reads 50.
        var progressBars = window.GetVisualDescendants().OfType<ProgressBar>().ToList();
        Assert.Contains(progressBars, pb => System.Math.Abs(pb.Value - 50.0) < 0.001);

        var summaries = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "").ToList();
        Assert.Contains(summaries, s => s.Contains("demo.bin") && s.Contains("5/10 chunks"));
    }
}
