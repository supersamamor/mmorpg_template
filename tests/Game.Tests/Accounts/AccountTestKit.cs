using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Game.Server.Accounts;
using Game.Server.Net;
using Game.Shared.Accounts;
using Game.Shared.Protocol;
using Game.Tests.Network;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Accounts;

/// <summary>A wall clock the test sets.</summary>
public sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A fast stand-in for Argon2id (which has tests of its own) that counts its work. Two of them with different versions see each other's hashes as outdated.</summary>
public sealed class FakeHasher(int version = 1) : IPasswordHasher
{
    private int _hashes, _checks;

    public int Hashes => Volatile.Read(ref _hashes);

    public int Checks => Volatile.Read(ref _checks);

    /// <summary>The stored hash of every check made, in order.</summary>
    public List<string> Checked { get; } = new();

    public string Hash(string password)
    {
        Interlocked.Increment(ref _hashes);
        return $"fake{version}:{Guid.NewGuid():N}:{Digest(password)}";
    }

    public PasswordCheck Verify(string password, string stored)
    {
        Interlocked.Increment(ref _checks);
        lock (Checked) Checked.Add(stored);
        var parts = stored.Split(':');
        if (parts[2] != Digest(password)) return PasswordCheck.Wrong;
        return parts[0] == "fake" + version ? PasswordCheck.Right : PasswordCheck.RightButOutdated;
    }

    private static string Digest(string password) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(password)));
}

/// <summary>
/// The account store's contract in memory, for tests of everything above it. It is <b>not</b> evidence about the database: uniqueness under concurrency, locking and
/// the migration are tested against a real PostgreSQL (<see cref="DatabaseFactAttribute"/>).
/// </summary>
public sealed class MemoryAccountStore : IAccountStore
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, (string Username, string Hash)> _accounts = new();
    private readonly Dictionary<string, (Guid Account, DateTime Created, DateTime Expires)> _sessions = new();
    private readonly Dictionary<Guid, (Guid Account, int Slot, string Name, string Key, string Job, bool Deleted, AppearanceChoice? Look)> _characters = new();

    /// <summary>When set, every call waits for it first: a slow database.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, every call fails: a database that is away.</summary>
    public bool Away { get; set; }

    /// <summary>Runs once, right before the next look-up of an account by its id: what happens between two reads of one request.</summary>
    public Func<Task>? BeforeFindById { get; set; }

    /// <summary>Calls started.</summary>
    public int Calls;

    /// <summary>The stored hash of the only account, for tests that look at it.</summary>
    public string OnlyHash
    {
        get { lock (_lock) return _accounts.Values.Single().Hash; }
    }

    public int SessionCount
    {
        get { lock (_lock) return _sessions.Count; }
    }

    private async Task Enter(CancellationToken cancellation)
    {
        Interlocked.Increment(ref Calls);
        if (Gate is { } gate) await gate.Task.WaitAsync(cancellation);
        if (Away) throw new StoreUnavailableException("test: away");
    }

    public async Task<AccountAuth?> FindAccountAsync(string username, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
            foreach (var (id, a) in _accounts)
                if (a.Username == username) return new AccountAuth(id, a.Hash, EraseAfter(id));
        return null;
    }

    public async Task<AccountAuth?> FindAccountAsync(Guid id, CancellationToken cancellation)
    {
        if (BeforeFindById is { } before)
        {
            BeforeFindById = null;
            await before();
        }
        await Enter(cancellation);
        lock (_lock) return _accounts.TryGetValue(id, out var a) ? new AccountAuth(id, a.Hash, EraseAfter(id)) : null;
    }

    public async Task<bool> TryCreateAccountAsync(Guid id, string username, string passwordHash, DateTime now, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
        {
            if (_accounts.Values.Any(a => a.Username == username)) return false;
            _accounts.Add(id, (username, passwordHash));
            return true;
        }
    }

    public async Task<IReadOnlyList<byte[]>?> TryAddSessionAsync(Guid account, string expectedHash, string? rehash, byte[] tokenHash, DateTime now, DateTime expires, int maxSessions, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
        {
            if (!_accounts.TryGetValue(account, out var a) || a.Hash != expectedHash || EraseAfter(account) is not null) return null;
            if (rehash is not null) _accounts[account] = (a.Username, rehash);
            _sessions.Add(Convert.ToHexString(tokenHash), (account, now, expires));
            foreach (var key in _sessions.Where(s => s.Value.Account == account && s.Value.Expires <= now).Select(s => s.Key).ToList()) _sessions.Remove(key);
            var surplus = _sessions.Where(s => s.Value.Account == account && s.Key != Convert.ToHexString(tokenHash)).OrderByDescending(s => s.Value.Created)
                .Skip(maxSessions - 1).Select(s => s.Key).ToList();
            foreach (var key in surplus) _sessions.Remove(key);
            return surplus.Select(Convert.FromHexString).ToList();
        }
    }

    public async Task<Guid?> FindSessionAsync(byte[] tokenHash, DateTime now, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock) return _sessions.TryGetValue(Convert.ToHexString(tokenHash), out var s) && s.Expires > now ? s.Account : null;
    }

    public async Task<bool> DeleteSessionAsync(byte[] tokenHash, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock) return _sessions.Remove(Convert.ToHexString(tokenHash));
    }

    public async Task<IReadOnlyList<byte[]>?> TryChangePasswordAsync(Guid account, string expectedHash, string newHash, DateTime now, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
        {
            if (!_accounts.TryGetValue(account, out var a) || a.Hash != expectedHash) return null;
            _accounts[account] = (a.Username, newHash);
            var ended = _sessions.Where(s => s.Value.Account == account).Select(s => s.Key).ToList();
            foreach (var key in ended) _sessions.Remove(key);
            return ended.Select(Convert.FromHexString).ToList();
        }
    }

    private readonly Dictionary<Guid, DateTime> _eraseAfter = new();

    /// <summary>Deletions recorded: when asked, and when erased.</summary>
    public Dictionary<Guid, (DateTime Asked, DateTime? Erased)> Deletions { get; } = new();

    public int AccountCount
    {
        get { lock (_lock) return _accounts.Count; }
    }

    private DateTime? EraseAfter(Guid account) => _eraseAfter.TryGetValue(account, out var at) ? at : null;

    public async Task<IReadOnlyList<byte[]>?> TryRequestErasureAsync(Guid account, string expectedHash, DateTime now, DateTime eraseAfter, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
        {
            if (!_accounts.TryGetValue(account, out var a) || a.Hash != expectedHash) return null;
            if (_eraseAfter.TryAdd(account, eraseAfter)) Deletions.Add(account, (now, null));
            var ended = _sessions.Where(s => s.Value.Account == account).Select(s => s.Key).ToList();
            foreach (var key in ended) _sessions.Remove(key);
            return ended.Select(Convert.FromHexString).ToList();
        }
    }

    public async Task<bool> TryCancelErasureAsync(Guid account, string expectedHash, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
            return _accounts.TryGetValue(account, out var a) && a.Hash == expectedHash && _eraseAfter.Remove(account) && Deletions.Remove(account);
    }

    public async Task<IReadOnlyList<Guid>> ListErasableAsync(DateTime now, int limit, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock) return _eraseAfter.Where(e => e.Value <= now).OrderBy(e => e.Value).Take(limit).Select(e => e.Key).ToList();
    }

    public async Task<EraseOutcome> EraseAsync(Guid account, DateTime now, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
        {
            if (EraseAfter(account) is not { } due || due > now) return EraseOutcome.Nothing;
            var characters = _characters.Where(c => c.Value.Account == account).Select(c => c.Key).ToList();
            if (characters.Any(HeldElsewhere.Contains)) return EraseOutcome.InUse;
            foreach (var id in characters) _characters.Remove(id);
            _accounts.Remove(account);
            _eraseAfter.Remove(account);
            Deletions[account] = (Deletions[account].Asked, now);
            return EraseOutcome.Erased;
        }
    }

    public async Task<IReadOnlyList<CharacterEntry>> ListCharactersAsync(Guid account, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
            return _characters.Where(c => c.Value.Account == account && !c.Value.Deleted).OrderBy(c => c.Value.Slot)
                .Select(c => new CharacterEntry(c.Key, c.Value.Slot, c.Value.Name, c.Value.Job, c.Value.Look)).ToList();
    }

    public async Task<CharacterEntry?> FindCharacterAsync(Guid account, Guid character, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
            return _characters.TryGetValue(character, out var c) && c.Account == account && !c.Deleted ? new CharacterEntry(character, c.Slot, c.Name, c.Job, c.Look) : null;
    }

    public async Task<(CreateCharacterOutcome Outcome, CharacterEntry? Character)> CreateCharacterAsync(Guid account, Guid character, string name, string nameKey, string jobId,
        int maxSlots, AppearanceRecord? appearance, DateTime now, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
        {
            if (!_accounts.ContainsKey(account) || EraseAfter(account) is not null) return (CreateCharacterOutcome.AccountGone, null);
            var used = _characters.Values.Where(c => c.Account == account && !c.Deleted).Select(c => c.Slot).ToList();
            var slot = Enumerable.Range(0, maxSlots).Where(s => !used.Contains(s)).Cast<int?>().FirstOrDefault();
            if (slot is null) return (CreateCharacterOutcome.NoFreeSlot, null);
            if (_characters.Values.Any(c => c.Key == nameKey)) return (CreateCharacterOutcome.NameTaken, null);
            var look = appearance is null ? null : new AppearanceChoice(appearance.Gender, appearance.Skin, appearance.Face, appearance.Hair, appearance.HairColor);
            _characters.Add(character, (account, slot.Value, name, nameKey, jobId, false, look));
            return (CreateCharacterOutcome.Created, new CharacterEntry(character, slot.Value, name, jobId, look));
        }
    }

    /// <summary>Characters another server holds.</summary>
    public HashSet<Guid> HeldElsewhere { get; } = new();

    public async Task<DeleteCharacterOutcome> DeleteCharacterAsync(Guid account, Guid character, DateTime now, CancellationToken cancellation)
    {
        await Enter(cancellation);
        lock (_lock)
        {
            if (!_characters.TryGetValue(character, out var c) || c.Account != account || c.Deleted) return DeleteCharacterOutcome.Unknown;
            if (HeldElsewhere.Contains(character)) return DeleteCharacterOutcome.InUse;
            _characters[character] = c with { Deleted = true };
            return DeleteCharacterOutcome.Deleted;
        }
    }
}

/// <summary>An account service over a store, with the test's clocks, log and hasher.</summary>
public sealed class ServiceRig
{
    public readonly MemoryAccountStore Memory = new();
    public readonly FakeTime Time = new();
    public readonly ManualClock Clock = new();
    public readonly ListLog Log = new();
    public readonly AccountService Service;

    public ServiceRig(IPasswordHasher? hasher = null, AccountLimits? limits = null, IAccountStore? store = null)
    {
        Hasher = hasher ?? new FakeHasher();
        Service = new AccountService(store ?? Memory, Hasher, new HashQueue(4, 64, TimeSpan.FromSeconds(10)), limits ?? new AccountLimits(), TestGrids.SampleContent.Balance, TestGrids.SampleContent.Appearance, Clock, Time, Log);
    }

    public IPasswordHasher Hasher { get; }

    public const string Password = "correct horse battery";

    public const string Address = "203.0.113.7";

    public async Task<string> RegisterAsync(string username = "ada", string password = Password)
    {
        var result = await Service.RegisterAsync(username, password, Address, default);
        result.Error.ShouldBeNull();
        return result.Value!.Token;
    }

    public Task<AccountResult<NewSession>> LoginAsync(string username = "ada", string password = Password, string address = Address) =>
        Service.LoginAsync(username, password, address, default);

    public async Task<CharacterRecord> CreateAsync(string token, string name)
    {
        var result = await Service.CreateAsync(token, name, default);
        result.Error.ShouldBeNull();
        return result.Value!;
    }
}

/// <summary>An HTTP client for a rig's account endpoints.</summary>
public sealed class AccountHttp : IDisposable
{
    private readonly HttpClient _http;

    public AccountHttp(Uri endpoint)
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, AllowAutoRedirect = false };   // the rig's throw-away certificate
        _http = new HttpClient(handler) { BaseAddress = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http", Path = "/" }.Uri };
    }

    public async Task<(int Status, JsonElement Body, HttpResponseMessage Response)> SendAsync(HttpMethod method, string path, string? json = null, string? token = null,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue(contentType));
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", AccountPaths.TokenScheme + token);
        var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement, response);
    }

    public Task<(int Status, JsonElement Body, HttpResponseMessage Response)> PostAsync(string path, object? body = null, string? token = null) =>
        SendAsync(HttpMethod.Post, path, body is null ? null : JsonSerializer.Serialize(body), token);

    public async Task<string> RegisterAsync(string username, string password = ServiceRig.Password)
    {
        var (status, body, _) = await PostAsync(AccountPaths.Register, new { username, password });
        status.ShouldBe(201);
        return body.GetProperty("token").GetString()!;
    }

    public async Task<string> CreateAsync(string token, string name)
    {
        var (status, body, _) = await PostAsync(AccountPaths.Characters, new { name }, token);
        status.ShouldBe(201, body.ToString());
        return body.GetProperty("id").GetString()!;
    }

    public void Dispose() => _http.Dispose();
}
