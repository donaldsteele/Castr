using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Transport;

namespace Castr.Core.Protocol;

/// <summary>
/// Drives the sender side of a transfer over a single <see cref="IMulticastTransport"/>: announces,
/// broadcasts the signed manifest, carousels every chunk once, and answers CHUNK_REQUEST with
/// CHUNK_RESPONSE for any chunk it holds. Requests and responses travel over the same multicast channel
/// (see wiki/concepts/repair-protocol.md) so any listener — not just the original requester — benefits
/// from a fulfilled repair.
/// </summary>
/// <remarks>
/// Known M1 scope trim: this sends the chunk carousel exactly once rather than repeating rounds
/// (FLUTE-style self-healing); fault tolerance instead relies on peer/sender repair via CHUNK_REQUEST,
/// which is fully implemented. Multi-round carousel repetition is a natural, low-risk future addition.
/// </remarks>
public sealed class SenderSession(
    SignedManifest signedManifest,
    IReadOnlyDictionary<int, IFileSource> fileSources,
    IReadOnlyDictionary<int, MerkleTree> merkleTrees,
    IMulticastTransport transport)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
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
            }
        }
    }

    private async Task HandleIncomingAsync(CancellationToken ct)
    {
        await foreach (var packet in transport.ReceiveAsync(ct).ConfigureAwait(false))
        {
            var decoded = TryDecode(packet.Payload);
            if (decoded is not ChunkRequestMessage request)
                continue;
            if (!request.SessionId.AsSpan().SequenceEqual(signedManifest.Manifest.SessionId))
                continue;
            if (!fileSources.TryGetValue(request.FileIndex, out var source) || !merkleTrees.TryGetValue(request.FileIndex, out var tree))
                continue;

            foreach (var chunkIndex in request.ChunkIndices)
                await SendChunkAsync(request.FileIndex, chunkIndex, source, tree, request.RequestNonce, ct).ConfigureAwait(false);
        }
    }

    private async Task SendChunkAsync(int fileIndex, int chunkIndex, IFileSource source, MerkleTree tree, byte[]? requestNonce, CancellationToken ct)
    {
        var file = signedManifest.Manifest.Files[fileIndex];
        var payload = await Chunker.ReadChunkAsync(source, file.ChunkSize, chunkIndex, ct).ConfigureAwait(false);
        var proof = tree.GetProof(chunkIndex);

        object message = requestNonce is null
            ? new ChunkDataMessage(signedManifest.Manifest.SessionId, fileIndex, chunkIndex, payload, proof)
            : new ChunkResponseMessage(signedManifest.Manifest.SessionId, requestNonce, fileIndex, chunkIndex, payload, proof);

        await transport.SendAsync(MessageCodec.Encode(message), ct).ConfigureAwait(false);
    }

    private static object? TryDecode(byte[] payload)
    {
        try { return MessageCodec.Decode(payload); }
        catch { return null; } // malformed/corrupt packet — ignore, don't crash the receive loop
    }
}
