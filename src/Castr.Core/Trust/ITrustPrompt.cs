using Castr.Core.Security;

namespace Castr.Core.Trust;

/// <summary>
/// Context handed to an <see cref="ITrustPrompt"/> so a human has enough to decide whether to trust a
/// previously-unknown sender: who is asking (<see cref="SenderId"/>) and what they are offering.
/// </summary>
/// <param name="SenderId">The sender's Ed25519 trust identity (the value that will be persisted if accepted).</param>
/// <param name="TransferName">Human-readable transfer name from the signed manifest.</param>
/// <param name="FileCount">Number of files in the offered transfer.</param>
/// <param name="TotalBytes">Total plaintext size of the offered transfer.</param>
public sealed record TrustPromptContext(
    PublicKeyId SenderId,
    string TransferName,
    int FileCount,
    long TotalBytes);

/// <summary>
/// A side-effecting capability injected into <see cref="Protocol.ReceiverSession"/> — the seam through which
/// the three M2 UI surfaces (CLI/TUI/GUI) ask a human to make a Trust-On-First-Use decision, without
/// <c>Castr.Core</c> ever doing console/UI I/O itself. Consulted only when
/// <see cref="TrustDecisionEngine"/> returns <see cref="TrustOutcome.PromptRequired"/> (i.e. an unknown
/// sender under an interactive <see cref="UnknownSenderPolicy.Prompt"/> policy). Returning <c>true</c>
/// persists a <see cref="TrustStatus.Trusted"/> entry and proceeds; <c>false</c> denies exactly as an
/// untrusted sender. Implementations must be non-throwing under normal use; a thrown exception propagates
/// out of the receive loop.
/// </summary>
public interface ITrustPrompt
{
    Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken);
}
