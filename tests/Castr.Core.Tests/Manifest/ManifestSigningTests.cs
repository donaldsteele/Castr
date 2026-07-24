using Castr.Core.Chunking;
using Castr.Core.Manifest;

namespace Castr.Core.Tests.Manifest;

public class ManifestSigningTests
{
    [Fact]
    public void Sign_ThenVerify_Succeeds()
    {
        using var key = ManifestSigner.CreateSigningKey();
        var manifest = SampleManifest();

        var signed = ManifestSigner.Sign(manifest, key);

        Assert.True(ManifestVerifier.VerifySignature(signed));
    }

    [Fact]
    public void Verify_TamperedManifestContent_Fails()
    {
        using var key = ManifestSigner.CreateSigningKey();
        var signed = ManifestSigner.Sign(SampleManifest(), key);

        var tampered = signed with { Manifest = signed.Manifest with { TransferName = "evil.exe" } };

        Assert.False(ManifestVerifier.VerifySignature(tampered));
    }

    [Fact]
    public void Verify_TamperedFileEntry_Fails()
    {
        using var key = ManifestSigner.CreateSigningKey();
        var signed = ManifestSigner.Sign(SampleManifest(), key);

        var tamperedFiles = signed.Manifest.Files.ToList();
        tamperedFiles[0] = tamperedFiles[0] with { Size = tamperedFiles[0].Size + 1 };
        var tampered = signed with { Manifest = signed.Manifest with { Files = tamperedFiles } };

        Assert.False(ManifestVerifier.VerifySignature(tampered));
    }

    [Fact]
    public void Verify_SignatureFromDifferentKey_Fails()
    {
        using var realKey = ManifestSigner.CreateSigningKey();
        using var attackerKey = ManifestSigner.CreateSigningKey();

        var signedByReal = ManifestSigner.Sign(SampleManifest(), realKey);
        var attackerPublicKey = attackerKey.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        // Attacker swaps in their own public key alongside the real signature — must not verify.
        var spoofed = signedByReal with { SenderPublicKey = attackerPublicKey };

        Assert.False(ManifestVerifier.VerifySignature(spoofed));
    }

    [Fact]
    public void Verify_SwappedSenderEncryptionKey_Fails()
    {
        // MITM defense (ADR-0003): the sender's X25519 encryption public key is carried INSIDE the signed
        // manifest, so an active attacker cannot substitute their own encryption key to hijack the
        // JOIN_REQUEST/KEY_GRANT handshake without invalidating the Ed25519 signature.
        using var key = ManifestSigner.CreateSigningKey();
        var signed = ManifestSigner.Sign(SampleManifest(), key);

        var attackerEncryptionKey = Enumerable.Repeat((byte)0x42, TransferManifest.EncryptionPublicKeySize).ToArray();
        var tampered = signed with { Manifest = signed.Manifest with { SenderEncryptionPublicKey = attackerEncryptionKey } };

        Assert.False(ManifestVerifier.VerifySignature(tampered));
    }

    [Fact]
    public void Verify_AlteredIssuedAt_Fails()
    {
        // The manifest's issued-at timestamp is covered by the signature. An attacker replaying an old
        // legitimate manifest therefore cannot forge a fresh timestamp on it to defeat a freshness check
        // (see the replay-protection note in wire-protocol.md) without breaking the signature.
        using var key = ManifestSigner.CreateSigningKey();
        var signed = ManifestSigner.Sign(SampleManifest(), key);

        var tampered = signed with { Manifest = signed.Manifest with { IssuedAt = signed.Manifest.IssuedAt.AddYears(5) } };

        Assert.False(ManifestVerifier.VerifySignature(tampered));
    }

    [Fact]
    public void Verify_GarbagePublicKeyBytes_FailsWithoutThrowing()
    {
        using var key = ManifestSigner.CreateSigningKey();
        var signed = ManifestSigner.Sign(SampleManifest(), key);

        var corrupted = signed with { SenderPublicKey = new byte[3] };

        Assert.False(ManifestVerifier.VerifySignature(corrupted));
    }

    [Fact]
    public void SenderId_MatchesPublicKeyIdFormat()
    {
        using var key = ManifestSigner.CreateSigningKey();
        var signed = ManifestSigner.Sign(SampleManifest(), key);

        Assert.StartsWith("ed25519:", signed.SenderId.ToString());
    }

    [Fact]
    public void VerifyChunk_ValidProofAgainstFileRoot_Succeeds()
    {
        var chunkHashes = new[]
        {
            ChunkHash.Compute("chunk-0"u8),
            ChunkHash.Compute("chunk-1"u8),
            ChunkHash.Compute("chunk-2"u8),
        };
        var tree = MerkleTree.Build(chunkHashes);
        var proof = tree.GetProof(1);

        Assert.True(ManifestVerifier.VerifyChunk(tree.Root, chunkHashes[1], proof));
    }

    [Fact]
    public void VerifyChunk_ChunkFromUntrustedRelayPeer_StillVerifiesAgainstSignedRoot()
    {
        // Simulates the repair scenario: the manifest (and its Merkle root) came from a trusted, signed
        // sender, but this specific chunk's bytes arrived from an arbitrary, untrusted peer during repair.
        using var key = ManifestSigner.CreateSigningKey();
        var chunkHashes = new[] { ChunkHash.Compute("a"u8), ChunkHash.Compute("b"u8), ChunkHash.Compute("c"u8), ChunkHash.Compute("d"u8) };
        var tree = MerkleTree.Build(chunkHashes);
        var manifest = new TransferManifest(
            new byte[16], "repair-test.bin", DateTimeOffset.UtcNow, SampleEncryptionKey(),
            [new ManifestFileEntry("repair-test.bin", 4000, 1000, 4, tree.Root)]);
        var signed = ManifestSigner.Sign(manifest, key);

        Assert.True(ManifestVerifier.VerifySignature(signed)); // sender is authenticated once

        // Now an untrusted peer relays chunk 2 plus its proof; receiver never needs to trust the peer.
        var relayedChunkHash = chunkHashes[2];
        var relayedProof = tree.GetProof(2);
        Assert.True(ManifestVerifier.VerifyChunk(signed.Manifest.Files[0].MerkleRoot, relayedChunkHash, relayedProof));

        // But a peer handing over the WRONG bytes for that index must fail verification.
        var wrongChunkHash = ChunkHash.Compute("not-actually-chunk-2"u8);
        Assert.False(ManifestVerifier.VerifyChunk(signed.Manifest.Files[0].MerkleRoot, wrongChunkHash, relayedProof));
    }

    private static TransferManifest SampleManifest() => new(
        SessionId: Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
        TransferName: "photos.zip",
        IssuedAt: DateTimeOffset.Parse("2026-07-24T12:00:00Z"),
        SenderEncryptionPublicKey: SampleEncryptionKey(),
        Files: [new ManifestFileEntry("photos/beach.jpg", 4_500_000, 262_144, 18, ChunkHash.Compute("root-a"u8))]);

    private static byte[] SampleEncryptionKey() => Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray();
}
