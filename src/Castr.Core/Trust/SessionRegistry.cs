using Castr.Core.Chunking;
using Castr.Core.Security;

namespace Castr.Core.Trust;

/// <summary>What a session id means to a receiver that has seen it before.</summary>
public enum SessionAdmission
{
    /// <summary>Never seen. Admissible, and recorded once the manifest is accepted.</summary>
    Fresh,

    /// <summary>Seen, bound to this same sender and this same manifest — a resume, a re-announce, or a second peer serving the same transfer.</summary>
    SameTransfer,

    /// <summary>Seen, but bound to a <b>different</b> transfer. Not admissible; see <see cref="ISessionRegistry"/>.</summary>
    Conflict,
}

/// <summary>One session id a receiver has accepted, and the transfer it is bound to.</summary>
public sealed record SessionBinding(string SessionId, PublicKeyId SenderId, string ManifestDigest, DateTimeOffset FirstSeen);

/// <summary>
/// A receiver's record of which session ids it has accepted, and what each one meant. Nothing enforced this
/// before: the session id was length-checked and otherwise taken at face value.
///
/// <para><b>Why it needs enforcing.</b> The session id is the HKDF salt in
/// <see cref="Security.ContentKeyWrap"/>, and it is the domain separator in every chunk's AEAD nonce
/// (<c>fileIndex|chunkIndex|0000</c>) and AAD (<c>sessionId|fileIndex|chunkIndex</c>). Two different transfers
/// sharing one id therefore re-derive the same wrapping key from the same X25519 pair, and give the same
/// (nonce, key) coordinates to different plaintext if the content key is ever reused alongside it — the failure
/// mode that costs a stream cipher its confidentiality outright. It is safe today only because
/// <c>Castr.Cli</c> mints a fresh random id per invocation, which is a property of one client rather than
/// anything the protocol checks.</para>
///
/// <para><b>Only accepted manifests are recorded</b>, so a sender the receiver does not trust cannot burn a
/// session id and lock a legitimate transfer out of it. Reuse by the <i>same</i> sender for the <i>same</i>
/// manifest is normal and expected — a resume, a re-announce, a second peer relaying the same transfer — and is
/// reported as <see cref="SessionAdmission.SameTransfer"/>, not as a conflict.</para>
/// </summary>
public interface ISessionRegistry
{
    /// <summary>Classifies a session id against what this receiver has already accepted. Does not record anything.</summary>
    SessionAdmission Classify(byte[] sessionId, PublicKeyId senderId, ChunkHash manifestDigest);

    /// <summary>Binds a session id to a transfer. Called only once its manifest has been fully admitted.</summary>
    void Record(byte[] sessionId, PublicKeyId senderId, ChunkHash manifestDigest, DateTimeOffset now);

    /// <summary>Every binding currently held, oldest first.</summary>
    IReadOnlyList<SessionBinding> All();
}

/// <summary>
/// Process-lifetime <see cref="ISessionRegistry"/>. Bounded and oldest-first evicted: an unbounded registry
/// would be a slow leak on a long-running receiver, and would itself be the memory-exhaustion vector that
/// M10 spent a milestone removing from the chunk cache.
///
/// <para>Eviction weakens the guarantee honestly rather than silently: a session id evicted from the registry
/// reads as <see cref="SessionAdmission.Fresh"/> again. At the default capacity that takes thousands of
/// distinct accepted transfers, which is far beyond the horizon over which a content key could plausibly be
/// reused.</para>
/// </summary>
public sealed class InMemorySessionRegistry(int capacity = InMemorySessionRegistry.DefaultCapacity) : ISessionRegistry
{
    public const int DefaultCapacity = 4096;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<SessionBinding>> _bindings = [];
    private readonly LinkedList<SessionBinding> _order = new(); // oldest first
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public SessionAdmission Classify(byte[] sessionId, PublicKeyId senderId, ChunkHash manifestDigest)
    {
        string key = KeyOf(sessionId);
        lock (_lock)
        {
            if (!_bindings.TryGetValue(key, out var node))
                return SessionAdmission.Fresh;

            return node.Value.SenderId == senderId && node.Value.ManifestDigest == manifestDigest.ToString()
                ? SessionAdmission.SameTransfer
                : SessionAdmission.Conflict;
        }
    }

    public void Record(byte[] sessionId, PublicKeyId senderId, ChunkHash manifestDigest, DateTimeOffset now)
    {
        string key = KeyOf(sessionId);
        lock (_lock)
        {
            if (_bindings.ContainsKey(key))
                return; // first binding wins; re-recording would move it to the back of the eviction queue

            while (_order.Count >= _capacity && _order.First is { } oldest)
            {
                _bindings.Remove(oldest.Value.SessionId);
                _order.RemoveFirst();
            }

            _bindings[key] = _order.AddLast(new SessionBinding(key, senderId, manifestDigest.ToString(), now));
        }
    }

    public IReadOnlyList<SessionBinding> All()
    {
        lock (_lock) { return [.. _order]; }
    }

    /// <summary>Rebuilds state loaded from disk. Used by <see cref="FileSessionRegistry"/>; not part of the interface.</summary>
    internal void Seed(IEnumerable<SessionBinding> bindings)
    {
        lock (_lock)
        {
            foreach (var binding in bindings)
            {
                if (_bindings.ContainsKey(binding.SessionId))
                    continue;
                while (_order.Count >= _capacity && _order.First is { } oldest)
                {
                    _bindings.Remove(oldest.Value.SessionId);
                    _order.RemoveFirst();
                }
                _bindings[binding.SessionId] = _order.AddLast(binding);
            }
        }
    }

    private static string KeyOf(byte[] sessionId) => Convert.ToHexStringLower(sessionId);
}
