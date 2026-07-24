namespace Castr.Core.Trust;

public enum TrustOutcome
{
    /// <summary>Sender is explicitly trusted — proceed.</summary>
    Trusted,

    /// <summary>Sender is explicitly blocked — always rejected, regardless of policy.</summary>
    Blocked,

    /// <summary>Unknown sender, policy says deny (or Prompt was requested but no interactive surface was available).</summary>
    DeniedUnknown,

    /// <summary>Unknown sender, offer held for later human review.</summary>
    QueuedForApproval,

    /// <summary>Unknown sender, an interactive surface should prompt the user now.</summary>
    PromptRequired,
}

public sealed record TrustDecision(TrustOutcome Outcome, TrustEntry? ExistingEntry)
{
    public bool ShouldProceed => Outcome == TrustOutcome.Trusted;
}
