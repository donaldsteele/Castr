using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Trust;

namespace Castr.Core.Swarm;

public sealed record SwarmPullSessionOptions(
    string DestinationRoot,
    UnknownSenderPolicy UnknownSenderPolicy = UnknownSenderPolicy.Deny,
    bool IsInteractive = false);

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
/// authentication). A chunk is verifiable — and cached for re-serving — the moment it arrives, even before the
/// content key does; it decrypts once the key lands. This session additionally binds each proof's
/// <see cref="MerkleProof.LeafIndex"/> to the claimed chunk index, closing a chunk-index-swap a malicious peer
/// could otherwise attempt over a directed request (see the position-binding note in the report).
/// <para>
/// State (manifest, content key, per-file bitmaps, verified-chunk cache) persists across
/// <see cref="PullFromAsync"/> calls, so a pull interrupted by a peer dropping resumes against the same or a
/// different peer requesting only still-missing chunks. Only the original sender can grant the content key;
/// chunks may be pulled from any peer (sender or another receiver). Not safe for concurrent
/// <see cref="PullFromAsync"/> calls — an internal gate serializes them.
/// </para>
/// </remarks>
public sealed class SwarmPullSession : IDisposable
{
    private const int MaxChunksPerBatch = 1024;

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
    private readonly Dictionary<(int File, int Chunk), (byte[] Ciphertext, MerkleProof Proof)> _chunkCache = [];
    private readonly HashSet<(int File, int Chunk)> _pendingDecrypt = [];

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
            _options.UnknownSenderPolicy, _options.IsInteractive, _trustPrompt, ct).ConfigureAwait(false);

        if (admission.Outcome == ManifestAdmissionOutcome.SignatureInvalid)
            return false; // forged or corrupt — reject silently, exactly like ReceiverSession

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
        _chunkCache[(fileIndex, chunkIndex)] = (ciphertext, proof);
        _pendingDecrypt.Add((fileIndex, chunkIndex));

        if (_contentKey is not null)
            await DecryptWriteAndTrackAsync(fileIndex, chunkIndex, ct).ConfigureAwait(false);

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
