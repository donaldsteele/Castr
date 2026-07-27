using Castr.Core.Chunking;
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

    /// <summary>
    /// Signature valid, but the manifest is not structurally well-formed — see <see cref="ManifestLimits"/>.
    /// Distinct from <see cref="SignatureInvalid"/>: the sender really did sign this, which is the point.
    /// </summary>
    Malformed,

    /// <summary>
    /// Signature valid, but this session id is already bound to a different transfer — see
    /// <see cref="ISessionRegistry"/> for why that is refused rather than tolerated. Distinct from
    /// <see cref="Denied"/>: nothing is wrong with the sender's trust status, so it raises no trust event.
    /// </summary>
    SessionIdConflict,

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
    /// <summary>
    /// Runs the gate. <paramref name="sessionRegistry"/> is optional: when supplied, the manifest's session id
    /// must not already be bound to a <i>different</i> transfer, and an accepted manifest binds it. When null no
    /// session-id binding is enforced at all — every composition root supplies one, and the parameter is
    /// nullable only so unit tests that are not about session identity need not construct a registry.
    /// </summary>
    public static async Task<ManifestAdmissionResult> EvaluateAsync(
        SignedManifest signedManifest,
        ITrustStore trustStore,
        ISystemClock clock,
        UnknownSenderPolicy unknownSenderPolicy,
        bool isInteractive,
        ITrustPrompt? trustPrompt,
        CancellationToken cancellationToken,
        ISessionRegistry? sessionRegistry = null)
    {
        if (!ManifestVerifier.VerifySignature(signedManifest))
            return new ManifestAdmissionResult(ManifestAdmissionOutcome.SignatureInvalid, null);

        // Structural bounds before anything acts on the manifest's numbers. Being signed makes a manifest
        // authentic, not well-formed, and the receive loop does not wrap manifest handling — so an out-of-range
        // ChunkSize used to reach ChunkPacketAssembler and throw out of the loop. See ManifestLimits.
        if (!ManifestLimits.IsWellFormed(signedManifest.Manifest))
            return new ManifestAdmissionResult(ManifestAdmissionOutcome.Malformed, null);

        var senderId = signedManifest.SenderId;

        // Checked before trust is evaluated, so a conflicting id is refused without consulting — or teaching
        // anything to — the sender presenting it. Recorded only on acceptance, so an untrusted sender cannot
        // burn a session id a legitimate transfer is about to use.
        var digest = ManifestDigest(signedManifest);
        if (sessionRegistry?.Classify(signedManifest.Manifest.SessionId, senderId, digest) == SessionAdmission.Conflict)
            return new ManifestAdmissionResult(ManifestAdmissionOutcome.SessionIdConflict, null);

        var decision = TrustDecisionEngine.Evaluate(senderId, trustStore, unknownSenderPolicy, isInteractive);
        if (decision.ShouldProceed)
        {
            sessionRegistry?.Record(signedManifest.Manifest.SessionId, senderId, digest, clock.UtcNow);
            return new ManifestAdmissionResult(ManifestAdmissionOutcome.Accepted, decision);
        }

        if (await TryPromptForTrustAsync(decision, signedManifest, senderId, trustStore, clock, trustPrompt, cancellationToken).ConfigureAwait(false))
        {
            sessionRegistry?.Record(signedManifest.Manifest.SessionId, senderId, digest, clock.UtcNow);
            return new ManifestAdmissionResult(ManifestAdmissionOutcome.Accepted, decision);
        }

        return new ManifestAdmissionResult(ManifestAdmissionOutcome.Denied, decision);
    }

    /// <summary>
    /// The identity of the transfer a session id is bound to: a digest over the canonical manifest encoding, the
    /// same bytes the sender's Ed25519 signature covers. Using the signed encoding rather than a subset means
    /// two manifests are "the same transfer" exactly when the signature says they are.
    /// </summary>
    public static ChunkHash ManifestDigest(SignedManifest signedManifest) =>
        ChunkHash.Compute(ManifestCodec.Encode(signedManifest.Manifest));

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
