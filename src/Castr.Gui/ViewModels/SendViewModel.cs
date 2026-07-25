using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Protocol;
using Castr.Core.Transport;
using Castr.Gui.Services;

namespace Castr.Gui.ViewModels;

/// <summary>
/// The Send flow: pick a file, build a signed manifest + content key for it, and drive a real
/// <see cref="SenderSession"/> over a multicast transport, surfacing live <see cref="TransferProgress"/>.
/// </summary>
public sealed partial class SendViewModel : ObservableObject
{
    private readonly ITransportFactory _transports;
    private readonly Key _signingKey;

    private CancellationTokenSource? _cts;
    private IMulticastTransport? _transport;

    public SendViewModel(ITransportFactory transports, Key signingKey)
    {
        _transports = transports;
        _signingKey = signingKey;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _filePath = "";

    /// <summary>
    /// Hash/repair granularity, 256 KiB — kept in step with <c>Castr.Cli.CastrPaths.DefaultChunkSize</c> and
    /// <see cref="Castr.Core.Chunking.ChunkLayout.DefaultChunkSize"/> so all three surfaces ship the same default.
    /// See the CastrPaths comment for the measured rationale (1.33x over the previous 8192). The view's
    /// NumericUpDown bound must stay above this or it silently clamps the default back down — it was 60000,
    /// which would have made this change invisible on this surface.
    /// </summary>
    [ObservableProperty]
    private int _chunkSize = ChunkLayout.DefaultChunkSize;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "Pick a file to send.";

    /// <summary>Live progress of the running send. Bound directly by the view.</summary>
    public TransferProgressViewModel Progress { get; } = new();

    /// <summary>Set by the view: opens a file picker and returns the chosen path (or null if cancelled).</summary>
    public Func<Task<string?>>? FilePicker { get; set; }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (FilePicker is null)
            return;
        var picked = await FilePicker().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(picked))
            FilePath = picked;
    }

    private bool CanStart => !IsRunning && !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsRunning = true;
        Status = "Preparing transfer…";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var prepared = await TransferBuilder
                .PrepareFileAsync(FilePath, _signingKey, ChunkSize, token)
                .ConfigureAwait(true);

            _transport = _transports.CreateSenderTransport();
            var session = new SenderSession(
                prepared.Signed, prepared.Sources, prepared.Trees, _transport,
                prepared.SenderEncryptionKey, prepared.ContentKey);
            session.ProgressChanged += OnProgress;

            Status = $"Serving \"{prepared.Signed.Manifest.TransferName}\" on the LAN…";
            await session.RunAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Status = "Send stopped.";
        }
        catch (Exception ex)
        {
            Status = $"Send failed: {ex.Message}";
        }
        finally
        {
            await StopTransportAsync().ConfigureAwait(true);
            IsRunning = false;
        }
    }

    private bool CanStop => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    /// <summary>
    /// Marshals the session's progress callback (which fires on the session's own thread) onto the UI thread
    /// before touching any bound state.
    /// </summary>
    private void OnProgress(TransferProgress progress) =>
        Dispatcher.UIThread.Post(() => Progress.Update(progress));

    private async Task StopTransportAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(true);
            _transport = null;
        }
        _cts?.Dispose();
        _cts = null;
    }
}
