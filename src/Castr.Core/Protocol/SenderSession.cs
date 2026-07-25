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
    /// <b>Deliberately 1 — a true no-op relative to pre-M6 behavior</b>, not a tuned "improvement" default. See
    /// the M6 write-up in wiki/synthesis/roadmap.md for the full three-round benchmark history before changing
    /// this. Round 1 shipped a higher default (2) based on real-socket measurements that looked like a net win;
    /// independent QA re-measured more thoroughly and found it was actually a consistent ~1.8-2.7x *regression*
    /// versus window=1 on that round's receive path, not the "roughly neutral, occasionally faster" picture the
    /// smaller round-1 sample suggested. Root cause (confirmed by both QA and a systems-design review, and
    /// verified again first-hand): the fully-sequential pre-M6 send loop was accidentally providing flow
    /// control — each awaited send's own latency naturally paced packet emission to what
    /// <see cref="ReceiverSession.RunAsync"/>'s single serialized per-packet chain (Merkle/AEAD verify, disk
    /// write, outbound PEER_HAVE broadcast, all under one lock) could keep up with, and
    /// <see cref="Castr.Core.Transport.Udp.UdpMulticastTransport"/>'s receive loop had no decoupling from that
    /// chain either — so the OS receive buffer only drained as fast as the whole chain ran. Round 2 fixed that
    /// receive-side bottleneck directly (a dedicated socket-reader task feeding a bounded channel, plus
    /// explicit larger socket buffers — see <c>UdpMulticastTransport.SocketBufferBytes</c>/<c>InboxCapacity</c>)
    /// and re-measured: with that fix in place, window=2 became a consistent, real ~1.4-1.6x win in both a 1:1
    /// benchmark and a 3-receiver (one deliberately throttled to a single, low-priority CPU core) fan-out
    /// benchmark — no collapse, every receiver always finished byte-identical — while window≥4 still gave the
    /// gain back (roughly back to window=1's own throughput, not a catastrophic regression like round 1's
    /// window≥3, but not better either).
    ///
    /// Despite that genuinely encouraging round-2 data, the shipped default stays at 1: this specific value (2)
    /// has now been wrong once already on a plausible-looking, smaller sample, and one more (larger, still
    /// single-machine) benchmarking pass is not the same as a second opinion from someone who didn't run the
    /// numbers. window=1 is not merely "safe" by assumption, either: <see cref="RunAsync"/> already ran the
    /// carousel and the JOIN/repair listener as two concurrently-scheduled tasks (via <c>Task.WhenAll</c>)
    /// before this whole pipelining change existed, so their sends could already interleave on the wire — a
    /// window of 1 on each of <see cref="RunChunkCarouselAsync"/> and <see cref="HandleChunkRequestAsync"/>
    /// independently reproduces exactly that pre-existing concurrency, no more and no less, which is why it is
    /// a genuine no-op rather than a new, unvalidated behavior.
    ///
    /// The <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource},ParallelOptions,Func{TSource,CancellationToken,ValueTask})"/>-based
    /// windowing mechanism itself is intact, tested (see <c>SenderSessionPipeliningTests</c>), and available to
    /// any caller that has validated a higher window for their own receiver hardware/network — this constant is
    /// only the shipped default, not a ceiling. Bumping it to 2 by default is the natural next step once someone
    /// independent has looked at the round-2 numbers.
    /// </summary>
    public const int DefaultSendWindowSize = 1;

    private readonly object _progressGate = new();
    private readonly HashSet<string> _grantedReceivers = [];
    private readonly PacketReassembler _reassembler = new();
    // Known limitation, deliberately not fixed here: RunChunkCarouselAsync and HandleChunkRequestAsync each
    // gate on their own independent ParallelOptions.MaxDegreeOfParallelism = _sendWindowSize, so a chunk-repair
    // burst arriving while the carousel is still running can transiently push real concurrent
    // transport.SendAsync calls to up to 2x _sendWindowSize, not a hard-enforced single window. A shared
    // SemaphoreSlim gate across both loops was prototyped and does close this gap, but cost a measured ~30%
    // real-socket throughput regression (repeated back-to-back A/B on the same benchmark: ~11 MB/s with the
    // gate bypassed vs. ~7.6 MB/s with it, at window=2, 80 MB/8192-byte-chunk, no contention even happening in
    // that test) — the extra SemaphoreSlim.WaitAsync/Release pair per chunk is not free even when it never
    // actually blocks, at the "once per chunk, tens of thousands of chunks" call frequency this is on. Given
    // the double-counting scenario is narrow (needs simultaneous heavy repair traffic during the main
    // carousel) and the fix's cost directly undermines M6's actual goal (throughput), it was not shipped — see
    // the M6 write-up in wiki/synthesis/roadmap.md. Worth reconsidering with a cheaper gating primitive (e.g. a
    // manually-managed counter/lock rather than SemaphoreSlim) if the double-counting ever proves to matter in
    // practice.
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
        // sequential await per requested chunk. This is independently windowed from the carousel (see
        // _sendWindowSize's doc comment for the known, deliberately-not-fixed double-counting limitation that
        // follows from that).
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
