using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;

namespace Castr.Bench.DatagramCeiling;

/// <summary>
/// Measures the number M12a needed and the repository did not have: <b>how many datagrams per second a
/// Castr receiver's transport can sustain before the kernel starts dropping them.</b>
///
/// <para>Two modes, run as two processes:</para>
/// <list type="bullet">
/// <item><c>drain</c> — stands up a real <see cref="UdpMulticastTransport"/> (the shipped socket options,
/// the shipped reader task, the shipped bounded inbox, the shipped per-datagram copy) and does nothing with
/// each datagram but count it. That makes the result an <i>upper bound</i> on any real receiver: a
/// <c>ReceiverSession</c> adds decode, Merkle verification, AEAD open, a disk write and an outbound
/// broadcast on top.</item>
/// <item><c>blast</c> — offers datagrams at a target rate from N independent sockets, each stamping
/// <c>[streamId:1][sequence:8]</c> into the payload.</item>
/// </list>
///
/// <para>Loss is computed from the sequence stamps, not from an OS counter, because
/// <c>netstat -s -p udp</c>'s "Receive Errors" reads 0 on Windows while hundreds of thousands of datagrams
/// are being dropped — recorded in docs/benchmarks/throughput-runs.md and re-confirmed here.</para>
///
/// <para>Both sides must run on the same host for the loopback fan-out path this is designed to
/// characterise. Note that the offered rate is itself capped by the sender's own kernel copy, which is why
/// <c>blast</c> takes a thread count: one thread cannot saturate a receiver on this hardware.</para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "drain" => await DrainAsync(Options.Parse(args[1..])).ConfigureAwait(false),
                "blast" => Blast(Options.Parse(args[1..])),
                _ => Fail($"Unknown mode '{args[0]}'."),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private const string Usage = """
        castr-dgram — receiver datagram-ceiling probe

          castr-dgram drain --group <ip> --port <n> --interface <name> [--idle-ms 3000] [--max-seconds 300] [--out <path>]
          castr-dgram blast --group <ip> --port <n> --interface <name> --size 1472 --rate <datagrams/s|0=unpaced>
                            [--seconds 15] [--threads 1] [--out <path>]

        --rate is the TOTAL offered rate across all threads. 0 means unpaced (offer as fast as the sender can).
        Always pass --interface: leaked memberships make a default group's interface ambiguous.
        """;

    // ---- drain ----

    private static async Task<int> DrainAsync(Options options)
    {
        var interfaceAddress = options.InterfaceName is null ? null : ResolveInterface(options.InterfaceName);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.MaxSeconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        // 256 possible stream ids; a blast thread owns one.
        var received = new long[256];
        var highestSequence = new long[256];
        Array.Fill(highestSequence, -1);

        long total = 0, totalBytes = 0, malformed = 0;
        var wall = Stopwatch.StartNew();
        long firstMs = -1, lastMs = -1;

        await using IMulticastTransport transport = new UdpMulticastTransport(
            options.Group, options.Port, interfaceAddress, multicastLoopback: true);

        Console.Error.WriteLine($"draining {options.Group}:{options.Port} on {options.InterfaceName ?? "(default)"}");

        var idle = Task.Run(async () =>
        {
            // The transfer-shaped exit condition: quiet for --idle-ms once traffic has started. Without it a
            // harness has to guess how long to wait, and a guess that is too short truncates the measurement.
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                if (firstMs >= 0 && wall.ElapsedMilliseconds - Interlocked.Read(ref lastMs) > options.IdleMilliseconds)
                {
                    stop.Cancel();
                    return;
                }
            }
        });

        try
        {
            await foreach (var packet in transport.ReceiveAsync(stop.Token).ConfigureAwait(false))
            {
                long now = wall.ElapsedMilliseconds;
                Interlocked.Exchange(ref lastMs, now);
                if (firstMs < 0)
                    firstMs = now;

                total++;
                totalBytes += packet.Payload.Length;

                if (packet.Payload.Length < 9)
                {
                    malformed++;
                    continue;
                }

                byte stream = packet.Payload[0];
                long sequence = BinaryPrimitives.ReadInt64BigEndian(packet.Payload.AsSpan(1, 8));
                received[stream]++;
                if (sequence > highestSequence[stream])
                    highestSequence[stream] = sequence;
            }
        }
        catch (OperationCanceledException)
        {
            // expected: idle or max-seconds
        }

        await idle.ConfigureAwait(false);

        double seconds = firstMs < 0 ? 0 : Math.Max(1, lastMs - firstMs) / 1000.0;
        long offered = 0;
        for (int i = 0; i < highestSequence.Length; i++)
        {
            if (highestSequence[i] >= 0)
                offered += highestSequence[i] + 1;
        }

        long lost = Math.Max(0, offered - total + malformed);
        var report = new DrainReport(
            Group: options.Group.ToString(),
            Port: options.Port,
            Interface: options.InterfaceName,
            SpanSeconds: Math.Round(seconds, 3),
            ReceivedDatagrams: total,
            ReceivedBytes: totalBytes,
            OfferedDatagrams: offered,
            LostDatagrams: lost,
            LossPercent: offered == 0 ? 0 : Math.Round(100.0 * lost / offered, 3),
            ReceivedDatagramsPerSecond: seconds == 0 ? 0 : Math.Round(total / seconds, 1),
            ReceivedMegabytesPerSecond: seconds == 0 ? 0 : Math.Round(totalBytes / seconds / (1024 * 1024), 3),
            MalformedDatagrams: malformed,
            Streams: highestSequence.Count(s => s >= 0));

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"drained {report.ReceivedDatagrams:N0}/{report.OfferedDatagrams:N0} in {report.SpanSeconds:F3} s = " +
            $"{report.ReceivedDatagramsPerSecond:N0} dgram/s, {report.ReceivedMegabytesPerSecond:F2} MB/s, loss {report.LossPercent:F3}%"));

        if (options.OutputPath is not null)
            File.WriteAllText(options.OutputPath, JsonSerializer.Serialize(report, BenchJsonContext.Default.DrainReport));

        return 0;
    }

    // ---- blast ----

    private static int Blast(Options options)
    {
        var interfaceAddress = options.InterfaceName is null ? null : ResolveInterface(options.InterfaceName);
        int threads = Math.Max(1, options.Threads);
        double perThreadRate = options.Rate <= 0 ? 0 : (double)options.Rate / threads;

        var sent = new long[threads];
        var failed = new long[threads];
        var elapsed = new double[threads];
        string? firstError = null;
        int joinedForRouting = 0;
        var workers = new Thread[threads];
        var barrier = new Barrier(threads);

        for (int t = 0; t < threads; t++)
        {
            int index = t;
            workers[t] = new Thread(() =>
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendBuffer, UdpMulticastTransport.SocketBufferBytes); }
                catch (SocketException) { }
                // Bind to the wildcard, exactly as the shipped transport does, and let IP_MULTICAST_IF below pick
                // the egress interface. Binding to the interface's own address instead is what produces
                // NetworkUnreachable for a loopback multicast send on Windows: the stack then resolves the
                // destination against the bound source rather than against IP_MULTICAST_IF.
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                if (interfaceAddress is not null)
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, interfaceAddress.GetAddressBytes());

                var endpoint = new IPEndPoint(options.Group, options.Port);
                var payload = new byte[options.Size];
                payload[0] = (byte)index;

                // Windows refuses a multicast send out the loopback pseudo-interface with NetworkUnreachable
                // unless the socket is also a group member — IP_MULTICAST_IF alone is not enough. The shipped
                // transport never notices because it always joins. Probe once and join only if the probe says
                // we must: an unnecessary join would make the kernel fan out an extra copy per datagram to this
                // very socket, which is exactly the cost this tool is trying to measure on the other side.
                // The probe carries sequence 0, so the measured run starts at 1 when it lands. Reusing 0 would
                // make the drain side see one more datagram than the sequence stamps claim were offered.
                long firstSequence = 1;
                try
                {
                    socket.SendTo(payload, SocketFlags.None, endpoint);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NetworkUnreachable)
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(options.Group, interfaceAddress ?? IPAddress.Any));
                    try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 8 * 1024); }
                    catch (SocketException) { }
                    Interlocked.CompareExchange(ref joinedForRouting, 1, 0);
                    firstSequence = 0;
                }

                barrier.SignalAndWait();
                var clock = Stopwatch.StartNew();
                var deadline = TimeSpan.FromSeconds(options.Seconds);
                long emitted = 0;

                while (clock.Elapsed < deadline)
                {
                    long sequence = firstSequence + emitted;
                    if (perThreadRate > 0)
                    {
                        // Burst pacing against an absolute schedule: send everything the schedule says is due,
                        // then sleep 1 ms. Deliberately NOT a spin-wait per datagram — at 200k datagrams/s the
                        // inter-send gap is ~5 us, so a spinning pacer burns a whole core per blast thread and
                        // the paced arms end up starving the very receiver they are measuring. Bursts of ~1 ms
                        // are well inside the receiver's 4 MiB socket buffer (~2,800 datagrams).
                        long due = (long)(clock.Elapsed.TotalSeconds * perThreadRate) - emitted;
                        if (due <= 0)
                        {
                            Thread.Sleep(1);
                            continue;
                        }
                    }

                    BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), sequence);
                    try
                    {
                        socket.SendTo(payload, SocketFlags.None, endpoint);
                    }
                    catch (SocketException ex)
                    {
                        // A full send buffer on an unpaced blast is expected and is itself part of the answer:
                        // it is the sender-side cap, not receiver loss. Anything else (no route, access denied)
                        // is a setup error that would otherwise present as a silent zero — surface the first one.
                        failed[index]++;
                        Interlocked.CompareExchange(ref firstError, $"{ex.SocketErrorCode}: {ex.Message}", null);
                        continue;
                    }
                    emitted++;
                }

                clock.Stop();
                sent[index] = emitted;
                elapsed[index] = clock.Elapsed.TotalSeconds;
            })
            { IsBackground = false, Priority = ThreadPriority.AboveNormal };
        }

        foreach (var worker in workers)
            worker.Start();
        foreach (var worker in workers)
            worker.Join();

        long totalSent = sent.Sum();
        double span = elapsed.Max();
        var report = new BlastReport(
            Group: options.Group.ToString(),
            Port: options.Port,
            Interface: options.InterfaceName,
            DatagramSizeBytes: options.Size,
            Threads: threads,
            TargetRatePerSecond: options.Rate,
            SpanSeconds: Math.Round(span, 3),
            SentDatagrams: totalSent,
            FailedSends: failed.Sum(),
            FirstSendError: firstError,
            JoinedForRouting: joinedForRouting == 1,
            OfferedDatagramsPerSecond: span == 0 ? 0 : Math.Round(totalSent / span, 1),
            OfferedMegabytesPerSecond: span == 0 ? 0 : Math.Round(totalSent * (double)options.Size / span / (1024 * 1024), 3));

        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"offered {report.SentDatagrams:N0} datagrams of {options.Size} B in {report.SpanSeconds:F3} s = " +
            $"{report.OfferedDatagramsPerSecond:N0} dgram/s, {report.OfferedMegabytesPerSecond:F2} MB/s"));
        if (report.FailedSends > 0)
            Console.Error.WriteLine($"{report.FailedSends:N0} sends failed; first error: {report.FirstSendError}");

        if (options.OutputPath is not null)
            File.WriteAllText(options.OutputPath, JsonSerializer.Serialize(report, BenchJsonContext.Default.BlastReport));

        return 0;
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
        throw new ArgumentException($"No IPv4 address found for interface '{name}'.");
    }

    private sealed record Options(
        IPAddress Group,
        int Port,
        string? InterfaceName,
        int Size,
        int Rate,
        int Seconds,
        int Threads,
        int IdleMilliseconds,
        int MaxSeconds,
        string? OutputPath)
    {
        public static Options Parse(string[] args)
        {
            var group = IPAddress.Parse("239.192.58.10");
            int port = 45058, size = 1472, rate = 0, seconds = 15, threads = 1, idleMs = 3000, maxSeconds = 300;
            string? iface = null, outPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} requires a value.");

                switch (arg)
                {
                    case "--group": group = IPAddress.Parse(Next()); break;
                    case "--port": port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--interface": iface = Next(); break;
                    case "--size": size = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--rate": rate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--seconds": seconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--threads": threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--idle-ms": idleMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--max-seconds": maxSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--out": outPath = Next(); break;
                    default: throw new ArgumentException($"Unrecognised argument '{arg}'.");
                }
            }

            if (size < 9)
                throw new ArgumentException("--size must be at least 9 bytes (1 stream id + 8 sequence).");

            return new Options(group, port, iface, size, rate, seconds, threads, idleMs, maxSeconds, outPath);
        }
    }
}

internal sealed record DrainReport(
    string Group,
    int Port,
    string? Interface,
    double SpanSeconds,
    long ReceivedDatagrams,
    long ReceivedBytes,
    long OfferedDatagrams,
    long LostDatagrams,
    double LossPercent,
    double ReceivedDatagramsPerSecond,
    double ReceivedMegabytesPerSecond,
    long MalformedDatagrams,
    int Streams);

internal sealed record BlastReport(
    string Group,
    int Port,
    string? Interface,
    int DatagramSizeBytes,
    int Threads,
    int TargetRatePerSecond,
    double SpanSeconds,
    long SentDatagrams,
    long FailedSends,
    string? FirstSendError,
    bool JoinedForRouting,
    double OfferedDatagramsPerSecond,
    double OfferedMegabytesPerSecond);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(DrainReport))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BlastReport))]
internal sealed partial class BenchJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
