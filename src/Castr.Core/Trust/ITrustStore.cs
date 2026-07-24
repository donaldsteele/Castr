using Castr.Core.Security;

namespace Castr.Core.Trust;

public interface ITrustStore
{
    TrustEntry? Find(PublicKeyId publicKeyId);

    void Upsert(TrustEntry entry);

    void Remove(PublicKeyId publicKeyId);

    IReadOnlyList<TrustEntry> All();
}
