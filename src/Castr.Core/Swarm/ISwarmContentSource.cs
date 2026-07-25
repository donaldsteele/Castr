using Castr.Core.Manifest;
using Castr.Core.Protocol;

namespace Castr.Core.Swarm;

/// <summary>One verified chunk exactly as it travels the swarm: deterministic ciphertext plus its Merkle inclusion proof over that ciphertext. Identical shape to what the multicast tier caches and relays.</summary>
public sealed record SwarmChunk(byte[] Ciphertext, MerkleProof Proof);

/// <summary>
/// The read-only capability a swarm participant exposes to <see cref="SwarmServeListener"/> so it can answer
/// incoming unicast TCP pull requests — its manifest, whichever chunks it currently holds and has verified,
/// and (sender only) the content key. Deliberately a narrow, cache-facing view rather than access to a
/// session's private state.
/// <para>
/// Role split, dictated by the existing crypto design (ADR-0003), not a shortcut: only the <b>original
/// sender</b> holds the X25519 private key bound in the signed manifest, so only a sender can wrap the content
/// key for a joining puller (<see cref="TryGrantContentKey"/> returns a grant). A relaying <b>receiver</b>
/// serves ciphertext chunks all the same — a puller verifies each against the signed Merkle root without
/// trusting the peer — but returns <c>null</c> from <see cref="TryGrantContentKey"/>; that puller must obtain
/// the content key from the sender. This mirrors the multicast tier exactly, where KEY_GRANT only ever comes
/// from the sender while any receiver can relay a CHUNK_RESPONSE.
/// </para>
/// </summary>
public interface ISwarmContentSource
{
    /// <summary>The signed manifest being served, or <c>null</c> if this participant has not accepted one yet (e.g. a receiver still evaluating trust).</summary>
    SignedManifest? Manifest { get; }

    /// <summary>Returns the verified (ciphertext, proof) for a chunk this participant currently holds, or <c>null</c> if it doesn't have it (yet).</summary>
    ValueTask<SwarmChunk?> TryGetChunkAsync(int fileIndex, int chunkIndex, CancellationToken cancellationToken = default);

    /// <summary>Wraps the content key for a joining puller (sender only), or <c>null</c> if this participant cannot grant it (any receiver relay).</summary>
    KeyGrantMessage? TryGrantContentKey(JoinRequestMessage request);
}
