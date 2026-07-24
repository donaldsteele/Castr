using Castr.Core.Security;

namespace Castr.Core.Trust;

/// <summary>
/// Pure policy logic, deliberately separated from I/O: given a trust-store lookup and a policy, decide
/// what should happen for a sender. Blocked always wins over Trusted-by-policy-default, and Trusted/Blocked
/// entries always short-circuit the unknown-sender policy entirely.
/// </summary>
public static class TrustDecisionEngine
{
    public static TrustDecision Evaluate(
        PublicKeyId senderId, ITrustStore store, UnknownSenderPolicy policy, bool isInteractive)
    {
        var entry = store.Find(senderId);

        if (entry is not null)
        {
            return entry.Status == TrustStatus.Blocked
                ? new TrustDecision(TrustOutcome.Blocked, entry)
                : new TrustDecision(TrustOutcome.Trusted, entry);
        }

        return policy switch
        {
            UnknownSenderPolicy.Prompt when isInteractive => new TrustDecision(TrustOutcome.PromptRequired, null),
            UnknownSenderPolicy.Queue => new TrustDecision(TrustOutcome.QueuedForApproval, null),
            // Deny, or Prompt requested with no interactive surface to actually ask through: fail safe, not open.
            _ => new TrustDecision(TrustOutcome.DeniedUnknown, null),
        };
    }
}
