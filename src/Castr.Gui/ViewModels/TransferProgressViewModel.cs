using CommunityToolkit.Mvvm.ComponentModel;
using Castr.Core.Protocol;

namespace Castr.Gui.ViewModels;

/// <summary>
/// A bindable read-model over the latest <see cref="TransferProgress"/> snapshot emitted by a session.
/// Purely presentational: <see cref="Update"/> copies one immutable snapshot into observable properties the
/// view binds to. <see cref="Update"/> mutates UI-bound state and therefore MUST be called on the UI thread —
/// callers marshal the session's <c>ProgressChanged</c> callback through <c>Dispatcher.UIThread</c> first.
/// </summary>
public sealed partial class TransferProgressViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _hasProgress;

    [ObservableProperty]
    private string _role = "";

    [ObservableProperty]
    private TransferPhase _phase = TransferPhase.Starting;

    [ObservableProperty]
    private string _phaseText = "Idle";

    [ObservableProperty]
    private string _transferName = "";

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private int _totalChunks;

    [ObservableProperty]
    private int _completedChunks;

    [ObservableProperty]
    private int _pendingChunks;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _completedBytes;

    [ObservableProperty]
    private int _peerCount;

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private string _summary = "No transfer running.";

    /// <summary>Copies one snapshot into the bound properties. Call on the UI thread only.</summary>
    public void Update(TransferProgress p)
    {
        HasProgress = true;
        Role = p.Role.ToString();
        Phase = p.Phase;
        PhaseText = DescribePhase(p.Phase);
        TransferName = p.TransferName;
        TotalFiles = p.TotalFiles;
        TotalChunks = p.TotalChunks;
        CompletedChunks = p.CompletedChunks;
        PendingChunks = p.PendingChunks;
        TotalBytes = p.TotalBytes;
        CompletedBytes = p.CompletedBytes;
        PeerCount = p.PeerCount;
        Fraction = p.FractionComplete;
        Percent = p.FractionComplete * 100.0;
        IsComplete = p.IsComplete;
        Summary = BuildSummary(p);
    }

    private static string DescribePhase(TransferPhase phase) => phase switch
    {
        TransferPhase.Starting => "Starting…",
        TransferPhase.AwaitingKey => "Awaiting content key…",
        TransferPhase.Transferring => "Transferring…",
        TransferPhase.Serving => "Serving (repairs)…",
        TransferPhase.Completed => "Completed",
        TransferPhase.TrustDenied => "Trust denied",
        _ => phase.ToString(),
    };

    private static string BuildSummary(TransferProgress p)
    {
        string name = string.IsNullOrEmpty(p.TransferName) ? "(unnamed)" : p.TransferName;
        return $"{name} — {DescribePhase(p.Phase)}  " +
               $"{p.CompletedChunks}/{p.TotalChunks} chunks  " +
               $"{FormatBytes(p.CompletedBytes)}/{FormatBytes(p.TotalBytes)}  " +
               $"peers: {p.PeerCount}";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}
