using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;

namespace Castr.Gui.Services;

/// <summary>
/// Wires Send and Receive onto a single in-process <see cref="InMemoryNetwork"/> (Core's simulated LAN).
/// Used for design-time/default composition and by headless tests, so the exact same view-model + session
/// code path runs end-to-end without touching real sockets.
/// </summary>
public sealed class InMemoryTransportFactory(InMemoryNetwork? network = null) : ITransportFactory
{
    private readonly InMemoryNetwork _network = network ?? new InMemoryNetwork();
    private int _counter;

    public IMulticastTransport CreateSenderTransport() =>
        _network.CreateMulticastTransport(new Endpoint($"sender-{Interlocked.Increment(ref _counter)}", 1));

    public IMulticastTransport CreateReceiverTransport() =>
        _network.CreateMulticastTransport(new Endpoint($"receiver-{Interlocked.Increment(ref _counter)}", 1));
}
