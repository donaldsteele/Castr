using System.Text.Json;
using Castr.Core.Chunking;
using Castr.Core.Security;

namespace Castr.Core.Trust;

/// <summary>
/// A JSON-file-backed <see cref="ISessionRegistry"/>, on the same shape as <see cref="FileTrustStore"/>: load
/// eagerly at construction, persist synchronously after every mutation, write via a temp file and move into
/// place so a crash mid-write cannot corrupt it.
///
/// <para>Persistence is the point rather than a convenience. The CLI runs one transfer per invocation, so a
/// registry that lived only as long as the process would classify every session id as fresh and enforce
/// nothing — a check that is not a check. What makes the binding meaningful is that it outlives the transfer
/// that created it.</para>
///
/// <para>A corrupt or unreadable file is treated as an empty registry rather than a fatal error: the failure
/// this guards against is a sender reusing a session id, and refusing to receive anything at all because a
/// cache file got truncated would be a worse outcome than losing the history it held.</para>
/// </summary>
public sealed class FileSessionRegistry : ISessionRegistry
{
    private readonly string _path;
    private readonly InMemorySessionRegistry _inner;

    public FileSessionRegistry(string path, int capacity = InMemorySessionRegistry.DefaultCapacity)
    {
        _path = path;
        _inner = new InMemorySessionRegistry(capacity);

        if (!File.Exists(path))
            return;

        try
        {
            _inner.Seed(SessionRegistryJsonCodec.Decode(File.ReadAllText(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Start empty. See the note on this class about why this is not fatal.
        }
    }

    public SessionAdmission Classify(byte[] sessionId, PublicKeyId senderId, ChunkHash manifestDigest) =>
        _inner.Classify(sessionId, senderId, manifestDigest);

    public void Record(byte[] sessionId, PublicKeyId senderId, ChunkHash manifestDigest, DateTimeOffset now)
    {
        _inner.Record(sessionId, senderId, manifestDigest, now);
        Save();
    }

    public IReadOnlyList<SessionBinding> All() => _inner.All();

    /// <summary>
    /// Writes the registry out, and <b>never lets a persistence failure fail the transfer that triggered it</b>.
    ///
    /// <para>Two independent processes can share this file: the path is derived from the <i>directory</i> of
    /// the trust store, so two receivers pointed at two trust stores in one config directory land on one
    /// registry. That is normal for several receivers on one host — the M12a fan-out harness, the showcase
    /// captures, anyone running two <c>castr receive</c> processes side by side.</para>
    ///
    /// <para>The temp file therefore carries the process id and a random component. A fixed <c>.tmp</c> name
    /// meant concurrent receivers collided on it, <see cref="File.WriteAllText(string,string)"/> threw
    /// <see cref="IOException"/> out of <see cref="Record"/>, and the throw unwound manifest admission —
    /// so a receiver that had already verified a good manifest silently sat at 0/0 chunks forever. Measured:
    /// exactly two of three same-host receivers completed a transfer, repeatably, and the losers logged
    /// nothing at all.</para>
    ///
    /// <para>Losing the race is also tolerated rather than thrown. This file is a memory of session ids
    /// already seen; a dropped write costs one binding's worth of enforcement, while a throw costs the whole
    /// transfer. That is the same trade the class already makes for an unreadable file on load.</para>
    /// </summary>
    private void Save()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{_path}.{Environment.ProcessId}.{Path.GetRandomFileName()}.tmp";
        try
        {
            File.WriteAllText(tempPath, SessionRegistryJsonCodec.Encode(_inner.All()));
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Reads/writes the session-registry file format.</summary>
public static class SessionRegistryJsonCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<SessionBinding> Decode(string json)
    {
        var document = JsonSerializer.Deserialize<SessionRegistryDocument>(json, Options)
            ?? throw new InvalidDataException("Session registry JSON deserialized to null.");

        return [.. document.Sessions.Select(dto => new SessionBinding(
            SessionId: dto.SessionId ?? throw new InvalidDataException("Entry missing sessionId."),
            SenderId: new PublicKeyId(dto.SenderId ?? throw new InvalidDataException("Entry missing senderId.")),
            ManifestDigest: dto.ManifestDigest ?? throw new InvalidDataException("Entry missing manifestDigest."),
            FirstSeen: dto.FirstSeen ?? DateTimeOffset.UnixEpoch))];
    }

    public static string Encode(IReadOnlyList<SessionBinding> bindings)
    {
        var document = new SessionRegistryDocument
        {
            Version = 1,
            Sessions = [.. bindings.Select(b => new SessionBindingDto
            {
                SessionId = b.SessionId,
                SenderId = b.SenderId.Value,
                ManifestDigest = b.ManifestDigest,
                FirstSeen = b.FirstSeen,
            })],
        };
        return JsonSerializer.Serialize(document, Options);
    }

    private sealed class SessionRegistryDocument
    {
        public int Version { get; set; } = 1;
        public List<SessionBindingDto> Sessions { get; set; } = [];
    }

    private sealed class SessionBindingDto
    {
        public string? SessionId { get; set; }
        public string? SenderId { get; set; }
        public string? ManifestDigest { get; set; }
        public DateTimeOffset? FirstSeen { get; set; }
    }
}
