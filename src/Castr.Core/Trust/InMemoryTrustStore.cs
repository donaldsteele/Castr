using Castr.Core.Security;

namespace Castr.Core.Trust;

public sealed class InMemoryTrustStore : ITrustStore
{
    private readonly Dictionary<PublicKeyId, TrustEntry> _entries = [];

    public TrustEntry? Find(PublicKeyId publicKeyId) => _entries.GetValueOrDefault(publicKeyId);

    public void Upsert(TrustEntry entry) => _entries[entry.PublicKeyId] = entry;

    public void Remove(PublicKeyId publicKeyId) => _entries.Remove(publicKeyId);

    public IReadOnlyList<TrustEntry> All() => [.. _entries.Values];
}
