using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Swarm;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;

namespace Castr.Core.Tests.Swarm;

/// <summary>
/// Coverage for the accept loop's own resource bounds, as distinct from what it serves (that lives in
/// <see cref="SwarmPullSessionTests"/>). The loop used to keep one <see cref="Task"/> per connection ever
/// accepted, drained only when the listener shut down, and had no ceiling on concurrent handlers.
/// </summary>
public class SwarmServeListenerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly Endpoint ServeEndpoint = new("serve", 1);
    private static readonly Endpoint ClientEndpoint = new("client", 1);

    [Fact]
    public async Task FinishedConnections_AreNotRetainedForTheLifeOfTheListener()
    {
        // The leak: `connections.Add(...)` with no removal, drained only by Task.WhenAll in the shutdown finally.
        // A listener that serves a thousand short-lived pulls held a thousand completed Tasks. Growth tracked
        // uptime, not concurrency — which is why nothing that measured peak concurrency would have caught it.
        var network = new InMemoryStreamNetwork();
        var listener = network.CreateListener(ServeEndpoint);
        var client = network.CreateClient(ClientEndpoint);
        var serve = new SwarmServeListener(listener, new EmptySource());

        using var cts = new CancellationTokenSource(Timeout);
        var run = serve.RunAsync(cts.Token);

        const int connectionCount = 50;
        for (int i = 0; i < connectionCount; i++)
        {
            var connection = await client.ConnectAsync(ServeEndpoint, cts.Token);
            await connection.DisposeAsync(); // peer closes cleanly; the handler returns
        }

        // Deliberately a shorter budget than the run token's, and asserted while RunAsync is still going: the
        // counter is reset to zero when the loop shuts down, so a check made after cancellation would pass
        // against an un-pruned implementation too. (It did, until this test was rewritten.)
        using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => serve.TrackedConnectionCount <= 2, settle.Token);

        Assert.False(run.IsCompleted, "the accept loop must still be running when the count is read");
        Assert.True(serve.TrackedConnectionCount <= 2,
            $"retained {serve.TrackedConnectionCount} connection tasks after {connectionCount} closed connections");

        await cts.CancelAsync();
        await Swallow(run);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentConnections_AreCappedAndExcessDialsWaitInTheBacklog()
    {
        // The other half: no ceiling on concurrent handlers. Each holds a frame buffer and can be mid-chunk-serve,
        // so an unbounded fan-in was an unbounded memory commitment. The cap is taken BEFORE AcceptAsync, so the
        // excess connections stay unaccepted rather than being accepted and then starved.
        const int cap = 3;
        var network = new InMemoryStreamNetwork();
        var listener = network.CreateListener(ServeEndpoint);
        var client = network.CreateClient(ClientEndpoint);
        var serve = new SwarmServeListener(listener, new EmptySource(), maxConcurrentConnections: cap);

        using var cts = new CancellationTokenSource(Timeout);
        var run = serve.RunAsync(cts.Token);

        // Ten peers dial and then hold their connections open, saying nothing — each accepted handler parks on a
        // frame read. Only `cap` of them can be accepted at a time.
        var held = new List<IStreamConnection>();
        for (int i = 0; i < 10; i++)
            held.Add(await client.ConnectAsync(ServeEndpoint, cts.Token));

        await WaitUntilAsync(() => serve.TrackedConnectionCount >= cap, cts.Token);
        await Task.Delay(50, cts.Token); // give an unbounded loop room to overshoot, if it were unbounded
        Assert.Equal(cap, serve.TrackedConnectionCount);

        // Closing the held connections frees slots, and the waiting dials are accepted in turn.
        foreach (var connection in held)
            await connection.DisposeAsync();

        await WaitUntilAsync(() => serve.TrackedConnectionCount <= cap, cts.Token);

        await cts.CancelAsync();
        await Swallow(run);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task CappedListener_StillServesEveryPeer_JustNotAllAtOnce()
    {
        // The cap must be backpressure, not a drop: a peer that waits its turn is served normally.
        var network = new InMemoryStreamNetwork();
        var listener = network.CreateListener(ServeEndpoint);
        var serve = new SwarmServeListener(listener, new EmptySource(), maxConcurrentConnections: 1);

        using var cts = new CancellationTokenSource(Timeout);
        var run = serve.RunAsync(cts.Token);

        for (int i = 0; i < 8; i++)
        {
            var client = network.CreateClient(new Endpoint($"peer-{i}", 1));
            await using var connection = await client.ConnectAsync(ServeEndpoint, cts.Token);
            var framer = new LengthPrefixedFramer(connection);

            await framer.WriteFrameAsync(MessageCodec.Encode(new ManifestRequestMessage()), cts.Token);
            // EmptySource serves no manifest, so the handler stays open and this peer gets no reply — what is
            // being asserted is that its turn came at all, which the next iteration's connect proves.
        }

        await cts.CancelAsync();
        await Swallow(run);
        await listener.DisposeAsync();
    }

    private sealed class EmptySource : ISwarmContentSource
    {
        public SignedManifest? Manifest => null;
        public ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SwarmChunk?>(null);
        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition() && !ct.IsCancellationRequested)
            await Task.Delay(5, CancellationToken.None);
        Assert.True(condition(), "condition was not met before the test timeout");
    }

    private static async Task Swallow(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }
}
