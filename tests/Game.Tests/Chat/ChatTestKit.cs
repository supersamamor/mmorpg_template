using System.Text.Json.Nodes;
using Game.Server.Accounts;
using Game.Server.Admin;
using Game.Server.Chat;
using Game.Server.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Tests.Chat;

/// <summary>A sink that keeps what chat sent one player.</summary>
public sealed class ListSink : IChatSink
{
    private readonly List<ServerMessage> _messages = new();

    public IReadOnlyList<ServerMessage> Messages
    {
        get { lock (_messages) return _messages.ToList(); }
    }

    public IReadOnlyList<ChatLineMessage> Lines => Messages.OfType<ChatLineMessage>().ToList();

    public IReadOnlyList<ChatRefusedMessage> Refusals => Messages.OfType<ChatRefusedMessage>().ToList();

    public void Post(ServerMessage message, byte[] bytes)
    {
        Game.Server.Net.ProtocolJson.ToBytes(Game.Server.Net.ProtocolJson.ReadServer(bytes).Message!).ShouldBe(Game.Server.Net.ProtocolJson.ToBytes(message), "the bytes are the message");
        lock (_messages) _messages.Add(message);
    }

    public void Clear()
    {
        lock (_messages) _messages.Clear();
    }
}

/// <summary>Who plays where, set by the test.</summary>
public sealed class FakeDirectory : IChatDirectory
{
    public List<ChatPeer> Peers { get; } = new();

    public ChatPeer Add(long entity, string name, string map, bool account = true)
    {
        var peer = new ChatPeer(new EntityId(entity), name, account ? Guid.NewGuid() : null, account ? Guid.NewGuid() : null, map, new ListSink());
        Peers.Add(peer);
        return peer;
    }

    public IReadOnlyList<ChatPeer> OnMap(string mapId) => Peers.Where(p => p.MapId == mapId).ToList();

    public ChatPeer? Online(string nameKey) => Peers.FirstOrDefault(p => ChatNames.Key(p.Name) == nameKey);

    public ChatPeer? Find(EntityId character) => Peers.FirstOrDefault(p => p.Character == character);

    public IReadOnlyCollection<Guid> PlayingAccounts() => Peers.Where(p => p.AccountId is not null).Select(p => p.AccountId!.Value).ToList();
}

/// <summary>Blocks and reports in memory, with the store's rules; counts its reads.</summary>
public sealed class MemoryChatStore : IChatStore, IChatReportStore
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, List<(Guid Character, DateTime At)>> _blocks = new();
    private readonly List<ChatReport> _reports = new();
    private long _nextReport;

    public Dictionary<string, ChatCharacter> Characters { get; } = new(StringComparer.Ordinal);

    public int BlockReads { get; private set; }

    public bool Down { get; set; }

    public IReadOnlyList<ChatReport> Reports
    {
        get { lock (_lock) return _reports.ToList(); }
    }

    public void Know(ChatPeer peer) => Characters[ChatNames.Key(peer.Name)!] = new ChatCharacter(peer.CharacterId!.Value, peer.AccountId!.Value, peer.Name);

    private void Check()
    {
        if (Down) throw new StoreUnavailableException("down (test)");
    }

    public Task<IReadOnlyList<BlockedCharacter>> BlocksAsync(Guid account, CancellationToken cancellation)
    {
        Check();
        lock (_lock)
        {
            BlockReads++;
            var names = Characters.Values.ToDictionary(c => c.Id, c => c.Name);
            return Task.FromResult<IReadOnlyList<BlockedCharacter>>(_blocks.TryGetValue(account, out var list)
                ? list.Select(b => new BlockedCharacter(b.Character, names.GetValueOrDefault(b.Character, "?"))).ToList() : []);
        }
    }

    public Task<ChatCharacter?> FindCharacterAsync(string nameKey, CancellationToken cancellation)
    {
        Check();
        return Task.FromResult(Characters.GetValueOrDefault(nameKey));
    }

    public Task<BlockOutcome> AddBlockAsync(Guid account, Guid character, int limit, DateTime now, CancellationToken cancellation)
    {
        Check();
        lock (_lock)
        {
            if (!_blocks.TryGetValue(account, out var list)) _blocks[account] = list = new();
            if (list.Any(b => b.Character == character)) return Task.FromResult(BlockOutcome.AlreadyBlocked);
            if (list.Count >= limit) return Task.FromResult(BlockOutcome.Limit);
            list.Add((character, now));
            return Task.FromResult(BlockOutcome.Added);
        }
    }

    public Task<bool> RemoveBlockAsync(Guid account, Guid character, CancellationToken cancellation)
    {
        Check();
        lock (_lock) return Task.FromResult(_blocks.TryGetValue(account, out var list) && list.RemoveAll(b => b.Character == character) > 0);
    }

    public Task<long> AddReportAsync(ChatReportDraft report, CancellationToken cancellation)
    {
        Check();
        lock (_lock)
        {
            var id = ++_nextReport;
            _reports.Add(new ChatReport(id, report.ReportedAt, report.ReporterAccountId, report.ReporterCharacterId, report.SenderAccountId, report.SenderCharacterId, report.Channel,
                report.Text, report.SaidAt, ChatReportStatuses.Open, null, null));
            return Task.FromResult(id);
        }
    }

    public Task<AdminPage<ChatReport>> FindReportsAsync(string? status, Guid? account, long? before, int limit, CancellationToken cancellation) =>
        Task.FromResult(new AdminPage<ChatReport>(Reports.Where(r => status is null || r.Status == status).OrderByDescending(r => r.Id).Take(limit).ToList(), null));

    public Task<ReportChange> CloseReportAsync(long report, DateTime now, Func<ChatReport, AuditDraft> audit, CancellationToken cancellation) => throw new NotSupportedException();

    public Task<int> PurgeClosedAsync(DateTime closedBefore, CancellationToken cancellation) => Task.FromResult(0);
}

/// <summary>Mutes only, counting how often the database would be asked.</summary>
public sealed class FakeModeration : IModerationStore
{
    private int _reads;

    public Dictionary<Guid, DateTime> Mutes { get; } = new();

    public int Reads => Volatile.Read(ref _reads);

    public bool Down { get; set; }

    public Task<Sanction?> ActiveSanctionAsync(Guid account, string kind, DateTime now, CancellationToken cancellation)
    {
        Interlocked.Increment(ref _reads);
        if (Down) throw new StoreUnavailableException("down (test)");
        return Task.FromResult(kind == SanctionKinds.Mute && Mutes.TryGetValue(account, out var until) && until > now
            ? new Sanction(1, account, kind, now, until, 1, null, null) : null);
    }

    public Task<IReadOnlyList<Guid>> BannedAmongAsync(IReadOnlyCollection<Guid> accounts, DateTime now, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<SanctionChange> AddSanctionAsync(Guid account, string kind, DateTime? until, DateTime now, AuditDraft audit, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<SanctionChange> LiftSanctionAsync(Guid account, string kind, DateTime now, Func<Sanction, AuditDraft> audit, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<IReadOnlyList<Sanction>> SanctionsAsync(Guid account, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<RequestChange> OpenRequestAsync(Guid account, string kind, DateTime now, Func<long, AuditDraft> audit, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<RequestChange> CloseRequestAsync(long request, string status, DateTime now, DateTime eraseAfter, Func<SupportRequest, AuditDraft> audit, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<SupportRequest?> GetRequestAsync(long request, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<AdminPage<SupportRequest>> FindRequestsAsync(string? status, Guid? account, long? before, int limit, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<JsonObject?> ExportAsync(Guid account, DateTime now, CancellationToken cancellation) => throw new NotSupportedException();
    public Task<OfflineResult> EditCharacterAsync(Guid character, Func<OfflineCharacter, OfflinePlan> plan, CancellationToken cancellation) => throw new NotSupportedException();
}
