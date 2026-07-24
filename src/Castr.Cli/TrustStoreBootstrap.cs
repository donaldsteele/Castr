using Castr.Core.Trust;

namespace Castr.Cli;

/// <summary>Opens the persistent <see cref="FileTrustStore"/> and merges a pre-deployment seed on first run.</summary>
internal static class TrustStoreBootstrap
{
    /// <summary>
    /// Loads (or creates) the file-backed trust store at <paramref name="trustStorePath"/>. If
    /// <paramref name="seedPath"/> exists, its entries are merged via <see cref="TrustSeedMerger"/> — the local
    /// store stays authoritative, so an already-known key is never clobbered.
    /// </summary>
    /// <returns>The store and the number of seed entries newly added (0 if none / no seed file).</returns>
    public static (FileTrustStore Store, int Merged) Load(string trustStorePath, string? seedPath)
    {
        var store = new FileTrustStore(trustStorePath);
        int merged = 0;

        if (!string.IsNullOrEmpty(seedPath) && File.Exists(seedPath))
        {
            var seedEntries = TrustStoreJsonCodec.Decode(File.ReadAllText(seedPath));
            merged = TrustSeedMerger.Merge(store, seedEntries).Count;
        }

        return (store, merged);
    }
}
