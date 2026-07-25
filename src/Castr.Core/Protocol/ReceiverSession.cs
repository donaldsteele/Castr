using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Security;
using Castr.Core.Swarm;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Trust;

namespace Castr.Core.Protocol;

public sealed record ReceiverSessionOptions(
    string DestinationRoot,
    UnknownSenderPolicy UnknownSenderPolicy = UnknownSenderPolicy.Deny,
    bool IsInteractive = false);

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
        _repairCoordinator = repairCoordinator ?? new RepairCoordinator(_peerTable, clock);
        _trustPrompt = trustPrompt;
        _maxDatagramPayloadBytes = maxDatagramPayloadBytes;

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

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await HandlePacketAsync(payload, cancellationToken).ConfigureAwait(false);
                if (IsComplete)
                    return;
            }
            finally
            {
                _stateGate.Release();
            }
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

            for (int fileIndex = 0; fileIndex < Manifest.Manifest.Files.Count; fileIndex++)
            {
                var bitmap = _bitmaps[fileIndex];
                if (bitmap.IsComplete)
                    continue;

                var missing = bitmap.MissingIndices().ToList();
                var senderEndpoint = new Endpoint("sender", 0); // multicast-only MVP: Target is informational, delivery is always multicast
                var plans = _repairCoordinator.PlanRepairs(fileIndex, missing, senderEndpoint, NewNonce);

                foreach (var plan in plans)
                {
                    var request = new ChunkRequestMessage(
                        Manifest.Manifest.SessionId, _receiverId, plan.RequestNonce, fileIndex, plan.ChunkIndices, "", 0);
                    await SendMessageAsync(request, cancellationToken).ConfigureAwait(false);
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
                await HandleChunkAsync(chunkData.SessionId, chunkData.FileIndex, chunkData.ChunkIndex, chunkData.Payload, chunkData.Proof, ct).ConfigureAwait(false);
                break;
            case ChunkResponseMessage chunkResponse:
                await HandleChunkAsync(chunkResponse.SessionId, chunkResponse.FileIndex, chunkResponse.ChunkIndex, chunkResponse.Payload, chunkResponse.Proof, ct).ConfigureAwait(false);
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
            || packet.ChunkIndex < 0 || packet.ChunkIndex >= bitmap.ChunkCount
            || bitmap.Get(packet.ChunkIndex))
            return; // unknown file, out-of-range chunk, or one we already have — drop stray/duplicate/bad packets

        var assembled = _chunkAssembler.Offer(packet);
        if (assembled is not { } complete)
            return; // still missing packets — the chunk stays "not received" until repair fills the gaps

        _chunkAssembler.Forget(packet.FileIndex, packet.ChunkIndex);
        await HandleChunkAsync(packet.SessionId, packet.FileIndex, packet.ChunkIndex, complete.Ciphertext, complete.Proof, ct).ConfigureAwait(false);
    }

    private async Task HandleChunkAsync(byte[] sessionId, int fileIndex, int chunkIndex, byte[] ciphertext, MerkleProof proof, CancellationToken ct)
    {
        if (Manifest is null || !sessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId))
            return;
        // Same guard for the whole-chunk paths (ChunkData / ChunkResponse): a wire-supplied chunkIndex out of the
        // file's range must be dropped, not passed to ChunkBitmap.Get where it would throw and fault the loop.
        if (!_bitmaps.TryGetValue(fileIndex, out var bitmap)
            || chunkIndex < 0 || chunkIndex >= bitmap.ChunkCount
            || bitmap.Get(chunkIndex))
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

        await BroadcastPeerHaveAsync(fileIndex, ct).ConfigureAwait(false);

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

        foreach (var chunkIndex in request.ChunkIndices)
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

    private async Task BroadcastPeerHaveAsync(int fileIndex, CancellationToken ct)
    {
        var message = new PeerHaveMessage(
            Manifest!.Manifest.SessionId, _receiverId, fileIndex, _bitmaps[fileIndex].ToBytes(), "", 0);
        await SendMessageAsync(message, ct).ConfigureAwait(false);
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
