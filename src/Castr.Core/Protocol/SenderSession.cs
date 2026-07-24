using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Security;
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
    ContentKey contentKey)
{
    private readonly object _progressGate = new();
    private readonly HashSet<string> _grantedReceivers = [];
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
        await transport.SendAsync(MessageCodec.Encode(announce), ct).ConfigureAwait(false);
    }

    private async Task SendManifestAsync(CancellationToken ct) =>
        await transport.SendAsync(MessageCodec.Encode(new ManifestMessage(signedManifest)), ct).ConfigureAwait(false);

    private async Task RunChunkCarouselAsync(CancellationToken ct)
    {
        for (int fileIndex = 0; fileIndex < signedManifest.Manifest.Files.Count; fileIndex++)
        {
            var file = signedManifest.Manifest.Files[fileIndex];
            var source = fileSources[fileIndex];
            var tree = merkleTrees[fileIndex];

            for (int chunkIndex = 0; chunkIndex < file.ChunkCount; chunkIndex++)
            {
                ct.ThrowIfCancellationRequested();
                await SendChunkAsync(fileIndex, chunkIndex, source, tree, requestNonce: null, ct).ConfigureAwait(false);

                int chunkLength = ChunkLayout.GetRange(file.Size, file.ChunkSize, chunkIndex).Length;
                lock (_progressGate)
                {
                    _sentChunks++;
                    _sentBytes += chunkLength;
                }
                EmitProgress(TransferPhase.Transferring);
            }
        }

        lock (_progressGate)
            _carouselComplete = true;
        EmitProgress(TransferPhase.Serving);
    }

    private async Task HandleIncomingAsync(CancellationToken ct)
    {
        await foreach (var packet in transport.ReceiveAsync(ct).ConfigureAwait(false))
        {
            switch (TryDecode(packet.Payload))
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

        foreach (var chunkIndex in request.ChunkIndices)
            await SendChunkAsync(request.FileIndex, chunkIndex, source, tree, request.RequestNonce, ct).ConfigureAwait(false);
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
        await transport.SendAsync(MessageCodec.Encode(grant), ct).ConfigureAwait(false);

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

        object message = requestNonce is null
            ? new ChunkDataMessage(signedManifest.Manifest.SessionId, fileIndex, chunkIndex, ciphertext, proof)
            : new ChunkResponseMessage(signedManifest.Manifest.SessionId, requestNonce, fileIndex, chunkIndex, ciphertext, proof);

        await transport.SendAsync(MessageCodec.Encode(message), ct).ConfigureAwait(false);
    }

    private static object? TryDecode(byte[] payload)
    {
        try { return MessageCodec.Decode(payload); }
        catch { return null; } // malformed/corrupt packet — ignore, don't crash the receive loop
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
