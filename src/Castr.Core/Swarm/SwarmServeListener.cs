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
/// Every request/response rides <see cref="LengthPrefixedFramer"/>, which bounds the accepted frame size —
/// a peer cannot force an unbounded allocation with a crafted length prefix. A malformed frame, an
/// over-limit prefix, or a decode failure simply drops that one connection; other peers are unaffected.
/// </remarks>
public sealed class SwarmServeListener(
    IStreamListener listener,
    ISwarmContentSource source,
    int maxFrameLength = LengthPrefixedFramer.DefaultMaxFrameLength)
{
    /// <summary>Accepts connections until cancelled, handling each concurrently. Never throws for a single peer's misbehavior.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IStreamConnection connection;
                try
                {
                    connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                connections.Add(HandleConnectionAsync(connection, cancellationToken));
            }
        }
        finally
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
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
