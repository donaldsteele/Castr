using System.Net;
using System.Security.Cryptography;
using Spectre.Console;
using Castr.Core.Chunking;
using Castr.Core.Protocol;
using Castr.Core.Security;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;
using Castr.Core.Trust;
using Castr.Tui;

namespace Castr.Cli;

internal sealed record ReceiveOptions(
    string DestinationDirectory,
    IPAddress Group,
    int Port,
    string? InterfaceName,
    UnknownSenderPolicy Policy,
    string TrustStorePath,
    string? TrustSeedPath,
    bool UseTui,
    bool MulticastLoopback = true);

/// <summary>
/// Drives one real <see cref="ReceiverSession"/> over UDP multicast until the transfer completes, the sender
/// is denied, or cancellation. Runs the session and a periodic repair loop concurrently (re-driving the
/// JOIN/KEY_GRANT handshake and re-requesting missing chunks), mirroring the GUI's receive flow.
/// </summary>
internal static class ReceiveRunner
{
    public static async Task<int> RunAsync(ReceiveOptions options, IAnsiConsole console, CancellationToken cancellationToken)
    {
        IPAddress? interfaceAddress;
        try
        {
            interfaceAddress = options.InterfaceName is null ? null : NetworkInterfaces.Resolve(options.InterfaceName);
        }
        catch (InvalidInterfaceException ex)
        {
            console.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return ExitCodes.InvalidInput;
        }

        string destination;
        try
        {
            destination = Path.GetFullPath(options.DestinationDirectory);
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            console.MarkupLineInterpolated($"[red]Invalid destination directory:[/] {ex.Message}");
            return ExitCodes.InvalidInput;
        }

        var (trustStore, merged) = TrustStoreBootstrap.Load(options.TrustStorePath, options.TrustSeedPath);

        var reporter = new ConsoleProgressReporter(console, "recv");
        if (!options.UseTui)
        {
            if (merged > 0)
                reporter.Line($"merged {merged} seed entr(y/ies) into the trust store");
            reporter.Line($"listening on {options.Group}:{options.Port}, policy={options.Policy}, dest={destination}");
        }

        // Prompting only makes sense in interactive, non-TUI mode (the live dashboard owns the console).
        bool interactive = options.Policy == UnknownSenderPolicy.Prompt && !options.UseTui;
        var sessionOptions = new ReceiverSessionOptions(destination, options.Policy, IsInteractive: interactive);
        var receiverId = RandomNumberGenerator.GetBytes(16);
        var trustPrompt = interactive ? new ConsoleTrustPrompt(console) : null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool trustDenied = false;

        try
        {
            await using IMulticastTransport transport =
                new UdpMulticastTransport(options.Group, options.Port, interfaceAddress, options.MulticastLoopback);

            var session = new ReceiverSession(
                receiverId, trustStore, transport, SystemClock.Instance, sessionOptions,
                sinkFactory: (path, length) => new FileSystemFileSink(path, length),
                trustPrompt: trustPrompt,
                // BENCH (temporary M7 instrumentation): -1 unless CASTR_BENCH_MAXDGRAM is set, so this is
                // WirePacketizer.DefaultMaxDatagramPayload in every normal run.
                maxDatagramPayloadBytes: Castr.Core.Diagnostics.BenchMetrics.MaxDatagramOverride > 0
                    ? Castr.Core.Diagnostics.BenchMetrics.MaxDatagramOverride
                    : WirePacketizer.DefaultMaxDatagramPayload);

            session.SenderTrustDenied += (decision, senderId) =>
            {
                trustDenied = true;
                reporter.Line($"trust denied for {senderId.Value} ({decision.Outcome}).");
                linked.Cancel(); // the session keeps listening after a denial; stop it deliberately
            };

            if (options.UseTui)
            {
                var dashboard = new TransferDashboard();
                var render = dashboard.RunAsync(session, linked.Token);
                await RunSessionAsync(session, linked.Token).ConfigureAwait(false);
                await Swallow(render).ConfigureAwait(false);
            }
            else
            {
                session.ProgressChanged += reporter.OnProgress;
                await RunSessionAsync(session, linked.Token).ConfigureAwait(false);
            }

            if (session.IsComplete)
            {
                reporter.Line("transfer complete.");
                return ExitCodes.Success;
            }

            return trustDenied ? ExitCodes.TrustDenied : ExitCodes.Incomplete;
        }
        catch (OperationCanceledException)
        {
            return trustDenied ? ExitCodes.TrustDenied : ExitCodes.Incomplete;
        }
        catch (Exception ex)
        {
            console.MarkupLineInterpolated($"[red]Receive failed:[/] {ex.Message}");
            return ExitCodes.RuntimeError;
        }
    }

    /// <summary>Runs the receive loop and a periodic repair loop concurrently until completion or cancellation.</summary>
    private static async Task RunSessionAsync(ReceiverSession session, CancellationToken token)
    {
        var run = session.RunAsync(token);
        var repairs = RunRepairLoopAsync(session, token);
        await Task.WhenAll(run, repairs).ConfigureAwait(false);
    }

    private static async Task RunRepairLoopAsync(ReceiverSession session, CancellationToken token)
    {
        // BENCH (temporary M7 instrumentation): both default to the shipped values (0 / 250), so this is
        // shipped behavior unless CASTR_BENCH_REPAIR_START_MS / CASTR_BENCH_REPAIR_PERIOD_MS are set. Used to
        // A/B the repair loop's contribution to wire amplification.
        int startMs = int.TryParse(Environment.GetEnvironmentVariable("CASTR_BENCH_REPAIR_START_MS"), out var s) ? s : 0;
        int periodMs = int.TryParse(Environment.GetEnvironmentVariable("CASTR_BENCH_REPAIR_PERIOD_MS"), out var p) ? p : 250;
        try
        {
            // Polled rather than one long Delay so a completed transfer still ends this task promptly.
            for (int waited = 0; waited < startMs && !token.IsCancellationRequested && !session.IsComplete; waited += 100)
                await Task.Delay(100, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested && !session.IsComplete)
            {
                await session.RequestRepairsAsync(token).ConfigureAwait(false);
                await Task.Delay(periodMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private static async Task Swallow(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
