using Castr.Core.Manifest;
using Castr.Core.Security;
using Castr.Core.Time;

namespace Castr.Core.Trust;

/// <summary>Outcome of running a signed manifest through signature verification and the TOFU trust flow.</summary>
public enum ManifestAdmissionOutcome
{
    /// <summary>The Ed25519 signature did not verify — forged or corrupt. Drop it silently (no trust event).</summary>
    SignatureInvalid,

    /// <summary>Signature valid but the sender is not trusted (and no interactive prompt accepted them).</summary>
    Denied,

    /// <summary>Signature valid and the sender is trusted — proceed with the transfer.</summary>
    Accepted,
}

/// <summary>Result of <see cref="ManifestAdmission.EvaluateAsync"/>: the outcome plus the trust decision that produced it (for <see cref="ManifestAdmissionOutcome.Denied"/> reporting).</summary>
public sealed record ManifestAdmissionResult(ManifestAdmissionOutcome Outcome, TrustDecision? Decision);

/// <summary>
/// The single, shared "should I accept this signed manifest?" gate: verify the sender's Ed25519 signature,
/// then run the sender through <see cref="TrustDecisionEngine"/> and — for an unknown sender under an
/// interactive Prompt policy — the <see cref="ITrustPrompt"/> Trust-On-First-Use flow, persisting a trusted
/// entry if the human accepts. Extracted so the multicast <c>ReceiverSession</c> and the unicast-swarm
/// <c>SwarmPullSession</c> run <b>exactly the same</b> admission logic against a manifest a peer hands them,
/// rather than maintaining two divergent copies of a security-critical decision. Pure of transport and I/O
/// beyond the injected store/prompt/clock.
/// </summary>
public static class ManifestAdmission
{
    public static async Task<ManifestAdmissionResult> EvaluateAsync(
        SignedManifest signedManifest,
        ITrustStore trustStore,
        ISystemClock clock,
        UnknownSenderPolicy unknownSenderPolicy,
        bool isInteractive,
        ITrustPrompt? trustPrompt,
        CancellationToken cancellationToken)
    {
        if (!ManifestVerifier.VerifySignature(signedManifest))
            return new ManifestAdmissionResult(ManifestAdmissionOutcome.SignatureInvalid, null);

        var senderId = signedManifest.SenderId;
        var decision = TrustDecisionEngine.Evaluate(senderId, trustStore, unknownSenderPolicy, isInteractive);
        if (decision.ShouldProceed)
            return new ManifestAdmissionResult(ManifestAdmissionOutcome.Accepted, decision);

        if (await TryPromptForTrustAsync(decision, signedManifest, senderId, trustStore, clock, trustPrompt, cancellationToken).ConfigureAwait(false))
            return new ManifestAdmissionResult(ManifestAdmissionOutcome.Accepted, decision);

        return new ManifestAdmissionResult(ManifestAdmissionOutcome.Denied, decision);
    }

    /// <summary>
    /// When trust evaluation asked for an interactive decision (<see cref="TrustOutcome.PromptRequired"/>) and
    /// an <see cref="ITrustPrompt"/> was supplied, consult it. If the human accepts, persist a
    /// <see cref="TrustStatus.Trusted"/> entry (TOFU) and return true. In every other case — no prompt supplied,
    /// a non-prompt denial, or the human rejecting — return false so the caller denies exactly as before.
    /// </summary>
    private static async Task<bool> TryPromptForTrustAsync(
        TrustDecision decision,
        SignedManifest signedManifest,
        PublicKeyId senderId,
        ITrustStore trustStore,
        ISystemClock clock,
        ITrustPrompt? trustPrompt,
        CancellationToken cancellationToken)
    {
        if (decision.Outcome != TrustOutcome.PromptRequired || trustPrompt is null)
            return false;

        var manifest = signedManifest.Manifest;
        long totalBytes = manifest.Files.Sum(f => f.Size);
        var context = new TrustPromptContext(senderId, manifest.TransferName, manifest.Files.Count, totalBytes);

        if (!await trustPrompt.RequestTrustAsync(context, cancellationToken).ConfigureAwait(false))
            return false;

        var displayName = string.IsNullOrEmpty(manifest.TransferName) ? senderId.Value : manifest.TransferName;
        trustStore.Upsert(new TrustEntry(senderId, displayName, TrustStatus.Trusted, clock.UtcNow, TrustEntrySource.Manual));
        return true;
    }
}
