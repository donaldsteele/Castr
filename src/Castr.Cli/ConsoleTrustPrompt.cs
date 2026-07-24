using Spectre.Console;
using Castr.Core.Trust;

namespace Castr.Cli;

/// <summary>
/// The CLI's interactive Trust-On-First-Use surface: asks a yes/no question about an unknown sender via
/// <see cref="AnsiConsole"/>. Consulted only under <c>--on-unknown-sender=prompt</c> in interactive mode.
/// <para>
/// <b>Critical contract (per Core QA):</b> if this method throws or is cancelled mid-prompt,
/// <see cref="Castr.Core.Protocol.ReceiverSession.RunAsync"/> propagates the exception instead of denying
/// gracefully. So every failure — cancellation, a non-interactive console that can't read input, any
/// exception at all — is caught internally and resolved to <c>false</c> (deny). An exception never escapes.
/// </para>
/// </summary>
internal sealed class ConsoleTrustPrompt(IAnsiConsole console) : ITrustPrompt
{
    public Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromResult(false);

            console.WriteLine();
            console.MarkupLineInterpolated($"[yellow]Unknown sender wants to send you a transfer:[/]");
            console.MarkupLineInterpolated($"  Sender id : {context.SenderId.Value}");
            console.MarkupLineInterpolated($"  Transfer  : {context.TransferName}");
            console.MarkupLineInterpolated($"  Files     : {context.FileCount}");
            console.MarkupLineInterpolated($"  Size      : {context.TotalBytes} bytes");

            bool trusted = console.Confirm("Trust this sender and accept the transfer?", defaultValue: false);
            return Task.FromResult(trusted);
        }
        catch
        {
            // Never let anything escape: a thrown/cancelled prompt would propagate out of the receive loop.
            return Task.FromResult(false);
        }
    }
}
