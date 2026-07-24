using System.Security.Cryptography;
using Castr.Cli;
using Castr.Core.Security;
using Castr.Core.Trust;
using Spectre.Console.Testing;

namespace Castr.Cli.Tests;

/// <summary>
/// The load-bearing contract from Core QA: the trust prompt must resolve to <c>false</c> on any failure and
/// must never let an exception escape — otherwise ReceiverSession.RunAsync propagates it instead of denying.
/// </summary>
public class ConsoleTrustPromptTests
{
    private static TrustPromptContext Context() =>
        new(PublicKeyId.FromRawEd25519(RandomNumberGenerator.GetBytes(32)), "some-transfer", 1, 1234);

    [Fact]
    public async Task CancelledToken_ResolvesToFalse_WithoutThrowing()
    {
        var prompt = new ConsoleTrustPrompt(new TestConsole());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(await prompt.RequestTrustAsync(Context(), cts.Token));
    }

    [Fact]
    public async Task NonInteractiveConsoleWithNoInput_ResolvesToFalse_WithoutThrowing()
    {
        // A TestConsole with no queued input would make Confirm throw; the wrapper must swallow it and deny.
        var prompt = new ConsoleTrustPrompt(new TestConsole());
        Assert.False(await prompt.RequestTrustAsync(Context(), CancellationToken.None));
    }

    [Fact]
    public async Task InteractiveYes_ResolvesToTrue()
    {
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("y");
        var prompt = new ConsoleTrustPrompt(console);

        Assert.True(await prompt.RequestTrustAsync(Context(), CancellationToken.None));
    }

    [Fact]
    public async Task InteractiveNo_ResolvesToFalse()
    {
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("n");
        var prompt = new ConsoleTrustPrompt(console);

        Assert.False(await prompt.RequestTrustAsync(Context(), CancellationToken.None));
    }
}
