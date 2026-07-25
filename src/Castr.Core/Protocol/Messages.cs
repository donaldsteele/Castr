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
    JoinRequest = 8,
    KeyGrant = 9,
    PacketFragment = 10,
    ChunkPacket = 11,
    ManifestRequest = 12,
    ChunkPullRequest = 13,
    ChunkPullResponse = 14,
    KeyUnavailable = 15,
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

/// <summary>
/// A receiver's request for the per-transfer content key, sent once it has verified and trusted the sender's
/// signed manifest. Carries the receiver's own X25519 public key so the sender can wrap the content key for
/// it. New in wiki/synthesis/adr-0003-payload-encryption.md.
/// </summary>
public sealed record JoinRequestMessage(
    byte[] SessionId,
    byte[] ReceiverId,
    byte[] ReceiverEncryptionPublicKey);

/// <summary>
/// The sender's reply to a JOIN_REQUEST: the per-transfer content key, wrapped (ChaCha20-Poly1305) under a
/// key derived from X25519(sender, receiver) + HKDF-SHA256. Only the addressed receiver can unwrap it. New in
/// wiki/synthesis/adr-0003-payload-encryption.md.
/// </summary>
public sealed record KeyGrantMessage(
    byte[] SessionId,
    byte[] ReceiverId,
    byte[] WrappedContentKey);

/// <summary>One chunk's <b>ciphertext</b> (ChaCha20-Poly1305 under the per-transfer content key) plus its Merkle inclusion proof over that ciphertext — self-verifying regardless of whether it arrived from the original sender or a relaying peer, and unreadable to anyone who never obtained the content key.</summary>
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

/// <summary>A repair reply, matched to its request via <see cref="RequestNonce"/>. Same self-verifying ciphertext shape as <see cref="ChunkDataMessage"/> — a relaying peer forwards ciphertext it need not itself be able to read.</summary>
public sealed record ChunkResponseMessage(
    byte[] SessionId,
    byte[] RequestNonce,
    int FileIndex,
    int ChunkIndex,
    byte[] Payload,
    MerkleProof Proof);

/// <summary>
/// One MTU-safe slice of a larger wire message (see <see cref="WirePacketizer"/>). All fragments of one
/// logical message share a random <see cref="GroupId"/>; <see cref="PacketIndex"/> orders them and
/// <see cref="PacketCount"/> tells the receiver how many to expect. <see cref="TotalLength"/> is the length of
/// the reassembled original message, used to size the reassembly buffer and validate completeness. This is a
/// pure transport wrapper — it carries opaque encoded-message bytes and is oblivious to chunk semantics,
/// encryption, and Merkle proofs.
/// </summary>
public sealed record PacketFragmentMessage(
    long GroupId,
    int PacketIndex,
    int PacketCount,
    int TotalLength,
    byte[] Fragment);

/// <summary>
/// One MTU-safe wire packet of a single chunk's <b>ciphertext</b> — the transport-granularity half of the
/// two-level chunking in wiki/concepts/wire-protocol.md, used when a chunk's ciphertext is too large to ride
/// in one datagram. Unlike the generic <see cref="PacketFragmentMessage"/>, this is keyed by chunk identity
/// (<see cref="FileIndex"/>/<see cref="ChunkIndex"/>) and carries only the deterministic ciphertext, so
/// every retransmission of a chunk — the original carousel send and every repair re-send from the sender or a
/// relaying peer — produces byte-identical packets. That lets a receiver <b>accumulate</b> a chunk's packets
/// across repair rounds instead of needing all of them to survive a single round: essential for large chunks,
/// where independent per-packet loss would otherwise make a whole-chunk round statistically impossible.
/// The Merkle inclusion proof (over the whole ciphertext) rides only on packet 0; a chunk cannot complete
/// without every packet, so packet 0 — and thus the proof — is always present at reassembly.
/// </summary>
public sealed record ChunkPacketMessage(
    byte[] SessionId,
    int FileIndex,
    int ChunkIndex,
    int PacketIndex,
    int PacketCount,
    int CiphertextLength,
    byte[] Fragment,
    MerkleProof? Proof);

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

// ---- unicast-swarm (mobile TCP pull) messages ----
// These have no multicast-tier equivalent: multicast broadcasts the manifest and chunks unprompted on a
// carousel, whereas a swarm-pull client over a point-to-point TCP stream must explicitly ASK for each thing.
// The reply shapes intentionally omit the multicast repair artifacts (RequestNonce, ReturnHost/Port) that a
// directed request/response over an ordered stream does not need.

/// <summary>A swarm-pull client's opening ask: "what transfer are you serving?" The server replies with its <see cref="ManifestMessage"/>. Carries no fields — the client has not yet learned the session id.</summary>
public sealed record ManifestRequestMessage;

/// <summary>A swarm-pull client's request for specific chunk indices of one file over a TCP stream. The server replies with exactly one <see cref="ChunkPullResponseMessage"/> per requested index, in the same order.</summary>
public sealed record ChunkPullRequestMessage(
    byte[] SessionId,
    int FileIndex,
    int[] ChunkIndices);

/// <summary>
/// A server's reply to one requested chunk index. <see cref="Found"/> false means "I don't hold that chunk
/// (yet)" — <see cref="Payload"/> is empty and <see cref="Proof"/> null; the client leaves it missing and
/// tries another peer. When found, the same self-verifying (ciphertext, Merkle proof) pair as the multicast
/// tier, so a relaying receiver forwards ciphertext it need not itself be able to read.
/// </summary>
public sealed record ChunkPullResponseMessage(
    byte[] SessionId,
    int FileIndex,
    int ChunkIndex,
    bool Found,
    byte[] Payload,
    MerkleProof? Proof);

/// <summary>
/// A server's reply to a JOIN_REQUEST when it cannot grant the content key. Only the original sender holds the
/// X25519 private key bound in the signed manifest, so a relaying <b>receiver</b> peer can serve ciphertext
/// chunks but not the key — it answers with this so the client stops waiting for a KEY_GRANT and obtains the
/// key from the sender instead.
/// </summary>
public sealed record KeyUnavailableMessage(
    byte[] SessionId,
    byte[] ReceiverId);
