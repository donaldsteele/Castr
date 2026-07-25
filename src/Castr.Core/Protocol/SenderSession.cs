using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Security;
using Castr.Core.Swarm;
using Castr.Core.Transport;

namespace Castr.Core.Protocol;

/// <summary>
/// Drives the sender side of a transfer over a single <see cref="IMulticastTransport"/>: announces,
/// broadcasts the signed manifest, carousels every chunk once (each chunk encrypted with the per-transfer
/// content key), grants that content key to each trusting receiver via the JOIN_REQUEST/KEY_GRANT handshake,
/// and answers CHUNK_REQUEST with CHUNK_RESPONSE for any chunk it holds. Chunk data and repair traffic travel
/// over the same multicast channel (see wiki/concepts/repair-protocol.md) so any listener — not just the
/// original requester — benefits from a fulfilled repair.
/// </summary>
/// <remarks>
/// Known M1 scope trim: this sends the chunk carousel exactly once rather than repeating rounds
/// (FLUTE-style self-healing); fault tolerance instead relies on peer/sender repair via CHUNK_REQUEST,
/// which is fully implemented. Multi-round carousel repetition is a natural, low-risk future addition.
/// Per wiki/synthesis/adr-0003-payload-encryption.md the JOIN_REQUEST/KEY_GRANT handshake is logically
/// per-receiver unicast; like CHUNK_REQUEST/RESPONSE it currently rides the shared multicast channel
/// (each KEY_GRANT is cryptographically readable only by its addressed receiver, so multicast delivery
/// leaks nothing) — the same documented M1 trim, not a security relaxation.
/// </remarks>
public sealed class SenderSession(
    SignedManifest signedManifest,
    IReadOnlyDictionary<int, IFileSource> fileSources,
    IReadOnlyDictionary<int, MerkleTree> merkleTrees,
    IMulticastTransport transport,
    Key senderEncryptionKey,
    ContentKey contentKey,
    int maxDatagramPayloadBytes = WirePacketizer.DefaultMaxDatagramPayload,
    int sendWindowSize = SenderSession.DefaultSendWindowSize)
{
    /// <summary>
    /// Default bound on concurrent in-flight <c>transport.SendAsync</c> calls for the chunk carousel and
    /// chunk-repair batches (see <see cref="RunChunkCarouselAsync"/> and <see cref="HandleChunkRequestAsync"/>).
    ///
    /// This value is a deliberately conservative, evidence-based compromise, not a proven optimum — see the
    /// M6 write-up in wiki/synthesis/roadmap.md for the full benchmark data. Two real-socket loopback-UDP
    /// benchmarks (an in-process harness and, more importantly, real separate <c>castr send</c>/<c>castr
    /// receive</c> processes matching actual CLI usage) both showed the *same* non-obvious shape: throughput
    /// does **not** monotonically improve with more concurrency. A window of 1-2 is roughly neutral-to-positive
    /// (occasionally ~1.8x faster, occasionally a wash, rarely a bit worse); a window of 3-4 is a consistent,
    /// significant regression (2-5x *slower*); and a window in the tens (64 was the first value tried) can
    /// make an 80 MB transfer effectively stall — the sender finishes its whole carousel while the receiver is
    /// still under 40% complete, because per-chunk repair recovery (5s default retry timeout) is far more
    /// expensive than the syscall overhead this change set out to remove.
    ///
    /// The likely mechanism: the old fully-sequential send loop was accidentally providing flow control — each
    /// awaited send's own latency naturally paced packet emission to roughly what <see cref="ReceiverSession"/>'s
    /// single-threaded receive loop can synchronously decode/verify/write. Concurrent sending removes that
    /// accidental pacing; past a small window the sender can emit faster than the receiver drains, overflowing
    /// the OS UDP receive buffer, and a single dropped packet stalls its whole chunk until repair recovers it.
    /// A larger safe window likely requires either a faster/parallel receive path or explicit congestion
    /// awareness on the send side — both out of scope for this change (send-path-only, no protocol changes).
    ///
    /// Given a real, measured downside (not just an unproven upside) at this exact default on the CLI's own
    /// default chunk size, this default deserves a second opinion before being trusted as "correct" — see the
    /// M6 write-up for the full trial-by-trial numbers.
    /// </summary>
    public const int DefaultSendWindowSize = 2;

    private readonly object _progressGate = new();
    private readonly HashSet<string> _grantedReceivers = [];
    private readonly PacketReassembler _reassembler = new();
    private readonly int _sendWindowSize = sendWindowSize > 0
        ? sendWindowSize
        : throw new ArgumentOutOfRangeException(nameof(sendWindowSize), "Send window size must be positive.");
    private int _sentChunks;
    private long _sentBytes;
    private bool _carouselComplete;

    /// <summary>
    /// Raised at every meaningful transition (start, each chunk broadcast, carousel completion, each new
    /// receiver granted the content key) with an immutable snapshot of current progress. Purely
    /// observational. The carousel and the JOIN/repair handler run concurrently, so handlers may be invoked
    /// from either task's thread; snapshots are built under an internal lock but the handler itself runs
    /// outside it. A UI should marshal to its own dispatcher and return quickly.
    /// </summary>
    public event Action<TransferProgress>? ProgressChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        EmitProgress(TransferPhase.Starting);
        await SendAnnounceAsync(cancellationToken).ConfigureAwait(false);
        await SendManifestAsync(cancellationToken).ConfigureAwait(false);

        var carousel = RunChunkCarouselAsync(cancellationToken);
        var requestHandler = HandleIncomingAsync(cancellationToken);
        await Task.WhenAll(carousel, requestHandler).ConfigureAwait(false);
    }

    private async Task SendAnnounceAsync(CancellationToken ct)
    {
        var digest = ChunkHash.Compute(ManifestCodec.Encode(signedManifest.Manifest));
        var announce = new AnnounceMessage(
            signedManifest.Manifest.SessionId, signedManifest.SenderPublicKey, digest,
            signedManifest.Manifest.TransferName, signedManifest.Manifest.IssuedAt);
        await SendMessageAsync(announce, ct).ConfigureAwait(false);
    }

    private async Task SendManifestAsync(CancellationToken ct) =>
        await SendMessageAsync(new ManifestMessage(signedManifest), ct).ConfigureAwait(false);

    /// <summary>Encodes a wire message and sends it as one or more MTU-safe datagrams (see <see cref="WirePacketizer"/>).</summary>
    private async Task SendMessageAsync(object message, CancellationToken ct)
    {
        foreach (var datagram in WirePacketizer.Fragment(MessageCodec.Encode(message), maxDatagramPayloadBytes))
            await transport.SendAsync(datagram, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends every chunk of every file with up to <see cref="_sendWindowSize"/> chunk-sends in flight
    /// concurrently, instead of one sequential <c>await</c> per wire packet. In isolation, that fully
    /// sequential form measurably wastes time on per-await/per-syscall overhead rather than network bandwidth;
    /// see <see cref="DefaultSendWindowSize"/>'s doc comment for why the right window size in practice is a
    /// real engineering tradeoff, not "bigger is always better" — real-socket measurements found a narrow safe
    /// range and a sharp cliff into much *worse* throughput past it. Concurrent sends necessarily complete (and
    /// thus hit the wire) out of strict chunk-index order; that is safe here because the wire protocol was
    /// already designed for arbitrary UDP reordering — <see cref="PacketReassembler"/> and
    /// <see cref="ChunkPacketAssembler"/> on the receive side reassemble purely by (file, chunk, packet) index,
    /// never by arrival order — so this only makes reordering (which a receiver already had to tolerate) more
    /// likely, not a new class of failure. <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource},ParallelOptions,Func{TSource,CancellationToken,ValueTask})"/>
    /// also gives correct-by-construction cancellation (stops scheduling new sends and propagates
    /// <see cref="OperationCanceledException"/>) and fail-fast error propagation (an exception from
    /// <c>transport.SendAsync</c>, e.g. a too-large-datagram <see cref="System.Net.Sockets.SocketException"/>,
    /// stops the loop and rethrows — it is never swallowed).
    /// </summary>
    private async Task RunChunkCarouselAsync(CancellationToken ct)
    {
        var sendOptions = new ParallelOptions { MaxDegreeOfParallelism = _sendWindowSize, CancellationToken = ct };

        for (int fileIndex = 0; fileIndex < signedManifest.Manifest.Files.Count; fileIndex++)
        {
            var file = signedManifest.Manifest.Files[fileIndex];
            var source = fileSources[fileIndex];
            var tree = merkleTrees[fileIndex];

            await Parallel.ForEachAsync(Enumerable.Range(0, file.ChunkCount), sendOptions, async (chunkIndex, token) =>
            {
                await SendChunkAsync(fileIndex, chunkIndex, source, tree, requestNonce: null, token).ConfigureAwait(false);

                int chunkLength = ChunkLayout.GetRange(file.Size, file.ChunkSize, chunkIndex).Length;
                lock (_progressGate)
                {
                    _sentChunks++;
                    _sentBytes += chunkLength;
                }
                EmitProgress(TransferPhase.Transferring);
            }).ConfigureAwait(false);
        }

        lock (_progressGate)
            _carouselComplete = true;
        EmitProgress(TransferPhase.Serving);
    }

    private async Task HandleIncomingAsync(CancellationToken ct)
    {
        await foreach (var packet in transport.ReceiveAsync(ct).ConfigureAwait(false))
        {
            var payload = _reassembler.Offer(packet.Payload);
            if (payload is null)
                continue; // an incomplete fragment (or malformed datagram) — nothing to handle yet

            switch (TryDecode(payload))
            {
                case ChunkRequestMessage request:
                    await HandleChunkRequestAsync(request, ct).ConfigureAwait(false);
                    break;
                case JoinRequestMessage join:
                    await HandleJoinRequestAsync(join, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task HandleChunkRequestAsync(ChunkRequestMessage request, CancellationToken ct)
    {
        if (!request.SessionId.AsSpan().SequenceEqual(signedManifest.Manifest.SessionId))
            return;
        if (!fileSources.TryGetValue(request.FileIndex, out var source) || !merkleTrees.TryGetValue(request.FileIndex, out var tree))
            return;

        // Same windowed-concurrency rationale as RunChunkCarouselAsync: a bulk cold-start repair batch can
        // legitimately span many thousands of chunk indices (see ChunkRequestMessage's UInt32 count widening,
        // wiki/synthesis/m1-core-summary.md), so this benefits from the same pipelining rather than one
        // sequential await per requested chunk.
        var sendOptions = new ParallelOptions { MaxDegreeOfParallelism = _sendWindowSize, CancellationToken = ct };
        await Parallel.ForEachAsync(request.ChunkIndices, sendOptions, async (chunkIndex, token) =>
            await SendChunkAsync(request.FileIndex, chunkIndex, source, tree, request.RequestNonce, token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private async Task HandleJoinRequestAsync(JoinRequestMessage join, CancellationToken ct)
    {
        if (!join.SessionId.AsSpan().SequenceEqual(signedManifest.Manifest.SessionId))
            return;

        // The receiver has, by definition, already verified and trusted this sender's signed manifest before
        // sending a JOIN_REQUEST — so the sender wraps the content key for anyone who completes the handshake
        // (the authorization boundary is unchanged; encryption only stops passive eavesdroppers). See ADR-0003.
        byte[] wrapped;
        try
        {
            wrapped = ContentKeyWrap.Wrap(
                senderEncryptionKey, join.ReceiverEncryptionPublicKey,
                signedManifest.Manifest.SessionId, join.ReceiverId, contentKey.Export());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return; // malformed receiver public key — ignore, don't crash the receive loop
        }

        var grant = new KeyGrantMessage(signedManifest.Manifest.SessionId, join.ReceiverId, wrapped);
        await SendMessageAsync(grant, ct).ConfigureAwait(false);

        bool isNewReceiver;
        lock (_progressGate)
            isNewReceiver = _grantedReceivers.Add(Convert.ToHexString(join.ReceiverId));
        if (isNewReceiver)
            EmitProgress(_carouselComplete ? TransferPhase.Serving : TransferPhase.Transferring);
    }

    private async Task SendChunkAsync(int fileIndex, int chunkIndex, IFileSource source, MerkleTree tree, byte[]? requestNonce, CancellationToken ct)
    {
        var file = signedManifest.Manifest.Files[fileIndex];
        var plaintext = await Chunker.ReadChunkAsync(source, file.ChunkSize, chunkIndex, ct).ConfigureAwait(false);
        var ciphertext = contentKey.EncryptChunk(signedManifest.Manifest.SessionId, fileIndex, chunkIndex, plaintext);
        var proof = tree.GetProof(chunkIndex);

        // A chunk whose whole envelope fits in one datagram goes as a single ChunkDataMessage/ChunkResponseMessage
        // (unchanged wire behavior). A larger chunk is split into identity-keyed ChunkPacketMessage wire packets
        // (see ChunkPacketizer) so it stays MTU-safe and accumulates across repair rounds.
        if (ChunkPacketizer.RequiresPacketization(ciphertext.Length, proof, maxDatagramPayloadBytes))
        {
            foreach (var packet in ChunkPacketizer.Split(
                signedManifest.Manifest.SessionId, fileIndex, chunkIndex, ciphertext, proof, maxDatagramPayloadBytes))
            {
                await SendMessageAsync(packet, ct).ConfigureAwait(false);
            }
            return;
        }

        object message = requestNonce is null
            ? new ChunkDataMessage(signedManifest.Manifest.SessionId, fileIndex, chunkIndex, ciphertext, proof)
            : new ChunkResponseMessage(signedManifest.Manifest.SessionId, requestNonce, fileIndex, chunkIndex, ciphertext, proof);

        await SendMessageAsync(message, ct).ConfigureAwait(false);
    }

    private static object? TryDecode(byte[] payload)
    {
        try { return MessageCodec.Decode(payload); }
        catch { return null; } // malformed/corrupt packet — ignore, don't crash the receive loop
    }

    /// <summary>
    /// Exposes this sender as a read-only <see cref="ISwarmContentSource"/> so a <see cref="SwarmServeListener"/>
    /// can answer unicast TCP pull requests — the sender holds every chunk and can grant the content key.
    /// Purely additive; the multicast carousel/repair behavior above is untouched.
    /// </summary>
    public ISwarmContentSource CreateSwarmContentSource() => new SenderContentSource(this);

    private SignedManifest ServeManifest => signedManifest;

    private async ValueTask<SwarmChunk?> BuildSwarmChunkAsync(int fileIndex, int chunkIndex, CancellationToken ct)
    {
        if (!fileSources.TryGetValue(fileIndex, out var source) || !merkleTrees.TryGetValue(fileIndex, out var tree))
            return null;

        var files = signedManifest.Manifest.Files;
        if (fileIndex < 0 || fileIndex >= files.Count)
            return null;
        var file = files[fileIndex];
        if (chunkIndex < 0 || chunkIndex >= file.ChunkCount)
            return null;

        var plaintext = await Chunker.ReadChunkAsync(source, file.ChunkSize, chunkIndex, ct).ConfigureAwait(false);
        var ciphertext = contentKey.EncryptChunk(signedManifest.Manifest.SessionId, fileIndex, chunkIndex, plaintext);
        var proof = tree.GetProof(chunkIndex);
        return new SwarmChunk(ciphertext, proof);
    }

    private KeyGrantMessage? GrantContentKey(JoinRequestMessage join)
    {
        if (!join.SessionId.AsSpan().SequenceEqual(signedManifest.Manifest.SessionId))
            return null;

        byte[] wrapped;
        try
        {
            wrapped = ContentKeyWrap.Wrap(
                senderEncryptionKey, join.ReceiverEncryptionPublicKey,
                signedManifest.Manifest.SessionId, join.ReceiverId, contentKey.Export());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null; // malformed receiver public key — refuse the grant, don't crash the serve loop
        }

        return new KeyGrantMessage(signedManifest.Manifest.SessionId, join.ReceiverId, wrapped);
    }

    private sealed class SenderContentSource(SenderSession session) : ISwarmContentSource
    {
        public SignedManifest? Manifest => session.ServeManifest;

        public ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            session.BuildSwarmChunkAsync(fileIndex, chunkIndex, cancellationToken);

        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => session.GrantContentKey(request);
    }

    private void EmitProgress(TransferPhase phase)
    {
        var handler = ProgressChanged;
        if (handler is null)
            return;

        TransferProgress snapshot;
        lock (_progressGate)
            snapshot = BuildProgressLocked(phase);
        handler(snapshot);
    }

    private TransferProgress BuildProgressLocked(TransferPhase phase)
    {
        int totalChunks = 0;
        long totalBytes = 0;
        foreach (var file in signedManifest.Manifest.Files)
        {
            totalChunks += file.ChunkCount;
            totalBytes += file.Size;
        }

        return new TransferProgress(
            TransferRole.Sender,
            phase,
            signedManifest.Manifest.TransferName,
            signedManifest.Manifest.Files.Count,
            totalChunks,
            _sentChunks,
            totalChunks - _sentChunks,
            totalBytes,
            _sentBytes,
            _grantedReceivers.Count);
    }
}
