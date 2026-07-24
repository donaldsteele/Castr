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

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
