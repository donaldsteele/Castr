using System.Threading.Channels;
using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Security;
using Castr.Core.Swarm;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Trust;

namespace Castr.Core.Protocol;

/// <param name="PeerHaveInterval">
/// Minimum gap between two PEER_HAVE broadcasts for the same file. Null uses
/// <see cref="ReceiverSession.DefaultPeerHaveInterval"/>. Injectable rather than a constant because the right
/// value tracks link and host speed, which real-socket benchmarking has shown move by 2x or more between
/// configurations.
/// </param>
/// <param name="CarouselIdleThreshold">
/// How long a file's carousel watermark must stop advancing before every missing index in that file becomes
/// eligible for repair. Null uses <see cref="ReceiverSession.DefaultCarouselIdleThreshold"/>. Injectable for the
/// same reason, and so tests can exercise the valve without burning real wall-clock time.
/// </param>
/// <param name="ChunkCacheBytes">
/// Ceiling on the total bytes of verified chunk <i>ciphertext</i> this receiver keeps in memory for relaying to
/// peers during repair. Null uses <see cref="ReceiverSession.DefaultChunkCacheBytes"/>. Injectable mainly so
/// tests can set it small enough to force eviction on a tiny transfer.
/// </param>
public sealed record ReceiverSessionOptions(
    string DestinationRoot,
    UnknownSenderPolicy UnknownSenderPolicy = UnknownSenderPolicy.Deny,
    bool IsInteractive = false,
    TimeSpan? PeerHaveInterval = null,
    TimeSpan? CarouselIdleThreshold = null,
    long? ChunkCacheBytes = null,
    ISessionRegistry? SessionRegistry = null);

/// <summary>
/// Why a receiver did or did not relay a chunk to a peer that asked for it — see
/// <see cref="ReceiverSession.ChunkServeCount"/> and <see cref="ReceiverSession.ChunkServeObserved"/>.
///
/// <para><b>Why this exists at all.</b> Peer-served repair degrades <i>silently</i> in three independent ways
/// that have accumulated over three milestones: a peer on a mismatched <c>--datagram-size</c> stops being able
/// to relay (M9), a receiver whose sink is not an <see cref="IReadableFileSink"/> cannot serve an evicted chunk,
/// and a chunk verified before the KEY_GRANT has no plaintext to rebuild from. Each is individually harmless —
/// the requester falls back to the sender — but together they mean the bandwidth-offload feature can be
/// completely non-functional with no error, no log and no counter anywhere. These are that counter.</para>
/// </summary>
public enum ChunkServeOutcome
{
    /// <summary>Relayed straight from the in-memory ciphertext cache.</summary>
    ServedFromCache,
    /// <summary>Relayed after re-reading the plaintext from disk and re-encrypting it (the cold path).</summary>
    ServedByRebuild,
    /// <summary>We hold no verified proof for this chunk, so we never verified it. The ordinary "not my chunk" case.</summary>
    DeclinedNotVerified,
    /// <summary>Evicted, and the content key has not been granted yet, so the plaintext was never written.</summary>
    DeclinedNoContentKey,
    /// <summary>Evicted while still awaiting decryption — its plaintext is not on disk to read back.</summary>
    DeclinedAwaitingDecrypt,
    /// <summary>Evicted, and this session's <see cref="IFileSink"/> does not implement <see cref="IReadableFileSink"/>.</summary>
    DeclinedSinkNotReadable,
    /// <summary>Evicted, and reading the plaintext back failed or came up short (moved, locked, or closed file).</summary>
    DeclinedReadFailed,
    /// <summary>Rebuilt, but the result did not verify against the signed Merkle root — the destination changed underneath us.</summary>
    DeclinedReverifyFailed,
}

/// <summary>
/// Drives the receiver side of a single transfer over one <see cref="IMulticastTransport"/>: evaluates
/// sender trust, verifies the signed manifest, joins the transfer to obtain the per-transfer content key
/// (JOIN_REQUEST/KEY_GRANT), verifies each chunk's <b>ciphertext</b> via its Merkle proof, decrypts it, writes
/// the plaintext, tracks per-file completion, broadcasts PEER_HAVE, and both requests and serves repairs.
/// </summary>
/// <remarks>
/// Merkle verification and AEAD decryption are two independent checks (see ADR-0003): the proof binds the
/// ciphertext to the signed transfer at a specific position; the AEAD tag independently authenticates the
/// ciphertext's integrity. A chunk is therefore verifiable (and re-servable to peers) the moment it arrives,
/// even before the content key does — the receiver caches the exact (ciphertext, proof) pair it verified and
/// decrypts it once the KEY_GRANT lands. A relaying peer forwards that ciphertext without needing to read it.
/// Known M1 scope trim (unchanged): CHUNK_REQUEST/RESPONSE — and now JOIN_REQUEST/KEY_GRANT — travel over the
/// shared multicast channel rather than being unicast-addressed; correctness is unaffected because each
/// KEY_GRANT is cryptographically readable only by its addressed receiver.
/// </remarks>
public sealed class ReceiverSession
{
    /// <summary>
    /// Default minimum wall-clock gap between two PEER_HAVE broadcasts for the same file. Override per session
    /// via <see cref="ReceiverSessionOptions.PeerHaveInterval"/>.
    ///
    /// <para>PEER_HAVE carries the <b>whole</b> per-file bitmap, so emitting one per verified chunk makes total
    /// gossip quadratic in file size: <c>FileSize² / (8 · chunkSize²)</c> bytes. At the old 8 KiB default that was
    /// ~13.5 MB of pure gossip for an 80 MiB transfer and ~1.3 GB for an 800 MiB one — more than the payload
    /// itself. Worse, the encoded message (~1328 bytes for an 80 MiB transfer) exceeded
    /// <see cref="WirePacketizer.DefaultMaxDatagramPayload"/>, so each one fragmented into two or more datagrams,
    /// and per-datagram cost — not bytes — is what both sides are actually bound by.</para>
    ///
    /// <para><b>Re-validated at the 256 KiB default, and kept unchanged.</b> Because the term is quadratic in
    /// <i>chunkSize</i> too, a 32x larger chunk cuts it 1024x: a 100 MB transfer now gossips ~20 KB total and an
    /// 800 MiB one ~1.3 MB, and the bitmap fits one datagram at any realistic file size. So this interval no
    /// longer defends against anything close to a scaling wall, and a passive sniffer counted just <b>33</b>
    /// PEER_HAVE datagrams across a lossless 100 MB transfer. It is retained anyway, on purpose: it still removes
    /// ~92% of those emissions (400 chunks would otherwise mean 400), it costs nothing when it does not bind, and
    /// it is the only thing keeping the quadratic term harmless for a caller who lowers <c>--chunk-size</c> —
    /// which remains fully supported. Removing a cheap guard because the <i>current default</i> happens to sit
    /// outside its range would re-arm the wall for everyone who moves off that default.</para>
    ///
    /// <para>Coalescing to a fixed interval keeps the message's documented dual purpose intact (see
    /// wiki/concepts/repair-protocol.md: PEER_HAVE doubles as free peer discovery on the multicast tier) while
    /// making the gossip volume linear in <i>time</i> rather than quadratic in file size. Two emissions are
    /// exempt from the interval because they are the ones peers cannot afford to wait for: a file's <i>first</i>
    /// chunk (so discovery still happens promptly at the start of a transfer) and a file's bitmap becoming
    /// <i>complete</i> (so peers learn about a fully-stocked repair source immediately).</para>
    /// </summary>
    public static readonly TimeSpan DefaultPeerHaveInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Default time a file's carousel watermark must stop advancing before the watermark stops gating repair for
    /// that file. Override per session via <see cref="ReceiverSessionOptions.CarouselIdleThreshold"/>.
    ///
    /// <para>Normally a missing chunk is only eligible for repair once the sender's carousel has demonstrably
    /// passed it — i.e. its index is at or below the highest index seen for that file. Without that gate, the
    /// first repair pass asks for essentially the whole file, because "not received yet" and "never sent yet" are
    /// indistinguishable from the bitmap alone. The sender then re-reads, re-encrypts, re-proves and
    /// re-multicasts thousands of chunks concurrently with the still-running carousel, roughly doubling wire
    /// bytes and sender CPU for no delivery benefit.</para>
    ///
    /// <para>But the watermark alone would hang forever if a file's <b>last</b> chunks were lost: no higher index
    /// will ever arrive to lift the watermark past them. So once that file's watermark has not advanced for this
    /// long, its carousel is presumed finished (or stalled) and <i>every</i> missing index in it becomes eligible
    /// again. This is the safety valve that makes the watermark sound rather than a deadlock, and it is what
    /// <c>Transfer_FinalChunkOfFileDropped_StillCompletesViaRepair</c> exists to prove.</para>
    ///
    /// <para>Both the timer and the watermark are strictly per file, the timer is refreshed only by genuine
    /// carousel advancement, and a file's valve stays shut until that file is known to have been transmitted —
    /// see <see cref="ObserveChunkActivity"/> and <see cref="RequestRepairsAsync"/>. Two earlier versions got
    /// this wrong in opposite directions, and both were the same modelling error (at seed time, "never reached"
    /// and "finished" are indistinguishable): one global last-arrival timestamp refreshed by any datagram
    /// <i>hung</i> a started file, because multicast repair traffic from any peer held every receiver's valve
    /// shut; then seeding every file's timer at manifest acceptance <i>over-requested</i>, because the sender
    /// carousels files sequentially, so a later file's valve opened before its carousel had begun.
    /// <see cref="RepairOptions.MaxRequestsPerPass"/> bounds amplification independently for files above ~268 MB,
    /// so below that size this threshold is once again the main thing standing between the system and a repair
    /// storm — see <see cref="RepairOptions.DefaultMaxRequestsPerPass"/>.</para>
    ///
    /// <para><b>Re-validated by measurement when the default chunk size went 8 KiB -> 256 KiB, and kept.</b> The
    /// worry was structural: the quantity this threshold races is the gap between two <i>strict watermark
    /// advances</i>, and a 32x larger chunk means 32x fewer advances, so the obvious prediction is that the
    /// margin shrinks 32x.</para>
    ///
    /// <para><b>The result that actually answers that worry is a load control, not a margin figure.</b> Under CPU
    /// saturation (16 contending tasks) the <i>old</i> 8 KiB default produced <b>more</b> false idles than the new
    /// 256 KiB one — <b>7 versus 3</b>. So false-idle exposure under contention is <i>pre-existing</i>, and this
    /// change <i>reduces</i> it. Two further facts from the same runs: every transfer that false-idled still
    /// completed <b>byte-identical</b> (the failure mode is amplification and latency, never correctness), and
    /// <b>1 MiB at the same load timed out entirely</b> — a third independent measurement converging on 256 KiB.</para>
    ///
    /// <para><b>On an unloaded host</b>, a passive external sniffer (100 MB, real two-process, warm, n=4 per arm)
    /// measured the advance-gap distribution directly:</para>
    /// <list type="bullet">
    /// <item>8 KiB — mean 0.65 ms, p99 1.2 ms, <b>worst 19.6 ms</b> -> 51x</item>
    /// <item>256 KiB — mean 15.3 ms, p99 24.1 ms, <b>worst 27.7 ms</b> -> <b>36x</b></item>
    /// <item>1 MiB — mean 57.6 ms, p99 86.6 ms, <b>worst 96.2 ms</b> -> 10.4x</item>
    /// </list>
    /// <para>The <i>mean</i> gap scales with the chunk size (23x from 8 KiB to 256 KiB, as predicted) but the
    /// <i>maximum</i> barely moves (19.6 -> 27.7 ms, 1.4x), because the worst case is set by host scheduling
    /// jitter rather than the per-chunk interval — which is why the 32x prediction did not hold.</para>
    ///
    /// <para><b>Read those multiples as a property of an unloaded host, not as a safety factor.</b> A harsher
    /// in-process harness measured the same quantity at ~2.7x <i>at both chunk sizes</i>, and a more
    /// representative adverse figure is the 382 ms worst gap seen on a rate-limited path (2.6x clear). On a
    /// contended host this threshold is breached at any chunk size; that is what the load control above measures,
    /// and it is the reason the argument for keeping 1000 ms rests on the control rather than on "36x".</para>
    /// </summary>
    public static readonly TimeSpan DefaultCarouselIdleThreshold = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Default ceiling on retained verified-chunk <b>ciphertext</b>. Override per session via
    /// <see cref="ReceiverSessionOptions.ChunkCacheBytes"/>.
    ///
    /// <para><b>What this bounds and why it had to exist.</b> A receiver keeps the ciphertext of every chunk it
    /// verifies so it can relay chunks to peers during repair. That cache previously had no eviction at all — it
    /// grew to the full size of the transfer, so a 5 GB transfer retained ~5 GB, every buffer of it on the large
    /// object heap at the 256 KiB default chunk size. That is a hard wall (an <c>OutOfMemoryException</c> that
    /// terminates the process), not a rate limit, and it scaled with transfer size rather than with anything the
    /// repair protocol actually needs.</para>
    ///
    /// <para><b>Why 32 MiB.</b> The cache only has to cover the window in which a peer can still usefully ask for
    /// a chunk before falling back to a cold read. Repair requests are emitted on the caller's repair period
    /// (250 ms in <c>Castr.Cli</c>) and a file's carousel valve opens at
    /// <see cref="DefaultCarouselIdleThreshold"/> (1 s), so the interesting window is on the order of a second of
    /// transfer. 32 MiB is ~1.3 s at the ~24 MB/s loopback figure in <c>docs/benchmarks/throughput-runs.md</c>
    /// and ~2.7 s at this LAN's ~11.8 MB/s multicast ceiling — comfortably past both, and 128 chunks at the
    /// 256 KiB default. Note it is deliberately <i>smaller</i> than the largest burst one inbound CHUNK_REQUEST
    /// can command (<c>MaxChunksPerRequestFor(1472)</c> = 336 chunks = 84 MiB): a bulk cold-start request is
    /// exactly the case that should be served from disk rather than from a cache sized for it.</para>
    ///
    /// <para><b>The cold path.</b> Evicting ciphertext is safe because it can be reconstructed exactly:
    /// <see cref="ContentKey.EncryptChunk"/> is a pure deterministic function of
    /// (key, sessionId, fileIndex, chunkIndex, plaintext) — the ChaCha20-Poly1305 nonce is
    /// <c>fileIndex|chunkIndex|0000</c> and the AAD is <c>sessionId|fileIndex|chunkIndex</c>, neither of which
    /// carries any randomness — so re-reading the plaintext this receiver already wrote to disk and re-encrypting
    /// it reproduces the evicted ciphertext byte for byte. <c>SenderSession.SendChunkAsync</c> has relied on the
    /// same property since M1: it re-reads and re-encrypts on every repair serve, and the receiver's packet
    /// assembler accumulates carousel and repair fragments of one chunk interchangeably.</para>
    ///
    /// <para><b>Merkle proofs are retained, not regenerated.</b> A proof cannot be rebuilt by the receiver:
    /// <see cref="MerkleTree.GetProof"/> needs the whole leaf set for the file and the receiver holds only the
    /// signed root, so mid-transfer — precisely when repair matters — it has no way to derive the sibling hashes.
    /// Proofs are therefore kept for the life of the session in <see cref="_chunkProofs"/>. They are small
    /// against what they replace: ~<c>32 x log2(chunkCount)</c> bytes of hash, ~1.5 KB of object graph in
    /// practice, against 256 KiB of ciphertext — a ~170x reduction, and the reason this design is a bound on the
    /// large term rather than on everything.</para>
    /// </summary>
    public const long DefaultChunkCacheBytes = 32L * 1024 * 1024;

    private readonly byte[] _receiverId;
    private readonly ITrustStore _trustStore;
    private readonly IMulticastTransport _transport;
    private readonly ISystemClock _clock;
    private readonly ReceiverSessionOptions _options;
    private readonly Func<string, long, IFileSink> _sinkFactory;
    private readonly IPeerTable _peerTable;
    private readonly RepairCoordinator _repairCoordinator;
    private readonly ITrustPrompt? _trustPrompt;
    private readonly int _maxDatagramPayloadBytes;
    private readonly PacketReassembler _reassembler = new();
    private readonly TimeSpan _peerHaveInterval;
    private readonly TimeSpan _carouselIdleThreshold;
    private readonly RepairOptions _repairOptions;
    private readonly int _maxChunksServedPerRequest;

    // Serializes the two concurrent drivers of a receive (ReceiveRunner runs RunAsync and the repair loop
    // that calls RequestRepairsAsync via Task.WhenAll on separate tasks). All the mutable transfer state below
    // — _bitmaps, _sinks, _chunkCache, _pendingDecrypt, _contentKey, _verifiedBytes, the ChunkBitmaps, the
    // RepairCoordinator and PeerTable — is otherwise touched from both without any synchronization, which
    // races (observed under the container E2E fan-out as KeyNotFoundException reading a not-yet-populated
    // _bitmaps entry and "Collection was modified" enumerating state mid-mutation). One gate held around each
    // packet-handle and each repair pass makes those two flows mutually exclusive.
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    // Replaced once the manifest is accepted with an instance bounded by that transfer's known chunk size, so an
    // attacker cannot force an allocation larger than a legitimate chunk. Until then no chunk packet is processed
    // (HandleChunkPacketAsync early-returns while Manifest is null), so the wide default bound is never exercised.
    private ChunkPacketAssembler _chunkAssembler = new();

    private readonly Key _encryptionKey;
    private readonly byte[] _encryptionPublicKey;

    private readonly Dictionary<int, ChunkBitmap> _bitmaps = [];
    private readonly Dictionary<int, IFileSink> _sinks = [];
    private readonly HashSet<(int File, int Chunk)> _pendingDecrypt = [];

    // ---- Verified-chunk cache (see DefaultChunkCacheBytes) ----
    //
    // Split deliberately in two, because the two halves have wildly different sizes and only one of them can be
    // reconstructed:
    //
    //   _chunkProofs      — the Merkle proof for every chunk this receiver has verified. Retained for the whole
    //                       session. Unbounded in chunk count but ~170x smaller than the ciphertext it replaces,
    //                       and NOT reconstructible: MerkleTree.GetProof needs the file's whole leaf set and this
    //                       side holds only the signed root.
    //   _cachedCiphertext — the verified ciphertext, byte-bounded and evicted least-recently-used. Reconstructible
    //                       exactly by re-reading the plaintext from the sink and re-encrypting (deterministic
    //                       AEAD), which is what TryGetServableChunkAsync does on a miss.
    //
    // _lru is most-recently-used first; each dictionary value is that chunk's node in it, so a hit is O(1) to
    // promote. All of this is touched only under _stateGate, like every other dictionary in this class.
    private readonly Dictionary<(int File, int Chunk), MerkleProof> _chunkProofs = [];
    private readonly Dictionary<(int File, int Chunk), LinkedListNode<CachedChunk>> _cachedCiphertext = [];
    private readonly LinkedList<CachedChunk> _lru = new();
    private readonly long _chunkCacheBytes;
    private long _cachedBytes;

    private sealed record CachedChunk((int File, int Chunk) Key, byte[] Ciphertext);

    // Peer chunk-serving work: planned under _stateGate by HandleChunkRequest, executed by a dedicated worker
    // task (RunChunkServeWorkerAsync) that shares nothing with the receive loop.
    //
    // Why a separate task rather than a post-gate drain in RunAsync — a distinction that cost a failed test to
    // learn. Serving an EVICTED chunk costs a disk read plus a ChaCha20 re-encrypt plus a BLAKE3 re-verify:
    // ~0.8 ms per 256 KiB chunk with a warm page cache, 5-10 ms when the read is a real seek on a transfer larger
    // than RAM. One CHUNK_REQUEST may name up to MaxChunksPerRequest (336) indices, so ONE unauthenticated
    // datagram (HandleChunkRequest checks only the session ID) buys ~263 ms of work warm and 1.7-3.4 s cold.
    //
    // Doing that inline under _stateGate was the original defect: no packet is handled and no repair pass runs
    // while the gate is held, and the cold figure exceeds CarouselIdleThreshold — which is exactly the input a
    // false idle needs, and a false idle restores the full M7 repair storm. But merely moving it *after* the gate
    // release, still on RunAsync's own loop, only fixes half of that: repair passes are freed, while packet
    // processing — and therefore watermark advancement, the thing the valve actually watches — is still stalled
    // behind the read. Only a separate task removes the class.
    //
    // Bounded and DropWrite on purpose: a flood of requests is discarded rather than queued, which is the
    // degradation the repair protocol already expects (the requester's own timer re-asks and reaches another peer
    // or the sender). Capacity is one request's worth of indices.
    private readonly Channel<PendingChunkServe> _serveChannel;

    /// <summary>
    /// One chunk this receiver has agreed to relay to a peer. <see cref="Ciphertext"/> non-null means it was a
    /// cache hit and the bytes are ready; null means <see cref="Rebuild"/> carries everything the cold path needs
    /// to reconstruct them off-gate. Every field is either immutable or safe to touch outside
    /// <see cref="_stateGate"/> — see <see cref="ColdRebuild"/>.
    /// </summary>
    private sealed record PendingChunkServe(
        byte[] SessionId,
        byte[] RequestNonce,
        int FileIndex,
        int ChunkIndex,
        MerkleProof Proof,
        byte[]? Ciphertext,
        ColdRebuild? Rebuild);

    /// <summary>
    /// The inputs to an off-gate cold rebuild, snapshotted under <see cref="_stateGate"/>.
    ///
    /// <para>Each one is safe to use without the gate: <see cref="ManifestFileEntry"/> is an immutable record from
    /// the signed manifest; <see cref="ContentKey"/> is set once and NSec keys are safe for concurrent algorithm
    /// use; <see cref="IReadableFileSink"/> reads are positional and stated to be safe alongside writes to other
    /// ranges. The one genuine race is a sink being <c>Complete()</c>d by the gated path mid-read, which surfaces
    /// as <see cref="ObjectDisposedException"/> and is caught and reported as a decline — it fails closed.</para>
    /// </summary>
    private sealed record ColdRebuild(ManifestFileEntry File, IReadableFileSink Sink, ContentKey Key);

    // Serve outcome counters, indexed by (int)ChunkServeOutcome. Incremented from both the gated planning path
    // and the un-gated drain, hence Interlocked rather than plain increments.
    private readonly long[] _serveCounters = new long[Enum.GetValues<ChunkServeOutcome>().Length];

    // PEER_HAVE coalescing state (see PeerHaveInterval). _lastPeerHaveAt is when this file's bitmap was last
    // announced; _pendingPeerHave holds bitmap snapshots taken under _stateGate but not yet sent, so the send
    // itself happens outside the gate (drained by RunAsync). Both are keyed by file index and — like every
    // other dictionary here — are only ever mutated from RunAsync's gated packet path.
    private readonly Dictionary<int, DateTimeOffset> _lastPeerHaveAt = [];
    private readonly Dictionary<int, byte[]> _pendingPeerHave = [];

    // Carousel watermark state (see CarouselIdleThreshold), both PER FILE and both updated from the gated packet
    // path, read by RequestRepairsAsync under the same gate:
    //   _highestChunkIndexSeen  — the highest chunk index observed for that file
    //   _carouselAdvancedAt     — when that file's watermark last actually INCREASED, from a carousel delivery
    //
    // Why "when the watermark last increased" and not "when a chunk last arrived": a chunk arriving tells you
    // nothing about the carousel's position unless it is a chunk you had never seen that high before. Repair
    // responses are multicast by design, so with an arrival-based timer every receiver's repair traffic — and
    // any re-delivery of a chunk already held — kept every other receiver's valve permanently refreshed, and a
    // file whose tail was lost could never become eligible. Keying on watermark advancement makes re-deliveries
    // of already-seen indices inert, which is exactly what they are as evidence.
    //   _lastCarouselAdvanceAt  — when ANY file's watermark last advanced from a carousel delivery, seeded at
    //                             manifest acceptance. Session-level escape hatch for a file whose carousel is
    //                             never observed at all (every chunk lost), which no per-file signal can detect.
    private readonly Dictionary<int, int> _highestChunkIndexSeen = [];
    private readonly Dictionary<int, DateTimeOffset> _carouselAdvancedAt = [];
    private DateTimeOffset? _lastCarouselAdvanceAt;

    // Round-robin start file for the next repair pass, so MaxRequestsPerPass cannot let a low-numbered file's
    // gaps permanently starve a later file's.
    private int _repairPassCursor;

    private ContentKey? _contentKey;
    private long _verifiedBytes;

    public ReceiverSession(
        byte[] receiverId,
        ITrustStore trustStore,
        IMulticastTransport transport,
        ISystemClock clock,
        ReceiverSessionOptions options,
        Func<string, long, IFileSink> sinkFactory,
        IPeerTable? peerTable = null,
        RepairCoordinator? repairCoordinator = null,
        ITrustPrompt? trustPrompt = null,
        int maxDatagramPayloadBytes = WirePacketizer.DefaultMaxDatagramPayload)
    {
        // Range-checked and captured once, here, for the life of the session: the budget determines how this
        // session slices chunks when it relays a repair response, and ChunkPacketAssembler.Offer rejects packets
        // whose slicing metadata disagrees with the first one seen for a chunk. See
        // WirePacketizer.ValidateMaxDatagramPayload for why it must never change mid-session.
        maxDatagramPayloadBytes = WirePacketizer.ValidateMaxDatagramPayload(
            maxDatagramPayloadBytes, nameof(maxDatagramPayloadBytes));

        _receiverId = receiverId;
        _trustStore = trustStore;
        _transport = transport;
        _clock = clock;
        _options = options;
        _sinkFactory = sinkFactory;
        _peerTable = peerTable ?? new PeerTable();
        // Sized against THIS session's datagram budget, not the shipped default, so a receiver configured with an
        // unusual budget still never emits a repair request that has to be fragmented (see
        // RepairOptions.MaxChunksPerRequestFor).
        _repairOptions = RepairOptions.Default with
        {
            MaxChunksPerRequest = RepairOptions.MaxChunksPerRequestFor(maxDatagramPayloadBytes),
        };
        // A caller that injects its own coordinator owns its options; mirror the per-pass cap from it so the two
        // cannot silently disagree about the pass budget.
        _repairCoordinator = repairCoordinator ?? new RepairCoordinator(_peerTable, clock, _repairOptions);
        if (repairCoordinator is not null)
        {
            _repairOptions = _repairOptions with
            {
                MaxChunksPerRequest = repairCoordinator.MaxChunksPerRequest,
                MaxRequestsPerPass = repairCoordinator.MaxRequestsPerPass,
            };
        }
        _trustPrompt = trustPrompt;
        _maxDatagramPayloadBytes = maxDatagramPayloadBytes;
        _peerHaveInterval = options.PeerHaveInterval ?? DefaultPeerHaveInterval;
        _carouselIdleThreshold = options.CarouselIdleThreshold ?? DefaultCarouselIdleThreshold;
        // Zero is a legitimate setting ("never retain ciphertext, always serve cold"); negative is not.
        _chunkCacheBytes = Math.Max(0, options.ChunkCacheBytes ?? DefaultChunkCacheBytes);
        // Bound on chunks this receiver will serve from a single inbound CHUNK_REQUEST when relaying to a peer —
        // see HandleChunkRequest. Derived from this session's own datagram budget, mirroring SenderSession.
        _maxChunksServedPerRequest = RepairOptions.MaxChunksPerRequestFor(maxDatagramPayloadBytes);
        _serveChannel = Channel.CreateBounded<PendingChunkServe>(new BoundedChannelOptions(_maxChunksServedPerRequest)
        {
            FullMode = BoundedChannelFullMode.DropWrite, // shed load rather than queue it — see _serveChannel
            SingleReader = true,
            SingleWriter = true,                         // only the gated packet path ever writes
        });

        // Every receiver identity holds its own X25519 encryption keypair (ADR-0003).
        _encryptionKey = EncryptionKeys.Create();
        _encryptionPublicKey = EncryptionKeys.ExportPublicKey(_encryptionKey);
    }

    public SignedManifest? Manifest { get; private set; }

    public event Action<TrustDecision, PublicKeyId>? SenderTrustDenied;

    /// <summary>
    /// Raised at every meaningful transition (start, manifest accepted, key granted, each chunk verified,
    /// completion, trust denial) with an immutable snapshot of current progress. Purely observational —
    /// subscribing or not changes nothing about the transfer. Handlers run synchronously on the receive
    /// loop's thread; a UI should marshal to its own dispatcher and return quickly.
    /// </summary>
    public event Action<TransferProgress>? ProgressChanged;

    /// <summary>Complete only once every chunk is received and verified, the content key has been granted, and all ciphertext has been decrypted and written.</summary>
    public bool IsComplete =>
        Manifest is not null
        && _contentKey is not null
        && _bitmaps.Count == Manifest.Manifest.Files.Count
        && _bitmaps.Values.All(b => b.IsComplete)
        && _pendingDecrypt.Count == 0;

    /// <summary>Processes packets until the transfer completes or cancellation is requested.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        EmitProgress();

        // Peer chunk relays run on their own task for the whole life of the receive, so an evicted chunk's disk
        // read can never stall packet processing (see _serveChannel). Started here rather than in the constructor
        // so a session that is never run starts no background work.
        var serveWorker = RunChunkServeWorkerAsync(cancellationToken);
        try
        {
            await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Let the worker finish whatever is already queued — including a request that arrived in the same
            // packet as the final chunk — then surface any fault it hit rather than swallowing it (M6 round 3).
            _serveChannel.Writer.TryComplete();
            try { await serveWorker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var packet in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            // Reassemble MTU-safe wire packets back into a whole message before decoding; a chunk that is
            // still missing fragments simply never surfaces here and stays "not received" until repair.
            var payload = _reassembler.Offer(packet.Payload);
            if (payload is null)
                continue;

            bool complete;
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await HandlePacketAsync(payload, cancellationToken).ConfigureAwait(false);
                complete = IsComplete;
            }
            finally
            {
                _stateGate.Release();
            }

            // Deliberately after the gate is released — but note the honest accounting: **coalescing is the win,
            // and this queue is the small remainder.** Before coalescing, holding _stateGate across an awaited
            // PEER_HAVE send did serialize all packet processing behind the network, once per verified chunk. With
            // coalescing landing in the same change there are only ~4 such sends per second, so moving them off
            // the gate saves on the order of 4 x 97 us/s — real, but not the effect. The measurements agree:
            // coalescing plus un-gating together measured +9%, while coalescing alone (PEER_HAVE every 64th) was
            // independently measured at +22% on another rig, i.e. coalescing accounts for the whole effect within
            // noise. Kept because it is nearly free and removes an unbounded-latency dependency from the gate, not
            // because it is where the throughput came from.
            //
            // The bitmap bytes were already snapshotted under the gate (see QueuePeerHave), so nothing here reads
            // shared mutable state. Run before the completion return, so the final "I have the whole file"
            // announcement still goes out.
            await DrainPendingPeerHaveAsync(cancellationToken).ConfigureAwait(false);

            if (complete)
                return;
        }
    }

    /// <summary>Encodes a wire message and sends it as one or more MTU-safe datagrams (see <see cref="WirePacketizer"/>).</summary>
    private async Task SendMessageAsync(object message, CancellationToken ct)
    {
        foreach (var datagram in WirePacketizer.Fragment(MessageCodec.Encode(message), _maxDatagramPayloadBytes))
            await _transport.SendAsync(datagram, ct).ConfigureAwait(false);
    }

    /// <summary>Computes and sends CHUNK_REQUEST for any file's currently-missing chunks. Call this periodically (e.g. from a stall timer) while <see cref="RunAsync"/> is also running.</summary>
    public async Task RequestRepairsAsync(CancellationToken cancellationToken)
    {
        if (Manifest is null)
            return;

        // Held for the whole pass so it never observes _bitmaps/_contentKey/RepairCoordinator mid-mutation by
        // the concurrent RunAsync packet loop (see _stateGate).
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-drive the content-key handshake until it succeeds: a single KEY_GRANT can be lost (it rides the
            // lossy multicast data plane), and without the content key no ciphertext can be decrypted. Each retry
            // prompts the sender to re-wrap and re-send. Cheap, and it reuses the caller's existing repair timer.
            if (_contentKey is null)
            {
                var join = new JoinRequestMessage(Manifest.Manifest.SessionId, _receiverId, _encryptionPublicKey);
                await SendMessageAsync(join, cancellationToken).ConfigureAwait(false);
            }

            var now = _clock.UtcNow;
            int fileCount = Manifest.Manifest.Files.Count;

            // Hard bound on how much repair traffic one pass may emit. The per-request cap
            // (RepairOptions.MaxChunksPerRequest) only bounds an individual datagram's size; on its own it lets a
            // cold-start pass emit ~38 single-datagram requests and the sender still serve every chunk in them, so
            // it fixes the all-or-nothing fragmentation hazard but is NOT an amplification bound. This is: it caps
            // requests per pass, which paces emission, bounds the damage of any false-idle in the carousel valve
            // (so that valve stops being load-bearing for amplification), bounds a hostile receiver's outbound
            // burst, and — because PlanRepairs also stops collecting candidates once the budget is covered —
            // bounds the per-index peer-table work done under _stateGate. See RepairOptions.MaxRequestsPerPass
            // for the derivation of 4 and for why it is deliberately loose relative to the wire rate.
            int requestBudget = _repairOptions.MaxRequestsPerPass;

            // Round-robin the starting file so a low-numbered file with many gaps cannot permanently consume the
            // whole budget and starve a later file's repairs.
            int startFile = fileCount == 0 ? 0 : _repairPassCursor % fileCount;
            _repairPassCursor = fileCount == 0 ? 0 : (_repairPassCursor + 1) % fileCount;

            for (int offset = 0; offset < fileCount && requestBudget > 0; offset++)
            {
                int fileIndex = (startFile + offset) % fileCount;
                var bitmap = _bitmaps[fileIndex];
                if (bitmap.IsComplete)
                    continue;

                // The valve: "this file's carousel is done or stalled", at which point its watermark stops gating
                // and every missing index becomes eligible. Without it, a file whose LAST chunks were lost would
                // never see its watermark rise past them and would hang forever.
                //
                // Two states must be kept apart here, because at seed time "never reached" and "finished" look
                // identical and conflating them has now caused a bug in each direction: round 1 refreshed one
                // global timer from any traffic and HUNG a started file; round 2 seeded every file's timer at
                // manifest time and OVER-REQUESTED files the sequential carousel had not begun yet. So a file's
                // valve may open only once that file is known to have been transmitted:
                //
                //   started            — we have seen at least one of its chunks, so its carousel began; its own
                //                        idle timer is meaningful from that point on (this is the tail-loss case)
                //   carouselMovedPast  — a LATER file has started. RunChunkCarouselAsync sends files strictly in
                //                        index order, so that is proof this file's carousel already ran to
                //                        completion and every chunk of it was lost. Exact rather than heuristic,
                //                        but on TWO preconditions, not one: (a) the sender transmits files in
                //                        index order — load-bearing, and flagged as such at RunChunkCarouselAsync;
                //                        and (b) the later-file arrival that set _highestChunkIndexSeen actually
                //                        came from that carousel. (b) can fail, because _highestChunkIndexSeen is
                //                        lifted unconditionally (see ObserveChunkActivity), CHUNK_RESPONSE
                //                        included. Within a conforming group it collapses: reaching that state for
                //                        the highest file requires sessionQuiet, during which this file's own
                //                        fileQuiet already holds, so the outcome is unchanged. A non-conforming or
                //                        older-build peer requesting file j early, though, makes the sender
                //                        multicast j's chunks and opens every conforming receiver's file-i valve.
                //                        Deliberately not fixed: bounded amplification, never a liveness failure,
                //                        same family and same MaxRequestsPerPass bound as the sparse-repair
                //                        watermark inflation documented on ObserveChunkActivity.
                //   sessionQuiet       — no file's watermark has advanced for a whole threshold. The escape hatch
                //                        for the one case neither per-file signal can see: the LAST (or only)
                //                        file, with every chunk lost, so nothing ever starts. Refreshed only by
                //                        genuine carousel advancement, so repair traffic cannot hold it shut.
                bool started = _highestChunkIndexSeen.ContainsKey(fileIndex);
                bool carouselMovedPast = started || AnyLaterFileStarted(fileIndex);
                bool sessionQuiet = !_lastCarouselAdvanceAt.HasValue
                    || now - _lastCarouselAdvanceAt.Value >= _carouselIdleThreshold;
                bool fileQuiet = !_carouselAdvancedAt.TryGetValue(fileIndex, out var advancedAt)
                    || now - advancedAt >= _carouselIdleThreshold;

                bool carouselIdle = (carouselMovedPast || sessionQuiet) && fileQuiet;

                // Only ask for chunks the sender has demonstrably already transmitted: requesting chunks the
                // carousel simply has not reached yet is what turned the first repair pass into a request for
                // ~97% of the file and made the sender re-serve the whole transfer alongside its own
                // still-running carousel.
                int watermark = _highestChunkIndexSeen.TryGetValue(fileIndex, out var highest) ? highest : -1;
                var missing = carouselIdle
                    ? bitmap.MissingIndices().ToList()
                    : bitmap.MissingIndices().Where(i => i <= watermark).ToList();
                if (missing.Count == 0)
                    continue;

                var senderEndpoint = new Endpoint("sender", 0); // multicast-only MVP: Target is informational, delivery is always multicast
                var plans = _repairCoordinator.PlanRepairs(fileIndex, missing, senderEndpoint, NewNonce, requestBudget);

                foreach (var plan in plans)
                {
                    var request = new ChunkRequestMessage(
                        Manifest.Manifest.SessionId, _receiverId, plan.RequestNonce, fileIndex, plan.ChunkIndices, "", 0);
                    await SendMessageAsync(request, cancellationToken).ConfigureAwait(false);
                    // Only now are these chunks in flight. Marking them in PlanRepairs (as this used to) meant a
                    // request that faulted before reaching the socket — or was lost as one of ~35 all-or-nothing
                    // fragments — still suppressed any retry for a full RequestTimeout, which is precisely why the
                    // observed stall quantum was ~5 s rather than the 250 ms repair period.
                    _repairCoordinator.MarkRequested(fileIndex, plan.ChunkIndices, now);
                    requestBudget--;
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task HandlePacketAsync(byte[] payload, CancellationToken ct)
    {
        object? decoded;
        try { decoded = MessageCodec.Decode(payload); }
        catch { return; }

        switch (decoded)
        {
            case ManifestMessage manifestMessage:
                await HandleManifestAsync(manifestMessage, ct).ConfigureAwait(false);
                break;
            case KeyGrantMessage keyGrant:
                await HandleKeyGrantAsync(keyGrant, ct).ConfigureAwait(false);
                break;
            case ChunkDataMessage chunkData:
                // CHUNK_DATA only ever comes from the sender's sequential carousel, so it is the one delivery
                // that carries real evidence about carousel position (see ObserveChunkActivity).
                await HandleChunkAsync(chunkData.SessionId, chunkData.FileIndex, chunkData.ChunkIndex, chunkData.Payload, chunkData.Proof, fromCarousel: true, ct).ConfigureAwait(false);
                break;
            case ChunkResponseMessage chunkResponse:
                // CHUNK_RESPONSE is by definition an answer to somebody's repair request — never evidence of
                // carousel progress, and multicast to everyone, so it must not refresh any receiver's valve.
                await HandleChunkAsync(chunkResponse.SessionId, chunkResponse.FileIndex, chunkResponse.ChunkIndex, chunkResponse.Payload, chunkResponse.Proof, fromCarousel: false, ct).ConfigureAwait(false);
                break;
            case ChunkPacketMessage chunkPacket:
                await HandleChunkPacketAsync(chunkPacket, ct).ConfigureAwait(false);
                break;
            case PeerHaveMessage peerHave:
                if (!peerHave.ReceiverId.AsSpan().SequenceEqual(_receiverId))
                    _peerTable.Observe(peerHave, _clock.UtcNow);
                break;
            case ChunkRequestMessage chunkRequest:
                // Planning only — the read/rebuild/send happens in DrainPendingChunkServesAsync, off the gate.
                HandleChunkRequest(chunkRequest);
                break;
            // AnnounceMessage: nothing to do in this MVP — trust and initialization both happen on MANIFEST.
            // JoinRequestMessage: a peer's request to the sender — not our concern.
        }
    }

    private async Task HandleManifestAsync(ManifestMessage message, CancellationToken ct)
    {
        if (Manifest is not null)
            return; // one-shot session: first accepted manifest wins

        // Signature verification + TOFU trust flow — the exact same admission gate the unicast-swarm
        // SwarmPullSession runs (see Castr.Core.Trust.ManifestAdmission), extracted so there is one copy.
        var admission = await ManifestAdmission.EvaluateAsync(
            message.SignedManifest, _trustStore, _clock, _options.UnknownSenderPolicy, _options.IsInteractive, _trustPrompt, ct,
            _options.SessionRegistry)
            .ConfigureAwait(false);

        if (admission.Outcome == ManifestAdmissionOutcome.SignatureInvalid)
            return; // invalid signature — forged or corrupt, reject outright (no trust event, unchanged behavior)

        if (admission.Outcome == ManifestAdmissionOutcome.SessionIdConflict)
            return; // this session id already means a different transfer — drop, exactly like a bad signature

        if (admission.Outcome == ManifestAdmissionOutcome.Denied)
        {
            EmitProgress(TransferPhase.TrustDenied);
            SenderTrustDenied?.Invoke(admission.Decision!, message.SignedManifest.SenderId);
            return;
        }

        Manifest = message.SignedManifest;
        int maxChunkSize = 0;
        for (int fileIndex = 0; fileIndex < Manifest.Manifest.Files.Count; fileIndex++)
        {
            var file = Manifest.Manifest.Files[fileIndex];
            _bitmaps[fileIndex] = new ChunkBitmap(file.ChunkCount);
            maxChunkSize = Math.Max(maxChunkSize, file.ChunkSize);
            var destination = PathSafety.ResolveDestination(_options.DestinationRoot, file.RelativePath);
            _sinks[fileIndex] = _sinkFactory(destination, file.Size);
        }

        // Now that the trusted manifest tells us the largest legitimate chunk for this transfer, bound the chunk
        // reassembler to that chunk size (+ AEAD tag) so a crafted ChunkPacket can never claim more ciphertext —
        // or more packets — than a real chunk of this transfer would. This is manifest-derived, so it tracks the
        // default chunk size automatically and needed no change when that default went 8 KiB -> 256 KiB (M8).
        //
        // KNOWN GAP found while re-validating that at M8, pre-existing and NOT introduced by the chunk-size
        // change: ManifestFileEntry.ChunkSize is never range-checked, here or in ManifestCodec.Decode. It is
        // covered by the sender's Ed25519 signature, so reaching this requires a *trusted* sender, which is why
        // this is a robustness gap rather than a remote-attacker hole. But a trusted sender that is buggy or
        // compromised gets two things: a ChunkSize near int.MaxValue makes CiphertextBoundForChunkSize overflow
        // to a negative bound and the ChunkPacketAssembler constructor throw ArgumentOutOfRangeException straight
        // out of the receive loop (HandlePacketAsync is not wrapped — only MessageCodec.Decode is), and any large
        // -but-not-overflowing value re-opens the very allocation ceiling this line exists to close. The fix is a
        // range check at manifest admission, which is a change to what manifests are *accepted* and so wants its
        // own review rather than riding along with a default-value change. Tracked in wiki/synthesis/roadmap.md.
        //
        // minFragmentBytes comes from THIS session's datagram budget rather than the shipped default, so a
        // session running on a non-default budget gets a packet-count bound derived from its own arithmetic.
        _chunkAssembler = new ChunkPacketAssembler(
            ChunkPacketAssembler.CiphertextBoundForChunkSize(maxChunkSize),
            minFragmentBytes: ChunkPacketAssembler.MinFragmentBytesFor(_maxDatagramPayloadBytes));

        // Start only the SESSION-level clock here. Deliberately NOT a per-file clock: the sender carousels files
        // sequentially, so seeding every file's timer at manifest time made file 1's valve open one threshold
        // after the manifest — before its carousel had even begun — and its whole chunk set became repair-
        // eligible while file 0 was still transmitting. That is the premature-repair storm re-created for every
        // file after the first. A file's own timer now starts when that file's carousel demonstrably starts (see
        // ObserveChunkActivity); until then the file is "not started", which is a different state from "started
        // and quiet" and must not be conflated with it. See CarouselIdleThreshold.
        _lastCarouselAdvanceAt = _clock.UtcNow;

        EmitProgress();

        // Now that the sender's manifest is trusted, request the per-transfer content key (ADR-0003).
        var join = new JoinRequestMessage(Manifest.Manifest.SessionId, _receiverId, _encryptionPublicKey);
        await SendMessageAsync(join, ct).ConfigureAwait(false);
    }

    private async Task HandleKeyGrantAsync(KeyGrantMessage grant, CancellationToken ct)
    {
        if (Manifest is null || _contentKey is not null)
            return;
        if (!grant.SessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId))
            return;
        if (!grant.ReceiverId.AsSpan().SequenceEqual(_receiverId))
            return; // addressed to a different receiver

        var raw = ContentKeyWrap.TryUnwrap(
            _encryptionKey, Manifest.Manifest.SenderEncryptionPublicKey,
            Manifest.Manifest.SessionId, _receiverId, grant.WrappedContentKey);
        if (raw is null)
            return; // could not unwrap (spoofed grant or mismatched key) — ignore

        _contentKey = ContentKey.Import(raw);

        // Drain any ciphertext chunks that arrived (and were verified) before the key did. Every one of them is
        // pinned in the cache (pinning is exactly "not yet written to disk"), so the lookup cannot miss; the
        // TryGetValue is defensive rather than expected, and skipping is the only safe response if it ever fails.
        foreach (var key in _pendingDecrypt.ToList())
        {
            if (_cachedCiphertext.TryGetValue(key, out var node))
                await DecryptWriteAndTrackAsync(key.File, key.Chunk, node.Value.Ciphertext, ct).ConfigureAwait(false);
        }

        EmitProgress();
    }

    /// <summary>
    /// Buffers one wire packet of a large chunk (see <see cref="ChunkPacketAssembler"/>). Once every packet of
    /// the chunk has arrived — accumulated across the carousel and any repair rounds — the reassembled
    /// ciphertext is fed through the very same verify/decrypt/write path as a chunk that arrived whole.
    /// </summary>
    private async Task HandleChunkPacketAsync(ChunkPacketMessage packet, CancellationToken ct)
    {
        if (Manifest is null || !packet.SessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId))
            return;
        // Validate the wire-supplied file/chunk indices before indexing anything: an out-of-range ChunkIndex would
        // otherwise throw ArgumentOutOfRangeException out of ChunkBitmap.Get and fault the whole receive loop.
        if (!_bitmaps.TryGetValue(packet.FileIndex, out var bitmap)
            || packet.ChunkIndex < 0 || packet.ChunkIndex >= bitmap.ChunkCount)
            return; // unknown file or out-of-range chunk — drop stray/bad packets

        // Observed after the range check (so a crafted index cannot inflate the watermark past the file) but
        // before the have-it-already check: a duplicate still proves the carousel reached this index.
        // fromCarousel: true because a ChunkPacket's origin is deliberately unknowable — see
        // ObserveChunkActivity's remarks for why that is safe.
        ObserveChunkActivity(packet.FileIndex, packet.ChunkIndex, fromCarousel: true);

        if (bitmap.Get(packet.ChunkIndex))
            return; // one we already have — drop the duplicate

        var assembled = _chunkAssembler.Offer(packet);
        if (assembled is not { } complete)
            return; // still missing packets — the chunk stays "not received" until repair fills the gaps

        _chunkAssembler.Forget(packet.FileIndex, packet.ChunkIndex);
        await HandleChunkAsync(packet.SessionId, packet.FileIndex, packet.ChunkIndex, complete.Ciphertext, complete.Proof, fromCarousel: true, ct).ConfigureAwait(false);
    }

    private async Task HandleChunkAsync(
        byte[] sessionId, int fileIndex, int chunkIndex, byte[] ciphertext, MerkleProof proof, bool fromCarousel, CancellationToken ct)
    {
        if (Manifest is null || !sessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId))
            return;
        // Same guard for the whole-chunk paths (ChunkData / ChunkResponse): a wire-supplied chunkIndex out of the
        // file's range must be dropped, not passed to ChunkBitmap.Get where it would throw and fault the loop.
        if (!_bitmaps.TryGetValue(fileIndex, out var bitmap)
            || chunkIndex < 0 || chunkIndex >= bitmap.ChunkCount)
            return;

        // Same watermark bookkeeping as HandleChunkPacketAsync: after the range check, before the duplicate
        // check. (A packetized chunk passes through both; the second observation is inert, since by then the
        // index is no longer above the watermark.)
        ObserveChunkActivity(fileIndex, chunkIndex, fromCarousel);

        if (bitmap.Get(chunkIndex))
            return;

        // Bind the proof's committed leaf position to the claimed chunk index: a relaying peer must not be able
        // to pass off chunk A's (valid) ciphertext+proof as chunk B. Merkle verification alone recomputes the
        // root from any real leaf and would accept it regardless of the claimed index; the AEAD AAD would later
        // reject the mismatch on decrypt, but only after the bitmap below was already set — permanently
        // stranding that position (marked "have", never actually written, never re-requested). Rejecting here
        // keeps a swapped chunk from ever being marked "have" in the first place.
        if (proof.LeafIndex != chunkIndex)
            return;

        var file = Manifest.Manifest.Files[fileIndex];
        var ciphertextHash = ChunkHash.Compute(ciphertext);
        if (!ManifestVerifier.VerifyChunk(file.MerkleRoot, ciphertextHash, proof))
            return; // corrupt or spoofed ciphertext — silently drop; repair will re-request it from someone else

        bitmap.Set(chunkIndex);
        _verifiedBytes += ChunkLayout.GetRange(file.Size, file.ChunkSize, chunkIndex).Length;
        _repairCoordinator.MarkFulfilled(fileIndex, chunkIndex);
        _pendingDecrypt.Add((fileIndex, chunkIndex));

        // Cached as ciphertext, for peer repair relay. The proof is kept for the session; the ciphertext enters a
        // byte-bounded LRU. Added AFTER _pendingDecrypt so eviction can see that this chunk is not yet on disk
        // and must not be dropped (see AdmitToCache / EvictDownToBudget).
        AdmitToCache(fileIndex, chunkIndex, ciphertext, proof);

        // Decrypt-and-write now if we already hold the content key; otherwise it stays pending until KEY_GRANT.
        if (_contentKey is not null)
            await DecryptWriteAndTrackAsync(fileIndex, chunkIndex, ciphertext, ct).ConfigureAwait(false);

        QueuePeerHave(fileIndex);

        EmitProgress();
    }

    private async Task DecryptWriteAndTrackAsync(int fileIndex, int chunkIndex, byte[] ciphertext, CancellationToken ct)
    {
        var plaintext = _contentKey!.TryDecryptChunk(Manifest!.Manifest.SessionId, fileIndex, chunkIndex, ciphertext);
        if (plaintext is null)
            return; // Merkle-valid ciphertext that fails AEAD should not happen with the right key; leave pending.

        var file = Manifest.Manifest.Files[fileIndex];
        var range = ChunkLayout.GetRange(file.Size, file.ChunkSize, chunkIndex);
        await _sinks[fileIndex].WriteAsync(range.Offset, plaintext, ct).ConfigureAwait(false);
        // Only now is this chunk's plaintext durable, which is exactly what makes its ciphertext evictable: the
        // cold path can re-read and re-encrypt it. Until this line runs the entry is pinned in the cache.
        _pendingDecrypt.Remove((fileIndex, chunkIndex));
        EvictDownToBudget();

        if (_bitmaps[fileIndex].IsComplete
            && !_pendingDecrypt.Any(k => k.File == fileIndex)
            && _sinks[fileIndex] is FileSystemFileSink diskSink)
        {
            diskSink.Complete();
        }
    }

    /// <summary>
    /// Decides, under <see cref="_stateGate"/>, which of a peer's requested chunks this receiver will relay, and
    /// queues them for <see cref="DrainPendingChunkServesAsync"/> to actually read/rebuild/send once the gate has
    /// been released. Does no I/O and no cryptography — that is the entire point (see
    /// <see cref="_pendingChunkServes"/>).
    /// </summary>
    private void HandleChunkRequest(ChunkRequestMessage request)
    {
        if (Manifest is null || !request.SessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId))
            return;
        if (request.RequesterId.AsSpan().SequenceEqual(_receiverId))
            return; // don't answer our own broadcast request

        // Bound how much one inbound request can make this receiver do, for exactly the reason SenderSession's
        // own copy of this cap exists — and more sharply, because a receiver is simultaneously trying to process
        // its OWN inbound chunk stream. A peer that sends a fragmented 5,000-index request (hostile, or simply
        // built by a pre-cap version of this code) would otherwise have a receiver re-serving 5,000 chunks.
        // Overflow is dropped rather than queued: the requester's own repair timer re-asks for whatever is still
        // missing, so nothing is lost. This also bounds _pendingChunkServes, which is drained per packet.
        var servedIndices = request.ChunkIndices.Length <= _maxChunksServedPerRequest
            ? request.ChunkIndices
            : request.ChunkIndices[.._maxChunksServedPerRequest];

        foreach (var chunkIndex in servedIndices)
        {
            var planned = PlanChunkServe(request.SessionId, request.RequestNonce, request.FileIndex, chunkIndex);
            // TryWrite never blocks: the channel is bounded with DropWrite, so a flood is shed here rather than
            // queued, and the requester's own repair timer re-asks elsewhere.
            if (planned is not null)
                _serveChannel.Writer.TryWrite(planned);
        }
    }

    /// <summary>
    /// Reads, rebuilds and sends every chunk <see cref="HandleChunkRequest"/> queued, on its own task for the
    /// life of the receive. Shares nothing with the receive loop: every input was snapshotted under
    /// <see cref="_stateGate"/> (see <see cref="ColdRebuild"/>), and the only state it touches afterwards is the
    /// transport and the <see cref="ChunkServeOutcome"/> counters, both of which are safe concurrently.
    ///
    /// <para>This is where an evicted chunk's disk read, re-encryption and re-verification happen. Running them
    /// anywhere on the receive path — under the gate, or merely after releasing it — let one unauthenticated
    /// CHUNK_REQUEST stall packet processing for hundreds of milliseconds warm and seconds cold, long enough to
    /// trip <see cref="CarouselIdleThreshold"/> and re-open the M7 repair storm. See <see cref="_serveChannel"/>
    /// for the measured figures and for why the post-gate variant was not sufficient.</para>
    ///
    /// <para>Exceptions are deliberately <b>not</b> swallowed — they surface where <see cref="RunAsync"/> joins
    /// this task. A silently-dying background loop is the exact defect M6 round 3 found in the receive path.</para>
    /// </summary>
    private async Task RunChunkServeWorkerAsync(CancellationToken ct)
    {
        await foreach (var serve in _serveChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var ciphertext = serve.Ciphertext;
            if (ciphertext is null)
            {
                ciphertext = await RebuildCiphertextAsync(serve, ct).ConfigureAwait(false);
                if (ciphertext is null)
                    continue; // decline — already counted inside RebuildCiphertextAsync
                Count(ChunkServeOutcome.ServedByRebuild, serve.FileIndex, serve.ChunkIndex);
            }
            else
            {
                Count(ChunkServeOutcome.ServedFromCache, serve.FileIndex, serve.ChunkIndex);
            }

            // Relay large chunks as identity-keyed wire packets (byte-identical to the sender's), so a
            // requester accumulates them across sources/rounds; small chunks go as a whole ChunkResponse.
            if (ChunkPacketizer.RequiresPacketization(ciphertext.Length, serve.Proof, _maxDatagramPayloadBytes))
            {
                foreach (var packet in ChunkPacketizer.Split(
                    serve.SessionId, serve.FileIndex, serve.ChunkIndex, ciphertext, serve.Proof, _maxDatagramPayloadBytes))
                {
                    await SendMessageAsync(packet, ct).ConfigureAwait(false);
                }
                continue;
            }

            var response = new ChunkResponseMessage(
                serve.SessionId, serve.RequestNonce, serve.FileIndex, serve.ChunkIndex, ciphertext, serve.Proof);
            await SendMessageAsync(response, ct).ConfigureAwait(false);
        }
    }

    // ---- Verified-chunk cache ----

    /// <summary>
    /// Total bytes of verified chunk ciphertext currently retained. Bounded by
    /// <see cref="ReceiverSessionOptions.ChunkCacheBytes"/> except while chunks are pinned awaiting a KEY_GRANT
    /// (see <see cref="EvictDownToBudget"/>). Exposed for tests and diagnostics; reading it does not take
    /// <see cref="_stateGate"/>, so treat it as a sample rather than a synchronized snapshot.
    /// </summary>
    public long CachedCiphertextBytes => Interlocked.Read(ref _cachedBytes);

    /// <summary>Number of chunks whose ciphertext is currently resident. Diagnostics only; see the caveat above.</summary>
    public int CachedChunkCount => _cachedCiphertext.Count;

    /// <summary>
    /// Records a freshly verified chunk: the proof for the session, the ciphertext into the LRU, then evicts
    /// down to budget. Called under <see cref="_stateGate"/>.
    /// </summary>
    private void AdmitToCache(int fileIndex, int chunkIndex, byte[] ciphertext, MerkleProof proof)
    {
        var key = (fileIndex, chunkIndex);
        _chunkProofs[key] = proof;

        if (_cachedCiphertext.ContainsKey(key))
            return; // already resident (this path is reached once per chunk, but stay idempotent)

        var node = _lru.AddFirst(new CachedChunk(key, ciphertext));
        _cachedCiphertext[key] = node;
        Interlocked.Add(ref _cachedBytes, ciphertext.Length);
        EvictDownToBudget();
    }

    /// <summary>
    /// Drops least-recently-used ciphertext until the cache is within budget, skipping chunks that are still
    /// awaiting decryption. Called under <see cref="_stateGate"/>.
    ///
    /// <para><b>Pinning is a correctness requirement, not a nicety.</b> A chunk verified before the KEY_GRANT
    /// arrives has no plaintext on disk yet, so its ciphertext is the only copy in existence and the cold path
    /// cannot rebuild it. Evicting one would strand that chunk permanently: the bitmap already says "have", so
    /// repair will never re-request it, and it would never be written. So a pinned entry is skipped even when
    /// that means exceeding the budget — the overshoot is bounded by how much arrives inside the
    /// JOIN_REQUEST/KEY_GRANT round trip, which is a startup window, whereas stranding a chunk is permanent.</para>
    /// </summary>
    private void EvictDownToBudget()
    {
        var node = _lru.Last;
        while (node is not null && Interlocked.Read(ref _cachedBytes) > _chunkCacheBytes)
        {
            var previous = node.Previous;
            if (!_pendingDecrypt.Contains(node.Value.Key))
            {
                _lru.Remove(node);
                _cachedCiphertext.Remove(node.Value.Key);
                Interlocked.Add(ref _cachedBytes, -node.Value.Ciphertext.Length);
            }
            node = previous;
        }
    }

    /// <summary>
    /// Decides whether a requested chunk can be relayed and, if so, returns everything needed to relay it —
    /// either the cached ciphertext outright, or the snapshot a cold rebuild will need. Returns null (having
    /// counted the reason) when this receiver cannot serve it at all. <b>Called under
    /// <see cref="_stateGate"/>; does no I/O and no cryptography.</b>
    ///
    /// <para>On a cache hit the entry is promoted to most-recently-used. On a miss the caller reconstructs the
    /// ciphertext off-gate: the plaintext this receiver already wrote is read back from the sink and
    /// re-encrypted. That reproduces the evicted bytes <b>exactly</b>, because
    /// <see cref="ContentKey.EncryptChunk"/> derives its ChaCha20-Poly1305 nonce from
    /// <c>(fileIndex, chunkIndex)</c> and its AAD from <c>(sessionId, fileIndex, chunkIndex)</c> and adds no
    /// randomness anywhere — the same property <c>SenderSession</c> has relied on since M1 to re-serve a chunk
    /// it never cached.</para>
    ///
    /// <para>A reconstructed chunk is deliberately <b>not</b> re-admitted to the LRU. A bulk cold-start request
    /// can name up to <c>MaxChunksPerRequest</c> (336) indices — 84 MiB at the default chunk size — and
    /// admitting those would flush the recent-chunk working set that the rest of the swarm is actually asking
    /// for, converting one bulk request into a cache-wide miss storm.</para>
    /// </summary>
    private PendingChunkServe? PlanChunkServe(byte[] sessionId, byte[] requestNonce, int fileIndex, int chunkIndex)
    {
        var key = (fileIndex, chunkIndex);
        if (!_chunkProofs.TryGetValue(key, out var proof))
        {
            Count(ChunkServeOutcome.DeclinedNotVerified, fileIndex, chunkIndex);
            return null; // never verified here — let someone else (or the sender) answer
        }

        if (_cachedCiphertext.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            // The cached array is never mutated after admission, so handing the reference off-gate is safe.
            return new PendingChunkServe(sessionId, requestNonce, fileIndex, chunkIndex, proof, node.Value.Ciphertext, null);
        }

        // Evicted. Snapshot what a rebuild needs, if all of it is available.
        if (_contentKey is null)
        {
            Count(ChunkServeOutcome.DeclinedNoContentKey, fileIndex, chunkIndex);
            return null;
        }
        if (_pendingDecrypt.Contains(key))
        {
            Count(ChunkServeOutcome.DeclinedAwaitingDecrypt, fileIndex, chunkIndex);
            return null; // the plaintext was never written — nothing to read back
        }
        if (Manifest is null || !_sinks.TryGetValue(fileIndex, out var sink) || sink is not IReadableFileSink readable)
        {
            Count(ChunkServeOutcome.DeclinedSinkNotReadable, fileIndex, chunkIndex);
            return null; // a write-only sink simply cannot serve evicted chunks; degrade quietly
        }

        return new PendingChunkServe(
            sessionId, requestNonce, fileIndex, chunkIndex, proof,
            Ciphertext: null,
            new ColdRebuild(Manifest.Manifest.Files[fileIndex], readable, _contentKey));
    }

    /// <summary>
    /// The cold path: reads a chunk's plaintext back from the destination and re-encrypts it, reproducing the
    /// evicted ciphertext byte for byte. <b>Runs outside <see cref="_stateGate"/></b> — every input was
    /// snapshotted under it (see <see cref="ColdRebuild"/>). Returns null, having counted the reason, if the
    /// chunk cannot be rebuilt.
    ///
    /// <para>The result is re-verified against the signed Merkle root before it is handed out. That is cheap
    /// next to the disk read, and it means a receiver whose destination file was modified underneath it declines
    /// to serve rather than relaying bytes a peer would have to reject. Determinism of
    /// <see cref="ContentKey.EncryptChunk"/> itself is proven by <c>ContentKeyDeterminismTests</c>.</para>
    /// </summary>
    private async ValueTask<byte[]?> RebuildCiphertextAsync(PendingChunkServe serve, CancellationToken ct)
    {
        var rebuild = serve.Rebuild!;
        var file = rebuild.File;
        var range = ChunkLayout.GetRange(file.Size, file.ChunkSize, serve.ChunkIndex);
        var plaintext = new byte[range.Length];

        int read;
        try
        {
            read = await rebuild.Sink.ReadAsync(range.Offset, plaintext, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Includes the one real off-gate race: the gated path calling Complete() on this sink mid-read.
            // Failing closed here is the correct resolution — the requester falls back to another source.
            Count(ChunkServeOutcome.DeclinedReadFailed, serve.FileIndex, serve.ChunkIndex);
            return null;
        }

        if (read != range.Length)
        {
            Count(ChunkServeOutcome.DeclinedReadFailed, serve.FileIndex, serve.ChunkIndex);
            return null;
        }

        var ciphertext = rebuild.Key.EncryptChunk(serve.SessionId, serve.FileIndex, serve.ChunkIndex, plaintext);

        if (!ManifestVerifier.VerifyChunk(file.MerkleRoot, ChunkHash.Compute(ciphertext), serve.Proof))
        {
            Count(ChunkServeOutcome.DeclinedReverifyFailed, serve.FileIndex, serve.ChunkIndex);
            return null;
        }

        return ciphertext;
    }

    /// <summary>
    /// How many times this receiver has reached <paramref name="outcome"/> while answering (or declining to
    /// answer) a peer's chunk request. See <see cref="ChunkServeOutcome"/> for why these are worth counting.
    /// </summary>
    public long ChunkServeCount(ChunkServeOutcome outcome) => Interlocked.Read(ref _serveCounters[(int)outcome]);

    /// <summary>
    /// Raised for every chunk-serve decision, with the file and chunk index it concerned. Purely observational.
    /// Handlers run on the receive loop's thread (or, for the unicast swarm path, the caller's) and should return
    /// quickly.
    /// </summary>
    public event Action<ChunkServeOutcome, int, int>? ChunkServeObserved;

    private void Count(ChunkServeOutcome outcome, int fileIndex, int chunkIndex)
    {
        Interlocked.Increment(ref _serveCounters[(int)outcome]);
        ChunkServeObserved?.Invoke(outcome, fileIndex, chunkIndex);
    }

    /// <summary>
    /// Records that a chunk-bearing packet for <paramref name="fileIndex"/> / <paramref name="chunkIndex"/>
    /// arrived, lifting that file's carousel watermark if this index is the highest seen. Called for verified,
    /// duplicate, and even soon-to-be-rejected chunks alike — arrival is evidence about the <i>sender's</i>
    /// progress, independent of whether this receiver can use the packet. Called under <see cref="_stateGate"/>.
    /// </summary>
    /// <param name="fromCarousel">
    /// True for a delivery that can only have come from the sender's sequential carousel
    /// (<see cref="ChunkDataMessage"/>), false for one that is definitely a reply to somebody's repair request
    /// (<see cref="ChunkResponseMessage"/>). Only a carousel delivery may refresh
    /// <see cref="_carouselAdvancedAt"/>: repair responses are multicast, so letting them refresh the valve is
    /// what allowed one peer's repair traffic (or a replayed chunk) to hold every other receiver's valve shut
    /// forever.
    ///
    /// <para><see cref="ChunkPacketMessage"/> is deliberately byte-identical between a carousel send and a
    /// repair re-send (that is what lets a receiver accumulate a large chunk's packets across rounds), so its
    /// origin is genuinely unknowable and it is passed here as carousel.</para>
    ///
    /// <para><b>Why treating it as carousel cannot cause a hang.</b> The refresh below is gated behind a
    /// <i>strict</i> watermark advance, the watermark is monotone, and it is bounded above by
    /// <c>ChunkCount-1</c> (every caller range-checks the index first). So a file's watermark can advance at most
    /// <c>ChunkCount-1</c> times in the whole session, and each refresh consumes one of those advances. Therefore
    /// either a missing index eventually falls at or below the watermark — in which case it is eligible under the
    /// ordinary rule and the valve is not needed at all — or the watermark stops advancing and the valve opens
    /// after <see cref="CarouselIdleThreshold"/>. <b>The valve cannot be held shut indefinitely, by anyone.</b>
    /// This is a termination argument and holds regardless of who requested what.</para>
    ///
    /// <para><b>Finiteness is not what makes that acceptable — do not read it as "small".</b> The literal
    /// worst-case bound, <c>(ChunkCount-1) x CarouselIdleThreshold</c>, is about 2.8 hours for a 10,240-chunk
    /// transfer at the default 1 s threshold, which would be useless as a guarantee on its own. What actually
    /// makes it benign is that novel-index arrivals track the sender's carousel: the watermark advances because
    /// chunks are being transmitted, so in practice the delay is bounded by <i>carousel duration plus one
    /// threshold</i>, not by the chunk count. The pathological schedule that would approach the literal bound —
    /// one novel index delivered every threshold, forever — is not something a conforming sender produces.</para>
    ///
    /// <para>Note the earlier justification for this — "a repair response can only advance the watermark after
    /// the valve has already opened" — was <i>wrong</i>, and is recorded here so it is not reintroduced. Repair
    /// responses are multicast: the valve that opened may be a <i>different</i> receiver's. A peer false-idling
    /// at a high carousel position makes the sender re-serve high indices, which this receiver then observes and
    /// lifts its own watermark on, with its own valve still shut.</para>
    ///
    /// <para><b>Known, accepted imprecision.</b> Because repair sends are sparse, a single high-index repair
    /// packet lifts this scalar watermark over a range the carousel never actually sent — so
    /// <c>index &lt;= watermark ⇒ has been transmitted</c> does not strictly hold. Reachable only via asymmetric
    /// false-idle across receivers, and bounded by <see cref="RepairOptions.MaxRequestsPerPass"/>. Deliberately
    /// not fixed: at every shipped chunk size <i>every</i> chunk travels as <see cref="ChunkPacketMessage"/> (this
    /// was true at the old 8 KiB default and is more so at 256 KiB, where a chunk is ~312 wire packets), so gating
    /// the watermark lift on <paramref name="fromCarousel"/> would buy nothing. The ambiguity is structural (it is
    /// the same property that makes cross-round accumulation work); the per-pass cap is the right mitigation.</para>
    /// </param>
    /// <summary>
    /// True when any file with a higher index has started arriving. Because <c>RunChunkCarouselAsync</c> sends
    /// files strictly in index order, that is proof <paramref name="fileIndex"/>'s own carousel has already run
    /// to completion — so if we hold none of its chunks, they were all lost rather than not yet sent.
    /// </summary>
    private bool AnyLaterFileStarted(int fileIndex)
    {
        foreach (var seenFileIndex in _highestChunkIndexSeen.Keys)
            if (seenFileIndex > fileIndex)
                return true;
        return false;
    }

    private void ObserveChunkActivity(int fileIndex, int chunkIndex, bool fromCarousel)
    {
        bool firstForFile = !_highestChunkIndexSeen.TryGetValue(fileIndex, out int highest);
        if (!firstForFile && chunkIndex <= highest)
            return; // nothing new about the sender's progress — re-delivery of an index already accounted for

        _highestChunkIndexSeen[fileIndex] = chunkIndex;

        // Start this file's own idle clock on its first observed chunk whatever the origin (so a file that has
        // demonstrably started always has a timer), and refresh it thereafter only on genuine carousel progress.
        //
        // Documented consequence, benign and deliberate — please do not "fix" it: for a file that was 100% lost,
        // the first arrival is necessarily a REPAIR reply, and it lands here as firstForFile, setting the timer
        // and so closing the valve for one threshold. Recovery therefore proceeds in waves — up to
        // MaxRequestsPerPass x MaxChunksPerRequest (1,072 at the defaults) chunks, then a ~1 s pause — rather
        // than continuously. At the 8 KiB default this added roughly 10 s to fully recovering a 10,240-chunk
        // file; at the 256 KiB default (M8) a file under 268 MB is fewer than 1,072 chunks, so a whole-file
        // recovery now fits in a single wave and the pacing never engages at all. That is pacing, not a stall:
        // progress is strictly monotone, every wave is bounded, and spreading a whole-file recovery over
        // seconds is arguably what you want from a receiver that just discovered it holds none of a file.
        if (fromCarousel || firstForFile)
            _carouselAdvancedAt[fileIndex] = _clock.UtcNow;

        if (fromCarousel)
            _lastCarouselAdvanceAt = _clock.UtcNow;
    }

    /// <summary>
    /// Decides whether this file's bitmap is due for a PEER_HAVE broadcast and, if so, snapshots it for
    /// <see cref="DrainPendingPeerHaveAsync"/> to send once <see cref="_stateGate"/> has been released.
    /// Rate-limited to one emission per <see cref="PeerHaveInterval"/> per file, with two exemptions: the first
    /// chunk of a file (early peer discovery) and the bitmap becoming complete (peers should learn about a
    /// complete source promptly). Called under <see cref="_stateGate"/>; does no I/O.
    /// </summary>
    private void QueuePeerHave(int fileIndex)
    {
        var bitmap = _bitmaps[fileIndex];
        var now = _clock.UtcNow;

        // Uses the injected ISystemClock, not DateTime.UtcNow/Stopwatch, so tests can drive the interval with
        // FakeClock. Note a frozen FakeClock therefore emits only the two exempt cases, which is correct
        // behavior for a clock that never advances, not a bug.
        bool everSent = _lastPeerHaveAt.TryGetValue(fileIndex, out var last);
        bool due = !everSent || now - last >= _peerHaveInterval || bitmap.IsComplete;
        if (!due)
            return;

        _lastPeerHaveAt[fileIndex] = now;
        // Snapshot here, inside the gate: ChunkBitmap.ToBytes() clones the backing array, so the bytes queued are
        // a stable point-in-time view that the out-of-gate send can use without touching shared state.
        _pendingPeerHave[fileIndex] = bitmap.ToBytes();
    }

    /// <summary>
    /// Sends any PEER_HAVE snapshots <see cref="QueuePeerHave"/> queued, one per file. Called from
    /// <see cref="RunAsync"/> only, <b>after</b> <see cref="_stateGate"/> is released — that is the entire point
    /// of the queue. Taking no gate is safe precisely because <see cref="_pendingPeerHave"/> is written only from
    /// <see cref="HandleChunkAsync"/>, which is reachable only from <see cref="RunAsync"/>'s own gated path (via
    /// <see cref="HandlePacketAsync"/> / <see cref="HandleChunkPacketAsync"/>): this loop is the sole mutator, so
    /// there is no concurrent writer to race with. <see cref="RequestRepairsAsync"/> never touches it.
    /// </summary>
    private async Task DrainPendingPeerHaveAsync(CancellationToken ct)
    {
        if (_pendingPeerHave.Count == 0)
            return;

        var sessionId = Manifest!.Manifest.SessionId;
        // Materialize before sending so multiple pending files are all drained even though each send awaits.
        var pending = _pendingPeerHave.ToArray();

        foreach (var (fileIndex, bitmapBytes) in pending)
        {
            var message = new PeerHaveMessage(sessionId, _receiverId, fileIndex, bitmapBytes, "", 0);
            await SendMessageAsync(message, ct).ConfigureAwait(false);
            // Removed only once actually sent. Clearing the whole map up front (as this used to) meant a
            // cancellation or a throwing send silently discarded snapshots that _lastPeerHaveAt had already
            // recorded as announced, so the file went quiet for a full interval for no reason.
            _pendingPeerHave.Remove(fileIndex);
        }
    }

    /// <summary>
    /// Exposes this receiver as a read-only <see cref="ISwarmContentSource"/> so a <see cref="SwarmServeListener"/>
    /// can relay the chunks it has already verified to a mobile <see cref="Swarm.SwarmPullSession"/> — the
    /// unicast-swarm equivalent of answering a CHUNK_REQUEST. A receiver serves ciphertext only and cannot grant
    /// the content key (it never holds the sender's X25519 private key), so <c>TryGrantContentKey</c> returns
    /// null. Purely additive and read-only; the multicast receive/repair behavior is untouched.
    /// </summary>
    public ISwarmContentSource CreateSwarmContentSource() => new ReceiverContentSource(this);

    private async ValueTask<SwarmChunk?> GetVerifiedChunkAsync(int fileIndex, int chunkIndex, CancellationToken ct)
    {
        // Read the verified-chunk cache under the same gate the receive loop mutates it with, so peer-serving
        // never races the multicast receive path (the concurrency invariant _stateGate exists for).
        // Same hit-or-reconstruct decision the multicast repair serve uses, so a bounded cache does not silently
        // shrink what the unicast swarm tier can offer a mobile puller either — and, like that path, only the
        // *planning* half runs under the gate. A mobile puller asking for an evicted chunk must not be able to
        // hold this receiver's packet loop across a disk read any more than a multicast peer can.
        PendingChunkServe? planned;
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            planned = PlanChunkServe(Manifest?.Manifest.SessionId ?? [], [], fileIndex, chunkIndex);
        }
        finally
        {
            _stateGate.Release();
        }

        if (planned is not { } serve)
            return null;

        if (serve.Ciphertext is { } cached)
        {
            Count(ChunkServeOutcome.ServedFromCache, fileIndex, chunkIndex);
            return new SwarmChunk(cached, serve.Proof);
        }

        var rebuilt = await RebuildCiphertextAsync(serve, ct).ConfigureAwait(false);
        if (rebuilt is null)
            return null;

        Count(ChunkServeOutcome.ServedByRebuild, fileIndex, chunkIndex);
        return new SwarmChunk(rebuilt, serve.Proof);
    }

    private sealed class ReceiverContentSource(ReceiverSession session) : ISwarmContentSource
    {
        public SignedManifest? Manifest => session.Manifest;

        public ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default) =>
            session.GetVerifiedChunkAsync(fileIndex, chunkIndex, cancellationToken);

        public KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request) => null;
    }

    private static byte[] NewNonce() => Guid.NewGuid().ToByteArray();

    private void EmitProgress() => EmitProgress(CurrentPhase());

    private void EmitProgress(TransferPhase phase)
    {
        var handler = ProgressChanged;
        if (handler is null)
            return;
        handler(BuildProgress(phase));
    }

    private TransferPhase CurrentPhase()
    {
        if (Manifest is null)
            return TransferPhase.Starting;
        if (IsComplete)
            return TransferPhase.Completed;
        return _contentKey is null ? TransferPhase.AwaitingKey : TransferPhase.Transferring;
    }

    private TransferProgress BuildProgress(TransferPhase phase)
    {
        int totalFiles = 0, totalChunks = 0, completedChunks = 0;
        long totalBytes = 0;

        if (Manifest is not null)
        {
            var files = Manifest.Manifest.Files;
            totalFiles = files.Count;
            foreach (var file in files)
            {
                totalChunks += file.ChunkCount;
                totalBytes += file.Size;
            }
            foreach (var bitmap in _bitmaps.Values)
                completedChunks += bitmap.CountSet();
        }

        return new TransferProgress(
            TransferRole.Receiver,
            phase,
            Manifest?.Manifest.TransferName ?? string.Empty,
            totalFiles,
            totalChunks,
            completedChunks,
            totalChunks - completedChunks,
            totalBytes,
            _verifiedBytes,
            _peerTable.All().Count);
    }
}
