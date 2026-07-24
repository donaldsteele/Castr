using Castr.Core.Security;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Trust;

public class TrustSeedMergerTests
{
    private static readonly PublicKeyId KeyA = PublicKeyId.FromRawEd25519(Fill(1));
    private static readonly PublicKeyId KeyB = PublicKeyId.FromRawEd25519(Fill(2));

    [Fact]
    public void FirstRun_EmptyStore_AllSeedEntriesAreAdded()
    {
        var store = new InMemoryTrustStore();
        var seed = new[]
        {
            SeedEntry(KeyA, TrustStatus.Trusted),
            SeedEntry(KeyB, TrustStatus.Blocked),
        };

        var added = TrustSeedMerger.Merge(store, seed);

        Assert.Equal(2, added.Count);
        Assert.Equal(TrustStatus.Trusted, store.Find(KeyA)!.Status);
        Assert.Equal(TrustStatus.Blocked, store.Find(KeyB)!.Status);
        Assert.All(store.All(), e => Assert.Equal(TrustEntrySource.PreDeployed, e.Source));
    }

    [Fact]
    public void ExistingLocalEntry_IsNeverOverwrittenBySeed()
    {
        var store = new InMemoryTrustStore();
        store.Upsert(new TrustEntry(KeyA, "locally-blocked-by-operator", TrustStatus.Blocked, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));

        // Seed file (perhaps from a newer package release) now claims this same key should be trusted.
        var added = TrustSeedMerger.Merge(store, [SeedEntry(KeyA, TrustStatus.Trusted)]);

        Assert.Empty(added);
        Assert.Equal(TrustStatus.Blocked, store.Find(KeyA)!.Status); // local operator decision wins
        Assert.Equal(TrustEntrySource.Manual, store.Find(KeyA)!.Source);
    }

    [Fact]
    public void ReRunningMergeWithSameSeed_IsIdempotent()
    {
        var store = new InMemoryTrustStore();
        var seed = new[] { SeedEntry(KeyA, TrustStatus.Trusted) };

        TrustSeedMerger.Merge(store, seed);
        var secondRunAdded = TrustSeedMerger.Merge(store, seed);

        Assert.Empty(secondRunAdded);
        Assert.Single(store.All());
    }

    private static TrustEntry SeedEntry(PublicKeyId id, TrustStatus status) =>
        new(id, "seed-entry", status, DateTimeOffset.UnixEpoch, TrustEntrySource.PreDeployed);

    private static byte[] Fill(byte value) => Enumerable.Repeat(value, 32).ToArray();
}
