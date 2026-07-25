using System.Net;

namespace Castr.Cli;

/// <summary>
/// Well-known defaults and per-user config locations for the CLI. The multicast group matches
/// wiki/concepts/wire-protocol.md (administratively-scoped 239.192.55.55, TTL=1, link-local only).
/// </summary>
internal static class CastrPaths
{
    /// <summary>Default administratively-scoped multicast group (see wiki/concepts/wire-protocol.md).</summary>
    public const string DefaultGroup = "239.192.55.55";

    /// <summary>Default UDP port. Not fixed by the wire protocol; chosen in the dynamic/registered range.</summary>
    public const int DefaultPort = 45055;

    /// <summary>
    /// Default chunk size (bytes) — 256 KiB, matching <see cref="Castr.Core.Chunking.ChunkLayout.DefaultChunkSize"/>
    /// and the bottom of the documented hash/repair chunk range (256 KB–1 MB, see wiki/concepts/wire-protocol.md
    /// and the <see cref="MaxChunkSize"/> comment below).
    ///
    /// <para><b>The primary justification is consistency, not throughput.</b>
    /// <see cref="Castr.Core.Chunking.ChunkLayout.DefaultChunkSize"/> has been 262144 all along, and
    /// wiki/concepts/wire-protocol.md has specified the 256 KB–1 MB range all along. Only the CLI and GUI
    /// surfaces disagreed — this constant was 8192, a value inherited from the pre-M3 era when a chunk had to fit
    /// in one UDP datagram, kept afterwards only "for continuity with M2", and left contradicting the range
    /// asserted eleven lines below it in this very file. Since M3, Castr.Core packetizes each encrypted chunk
    /// into MTU-safe wire packets (see Castr.Core.Protocol.WirePacketizer), so the datagram limit stopped
    /// constraining it years of milestones ago. M8 makes the surfaces match what Core already shipped; the
    /// measurements below are supporting evidence, not the case itself.</para>
    ///
    /// <para><b>Measured, not assumed:</b> 100 MB over real two-process <c>castr send</c>/<c>castr receive</c>,
    /// warm cache, arms interleaved within each rep, n=5, every run SHA-256 byte-identical — 8192 → 262144 is
    /// <b>1.33x</b> faster, 9.30 s → 6.98 s (see the 2026-07-25 M8 section of
    /// docs/benchmarks/throughput-runs.md). The gain is not from fewer data datagrams (that term moves ~33%): a
    /// larger chunk amortizes the per-chunk Merkle proof over 32x more payload, and it collapses the per-chunk
    /// PEER_HAVE gossip term, which is <c>FileSize²/(8·chunkSize²)</c> — quadratic in file size and quartically
    /// sensitive to this constant. At 8 KiB an 800 MiB transfer gossips 1.31 GB, more than the payload; at
    /// 256 KiB it gossips 1.3 MB.</para>
    ///
    /// <para><b>Do not expect the +77% the measurement campaign recorded for this change.</b> That figure was
    /// taken pre-M7, when a larger chunk also collapsed the premature-repair storm; M7 has since claimed that
    /// half, so amplification is already 1.12x at 8192 and only the permanent part is left. Both numbers are
    /// correct against their own baselines. On post-M7 code a result near 1.77x would be evidence M7 regressed,
    /// not good news.</para>
    ///
    /// <para><b>Why not 1 MiB, the top of the range:</b> 1048576 measured <b>+7.7%</b> over 262144 (6.48 s vs
    /// 6.98 s) while quadrupling the repair granularity — the bytes a single lost chunk costs to re-request, and
    /// the bytes Castr.Core.Protocol.ChunkPacketAssembler must buffer per in-flight chunk. Three independent
    /// measurements converge on stopping here: that ~8% is the smallest step in the sweep, 1 MiB roughly halves
    /// the carousel-idle headroom (see ReceiverSession.DefaultCarouselIdleThreshold), and under deliberate CPU
    /// contention a 1 MiB transfer <i>timed out entirely</i> where 256 KiB completed. A bad trade on a lossy or
    /// busy machine, which is the case repair exists for.</para>
    ///
    /// <para><b>Coarser repair granularity was measured, not assumed safe:</b> under real kernel <c>tc netem</c>
    /// loss (32 MB, 10%, interleaved n=6, 12/12 byte-identical) 256 KiB is <b>2.80x faster</b> than 8 KiB, not
    /// slower — fewer, larger repair round trips beat many small ones when each is quantised by the 5 s
    /// RepairOptions.RequestTimeout. The Docker E2E fan-out tier (5 and 9 receivers under real netem loss) is
    /// green on this default with the tests unmodified.</para>
    /// </summary>
    public const int DefaultChunkSize = 262_144;

    /// <summary>
    /// Upper bound on <c>--chunk-size</c>. Since M3, Castr.Core splits every encrypted chunk into MTU-safe wire
    /// packets before it hits the socket (see Castr.Core.Protocol.WirePacketizer), so a large chunk no longer
    /// risks the old 65,507-byte single-datagram SocketException/silent-stall failure that forced a ~65 KB cap.
    /// The documented hash/repair chunk range is 256 KB–1 MB; this ceiling sits well above it. It exists purely
    /// as a sanity guard against pathological memory use: a receiver buffers a whole chunk's fragments in RAM
    /// while reassembling it, so an unbounded chunk size would be a memory-exhaustion foot-gun. 16 MiB covers
    /// the documented range with generous headroom.
    ///
    /// <para><b>Known, deliberate surface disagreement:</b> the GUI's chunk-size control caps at <b>1 MiB</b>
    /// (the top of the documented hash/repair range) while this CLI ceiling is 16 MiB. That is intentional — the
    /// GUI offers a spinner with no way to explain the consequences, so it exposes only the documented range,
    /// whereas the CLI is the escape hatch for someone who has measured their own hardware. Recorded here rather
    /// than left for a reader to discover as an inconsistency. Note the 16 MiB ceiling is also what sizes
    /// Castr.Core.Protocol.ChunkPacketAssembler's worst-case reassembly buffer, so raising it is a memory
    /// decision, not just a validation one.</para>
    /// </summary>
    public const int MaxChunkSize = 16 * 1024 * 1024;

    /// <summary>Per-user config directory, e.g. %APPDATA%/castr on Windows, ~/.config/castr elsewhere.</summary>
    public static string ConfigDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(appData, "castr");
        }
    }

    public static string DefaultTrustStorePath => Path.Combine(ConfigDirectory, "trusted-senders.json");

    public static string DefaultIdentityPath => Path.Combine(ConfigDirectory, "identity.key");

    /// <summary>Conventional per-user seed location. A deployment drops trusted-senders.seed.json here to pre-authorize senders.</summary>
    public static string DefaultTrustSeedPath => Path.Combine(ConfigDirectory, "trusted-senders.seed.json");

    public static IPAddress DefaultGroupAddress => IPAddress.Parse(DefaultGroup);
}
