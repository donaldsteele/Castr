using System.Runtime.CompilerServices;
using Castr.Core.Transport;

namespace Castr.Core.Discovery.InMemory;

/// <summary>
/// Platform-neutral fake <see cref="IServiceDiscovery"/> over an <see cref="InMemoryDiscoveryNetwork"/>,
/// analogous to <c>InMemoryMulticastTransport</c>. Advertises this node's endpoint on the shared bus
/// and browses everything advertised there. Deterministic and network-free — for tests only.
/// </summary>
public sealed class InMemoryServiceDiscovery : IServiceDiscovery
{
    private readonly InMemoryDiscoveryNetwork _network;
    private readonly string _host;
    private readonly bool _excludeSelf;
    private readonly HashSet<Endpoint> _selfEndpoints = [];
    private bool _disposed;

    /// <param name="network">The shared in-memory LAN this node participates in.</param>
    /// <param name="host">
    /// The host this node advertises itself at (e.g. <c>"127.0.0.1"</c>); combined with the advertised
    /// port into the <see cref="Endpoint"/> browsers see.
    /// </param>
    /// <param name="excludeSelf">
    /// When true, <see cref="BrowseAsync"/> filters out this node's own advertisements — matching the
    /// common real-world case where a device does not want to discover itself.
    /// </param>
    public InMemoryServiceDiscovery(InMemoryDiscoveryNetwork network, string host = "127.0.0.1", bool excludeSelf = true)
    {
        _network = network;
        _host = host;
        _excludeSelf = excludeSelf;
    }

    public Task AdvertiseAsync(string serviceName, int port, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = new Endpoint(_host, port);
        lock (_selfEndpoints) { _selfEndpoints.Add(endpoint); }
        _network.Advertise(new DiscoveredPeer(serviceName, endpoint));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<DiscoveredPeer> BrowseAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var reader = _network.Subscribe(out var unsubscribe);
        try
        {
            await foreach (var peer in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_excludeSelf)
                {
                    lock (_selfEndpoints)
                    {
                        if (_selfEndpoints.Contains(peer.Endpoint))
                            continue;
                    }
                }

                yield return peer;
            }
        }
        finally
        {
            unsubscribe();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;

        lock (_selfEndpoints)
        {
            foreach (var endpoint in _selfEndpoints)
                _network.Withdraw(endpoint);
            _selfEndpoints.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
