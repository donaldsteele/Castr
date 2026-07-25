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
public sealed record ReceiverSessionOptions(
    string DestinationRoot,
    UnknownSenderPolicy UnknownSenderPolicy = UnknownSenderPolicy.Deny,
    bool IsInteractive = false,
    TimeSpan? PeerHaveInterval = null,
    TimeSpan? CarouselIdleThreshold = null);

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
    /// gossip quadratic in file size: <c>FileSize² / (8 · chunkSize²)</c> bytes. At the 8 KiB default that is
    /// ~13.5 MB of pure gossip for an 80 MiB transfer and ~1.3 GB for an 800 MiB one — more than the payload
    /// itself. Worse, the encoded message (~1328 bytes for an 80 MiB transfer) exceeds
    /// <see cref="WirePacketizer.DefaultMaxDatagramPayload"/>, so each one fragments into two or more datagrams,
    /// and per-datagram cost — not bytes — is what both sides are actually bound by.</para>
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
    /// <see cref="RepairOptions.MaxRequestsPerPass"/> now bounds amplification independently, so this threshold
    /// is a latency knob rather than the thing standing between the system and a repair storm.</para>
    /// </summary>
    public static readonly TimeSpan DefaultCarouselIdleThreshold = TimeSpan.FromMilliseconds(1000);

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
    private readonly Dictionary<(int File, int Chunk), (byte[] Ciphertext, MerkleProof Proof)> _chunkCache = [];
    private readonly HashSet<(int File, int Chunk)> _pendingDecrypt = [];

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
        // Bound on chunks this receiver will serve from a single inbound CHUNK_REQUEST when relaying to a peer —
        // see HandleChunkRequestAsync. Derived from this session's own datagram budget, mirroring SenderSession.
        _maxChunksServedPerRequest = RepairOptions.MaxChunksPerRequestFor(maxDatagramPayloadBytes);

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
                await HandleChunkRequestAsync(chunkRequest, ct).ConfigureAwait(false);
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
            message.SignedManifest, _trustStore, _clock, _options.UnknownSenderPolicy, _options.IsInteractive, _trustPrompt, ct)
            .ConfigureAwait(false);

        if (admission.Outcome == ManifestAdmissionOutcome.SignatureInvalid)
            return; // invalid signature — forged or corrupt, reject outright (no trust event, unchanged behavior)

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
        // or more packets — than a real chunk of this transfer would.
        _chunkAssembler = new ChunkPacketAssembler(
            ChunkPacketAssembler.CiphertextBoundForChunkSize(maxChunkSize));

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

        // Drain any ciphertext chunks that arrived (and were verified) before the key did.
        foreach (var key in _pendingDecrypt.ToList())
            await DecryptWriteAndTrackAsync(key.File, key.Chunk, ct).ConfigureAwait(false);

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
        _chunkCache[(fileIndex, chunkIndex)] = (ciphertext, proof); // cached as ciphertext, for peer repair relay
        _repairCoordinator.MarkFulfilled(fileIndex, chunkIndex);
        _pendingDecrypt.Add((fileIndex, chunkIndex));

        // Decrypt-and-write now if we already hold the content key; otherwise it stays pending until KEY_GRANT.
        if (_contentKey is not null)
            await DecryptWriteAndTrackAsync(fileIndex, chunkIndex, ct).ConfigureAwait(false);

        QueuePeerHave(fileIndex);

        EmitProgress();
    }

    private async Task DecryptWriteAndTrackAsync(int fileIndex, int chunkIndex, CancellationToken ct)
    {
        var (ciphertext, _) = _chunkCache[(fileIndex, chunkIndex)];
        var plaintext = _contentKey!.TryDecryptChunk(Manifest!.Manifest.SessionId, fileIndex, chunkIndex, ciphertext);
        if (plaintext is null)
            return; // Merkle-valid ciphertext that fails AEAD should not happen with the right key; leave pending.

        var file = Manifest.Manifest.Files[fileIndex];
        var range = ChunkLayout.GetRange(file.Size, file.ChunkSize, chunkIndex);
        await _sinks[fileIndex].WriteAsync(range.Offset, plaintext, ct).ConfigureAwait(false);
        _pendingDecrypt.Remove((fileIndex, chunkIndex));

        if (_bitmaps[fileIndex].IsComplete
            && !_pendingDecrypt.Any(k => k.File == fileIndex)
            && _sinks[fileIndex] is FileSystemFileSink diskSink)
        {
            diskSink.Complete();
        }
    }

    private async Task HandleChunkRequestAsync(ChunkRequestMessage request, CancellationToken ct)
    {
        if (Manifest is null || !request.SessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId))
            return;
        if (request.RequesterId.AsSpan().SequenceEqual(_receiverId))
            return; // don't answer our own broadcast request

        // Bound how much one inbound request can make this receiver do, for exactly the reason SenderSession's
        // own copy of this cap exists — and more sharply, because a receiver is simultaneously trying to process
        // its OWN inbound chunk stream. This handler is awaited inline from the gated packet path, so while it
        // runs no other packet is handled at all; a peer that sends a fragmented 5,000-index request (hostile, or
        // simply built by a pre-cap version of this code) would otherwise have a receiver re-serving 5,000 chunks
        // inline and stalling its own transfer. Overflow is dropped rather than queued: the requester's own
        // repair timer re-asks for whatever is still missing, so nothing is lost.
        var servedIndices = request.ChunkIndices.Length <= _maxChunksServedPerRequest
            ? request.ChunkIndices
            : request.ChunkIndices[.._maxChunksServedPerRequest];

        foreach (var chunkIndex in servedIndices)
        {
            if (!_chunkCache.TryGetValue((request.FileIndex, chunkIndex), out var cached))
                continue; // we don't have it either — let someone else (or the sender) answer

            // Relay large chunks as identity-keyed wire packets (byte-identical to the sender's), so a
            // requester accumulates them across sources/rounds; small chunks go as a whole ChunkResponse.
            if (ChunkPacketizer.RequiresPacketization(cached.Ciphertext.Length, cached.Proof, _maxDatagramPayloadBytes))
            {
                foreach (var packet in ChunkPacketizer.Split(
                    Manifest.Manifest.SessionId, request.FileIndex, chunkIndex, cached.Ciphertext, cached.Proof, _maxDatagramPayloadBytes))
                {
                    await SendMessageAsync(packet, ct).ConfigureAwait(false);
                }
                continue;
            }

            var response = new ChunkResponseMessage(
                Manifest.Manifest.SessionId, request.RequestNonce, request.FileIndex, chunkIndex, cached.Ciphertext, cached.Proof);
            await SendMessageAsync(response, ct).ConfigureAwait(false);
        }
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
    /// not fixed: at the 8 KiB default chunk size <i>every</i> chunk travels as <see cref="ChunkPacketMessage"/>,
    /// so gating the watermark lift on <paramref name="fromCarousel"/> would buy nothing. The ambiguity is
    /// structural (it is the same property that makes cross-round accumulation work); the per-pass cap is the
    /// right mitigation.</para>
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
        // than continuously, adding roughly 10 s to fully recovering a 10,240-chunk file. That is pacing, not a
        // stall: progress is strictly monotone, every wave is bounded, and spreading a whole-file recovery over
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
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _chunkCache.TryGetValue((fileIndex, chunkIndex), out var cached)
                ? new SwarmChunk(cached.Ciphertext, cached.Proof)
                : null;
        }
        finally
        {
            _stateGate.Release();
        }
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
