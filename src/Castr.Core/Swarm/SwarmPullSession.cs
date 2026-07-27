using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Trust;

namespace Castr.Core.Swarm;

/// <param name="ChunkCacheBytes">
/// Ceiling on verified-but-not-yet-decrypted chunk ciphertext held in memory. Null uses
/// <see cref="SwarmPullSession.DefaultChunkCacheBytes"/>. Injectable mainly so tests can drive eviction without
/// allocating the default budget; a mobile host with a tight memory ceiling is the other real caller.
/// </param>
public sealed record SwarmPullSessionOptions(
    string DestinationRoot,
    UnknownSenderPolicy UnknownSenderPolicy = UnknownSenderPolicy.Deny,
    bool IsInteractive = false,
    long? ChunkCacheBytes = null,
    ISessionRegistry? SessionRegistry = null);

/// <summary>
/// The mobile unicast-swarm client: pulls a transfer over point-to-point TCP instead of joining IP multicast.
/// Given a peer <see cref="Endpoint"/> (fed by native mDNS discovery, which lives in Castr.Core.Discovery and
/// is not this class's concern) it connects, requests the signed manifest, runs it through the <b>exact same</b>
/// signature + TOFU trust gate the multicast <see cref="ReceiverSession"/> uses
/// (<see cref="ManifestAdmission"/>), obtains the per-transfer content key via the same JOIN_REQUEST/KEY_GRANT
/// handshake (now over the stream), then pulls, verifies, decrypts, and writes each chunk.
/// </summary>
/// <remarks>
/// Verification is the multicast tier's double check, unchanged: every chunk's ciphertext is verified against
/// the signed per-file Merkle root, then AEAD-decrypted under the content key (a second, independent
/// authentication). A chunk is verifiable the moment it arrives, even before the content key does; its
/// ciphertext is held and it decrypts once the key lands. This session additionally binds each proof's
/// <see cref="MerkleProof.LeafIndex"/> to the claimed chunk index, closing a chunk-index-swap a malicious peer
/// could otherwise attempt over a directed request (see the position-binding note in the report).
/// <para>
/// State (manifest, content key, per-file bitmaps, held ciphertext) persists across
/// <see cref="PullFromAsync"/> calls, so a pull interrupted by a peer dropping resumes against the same or a
/// different peer requesting only still-missing chunks. Only the original sender can grant the content key;
/// chunks may be pulled from any peer (sender or another receiver). Not safe for concurrent
/// <see cref="PullFromAsync"/> calls — an internal gate serializes them.
/// </para>
/// </remarks>
public sealed class SwarmPullSession : IDisposable
{
    private const int MaxChunksPerBatch = 1024;

    /// <summary>
    /// Default ceiling on held chunk ciphertext, in bytes. Matches
    /// <see cref="Protocol.ReceiverSession.DefaultChunkCacheBytes"/> so the two receive tiers have the same
    /// memory shape, though the two caches exist for different reasons — see the eviction note on
    /// <see cref="EvictDownToBudget"/>.
    ///
    /// <para><b>Why a bound is needed at all.</b> Ciphertext is retained only until the chunk is decrypted and
    /// written, and the content key is normally acquired before any chunk is pulled — so the steady state holds
    /// almost nothing. But the swarm tier's whole point is that ciphertext and key may come from different
    /// peers: a puller can take the entire transfer from a relaying receiver, which cannot grant the key
    /// (<see cref="ISwarmContentSource.TryGrantContentKey"/> returns null for any receiver), and only afterwards
    /// reach the sender. Unbounded, that path holds the <b>whole transfer</b> in memory before a single byte is
    /// written — the same defect M10 fixed on the multicast side, where the relay case is a startup window
    /// rather than, as here, an ordinary way to run.</para>
    /// </summary>
    public const long DefaultChunkCacheBytes = 32L * 1024 * 1024;

    private readonly byte[] _receiverId;
    private readonly ITrustStore _trustStore;
    private readonly IStreamClient _streamClient;
    private readonly ISystemClock _clock;
    private readonly SwarmPullSessionOptions _options;
    private readonly Func<string, long, IFileSink> _sinkFactory;
    private readonly ITrustPrompt? _trustPrompt;
    private readonly int _maxFrameLength;

    private readonly SemaphoreSlim _pullGate = new(1, 1);
    private readonly Key _encryptionKey;
    private readonly byte[] _encryptionPublicKey;

    private readonly Dictionary<int, ChunkBitmap> _bitmaps = [];
    private readonly Dictionary<int, IFileSink> _sinks = [];
    private readonly HashSet<(int File, int Chunk)> _pendingDecrypt = [];

    // ---- Held chunk ciphertext (see DefaultChunkCacheBytes) ----
    //
    // Byte-bounded and evicted least-recently-used, on M10's shape. Two deliberate differences from
    // ReceiverSession's cache, both of which follow from this class not being an ISwarmContentSource:
    //
    //   * No proof retention. ReceiverSession keeps every chunk's MerkleProof for the life of the session
    //     because it re-serves chunks to peers and a proof cannot be regenerated from the signed root alone.
    //     Nothing reads a proof here after AcceptChunkAsync has verified it, so keeping one per chunk would be
    //     write-only state unbounded in chunk count — the defect, not the fix.
    //   * No cold rebuild. ReceiverSession reconstructs an evicted chunk by re-encrypting the plaintext off the
    //     sink. The only reader here is the decrypt-and-write path, which by definition runs on chunks that
    //     have no plaintext on disk yet, so there would be nothing to read back.
    //
    // _lru is most-recently-used first; each dictionary value is that chunk's node in it, so a hit is O(1) to
    // promote. All of it is touched only under _pullGate, like every other field below.
    private readonly Dictionary<(int File, int Chunk), LinkedListNode<HeldChunk>> _heldCiphertext = [];
    private readonly LinkedList<HeldChunk> _lru = new();
    private readonly long _chunkCacheBytes;
    private long _heldBytes;

    private sealed record HeldChunk((int File, int Chunk) Key, byte[] Ciphertext);

    private ContentKey? _contentKey;
    private long _verifiedBytes;

    public SwarmPullSession(
        byte[] receiverId,
        ITrustStore trustStore,
        IStreamClient streamClient,
        ISystemClock clock,
        SwarmPullSessionOptions options,
        Func<string, long, IFileSink> sinkFactory,
        ITrustPrompt? trustPrompt = null,
        int maxFrameLength = LengthPrefixedFramer.DefaultMaxFrameLength)
    {
        _receiverId = receiverId;
        _trustStore = trustStore;
        _streamClient = streamClient;
        _clock = clock;
        _options = options;
        _sinkFactory = sinkFactory;
        _trustPrompt = trustPrompt;
        _maxFrameLength = maxFrameLength;
        _chunkCacheBytes = Math.Max(0, options.ChunkCacheBytes ?? DefaultChunkCacheBytes);

        _encryptionKey = EncryptionKeys.Create();
        _encryptionPublicKey = EncryptionKeys.ExportPublicKey(_encryptionKey);
    }

    public SignedManifest? Manifest { get; private set; }

    /// <summary>Raised when a peer's manifest is signature-valid but its sender is not trusted (and no prompt accepted them) — mirrors <see cref="ReceiverSession.SenderTrustDenied"/>.</summary>
    public event Action<TrustDecision, PublicKeyId>? SenderTrustDenied;

    /// <summary>Same observational progress contract as <see cref="ReceiverSession.ProgressChanged"/>, so mobile UIs consume it identically.</summary>
    public event Action<TransferProgress>? ProgressChanged;

    /// <summary>Complete once every chunk of every file is verified, the content key is held, and all ciphertext is decrypted and written.</summary>
    public bool IsComplete =>
        Manifest is not null
        && _contentKey is not null
        && _bitmaps.Count == Manifest.Manifest.Files.Count
        && _bitmaps.Values.All(b => b.IsComplete)
        && _pendingDecrypt.Count == 0;

    /// <summary>
    /// Connects to one peer and pulls as much as that peer can provide: the manifest (if not yet accepted), the
    /// content key (if the peer is the sender and the key is not yet held), and any still-missing chunks.
    /// Returns true if the session holds an accepted manifest afterward; false if this peer's manifest was
    /// rejected (bad signature or untrusted sender). Idempotent and resumable — call again, on the same or
    /// another peer, to make further progress.
    /// </summary>
    public async Task<bool> PullFromAsync(Endpoint peer, CancellationToken cancellationToken)
    {
        await _pullGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EmitProgress();
            await using var connection = await _streamClient.ConnectAsync(peer, cancellationToken).ConfigureAwait(false);
            var framer = new LengthPrefixedFramer(connection, _maxFrameLength);

            if (Manifest is null && !await AcquireManifestAsync(framer, cancellationToken).ConfigureAwait(false))
                return false;

            if (_contentKey is null)
                await AcquireContentKeyAsync(framer, cancellationToken).ConfigureAwait(false);

            await PullMissingChunksAsync(framer, cancellationToken).ConfigureAwait(false);

            EmitProgress();
            return true;
        }
        finally
        {
            _pullGate.Release();
        }
    }

    private async Task<bool> AcquireManifestAsync(LengthPrefixedFramer framer, CancellationToken ct)
    {
        await framer.WriteFrameAsync(MessageCodec.Encode(new ManifestRequestMessage()), ct).ConfigureAwait(false);

        byte[]? frame = await framer.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frame is null || MessageCodec.Decode(frame) is not ManifestMessage manifestMessage)
            return false; // peer served no manifest

        var admission = await ManifestAdmission.EvaluateAsync(
            manifestMessage.SignedManifest, _trustStore, _clock,
            _options.UnknownSenderPolicy, _options.IsInteractive, _trustPrompt, ct,
            _options.SessionRegistry).ConfigureAwait(false);

        if (admission.Outcome == ManifestAdmissionOutcome.SignatureInvalid)
            return false; // forged or corrupt — reject silently, exactly like ReceiverSession

        if (admission.Outcome == ManifestAdmissionOutcome.SessionIdConflict)
            return false; // this session id already means a different transfer — see ISessionRegistry

        if (admission.Outcome == ManifestAdmissionOutcome.Denied)
        {
            EmitProgress(TransferPhase.TrustDenied);
            SenderTrustDenied?.Invoke(admission.Decision!, manifestMessage.SignedManifest.SenderId);
            return false;
        }

        Manifest = manifestMessage.SignedManifest;
        for (int fileIndex = 0; fileIndex < Manifest.Manifest.Files.Count; fileIndex++)
        {
            var file = Manifest.Manifest.Files[fileIndex];
            _bitmaps[fileIndex] = new ChunkBitmap(file.ChunkCount);
            var destination = PathSafety.ResolveDestination(_options.DestinationRoot, file.RelativePath);
            _sinks[fileIndex] = _sinkFactory(destination, file.Size);
        }

        EmitProgress();
        return true;
    }

    private async Task AcquireContentKeyAsync(LengthPrefixedFramer framer, CancellationToken ct)
    {
        if (Manifest is null)
            return;

        var join = new JoinRequestMessage(Manifest.Manifest.SessionId, _receiverId, _encryptionPublicKey);
        await framer.WriteFrameAsync(MessageCodec.Encode(join), ct).ConfigureAwait(false);

        byte[]? frame = await framer.ReadFrameAsync(ct).ConfigureAwait(false);
        if (frame is null)
            return;

        if (MessageCodec.Decode(frame) is not KeyGrantMessage grant)
            return; // KeyUnavailable (a receiver relay) or anything else — get the key from the sender instead

        if (!grant.SessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId)
            || !grant.ReceiverId.AsSpan().SequenceEqual(_receiverId))
            return;

        var raw = ContentKeyWrap.TryUnwrap(
            _encryptionKey, Manifest.Manifest.SenderEncryptionPublicKey,
            Manifest.Manifest.SessionId, _receiverId, grant.WrappedContentKey);
        if (raw is null)
            return; // spoofed grant or mismatched key — ignore

        _contentKey = ContentKey.Import(raw);

        foreach (var key in _pendingDecrypt.ToList())
            await DecryptWriteAndTrackAsync(key.File, key.Chunk, ct).ConfigureAwait(false);

        EmitProgress();
    }

    private async Task PullMissingChunksAsync(LengthPrefixedFramer framer, CancellationToken ct)
    {
        if (Manifest is null)
            return;

        for (int fileIndex = 0; fileIndex < Manifest.Manifest.Files.Count; fileIndex++)
        {
            var bitmap = _bitmaps[fileIndex];
            if (bitmap.IsComplete)
                continue;

            var missing = bitmap.MissingIndices().ToList();
            foreach (var batch in Batch(missing, MaxChunksPerBatch))
            {
                await framer.WriteFrameAsync(
                    MessageCodec.Encode(new ChunkPullRequestMessage(Manifest.Manifest.SessionId, fileIndex, [.. batch])), ct)
                    .ConfigureAwait(false);

                // The server replies with exactly one response per requested index, in order.
                for (int i = 0; i < batch.Count; i++)
                {
                    byte[]? frame = await framer.ReadFrameAsync(ct).ConfigureAwait(false);
                    if (frame is null)
                        return; // peer dropped mid-batch — remaining chunks stay missing, resume later
                    if (MessageCodec.Decode(frame) is ChunkPullResponseMessage { Found: true } response)
                        await AcceptChunkAsync(response.FileIndex, response.ChunkIndex, response.Payload, response.Proof!, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task AcceptChunkAsync(int fileIndex, int chunkIndex, byte[] ciphertext, MerkleProof proof, CancellationToken ct)
    {
        if (Manifest is null || !_bitmaps.TryGetValue(fileIndex, out var bitmap))
            return;
        if (chunkIndex < 0 || chunkIndex >= bitmap.ChunkCount || bitmap.Get(chunkIndex))
            return;

        // Bind the proof's committed leaf position to the claimed chunk index: a peer must not be able to pass
        // off chunk A's (valid) ciphertext+proof as chunk B. Merkle verification alone recomputes the root from
        // any real leaf and would accept it; the AEAD AAD would later reject the mismatch, but only after the
        // bitmap was set. Rejecting here keeps a swapped chunk from ever being marked "have."
        if (proof.LeafIndex != chunkIndex)
            return;

        var file = Manifest.Manifest.Files[fileIndex];
        var ciphertextHash = ChunkHash.Compute(ciphertext);
        if (!ManifestVerifier.VerifyChunk(file.MerkleRoot, ciphertextHash, proof))
            return; // corrupt or spoofed ciphertext — drop; re-request from another peer

        bitmap.Set(chunkIndex);
        _verifiedBytes += ChunkLayout.GetRange(file.Size, file.ChunkSize, chunkIndex).Length;
        // Mark pending before holding the ciphertext: Hold's eviction pass treats a pending chunk as the
        // expensive kind to drop, and this one has no plaintext on disk to fall back on.
        _pendingDecrypt.Add((fileIndex, chunkIndex));
        Hold(fileIndex, chunkIndex, ciphertext);

        if (_contentKey is not null)
            await DecryptWriteAndTrackAsync(fileIndex, chunkIndex, ct).ConfigureAwait(false);

        EmitProgress();
    }

    private async Task DecryptWriteAndTrackAsync(int fileIndex, int chunkIndex, CancellationToken ct)
    {
        var key = (fileIndex, chunkIndex);

        // A miss means the budget already forced this chunk back to "missing" and a later pull will re-fetch it,
        // or it was decrypted and written on an earlier call. Either way there is nothing left to do here.
        if (!_heldCiphertext.TryGetValue(key, out var node))
            return;

        var plaintext = _contentKey!.TryDecryptChunk(Manifest!.Manifest.SessionId, fileIndex, chunkIndex, node.Value.Ciphertext);
        if (plaintext is null)
            return; // Merkle-valid ciphertext that fails AEAD should not happen with the right key; leave pending.

        var file = Manifest.Manifest.Files[fileIndex];
        var range = ChunkLayout.GetRange(file.Size, file.ChunkSize, chunkIndex);
        await _sinks[fileIndex].WriteAsync(range.Offset, plaintext, ct).ConfigureAwait(false);
        // The plaintext is on disk now. Nothing in this class ever reads the ciphertext again — unlike
        // ReceiverSession, this session serves no peers — so release it immediately rather than letting it age
        // out of the LRU and crowd out chunks that still need it.
        _pendingDecrypt.Remove(key);
        Drop(node);

        if (_bitmaps[fileIndex].IsComplete
            && !_pendingDecrypt.Any(k => k.File == fileIndex)
            && _sinks[fileIndex] is FileSystemFileSink diskSink)
        {
            diskSink.Complete();
        }
    }

    // ---- Held chunk ciphertext ----

    /// <summary>
    /// Bytes of chunk ciphertext currently held in memory. Bounded by
    /// <see cref="SwarmPullSessionOptions.ChunkCacheBytes"/>, except that one chunk is always kept so a budget
    /// smaller than a single chunk still makes progress. Exposed for tests and diagnostics.
    /// </summary>
    public long HeldCiphertextBytes => _heldBytes;

    /// <summary>Number of chunks whose ciphertext is currently held. Diagnostics only.</summary>
    public int HeldChunkCount => _heldCiphertext.Count;

    private void Hold(int fileIndex, int chunkIndex, byte[] ciphertext)
    {
        var key = (fileIndex, chunkIndex);
        if (_heldCiphertext.ContainsKey(key))
            return; // already held (this path is reached once per chunk, but stay idempotent)

        _heldCiphertext[key] = _lru.AddFirst(new HeldChunk(key, ciphertext));
        _heldBytes += ciphertext.Length;
        EvictDownToBudget();
    }

    /// <summary>
    /// Drops least-recently-used ciphertext until the budget is met.
    ///
    /// <para><b>Why this evicts undecrypted chunks where <c>ReceiverSession</c> pins them.</b> On the multicast
    /// side a chunk verified before the KEY_GRANT lands is pinned, because that window is a short startup
    /// transient and overshooting the budget for its duration is cheaper than the alternative. Here the same
    /// state is a normal way to run for an entire transfer — a puller taking ciphertext from relaying receivers
    /// holds every chunk undecrypted until it reaches the sender — so pinning would mean no bound at all.</para>
    ///
    /// <para><b>Dropping one is safe only because the bitmap bit goes with it.</b> An undecrypted chunk's
    /// ciphertext is the only copy in existence; there is no plaintext on disk to rebuild it from. Leaving the
    /// bit set would strand the chunk permanently — <see cref="PullMissingChunksAsync"/> asks only for
    /// still-missing indices, so a "have" bit with nothing behind it is never re-requested and never written.
    /// Clearing the bit (and the verified-byte count that went with it) puts the chunk back in the missing set,
    /// where the session's ordinary resume path re-fetches it from this or another peer.</para>
    ///
    /// <para>One chunk is always retained, so a budget below a single chunk's size degrades to
    /// hold-one-at-a-time rather than to a livelock where every chunk is dropped the instant it arrives.</para>
    /// </summary>
    private void EvictDownToBudget()
    {
        // Everything resident is by construction still undecrypted: a chunk's ciphertext is released the moment
        // its plaintext reaches the sink, so there is no cheap tier to evict first.
        var node = _lru.Last;
        while (node is not null && _heldBytes > _chunkCacheBytes && _lru.Count > 1)
        {
            var previous = node.Previous;
            ReturnToMissing(node.Value.Key);
            Drop(node);
            node = previous;
        }
    }

    private void Drop(LinkedListNode<HeldChunk> node)
    {
        _lru.Remove(node);
        _heldCiphertext.Remove(node.Value.Key);
        _heldBytes -= node.Value.Ciphertext.Length;
    }

    /// <summary>Walks back a verified-but-undecryptable chunk so the next pull re-requests it. See <see cref="EvictDownToBudget"/>.</summary>
    private void ReturnToMissing((int File, int Chunk) key)
    {
        _pendingDecrypt.Remove(key);

        var bitmap = _bitmaps[key.File];
        if (!bitmap.Get(key.Chunk))
            return;

        bitmap.Clear(key.Chunk);
        var file = Manifest!.Manifest.Files[key.File];
        _verifiedBytes -= ChunkLayout.GetRange(file.Size, file.ChunkSize, key.Chunk).Length;
    }

    private static IEnumerable<List<int>> Batch(List<int> items, int size)
    {
        for (int i = 0; i < items.Count; i += size)
            yield return items.GetRange(i, Math.Min(size, items.Count - i));
    }

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
            PeerCount: 0);
    }

    public void Dispose()
    {
        _encryptionKey.Dispose();
        _contentKey?.Dispose();
        _pullGate.Dispose();
    }
}
