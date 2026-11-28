using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Hosting;
using Game.Shared.Protocol;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Game.Tests.Server;

namespace Game.Tests.Admin;

/// <summary>An admin store in memory over a rig's <see cref="MemoryAccountStore"/>: enough for the API's rules. The PostgreSQL one has its own tests.</summary>
public sealed class MemoryAdminStore(MemoryAccountStore? accounts = null) : IAdminStore
{
    private readonly object _lock = new();
    private readonly List<AuditRecord> _audit = new();

    /// <summary>When set, every call fails: a database that is away.</summary>
    public bool Away { get; set; }

    /// <summary>Every audit row, oldest first.</summary>
    public IReadOnlyList<AuditRecord> Audit
    {
        get { lock (_lock) return _audit.ToList(); }
    }

    /// <summary>The last query of each kind the API made.</summary>
    public object? LastQuery { get; private set; }

    private void Check()
    {
        if (Away) throw new StoreUnavailableException("test: away");
    }

    public Task<AdminPage<AdminAccount>> FindAccountsAsync(AccountQuery query, CancellationToken cancellation)
    {
        Check();
        LastQuery = query;
        var rows = (accounts?.AccountList ?? []).Where(a => (query.Id is null || a.Id == query.Id) && (query.NamePrefix is null || a.Username.StartsWith(query.NamePrefix, StringComparison.Ordinal))
                && (query.After is null || string.CompareOrdinal(a.Username, query.After) > 0))
            .OrderBy(a => a.Username, StringComparer.Ordinal).Select(a => new AdminAccount(a.Id, a.Username, DateTime.UnixEpoch, DateTime.UnixEpoch, null, 0, a.Sessions)).ToList();
        return Task.FromResult(rows.Count > query.Limit ? new AdminPage<AdminAccount>(rows.Take(query.Limit).ToList(), rows[query.Limit - 1].Username) : new AdminPage<AdminAccount>(rows, null));
    }

    public async Task<(AdminAccount Account, IReadOnlyList<AdminCharacter> Characters)?> GetAccountAsync(Guid id, CancellationToken cancellation)
    {
        var page = await FindAccountsAsync(new AccountQuery(null, id, null, 1), cancellation);
        return page.Items.Count == 0 ? null : (page.Items[0], []);
    }

    public Task<AdminPage<AdminCharacter>> FindCharactersAsync(CharacterQuery query, CancellationToken cancellation)
    {
        Check();
        LastQuery = query;
        return Task.FromResult(new AdminPage<AdminCharacter>([], null));
    }

    public Task<AdminCharacterDetail?> GetCharacterAsync(Guid id, CancellationToken cancellation)
    {
        Check();
        return Task.FromResult<AdminCharacterDetail?>(null);
    }

    public Task<AdminPage<AuditRecord>> SearchAuditAsync(AuditQuery query, CancellationToken cancellation)
    {
        Check();
        LastQuery = query;
        lock (_lock)
        {
            var rows = _audit.Where(a => (query.TargetId is null || a.TargetId == query.TargetId) && (query.StaffId is null || a.StaffId == query.StaffId)
                                         && (query.Action is null || a.Action == query.Action) && (query.BeforeId is null || a.Id < query.BeforeId))
                .OrderByDescending(a => a.Id).ToList();
            return Task.FromResult(rows.Count > query.Limit
                ? new AdminPage<AuditRecord>(rows.Take(query.Limit).ToList(), rows[query.Limit - 1].Id.ToString())
                : new AdminPage<AuditRecord>(rows, null));
        }
    }

    public Task<long> AppendAuditAsync(AuditDraft draft, CancellationToken cancellation)
    {
        Check();
        return Task.FromResult(Add(draft));
    }

    public Task<SessionsEnded?> EndSessionsAsync(Guid account, Func<int, AuditDraft> audit, CancellationToken cancellation)
    {
        Check();
        if (accounts?.EndAllSessions(account) is not { } ended) return Task.FromResult<SessionsEnded?>(null);
        return Task.FromResult<SessionsEnded?>(new SessionsEnded(ended, Add(audit(ended.Count))));
    }

    private long Add(AuditDraft d)
    {
        lock (_lock)
        {
            var row = new AuditRecord(_audit.Count + 1, d.At, d.RequestId, d.Staff.Id, d.Staff.Name, d.Staff.Role, d.Action, d.TargetKind, d.TargetId, d.Before, d.After, d.Reason);
            _audit.Add(row);
            return row.Id;
        }
    }
}

/// <summary>A gateway rig (with accounts) and the admin API beside it, wired as Program wires them.</summary>
public sealed class AdminRig : IAsyncDisposable
{
    /// <summary>The service credential of every rig (a test value).</summary>
    public const string Credential = "test-only-admin-credential-0123456789abcdef";

    private AdminRig(GatewayRig game, IAdminStore store, AdminHost host, AdminEvents events, CountingLog log, MemoryAccountStore? accounts, FakeTime time, AdminOptions options)
    {
        Game = game;
        Store = store;
        Host = host;
        Events = events;
        Log = log;
        Accounts = accounts;
        Time = time;
        Options = options;
        Http = new AdminHttp(host.Endpoint);
    }

    public GatewayRig Game { get; }

    public IAdminStore Store { get; }

    public MemoryAdminStore? Memory => Store as MemoryAdminStore;

    public MemoryAccountStore? Accounts { get; }

    public AdminHost Host { get; }

    public AdminEvents Events { get; }

    public CountingLog Log { get; }

    public FakeTime Time { get; }

    public AdminOptions Options { get; }

    public AdminHttp Http { get; }

    /// <summary>The road the commands take (T-806).</summary>
    public AdminRoad Road { get; private init; } = null!;

    /// <summary>The moderation checks, when the rig has a moderation store.</summary>
    public ModerationChecks? Checks { get; private init; }

    /// <summary>
    /// Starts a gateway rig with accounts over <paramref name="accounts"/> (or a memory store) and the admin API over <paramref name="store"/> (or a <see cref="MemoryAdminStore"/>
    /// over the same accounts).
    /// </summary>
    public static async Task<AdminRig> StartAsync(Func<AdminOptions, AdminOptions>? configure = null, IAdminStore? store = null, AccountRigOptions? accounts = null,
        IEnumerable<IAdminCommand>? commands = null, IReadOnlyDictionary<string, string>? table = null, IModerationStore? moderation = null, int saveWaitMs = 10_000,
        Game.Server.Chat.IChatStore? chat = null)
    {
        var time = accounts?.Time as FakeTime ?? new FakeTime { Now = DateTimeOffset.UtcNow };   // one clock for accounts and the admin road
        var memory = accounts is null ? new MemoryAccountStore() : accounts.Store as MemoryAccountStore;
        var game = await GatewayRig.StartAsync(accounts: accounts ?? new AccountRigOptions(memory!, new FakeHasher(), new AccountLimits(), time));
        store ??= new MemoryAdminStore(memory);
        // T-808: the step timeout runs on a real timer (FakeTime keeps the system's timers), so it is a wait for the instance's next tick on this machine. 3 s was overrun under load,
        // and the kick was refused with timeout. 8 s stays inside the 10 s request limit; the stalled-world test sets its own 300 ms.
        var options = new AdminOptions { Development = true, Port = 0, StepTimeoutMs = 8_000 };
        if (configure is not null) options = configure(options);
        var log = new CountingLog(game.Log);
        var events = new AdminEvents(log);
        var checks = moderation is null ? null : new ModerationChecks(moderation, time);
        if (checks is not null) game.Accounts!.BanOf = checks.BanOfAsync;   // T-806, as Program wires it
        var road = new AdminRoad(game.World, game.Gateway, store, options, time)
        {
            Moderation = moderation, Checks = checks, NextItemId = game.Saves is null ? null : game.Saves.NextItemId, Content = game.Content, SaveWaitMs = saveWaitMs,
        };
        // T-901: chat, as Program wires it (before anyone plays), when the rig has a chat store.
        if (chat is not null)
        {
            game.Gateway.Chat = new Game.Server.Chat.ChatService(new Game.Server.Chat.ChatOptions(), game.Gateway, chat, checks is null ? null : checks.SpeakAsync, time, log,
                chat as Game.Server.Chat.IChatReportStore);
            road = new AdminRoad(game.World, game.Gateway, store, options, time)
            {
                Moderation = moderation, Checks = checks, NextItemId = game.Saves is null ? null : game.Saves.NextItemId, Content = game.Content, SaveWaitMs = saveWaitMs,
                ChatReports = chat as Game.Server.Chat.IChatReportStore,
            };
        }
        var runner = new AdminCommands(commands ?? [new KickCommand()], road, events, log, table);
        var api = new AdminApi(options, new ServiceCredential(Credential), new AdminRateLimit(options, time), runner, store,
            new AdminStatusSource(game.World, game.Gateway, game.Saves, log, game.ContentHash, new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc)), game.Gateway, log, moderation, road.Maintenance,
            chat as Game.Server.Chat.IChatReportStore);
        var host = await AdminHost.StartAsync(options, game.Endpoint.Port, api, name => name == options.CredentialEnv ? Credential : null);
        return new AdminRig(game, store, host, events, log, memory, time, options) { Road = road, Checks = checks };
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Host.StopAsync();
        await Host.DisposeAsync();
        await Game.DisposeAsync();
    }
}

/// <summary>An HTTP client for the admin API: the credential and a staff member on every request unless told otherwise.</summary>
public sealed class AdminHttp : IDisposable
{
    private readonly HttpClient _http;

    public AdminHttp(Uri endpoint)
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, AllowAutoRedirect = false };   // a test's throw-away certificate
        _http = new HttpClient(handler) { BaseAddress = endpoint };
    }

    public string? Credential { get; set; } = AdminRig.Credential;

    public (string? Id, string? Name, string? Role) Staff { get; set; } = ("staff-1", "Robin Q", "support");

    public async Task<(int Status, JsonElement Body, HttpResponseMessage Response)> SendAsync(HttpMethod method, string path, string? json = null, string? requestId = null,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue(contentType));
        if (Credential is not null) request.Headers.TryAddWithoutValidation("Authorization", AdminHeaders.CredentialScheme + Credential);
        if (Staff.Id is not null) request.Headers.TryAddWithoutValidation(AdminHeaders.StaffId, Staff.Id);
        if (Staff.Name is not null) request.Headers.TryAddWithoutValidation(AdminHeaders.StaffName, Uri.EscapeDataString(Staff.Name));
        if (Staff.Role is not null) request.Headers.TryAddWithoutValidation(AdminHeaders.StaffRole, Staff.Role);
        if (requestId is not null) request.Headers.TryAddWithoutValidation(AdminHeaders.RequestId, requestId);
        var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(), response);
    }

    public Task<(int Status, JsonElement Body, HttpResponseMessage Response)> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<(int Status, JsonElement Body, HttpResponseMessage Response)> PostAsync(string path, object body, string? requestId = null) =>
        SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(body), requestId);

    public Task<(int Status, JsonElement Body, HttpResponseMessage Response)> KickAsync(Guid account, string reason = "spamming the start square", string? requestId = null) =>
        PostAsync(AdminPaths.Commands + "/kick", new Dictionary<string, object> { ["account_id"] = account.ToString(), ["reason"] = reason }, requestId);

    public void Dispose() => _http.Dispose();
}
