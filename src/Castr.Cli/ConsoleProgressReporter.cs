using Spectre.Console;
using Castr.Core.Protocol;

namespace Castr.Cli;

/// <summary>
/// Plain, script-friendly progress output for headless (non-<c>--tui</c>) mode. Prints a line on every phase
/// change and whenever completion crosses a new whole-percent bucket, so a running send/receive produces a
/// readable, greppable log without flooding stdout. Progress events arrive on the session's own threads, so
/// all writes are serialized under a lock.
/// </summary>
internal sealed class ConsoleProgressReporter(IAnsiConsole console, string label)
{
    private readonly object _gate = new();
    private TransferPhase? _lastPhase;
    private int _lastPercent = -1;

    public void OnProgress(TransferProgress progress)
    {
        lock (_gate)
        {
            int percent = (int)(progress.FractionComplete * 100);
            bool phaseChanged = progress.Phase != _lastPhase;
            if (!phaseChanged && percent == _lastPercent)
                return;

            _lastPhase = progress.Phase;
            _lastPercent = percent;

            console.WriteLine(
                $"[{label}] {progress.Phase} {progress.CompletedChunks}/{progress.TotalChunks} chunks " +
                $"({percent}%), {FormatBytes(progress.CompletedBytes)}/{FormatBytes(progress.TotalBytes)}" +
                (progress.PeerCount > 0 ? $", {progress.PeerCount} peer(s)" : string.Empty));
        }
    }

    public void Line(string message)
    {
        lock (_gate)
            console.WriteLine($"[{label}] {message}");
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };
}
