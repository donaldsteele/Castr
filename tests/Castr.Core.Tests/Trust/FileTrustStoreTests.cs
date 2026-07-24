using Castr.Core.Security;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Trust;

public class FileTrustStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("castr-trust-tests-").FullName;

    [Fact]
    public void Upsert_PersistsAcrossInstances()
    {
        var path = Path.Combine(_tempDir, "trust.json");
        var key = PublicKeyId.FromRawEd25519(new byte[32]);

        var store1 = new FileTrustStore(path);
        store1.Upsert(new TrustEntry(key, "sender-1", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));

        var store2 = new FileTrustStore(path);
        Assert.Equal(TrustStatus.Trusted, store2.Find(key)!.Status);
    }

    [Fact]
    public void Remove_PersistsAcrossInstances()
    {
        var path = Path.Combine(_tempDir, "trust.json");
        var key = PublicKeyId.FromRawEd25519(new byte[32]);

        var store1 = new FileTrustStore(path);
        store1.Upsert(new TrustEntry(key, "sender-1", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
        store1.Remove(key);

        var store2 = new FileTrustStore(path);
        Assert.Null(store2.Find(key));
    }

    [Fact]
    public void ConstructingOverMissingFile_StartsEmpty_DoesNotThrow()
    {
        var store = new FileTrustStore(Path.Combine(_tempDir, "does-not-exist.json"));
        Assert.Empty(store.All());
    }

    [Fact]
    public void Upsert_DoesNotLeaveTempFileBehind()
    {
        var path = Path.Combine(_tempDir, "trust.json");
        var store = new FileTrustStore(path);

        store.Upsert(new TrustEntry(PublicKeyId.FromRawEd25519(new byte[32]), "x", TrustStatus.Trusted, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));

        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ConstructingOverCorruptJsonFile_Throws_DoesNotSilentlyStartEmpty()
    {
        // Security-relevant fail-closed behavior: silently starting empty over a corrupt store would downgrade
        // previously-BLOCKED senders to "unknown," potentially re-enabling them. It must fail loudly instead.
        var path = Path.Combine(_tempDir, "corrupt.json");
        File.WriteAllText(path, "{ not valid json at all ");

        Assert.ThrowsAny<Exception>(() => new FileTrustStore(path));
    }

    [Fact]
    public void Load_DuplicateConflictingEntries_LastEntryInFileWins()
    {
        // Pins the on-load resolution for a hand-tampered file that lists the same key twice with conflicting
        // status: entries are applied in file order, so the LAST one wins (dictionary upsert). Editing the
        // store file already requires local write access (outside the network threat model), but the behavior
        // should be explicit and stable rather than accidental.
        var key = PublicKeyId.FromRawEd25519(new byte[32]);
        var path = Path.Combine(_tempDir, "dupes.json");
        var json = $$"""
        {"version":1,"entries":[
          {"publicKeyId":"{{key.Value}}","displayName":"a","status":"blocked","addedAt":"2026-01-01T00:00:00Z","source":"manual"},
          {"publicKeyId":"{{key.Value}}","displayName":"b","status":"trusted","addedAt":"2026-01-01T00:00:00Z","source":"manual"}
        ]}
        """;
        File.WriteAllText(path, json);

        var store = new FileTrustStore(path);

        Assert.Single(store.All());
        Assert.Equal(TrustStatus.Trusted, store.Find(key)!.Status); // last entry (trusted) won
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
