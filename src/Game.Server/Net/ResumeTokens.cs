using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Game.Server.World;

namespace Game.Server.Net;

/// <summary>
/// The resume tokens of the characters the gateway holds (ARCHITECTURE §10): one live token per character, issued in <c>welcome</c>,
/// redeemed by <c>hello</c> <c>resume</c>. Thread-safe. In memory only, so a server restart invalidates every token (durable sessions are
/// T-701).
/// </summary>
/// <remarks>
/// <para>A token is <see cref="TokenBytes"/> bytes (256 bits) from <see cref="RandomNumberGenerator"/>, base64url-encoded without padding
/// (43 characters). The store keeps only its SHA-256, never the token: a heap dump or a log of this object reveals nothing that can be
/// replayed.</para>
/// <para>Lookups are indexed by the first 8 bytes of the hash, then the full 32 bytes are compared with
/// <see cref="CryptographicOperations.FixedTimeEquals"/>. Timing can at most tell a guesser whether the hash of its guess shares an 8-byte
/// prefix with a live token's hash, which says nothing about the token itself.</para>
/// <para>Acknowledgement (ADR-002 #55): a resume's <c>welcome</c> can be lost when the connection drops again before it arrives. So the token a
/// resume redeemed stays valid, as the character's <i>previous</i> token, until the new session's first message (<see cref="Acknowledge"/>):
/// until then the client may still hold only that one. Redeeming it again rotates once more and kills the token issued in between (it never
/// reached anyone who spoke with it). A token therefore opens at most one session that speaks; the gateway revokes both tokens when the
/// character leaves, so neither outlives the grace period.</para>
/// <para>Never log a token; <see cref="Fingerprint"/> gives a short tag that is safe to log.</para>
/// </remarks>
public sealed class ResumeTokens
{
    /// <summary>Random bytes per token (256 bits).</summary>
    public const int TokenBytes = 32;

    private readonly object _lock = new();
    private readonly Dictionary<ulong, Entry> _byPrefix = new();
    private readonly Dictionary<EntityId, ulong> _byCharacter = new();
    private readonly Dictionary<EntityId, ulong> _previous = new();   // the token the last resume redeemed, until acknowledged

    private readonly record struct Entry(byte[] Hash, EntityId Character);

    /// <summary>Live tokens: one per character that has one, plus a not yet acknowledged previous token per resumed character.</summary>
    public int Count
    {
        get { lock (_lock) return _byPrefix.Count; }
    }

    /// <summary>Issues a new token for <paramref name="character"/>; any token it had (previous one included) stops working.</summary>
    public string Issue(EntityId character)
    {
        lock (_lock)
        {
            RevokeLocked(character);
            return AddLocked(character);
        }
    }

    /// <summary>The character <paramref name="token"/> belongs to, or null when it is unknown or no longer valid. Does not use it up.</summary>
    public EntityId? Find(string token)
    {
        var hash = Hash(token);
        lock (_lock) return FindLocked(hash, Prefix(hash))?.Character;
    }

    /// <summary>
    /// Redeems <paramref name="token"/> if it is a live token of <paramref name="character"/> (its current one, or its previous one not yet
    /// acknowledged): a new token is returned in <paramref name="next"/>, every other token of the character stops working, and
    /// <paramref name="token"/> itself stays valid as the previous token until <see cref="Acknowledge"/>. False (and nothing changes)
    /// otherwise, e.g. an unknown, revoked or acknowledged-away token, or another character's.
    /// </summary>
    public bool TryRotate(string token, EntityId character, out string next)
    {
        var hash = Hash(token);
        var prefix = Prefix(hash);
        lock (_lock)
        {
            if (FindLocked(hash, prefix) is not { } entry || entry.Character != character)
            {
                next = string.Empty;
                return false;
            }
            if (_byCharacter.Remove(character, out var current) && current != prefix) _byPrefix.Remove(current);
            if (_previous.Remove(character, out var previous) && previous != prefix) _byPrefix.Remove(previous);
            _previous[character] = prefix;
            next = AddLocked(character);
            return true;
        }
    }

    /// <summary>
    /// The session that resumed <paramref name="character"/> has spoken, so its client holds the new token: the previous one stops working.
    /// False when there was none (a joined character, or already acknowledged).
    /// </summary>
    public bool Acknowledge(EntityId character)
    {
        lock (_lock)
        {
            if (!_previous.Remove(character, out var previous)) return false;
            _byPrefix.Remove(previous);
            return true;
        }
    }

    /// <summary>True while <paramref name="character"/> has a previous token that is not yet acknowledged.</summary>
    public bool HasUnacknowledged(EntityId character)
    {
        lock (_lock) return _previous.ContainsKey(character);
    }

    /// <summary>Revokes the character's tokens (current and previous), if it has any.</summary>
    public bool Revoke(EntityId character)
    {
        lock (_lock) return RevokeLocked(character);
    }

    /// <summary>
    /// A short tag for logs: the first 6 hex digits of the token's SHA-256. It identifies a token across log lines without revealing it
    /// (24 bits of a hash of 256 random bits cannot be turned back into the token).
    /// </summary>
    public static string Fingerprint(string token) => "#" + Convert.ToHexStringLower(Hash(token).AsSpan(0, 3));

    private Entry? FindLocked(byte[] hash, ulong prefix) =>
        _byPrefix.TryGetValue(prefix, out var entry) && CryptographicOperations.FixedTimeEquals(entry.Hash, hash) ? entry : null;

    private bool RevokeLocked(EntityId character)
    {
        var any = false;
        if (_byCharacter.Remove(character, out var prefix)) any = _byPrefix.Remove(prefix);
        if (_previous.Remove(character, out var previous)) any |= _byPrefix.Remove(previous);
        return any;
    }

    private string AddLocked(EntityId character)
    {
        while (true)
        {
            var token = NewToken();
            var hash = Hash(token);
            var prefix = Prefix(hash);
            if (!_byPrefix.TryAdd(prefix, new Entry(hash, character))) continue; // a 64-bit prefix clash: draw again
            _byCharacter[character] = prefix;
            return token;
        }
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static ulong Prefix(byte[] hash) => BinaryPrimitives.ReadUInt64LittleEndian(hash);
}
