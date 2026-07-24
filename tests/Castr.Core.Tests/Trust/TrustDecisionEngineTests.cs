using Castr.Core.Security;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Trust;

public class TrustDecisionEngineTests
{
    private static readonly PublicKeyId SenderA = PublicKeyId.FromRawEd25519(new byte[32]);

    [Fact]
    public void TrustedEntry_AlwaysProceedsRegardlessOfPolicy()
    {
        var store = new InMemoryTrustStore();
        store.Upsert(Entry(SenderA, TrustStatus.Trusted));

        foreach (var policy in Enum.GetValues<UnknownSenderPolicy>())
        {
            var decision = TrustDecisionEngine.Evaluate(SenderA, store, policy, isInteractive: false);
            Assert.Equal(TrustOutcome.Trusted, decision.Outcome);
            Assert.True(decision.ShouldProceed);
        }
    }

    [Fact]
    public void BlockedEntry_AlwaysDeniedRegardlessOfPolicy()
    {
        var store = new InMemoryTrustStore();
        store.Upsert(Entry(SenderA, TrustStatus.Blocked));

        foreach (var policy in Enum.GetValues<UnknownSenderPolicy>())
        {
            foreach (var interactive in new[] { true, false })
            {
                var decision = TrustDecisionEngine.Evaluate(SenderA, store, policy, interactive);
                Assert.Equal(TrustOutcome.Blocked, decision.Outcome);
                Assert.False(decision.ShouldProceed);
            }
        }
    }

    [Fact]
    public void UnknownSender_DenyPolicy_IsDeniedInteractiveOrNot()
    {
        var store = new InMemoryTrustStore();

        Assert.Equal(TrustOutcome.DeniedUnknown, TrustDecisionEngine.Evaluate(SenderA, store, UnknownSenderPolicy.Deny, isInteractive: false).Outcome);
        Assert.Equal(TrustOutcome.DeniedUnknown, TrustDecisionEngine.Evaluate(SenderA, store, UnknownSenderPolicy.Deny, isInteractive: true).Outcome);
    }

    [Fact]
    public void UnknownSender_QueuePolicy_IsQueuedRegardlessOfInteractivity()
    {
        var store = new InMemoryTrustStore();

        Assert.Equal(TrustOutcome.QueuedForApproval, TrustDecisionEngine.Evaluate(SenderA, store, UnknownSenderPolicy.Queue, isInteractive: false).Outcome);
        Assert.Equal(TrustOutcome.QueuedForApproval, TrustDecisionEngine.Evaluate(SenderA, store, UnknownSenderPolicy.Queue, isInteractive: true).Outcome);
    }

    [Fact]
    public void UnknownSender_PromptPolicy_Interactive_RequiresPrompt()
    {
        var store = new InMemoryTrustStore();

        var decision = TrustDecisionEngine.Evaluate(SenderA, store, UnknownSenderPolicy.Prompt, isInteractive: true);

        Assert.Equal(TrustOutcome.PromptRequired, decision.Outcome);
    }

    [Fact]
    public void UnknownSender_PromptPolicy_Headless_FailsSafeToDeny()
    {
        // A misconfigured headless deployment (--on-unknown-sender=prompt with no TUI/GUI attached)
        // must not silently allow anything through — it fails to the safe default, not to open trust.
        var store = new InMemoryTrustStore();

        var decision = TrustDecisionEngine.Evaluate(SenderA, store, UnknownSenderPolicy.Prompt, isInteractive: false);

        Assert.Equal(TrustOutcome.DeniedUnknown, decision.Outcome);
        Assert.False(decision.ShouldProceed);
    }

    [Fact]
    public void NoEntry_DecisionCarriesNullEntry()
    {
        var store = new InMemoryTrustStore();
        var decision = TrustDecisionEngine.Evaluate(SenderA, store, UnknownSenderPolicy.Deny, isInteractive: false);
        Assert.Null(decision.ExistingEntry);
    }

    private static TrustEntry Entry(PublicKeyId id, TrustStatus status) =>
        new(id, "test-sender", status, DateTimeOffset.UnixEpoch, TrustEntrySource.Manual);
}
