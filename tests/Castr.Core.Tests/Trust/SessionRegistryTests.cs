using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Trust;

/// <summary>
/// Session-id binding: a session id a receiver has accepted must keep meaning the same transfer. Nothing
/// enforced this before — the id was length-checked and otherwise taken on faith, which was safe only because
/// the shipped CLI happens to mint a fresh random one per invocation.
/// </summary>
public class SessionRegistryTests
{
    private static readonly ISystemClock Clock = new FakeClock(DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ReusedSessionId_ForADifferentTransfer_IsRefused()
    {
        // The failure this exists to stop. The session id is ContentKeyWrap's HKDF salt and every chunk's AEAD
        // domain separator, so two transfers sharing one id derive the same wrapping key from the same X25519
        // pair and hand the same (nonce, key) coordinates to different plaintext.
        using var key = ManifestSigner.CreateSigningKey();
        var sessionId = SessionId(0xAB);
        var first = SignedFixture(key, sessionId, "first.bin", 4096);
        var second = SignedFixture(key, sessionId, "second.bin", 8192); // same id, different transfer

        var registry = new InMemorySessionRegistry();
        var store = TrustedStoreFor(key);

        var accepted = await EvaluateAsync(first, store, registry);
        Assert.Equal(ManifestAdmissionOutcome.Accepted, accepted.Outcome);

        var conflicting = await EvaluateAsync(second, store, registry);
        Assert.Equal(ManifestAdmissionOutcome.SessionIdConflict, conflicting.Outcome);
        Assert.Null(conflicting.Decision); // not a trust event — nothing is wrong with the sender
    }

    [Fact]
    public async Task SameSessionId_SameTransfer_IsStillAccepted()
    {
        // Reuse is the normal case, not the exceptional one: a resume, a re-announce, and a second peer relaying
        // the same transfer all present the same id for the same manifest. A registry that refused those would
        // break every resumable path in the system.
        using var key = ManifestSigner.CreateSigningKey();
        var sessionId = SessionId(0x11);
        var manifest = SignedFixture(key, sessionId, "resumed.bin", 4096);

        var registry = new InMemorySessionRegistry();
        var store = TrustedStoreFor(key);

        for (int attempt = 0; attempt < 3; attempt++)
            Assert.Equal(ManifestAdmissionOutcome.Accepted, (await EvaluateAsync(manifest, store, registry)).Outcome);

        Assert.Single(registry.All()); // one binding, not three
    }

    [Fact]
    public async Task ADeniedManifest_DoesNotBurnItsSessionId()
    {
        // If a rejected manifest bound its session id, any sender holding a valid Ed25519 key could lock a
        // legitimate transfer out of an id it was about to use — turning this check into a denial-of-service
        // primitive. Only accepted manifests record.
        using var untrusted = ManifestSigner.CreateSigningKey();
        using var trusted = ManifestSigner.CreateSigningKey();
        var sessionId = SessionId(0x77);

        var registry = new InMemorySessionRegistry();
        var store = TrustedStoreFor(trusted); // the other key is unknown

        var denied = await EvaluateAsync(SignedFixture(untrusted, sessionId, "hostile.bin", 4096), store, registry);
        Assert.Equal(ManifestAdmissionOutcome.Denied, denied.Outcome);
        Assert.Empty(registry.All());

        var legitimate = await EvaluateAsync(SignedFixture(trusted, sessionId, "real.bin", 4096), store, registry);
        Assert.Equal(ManifestAdmissionOutcome.Accepted, legitimate.Outcome);
    }

    [Fact]
    public async Task DifferentSenderReusingAnAcceptedSessionId_IsRefused()
    {
        using var first = ManifestSigner.CreateSigningKey();
        using var second = ManifestSigner.CreateSigningKey();
        var sessionId = SessionId(0x5A);

        var registry = new InMemorySessionRegistry();
        var store = new InMemoryTrustStore();
        Trust(store, first);
        Trust(store, second); // both trusted — this is about identity of the transfer, not of the sender

        Assert.Equal(ManifestAdmissionOutcome.Accepted,
            (await EvaluateAsync(SignedFixture(first, sessionId, "a.bin", 4096), store, registry)).Outcome);
        Assert.Equal(ManifestAdmissionOutcome.SessionIdConflict,
            (await EvaluateAsync(SignedFixture(second, sessionId, "a.bin", 4096), store, registry)).Outcome);
    }

    [Fact]
    public async Task NoRegistrySupplied_LeavesAdmissionExactlyAsItWas()
    {
        using var key = ManifestSigner.CreateSigningKey();
        var sessionId = SessionId(0x02);
        var store = TrustedStoreFor(key);

        Assert.Equal(ManifestAdmissionOutcome.Accepted,
            (await EvaluateAsync(SignedFixture(key, sessionId, "x.bin", 4096), store, registry: null)).Outcome);
        Assert.Equal(ManifestAdmissionOutcome.Accepted,
            (await EvaluateAsync(SignedFixture(key, sessionId, "y.bin", 8192), store, registry: null)).Outcome);
    }

    [Fact]
    public void InMemoryRegistry_EvictsOldestBeyondCapacity()
    {
        // The registry must not become the leak it exists to prevent. Eviction weakens the guarantee visibly —
        // an evicted id reads as fresh again — rather than growing without bound.
        var registry = new InMemorySessionRegistry(capacity: 4);
        var senderId = PublicKeyId.FromRawEd25519(new byte[32]);
        var digest = ChunkHash.Compute([1]);

        for (byte i = 0; i < 10; i++)
            registry.Record(SessionId(i), senderId, digest, DateTimeOffset.UnixEpoch);

        Assert.Equal(4, registry.All().Count);
        Assert.Equal(SessionAdmission.Fresh, registry.Classify(SessionId(0), senderId, ChunkHash.Compute([2])));   // evicted
        Assert.Equal(SessionAdmission.Conflict, registry.Classify(SessionId(9), senderId, ChunkHash.Compute([2]))); // retained
    }

    [Fact]
    public void FileRegistry_RemembersBindingsAcrossProcesses()
    {
        // The property that makes this worth having at all: the CLI runs one transfer per invocation, so a
        // registry that died with the process would classify every id as fresh and enforce nothing.
        var directory = Path.Combine(Path.GetTempPath(), "castr-session-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "seen-sessions.json");
            var senderId = PublicKeyId.FromRawEd25519(new byte[32]);
            var digest = ChunkHash.Compute([7]);

            new FileSessionRegistry(path).Record(SessionId(0x33), senderId, digest, DateTimeOffset.UnixEpoch);

            var reopened = new FileSessionRegistry(path);
            Assert.Equal(SessionAdmission.SameTransfer, reopened.Classify(SessionId(0x33), senderId, digest));
            Assert.Equal(SessionAdmission.Conflict, reopened.Classify(SessionId(0x33), senderId, ChunkHash.Compute([8])));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileRegistry_WithAnUnreadableFile_StartsEmptyRatherThanThrowing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "castr-session-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "seen-sessions.json");
            File.WriteAllText(path, "{ this is not json");

            var registry = new FileSessionRegistry(path);

            Assert.Empty(registry.All());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileRegistry_ConcurrentWritersToOneFile_DoNotThrow()
    {
        // Found by M12a's fan-out harness, not by review. The registry path is derived from the trust store's
        // DIRECTORY, so several receivers on one host share one file; the old fixed "<path>.tmp" temp name made
        // them collide, File.WriteAllText threw IOException straight out of Record, and that unwound manifest
        // admission — the receiver sat at 0/0 chunks with nothing logged. Exactly two of three same-host
        // receivers completed, repeatably.
        var directory = Path.Combine(Path.GetTempPath(), "castr-session-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "seen-sessions.json");
            var senderId = PublicKeyId.FromRawEd25519(new byte[32]);
            var digest = ChunkHash.Compute([7]);

            // Separate instances, as separate processes would be — each with its own in-memory state and its
            // own view of the file.
            var writers = Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
            {
                var registry = new FileSessionRegistry(path);
                for (int i = 0; i < 40; i++)
                    registry.Record(SessionId((byte)(writer * 40 + i)), senderId, digest, DateTimeOffset.UnixEpoch);
            })).ToArray();

            await Task.WhenAll(writers); // the assertion: no writer faults

            // And the file that survives the race is still a readable registry, not a truncated one.
            Assert.NotEmpty(new FileSessionRegistry(path).All());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp")); // no temp files left behind
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileRegistry_WhenTheFileCannotBeReplaced_RecordsInMemoryRatherThanThrowing()
    {
        // A persistence failure must degrade enforcement, never fail the transfer: this file is a memory of
        // session ids already seen, and Record runs inside manifest admission on the receive path.
        var directory = Path.Combine(Path.GetTempPath(), "castr-session-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "seen-sessions.json");
            var senderId = PublicKeyId.FromRawEd25519(new byte[32]);
            var digest = ChunkHash.Compute([7]);
            var registry = new FileSessionRegistry(path);

            using (File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                registry.Record(SessionId(0x51), senderId, digest, DateTimeOffset.UnixEpoch);
            }

            Assert.Equal(SessionAdmission.SameTransfer, registry.Classify(SessionId(0x51), senderId, digest));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    // ---- harness ----

    private static Task<ManifestAdmissionResult> EvaluateAsync(
        SignedManifest manifest, ITrustStore store, ISessionRegistry? registry) =>
        ManifestAdmission.EvaluateAsync(
            manifest, store, Clock, UnknownSenderPolicy.Deny, isInteractive: false, trustPrompt: null,
            CancellationToken.None, registry);

    private static SignedManifest SignedFixture(NSec.Cryptography.Key key, byte[] sessionId, string relativePath, int size)
    {
        using var encryptionKey = EncryptionKeys.Create();
        var manifest = new TransferManifest(
            sessionId, "session-registry-fixture", DateTimeOffset.UnixEpoch,
            EncryptionKeys.ExportPublicKey(encryptionKey),
            [new ManifestFileEntry(relativePath, size, 4096, (size + 4095) / 4096, ChunkHash.Compute([(byte)size]))]);
        return ManifestSigner.Sign(manifest, key);
    }

    private static ITrustStore TrustedStoreFor(NSec.Cryptography.Key key)
    {
        var store = new InMemoryTrustStore();
        Trust(store, key);
        return store;
    }

    private static void Trust(ITrustStore store, NSec.Cryptography.Key key)
    {
        var publicKey = key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        store.Upsert(new TrustEntry(
            PublicKeyId.FromRawEd25519(publicKey), "sender", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
    }

    private static byte[] SessionId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
