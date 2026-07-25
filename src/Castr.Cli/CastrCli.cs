using System.CommandLine;
using System.Net;
using Spectre.Console;
using Castr.Core.Protocol;
using Castr.Core.Trust;

namespace Castr.Cli;

/// <summary>
/// Builds the <c>castr</c> command tree (send / receive / trust) on System.CommandLine 2.0. Kept separate
/// from <c>Program</c> so tests can parse args against the same tree the executable uses.
/// </summary>
internal static class CastrCli
{
    public static RootCommand BuildRootCommand(IAnsiConsole? console = null)
    {
        var output = console ?? AnsiConsole.Console;
        var root = new RootCommand("castr — LAN multicast file transfer");
        root.Subcommands.Add(BuildSendCommand(output));
        root.Subcommands.Add(BuildReceiveCommand(output));
        root.Subcommands.Add(BuildTrustCommand(output));
        return root;
    }

    // ---- shared option factories (fresh instances per command) ----

    private static Option<IPAddress> GroupOption() => new("--group", "-g")
    {
        Description = "Administratively-scoped IPv4 multicast group.",
        DefaultValueFactory = _ => CastrPaths.DefaultGroupAddress,
        CustomParser = result =>
        {
            var token = result.Tokens.Count == 0 ? null : result.Tokens[0].Value;
            if (token is null)
                return CastrPaths.DefaultGroupAddress;
            if (IPAddress.TryParse(token, out var ip))
                return ip;
            result.AddError($"'{token}' is not a valid IP address.");
            return CastrPaths.DefaultGroupAddress;
        },
    };

    private static Option<int> PortOption() => new("--port", "-p")
    {
        Description = "UDP port for the multicast group.",
        DefaultValueFactory = _ => CastrPaths.DefaultPort,
    };

    private static Option<string?> InterfaceOption() => new("--interface", "-i")
    {
        Description = "Network interface (NIC) name to bind multicast to. Auto-selected when omitted.",
    };

    private static Option<bool> TuiOption() => new("--tui")
    {
        Description = "Render a live Spectre.Console dashboard instead of plain progress lines.",
    };

    // ---- send ----

    private static Command BuildSendCommand(IAnsiConsole console)
    {
        var fileArg = new Argument<string>("file") { Description = "Path to the file to send." };
        var group = GroupOption();
        var port = PortOption();
        var iface = InterfaceOption();
        var tui = TuiOption();
        var chunkSize = new Option<int>("--chunk-size")
        {
            Description = "Chunk size in bytes (hash/repair granularity).",
            DefaultValueFactory = _ => CastrPaths.DefaultChunkSize,
        };
        var identity = new Option<string>("--identity")
        {
            Description = "Path to the persistent Ed25519 sender identity key (created if absent).",
            DefaultValueFactory = _ => CastrPaths.DefaultIdentityPath,
        };
        var sendWindowSize = new Option<int>("--send-window-size")
        {
            Description = "Max concurrent in-flight chunk sends (1 = today's sequential behavior). " +
                "The safe value is receiver-hardware/network-dependent — see SenderSession.DefaultSendWindowSize's " +
                "doc comment before raising this above the default.",
            DefaultValueFactory = _ => SenderSession.DefaultSendWindowSize,
        };

        var command = new Command("send", "Send a file over multicast.")
        {
            fileArg, group, port, iface, tui, chunkSize, identity, sendWindowSize,
        };
        command.SetAction((parseResult, ct) => SendRunner.RunAsync(
            new SendOptions(
                parseResult.GetValue(fileArg)!,
                parseResult.GetValue(group)!,
                parseResult.GetValue(port),
                parseResult.GetValue(iface),
                parseResult.GetValue(chunkSize),
                parseResult.GetValue(identity)!,
                parseResult.GetValue(tui),
                SendWindowSize: parseResult.GetValue(sendWindowSize)),
            console, ct));
        return command;
    }

    // ---- receive ----

    private static Command BuildReceiveCommand(IAnsiConsole console)
    {
        var destDir = new Option<string>("--dest-dir", "-d")
        {
            Description = "Destination directory for received files (path-safety enforced).",
            DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
        };
        var onUnknown = new Option<UnknownSenderPolicy>("--on-unknown-sender")
        {
            Description = "What to do with a sender not in the trust store: deny | queue | prompt.",
            DefaultValueFactory = _ => UnknownSenderPolicy.Deny,
        };
        var trustStore = new Option<string>("--trust-store")
        {
            Description = "Path to the persistent trust store JSON file.",
            DefaultValueFactory = _ => CastrPaths.DefaultTrustStorePath,
        };
        var trustSeed = new Option<string?>("--trust-seed")
        {
            Description = "Pre-deployment trust seed to merge on startup. Defaults to a conventional per-user location if present.",
        };
        var group = GroupOption();
        var port = PortOption();
        var iface = InterfaceOption();
        var tui = TuiOption();

        var command = new Command("receive", "Listen for and receive a transfer.")
        {
            destDir, onUnknown, trustStore, trustSeed, group, port, iface, tui,
        };
        command.SetAction((parseResult, ct) =>
        {
            var seed = parseResult.GetValue(trustSeed);
            // If the user did not pass --trust-seed, fall back to the conventional per-user seed when it exists.
            if (string.IsNullOrEmpty(seed) && File.Exists(CastrPaths.DefaultTrustSeedPath))
                seed = CastrPaths.DefaultTrustSeedPath;

            return ReceiveRunner.RunAsync(
                new ReceiveOptions(
                    parseResult.GetValue(destDir)!,
                    parseResult.GetValue(group)!,
                    parseResult.GetValue(port),
                    parseResult.GetValue(iface),
                    parseResult.GetValue(onUnknown),
                    parseResult.GetValue(trustStore)!,
                    seed,
                    parseResult.GetValue(tui)),
                console, ct);
        });
        return command;
    }

    // ---- trust ----

    private static Command BuildTrustCommand(IAnsiConsole console)
    {
        var command = new Command("trust", "Inspect and manage the local trust store.");
        command.Subcommands.Add(BuildTrustList(console));
        command.Subcommands.Add(BuildTrustMutation("add", "Trust a sender by public key id.", TrustStatus.Trusted, console));
        command.Subcommands.Add(BuildTrustMutation("block", "Block a sender by public key id.", TrustStatus.Blocked, console));
        command.Subcommands.Add(BuildTrustRemove(console));
        return command;
    }

    private static Option<string> TrustStoreOption() => new("--trust-store")
    {
        Description = "Path to the persistent trust store JSON file.",
        DefaultValueFactory = _ => CastrPaths.DefaultTrustStorePath,
    };

    private static Command BuildTrustList(IAnsiConsole console)
    {
        var trustStore = TrustStoreOption();
        var command = new Command("list", "List trust store entries.") { trustStore };
        command.SetAction(parseResult => TrustRunner.List(parseResult.GetValue(trustStore)!, console));
        return command;
    }

    private static Command BuildTrustMutation(string name, string description, TrustStatus status, IAnsiConsole console)
    {
        var idArg = new Argument<string>("public-key-id") { Description = "Sender id, e.g. ed25519:<base64>." };
        var displayName = new Option<string?>("--name") { Description = "Optional display name for the entry." };
        var trustStore = TrustStoreOption();
        var command = new Command(name, description) { idArg, displayName, trustStore };
        command.SetAction(parseResult => TrustRunner.Add(
            parseResult.GetValue(trustStore)!,
            parseResult.GetValue(idArg)!,
            parseResult.GetValue(displayName),
            status,
            console));
        return command;
    }

    private static Command BuildTrustRemove(IAnsiConsole console)
    {
        var idArg = new Argument<string>("public-key-id") { Description = "Sender id to remove." };
        var trustStore = TrustStoreOption();
        var command = new Command("remove", "Remove a trust store entry.") { idArg, trustStore };
        command.SetAction(parseResult => TrustRunner.Remove(
            parseResult.GetValue(trustStore)!,
            parseResult.GetValue(idArg)!,
            console));
        return command;
    }
}
