using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Game.Server.Accounts;

/// <summary>
/// Session tokens: 256 bits from the OS CSPRNG, base64url without padding (43 characters), sent once, in the answer to a sign-in. Only the SHA-256 is stored, so a
/// copy of the database opens no session. A plain hash is enough: there is nothing to guess in 256 random bits. Never log a token.
/// </summary>
public static class SessionTokens
{
    /// <summary>Random bytes per token.</summary>
    public const int TokenBytes = 32;

    /// <summary>Characters of a token.</summary>
    public const int TokenLength = 43;

    /// <summary>A new token.</summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>What is stored and looked up for a token, or null when the text cannot be one (so it never reaches the database).</summary>
    public static byte[]? HashOf(string? token)
    {
        if (token is null || token.Length != TokenLength) return null;
        foreach (var c in token)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) return null;
        return SHA256.HashData(Encoding.ASCII.GetBytes(token));
    }
}
