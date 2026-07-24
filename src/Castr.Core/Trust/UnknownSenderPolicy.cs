namespace Castr.Core.Trust;

/// <summary>Governs what happens when a sender's public key has no entry in the trust store. Maps directly to the CLI's `--on-unknown-sender=deny|queue|prompt` flag.</summary>
public enum UnknownSenderPolicy
{
    /// <summary>Safe default: reject, log, no transfer proceeds without an explicit trust decision made out-of-band.</summary>
    Deny,

    /// <summary>Hold the offer for later human review rather than rejecting it outright.</summary>
    Queue,

    /// <summary>Ask interactively (TUI/GUI). Falls back to Deny if there is no interactive surface to ask through.</summary>
    Prompt,
}
