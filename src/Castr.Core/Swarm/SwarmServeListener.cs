using Castr.Core.Protocol;
using Castr.Core.Transport;

namespace Castr.Core.Swarm;

/// <summary>
/// Serves the unicast-swarm (mobile TCP pull) protocol: accepts inbound <see cref="IStreamListener"/>
/// connections and answers each peer's framed requests from an <see cref="ISwarmContentSource"/> — its
/// manifest, verified ciphertext chunks, and (sender only) a content-key grant. Additive and read-only: it
/// never mutates the underlying session, so wrapping a live <c>SenderSession</c> or <c>ReceiverSession</c>
/// for peer-serving leaves that session's multicast behavior untouched.
/// </summary>
/// <remarks>
/// <para>Every request/response rides <see cref="LengthPrefixedFramer"/>, which bounds the accepted frame size —
/// a peer cannot force an unbounded allocation with a crafted length prefix. A malformed frame, an
/// over-limit prefix, or a decode failure simply drops that one connection; other peers are unaffected.</para>
/// <para><b>Two bounds on the accept loop itself.</b> Finished connection tasks are pruned rather than retained
/// for the life of the listener, and at most <see cref="DefaultMaxConcurrentConnections"/> are served at once.
/// Without the first, a long-lived listener kept one <see cref="Task"/> per connection ever accepted — a leak
/// proportional to uptime, not to load. Without the second there was no ceiling on concurrent handlers at all,
/// and each one holds a frame buffer and can be mid-chunk-serve. The cap is applied <i>before</i>
/// <see cref="IStreamListener.AcceptAsync"/>, so excess dials wait in the transport's own backlog instead of
/// being accepted and then starved — real backpressure rather than a queue this class would have to bound.</para>
/// </remarks>
public sealed class SwarmServeListener(
    IStreamListener listener,
    ISwarmContentSource source,
    int maxFrameLength = LengthPrefixedFramer.DefaultMaxFrameLength,
    int maxConcurrentConnections = SwarmServeListener.DefaultMaxConcurrentConnections)
{
    /// <summary>
    /// Default ceiling on connections served at once. Sized for what a swarm participant is: peers pull whole
    /// chunks over a point-to-point stream, so a handler's cost is one frame buffer plus at most one chunk read
    /// in flight. 64 is far above the handful of peers a real LAN swarm produces and far below anything that
    /// would matter for memory.
    /// </summary>
    public const int DefaultMaxConcurrentConnections = 64;

    private int _tracked;

    /// <summary>
    /// Connection tasks currently retained by the accept loop. Diagnostics and tests only — this is what the
    /// pruning bound is expressed in, so it is worth being able to assert on directly.
    /// </summary>
    public int TrackedConnectionCount => Volatile.Read(ref _tracked);

    /// <summary>Accepts connections until cancelled, handling each concurrently. Never throws for a single peer's misbehavior.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (maxConcurrentConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentConnections));

        using var slots = new SemaphoreSlim(maxConcurrentConnections, maxConcurrentConnections);
        var connections = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                IStreamConnection connection;
                try
                {
                    connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    slots.Release();
                    break;
                }

                connections.Add(ServeAsync(connection, slots, cancellationToken));
                Prune(connections);
            }
        }
        finally
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
            Volatile.Write(ref _tracked, 0);
        }
    }

    /// <summary>
    /// Drops finished connection tasks. Only <b>successfully</b> completed ones: a faulted task is kept so the
    /// <c>Task.WhenAll</c> in <see cref="RunAsync"/>'s finally still surfaces it, which is the behaviour this
    /// class had before pruning existed. <see cref="HandleConnectionAsync"/> swallows everything a peer can
    /// cause, so a fault here means a defect and losing it silently would be worse than retaining a task.
    /// </summary>
    private void Prune(List<Task> connections)
    {
        connections.RemoveAll(t => t.IsCompletedSuccessfully);
        Volatile.Write(ref _tracked, connections.Count);
    }

    private async Task ServeAsync(IStreamConnection connection, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await HandleConnectionAsync(connection, ct).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task HandleConnectionAsync(IStreamConnection connection, CancellationToken ct)
    {
        await using (connection)
        {
            var framer = new LengthPrefixedFramer(connection, maxFrameLength);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte[]? frame = await framer.ReadFrameAsync(ct).ConfigureAwait(false);
                    if (frame is null)
                        return; // peer closed cleanly

                    object message;
                    try { message = MessageCodec.Decode(frame); }
                    catch { return; } // malformed message — drop this connection, don't crash the listener

                    if (!await DispatchAsync(framer, message, ct).ConfigureAwait(false))
                        return;
                }
            }
            catch (InvalidDataException)
            {
                // Over-limit length prefix or truncated frame from a hostile/broken peer — drop the connection.
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
        }
    }

    /// <summary>Handles one request. Returns false to close the connection.</summary>
    private async Task<bool> DispatchAsync(LengthPrefixedFramer framer, object message, CancellationToken ct)
    {
        switch (message)
        {
            case ManifestRequestMessage:
                if (source.Manifest is { } manifest)
                    await framer.WriteFrameAsync(MessageCodec.Encode(new ManifestMessage(manifest)), ct).ConfigureAwait(false);
                return true;

            case JoinRequestMessage join:
                await HandleJoinAsync(framer, join, ct).ConfigureAwait(false);
                return true;

            case ChunkPullRequestMessage pull:
                await HandleChunkPullAsync(framer, pull, ct).ConfigureAwait(false);
                return true;

            default:
                return true; // ignore anything unexpected but keep the connection open
        }
    }

    private async Task HandleJoinAsync(LengthPrefixedFramer framer, JoinRequestMessage join, CancellationToken ct)
    {
        var manifest = source.Manifest;
        if (manifest is null || !join.SessionId.AsSpan().SequenceEqual(manifest.Manifest.SessionId))
            return;

        var grant = source.TryGrantContentKey(join);
        object reply = grant is not null
            ? grant
            : new KeyUnavailableMessage(manifest.Manifest.SessionId, join.ReceiverId);
        await framer.WriteFrameAsync(MessageCodec.Encode(reply), ct).ConfigureAwait(false);
    }

    private async Task HandleChunkPullAsync(LengthPrefixedFramer framer, ChunkPullRequestMessage pull, CancellationToken ct)
    {
        var manifest = source.Manifest;
        if (manifest is null || !pull.SessionId.AsSpan().SequenceEqual(manifest.Manifest.SessionId))
        {
            // Unknown session: still answer one not-found per requested index so the client's fixed-count read
            // completes rather than hangs.
            foreach (var chunkIndex in pull.ChunkIndices)
                await framer.WriteFrameAsync(MessageCodec.Encode(NotFound(pull, chunkIndex)), ct).ConfigureAwait(false);
            return;
        }

        foreach (var chunkIndex in pull.ChunkIndices)
        {
            var chunk = await source.TryGetChunkAsync(pull.FileIndex, chunkIndex, ct).ConfigureAwait(false);
            object response = chunk is not null
                ? new ChunkPullResponseMessage(pull.SessionId, pull.FileIndex, chunkIndex, true, chunk.Ciphertext, chunk.Proof)
                : NotFound(pull, chunkIndex);
            await framer.WriteFrameAsync(MessageCodec.Encode(response), ct).ConfigureAwait(false);
        }
    }

    private static ChunkPullResponseMessage NotFound(ChunkPullRequestMessage pull, int chunkIndex) =>
        new(pull.SessionId, pull.FileIndex, chunkIndex, false, [], null);
}
