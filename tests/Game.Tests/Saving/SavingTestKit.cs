using System.Reflection;
using Game.Content.Registry;
using Game.Server.Accounts;
using Game.Server.Hosting;
using Game.Server.Net;
using Game.Server.World;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Server.World.Saving;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Progression;
using Game.Shared.World;
using Game.Tests.Accounts;
using Game.Tests.Network;
using Game.Tests.Server;
using Game.Tests.World;

namespace Game.Tests.Saving;

/// <summary>
/// The character store's contract in memory, for tests of everything above it. It is <b>not</b> evidence about the database (locking, the unique keys, what a crash leaves): that is
/// tested against a real PostgreSQL (<see cref="CharacterDatabaseTests"/>).
/// </summary>
public sealed class MemoryCharacterStore : ICharacterStore
{
    private sealed record Row(long Claim, long? HeldBy, long Version, CharacterState? State);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Row> _rows = new();
    private readonly HashSet<long> _alive = new();
    private long _lastRun;

    /// <summary>When set, every call fails: a database that is away.</summary>
    public volatile bool Away;

    /// <summary>When set, claims fail (and only they).</summary>
    public volatile bool ClaimsFail;

    /// <summary>When set, every save waits for it first.</summary>
    public TaskCompletionSource? SaveGate;

    /// <summary>When set, every claim waits for it first.</summary>
    public TaskCompletionSource? ClaimGate;

    /// <summary>Characters that do not exist (deleted).</summary>
    public HashSet<Guid> Deleted { get; } = new();

    /// <summary>Every group saved, in order, as (character, version).</summary>
    public List<List<(Guid Character, long Version)>> Groups { get; } = new();

    public int Claims;

    public CharacterState? StateOf(Guid character)
    {
        lock (_lock) return _rows.TryGetValue(character, out var row) ? row.State : null;
    }

    public long VersionOf(Guid character)
    {
        lock (_lock) return _rows.TryGetValue(character, out var row) ? row.Version : 0;
    }

    public bool IsHeld(Guid character)
    {
        lock (_lock) return _rows.TryGetValue(character, out var row) && row.HeldBy is not null;
    }

    /// <summary>Another server takes the character: whoever held it has lost it.</summary>
    public void Steal(Guid character)
    {
        lock (_lock) _rows[character] = _rows[character] with { Claim = _rows[character].Claim + 1, HeldBy = -1 };
    }

    /// <summary>Puts a state in as an earlier stay's last save.</summary>
    public void Put(Guid character, CharacterState state)
    {
        lock (_lock) _rows[character] = new Row(1, null, 1, state);
    }

    public Task<long> StartRunAsync(CancellationToken cancellation)
    {
        Check();
        lock (_lock)
        {
            _alive.Add(++_lastRun);
            return Task.FromResult(_lastRun);
        }
    }

    public Task BeatAsync(long run, CancellationToken cancellation)
    {
        Check();
        return Task.CompletedTask;
    }

    public Task EndRunAsync(long run, CancellationToken cancellation)
    {
        Check();
        lock (_lock) _alive.Remove(run);
        return Task.CompletedTask;
    }

    public async Task<CharacterClaim> ClaimAsync(Guid character, long run, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Claims);
        if (ClaimGate is { } gate) await gate.Task;
        Check();
        if (ClaimsFail) throw new InvalidDataException("test: an unreadable state");
        lock (_lock)
        {
            if (Deleted.Contains(character)) return new CharacterClaim(ClaimOutcome.NotFound);
            var row = _rows.TryGetValue(character, out var found) ? found : new Row(0, null, 0, null);
            if (row.HeldBy is { } holder && holder != run && _alive.Contains(holder)) return new CharacterClaim(ClaimOutcome.HeldElsewhere);
            row = row with { Claim = row.Claim + 1, HeldBy = run };
            _rows[character] = row;
            return new CharacterClaim(ClaimOutcome.Claimed, row.Claim, row.Version, row.State);
        }
    }

    public Task ReleaseAsync(Guid character, long run, long claim, CancellationToken cancellation)
    {
        Check();
        lock (_lock)
            if (_rows.TryGetValue(character, out var row) && row.HeldBy == run && (claim == 0 || row.Claim == claim)) _rows[character] = row with { HeldBy = null };
        return Task.CompletedTask;
    }

    public async Task<SaveResult> SaveAsync(IReadOnlyList<CharacterSave> group, CancellationToken cancellation)
    {
        if (SaveGate is { } gate) await gate.Task;
        Check();
        lock (_lock)
        {
            var lost = group.Where(s => Deleted.Contains(s.CharacterId) || !_rows.TryGetValue(s.CharacterId, out var row) || row.Claim != s.Claim).Select(s => s.CharacterId).ToList();
            if (lost.Count > 0) return new SaveResult.Lost(lost);
            var write = group.Where(s => _rows[s.CharacterId].Version < s.Version).ToList();
            var writing = write.Select(s => s.CharacterId).ToHashSet();
            var taken = _rows.Where(r => !writing.Contains(r.Key) && r.Value.State is not null).SelectMany(r => r.Value.State!.Items.Select(i => i.Id)).ToHashSet();
            var ids = write.SelectMany(s => s.State.Items.Select(i => i.Id)).ToList();
            var duplicated = ids.Where(taken.Contains).Concat(ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key)).ToList();
            if (duplicated.Count > 0) return new SaveResult.Duplicated(duplicated);
            foreach (var save in write)
                _rows[save.CharacterId] = _rows[save.CharacterId] with { Version = save.Version, State = save.State, HeldBy = save.Final ? null : _rows[save.CharacterId].HeldBy };
            Groups.Add(group.Select(s => (s.CharacterId, s.Version)).ToList());
            return new SaveResult.Saved();
        }
    }

    private void Check()
    {
        if (Away) throw new StoreUnavailableException("test: away");
    }
}

/// <summary>
/// The sample content with the whole server pipeline and saving, over any store, ticked by the test. <see cref="EnterAsync"/> and <see cref="LeaveAsync"/> do what the gateway does.
/// </summary>
public sealed class SaveRig : IAsyncDisposable
{
    public readonly GameWorld World;
    public readonly ListLog Log = new();
    public readonly FakeTime Time = new();
    public readonly SystemClock Clock = new();
    public readonly InMemoryInventoryStore Inventories;
    public readonly InMemoryProgressionStore Progressions = new();
    public readonly InMemorySavePointStore SavePoints = new();
    public readonly CharacterSaves Saves;
    public readonly CharacterKeeper Keeper;
    public readonly CharacterLoader Loader;
    public readonly ContentDatabase Content;
    public readonly List<Game.Server.Events.IGameEvent> Events = new();
    public readonly Dictionary<EntityId, CharacterTicket> Tickets = new();
    private readonly NewCharacters _start;
    private readonly InventoryContent _items;
    private bool _drained;

    private SaveRig(ICharacterStore store, CharacterSaveOptions? options, ContentDatabase? content, FakeTime? time)
    {
        Content = content ?? TestGrids.SampleContent;
        if (time is not null) Time = time;
        _start = new NewCharacters(Content);
        _items = InventoryContent.From(Content);
        Saves = new CharacterSaves(store, options ?? Fast, Clock, Time, Log);
        Saves.StartAsync().GetAwaiter().GetResult();
        Inventories = new InMemoryInventoryStore(Saves.NextItemId);
        Loader = new CharacterLoader(Content, Saves.NextItemId);
        Saves.Open(Loader);
        Keeper = new CharacterKeeper(Saves, Progressions, Inventories, SavePoints, Clock, Time);
        World = new GameWorld(Content.Grids.Values, ServerPipeline.Create(Content, Progressions, Inventories, Clock, SavePoints, Keeper), 702, Clock, Log) { Keeper = Keeper };
        foreach (var instance in World.Instances) instance.Events.SubscribeAll(Events.Add);
        Saves.Lost += t =>
        {
            foreach (var (id, ticket) in Tickets)
                if (ReferenceEquals(ticket, t)) World.Despawn(id);
        };
    }

    /// <summary>No spacing, short retries, an interval no test reaches unless it asks for it.</summary>
    public static CharacterSaveOptions Fast => new()
    {
        IntervalMs = 3_600_000, SpacingMs = 0, RetryBaseMs = 5, RetryCapMs = 20, AtRiskMs = 100, LoginWaitMs = 300, BeatMs = 20, Writers = 4,
    };

    public static SaveRig Start(ICharacterStore store, CharacterSaveOptions? options = null, ContentDatabase? content = null, FakeTime? time = null) => new(store, options, content, time);

    public MapInstance On(EntityId id) => World.GetInstance(World.LocationOf(id)!);

    public Entity Get(EntityId id) => On(id).TryGetEntity(id, out var e) ? e : throw new InvalidOperationException($"{id} is gone");

    public ProgressionState Progress(EntityId id) => Progressions.TryGet(id, out var s) ? s : throw new InvalidOperationException("no progression");

    public InventoryState Bag(EntityId id) => Inventories.TryGet(id, out var s) ? s : throw new InvalidOperationException("no inventory");

    public int Count(EntityId id, string itemId) => Bag(id).Entries.Where(e => e.ItemId == itemId).Sum(e => e.Quantity);

    public long Put(EntityId id, string itemId, int quantity = 1)
    {
        var delta = new InventoryDelta();
        _items.Rules.TryAdd(Bag(id), itemId, quantity, int.MaxValue, Inventories.NextEntryId, delta).ShouldBeNull();
        return delta.Upserted.Last().Id;
    }

    public void Send(IIntent intent) => World.TryEnqueue(intent).ShouldBe(EnqueueResult.Accepted);

    public void Ticks(int n = 1)
    {
        for (var i = 0; i < n; i++)
            foreach (var instance in World.Instances) instance.Tick();
    }

    public void TickUntil(Func<bool> done, string what, int max = 300)
    {
        for (var i = 0; i < max && !done(); i++) Ticks();
        done().ShouldBeTrue(what);
    }

    public List<T> Of<T>() where T : Game.Server.Events.IGameEvent => Events.OfType<T>().ToList();

    /// <summary>Loads the character and puts it in the world, as the gateway does.</summary>
    public async Task<EntityId> EnterAsync(Guid character, GridCell? at = null)
    {
        var entered = await Saves.EnterAsync(character, default);
        entered.Refusal.ShouldBeNull(string.Join("\n", Log.Lines));
        return Spawn(entered, at);
    }

    public EntityId Spawn(EnterResult entered, GridCell? at = null)
    {
        var loaded = entered.Loaded;
        var id = World.Spawn(EntityKind.Player, loaded?.MapId ?? _start.MapId, at ?? loaded?.Cell ?? _start.Cell, loaded?.Facing ?? _start.Facing, e =>
        {
            e.Stats = loaded is null ? _start.CreateStats() : _start.CreateStats(loaded.Progression);
            e.DefinitionId = loaded?.Progression.JobId ?? _start.JobId;
            Keeper.Enter(e, loaded);
        }, entered.Ticket);
        Tickets[id] = entered.Ticket!;
        Ticks(2);
        return id;
    }

    /// <summary>Takes the character out of the world and waits for its last save.</summary>
    public async Task LeaveAsync(EntityId id)
    {
        var ticket = Tickets[id];
        World.Despawn(id);
        TickUntil(() => World.LocationOf(id) is null, "leaving");
        await ticket.Gone.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Asks for a copy now, and waits until it has landed.</summary>
    public async Task SaveAsync(EntityId id)
    {
        var ticket = Tickets[id];
        ticket.SaveSoon = true;
        var before = ticket.TakenVersion;
        TickUntil(() => ticket.TakenVersion > before, "a copy");
        var version = ticket.TakenVersion;
        await Poll.UntilAsync(() => ticket.LandedVersion, v => v >= version, "the save");
    }

    /// <summary>Every copy taken so far has landed.</summary>
    public Task SettledAsync() => Poll.UntilAsync(() => Saves.Stats.Waiting, w => w == 0, "the saves");

    /// <summary>The process dies: nothing more is written. The store must be one that can be made to refuse.</summary>
    public async Task StopAsync(TimeSpan? wait = null)
    {
        _drained = true;
        await Saves.DrainAsync(wait ?? TimeSpan.Zero);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_drained) await StopAsync();
    }
}

/// <summary>Content without some definitions, and other builders.</summary>
public static class SavedContent
{
    /// <summary>The sample content with some definitions gone, as a later version of the game might have it. It is not validated: the loader must cope with any.</summary>
    public static ContentDatabase Without(IEnumerable<string> ids, Func<ItemDef, ItemDef>? items = null, Func<SkillDef, SkillDef>? skills = null, Func<QuestDef, QuestDef>? quests = null,
        IReadOnlyDictionary<string, MapGrid>? grids = null)
    {
        var db = TestGrids.SampleContent;
        var gone = ids.ToHashSet();
        return new ContentDatabase(
            new Registry<JobDef>(db.Jobs.All.Where(j => !gone.Contains(j.Id))),
            new Registry<SkillDef>(db.Skills.All.Where(s => !gone.Contains(s.Id)).Select(s => skills?.Invoke(s) ?? s)),
            new Registry<ItemDef>(db.Items.All.Where(i => !gone.Contains(i.Id)).Select(i => items?.Invoke(i) ?? i)),
            db.Monsters, db.Statuses,
            new Registry<QuestDef>(db.Quests.All.Where(q => !gone.Contains(q.Id)).Select(q => quests?.Invoke(q) ?? q)),
            db.Npcs, db.Dialogue, db.Drops, db.Maps, db.ExpTables, db.Formulas, db.Elements, db.Balance, db.Appearance,
            grids: grids ?? db.Grids.Where(g => !gone.Contains(g.Key)).ToDictionary(g => g.Key, g => g.Value),
            outfits: new Registry<OutfitDef>(db.Outfits.All.Where(o => !gone.Contains(o.Id))));
    }

    /// <summary>A state with something in every field, of sample content.</summary>
    public static CharacterState Full(Func<long> newId, DateTime at) => new()
    {
        TakenAt = at,
        MapId = "map.starter_meadow",
        Cell = new GridCell(13, 12),
        Facing = "ne",
        Hp = 31,
        Sp = 7,
        SavePoint = new SavePoint("map.starter_meadow", new GridCell(12, 12)),
        BaseLevel = 14,
        BaseExp = 1234,
        JobId = "job.warden",
        JobLevel = 6,
        JobExp = 345,
        StatPoints = 5,
        SkillPoints = 2,
        Stats = new Dictionary<string, int> { ["str"] = 12, ["agi"] = 3, ["vit"] = 4, ["int"] = 5, ["dex"] = 6, ["luk"] = 7 },
        Skills = new Dictionary<string, int> { ["skill.twin_strike"] = 2 },
        ActiveQuests = [new SavedQuest("quest.first_hunt", 0, [2, 3, 0], 4)],
        CompletedQuests = ["quest.warden_trial"],
        UnlockedJobs = ["job.warden"],
        TurnInSequence = 9,
        OwedRewards = [new OwedQuestRewards("quest.gear_up", 10, 20, 1, 2, ["job.warden"], ["quest.gear_up#reward#0"], ["outfit.rain_cloak"])],
        Outfits = ["outfit.harvest_garland", "outfit.rain_cloak"],   // T-708
        OutfitSlots = new Dictionary<string, string> { ["head"] = "outfit.harvest_garland", ["back"] = "outfit.rain_cloak" },
        OutfitsHidden = true,
        Items =
        [
            new InventoryEntry(newId(), "item.bone_dirk", 1, "weapon") { Refine = 3 },
            new InventoryEntry(newId(), "item.padded_vest", 1) { Cards = ["item.gelcap_card"], Refine = 2 },
            new InventoryEntry(newId(), "item.healing_draught", 7),
            new InventoryEntry(newId(), "item.gel_glob", 3),
        ],
        Currency = 4321,
        SettledTurnIns = [4, 7],
        SkillCooldowns = new Dictionary<string, long> { ["skill.twin_strike"] = 9_000 },
        ItemCooldowns = new Dictionary<string, long> { ["item.healing_draught"] = 2_500 },
        AutopilotUsedMs = 123_456,
        AutopilotDayStartedAt = at.AddHours(-3),
        AsideItems = [new SetAsideItem(new InventoryEntry(newId(), "item.long_gone", 2), "unknown item")],
        Aside = [new SetAside(SetAsideKinds.Job, "job.long_gone", [5, 12], "unknown job"), new SetAside(SetAsideKinds.ActiveQuest, "quest.long_gone", [1, 3, 2, 0], "unknown quest")],
    };

    /// <summary>What every property holds, as text, so that two states can be compared field by field and a new field cannot be forgotten.</summary>
    public static SortedDictionary<string, string> Fields(CharacterState state)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in typeof(CharacterState).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            fields[property.Name] = System.Text.Json.JsonSerializer.Serialize(property.GetValue(state) switch
            {
                IReadOnlyDictionary<string, int> d => d.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"),
                IReadOnlyDictionary<string, long> d => d.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"),
                IReadOnlyDictionary<string, string> d => d.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"),   // jsonb keeps no key order (T-708)
                var value => value,
            });
        return fields;
    }
}
