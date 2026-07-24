using System.Net;
using Spectre.Console;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;
using Castr.Tui;

namespace Castr.Cli;

internal sealed record SendOptions(
    string FilePath,
    IPAddress Group,
    int Port,
    string? InterfaceName,
    int ChunkSize,
    string IdentityPath,
    bool UseTui,
    bool MulticastLoopback = true);

/// <summary>
/// Drives one real <see cref="Castr.Core.Protocol.SenderSession"/> over UDP multicast. Factored out of the
/// command wiring so it can be invoked directly, in-process, by the end-to-end tests. A sender has no terminal
/// "complete" state — it keeps serving repair requests — so this runs until <paramref name="cancellationToken"/>
/// is cancelled (Ctrl+C in real use).
/// </summary>
internal static class SendRunner
{
    public static async Task<int> RunAsync(SendOptions options, IAnsiConsole console, CancellationToken cancellationToken)
    {
        if (options.ChunkSize > CastrPaths.MaxChunkSize)
        {
            int cipherSize = options.ChunkSize + CastrPaths.ChaCha20Poly1305TagBytes;
            console.MarkupLineInterpolated(
                $"[red]Chunk size {options.ChunkSize} bytes would produce a {options.ChunkSize}+{CastrPaths.ChaCha20Poly1305TagBytes}={cipherSize}-byte encrypted payload, exceeding the safe UDP datagram limit of ~{CastrPaths.MaxSafeUdpPayloadBytes} bytes.[/]");
            console.MarkupLineInterpolated(
                $"[red]Use --chunk-size <= {CastrPaths.MaxChunkSize} until Castr.Core's transport packetizes large chunks (tracked for M3).[/]");
            return ExitCodes.InvalidInput;
        }

        if (!File.Exists(options.FilePath))
        {
            console.MarkupLineInterpolated($"[red]File not found:[/] {options.FilePath}");
            return ExitCodes.InvalidInput;
        }

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

        using var identity = SenderIdentity.LoadOrCreate(options.IdentityPath);

        var reporter = new ConsoleProgressReporter(console, "send");
        if (!options.UseTui)
        {
            reporter.Line($"identity {identity.PublicKeyId.Value}");
            reporter.Line($"serving {Path.GetFileName(options.FilePath)} on {options.Group}:{options.Port} (share the id above so receivers can trust you)");
        }

        try
        {
            using var prepared = await TransferPreparation
                .PrepareFileAsync(options.FilePath, identity.SigningKey, options.ChunkSize, cancellationToken)
                .ConfigureAwait(false);

            await using IMulticastTransport transport =
                new UdpMulticastTransport(options.Group, options.Port, interfaceAddress, options.MulticastLoopback);
            var session = prepared.CreateSession(transport);

            if (options.UseTui)
            {
                var dashboard = new TransferDashboard();
                var render = dashboard.RunAsync(session, cancellationToken);
                var run = session.RunAsync(cancellationToken);
                await Task.WhenAll(render, run).ConfigureAwait(false);
            }
            else
            {
                session.ProgressChanged += reporter.OnProgress;
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            reporter.Line("stopped.");
            return ExitCodes.Success; // a cancelled sender is a normal, deliberate shutdown
        }
        catch (Exception ex)
        {
            console.MarkupLineInterpolated($"[red]Send failed:[/] {ex.Message}");
            return ExitCodes.RuntimeError;
        }
    }
}
