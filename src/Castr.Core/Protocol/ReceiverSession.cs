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
/// sender trust, verifies the signed manifest, verifies and writes each chunk via its Merkle proof,
/// tracks per-file completion, broadcasts PEER_HAVE, and both requests and serves repairs.
/// </summary>
/// <remarks>
/// A receiver re-serves chunks to peers by caching the exact (payload, proof) pair it already verified
/// for that chunk — not by reconstructing the file's full Merkle tree, which it never has. The proof is a
/// self-contained artifact of leaf index + tree shape, so it verifies identically no matter who relays it.
/// Known M1 scope trim: CHUNK_REQUEST/RESPONSE both travel over the shared multicast channel rather than
/// being unicast-addressed to a specific peer — simpler, and consistent with "any listener can answer."
/// <see cref="RepairCoordinator"/>'s per-plan Target is still computed and tested for when a future pass
/// wires in targeted unicast (needed for the mobile tier regardless).
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

    private readonly Dictionary<int, ChunkBitmap> _bitmaps = [];
    private readonly Dictionary<int, IFileSink> _sinks = [];
    private readonly Dictionary<(int File, int Chunk), (byte[] Payload, MerkleProof Proof)> _chunkCache = [];

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
    }

    public SignedManifest? Manifest { get; private set; }

    public event Action<TrustDecision, PublicKeyId>? SenderTrustDenied;

    public bool IsComplete =>
        Manifest is not null && _bitmaps.Count == Manifest.Manifest.Files.Count && _bitmaps.Values.All(b => b.IsComplete);

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
                await HandleManifestAsync(manifestMessage).ConfigureAwait(false);
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
        }
    }

    private Task HandleManifestAsync(ManifestMessage message)
    {
        if (Manifest is not null)
            return Task.CompletedTask; // one-shot session: first accepted manifest wins

        if (!ManifestVerifier.VerifySignature(message.SignedManifest))
            return Task.CompletedTask; // invalid signature — forged or corrupt, reject outright

        var senderId = message.SignedManifest.SenderId;
        var decision = TrustDecisionEngine.Evaluate(senderId, _trustStore, _options.UnknownSenderPolicy, _options.IsInteractive);
        if (!decision.ShouldProceed)
        {
            SenderTrustDenied?.Invoke(decision, senderId);
            return Task.CompletedTask;
        }

        Manifest = message.SignedManifest;
        for (int fileIndex = 0; fileIndex < Manifest.Manifest.Files.Count; fileIndex++)
        {
            var file = Manifest.Manifest.Files[fileIndex];
            _bitmaps[fileIndex] = new ChunkBitmap(file.ChunkCount);
            var destination = PathSafety.ResolveDestination(_options.DestinationRoot, file.RelativePath);
            _sinks[fileIndex] = _sinkFactory(destination, file.Size);
        }

        return Task.CompletedTask;
    }

    private async Task HandleChunkAsync(byte[] sessionId, int fileIndex, int chunkIndex, byte[] payload, MerkleProof proof, CancellationToken ct)
    {
        if (Manifest is null || !sessionId.AsSpan().SequenceEqual(Manifest.Manifest.SessionId))
            return;
        if (!_bitmaps.TryGetValue(fileIndex, out var bitmap) || bitmap.Get(chunkIndex))
            return;

        var file = Manifest.Manifest.Files[fileIndex];
        var chunkHash = ChunkHash.Compute(payload);
        if (!ManifestVerifier.VerifyChunk(file.MerkleRoot, chunkHash, proof))
            return; // corrupt or spoofed — silently drop; repair will re-request it from someone else

        var range = ChunkLayout.GetRange(file.Size, file.ChunkSize, chunkIndex);
        await _sinks[fileIndex].WriteAsync(range.Offset, payload, ct).ConfigureAwait(false);

        bitmap.Set(chunkIndex);
        _chunkCache[(fileIndex, chunkIndex)] = (payload, proof);
        _repairCoordinator.MarkFulfilled(fileIndex, chunkIndex);

        if (bitmap.IsComplete && _sinks[fileIndex] is FileSystemFileSink diskSink)
            diskSink.Complete();

        await BroadcastPeerHaveAsync(fileIndex, ct).ConfigureAwait(false);
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
                Manifest.Manifest.SessionId, request.RequestNonce, request.FileIndex, chunkIndex, cached.Payload, cached.Proof);
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
