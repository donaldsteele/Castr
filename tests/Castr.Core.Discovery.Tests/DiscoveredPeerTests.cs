using Castr.Core.Discovery;
using Castr.Core.Transport;

namespace Castr.Core.Discovery.Tests;

public class DiscoveredPeerTests
{
    [Fact]
    public void CarriesServiceNameAndEndpoint()
    {
        var peer = new DiscoveredPeer("Don's Laptop", new Endpoint("192.168.1.20", 51820));

        Assert.Equal("Don's Laptop", peer.ServiceName);
        Assert.Equal("192.168.1.20", peer.Endpoint.Host);
        Assert.Equal(51820, peer.Endpoint.Port);
    }

    [Fact]
    public void ReusesCoreTransportEndpointType()
    {
        // The composition boundary: discovery must hand back the SAME Endpoint type the swarm-pull side
        // consumes, not a parallel address type. This is a compile-and-assign proof of that.
        Endpoint endpoint = new DiscoveredPeer("peer", new Endpoint("10.0.0.5", 7000)).Endpoint;
        Assert.Equal("10.0.0.5:7000", endpoint.ToString());
    }

    [Fact]
    public void RecordEquality_HoldsByValue()
    {
        var a = new DiscoveredPeer("peer", new Endpoint("10.0.0.5", 7000));
        var b = new DiscoveredPeer("peer", new Endpoint("10.0.0.5", 7000));
        var c = new DiscoveredPeer("peer", new Endpoint("10.0.0.6", 7000));

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
