using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Castr.Core.Discovery;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Swarm;
using Castr.Core.Transport;
using Castr.Core.Trust;

namespace Castr.Gui.ViewModels;

/// <summary>
/// The mobile receive flow — the platform-neutral view-model both the iOS and Android heads bind to.
/// <para>
/// Mobile devices cannot join IP multicast (see the project's "why mobile is architecturally different"
/// note and ADR-0002), so this flow is fundamentally different from the desktop <see cref="ReceiveViewModel"/>:
/// it <b>browses</b> for peers via <see cref="IServiceDiscovery"/> (native mDNS/Bonjour under the hood),
/// lets the user pick a discovered peer, and drives a real <see cref="SwarmPullSession"/> — a unicast-TCP
/// swarm pull that runs the exact same signature + TOFU trust gate and Merkle/AEAD verification the multicast
/// tier uses. It surfaces the identical <see cref="TransferProgress"/> contract, so
/// <see cref="TransferProgressViewModel"/> is reused verbatim.
/// </para>
/// <para>
/// Composition is injected so this stays head-agnostic and fully unit-testable: a real head passes a
/// <see cref="Network.NWBrowser"/>-backed (iOS) / NsdManager-backed (Android) discovery plus a
/// TCP-stream-client-backed session; a test passes an in-memory discovery + in-memory-stream session and
/// drives the whole flow with no sockets. The <see cref="SwarmPullSession"/> is resumable, so a single
/// session instance is reused across pulls from different peers — pulling again requests only still-missing
/// chunks, exactly as the swarm design intends.
/// </para>
/// </summary>
public sealed partial class MobileReceiveViewModel : ObservableObject, ITrustPrompt, IDisposable
{
    private readonly IServiceDiscovery _discovery;
    private readonly Func<ITrustPrompt, SwarmPullSession> _sessionFactory;
    private readonly HashSet<Endpoint> _knownEndpoints = [];

    private SwarmPullSession? _session;
    private CancellationTokenSource? _browseCts;
    private Task? _browseTask;

    /// <param name="discovery">Local-network peer discovery (native mDNS on device; in-memory in tests).</param>
    /// <param name="sessionFactory">
    /// Builds the (resumable) <see cref="SwarmPullSession"/> the first time a pull starts. The head supplies
    /// everything platform-specific here — the receiver identity, trust store, stream client, and destination
    /// sink factory (the app sandbox on mobile). The <see cref="ITrustPrompt"/> handed to the factory is this
    /// view-model itself, so an unknown sender surfaces the in-app Trust-On-First-Use prompt
    /// (<see cref="PendingTrustPrompt"/>) rather than a desktop-only modal window.
    /// </param>
    public MobileReceiveViewModel(IServiceDiscovery discovery, Func<ITrustPrompt, SwarmPullSession> sessionFactory)
    {
        _discovery = discovery;
        _sessionFactory = sessionFactory;
    }

    /// <summary>Peers discovered on the local network, newest last. Bound to the peer list.</summary>
    public ObservableCollection<DiscoveredPeerItem> Peers { get; } = [];

    public TransferProgressViewModel Progress { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    private DiscoveredPeerItem? _selectedPeer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartBrowsingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopBrowsingCommand))]
    private bool _isBrowsing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    private bool _isPulling;

    [ObservableProperty]
    private string _status = "Tap “Find peers” to discover devices sharing on your network.";

    /// <summary>
    /// Non-null while an unknown sender is awaiting a Trust-On-First-Use decision. The mobile view binds an
    /// inline accept/reject panel to this; the pull is blocked until the user decides.
    /// </summary>
    [ObservableProperty]
    private TrustPromptViewModel? _pendingTrustPrompt;

    private bool CanStartBrowsing => !IsBrowsing;

    /// <summary>Starts (or restarts) the discovery browse loop, streaming peers into <see cref="Peers"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanStartBrowsing))]
    private void StartBrowsing()
    {
        IsBrowsing = true;
        Status = "Searching for peers…";
        _browseCts = new CancellationTokenSource();
        _browseTask = BrowseLoopAsync(_browseCts.Token);
    }

    private bool CanStopBrowsing => IsBrowsing;

    [RelayCommand(CanExecute = nameof(CanStopBrowsing))]
    private void StopBrowsing()
    {
        _browseCts?.Cancel();
        IsBrowsing = false;
        Status = Peers.Count == 0 ? "No peers found. Try again." : $"{Peers.Count} peer(s) found.";
    }

    private async Task BrowseLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var peer in _discovery.BrowseAsync(token).ConfigureAwait(false))
                Dispatcher.UIThread.Post(() => AddPeer(peer));
        }
        catch (OperationCanceledException)
        {
            // normal shutdown of the browse loop
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Status = $"Discovery failed: {ex.Message}");
        }
    }

    /// <summary>Adds a newly discovered peer, de-duplicating by endpoint. UI thread only.</summary>
    private void AddPeer(DiscoveredPeer peer)
    {
        if (!_knownEndpoints.Add(peer.Endpoint))
            return;
        Peers.Add(new DiscoveredPeerItem(peer));
        SelectedPeer ??= Peers[0];
        if (IsBrowsing)
            Status = $"{Peers.Count} peer(s) found.";
    }

    private bool CanPull => SelectedPeer is not null && !IsPulling;

    /// <summary>Pulls the transfer from the selected peer over unicast TCP, resuming an in-progress session.</summary>
    [RelayCommand(CanExecute = nameof(CanPull))]
    private async Task PullAsync()
    {
        var target = SelectedPeer;
        if (target is null)
            return;

        IsPulling = true;
        try
        {
            _session ??= CreateSession();

            Status = $"Pulling from {target.DisplayName}…";
            using var cts = new CancellationTokenSource();
            bool accepted = await _session.PullFromAsync(target.Endpoint, cts.Token).ConfigureAwait(true);

            if (!accepted)
                Status = "Peer rejected: untrusted sender or no transfer on offer.";
            else if (_session.IsComplete)
                Status = "Transfer complete.";
            else
                Status = "Partial transfer — pull again (same or another peer) to finish.";
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
            IsPulling = false;
        }
    }

    private SwarmPullSession CreateSession()
    {
        var session = _sessionFactory(this);
        session.ProgressChanged += OnProgress;
        session.SenderTrustDenied += OnTrustDenied;
        return session;
    }

    /// <summary>
    /// <see cref="ITrustPrompt"/> implementation: surfaces the in-app TOFU prompt and blocks the pull until the
    /// user decides. Contractually never throws (a thrown prompt would crash the pull) — any failure is a
    /// graceful denial, mirroring the desktop <c>DialogTrustPrompt</c>.
    /// </summary>
    public async Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            var prompt = new TrustPromptViewModel(context);
            await using var registration = cancellationToken.Register(prompt.Cancel);

            Dispatcher.UIThread.Post(() => PendingTrustPrompt = prompt);
            bool accepted = await prompt.Result.ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => PendingTrustPrompt = null);
            return accepted;
        }
        catch
        {
            Dispatcher.UIThread.Post(() => PendingTrustPrompt = null);
            return false;
        }
    }

    private void OnProgress(TransferProgress progress) =>
        Dispatcher.UIThread.Post(() => Progress.Update(progress));

    private void OnTrustDenied(TrustDecision decision, PublicKeyId senderId) =>
        Dispatcher.UIThread.Post(() =>
            Status = $"Trust denied for sender {senderId.Value} ({decision.Outcome}).");

    public void Dispose()
    {
        _browseCts?.Cancel();
        _browseCts?.Dispose();
        _session?.Dispose();
    }
}

/// <summary>A discovered peer as presented in the mobile peer list.</summary>
public sealed class DiscoveredPeerItem(DiscoveredPeer peer)
{
    public string ServiceName { get; } = peer.ServiceName;

    public Endpoint Endpoint { get; } = peer.Endpoint;

    /// <summary>Human-readable label: the advertised instance name plus its resolved address.</summary>
    public string DisplayName { get; } =
        string.IsNullOrWhiteSpace(peer.ServiceName) ? peer.Endpoint.ToString() : peer.ServiceName;

    public string EndpointText { get; } = $"{peer.Endpoint.Host}:{peer.Endpoint.Port}";
}
