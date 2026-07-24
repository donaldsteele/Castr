using System.Security.Cryptography;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Castr.Core.Chunking;
using Castr.Core.Protocol;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Trust;
using Castr.Gui.Services;

namespace Castr.Gui.ViewModels;

/// <summary>
/// The Receive flow: choose a destination directory and an unknown-sender policy, then drive a real
/// <see cref="ReceiverSession"/>. When the policy is "Prompt me" the session consults the injected
/// <see cref="ITrustPrompt"/> (the GUI dialog) on an unknown sender; otherwise unknown senders are denied.
/// A periodic repair loop re-drives the key handshake and re-requests missing chunks over lossy links.
/// </summary>
public sealed partial class ReceiveViewModel : ObservableObject
{
    private readonly ITransportFactory _transports;
    private readonly ITrustStore _trustStore;
    private readonly ITrustPrompt _trustPrompt;
    private readonly ISystemClock _clock;

    private CancellationTokenSource? _cts;
    private IMulticastTransport? _transport;

    public ReceiveViewModel(
        ITransportFactory transports, ITrustStore trustStore, ITrustPrompt trustPrompt, ISystemClock? clock = null)
    {
        _transports = transports;
        _trustStore = trustStore;
        _trustPrompt = trustPrompt;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>The unknown-sender policies a user can pick, mapped to Core's <see cref="UnknownSenderPolicy"/>.</summary>
    public IReadOnlyList<string> PolicyChoices { get; } =
        ["Deny unknown senders", "Prompt me (Trust-On-First-Use)", "Queue for later review"];

    [ObservableProperty]
    private int _selectedPolicyIndex; // 0 = Deny, 1 = Prompt, 2 = Queue

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _destinationDirectory = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "Choose a destination and start listening.";

    public TransferProgressViewModel Progress { get; } = new();

    /// <summary>Set by the view: opens a folder picker and returns the chosen path (or null if cancelled).</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (FolderPicker is null)
            return;
        var picked = await FolderPicker().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(picked))
            DestinationDirectory = picked;
    }

    private UnknownSenderPolicy Policy => SelectedPolicyIndex switch
    {
        1 => UnknownSenderPolicy.Prompt,
        2 => UnknownSenderPolicy.Queue,
        _ => UnknownSenderPolicy.Deny,
    };

    private bool CanStart => !IsRunning && !string.IsNullOrWhiteSpace(DestinationDirectory);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            Directory.CreateDirectory(DestinationDirectory);
            _transport = _transports.CreateReceiverTransport();

            bool interactive = Policy == UnknownSenderPolicy.Prompt;
            var options = new ReceiverSessionOptions(DestinationDirectory, Policy, IsInteractive: interactive);
            var receiverId = RandomNumberGenerator.GetBytes(16);

            var session = new ReceiverSession(
                receiverId, _trustStore, _transport, _clock, options,
                sinkFactory: (destination, length) => new FileSystemFileSink(destination, length),
                trustPrompt: interactive ? _trustPrompt : null);
            session.ProgressChanged += OnProgress;
            session.SenderTrustDenied += OnTrustDenied;

            Status = "Listening for a transfer…";
            var run = session.RunAsync(token);
            var repairs = RunRepairLoopAsync(session, token);
            await Task.WhenAll(run, repairs).ConfigureAwait(true);

            Status = session.IsComplete ? "Transfer complete." : "Stopped.";
        }
        catch (OperationCanceledException)
        {
            Status = "Receive stopped.";
        }
        catch (Exception ex)
        {
            Status = $"Receive failed: {ex.Message}";
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

    /// <summary>Periodically re-drives the JOIN/KEY_GRANT handshake and re-requests any missing chunks.</summary>
    private static async Task RunRepairLoopAsync(ReceiverSession session, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !session.IsComplete)
            {
                await session.RequestRepairsAsync(token).ConfigureAwait(false);
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void OnProgress(TransferProgress progress) =>
        Dispatcher.UIThread.Post(() => Progress.Update(progress));

    private void OnTrustDenied(TrustDecision decision, Castr.Core.Security.PublicKeyId senderId) =>
        Dispatcher.UIThread.Post(() => Status = $"Trust denied for sender {senderId.Value} ({decision.Outcome}).");

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
