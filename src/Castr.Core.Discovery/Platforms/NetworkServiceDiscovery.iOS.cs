using System.Threading.Channels;
using CoreFoundation;
using Castr.Core.Transport;
using Network;

namespace Castr.Core.Discovery;

// =====================================================================================================
// iOS / macOS implementation of IServiceDiscovery using Apple's Network.framework
// (NWBrowser / NWListener) via the dotnet/macios bindings — no P/Invoke, per ADR-0002. Compiled only
// into the net10.0-ios target.
//
// Advertise -> NWListener with a Bonjour advertise descriptor for "_castr._tcp".
// Browse     -> NWBrowser over the same Bonjour service type; each discovered service is resolved to a
//               concrete host+port by opening a short-lived NWConnection and reading the effective
//               remote endpoint from its established path (a Bonjour browse result names a service but
//               does not itself carry the resolved IP/port — Network.framework resolves it lazily when
//               you connect).
//
// -----------------------------------------------------------------------------------------------------
// Info.plist DEPENDENCY (cannot be satisfied from this library — flagged for whoever builds
// Castr.Gui.iOS). iOS 14+ requires BOTH of these in the APP's Info.plist or local-network discovery
// silently finds nothing on a real signed/device build (the simulator and debug builds are more
// permissive and can mask a missing entry — do NOT trust simulator-only testing here, per ADR-0002):
//
//     <key>NSLocalNetworkUsageDescription</key>
//     <string>Castr discovers nearby devices on your local network to share files.</string>
//     <key>NSBonjourServices</key>
//     <array>
//         <string>_castr._tcp</string>   <!-- MUST match IServiceDiscovery.ServiceType verbatim -->
//     </array>
//
// -----------------------------------------------------------------------------------------------------
// VERIFICATION HONESTY: written against the documented dotnet/macios Network.framework binding. On this
// Windows dev machine there is NO Xcode and NO macOS host, so this is verified only to the extent the
// net10.0-ios reference assemblies allow it to COMPILE (see the build report). It has NEVER been run,
// linked into an app, or exercised on a simulator or device. Runtime behavior — advertise visibility,
// browse/resolve correctness, and Android<->iOS interop — is entirely UNVERIFIED and is the M4
// hands-on follow-up from ADR-0002.
// =====================================================================================================

/// <summary>
/// iOS/macOS <see cref="NWBrowser"/>/<see cref="NWListener"/>-backed <see cref="IServiceDiscovery"/>.
/// Compiled only into <c>net10.0-ios</c>. See the file header for the required <c>Info.plist</c> keys
/// (a dependency on the app project, not satisfiable here) and verification caveats.
/// </summary>
public sealed class NetworkServiceDiscovery : IServiceDiscovery
{
    private readonly DispatchQueue _queue = new("com.castr.discovery");
    private NWListener? _listener;
    private NWBrowser? _browser;
    private readonly List<NWConnection> _resolveConnections = [];
    private bool _disposed;

    public Task AdvertiseAsync(string serviceName, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        using var parameters = NWParameters.CreateTcp();
        var listener = NWListener.Create(port.ToString(), parameters)
            ?? throw new InvalidOperationException("Failed to create NWListener for Castr advertisement.");

        using var advertise = NWAdvertiseDescriptor.CreateBonjourService(serviceName, IServiceDiscovery.ServiceType, null)
            ?? throw new InvalidOperationException("Failed to create Bonjour advertise descriptor for Castr.");
        listener.SetAdvertiseDescriptor(advertise);

        // A listener must accept (or explicitly cancel) inbound connections. Swarm-pull owns the real
        // TCP acceptance; here we only need presence on the network, so cancel any inbound connection.
        listener.SetNewConnectionHandler(connection => connection.Cancel());

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SetStateChangedHandler((state, error) =>
        {
            switch (state)
            {
                case NWListenerState.Ready:
                    tcs.TrySetResult();
                    break;
                case NWListenerState.Failed:
                    tcs.TrySetException(new InvalidOperationException($"NWListener failed: {error}."));
                    break;
            }
        });

        listener.SetQueue(_queue);
        _listener = listener;
        listener.Start();

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    public async IAsyncEnumerable<DiscoveredPeer> BrowseAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = Channel.CreateUnbounded<DiscoveredPeer>();

        using var descriptor = NWBrowserDescriptor.CreateBonjourService(IServiceDiscovery.ServiceType, null);
        using var parameters = NWParameters.CreateTcp();
        var browser = new NWBrowser(descriptor, parameters);
        browser.SetDispatchQueue(_queue);

        browser.IndividualChangesDelegate = (oldResult, newResult) =>
        {
            // We care about newly-added services. A browse result is name-only; resolve to host+port.
            if (newResult is not null)
                ResolveAndEmit(newResult, channel.Writer);
        };

        browser.SetStateChangesHandler((state, error) =>
        {
            if (state == NWBrowserState.Failed)
                channel.Writer.TryComplete(new InvalidOperationException($"NWBrowser failed: {error}."));
        });

        _browser = browser;
        browser.Start();

        try
        {
            await foreach (var peer in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return peer;
        }
        finally
        {
            try { browser.Cancel(); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>
    /// Resolves a Bonjour browse result to a concrete <see cref="Endpoint"/> by opening a short-lived
    /// <see cref="NWConnection"/> and reading the effective remote endpoint once the path is established.
    /// </summary>
    private void ResolveAndEmit(NWBrowseResult result, ChannelWriter<DiscoveredPeer> writer)
    {
        var serviceEndpoint = result.EndPoint;
        if (serviceEndpoint is null)
            return;

        var serviceName = serviceEndpoint.BonjourServiceName ?? serviceEndpoint.Hostname ?? "castr-peer";

        var parameters = NWParameters.CreateTcp();
        var connection = new NWConnection(serviceEndpoint, parameters);
        lock (_resolveConnections) { _resolveConnections.Add(connection); }

        connection.SetStateChangeHandler((state, error) =>
        {
            if (state == NWConnectionState.Ready)
            {
                using var path = connection.CurrentPath;
                var remote = path?.EffectiveRemoteEndpoint;
                var host = remote?.Hostname;
                if (!string.IsNullOrEmpty(host))
                {
                    int resolvedPort = remote!.PortNumber; // NWEndpoint.Port is a string; PortNumber is the ushort
                    writer.TryWrite(new DiscoveredPeer(serviceName, new Endpoint(host, resolvedPort)));
                }
                CleanupConnection(connection);
            }
            else if (state is NWConnectionState.Failed or NWConnectionState.Cancelled)
            {
                CleanupConnection(connection);
            }
        });

        connection.SetQueue(_queue);
        connection.Start();
    }

    private void CleanupConnection(NWConnection connection)
    {
        lock (_resolveConnections) { _resolveConnections.Remove(connection); }
        try { connection.Cancel(); } catch (Exception) { /* best effort */ }
        connection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        try { _listener?.Cancel(); } catch (Exception) { /* best effort */ }
        try { _browser?.Cancel(); } catch (Exception) { /* best effort */ }

        NWConnection[] connections;
        lock (_resolveConnections)
        {
            connections = [.. _resolveConnections];
            _resolveConnections.Clear();
        }
        foreach (var connection in connections)
        {
            try { connection.Cancel(); } catch (Exception) { /* best effort */ }
            connection.Dispose();
        }

        _listener?.Dispose();
        _browser?.Dispose();
        _queue.Dispose();
        return ValueTask.CompletedTask;
    }
}
