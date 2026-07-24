using Castr.Core.Protocol;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Castr.Tui;

/// <summary>
/// Builds the colorful dashboard layout for a single <see cref="TransferProgress"/> snapshot. Pure and
/// side-effect free: given the same snapshot and rate it always produces the same <see cref="IRenderable"/>,
/// which makes it directly unit-testable against a <c>TestConsole</c>.
/// </summary>
public static class TransferDashboardRenderer
{
    /// <summary>
    /// Composes the dashboard for <paramref name="progress"/>. <paramref name="bytesPerSecond"/> is the
    /// smoothed aggregate throughput (0 if unknown).
    /// </summary>
    public static IRenderable Render(TransferProgress progress, double bytesPerSecond = 0)
    {
        var (phaseText, phaseColor) = DescribePhase(progress.Phase);
        string roleText = progress.Role == TransferRole.Sender ? "SENDER" : "RECEIVER";
        string title = string.IsNullOrEmpty(progress.TransferName) ? "(awaiting manifest)" : progress.TransferName;

        var header = new Markup($"[bold]{Markup.Escape(title)}[/]  [grey]·[/]  [blue]{roleText}[/]  [grey]·[/]  [bold {phaseColor}]{phaseText}[/]");

        var stats = new Grid();
        stats.AddColumn();
        stats.AddColumn();
        stats.AddRow(new Markup("[grey]Files[/]"), new Markup($"{progress.TotalFiles}"));
        stats.AddRow(
            new Markup("[grey]Chunks[/]"),
            new Markup($"[green]{progress.CompletedChunks}[/] / {progress.TotalChunks}  [grey]({progress.PendingChunks} pending)[/]"));
        stats.AddRow(
            new Markup("[grey]Bytes[/]"),
            new Markup($"[green]{FormatBytes(progress.CompletedBytes)}[/] / {FormatBytes(progress.TotalBytes)}"));
        stats.AddRow(
            new Markup(progress.Role == TransferRole.Sender ? "[grey]Receivers[/]" : "[grey]Peers[/]"),
            new Markup($"{progress.PeerCount}"));
        stats.AddRow(
            new Markup("[grey]Throughput[/]"),
            new Markup(bytesPerSecond > 0 ? $"[aqua]{FormatBytes((long)bytesPerSecond)}/s[/]" : "[grey]—[/]"));

        var bar = ProgressBar(progress.FractionComplete, phaseColor);
        var heatmap = new ChunkHeatmap(progress.CompletedChunks, progress.TotalChunks);

        var body = new Grid();
        body.AddColumn();
        body.AddRow(bar);
        body.AddRow(new Text(string.Empty));
        body.AddRow(stats);
        body.AddRow(new Text(string.Empty));
        body.AddRow(new Markup("[grey]Chunk map[/] [grey35](completion density across the chunk space)[/]"));
        body.AddRow(heatmap);

        var panel = new Panel(body)
        {
            Header = new PanelHeader(" Castr transfer "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(phaseColor),
            Padding = new Padding(1, 0, 1, 0),
        };

        return new Rows(header, panel);
    }

    private static IRenderable ProgressBar(double fraction, Color color)
    {
        const int width = 40;
        fraction = Math.Clamp(fraction, 0, 1);
        int filled = (int)Math.Round(fraction * width);
        string filledPart = new string('█', filled);
        string emptyPart = new string('░', width - filled);
        int percent = (int)Math.Round(fraction * 100);
        return new Markup($"[{color}]{filledPart}[/][grey35]{emptyPart}[/]  [bold]{percent,3}%[/]");
    }

    private static (string Text, Color Color) DescribePhase(TransferPhase phase) => phase switch
    {
        TransferPhase.Starting => ("Starting", Color.Grey),
        TransferPhase.AwaitingKey => ("Awaiting key", Color.Yellow),
        TransferPhase.Transferring => ("Transferring", Color.Aqua),
        TransferPhase.Serving => ("Serving (repair)", Color.Blue),
        TransferPhase.Completed => ("Completed", Color.Green),
        TransferPhase.TrustDenied => ("Trust denied", Color.Red),
        _ => (phase.ToString(), Color.White),
    };

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{(long)value} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
