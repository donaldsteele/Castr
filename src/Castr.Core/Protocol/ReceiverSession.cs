using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Security;
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

    private readonly Key _encryptionKey;
    private readonly byte[] _encryptionPublicKey;

    private readonly Dictionary<int, ChunkBitmap> _bitmaps = [];
    private readonly Dictionary<int, IFileSink> _sinks = [];
    private readonly Dictionary<(int File, int Chunk), (byte[] Ciphertext, MerkleProof Proof)> _chunkCache = [];
    private readonly HashSet<(int File, int Chunk)> _pendingDecrypt = [];

    private ContentKey? _contentKey;

    public ReceiverSession(
        byte[] receiverId,
        ITrustStore trustStore,
        IMulticastTransport transport,
        ISystemClock clock,
        ReceiverSessionOptions options,
        Func<string, long, IFileSink> sinkFactory,
        IPeerTable? peerTable = null,
        RepairCoordinator? repairCoordinator = null)
    {
        _receiverId = receiverId;
        _trustStore = trustStore;
        _transport = transport;
        _clock = clock;
        _options = options;
        _sinkFactory = sinkFactory;
        _peerTable = peerTable ?? new PeerTable();
        _repairCoordinator = repairCoordinator ?? new RepairCoordinator(_peerTable, clock);

        // Every receiver identity holds its own X25519 encryption keypair (ADR-0003).
        _encryptionKey = EncryptionKeys.Create();
        _encryptionPublicKey = EncryptionKeys.ExportPublicKey(_encryptionKey);
    }

    public SignedManifest? Manifest { get; private set; }

    public event Action<TrustDecision, PublicKeyId>? SenderTrustDenied;

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
        await foreach (var packet in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            await HandlePacketAsync(packet, cancellationToken).ConfigureAwait(false);
            if (IsComplete)
                return;
        }
    }

    /// <summary>Computes and sends CHUNK_REQUEST for any file's currently-missing chunks. Call this periodically (e.g. from a stall timer) while <see cref="RunAsync"/> is also running.</summary>
    public async Task RequestRepairsAsync(CancellationToken cancellationToken)
    {
        if (Manifest is null)
            return;

        // Re-drive the content-key handshake until it succeeds: a single KEY_GRANT can be lost (it rides the
        // lossy multicast data plane), and without the content key no ciphertext can be decrypted. Each retry
        // prompts the sender to re-wrap and re-send. Cheap, and it reuses the caller's existing repair timer.
        if (_contentKey is null)
        {
            var join = new JoinRequestMessage(Manifest.Manifest.SessionId, _receiverId, _encryptionPublicKey);
            await _transport.SendAsync(MessageCodec.Encode(join), cancellationToken).ConfigureAwait(false);
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
                await _transport.SendAsync(MessageCodec.Encode(request), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandlePacketAsync(ReceivedPacket packet, CancellationToken ct)
    {
        object? decoded;
        try { decoded = MessageCodec.Decode(packet.Payload); }
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

        if (!ManifestVerifier.VerifySignature(message.SignedManifest))
            return; // invalid signature — forged or corrupt, reject outright

        var senderId = message.SignedManifest.SenderId;
        var decision = TrustDecisionEngine.Evaluate(senderId, _trustStore, _options.UnknownSenderPolicy, _options.IsInteractive);
        if (!decision.ShouldProceed)
        {
            SenderTrustDenied?.Invoke(decision, senderId);
            return;
        }

        Manifest = message.SignedManifest;
        for (int fileIndex = 0; fileIndex < Manifest.Manifest.Files.Count; fileIndex++)
        {
            var file = Manifest.Manifest.Files[fileIndex];
            _bitmaps[fileIndex] = new ChunkBitmap(file.ChunkCount);
            var destination = PathSafety.ResolveDestination(_options.DestinationRoot, file.RelativePath);
            _sinks[fileIndex] = _sinkFactory(destination, file.Size);
        }

        // Now that the sender's manifest is trusted, request the per-transfer content key (ADR-0003).
        var join = new JoinRequestMessage(Manifest.Manifest.SessionId, _receiverId, _encryptionPublicKey);
        await _transport.SendAsync(MessageCodec.Encode(join), ct).ConfigureAwait(false);
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
    }

    private async Task HandleChunkAsync(byte[] sessionId, int fileIndex, int chunkIndex, byte[] ciphertext, MerkleProof proof, CancellationToken ct)
    {
        if (Manifest is null || !sessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId))
            return;
        if (!_bitmaps.TryGetValue(fileIndex, out var bitmap) || bitmap.Get(chunkIndex))
            return;

        var file = Manifest.Manifest.Files[fileIndex];
        var ciphertextHash = ChunkHash.Compute(ciphertext);
        if (!ManifestVerifier.VerifyChunk(file.MerkleRoot, ciphertextHash, proof))
            return; // corrupt or spoofed ciphertext — silently drop; repair will re-request it from someone else

        bitmap.Set(chunkIndex);
        _chunkCache[(fileIndex, chunkIndex)] = (ciphertext, proof); // cached as ciphertext, for peer repair relay
        _repairCoordinator.MarkFulfilled(fileIndex, chunkIndex);
        _pendingDecrypt.Add((fileIndex, chunkIndex));

        // Decrypt-and-write now if we already hold the content key; otherwise it stays pending until KEY_GRANT.
        if (_contentKey is not null)
            await DecryptWriteAndTrackAsync(fileIndex, chunkIndex, ct).ConfigureAwait(false);

        await BroadcastPeerHaveAsync(fileIndex, ct).ConfigureAwait(false);
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

            var response = new ChunkResponseMessage(
                Manifest.Manifest.SessionId, request.RequestNonce, request.FileIndex, chunkIndex, cached.Ciphertext, cached.Proof);
            await _transport.SendAsync(MessageCodec.Encode(response), ct).ConfigureAwait(false);
        }
    }

    private async Task BroadcastPeerHaveAsync(int fileIndex, CancellationToken ct)
    {
        var message = new PeerHaveMessage(
            Manifest!.Manifest.SessionId, _receiverId, fileIndex, _bitmaps[fileIndex].ToBytes(), "", 0);
        await _transport.SendAsync(MessageCodec.Encode(message), ct).ConfigureAwait(false);
    }

    private static byte[] NewNonce() => Guid.NewGuid().ToByteArray();
}
