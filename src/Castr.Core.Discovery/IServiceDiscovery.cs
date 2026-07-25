namespace Castr.Core.Discovery;

/// <summary>
/// Platform-neutral local-network peer discovery for Castr, backed per-platform by the OS-mediated
/// mDNS/Bonjour service-discovery API (Android <c>NsdManager</c>, iOS/macOS <c>NWBrowser</c>/
/// <c>NWListener</c>) and by an in-memory fake for tests. See ADR-0002.
/// </summary>
/// <remarks>
/// The service type is <see cref="ServiceType"/> (<c>_castr._tcp</c>) and MUST match verbatim
/// between advertise and browse, and across platforms, or cross-platform discovery finds nothing.
/// </remarks>
public interface IServiceDiscovery : IAsyncDisposable
{
    /// <summary>
    /// The Castr mDNS/Bonjour/DNS-SD service type. Fixed by ADR-0002; use verbatim everywhere,
    /// including the iOS <c>Info.plist</c> <c>NSBonjourServices</c> array and the Android
    /// <c>NsdServiceInfo.ServiceType</c>.
    /// </summary>
    public const string ServiceType = "_castr._tcp";

    /// <summary>
    /// Announce this device as a Castr peer on the local network under <see cref="ServiceType"/>.
    /// </summary>
    /// <param name="serviceName">
    /// The instance name to advertise (becomes <see cref="DiscoveredPeer.ServiceName"/> on browsers).
    /// The OS may rename it on collision (e.g. append " (2)").
    /// </param>
    /// <param name="port">The TCP port remote peers should connect to for swarm-pull.</param>
    /// <param name="cancellationToken">Cancels the advertise setup.</param>
    Task AdvertiseAsync(string serviceName, int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Browse for other Castr peers, yielding each as it is discovered and resolved. The stream runs
    /// until <paramref name="cancellationToken"/> is cancelled or the instance is disposed.
    /// </summary>
    IAsyncEnumerable<DiscoveredPeer> BrowseAsync(CancellationToken cancellationToken = default);
}
