using Castr.Core.Transport;

namespace Castr.Core.Discovery;

/// <summary>
/// A Castr peer found on the local network by an <see cref="IServiceDiscovery"/> browse.
/// </summary>
/// <param name="ServiceName">
/// The advertised instance name (the mDNS/Bonjour service instance label, e.g. the device or user
/// name). Unique per advertiser within the <c>_castr._tcp</c> service type on a given LAN.
/// </param>
/// <param name="Endpoint">
/// The resolved network endpoint (host + port) the peer is reachable at. Reuses
/// <see cref="Castr.Core.Transport.Endpoint"/> so downstream swarm-pull code consumes the same
/// address type the rest of Castr.Core already uses — this is the composition boundary: discovery
/// produces these endpoints, the swarm client consumes them.
/// </param>
public sealed record DiscoveredPeer(string ServiceName, Endpoint Endpoint);
