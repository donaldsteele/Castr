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
    /// Default chunk size (bytes). Kept small because the M1 UDP transport puts a whole (encrypted) chunk in a
    /// single datagram — the two-level chunking into ~1200-byte wire packets described in
    /// wiki/concepts/wire-protocol.md is a documented not-yet-implemented trim, so a chunk must fit in one UDP
    /// datagram (max 65507 bytes). 8 KB matches the GUI's established default and leaves ample headroom.
    /// </summary>
    public const int DefaultChunkSize = 8192;

    /// <summary>
    /// Fixed ChaCha20-Poly1305 overhead <see cref="Castr.Core.Security.ContentKey"/> adds per chunk: a
    /// 16-byte Poly1305 auth tag and nothing else (the nonce is derived deterministically from
    /// (fileIndex, chunkIndex) rather than carried alongside the ciphertext), so ciphertext size is always
    /// exactly plaintext size + 16.
    /// </summary>
    public const int ChaCha20Poly1305TagBytes = 16;

    /// <summary>
    /// Conservative ceiling on the encrypted chunk payload the CLI will allow onto the wire. The M1 UDP
    /// transport still puts one whole encrypted chunk into a single datagram (the two-level chunk/wire-packet
    /// split in wiki/concepts/wire-protocol.md isn't implemented yet, tracked for M3), and on Windows
    /// Socket.SendToAsync throws SocketException (MessageSize) synchronously above 65,507 bytes — the
    /// theoretical max IPv4 UDP payload. Worse, a receiver already listening has no timeout, so an oversized
    /// send silently hangs it forever instead of failing.
    ///
    /// We stop short of that exact 65,507-byte ceiling and use 65,000: the ciphertext this bounds doesn't
    /// travel alone — it rides inside a ChunkDataMessage wire envelope (session id, file/chunk indices, a
    /// length prefix, and a Merkle inclusion proof whose size grows with chunk count) that this simple,
    /// Core-format-agnostic check does not itself measure, and some paths trim usable space further with IPv4
    /// options. The ~500-byte margin absorbs that envelope for realistic transfer sizes.
    /// </summary>
    public const int MaxSafeUdpPayloadBytes = 65_000;

    /// <summary>
    /// Largest --chunk-size the CLI accepts: the largest plaintext chunk whose ciphertext
    /// (chunk size + <see cref="ChaCha20Poly1305TagBytes"/>) still fits under <see cref="MaxSafeUdpPayloadBytes"/>.
    /// </summary>
    public const int MaxChunkSize = MaxSafeUdpPayloadBytes - ChaCha20Poly1305TagBytes;

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
