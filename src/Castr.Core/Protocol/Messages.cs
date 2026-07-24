using Castr.Core.Chunking;
using Castr.Core.Manifest;

namespace Castr.Core.Protocol;

public enum MessageType : byte
{
    Announce = 1,
    Manifest = 2,
    ChunkData = 3,
    PeerHave = 4,
    ChunkRequest = 5,
    ChunkResponse = 6,
    TransferComplete = 7,
}

/// <summary>Periodic lightweight heartbeat advertising an active transfer offer, before a receiver commits to fetching the full signed manifest.</summary>
public sealed record AnnounceMessage(
    byte[] SessionId,
    byte[] SenderPublicKey,
    ChunkHash ManifestDigest,
    string TransferName,
    DateTimeOffset IssuedAt);

/// <summary>The full signed manifest, carried as its own message so it can be re-requested (unicast) independently of the ANNOUNCE carousel.</summary>
public sealed record ManifestMessage(SignedManifest SignedManifest);

/// <summary>One chunk's bytes plus its Merkle inclusion proof — self-verifying regardless of whether it arrived from the original sender or a relaying peer.</summary>
public sealed record ChunkDataMessage(
    byte[] SessionId,
    int FileIndex,
    int ChunkIndex,
    byte[] Payload,
    MerkleProof Proof);

/// <summary>A receiver's chunk-bitmap broadcast: doubles as free peer discovery on the desktop multicast tier (see wiki/concepts/repair-protocol.md).</summary>
public sealed record PeerHaveMessage(
    byte[] SessionId,
    byte[] ReceiverId,
    int FileIndex,
    byte[] ChunkBitmap,
    string EndpointHost,
    int EndpointPort);

/// <summary>A targeted repair request for specific missing chunk indices, addressed to a peer or (fallback) the original sender.</summary>
public sealed record ChunkRequestMessage(
    byte[] SessionId,
    byte[] RequesterId,
    byte[] RequestNonce,
    int FileIndex,
    int[] ChunkIndices,
    string ReturnHost,
    int ReturnPort);

/// <summary>A repair reply, matched to its request via <see cref="RequestNonce"/>. Same self-verifying shape as <see cref="ChunkDataMessage"/>.</summary>
public sealed record ChunkResponseMessage(
    byte[] SessionId,
    byte[] RequestNonce,
    int FileIndex,
    int ChunkIndex,
    byte[] Payload,
    MerkleProof Proof);

public enum TransferOutcome : byte
{
    Completed = 1,
    Failed = 2,
}

/// <summary>Status telemetry a receiver may emit when a transfer finishes (successfully or not).</summary>
public sealed record TransferCompleteMessage(
    byte[] SessionId,
    byte[] ReceiverId,
    TransferOutcome Outcome);
