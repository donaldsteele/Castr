using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Castr.Bench.Sniffer;

/// <summary>
/// A passive, read-only multicast sniffer for Castr benchmark runs. It joins a group, never sends a single
/// byte, and counts datagrams and payload bytes broken down by Castr message type.
///
/// <para>It knows exactly one thing about the protocol: every datagram begins
/// <c>[FormatVersion:1][MessageType:1]</c>. It links against no Castr assembly, so a wire-composition figure
/// it reports cannot be an artifact of the product code agreeing with itself. Earlier campaigns
/// (M8, M9 — see docs/benchmarks/throughput-runs.md) used an uncommitted ad-hoc build of exactly this; M12a
/// committed it so a wire-amplification row can be reproduced rather than re-written.</para>
///
/// <para><b>The sniffer is not free.</b> Joining the group on the measurement host adds another kernel
/// multicast fan-out copy per datagram, which on loopback is the dominant per-datagram cost. Run wire-
/// composition arms and wall-clock arms <i>separately</i> and never quote a goodput number from a sniffer
/// run.</para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// Per-frame Ethernet overhead used for the on-wire byte model: 8 preamble/SFD + 14 Ethernet header +
    /// 20 IPv4 header + 8 UDP header + 4 FCS + 12 inter-frame gap. This is the same preamble+IFG-inclusive
    /// model the M9 link-partner diagnosis matched to 0.07%, so a number here is comparable with that one.
    /// </summary>
    private const int EthernetFrameOverheadBytes = 8 + 14 + 20 + 8 + 4 + 12;

    /// <summary>
    /// 16 MiB — deliberately 4x the transport's own <c>SocketBufferBytes</c>. The sniffer must never be the
    /// thing that drops a datagram, or it under-reports exactly the traffic a fan-out run is measuring.
    /// </summary>
    private const int ReceiveBufferBytes = 16 * 1024 * 1024;

    private static readonly string[] TypeNames =
    [
        /* 0 */ "UNKNOWN_0",
        /* 1 */ "ANNOUNCE",
        /* 2 */ "MANIFEST",
        /* 3 */ "CHUNK_DATA",
        /* 4 */ "PEER_HAVE",
        /* 5 */ "CHUNK_REQUEST",
        /* 6 */ "CHUNK_RESPONSE",
        /* 7 */ "TRANSFER_COMPLETE",
        /* 8 */ "JOIN_REQUEST",
        /* 9 */ "KEY_GRANT",
        /* 10 */ "PACKET_FRAGMENT",
        /* 11 */ "CHUNK_PACKET",
        /* 12 */ "MANIFEST_REQUEST",
        /* 13 */ "CHUNK_PULL_REQUEST",
        /* 14 */ "CHUNK_PULL_RESPONSE",
        /* 15 */ "KEY_UNAVAILABLE",
    ];

    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        IPAddress? interfaceAddress;
        try
        {
            interfaceAddress = options.InterfaceName is null ? null : ResolveInterface(options.InterfaceName);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        using var stopSignal = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // Ctrl-C means "finish and report", not "die"
            stopSignal.Cancel();
        };

        var counts = new long[256];
        var bytes = new long[256];
        long total = 0, totalBytes = 0, foreignVersion = 0, runt = 0;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, ReceiveBufferBytes); }
        catch (SocketException) { /* best-effort, same as the transport */ }
        socket.Bind(new IPEndPoint(IPAddress.Any, options.Port));
        socket.SetSocketOption(
            SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(options.Group, interfaceAddress ?? IPAddress.Any));

        Console.Error.WriteLine(
            $"sniffing {options.Group}:{options.Port} on {options.InterfaceName ?? "(default)"} — " +
            $"idle-exit {options.IdleMilliseconds} ms, max {options.MaxSeconds} s");

        var buffer = new byte[65_507];
        var wall = Stopwatch.StartNew();
        long firstTicks = -1, lastTicks = -1;
        var deadline = TimeSpan.FromSeconds(options.MaxSeconds);

        while (!stopSignal.IsCancellationRequested && wall.Elapsed < deadline)
        {
            // Poll rather than block: a plain blocking Receive cannot notice the idle deadline, and the
            // idle deadline is what lets a harness treat sniffer exit as "the transfer went quiet".
            if (!socket.Poll(50_000 /* 50 ms */, SelectMode.SelectRead))
            {
                if (firstTicks >= 0 && wall.ElapsedMilliseconds - (lastTicks / TimeSpan.TicksPerMillisecond) > options.IdleMilliseconds)
                    break;
                continue;
            }

            int received;
            try
            {
                received = socket.Receive(buffer, SocketFlags.None);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                break;
            }

            lastTicks = wall.Elapsed.Ticks;
            if (firstTicks < 0)
                firstTicks = lastTicks;

            total++;
            totalBytes += received;

            if (received < 2)
            {
                runt++;
                continue;
            }

            // Byte 0 is the format version, byte 1 the message type. Anything else is deliberately NOT
            // decoded: a sniffer that parsed bodies would have to be kept in step with the codec, which is
            // precisely the coupling this tool exists to avoid.
            if (options.FormatVersion is byte expected && buffer[0] != expected)
                foreignVersion++;

            byte type = buffer[1];
            counts[type]++;
            bytes[type] += received;
        }

        var span = firstTicks < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(lastTicks - firstTicks);
        var report = BuildReport(options, counts, bytes, total, totalBytes, foreignVersion, runt, span);

        Console.Error.WriteLine();
        Console.Error.WriteLine(RenderTable(report));

        if (options.OutputPath is not null)
        {
            var json = JsonSerializer.Serialize(report, SnifferJsonContext.Default.SnifferReport);
            File.WriteAllText(options.OutputPath, json);
            Console.Error.WriteLine($"wrote {options.OutputPath}");
        }

        return total == 0 ? 1 : 0;
    }

    private static SnifferReport BuildReport(
        Options options, long[] counts, long[] bytes, long total, long totalBytes,
        long foreignVersion, long runt, TimeSpan span)
    {
        var byType = new List<TypeRow>();
        for (int type = 0; type < counts.Length; type++)
        {
            if (counts[type] == 0)
                continue;
            byType.Add(new TypeRow(
                Type: type,
                Name: type < TypeNames.Length ? TypeNames[type] : $"UNKNOWN_{type}",
                Datagrams: counts[type],
                PayloadBytes: bytes[type],
                MeanPayloadBytes: Math.Round((double)bytes[type] / counts[type], 1)));
        }
        byType.Sort((a, b) => b.Datagrams.CompareTo(a.Datagrams));

        return new SnifferReport(
            Group: options.Group.ToString(),
            Port: options.Port,
            Interface: options.InterfaceName,
            SpanSeconds: Math.Round(span.TotalSeconds, 3),
            TotalDatagrams: total,
            TotalPayloadBytes: totalBytes,
            EthernetWireBytes: totalBytes + (total * EthernetFrameOverheadBytes),
            ForeignFormatVersionDatagrams: foreignVersion,
            RuntDatagrams: runt,
            ByType: byType);
    }

    private static string RenderTable(SnifferReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"span {report.SpanSeconds:F3} s   datagrams {report.TotalDatagrams:N0}   payload {report.TotalPayloadBytes:N0} B   ethernet {report.EthernetWireBytes:N0} B");
        sb.AppendLine("type                  datagrams        payload bytes   mean B");
        foreach (var row in report.ByType)
            sb.AppendLine(CultureInfo.InvariantCulture, $"{row.Name,-20} {row.Datagrams,12:N0} {row.PayloadBytes,20:N0} {row.MeanPayloadBytes,8:F1}");
        if (report.ForeignFormatVersionDatagrams > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"WARNING: {report.ForeignFormatVersionDatagrams:N0} datagrams carried an unexpected format version — is another Castr build on this group?");
        if (report.RuntDatagrams > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"WARNING: {report.RuntDatagrams:N0} datagrams were shorter than the 2-byte prefix — foreign traffic on this group.");
        return sb.ToString();
    }

    private static IPAddress ResolveInterface(string name)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(nic.Name, name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(nic.Description, name, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    return unicast.Address;
            }
        }
        var available = string.Join(", ", NetworkInterface.GetAllNetworkInterfaces().Select(n => $"'{n.Name}'"));
        throw new ArgumentException($"No IPv4 address found for interface '{name}'. Available: {available}");
    }

    private sealed record Options(
        IPAddress Group,
        int Port,
        string? InterfaceName,
        int IdleMilliseconds,
        int MaxSeconds,
        string? OutputPath,
        byte? FormatVersion,
        bool ShowHelp)
    {
        public const string Usage = """
            castr-sniff — passive read-only Castr multicast wire counter

              --group <ip>          multicast group to join (default 239.192.55.55)
              --port <n>            UDP port (default 45055)
              --interface <name>    NIC name to join on. ALWAYS pass this in a benchmark: leaked
                                    memberships make the default group's interface ambiguous.
              --idle-ms <n>         exit after this long with no datagram, once traffic has started (default 5000)
              --max-seconds <n>     hard cap on run length (default 900)
              --format-version <n>  warn on datagrams not carrying this version byte (default: no check)
              --out <path>          write the report as JSON here
              --help
            """;

        public static Options Parse(string[] args)
        {
            var group = IPAddress.Parse("239.192.55.55");
            int port = 45055, idleMs = 5000, maxSeconds = 900;
            string? iface = null, outPath = null;
            byte? formatVersion = null;
            bool help = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next(string name) => i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"{name} requires a value.");

                switch (arg)
                {
                    case "--group": group = IPAddress.Parse(Next(arg)); break;
                    case "--port": port = int.Parse(Next(arg), CultureInfo.InvariantCulture); break;
                    case "--interface": iface = Next(arg); break;
                    case "--idle-ms": idleMs = int.Parse(Next(arg), CultureInfo.InvariantCulture); break;
                    case "--max-seconds": maxSeconds = int.Parse(Next(arg), CultureInfo.InvariantCulture); break;
                    case "--format-version": formatVersion = byte.Parse(Next(arg), CultureInfo.InvariantCulture); break;
                    case "--out": outPath = Next(arg); break;
                    case "--help" or "-h": help = true; break;
                    default: throw new ArgumentException($"Unrecognised argument '{arg}'.");
                }
            }

            return new Options(group, port, iface, idleMs, maxSeconds, outPath, formatVersion, help);
        }
    }
}

internal sealed record TypeRow(int Type, string Name, long Datagrams, long PayloadBytes, double MeanPayloadBytes);

internal sealed record SnifferReport(
    string Group,
    int Port,
    string? Interface,
    double SpanSeconds,
    long TotalDatagrams,
    long TotalPayloadBytes,
    long EthernetWireBytes,
    long ForeignFormatVersionDatagrams,
    long RuntDatagrams,
    IReadOnlyList<TypeRow> ByType);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(SnifferReport))]
internal sealed partial class SnifferJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
