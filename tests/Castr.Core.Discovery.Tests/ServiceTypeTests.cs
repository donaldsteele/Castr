using Castr.Core.Discovery;

namespace Castr.Core.Discovery.Tests;

/// <summary>
/// Guards the one string that MUST be byte-for-byte identical between advertise and browse and across
/// Android (NsdServiceInfo.ServiceType), iOS (NWBrowser/NWListener + Info.plist NSBonjourServices), and
/// the fake — cross-platform discovery finds nothing if it ever drifts. See ADR-0002.
/// </summary>
public class ServiceTypeTests
{
    [Fact]
    public void ServiceType_IsExactlyCastrTcp()
    {
        Assert.Equal("_castr._tcp", IServiceDiscovery.ServiceType);
    }

    [Fact]
    public void ServiceType_FollowsDnsSdTypeShape()
    {
        // DNS-SD service type: _<name>._<proto>, proto is tcp or udp. Castr uses TCP for swarm-pull.
        Assert.Matches(@"^_[a-z0-9-]+\._tcp$", IServiceDiscovery.ServiceType);
    }
}
