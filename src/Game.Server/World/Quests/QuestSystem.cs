using Game.Content.Registry;
using Game.Server.Events;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Quests;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Quests;

/// <summary>The quests and the quest numbers the quest system reads. Immutable after construction, so shared by every instance.</summary>
public sealed class QuestContent
{
    /// <summary>Creates the content from quest definitions and <c>quests.max_active</c>.</summary>
    public QuestContent(IEnumerable<QuestDef> quests, QuestBalanceDef balance)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(balance);
        if (balance.MaxActive < 1) throw new ArgumentOutOfRangeException(nameof(balance), "quests.max_active must be at least 1.");
        Quests = quests.ToList();
        Balance = balance;
    }

    /// <summary>The loaded database's quests.</summary>
    public static QuestContent From(ContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new QuestContent(content.Quests.All, content.Balance.Quests);
    }

    /// <summary>Every quest.</summary>
    public IReadOnlyList<QuestDef> Quests { get; }

    /// <summary>The <c>quests</c> section of <c>balance.yaml</c>.</summary>
    public QuestBalanceDef Balance { get; }

    /// <summary>New rules over these quests (one per system: the rules are not thread-safe only in what they are given).</summary>
    public QuestRules CreateRules() => new(Quests, Balance.MaxActive);
}

/// <summary>
/// Quests (ARCHITECTURE §6.4, T-602): the per-character state machine, driven only by the event bus (hard rule 7). It never calls combat, the inventory or dialogue: it
/// listens to <see cref="QuestStartRequested"/> (a dialogue's <c>start_quest</c>), <see cref="MonsterKilled"/>, <see cref="ItemUsed"/>, <see cref="ItemEquipped"/>,
/// <see cref="NpcTalked"/>, <see cref="CellReached"/>, arrivals, <see cref="InventoryChanged"/> and <see cref="PlayerProgressed"/>, and asks the inventory
/// (<see cref="QuestItemsRequested"/>) and the progression host (<see cref="QuestRewardsRequested"/>) to pay. Owns <see cref="AbandonQuest"/>. Runs after the inventory.
/// </summary>
/// <remarks>
/// <para><b>State</b> is in the shared progression store (<see cref="ProgressionState.ActiveQuests"/>, <see cref="ProgressionState.CompletedQuests"/>; in memory until T-702),
/// changed only through <see cref="QuestRules"/> on the tick thread of the instance that holds the player.</para>
/// <para><b>Turn-in.</b> When every objective of the current step is met, the step's consumed items, and for the last step the item and currency rewards, go to the inventory
/// as one transaction (all or nothing, rule 10). When it is applied the step advances; the last step completes the quest and sends the other rewards (EXP, points, unlocks,
/// statuses: none can fail) to the progression host. <b>Full bag:</b> when the inventory refuses (no room, too heavy), nothing changed and the quest <b>waits</b>: the step stays
/// met and <i>held</i> (the reason goes to the player) and is tried again whenever the player's bag changes or it arrives on a map. Nothing is dropped on the ground.</para>
/// <para><b>Events are handled on the next tick</b>, in raise order: an event raised in tick N is dispatched in its phase 4 and acted on in this system's tick N + 1.</para>
/// </remarks>
public sealed class QuestSystem : ITickSystem
{
    private static readonly IReadOnlyCollection<Type> Owned = [typeof(AbandonQuest)];

    private readonly QuestRules _rules;
    private readonly IPlayerProgressionStore _progressions;
    private readonly IInventoryStore? _inventories;
    private readonly Dictionary<long, Markers> _markers = new();
    private readonly SortedSet<long> _dirty = new();
    private readonly SortedSet<string> _offer = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _turnIn = new(StringComparer.Ordinal);
    private readonly QuestPlayer _player = new();
    private List<IGameEvent> _events = new();
    private List<IGameEvent> _handling = new();
    private MapInstance? _instance;
    private TickContext? _context;
    private long _nextToken = 1;

    /// <summary>
    /// Creates the quest system for one instance. <paramref name="progressions"/> is shared by every instance (quest state lives there); <paramref name="inventories"/> is read
    /// (never changed) for <c>collect</c> and <c>equip</c>: without it the bag counts as empty.
    /// </summary>
    public QuestSystem(QuestContent content, IPlayerProgressionStore progressions, IInventoryStore? inventories = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _rules = content.CreateRules();
        _progressions = progressions ?? throw new ArgumentNullException(nameof(progressions));
        _inventories = inventories;
    }

    /// <inheritdoc />
    public string Name => "quest";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>The rules this system runs (tests read the markers and entries through it).</summary>
    public QuestRules Rules => _rules;

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        _context = context;
        if (!ReferenceEquals(_instance, context.Instance)) Setup(context);

        var intents = context.Intents;
        for (var i = 0; i < intents.Count; i++)
            if (intents[i] is AbandonQuest abandon) Abandon(abandon.Entity, abandon.QuestId);

        (_events, _handling) = (_handling, _events);
        for (var i = 0; i < _handling.Count; i++) Handle(_handling[i]);
        _handling.Clear();

        foreach (var id in _dirty) PublishMarkers(new EntityId(id), announce: true);
        _dirty.Clear();
    }

    private void Setup(TickContext context)
    {
        _instance = context.Instance;
        var events = _instance.Events;
        events.Subscribe<QuestStartRequested>(e => _events.Add(e));
        events.Subscribe<MonsterKilled>(e => _events.Add(e));
        events.Subscribe<ItemUsed>(e => _events.Add(e));
        events.Subscribe<ItemEquipped>(e => _events.Add(e));
        events.Subscribe<NpcTalked>(e => _events.Add(e));
        events.Subscribe<CellReached>(e =>
        {
            if (IsQuestingPlayer(e.Entity)) _events.Add(e);
        });
        events.Subscribe<EntityEnteredMap>(e =>
        {
            if (e.Kind == EntityKind.Player) _events.Add(e);
        });
        events.Subscribe<EntityLeftMap>(OnLeft);
        events.Subscribe<InventoryChanged>(e => _events.Add(e));
        events.Subscribe<PlayerProgressed>(e => _events.Add(e));
        events.Subscribe<QuestItemsResolved>(e => _events.Add(e));
    }

    private bool IsQuestingPlayer(EntityId id) => _progressions.TryGet(id, out var state) && state.ActiveQuests.Count > 0;

    private void OnLeft(EntityLeftMap e)
    {
        if (e.Kind != EntityKind.Player || _instance!.TryGetEntity(e.Entity, out _)) return;   // a same-map warp comes straight back
        _markers.Remove(e.Entity.Value);
        _dirty.Remove(e.Entity.Value);
    }

    // ---- events -----------------------------------------------------------------------------------------------------------------------

    private void Handle(IGameEvent e)
    {
        switch (e)
        {
            case QuestStartRequested start: Start(start.Player, start.QuestId); break;
            case MonsterKilled killed:
                for (var i = 0; i < killed.Credited.Count; i++) Count(killed.Credited[i], p => _rules.OnKill(p, killed.MonsterId));
                break;
            case ItemUsed used: Count(used.Player, p => _rules.OnUse(p, used.ItemId)); break;
            case ItemEquipped equipped: Count(equipped.Player, p => _rules.OnEquip(p, equipped.ItemId)); break;
            case NpcTalked talked: Count(talked.Player, p => _rules.OnTalk(p, talked.NpcId)); break;
            case CellReached reached: RefreshAll(reached.Entity, retryHeld: false); break;
            case EntityEnteredMap entered: Arrived(entered.Entity); break;
            case InventoryChanged changed: RefreshAll(changed.Player, retryHeld: true); break;
            case PlayerProgressed progressed:
                if (progressed.Change is LevelUp or JobChanged) RefreshAll(progressed.Player, retryHeld: false);
                _dirty.Add(progressed.Player.Value);   // a level or a job may make a quest available
                break;
            case QuestItemsResolved resolved: Resolved(resolved); break;
        }
    }

    /// <summary>The player and its progression, when the player is on this instance.</summary>
    private bool TryPlayer(EntityId id, out Entity player, out ProgressionState state)
    {
        state = null!;
        if (!_instance!.TryGetEntity(id, out player!) || player.Kind != EntityKind.Player) return false;
        return _progressions.TryGet(id, out state);
    }

    private void Start(EntityId id, string questId)
    {
        if (!TryPlayer(id, out var player, out var state)) return;
        var reason = _rules.CheckStart(state, questId);
        if (reason is not null)
        {
            _context!.Publish(new QuestRefused(id, MessageTypes.DialogueChoice, questId, reason));
            return;
        }
        var progress = _rules.Start(state, questId, View(player));
        _context!.Publish(new QuestStarted(id, questId, _rules.Entry(progress)));
        _dirty.Add(id.Value);
        Evaluate(player, state, progress);
    }

    private void Abandon(EntityId id, string questId)
    {
        if (!TryPlayer(id, out _, out var state)) return;
        if (!_rules.Abandon(state, questId))
        {
            _context!.Publish(new QuestRefused(id, MessageTypes.AbandonQuest, questId, QuestReasons.NotActive));
            return;
        }
        _context!.Publish(new QuestAbandoned(id, questId));
        _dirty.Add(id.Value);
    }

    /// <summary>Applies a counting rule to every active quest of the player; a quest whose counters moved is reported and checked for its turn-in.</summary>
    private void Count(EntityId id, Func<QuestProgress, bool> count)
    {
        if (!TryPlayer(id, out var player, out var state) || state.ActiveQuests.Count == 0) return;
        foreach (var progress in Snapshot(state))
        {
            if (!count(progress)) continue;
            _dirty.Add(id.Value);
            Progressed(id, progress);
            Evaluate(player, state, progress);
        }
    }

    /// <summary>Re-reads the live objectives of every active quest of the player; with <paramref name="retryHeld"/> a held turn-in is tried again.</summary>
    private void RefreshAll(EntityId id, bool retryHeld)
    {
        if (!TryPlayer(id, out var player, out var state) || state.ActiveQuests.Count == 0) return;
        var view = View(player);
        foreach (var progress in Snapshot(state))
        {
            var changed = _rules.Refresh(progress, state, view);
            if (changed)
            {
                _dirty.Add(id.Value);
                Progressed(id, progress);
            }
            if (changed || (retryHeld && progress.Held is not null)) Evaluate(player, state, progress);
        }
    }

    /// <summary>A player arrived (join, warp, respawn elsewhere): a turn-in that was waiting for an inventory on the map it left is asked again, then everything is re-read.</summary>
    private void Arrived(EntityId id)
    {
        if (!TryPlayer(id, out var player, out var state)) return;
        foreach (var progress in state.ActiveQuests.Values) progress.Token = 0;
        PublishMarkers(id, announce: false);
        RefreshAll(id, retryHeld: true);
        foreach (var progress in Snapshot(state)) Evaluate(player, state, progress);
    }

    // ---- turning in ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// When the current step is met and no turn-in is pending, starts it: the consumed items (and for the last step the item and currency rewards) go to the inventory as one
    /// transaction; with nothing for the bag the step advances at once. Returns true when it started a turn-in or advanced.
    /// </summary>
    private bool Evaluate(Entity player, ProgressionState state, QuestProgress progress)
    {
        if (progress.Token != 0 || !_rules.StepMet(progress)) return false;
        var takes = _rules.Consumed(progress);
        var last = _rules.IsLastStep(progress);
        var rewards = last ? _rules.Rewards(progress.QuestId) : null;
        var gives = rewards?.Items ?? [];
        var currency = rewards?.Currency ?? 0;
        if (takes.Count == 0 && gives.Count == 0 && currency == 0)
        {
            Advance(player.Id, state, progress, rewards);
            return true;
        }
        progress.Token = _nextToken++;
        _context!.Publish(new QuestItemsRequested(player.Id, progress.Token, Quantities(takes), Quantities(gives), currency));
        return true;
    }

    private void Resolved(QuestItemsResolved e)
    {
        if (!TryPlayer(e.Player, out var player, out var state)) return;
        QuestProgress? progress = null;
        foreach (var p in state.ActiveQuests.Values)
            if (p.Token == e.Token) progress = p;
        if (progress is null) return;
        progress.Token = 0;
        if (e.Reason is not null)
        {
            // Full bag (or the consumed items went meanwhile): nothing changed, the quest waits and says why.
            if (!string.Equals(progress.Held, e.Reason, StringComparison.Ordinal))
            {
                progress.Held = e.Reason;
                Progressed(player.Id, progress);
            }
            return;
        }
        Advance(player.Id, state, progress, _rules.IsLastStep(progress) ? _rules.Rewards(progress.QuestId) : null);
    }

    /// <summary>The step's items are settled: the next step starts (and is checked at once), or the quest completes and its other rewards go to the progression host.</summary>
    private void Advance(EntityId id, ProgressionState state, QuestProgress progress, QuestRewardSplit? rewards)
    {
        if (!TryPlayer(id, out var player, out _)) return;
        var step = progress.Step;
        var completed = _rules.Advance(state, progress, View(player));
        _dirty.Add(id.Value);
        if (completed)
        {
            rewards ??= _rules.Rewards(progress.QuestId);
            if (rewards.BaseExp > 0 || rewards.JobExp > 0 || rewards.StatPoints > 0 || rewards.SkillPoints > 0 || rewards.UnlockJobs.Count > 0 || rewards.StatusRewards.Count > 0)
                _context!.Publish(new QuestRewardsRequested(id, progress.QuestId, rewards.BaseExp, rewards.JobExp, rewards.StatPoints, rewards.SkillPoints, rewards.UnlockJobs,
                    rewards.StatusRewards.Select(i => QuestRules.RewardKey(progress.QuestId, i)).ToList()));
            _context!.Publish(new QuestCompleted(id, progress.QuestId));
            return;
        }
        _context!.Publish(new QuestStepCompleted(id, progress.QuestId, step, _rules.Entry(progress)));
        Evaluate(player, state, progress);   // the next step may be met already (a level reached, a job held)
    }

    private void Progressed(EntityId id, QuestProgress progress) => _context!.Publish(new QuestProgressed(id, progress.QuestId, _rules.Entry(progress)));

    // ---- markers ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Recomputes the player's NPC markers; announces them when they changed (and <paramref name="announce"/>).</summary>
    private void PublishMarkers(EntityId id, bool announce)
    {
        if (!TryPlayer(id, out _, out var state)) return;
        _rules.Markers(state, _offer, _turnIn);
        if (_markers.TryGetValue(id.Value, out var last) && last.Offer.SequenceEqual(_offer, StringComparer.Ordinal) && last.TurnIn.SequenceEqual(_turnIn, StringComparer.Ordinal))
            return;
        var now = new Markers(_offer.ToArray(), _turnIn.ToArray());
        _markers[id.Value] = now;
        if (announce) _context!.Publish(new QuestMarkersChanged(id, now.Offer, now.TurnIn));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------------

    private static List<QuestProgress> Snapshot(ProgressionState state) => state.ActiveQuests.Values.OrderBy(p => p.QuestId, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<ItemQuantity> Quantities(List<KeyValuePair<string, int>> list) => list.Select(p => new ItemQuantity(p.Key, p.Value)).ToList();

    private QuestPlayer View(Entity player)
    {
        _player.Set(player, _inventories is not null && _inventories.TryGet(player.Id, out var bag) ? bag : null);
        return _player;
    }

    private sealed record Markers(string[] Offer, string[] TurnIn);

    /// <summary>The player as the objectives see it: its cell and map from the entity, its bag from the shared store.</summary>
    private sealed class QuestPlayer : IQuestPlayer
    {
        private Entity? _entity;
        private InventoryState? _bag;

        public void Set(Entity entity, InventoryState? bag)
        {
            _entity = entity;
            _bag = bag;
        }

        public string MapId => _entity!.MapId;

        public GridCell Cell => _entity!.Cell;

        public int ItemCount(string itemId)
        {
            if (_bag is null) return 0;
            var total = 0;
            foreach (var entry in _bag.Entries)
                if (entry.Slot is null && string.Equals(entry.ItemId, itemId, StringComparison.Ordinal)) total += entry.Quantity;
            return total;
        }

        public bool IsWearing(string itemId)
        {
            if (_bag is null) return false;
            foreach (var entry in _bag.Entries)
                if (entry.Slot is not null && string.Equals(entry.ItemId, itemId, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
