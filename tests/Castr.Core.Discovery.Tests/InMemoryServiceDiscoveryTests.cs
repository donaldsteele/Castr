using Castr.Core.Discovery;
using Castr.Core.Discovery.InMemory;
using Castr.Core.Transport;

namespace Castr.Core.Discovery.Tests;

/// <summary>
/// Exercises the platform-neutral fake against the <see cref="IServiceDiscovery"/> contract:
/// advertise -> browse discovery, snapshot vs live delivery, self-exclusion, cancellation, disposal.
/// No real network, platform API, or timing — deterministic like Core's InMemory transport tests.
/// </summary>
public class InMemoryServiceDiscoveryTests
{
    private static async Task<List<DiscoveredPeer>> TakeAsync(
        IAsyncEnumerable<DiscoveredPeer> stream, int count, TimeSpan timeout)
    {
        var results = new List<DiscoveredPeer>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var peer in stream.WithCancellation(cts.Token))
            {
                results.Add(peer);
                if (results.Count >= count)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for `count` peers — return what we got so the assertion reports the shortfall.
        }
        return results;
    }

    [Fact]
    public async Task Browse_DiscoversAPeerAdvertisedBeforeSubscribe_WithCorrectEndpoint()
    {
        var network = new InMemoryDiscoveryNetwork();
        await using var advertiser = new InMemoryServiceDiscovery(network, host: "192.168.0.10");
        await using var browser = new InMemoryServiceDiscovery(network, host: "192.168.0.11");

        await advertiser.AdvertiseAsync("advertiser", 51820, CancellationToken.None);

        var found = await TakeAsync(browser.BrowseAsync(), 1, TimeSpan.FromSeconds(5));

        var peer = Assert.Single(found);
        Assert.Equal("advertiser", peer.ServiceName);
        Assert.Equal(new Endpoint("192.168.0.10", 51820), peer.Endpoint);
    }

    [Fact]
    public async Task Browse_DeliversAPeerAdvertisedAfterSubscribe_Live()
    {
        var network = new InMemoryDiscoveryNetwork();
        await using var advertiser = new InMemoryServiceDiscovery(network, host: "10.1.1.1");
        await using var browser = new InMemoryServiceDiscovery(network, host: "10.1.1.2");

        var browseTask = TakeAsync(browser.BrowseAsync(), 1, TimeSpan.FromSeconds(5));
        await Task.Delay(50); // let the browse subscribe first, so this advertisement is a live delivery
        await advertiser.AdvertiseAsync("late-joiner", 7000, CancellationToken.None);

        var peer = Assert.Single(await browseTask);
        Assert.Equal("late-joiner", peer.ServiceName);
        Assert.Equal(new Endpoint("10.1.1.1", 7000), peer.Endpoint);
    }

    [Fact]
    public async Task TwoNodes_DiscoverEachOther()
    {
        var network = new InMemoryDiscoveryNetwork();
        await using var alice = new InMemoryServiceDiscovery(network, host: "10.0.0.1");
        await using var bob = new InMemoryServiceDiscovery(network, host: "10.0.0.2");

        await alice.AdvertiseAsync("alice", 5000, CancellationToken.None);
        await bob.AdvertiseAsync("bob", 5001, CancellationToken.None);

        var aliceSees = await TakeAsync(alice.BrowseAsync(), 1, TimeSpan.FromSeconds(5));
        var bobSees = await TakeAsync(bob.BrowseAsync(), 1, TimeSpan.FromSeconds(5));

        Assert.Equal("bob", Assert.Single(aliceSees).ServiceName);
        Assert.Equal("alice", Assert.Single(bobSees).ServiceName);
    }

    [Fact]
    public async Task Browse_ExcludesOwnAdvertisement_ByDefault()
    {
        var network = new InMemoryDiscoveryNetwork();
        await using var self = new InMemoryServiceDiscovery(network, host: "10.0.0.9");   // excludeSelf defaults true
        await using var other = new InMemoryServiceDiscovery(network, host: "10.0.0.10");

        await self.AdvertiseAsync("self", 6000, CancellationToken.None);
        await other.AdvertiseAsync("other", 6001, CancellationToken.None);

        var found = await TakeAsync(self.BrowseAsync(), 1, TimeSpan.FromSeconds(5));

        // Only "other" should surface; "self" is filtered out.
        Assert.Equal("other", Assert.Single(found).ServiceName);
    }

    [Fact]
    public async Task Browse_IncludesOwnAdvertisement_WhenSelfExclusionDisabled()
    {
        var network = new InMemoryDiscoveryNetwork();
        await using var node = new InMemoryServiceDiscovery(network, host: "10.0.0.9", excludeSelf: false);

        await node.AdvertiseAsync("self", 6000, CancellationToken.None);

        var found = await TakeAsync(node.BrowseAsync(), 1, TimeSpan.FromSeconds(5));

        Assert.Equal("self", Assert.Single(found).ServiceName);
    }

    [Fact]
    public async Task Browse_StopsWhenCancelled()
    {
        var network = new InMemoryDiscoveryNetwork();
        await using var browser = new InMemoryServiceDiscovery(network);

        using var cts = new CancellationTokenSource();
        var enumerator = browser.BrowseAsync(cts.Token).GetAsyncEnumerator();
        try
        {
            var moveNext = enumerator.MoveNextAsync();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNext);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_WithdrawsAdvertisement_SoLaterBrowsersDoNotSeeIt()
    {
        var network = new InMemoryDiscoveryNetwork();
        var advertiser = new InMemoryServiceDiscovery(network, host: "10.2.2.1");
        await advertiser.AdvertiseAsync("ephemeral", 8000, CancellationToken.None);
        await advertiser.DisposeAsync();

        await using var browser = new InMemoryServiceDiscovery(network, host: "10.2.2.2");
        var found = await TakeAsync(browser.BrowseAsync(), 1, TimeSpan.FromMilliseconds(300));

        Assert.Empty(found);
    }

    [Fact]
    public async Task AdvertiseAsync_AfterDispose_Throws()
    {
        var network = new InMemoryDiscoveryNetwork();
        var discovery = new InMemoryServiceDiscovery(network);
        await discovery.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => discovery.AdvertiseAsync("x", 1, CancellationToken.None));
    }

    [Fact]
    public async Task AdvertiseAsync_HonorsAlreadyCancelledToken()
    {
        var network = new InMemoryDiscoveryNetwork();
        await using var discovery = new InMemoryServiceDiscovery(network);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery.AdvertiseAsync("x", 1, cts.Token));
    }
}
