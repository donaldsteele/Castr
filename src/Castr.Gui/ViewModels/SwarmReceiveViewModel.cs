using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Castr.Core.Chunking;
using Castr.Core.Discovery;
using Castr.Core.Protocol;
using Castr.Core.Swarm;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Trust;

namespace Castr.Gui.ViewModels;

/// <summary>
/// The mobile Receive flow: the unicast-swarm client surface. Unlike the desktop <see cref="ReceiveViewModel"/>
/// (which joins real IP multicast — something iOS/Android cannot reliably do), this browses the LAN for peers
/// via an injected <see cref="IServiceDiscovery"/> (native <c>NsdManager</c>/<c>NWBrowser</c> in production, an
/// in-memory fake in tests), lets the user pick a discovered peer, and drives a real
/// <see cref="SwarmPullSession"/> that pulls a signed, verified, encrypted transfer over unicast TCP.
/// <para>
/// Everything is injected, so the whole flow — browse → select → pull → progress → TOFU prompt — is unit-testable
/// against <c>InMemoryServiceDiscovery</c> + <c>InMemoryStreamNetwork</c> with no device, emulator, or socket.
/// Platform-neutral by construction, so the future iOS head reuses it verbatim. Progress and discovery callbacks
/// are marshaled onto the UI thread via <see cref="Dispatcher"/>, exactly like <see cref="ReceiveViewModel"/>.
/// </para>
/// </summary>
public sealed partial class SwarmReceiveViewModel : ObservableObject, IDisposable
{
    private readonly IServiceDiscovery _discovery;
    private readonly IStreamClient _streamClient;
    private readonly ITrustStore _trustStore;
    private readonly byte[] _receiverId;
    private readonly SwarmPullSessionOptions _options;
    private readonly Func<string, long, IFileSink> _sinkFactory;
    private readonly ISystemClock _clock;

    private readonly HashSet<Endpoint> _knownEndpoints = [];
    private SwarmPullSession? _session;
    private CancellationTokenSource? _browseCts;
    private CancellationTokenSource? _pullCts;
    private Task? _browseTask;

    public SwarmReceiveViewModel(
        IServiceDiscovery discovery,
        IStreamClient streamClient,
        ITrustStore trustStore,
        byte[] receiverId,
        SwarmPullSessionOptions options,
        Func<string, long, IFileSink> sinkFactory,
        ISystemClock? clock = null)
    {
        _discovery = discovery;
        _streamClient = streamClient;
        _trustStore = trustStore;
        _receiverId = receiverId;
        _options = options;
        _sinkFactory = sinkFactory;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Peers discovered on the LAN, in first-seen order, de-duplicated by endpoint.</summary>
    public ObservableCollection<DiscoveredPeer> Peers { get; } = [];

    /// <summary>The in-page Trust-On-First-Use prompt surface. The view binds an overlay to <see cref="InAppTrustPrompt.Pending"/>.</summary>
    public InAppTrustPrompt Trust { get; } = new();

    /// <summary>The latest transfer-progress snapshot, shared shape with every other Castr surface.</summary>
    public TransferProgressViewModel Progress { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartBrowsingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopBrowsingCommand))]
    private bool _isBrowsing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    private bool _isPulling;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    private DiscoveredPeer? _selectedPeer;

    [ObservableProperty]
    private string _status = "Browse for a Castr peer on your network, then pull.";

    private bool CanStartBrowsing => !IsBrowsing;

    [RelayCommand(CanExecute = nameof(CanStartBrowsing))]
    private void StartBrowsing()
    {
        IsBrowsing = true;
        Status = "Browsing for peers…";
        _browseCts = new CancellationTokenSource();
        _browseTask = BrowseLoopAsync(_browseCts.Token);
    }

    private bool CanStopBrowsing => IsBrowsing;

    [RelayCommand(CanExecute = nameof(CanStopBrowsing))]
    private void StopBrowsing()
    {
        _browseCts?.Cancel();
        IsBrowsing = false;
        Status = Peers.Count == 0 ? "No peers found. Try again." : "Stopped browsing.";
    }

    private async Task BrowseLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var peer in _discovery.BrowseAsync(token).ConfigureAwait(false))
            {
                var found = peer;
                Dispatcher.UIThread.Post(() => AddPeer(found));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Status = $"Discovery failed: {ex.Message}");
        }
    }

    /// <summary>Adds a newly-discovered peer if its endpoint is not already listed. UI-thread only.</summary>
    private void AddPeer(DiscoveredPeer peer)
    {
        if (!_knownEndpoints.Add(peer.Endpoint))
            return;
        Peers.Add(peer);
        if (SelectedPeer is null)
            SelectedPeer = peer;
        Status = $"{Peers.Count} peer(s) found. Select one and pull.";
    }

    private bool CanPull => SelectedPeer is not null && !IsPulling;

    [RelayCommand(CanExecute = nameof(CanPull))]
    private async Task PullAsync()
    {
        var peer = SelectedPeer;
        if (peer is null)
            return;

        IsPulling = true;
        _pullCts = new CancellationTokenSource();
        try
        {
            var session = EnsureSession();
            Status = $"Pulling from {peer.ServiceName} ({peer.Endpoint})…";

            bool accepted = await session.PullFromAsync(peer.Endpoint, _pullCts.Token).ConfigureAwait(true);

            if (!accepted)
                Status = "Peer refused: untrusted sender or no manifest offered.";
            else if (session.IsComplete)
                Status = "Transfer complete.";
            else
                Status = "Partial transfer — pick another peer to fetch the rest.";
        }
        catch (OperationCanceledException)
        {
            Status = "Pull cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Pull failed: {ex.Message}";
        }
        finally
        {
            _pullCts?.Dispose();
            _pullCts = null;
            IsPulling = false;
        }
    }

    /// <summary>Cancels an in-flight pull.</summary>
    [RelayCommand]
    private void CancelPull() => _pullCts?.Cancel();

    /// <summary>
    /// Creates the resumable pull session lazily and reuses it across pulls, so a transfer partly fetched from
    /// one peer resumes against another — the exact resumability <see cref="SwarmPullSession"/> is built for.
    /// </summary>
    private SwarmPullSession EnsureSession()
    {
        if (_session is not null)
            return _session;

        var session = new SwarmPullSession(
            _receiverId, _trustStore, _streamClient, _clock, _options, _sinkFactory,
            trustPrompt: _options.IsInteractive ? Trust : null);

        session.ProgressChanged += OnProgress;
        session.SenderTrustDenied += OnTrustDenied;
        _session = session;
        return session;
    }

    private void OnProgress(TransferProgress progress) =>
        Dispatcher.UIThread.Post(() => Progress.Update(progress));

    private void OnTrustDenied(TrustDecision decision, Castr.Core.Security.PublicKeyId senderId) =>
        Dispatcher.UIThread.Post(() => Status = $"Trust denied for sender {senderId.Value} ({decision.Outcome}).");

    public void Dispose()
    {
        _browseCts?.Cancel();
        _browseCts?.Dispose();
        _pullCts?.Cancel();
        _pullCts?.Dispose();
        _session?.Dispose();
    }
}
