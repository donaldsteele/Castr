using Castr.Core.Security;

namespace Castr.Core.Trust;

public enum TrustStatus
{
    Trusted,
    Blocked,
}

public enum TrustEntrySource
{
    PreDeployed,
    Manual,
}

public sealed record TrustEntry(
    PublicKeyId PublicKeyId,
    string DisplayName,
    TrustStatus Status,
    DateTimeOffset AddedAt,
    TrustEntrySource Source);
