using Game.Server.Events;
using Game.Server.World.Combat;
using Game.Server.World.Effects;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Monsters;

/// <summary>
/// Puts every <c>spawns</c> entry of the map grid in the world (ARCHITECTURE §6.6). On the instance's first tick each entry gets its
/// <c>count</c> monsters on random walkable cells of its area (the instance's seeded random, so a seed replays the same positions). Each
/// monster is a <see cref="EntityKind.Monster"/> entity whose <see cref="Entity.DefinitionId"/> is the monster id and whose
/// <see cref="Entity.Stats"/> come from the stats pipeline; its spawn cell is its home, announced with <see cref="EntitySpawned"/>.
/// </summary>
/// <remarks>
/// <para>Each entry has <c>count</c> slots. A slot holds a live monster or a respawn time, never both, so an entry never has more than
/// <c>count</c> monsters. On <see cref="EntityDied"/> for one of its monsters the slot is freed and refilled <c>respawn_seconds</c> of simulated time after the
/// tick the death was raised in, on a fresh random cell of the same area. The dead monster itself stays in the world as a corpse for
/// <c>death.corpse_ms</c> (so clients can play the death animation) and is despawned then; the respawn timer does not wait for it. A monster that
/// leaves the map some other way (despawned by someone else, warped) frees its slot the same way.</para>
/// <para><b>Summons</b> (T-501): a <see cref="SummonRequested"/> (a <c>summon</c> effect) is handled on the next tick. Its owner must still be alive on
/// this map. The <b>cap</b>: an owner has at most the effect's <c>count</c> live summons of that monster, so casting again only tops them up. Each
/// one is created like a spawned monster (stats from the catalog, <see cref="EntitySpawned"/> with spawn index -1, so the AI runs it from its cell as
/// home) on a random walkable cell within one cell of the requested one, with <see cref="Entity.Owner"/> set (<see cref="MonsterSummoned"/>). It is
/// never respawned: it is despawned when its <c>duration_ms</c> is over, or as a corpse after <c>death.corpse_ms</c> when it dies.</para>
/// <para>Respawn timers run whether or not anyone is near; the spawner's per-tick cost is one comparison per waiting slot. Allocation after
/// warm-up: only the entity, its stat sheet and the event when a monster (re)spawns.</para>
/// </remarks>
public sealed class MonsterSpawnerSystem : ITickSystem, Reload.IContentReloadable
{
    private static readonly IReadOnlyCollection<Type> Owned = [];

    private MonsterCatalog _catalog;
    private readonly Dictionary<long, Slot> _byEntity = new();
    private readonly List<EntityId> _died = new();
    private readonly List<EntityId> _left = new();
    private readonly Queue<(EntityId Id, long DueMs)> _corpses = new();   // one corpse time for all, so in despawn order
    private readonly List<SummonRequested> _summonRequests = new();
    private readonly Dictionary<long, Summoned> _summons = new();          // live summons by entity id
    private readonly List<long> _expired = new();
    private int _corpseMs;
    private SpawnEntry[] _entries = [];
    private MapGrid? _grid;
    private EventBus? _subscribed;
    private long _lastTickMs;

    /// <summary>
    /// Creates a spawner that reads monsters from <paramref name="catalog"/>. A dead monster stays <paramref name="corpseMs"/> simulated
    /// milliseconds (<c>death.corpse_ms</c>) before it is despawned.
    /// </summary>
    public MonsterSpawnerSystem(MonsterCatalog catalog, int corpseMs = 0)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (corpseMs < 0) throw new ArgumentOutOfRangeException(nameof(corpseMs), corpseMs, "corpseMs must not be negative.");
        _corpseMs = corpseMs;
    }

    /// <summary>
    /// T-801: the instance has removed every monster (corpses and summons too) before this; the slot table is built again from the grid with the
    /// new definitions, every slot due at once, so the next tick spawns the map's monsters afresh.
    /// </summary>
    public void Reload(Reload.ServerContent next)
    {
        _catalog = next.Monsters;
        _corpseMs = Math.Max(0, next.Balance.Death.CorpseMs);
        _grid = null;   // Setup on the next tick
        _entries = [];
        _byEntity.Clear();
        _summons.Clear();
        _corpses.Clear();
        _died.Clear();
        _left.Clear();
        _summonRequests.Clear();
    }

    /// <inheritdoc />
    public string Name => "monster_spawner";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Dead monsters still lying in the world, waiting to be despawned.</summary>
    public int CorpseCount => _corpses.Count;

    /// <summary>Live monsters this spawner owns.</summary>
    public int LiveCount => _byEntity.Count;

    /// <summary>Live summoned monsters (they belong to no spawn entry).</summary>
    public int SummonCount => _summons.Count;

    /// <summary>Monsters created so far, first spawns and respawns.</summary>
    public long Spawned { get; private set; }

    /// <summary>Live monsters of spawn entry <paramref name="index"/>.</summary>
    public int LiveCountOf(int index)
    {
        var live = 0;
        foreach (var slot in _entries[index].Slots)
            if (slot.Entity is not null) live++;
        return live;
    }

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        var instance = context.Instance;
        Subscribe(instance);
        if (!ReferenceEquals(_grid, instance.Grid)) Setup(context);

        foreach (var id in _died)
        {
            if (_summons.Remove(id.Value))
            {
                _corpses.Enqueue((id, _lastTickMs + _corpseMs));   // a summon is never respawned
                continue;
            }
            if (!_byEntity.TryGetValue(id.Value, out var slot)) continue;
            Free(slot);   // the respawn timer counts from the death tick (the tick before this one)
            _corpses.Enqueue((id, _lastTickMs + _corpseMs));
        }
        _died.Clear();
        foreach (var id in _left)
        {
            _summons.Remove(id.Value);
            if (_byEntity.TryGetValue(id.Value, out var slot)) Free(slot);
        }
        _left.Clear();
        while (_corpses.Count > 0 && _corpses.Peek().DueMs <= context.SimTimeMs)
        {
            var id = _corpses.Dequeue().Id;
            if (instance.TryGetEntity(id, out _)) context.Despawn(id);   // not if someone else removed it meanwhile
        }

        var now = context.SimTimeMs;
        _lastTickMs = now;
        foreach (var entry in _entries)
        foreach (var slot in entry.Slots)
            if (slot.Entity is null && slot.DueMs <= now) Fill(context, entry, slot);
        ExpireSummons(context, now);
        for (var i = 0; i < _summonRequests.Count; i++) Summon(context, _summonRequests[i], now);
        _summonRequests.Clear();
    }

    private void Summon(TickContext context, SummonRequested request, long now)
    {
        var instance = context.Instance;
        if (!instance.TryGetEntity(request.Owner, out var owner) || owner.Dead) return;
        if (!_catalog.TryGet(request.MonsterId, out var def))
        {
            context.Log.Error($"[{instance.MapId}] summon of unknown monster '{request.MonsterId}' (the validator should have caught this).");
            return;
        }
        var live = 0;
        foreach (var s in _summons.Values)
            if (s.Owner == request.Owner && string.Equals(s.MonsterId, def.Id, StringComparison.Ordinal)) live++;
        var expires = request.DurationMs is { } ms ? now + ms : long.MaxValue;
        for (var n = live; n < request.Count; n++)
        {
            if (!DropRoller.TryPlace(instance.Grid, request.Near, 1, context.Random, out var cell)) return;
            var facing = owner.Facing;
            var ownerId = owner.Id;
            var id = context.Spawn(EntityKind.Monster, cell, facing, e =>
            {
                e.DefinitionId = def.Id;
                e.Stats = _catalog.CreateStats(def);
                e.Owner = ownerId;
                e.SummonedByPlayer = owner.Kind == EntityKind.Player;
            });
            _summons.Add(id.Value, new Summoned(ownerId, def.Id, expires));
            Spawned++;
            context.Publish(new EntitySpawned(id, EntityKind.Monster, def.Id, cell, -1));
            context.Publish(new MonsterSummoned(ownerId, id, def.Id, cell, expires));
        }
    }

    /// <summary>Despawns the summons whose time is over (a living one; a dead one is a corpse on the corpse timer already).</summary>
    private void ExpireSummons(TickContext context, long now)
    {
        if (_summons.Count == 0) return;
        foreach (var (id, s) in _summons)
            if (s.ExpiresAtMs <= now) _expired.Add(id);
        foreach (var id in _expired)
        {
            _summons.Remove(id);
            var entityId = new EntityId(id);
            if (context.Instance.TryGetEntity(entityId, out _)) context.Despawn(entityId);
        }
        _expired.Clear();
    }

    private void Subscribe(MapInstance instance)
    {
        if (!ReferenceEquals(_subscribed, instance.Events)) SubscribeTo(instance);
    }

    /// <summary>Separate from <see cref="Subscribe"/> so the handlers' closure is only allocated when subscribing, not every tick.</summary>
    private void SubscribeTo(MapInstance instance)
    {
        var events = instance.Events;
        _subscribed = events;
        events.Subscribe<EntityDied>(e => _died.Add(e.Entity));
        events.Subscribe<SummonRequested>(e => _summonRequests.Add(e));
        events.Subscribe<EntityLeftMap>(e =>
        {
            // A same-map warp leaves and re-enters at once; only a monster no longer in the instance frees its slot.
            if (e.Kind == EntityKind.Monster && !instance.TryGetEntity(e.Entity, out _)) _left.Add(e.Entity);
        });
    }

    /// <summary>Builds the slot table from the grid; every slot is due now, so the first tick fills them all.</summary>
    private void Setup(TickContext context)
    {
        var grid = context.Instance.Grid;
        _grid = grid;
        _byEntity.Clear();
        var entries = new List<SpawnEntry>();
        for (var i = 0; i < grid.Spawns.Count; i++)
        {
            var spawn = grid.Spawns[i];
            if (!_catalog.TryGet(spawn.MonsterId, out var def))
            {
                context.Log.Error($"[{grid.MapId}] spawn {i}: unknown monster '{spawn.MonsterId}'; skipped (the validator should have caught this).");
                continue;
            }
            var cells = WalkableCells(grid, spawn.Area);
            if (cells.Length == 0)
            {
                context.Log.Error($"[{grid.MapId}] spawn {i} ({spawn.MonsterId}): no walkable cell in area {spawn.Area}; skipped.");
                continue;
            }
            var slots = new Slot[Math.Max(0, spawn.Count)];
            var entry = new SpawnEntry(i, def, cells, (long)Math.Round(spawn.RespawnSeconds * 1000), slots);
            for (var s = 0; s < slots.Length; s++) slots[s] = new Slot(entry) { DueMs = long.MinValue };
            entries.Add(entry);
        }
        _entries = entries.ToArray();
        context.Log.Info($"[{grid.MapId}] spawning {_entries.Sum(e => e.Slots.Length)} monster(s) from {_entries.Length} spawn entr{(_entries.Length == 1 ? "y" : "ies")}.");
    }

    private void Fill(TickContext context, SpawnEntry entry, Slot slot)
    {
        var cell = entry.Cells[context.Random.Next(entry.Cells.Length)];
        var facing = GridDirection.Name(context.Random.Next(Directions.All.Count));
        var def = entry.Def;
        var id = context.Spawn(EntityKind.Monster, cell, facing, e =>
        {
            e.DefinitionId = def.Id;
            e.Stats = _catalog.CreateStats(def);
        });
        slot.Entity = id;
        _byEntity.Add(id.Value, slot);
        Spawned++;
        context.Publish(new EntitySpawned(id, EntityKind.Monster, def.Id, cell, entry.Index));
    }

    /// <summary>Empties a slot; it refills <c>respawn_seconds</c> after the tick the loss was seen in.</summary>
    private void Free(Slot slot)
    {
        _byEntity.Remove(slot.Entity!.Value.Value);
        slot.Entity = null;
        slot.DueMs = _lastTickMs + slot.Entry.RespawnMs;
    }

    private static GridCell[] WalkableCells(MapGrid grid, GridRect area)
    {
        var cells = new List<GridCell>();
        for (var y = area.Y; y < area.Y + area.H; y++)
        for (var x = area.X; x < area.X + area.W; x++)
            if (grid.IsWalkable(x, y)) cells.Add(new GridCell(x, y));
        return cells.ToArray();
    }

    private sealed class SpawnEntry(int index, MonsterDef def, GridCell[] cells, long respawnMs, Slot[] slots)
    {
        public readonly int Index = index;
        public readonly MonsterDef Def = def;
        public readonly GridCell[] Cells = cells;
        public readonly long RespawnMs = respawnMs;
        public readonly Slot[] Slots = slots;
    }

    private sealed record Summoned(EntityId Owner, string MonsterId, long ExpiresAtMs);

    private sealed class Slot(SpawnEntry entry)
    {
        public readonly SpawnEntry Entry = entry;
        public EntityId? Entity;
        public long DueMs;
    }
}
