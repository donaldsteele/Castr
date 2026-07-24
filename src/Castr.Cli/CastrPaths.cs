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
    /// Default chunk size (bytes). 8 KB matches the GUI's established default. As of M3, Castr.Core packetizes
    /// each encrypted chunk into MTU-safe wire packets (see Castr.Core.Protocol.WirePacketizer), so this is no
    /// longer constrained by the single-datagram UDP limit; it stays at 8 KB for continuity with M2.
    /// </summary>
    public const int DefaultChunkSize = 8192;

    /// <summary>
    /// Upper bound on <c>--chunk-size</c>. Since M3, Castr.Core splits every encrypted chunk into MTU-safe wire
    /// packets before it hits the socket (see Castr.Core.Protocol.WirePacketizer), so a large chunk no longer
    /// risks the old 65,507-byte single-datagram SocketException/silent-stall failure that forced a ~65 KB cap.
    /// The documented hash/repair chunk range is 256 KB–1 MB; this ceiling sits well above it. It exists purely
    /// as a sanity guard against pathological memory use: a receiver buffers a whole chunk's fragments in RAM
    /// while reassembling it, so an unbounded chunk size would be a memory-exhaustion foot-gun. 16 MiB covers
    /// the documented range with generous headroom.
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
