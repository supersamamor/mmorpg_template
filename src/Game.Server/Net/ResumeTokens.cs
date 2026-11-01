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
/// <para>Never log a token; <see cref="Fingerprint"/> gives a short tag that is safe to log.</para>
/// </remarks>
public sealed class ResumeTokens
{
    /// <summary>Random bytes per token (256 bits).</summary>
    public const int TokenBytes = 32;

    private readonly object _lock = new();
    private readonly Dictionary<ulong, Entry> _byPrefix = new();
    private readonly Dictionary<EntityId, ulong> _byCharacter = new();

    private readonly record struct Entry(byte[] Hash, EntityId Character);

    /// <summary>Live tokens (one per character that has one).</summary>
    public int Count
    {
        get { lock (_lock) return _byPrefix.Count; }
    }

    /// <summary>Issues a new token for <paramref name="character"/>; any token it had stops working.</summary>
    public string Issue(EntityId character)
    {
        lock (_lock)
        {
            RevokeLocked(character);
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
    }

    /// <summary>The character <paramref name="token"/> belongs to, or null when it is unknown or no longer valid. Does not use it up.</summary>
    public EntityId? Find(string token)
    {
        var hash = Hash(token);
        lock (_lock) return FindLocked(hash, Prefix(hash))?.Character;
    }

    /// <summary>
    /// Redeems <paramref name="token"/> if it is still the live token of <paramref name="character"/>: it stops working and a new one is
    /// returned in <paramref name="next"/>. False (and nothing changes) otherwise, e.g. when another resume used it first.
    /// </summary>
    public bool TryRotate(string token, EntityId character, out string next)
    {
        var hash = Hash(token);
        lock (_lock)
        {
            if (FindLocked(hash, Prefix(hash)) is not { } entry || entry.Character != character)
            {
                next = string.Empty;
                return false;
            }
            next = Issue(character); // re-entrant lock; revokes the old one
            return true;
        }
    }

    /// <summary>Revokes the character's token, if it has one.</summary>
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
        if (!_byCharacter.Remove(character, out var prefix)) return false;
        _byPrefix.Remove(prefix);
        return true;
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
