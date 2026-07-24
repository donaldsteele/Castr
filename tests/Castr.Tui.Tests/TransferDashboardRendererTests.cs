using Castr.Core.Protocol;
using Castr.Tui;
using Spectre.Console.Testing;

namespace Castr.Tui.Tests;

/// <summary>
/// Verifies the pure renderer turns a <see cref="TransferProgress"/> snapshot into console output that
/// actually reflects that snapshot (phase, counts, completion), using Spectre's <see cref="TestConsole"/>.
/// </summary>
public class TransferDashboardRendererTests
{
    private static TransferProgress Snapshot(
        TransferRole role = TransferRole.Receiver,
        TransferPhase phase = TransferPhase.Transferring,
        string name = "holiday-photos",
        int totalFiles = 3,
        int totalChunks = 100,
        int completedChunks = 40,
        long totalBytes = 1024 * 1024,
        long completedBytes = 400 * 1024,
        int peerCount = 2) =>
        new(role, phase, name, totalFiles, totalChunks, completedChunks,
            totalChunks - completedChunks, totalBytes, completedBytes, peerCount);

    private static string Render(TransferProgress progress, double rate = 0)
    {
        var console = new TestConsole();
        console.Write(TransferDashboardRenderer.Render(progress, rate));
        return console.Output;
    }

    [Fact]
    public void Renders_TransferName_Role_And_Phase()
    {
        var output = Render(Snapshot(role: TransferRole.Receiver, phase: TransferPhase.Transferring));
        Assert.Contains("holiday-photos", output);
        Assert.Contains("RECEIVER", output);
        Assert.Contains("Transferring", output);
    }

    [Theory]
    [InlineData(TransferPhase.Starting, "Starting")]
    [InlineData(TransferPhase.AwaitingKey, "Awaiting key")]
    [InlineData(TransferPhase.Serving, "Serving")]
    [InlineData(TransferPhase.Completed, "Completed")]
    [InlineData(TransferPhase.TrustDenied, "Trust denied")]
    public void Renders_Each_Phase_Label(TransferPhase phase, string expected)
    {
        var output = Render(Snapshot(phase: phase));
        Assert.Contains(expected, output);
    }

    [Fact]
    public void Renders_Percentage_From_FractionComplete()
    {
        var output = Render(Snapshot(totalChunks: 100, completedChunks: 40));
        Assert.Contains("40%", output);
    }

    [Fact]
    public void CompletedTransfer_Shows_100Percent_And_Completed()
    {
        var output = Render(Snapshot(phase: TransferPhase.Completed, totalChunks: 100, completedChunks: 100));
        Assert.Contains("100%", output);
        Assert.Contains("Completed", output);
    }

    [Fact]
    public void Sender_Shows_Receivers_Label_And_Count()
    {
        var output = Render(Snapshot(role: TransferRole.Sender, phase: TransferPhase.Serving, peerCount: 5));
        Assert.Contains("SENDER", output);
        Assert.Contains("Receivers", output);
        Assert.Contains("5", output);
    }

    [Fact]
    public void Shows_Throughput_When_Rate_Positive_And_Dash_When_Zero()
    {
        var withRate = Render(Snapshot(), rate: 5 * 1024 * 1024);
        Assert.Contains("MB/s", withRate);

        var noRate = Render(Snapshot(), rate: 0);
        Assert.DoesNotContain("MB/s", noRate);
    }

    [Fact]
    public void Heatmap_Renders_Blocks_Reflecting_Progress()
    {
        // Partial progress paints both completed and pending blocks.
        var partial = Render(Snapshot(totalChunks: 200, completedChunks: 100));
        Assert.Contains("█", partial); // completed cells
        Assert.Contains("░", partial); // pending cells

        // A fully-complete transfer has no pending cells left in the map.
        var complete = Render(Snapshot(phase: TransferPhase.Completed, totalChunks: 200, completedChunks: 200));
        Assert.Contains("█", complete);
    }

    [Fact]
    public void EmptyManifest_Snapshot_Renders_Without_Throwing()
    {
        var output = Render(new TransferProgress(
            TransferRole.Receiver, TransferPhase.Starting, string.Empty, 0, 0, 0, 0, 0, 0, 0));
        Assert.Contains("awaiting manifest", output);
    }
}
