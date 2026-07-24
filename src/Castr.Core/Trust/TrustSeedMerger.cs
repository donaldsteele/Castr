namespace Castr.Core.Trust;

/// <summary>
/// Merges a pre-deployment seed file into a receiver's local trust store on first run. The local store
/// is authoritative afterward: re-merging the same (or an updated) seed never overwrites an entry the
/// store already has, even if the seed's status for that key later changes.
/// </summary>
public static class TrustSeedMerger
{
    /// <returns>The seed entries that were actually added (i.e. did not already exist in <paramref name="store"/>).</returns>
    public static IReadOnlyList<TrustEntry> Merge(ITrustStore store, IEnumerable<TrustEntry> seedEntries)
    {
        var added = new List<TrustEntry>();
        foreach (var seedEntry in seedEntries)
        {
            if (store.Find(seedEntry.PublicKeyId) is not null)
                continue; // local store already has an opinion about this key — never clobber it

            var entry = seedEntry with { Source = TrustEntrySource.PreDeployed };
            store.Upsert(entry);
            added.Add(entry);
        }
        return added;
    }
}
