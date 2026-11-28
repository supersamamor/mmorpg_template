using System.Diagnostics;
using Game.Server.Events;
using Game.Server.World.Aoi;
using Game.Shared.Random;
using Game.Shared.Time;
using Game.Shared.World;

namespace Game.Server.World;

/// <summary>Running totals for one instance. Written only by the instance's tick; reads from other threads may be slightly stale.</summary>
public sealed class InstanceCounters
{
    /// <summary>Intents routed to a system.</summary>
    public long IntentsRouted { get; internal set; }

    /// <summary>Intents dropped because their entity is not in this instance (never was, left, or still in flight).</summary>
    public long IntentsDroppedUnknownEntity { get; internal set; }

    /// <summary>Intents dropped because their entity is <see cref="Entity.Dead"/> (except <see cref="IDeadEntityIntent"/>s).</summary>
    public long IntentsDroppedDeadEntity { get; internal set; }

    /// <summary>Intents dropped because no system owns their type.</summary>
    public long IntentsDroppedUnrouted { get; internal set; }

    /// <summary><see cref="Automated"/> intents dropped because the player took over in the same tick (<see cref="ICancelsAutomation"/>).</summary>
    public long IntentsDroppedCancelled { get; internal set; }

    /// <summary>System <see cref="ITickSystem.Tick"/> calls that threw.</summary>
    public long SystemErrors { get; internal set; }

    /// <summary>Event handler calls that threw.</summary>
    public long HandlerErrors { get; internal set; }

    /// <summary>Structural changes that did nothing (unknown entity, duplicate add).</summary>
    public long StructuralIgnored { get; internal set; }
}

/// <summary>
/// The simulation of one map: its entities, its intent queue, its systems and its event bus. Single-threaded: only one
/// thread calls <see cref="Tick"/> (the host gives each instance its own), and only during <see cref="Tick"/> are
/// entities read or changed. Other threads talk to it only through <see cref="TryEnqueue"/> and the world's structural
/// inbox. No I/O, no wall clock: tick numbers come from the tick count, randomness from the injected <see cref="IRandom"/>,
/// so the same inputs at the same tick numbers give the same state and events.
/// </summary>
/// <remarks>
/// Tick phases (ARCHITECTURE §3): 1. drain intents and the structural inbox, route intents to their systems;
/// 2. run systems in registration order; 3. apply structural changes (inbox first, then the systems' requests, each in
/// order); 4. dispatch this tick's events in raise order;
/// 5. outbound: systems that implement <see cref="IOutboundPhase"/> (the AOI) see the final state and send each observer its view.
/// </remarks>
public sealed class MapInstance
{
    /// <summary>A system slower than this logs a warning (wall time, via the injected clock; does not affect state).</summary>
    public const int SlowSystemWarnMs = TickRate.IntervalMs / 2;

    private readonly IReadOnlyList<ITickSystem> _systems;
    private readonly IOutboundPhase[] _outbound;
    private readonly Dictionary<Type, int> _routes = new();
    private readonly List<IIntent>[] _routed;
    private readonly IClock _clock;
    private readonly IServerLog _log;
    private readonly IntentQueue _intents;
    private readonly TickContext _context;
    private readonly Dictionary<EntityId, Entity> _entities = new();
    private readonly List<Entity> _ordered = new();
    private readonly List<Entity> _players = new();
    private readonly List<StructuralChange> _deferred = new();
    private readonly HashSet<EntityId> _cancelled = new();       // entities whose automated intents of this tick are dropped
    private readonly List<IIntent> _early = new();               // cancels for an entity still on its way here: offered again next tick
    private readonly object _inboxLock = new();
    private List<StructuralChange> _inbox = new();
    private List<StructuralChange> _inboxDrained = new();
    private List<IIntent> _drained = new();
    private Reload.ContentSwap? _reload;   // T-801: a content set waiting for the next tick
    private long _generation;

    internal MapInstance(GameWorld world, MapGrid grid, IReadOnlyList<ITickSystem> systems, IRandom random, IClock clock, IServerLog log, int intentCapacity)
    {
        World = world;
        Grid = grid;
        _systems = systems;
        _outbound = systems.OfType<IOutboundPhase>().ToArray();
        Random = random;
        _clock = clock;
        _log = log;
        _intents = new IntentQueue(intentCapacity);
        _context = new TickContext(this);
        Events = new EventBus(OnHandlerError);
        _routed = new List<IIntent>[systems.Count];
        for (var i = 0; i < systems.Count; i++)
        {
            _routed[i] = new List<IIntent>();
            foreach (var type in systems[i].IntentTypes) _routes.Add(type, i);
        }
    }

    /// <summary>The world this instance belongs to.</summary>
    public GameWorld World { get; }

    /// <summary>The map id.</summary>
    public string MapId => Grid.MapId;

    /// <summary>The map's immutable logic grid.</summary>
    public MapGrid Grid { get; }

    /// <summary>This instance's random source (one PCG32 stream per instance).</summary>
    public IRandom Random { get; }

    /// <summary>The log this instance reports to.</summary>
    internal IServerLog Log => _log;

    /// <summary>This instance's event bus.</summary>
    public EventBus Events { get; }

    /// <summary>Ticks completed, which is also the number the next tick will have.</summary>
    public long TickNumber { get; private set; }

    /// <summary>Running totals.</summary>
    public InstanceCounters Counters { get; } = new();

    /// <summary>Durations of the most recent ticks (recorded by <see cref="Tick"/>).</summary>
    public TickTimings Timings { get; } = new();

    /// <summary>A read-only copy of the tick timings and counters. Safe to call from any thread; allocates, so not for the tick itself.</summary>
    public InstanceStats GetStats()
    {
        var c = Counters;
        return new InstanceStats(MapId, TickNumber, Timings.Snapshot(), new CounterSnapshot(
            c.IntentsRouted, c.IntentsDroppedUnknownEntity, c.IntentsDroppedDeadEntity, c.IntentsDroppedUnrouted,
            c.SystemErrors, c.HandlerErrors, c.StructuralIgnored));
    }

    /// <summary>The intent queue (for its bound and counters).</summary>
    public IntentQueue Intents => _intents;

    /// <summary>The systems, in run order.</summary>
    public IReadOnlyList<ITickSystem> Systems => _systems;

    /// <summary>Entities in the order they were added. Read only from this instance's tick (or when it is not running).</summary>
    public IReadOnlyList<Entity> Entities => _ordered;

    /// <summary>
    /// The entities of kind <see cref="EntityKind.Player"/>, in the order they were added: a small read-only index so systems that care only about
    /// players near something (monster AI) never walk every entity. Same threading rule as <see cref="Entities"/>.
    /// </summary>
    public IReadOnlyList<Entity> Players => _players;

    /// <summary>Looks up an entity of this instance. Same threading rule as <see cref="Entities"/>.</summary>
    public bool TryGetEntity(EntityId id, out Entity entity) => _entities.TryGetValue(id, out entity!);

    /// <summary>Queues an intent for the next tick. Thread-safe. Prefer <see cref="GameWorld.TryEnqueue"/>, which finds the instance.</summary>
    public EnqueueResult TryEnqueue(IIntent intent) => _intents.TryEnqueue(intent);

    /// <summary>
    /// T-801: asks this instance to take a new content set between two ticks: at the start of its next tick, before anything of that tick happens.
    /// Thread-safe; a later request before then replaces an earlier one (only the newest set is swapped in).
    /// </summary>
    public void RequestReload(Reload.ContentSwap swap) => Interlocked.Exchange(ref _reload, swap ?? throw new ArgumentNullException(nameof(swap)));

    /// <summary>The <see cref="Reload.ServerContent.Generation"/> of the content this instance runs (0 until a hot reload).</summary>
    public long ContentGeneration => Interlocked.Read(ref _generation);

    /// <summary>Runs one tick (all five phases). Never throws because of a system or event handler.</summary>
    public void Tick()
    {
        var startedAt = Stopwatch.GetTimestamp();
        var tick = TickNumber;
        _context.TickNumber = tick;
        if (Interlocked.Exchange(ref _reload, null) is { } swap) ApplyReload(swap, tick);

        // Phase 1: take everything that arrived before this tick began.
        _drained = _intents.DrainTo(_drained);
        lock (_inboxLock) (_inbox, _inboxDrained) = (_inboxDrained, _inbox);
        foreach (var intent in _drained)
            if (intent is ICancelsAutomation) _cancelled.Add(intent.Entity);
        foreach (var intent in _drained) Route(intent);

        // Phase 2: systems in order, each seeing its own intents.
        for (var i = 0; i < _systems.Count; i++) RunSystem(i, tick);

        // Phase 3: structural changes, never mid-system.
        foreach (var change in _inboxDrained) Apply(change, tick);
        foreach (var change in _deferred) Apply(change, tick);

        // Phase 4: events raised this tick, in raise order. Handlers that publish queue for the next tick.
        Events.Dispatch();

        // Phase 5: outbound. The state is final for this tick; observers are told what changed near them.
        foreach (var outbound in _outbound) RunOutbound(outbound, tick);

        _drained.Clear();
        _cancelled.Clear();
        foreach (var intent in _early) _intents.TryEnqueue(intent);
        _early.Clear();
        _inboxDrained.Clear();
        _deferred.Clear();
        foreach (var list in _routed) list.Clear();
        _context.Intents = [];
        TickNumber = tick + 1;
        Timings.Record((long)Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds);
    }

    /// <summary>
    /// T-801: the swap, between ticks, all at once for this instance (ARCHITECTURE §5.5).
    /// 1. Every monster and NPC is removed (<c>EntityLeftMap</c>, as a despawn) and every player is taken off the map (<c>EntityLeftMap</c>, as a warp):
    ///    the old systems forget what they built for them from the old content. Ground items stay.
    /// 2. Every system that reads content (<see cref="Reload.IContentReloadable"/>) takes the new set, in pipeline order.
    /// 3. The players come back on the cell they stood on (<c>EntityEnteredMap</c>, as a same-map warp) with a stat sheet built from the new content; their HP and SP
    ///    stand (a dead player stays dead) and their statuses end. The new systems set them up as on any arrival: worn items, passives, markers, a snapshot.
    /// 4. The spawner and the dialogue system put the map's monsters and NPCs back in this tick.
    /// Events still waiting from the last tick's handlers are dispatched in step 1, a tick early. A system that throws is logged and the swap goes on.
    /// </summary>
    private void ApplyReload(Reload.ContentSwap swap, long tick)
    {
        var started = Stopwatch.GetTimestamp();
        var players = _players.ToList();
        var removed = 0;
        foreach (var entity in _ordered.Where(e => e.Kind is EntityKind.Monster or EntityKind.Npc).ToList())
        {
            ApplyRemove(entity.Id);
            removed++;
        }
        foreach (var player in players)
        {
            Detach(player.Id, out _);   // the world still has it here: systems treat it as a warp, not as leaving the world
            Events.Publish(new EntityLeftMap(player.Id, player.Kind, MapId, player.Cell, MapChangeReason.Warp));
        }
        Events.Dispatch();

        foreach (var system in _systems)
        {
            if (system is not Reload.IContentReloadable reloadable) continue;
            try
            {
                reloadable.Reload(swap.Next);
            }
            catch (Exception ex)
            {
                Counters.SystemErrors++;
                _log.Error($"[{MapId}] tick {tick}: system '{system.Name}' could not take the reloaded content; it keeps parts of the old.", ex);
            }
        }

        foreach (var player in players)
        {
            try
            {
                if (swap.PlayerSheet(player) is { } sheet)
                {
                    var hp = player.Vitals?.Hp ?? 0;
                    var sp = player.Vitals?.Sp ?? 0;
                    player.Stats = sheet;
                    player.Vitals = player.Vitals is null ? null : Combat.Vitals.Restore(sheet, hp, sp);
                }
                if (player.Statuses is { } set) set.MapId = null;   // timed on the old content: combat ends them on arrival
            }
            catch (Exception ex)
            {
                Counters.HandlerErrors++;
                _log.Error($"[{MapId}] tick {tick}: the new stat sheet of {player.Id} could not be built; it keeps the old one.", ex);
            }
            if (!_entities.TryAdd(player.Id, player)) continue;
            _ordered.Add(player);
            _players.Add(player);
            Events.Publish(new EntityEnteredMap(player.Id, player.Kind, MapId, player.Cell, MapChangeReason.Warp));
        }
        Events.Dispatch();
        foreach (var player in players)
        {
            if (player.Vitals is not { } vitals) continue;
            vitals.EndRestore();
            if (player.Dead) vitals.Hp = 0;
        }
        Interlocked.Exchange(ref _generation, swap.Next.Generation);
        swap.Swapped(this, players.Count, removed, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Posts a structural change from outside the tick (another instance, the world). Thread-safe.</summary>
    internal void Post(StructuralChange change)
    {
        lock (_inboxLock) _inbox.Add(change);
    }

    /// <summary>Queues a structural change requested during this instance's tick.</summary>
    internal void Defer(StructuralChange change) => _deferred.Add(change);

    private void Route(IIntent intent)
    {
        if (intent is Automated automated)
        {
            if (_cancelled.Contains(intent.Entity))
            {
                Counters.IntentsDroppedCancelled++;
                return;
            }
            intent = automated.Intent;
        }
        if (!_entities.TryGetValue(intent.Entity, out var entity))
        {
            if (intent is ICancelsAutomation && ReferenceEquals(World.InstanceOf(intent.Entity), this)) _early.Add(intent);
            else Counters.IntentsDroppedUnknownEntity++;
        }
        else if (entity.Dead && intent is not IDeadEntityIntent)
            Counters.IntentsDroppedDeadEntity++;
        else if (!_routes.TryGetValue(intent.GetType(), out var index))
            Counters.IntentsDroppedUnrouted++;
        else
        {
            _routed[index].Add(intent);
            Counters.IntentsRouted++;
        }
    }

    private void RunSystem(int index, long tick)
    {
        var system = _systems[index];
        _context.Intents = _routed[index];
        var start = _clock.NowMilliseconds;
        try
        {
            system.Tick(_context);
        }
        catch (Exception ex)
        {
            Counters.SystemErrors++;
            _log.Error($"[{MapId}] tick {tick}: system '{system.Name}' threw; continuing with the next system.", ex);
        }
        var elapsed = _clock.NowMilliseconds - start;
        if (elapsed > SlowSystemWarnMs)
            _log.Warn($"[{MapId}] tick {tick}: system '{system.Name}' took {elapsed} ms (warn above {SlowSystemWarnMs} ms).");
    }

    private void RunOutbound(IOutboundPhase outbound, long tick)
    {
        _context.Intents = [];
        try
        {
            outbound.Outbound(_context);
        }
        catch (Exception ex)
        {
            Counters.SystemErrors++;
            _log.Error($"[{MapId}] tick {tick}: the outbound phase of '{(outbound as ITickSystem)?.Name ?? outbound.GetType().Name}' threw; continuing.", ex);
        }
    }

    private void Apply(StructuralChange change, long tick)
    {
        try
        {
            switch (change)
            {
                case StructuralChange.Add add: ApplyAdd(add.Entity, add.Reason, add.Initialize); break;
                case StructuralChange.Remove remove: ApplyRemove(remove.Id); break;
                case StructuralChange.Warp warp: ApplyWarp(warp); break;
            }
        }
        catch (Exception ex)
        {
            Counters.StructuralIgnored++;
            _log.Error($"[{MapId}] tick {tick}: applying {change.GetType().Name} failed.", ex);
        }
    }

    private void ApplyAdd(Entity entity, MapChangeReason reason, Action<Entity>? initialize)
    {
        if (World.RemovalPending(entity.Id))
        {
            // Despawned while it was on its way here (warp in flight, or a spawn not yet applied): it never arrives. No event: it entered
            // nowhere, and its old map already said it left.
            World.ClearLocation(entity.Id, this);
            World.RemovalDone(entity.Id);
            if (entity.Save is not null) Keep(k => k.Dropped(entity, reason));   // T-702: it is saved with the map it was going to
            return;
        }
        if (!_entities.TryAdd(entity.Id, entity))
        {
            Counters.StructuralIgnored++;
            return;
        }
        _ordered.Add(entity);
        if (entity.Kind == EntityKind.Player) _players.Add(entity);
        entity.MapId = MapId;
        World.SetLocation(entity.Id, this);
        if (initialize is not null)
        {
            try
            {
                initialize(entity);
            }
            catch (Exception ex)
            {
                Counters.HandlerErrors++;
                _log.Error($"[{MapId}] tick {_context.TickNumber}: initializing {entity.Id} threw; it joins as it is.", ex);
            }
        }
        Events.Publish(new EntityEnteredMap(entity.Id, entity.Kind, MapId, entity.Cell, reason));
    }

    private void ApplyRemove(EntityId id)
    {
        if (World.Keeper is { } keeper && _entities.TryGetValue(id, out var leaving) && leaving.Save is not null)
        {
            // T-702: a saved character leaves when nothing it began is half done, and its state is copied here, before any system forgets it.
            if (World.RemovalPending(id) && !keeper.MayLeave(leaving, _context.TickNumber))
            {
                Post(new StructuralChange.Remove(id));
                return;
            }
            Keep(k => k.Leaving(leaving));
        }
        if (!Detach(id, out var entity))
        {
            // Not here. A world despawn follows the entity: to the instance a warp took it to, or (it is on its way here) to our next tick,
            // where the add sees the pending removal and drops it. A system's despawn of an entity it does not hold is ignored as before.
            if (World.RemovalPending(id) && World.InstanceOf(id) is { } now)
            {
                now.Post(new StructuralChange.Remove(id));
                return;
            }
            World.RemovalDone(id);
            Counters.StructuralIgnored++;
            return;
        }
        World.RemovalDone(id);
        World.ClearLocation(id, this);
        Events.Publish(new EntityLeftMap(id, entity.Kind, MapId, entity.Cell, MapChangeReason.Spawn));
    }

    /// <summary>Runs a keeper's step; one that throws is logged and the structural change goes on.</summary>
    private void Keep(Action<IEntityKeeper> step)
    {
        try
        {
            if (World.Keeper is { } keeper) step(keeper);
        }
        catch (Exception ex)
        {
            Counters.HandlerErrors++;
            _log.Error($"[{MapId}] tick {_context.TickNumber}: the keeper threw; the character may not have been saved.", ex);
        }
    }

    private void ApplyWarp(StructuralChange.Warp warp)
    {
        if (!_entities.TryGetValue(warp.Id, out var entity))
        {
            Counters.StructuralIgnored++;
            return;
        }
        if (!World.TryGetInstance(warp.TargetMapId, out var target))
        {
            Events.Publish(new WarpRejected(warp.Id, MapId, warp.TargetMapId, warp.TargetCell, "unknown map"));
            return;
        }
        if (!target.Grid.IsWalkable(warp.TargetCell.X, warp.TargetCell.Y))
        {
            Events.Publish(new WarpRejected(warp.Id, MapId, warp.TargetMapId, warp.TargetCell, "target cell not walkable"));
            return;
        }

        Events.Publish(new EntityLeftMap(entity.Id, entity.Kind, MapId, entity.Cell, MapChangeReason.Warp));
        if (target == this)
        {
            entity.Cell = warp.TargetCell;
            Events.Publish(new EntityEnteredMap(entity.Id, entity.Kind, MapId, entity.Cell, MapChangeReason.Warp));
            return;
        }

        Detach(entity.Id, out _);
        entity.Cell = warp.TargetCell;
        entity.MapId = target.MapId;
        World.SetLocation(entity.Id, target);
        target.Post(new StructuralChange.Add(entity, MapChangeReason.Warp));
    }

    private bool Detach(EntityId id, out Entity entity)
    {
        if (!_entities.Remove(id, out entity!)) return false;
        _ordered.Remove(entity);
        if (entity.Kind == EntityKind.Player) _players.Remove(entity);
        return true;
    }

    private void OnHandlerError(IGameEvent e, Exception ex)
    {
        Counters.HandlerErrors++;
        _log.Error($"[{MapId}] tick {_context.TickNumber}: a handler for {e.Name} threw; continuing with the next handler.", ex);
    }
}
