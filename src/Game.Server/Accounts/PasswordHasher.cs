using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Game.Server.Accounts;

/// <summary>How a password compares with a stored hash.</summary>
public enum PasswordCheck
{
    /// <summary>It does not match (or the stored hash cannot be read).</summary>
    Wrong,

    /// <summary>It matches.</summary>
    Right,

    /// <summary>It matches, and the hash was made with other parameters than today's: store a new one.</summary>
    RightButOutdated,
}

/// <summary>Hashes and checks passwords. Both are slow on purpose: call them through <see cref="HashQueue"/>, never on a tick thread.</summary>
public interface IPasswordHasher
{
    /// <summary>A new hash with a new salt, as the text to store.</summary>
    string Hash(string password);

    /// <summary>Compares a password with a stored hash.</summary>
    PasswordCheck Verify(string password, string stored);
}

/// <summary>
/// Argon2id (RFC 9106) from libsodium, through NSec. Why that package: it is maintained (a release this year, next to libsodium's), libsodium is audited and
/// is what most systems use, and it hashes in native memory. The managed packages (Konscious, Isopoh: last releases 2024 and 2023) allocate the 19 MiB of every
/// hash on the GC heap of a process that must not pause for longer than a tick.
/// </summary>
/// <remarks>
/// Defaults: 19 MiB, 2 passes, 1 lane (OWASP's Argon2id recommendation; libsodium supports one lane only), a 16-byte random salt, a 32-byte hash. The stored text
/// is the PHC format <c>$argon2id$v=19$m=19456,t=2,p=1$salt$hash</c>, so parameters can change: an old hash is still checked with its own, and replaced at the
/// next login.
/// </remarks>
public sealed class Argon2idHasher : IPasswordHasher
{
    /// <summary>Default memory, in KiB.</summary>
    public const int DefaultMemoryKib = 19_456;

    /// <summary>Default passes.</summary>
    public const int DefaultPasses = 2;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "$argon2id$v=19$";
    private const int MaxMemoryKib = 1 << 20;   // a stored hash may not ask for more than 1 GiB or 16 passes: a damaged row must not stall the server
    private const int MaxPasses = 16;

    private readonly int _memoryKib;
    private readonly int _passes;

    /// <summary>Creates a hasher with the given parameters for new hashes.</summary>
    public Argon2idHasher(int memoryKib = DefaultMemoryKib, int passes = DefaultPasses)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryKib, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(memoryKib, MaxMemoryKib);
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(passes, MaxPasses);
        _memoryKib = memoryKib;
        _passes = passes;
    }

    /// <inheritdoc />
    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, _memoryKib, _passes);
        return Prefix + Parameters(_memoryKib, _passes) + "$" + Encode(salt) + "$" + Encode(hash);
    }

    /// <inheritdoc />
    public PasswordCheck Verify(string password, string stored)
    {
        if (!TryParse(stored, out var memoryKib, out var passes, out var salt, out var expected)) return PasswordCheck.Wrong;
        var actual = Derive(password, salt, memoryKib, passes);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return PasswordCheck.Wrong;
        return memoryKib == _memoryKib && passes == _passes ? PasswordCheck.Right : PasswordCheck.RightButOutdated;
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int passes)
    {
        var algorithm = new Argon2id(new Argon2Parameters { DegreeOfParallelism = 1, MemorySize = memoryKib, NumberOfPasses = passes });
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return algorithm.DeriveBytes(bytes, salt, HashBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Parameters(int memoryKib, int passes) => string.Create(CultureInfo.InvariantCulture, $"m={memoryKib},t={passes},p=1");

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryParse(string stored, out int memoryKib, out int passes, out byte[] salt, out byte[] hash)
    {
        memoryKib = passes = 0;
        salt = hash = [];
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = stored[Prefix.Length..].Split('$');
        if (parts.Length != 3) return false;
        var numbers = parts[0].Split(',');
        if (numbers.Length != 3 || !Number(numbers[0], "m=", out memoryKib) || !Number(numbers[1], "t=", out passes) || numbers[2] != "p=1") return false;
        if (memoryKib is < 8 or > MaxMemoryKib || passes is < 1 or > MaxPasses) return false;
        try
        {
            salt = Decode(parts[1]);
            hash = Decode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length == SaltBytes && hash.Length == HashBytes;
    }

    private static bool Number(string text, string prefix, out int value)
    {
        value = 0;
        return text.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static byte[] Decode(string text) => Convert.FromBase64String(text + new string('=', (4 - text.Length % 4) % 4));
}

/// <summary>
/// Bounds how much hashing runs at once, so a flood of logins cannot take the cores the map instances tick on, nor unbounded memory (each hash holds its
/// 19 MiB): a few at a time, a short line behind them, and beyond that the caller is told to come back.
/// </summary>
public sealed class HashQueue(int concurrent, int waiting, TimeSpan patience) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(concurrent);
    private int _queued;

    /// <summary>A quarter of the cores (at least one), 32 waiting, 5 seconds.</summary>
    public static HashQueue Default() => new(Math.Max(1, Environment.ProcessorCount / 4), 32, TimeSpan.FromSeconds(5));

    /// <summary>Runs <paramref name="work"/> on the thread pool when a slot is free. Throws <see cref="StoreUnavailableException"/> when the line is full or too slow.</summary>
    public async Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellation)
    {
        Hosting.TickThread.Forbid("Password hashing");
        if (Interlocked.Increment(ref _queued) > concurrent + waiting)
        {
            Interlocked.Decrement(ref _queued);
            throw new StoreUnavailableException("too many logins at once");
        }
        try
        {
            if (!await _slots.WaitAsync(patience, cancellation).ConfigureAwait(false)) throw new StoreUnavailableException("too many logins at once");
            try
            {
                return await Task.Run(work, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();
}
