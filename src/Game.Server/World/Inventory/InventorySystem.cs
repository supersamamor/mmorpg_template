using Game.Server.Events;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Quests;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Items;
using Game.Shared.Protocol;
using Game.Shared.Stats;
using Game.Shared.Time;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Inventory;

/// <summary>
/// The server's inventory (ARCHITECTURE §6.8): picking up ground items, using consumables, equipping and unequipping, dropping items, socketing
/// cards, refining, and the weight limit. Owns <see cref="PickUp"/>, <see cref="UseItem"/>, <see cref="Equip"/>, <see cref="Unequip"/>,
/// <see cref="DropItem"/>, <see cref="InsertCard"/> and <see cref="RefineItem"/>. Runs after combat, before the AOI.
/// </summary>
/// <remarks>
/// <para><b>Per tick</b>, in this order: the intents (in arrival order); pending pick-ups (in player id order); weight limits re-read from changed stat
/// sheets; dropped items whose lifetime is over. Every change goes through <see cref="InventoryRules"/>, which checks everything first, so an
/// operation applies fully or not at all within the tick; a refusal is an <see cref="ItemRefused"/> event and changes nothing.</para>
/// <para><b>What it sets on the player entity</b> for other systems to read (hard rule 7, no calls): the equipment modifiers on the stat sheet, each
/// with its source <c>equip:&lt;entry id&gt;</c>, evaluated with the entry's refine level, and each socketed card's with <c>card:&lt;entry id&gt;:&lt;socket&gt;</c>
/// (ARCHITECTURE §6.1); <see cref="Entity.Weapon"/> (combat's weapon inputs and range);
/// <see cref="Entity.Look"/> (what others see, the AOI); <see cref="Entity.Load"/> (combat stops regeneration and attacks). Walks players to ground
/// items with <see cref="MoveTo"/> / <see cref="StopMove"/> and follows the movement events.</para>
/// <para><b>Time:</b> ground items use the instance's simulated time, like combat's drops; cooldowns use the injected server clock, so they hold
/// across maps. <b>Randomness:</b> a refine draws one value from the instance's seeded random source.</para>
/// </remarks>
public sealed class InventorySystem : ITickSystem
{
    private static readonly IReadOnlyCollection<Type> Owned =
        [typeof(PickUp), typeof(UseItem), typeof(Equip), typeof(Unequip), typeof(DropItem), typeof(InsertCard), typeof(RefineItem)];
    private static readonly int WeightLimitStat = StatIndex.IndexOf(DerivedStats.WeightLimit);
    private const int PickupGraceTicks = 3;   // a pick-up whose walk never started within this many ticks has no way there

    private readonly InventoryContent _content;
    private readonly InventoryRules _rules;
    private readonly IInventoryStore _store;
    private readonly IClock _clock;
    private readonly IPlayerProgressionStore? _progressions;
    private readonly Func<long> _newId;
    private readonly Dictionary<long, Holder> _holders = new();
    private readonly List<long> _jobChanged = new();   // players whose job changed since the last tick: worn gear their new job cannot use comes off (row 219)
    private readonly List<Pending> _pending = new();                    // sorted by player id
    private readonly Queue<(EntityId Id, GroundItem Item)> _dropped = new();
    private List<DialogueItemsRequested> _dialogueItems = new();   // a choice's item actions, applied next tick (T-601)
    private List<DialogueItemsRequested> _dialogueItemsRunning = new();
    private List<QuestItemsRequested> _questItems = new();          // a quest step's turn-in, applied next tick (T-602)
    private List<QuestItemsRequested> _questItemsRunning = new();
    private readonly InventoryDelta _delta = new();
    private readonly FormulaContext _equipContext = new();
    private MapInstance? _instance;
    private long _tick;
    private long _nowMs;

    /// <summary>Creates the inventory system for one instance; <paramref name="store"/> is shared by every instance, <paramref name="clock"/> times cooldowns.</summary>
    public InventorySystem(InventoryContent content, IInventoryStore store, IClock clock, IPlayerProgressionStore? progressions = null)
    {
        _progressions = progressions;
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _rules = content.Rules;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _newId = store.NextEntryId;
    }

    /// <inheritdoc />
    public string Name => "inventory";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Players whose inventory this instance tracks.</summary>
    public int HolderCount => _holders.Count;

    /// <summary>Pick-ups waiting for their player to walk into range.</summary>
    public int PendingPickups => _pending.Count;

    /// <summary>The source id of an equipped entry's stat modifiers.</summary>
    public static string SourceOf(long entryId) => "equip:" + entryId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The source id of the modifiers of the card in socket <paramref name="socket"/> (0-based) of an equipped entry: <c>card:&lt;entry id&gt;:&lt;socket&gt;</c>.</summary>
    public static string CardSourceOf(long entryId, int socket) =>
        "card:" + entryId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + socket.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        if (!ReferenceEquals(_instance, context.Instance)) Setup(context);
        _tick = context.TickNumber;
        _nowMs = context.SimTimeMs;

        var intents = context.Intents;
        for (var i = 0; i < intents.Count; i++)
        {
            if (!_instance!.TryGetEntity(intents[i].Entity, out var entity) || !_holders.TryGetValue(entity.Id.Value, out var holder)) continue;
            switch (intents[i])
            {
                case PickUp pick: StartPickup(context, holder, pick.Target); break;
                case UseItem use: Use(context, holder, use.EntryId); break;
                case Equip equip: PutOn(context, holder, equip.EntryId, equip.Slot); break;
                case Unequip off: TakeOff(context, holder, off.Slot); break;
                case DropItem drop: Drop(context, holder, drop.EntryId, drop.Quantity); break;
                case InsertCard card: Socket(context, holder, card.EntryId, card.CardEntryId); break;
                case RefineItem refine: TryRefine(context, holder, refine.EntryId); break;
            }
        }
        UnequipForNewJob(context);
        RunDialogueItems(context);
        RunQuestItems(context);
        RunPickups(context);
        foreach (var holder in _holders.Values) SyncLimit(context, holder, publish: true);
        ExpireDropped(context);
    }

    // ---- setup and tracking --------------------------------------------------------------------------------------------------------------

    private void Setup(TickContext context)
    {
        _instance = context.Instance;
        _equipContext[FormulaVariables.Refine] = 0;   // set to each entry's refine level before its modifiers are evaluated
        var events = _instance.Events;
        events.Subscribe<EntityEnteredMap>(e =>
        {
            if (_instance.TryGetEntity(e.Entity, out var entity)) Track(entity);
        });
        events.Subscribe<EntityLeftMap>(OnLeft);
        events.Subscribe<PlayerProgressed>(e =>
        {
            if (e.Change is Game.Shared.Progression.JobChanged && !_jobChanged.Contains(e.Player.Value)) _jobChanged.Add(e.Player.Value);
        });
        events.Subscribe<MovementStarted>(OnMovementStarted);
        events.Subscribe<MovementStopped>(OnMovementStopped);
        events.Subscribe<MoveRejected>(OnMoveRejected);
        events.Subscribe<DialogueItemsRequested>(e => _dialogueItems.Add(e));
        events.Subscribe<QuestItemsRequested>(e => _questItems.Add(e));
        foreach (var entity in _instance.Entities) Track(entity);
    }

    /// <summary>
    /// Starts tracking a player with stats: its inventory from the store (a new character gets <c>new_character.start_items</c>), its equipment
    /// modifiers on the sheet (once: they travel with the sheet across warps), its weapon, look and weight state.
    /// </summary>
    private void Track(Entity entity)
    {
        if (entity.Kind != EntityKind.Player || entity.Stats is not { } sheet || _holders.ContainsKey(entity.Id.Value)) return;
        var state = _store.GetOrCreate(entity.Id, () => new InventoryState(), out var created);
        var holder = new Holder(entity, state);
        _holders.Add(entity.Id.Value, holder);
        _rules.Refresh(state);
        SyncLimit(null, holder, publish: false);
        if (created) GiveStartItems(holder);
        foreach (var entry in state.Entries)
            if (entry.Slot is not null && !sheet.Modifiers.HasSource(SourceOf(entry.Id))) Apply(sheet, entry);
        Refresh(holder);
        SyncLimit(null, holder, publish: false);
    }

    private void GiveStartItems(Holder holder)
    {
        foreach (var start in _content.Balance.NewCharacter.StartItems)
        {
            _delta.Clear();
            var reason = _rules.TryAdd(holder.State, start.ItemId, start.Quantity, int.MaxValue, _newId, _delta);
            if (reason is not null)
            {
                _instance!.Log.Warn($"[{_instance.MapId}] start item {start.ItemId} x{start.Quantity} not given to {holder.Entity.Id}: {reason}.");
                continue;
            }
            if (!start.Equipped) continue;
            foreach (var entry in _delta.Upserted.ToArray())
            {
                if (!string.Equals(entry.ItemId, start.ItemId, StringComparison.Ordinal)) continue;
                _delta.Clear();
                var why = _rules.PlanEquip(holder.State, entry.Id, null, WearerOf(holder.Entity), out var plan);
                if (why is not null)
                {
                    _instance!.Log.Warn($"[{_instance.MapId}] start item {start.ItemId} not equipped on {holder.Entity.Id}: {why}.");
                    continue;
                }
                foreach (var off in plan!.Displaced) Remove(holder.Entity.Stats!, off);
                _rules.ApplyEquip(holder.State, plan, _delta);
            }
        }
        _delta.Clear();
    }

    /// <summary>
    /// ADR-002 row 219: after a job change, every worn item the new job cannot use (a weapon type it lacks, a <c>job_ids</c> list without it) goes back to the bag,
    /// like a player taking it off (the usual <see cref="ItemUnequipped"/> and inventory update). The level is not rechecked: a job change never lowers it.
    /// </summary>
    private void UnequipForNewJob(TickContext context)
    {
        if (_jobChanged.Count == 0) return;
        for (var i = 0; i < _jobChanged.Count; i++)
        {
            if (!_holders.TryGetValue(_jobChanged[i], out var holder)) continue;
            var wearer = WearerOf(holder.Entity);
            foreach (var entry in holder.State.Entries.Where(e => e.Slot is not null).ToArray())
                if (_content.TryGetItem(entry.ItemId, out var item) && _rules.JobCannotUse(item, wearer)) TakeOff(context, holder, entry.Slot!);
        }
        _jobChanged.Clear();
    }

    private void OnLeft(EntityLeftMap e)
    {
        if (_instance!.TryGetEntity(e.Entity, out _)) return;   // a same-map warp leaves and comes back at once
        if (!_holders.Remove(e.Entity.Value)) return;
        var index = FindPending(e.Entity.Value);
        if (index >= 0) _pending.RemoveAt(index);
        if (_instance.World.LocationOf(e.Entity) is null) _store.Release(e.Entity);
    }

    // ---- use -----------------------------------------------------------------------------------------------------------------------------

    private void Use(TickContext context, Holder holder, long entryId)
    {
        var state = holder.State;
        var reason = _rules.CanUse(state, entryId, _clock.NowMilliseconds, out var entry, out var item);
        if (reason is null && entry!.Slot is not null) reason = ItemReasons.ItemEquipped;
        var free = false;
        if (reason is null) reason = CheckReset(holder.Entity, item!, out free);
        if (reason is not null)
        {
            Refuse(context, holder, MessageTypes.UseItem, reason, entryId: entryId);
            return;
        }
        // The effects are combat's: it runs the consumable's effects through the effect runner on its next tick, on ItemUsed (T-501).
        _delta.Clear();
        _rules.ApplyUse(state, entry!, item!, _clock.NowMilliseconds, _delta, free);
        context.Publish(new ItemUsed(holder.Entity.Id, item!.Id, item.Consumable!.Consumed && !free));
        Changed(context, holder, InventoryChanges.Used, item.Id);
    }

    /// <summary>
    /// The reset rules (T-503, <c>progression.reset</c>) for an item whose consumable effects include <c>reset_stats</c> or <c>reset_skills</c>, checked
    /// before anything is used up: refused while dead or in combat when the balance says so, and when it would change nothing. <paramref name="free"/>
    /// is true below <c>free_below_base_level</c>: the reset happens and the item is kept. Null reason for any other item.
    /// </summary>
    private string? CheckReset(Entity player, ItemDef item, out bool free)
    {
        free = false;
        var stats = false;
        var skills = false;
        foreach (var effect in item.Consumable!.Effects)
        {
            if (effect is ResetStatsEffect) stats = true;
            else if (effect is ResetSkillsEffect) skills = true;
        }
        if (!stats && !skills) return null;
        var rules = _content.Balance.Progression.Reset;
        if (rules.RefuseWhenDead && player.Dead) return ItemReasons.ResetDead;
        if (rules.RefuseInCombat && _nowMs < player.InCombatUntilMs) return ItemReasons.ResetInCombat;
        if (_progressions is null || !_progressions.TryGet(player.Id, out var state)) return null;
        var start = _content.Balance.Progression.StartingStatValue;
        var anything = false;
        if (stats) foreach (var value in state.Stats.Values) anything |= value > start;
        if (skills) anything |= state.Skills.Count > 0;
        if (!anything) return ItemReasons.NothingToReset;
        free = state.BaseLevel < rules.FreeBelowBaseLevel;
        return null;
    }

    // ---- equip ---------------------------------------------------------------------------------------------------------------------------

    private void PutOn(TickContext context, Holder holder, long entryId, string? slot)
    {
        var reason = _rules.PlanEquip(holder.State, entryId, slot, WearerOf(holder.Entity), out var plan);
        if (reason is not null)
        {
            Refuse(context, holder, MessageTypes.Equip, reason, entryId: entryId, slot: slot);
            return;
        }
        var sheet = holder.Entity.Stats!;
        _delta.Clear();
        var was = plan!.Entry.Slot;
        _rules.ApplyEquip(holder.State, plan, _delta);
        foreach (var off in _delta.TakenOff)
        {
            Remove(sheet, off);
            context.Publish(new ItemUnequipped(holder.Entity.Id, off.ItemId, off.Id, plan.Displaced.First(d => d.Id == off.Id).Slot!));
        }
        if (was is null || !sheet.Modifiers.HasSource(SourceOf(plan.Entry.Id))) Apply(sheet, plan.Entry);
        context.Publish(new ItemEquipped(holder.Entity.Id, plan.Item.Id, plan.Entry.Id, plan.Slot));
        Refresh(holder);
        Changed(context, holder, InventoryChanges.Equipped, plan.Item.Id);
    }

    private void TakeOff(TickContext context, Holder holder, string slot)
    {
        _delta.Clear();
        var reason = _rules.TryUnequip(holder.State, slot, _delta);
        if (reason is not null)
        {
            Refuse(context, holder, MessageTypes.Unequip, reason, slot: slot);
            return;
        }
        var off = _delta.TakenOff[0];
        Remove(holder.Entity.Stats!, off);
        context.Publish(new ItemUnequipped(holder.Entity.Id, off.ItemId, off.Id, slot));
        Refresh(holder);
        Changed(context, holder, InventoryChanges.Unequipped, off.ItemId);
    }

    /// <summary>
    /// The worn entry's equipment modifiers onto the sheet with its source, and each socketed card's with its own (<see cref="CardSourceOf"/>).
    /// Values are evaluated once, with <c>refine</c> = the entry's refine level (an item's modifiers may use it, e.g. armour DEF).
    /// </summary>
    private void Apply(StatSheet sheet, InventoryEntry entry)
    {
        if (!_content.TryGetItem(entry.ItemId, out var item)) return;
        _equipContext[FormulaVariables.Refine] = entry.Refine;
        sheet.Modifiers.AddRange(SourceOf(entry.Id), _content.Modifiers(item), _equipContext);
        for (var i = 0; i < entry.Cards.Count; i++)
            if (_content.TryGetItem(entry.Cards[i], out var card)) sheet.Modifiers.AddRange(CardSourceOf(entry.Id, i), _content.Modifiers(card), _equipContext);
        _equipContext[FormulaVariables.Refine] = 0;
    }

    /// <summary>Takes exactly what <see cref="Apply"/> put on for <paramref name="entry"/> off the sheet: its equipment source and every card source.</summary>
    private static void Remove(StatSheet sheet, InventoryEntry entry)
    {
        sheet.Modifiers.RemoveBySource(SourceOf(entry.Id));
        for (var i = 0; i < entry.Cards.Count; i++) sheet.Modifiers.RemoveBySource(CardSourceOf(entry.Id, i));
    }

    // ---- cards and refining --------------------------------------------------------------------------------------------------------------

    private void Socket(TickContext context, Holder holder, long entryId, long cardEntryId)
    {
        var reason = _rules.PlanInsertCard(holder.State, cardEntryId, entryId, out var plan);
        if (reason is not null)
        {
            Refuse(context, holder, MessageTypes.InsertCard, reason, entryId: entryId);
            return;
        }
        _delta.Clear();
        _rules.ApplyInsertCard(holder.State, plan!, _delta);
        if (plan!.Target.Slot is not null && holder.Entity.Stats is { } sheet && _content.TryGetItem(plan.CardItem.Id, out var card))
        {
            // Worn: the new card counts at once, with its own source; the equipment's own modifiers do not change.
            _equipContext[FormulaVariables.Refine] = plan.Target.Refine;
            sheet.Modifiers.AddRange(CardSourceOf(plan.Target.Id, plan.Socket), _content.Modifiers(card), _equipContext);
            _equipContext[FormulaVariables.Refine] = 0;
        }
        context.Publish(new CardInserted(holder.Entity.Id, plan.Target.Id, plan.TargetItem.Id, plan.CardItem.Id, plan.Socket));
        Changed(context, holder, InventoryChanges.CardInserted, plan.TargetItem.Id);
    }

    private void TryRefine(TickContext context, Holder holder, long entryId)
    {
        var state = holder.State;
        if (_content.Balance.Dialogue.RefineNeedsService && !string.Equals(holder.Entity.OpenService, ServiceIds.Refine, StringComparison.Ordinal))
        {
            Refuse(context, holder, MessageTypes.Refine, ItemReasons.ServiceRequired, entryId: entryId);
            return;
        }
        var reason = _rules.PlanRefine(state, entryId, out var plan);
        if (reason is not null)
        {
            Refuse(context, holder, MessageTypes.Refine, reason, entryId: entryId);
            return;
        }
        var worn = plan!.Entry.Slot is not null;
        var sheet = holder.Entity.Stats!;
        _delta.Clear();
        var result = _rules.ApplyRefine(state, plan, context.Random, _delta);
        var now = state.Find(plan.Entry.Id);
        if (worn)
        {
            // Re-evaluated with the new level (or gone with the item when it broke).
            Remove(sheet, plan.Entry);
            if (now is not null) Apply(sheet, now);
            Refresh(holder);
        }
        Changed(context, holder, InventoryChanges.Refined, plan.Item.Id);
        context.Publish(new ItemRefined(holder.Entity.Id, plan.Entry.Id, plan.Item.Id, result, now?.Refine ?? plan.Entry.Refine));
    }

    private Wearer WearerOf(Entity player) =>
        new(player.Stats?.Base.BaseLevel ?? 1, player.DefinitionId ?? string.Empty, _content.WeaponTypesOf(player.DefinitionId));

    /// <summary>Re-reads the weapon and the look from what is worn; a look equal to the last keeps its object (the AOI compares references).</summary>
    private void Refresh(Holder holder)
    {
        var state = holder.State;
        var entity = holder.Entity;
        EquippedWeapon? weapon = null;
        var worn = new List<string>();
        foreach (var slot in EquipSlots.All.Ordered)
        {
            if (state.InSlot(slot) is not { } entry || !_content.TryGetItem(entry.ItemId, out var item) || item.Equip is not { } equip) continue;
            if (string.Equals(slot, EquipSlots.Weapon, StringComparison.Ordinal) && equip.WeaponType is { } type)
                weapon = new EquippedWeapon(item.Id, type, equip.WeaponAtk, entry.Refine, Math.Max(1, equip.AttackRange), equip.Element);
            if (equip.VisualAssetKey is not null) worn.Add(item.Id);
        }
        entity.Weapon = weapon;
        var look = new WornLook(weapon?.WeaponType ?? WeaponTypes.Unarmed, worn);
        if (!look.Equals(entity.Look)) entity.Look = look;
    }

    // ---- drop ----------------------------------------------------------------------------------------------------------------------------

    private void Drop(TickContext context, Holder holder, long entryId, int quantity)
    {
        var state = holder.State;
        var player = holder.Entity;
        var reason = _rules.CanRemove(state, entryId, quantity, out var entry);
        if (reason is not null)
        {
            Refuse(context, holder, MessageTypes.DropItem, reason, entryId: entryId);
            return;
        }
        var equipment = _content.TryGetItem(entry!.ItemId, out var item) && item.Kind == ItemKind.Equip;
        _delta.Clear();
        _rules.TryRemove(state, entryId, quantity, _delta);
        var ground = new GroundItem(entry.ItemId, quantity, player.Id, null, _nowMs, _nowMs, _nowMs + _content.Balance.Inventory.DroppedLifetimeMs,
            equipment ? entry.Id : 0, equipment && entry.Cards.Count > 0 ? entry.Cards : null, equipment ? entry.Refine : 0);
        var id = context.Spawn(EntityKind.Item, player.Cell, Directions.S, e =>
        {
            e.DefinitionId = ground.ItemId;
            e.GroundItem = ground;
        });
        _dropped.Enqueue((id, ground));
        context.Publish(new ItemDiscarded(player.Id, id, ground.ItemId, quantity, player.Cell, ground.ExpiresAtMs));
        Changed(context, holder, InventoryChanges.Dropped, entry.ItemId);
    }

    /// <summary>Despawns items players dropped whose lifetime is over (one lifetime for all, so the queue is in expiry order).</summary>
    private void ExpireDropped(TickContext context)
    {
        while (_dropped.Count > 0 && _dropped.Peek().Item.ExpiresAtMs <= _nowMs)
        {
            var (id, item) = _dropped.Dequeue();
            if (!_instance!.TryGetEntity(id, out var entity) || !ReferenceEquals(entity.GroundItem, item)) continue;   // picked up meanwhile
            context.Despawn(id);
            context.Publish(new ItemExpired(id, item.ItemId, item.Quantity, entity.Cell));
        }
    }

    // ---- pick up -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The item actions of dialogue choices raised last tick (T-601): each is one all-or-nothing exchange (<see cref="InventoryRules.TryExchange"/>), answered with
    /// <see cref="DialogueItemsResolved"/>. A player who left meanwhile gets nothing and no answer.
    /// </summary>
    private void RunDialogueItems(TickContext context)
    {
        if (_dialogueItems.Count == 0) return;
        (_dialogueItems, _dialogueItemsRunning) = (_dialogueItemsRunning, _dialogueItems);
        foreach (var request in _dialogueItemsRunning)
        {
            if (!_holders.TryGetValue(request.Player.Value, out var holder)) continue;
            var takes = request.Takes.Select(t => new KeyValuePair<string, int>(t.ItemId, t.Quantity)).ToList();
            var gives = request.Gives.Select(g => new KeyValuePair<string, int>(g.ItemId, g.Quantity)).ToList();
            var reason = _rules.TryExchange(holder.State, takes, gives, holder.State.WeightLimit, _newId, _delta);
            if (reason is null) Changed(context, holder, InventoryChanges.Exchanged, null);
            context.Publish(new DialogueItemsResolved(request.Player, request.Token, reason));
        }
        _dialogueItemsRunning.Clear();
    }

    /// <summary>
    /// Quest turn-ins raised last tick (T-602): each takes the step's consumed items, gives the item rewards and adds the currency as one all-or-nothing transaction
    /// (<see cref="InventoryRules.TryExchange"/>, then the currency, which cannot fail), answered with <see cref="QuestItemsResolved"/>. Refused (<c>missing_item</c>,
    /// <c>inventory_full</c>, <c>too_heavy</c>): nothing changed and the quest waits. A player who left meanwhile gets no answer (its quests ask again on arrival).
    /// </summary>
    private void RunQuestItems(TickContext context)
    {
        if (_questItems.Count == 0) return;
        (_questItems, _questItemsRunning) = (_questItemsRunning, _questItems);
        foreach (var request in _questItemsRunning)
        {
            if (!_holders.TryGetValue(request.Player.Value, out var holder)) continue;
            var takes = request.Takes.Select(t => new KeyValuePair<string, int>(t.ItemId, t.Quantity)).ToList();
            var gives = request.Gives.Select(g => new KeyValuePair<string, int>(g.ItemId, g.Quantity)).ToList();
            var reason = _rules.TryExchange(holder.State, takes, gives, holder.State.WeightLimit, _newId, _delta);
            if (reason is null)
            {
                if (request.Currency > 0) holder.State.Currency = long.MaxValue - holder.State.Currency < request.Currency ? long.MaxValue : holder.State.Currency + request.Currency;
                Changed(context, holder, InventoryChanges.Quest, null);
            }
            context.Publish(new QuestItemsResolved(request.Player, request.Token, reason));
        }
        _questItemsRunning.Clear();
    }

    private void StartPickup(TickContext context, Holder holder, EntityId targetId)
    {
        var player = holder.Entity;
        var existing = FindPending(player.Id.Value);
        if (existing >= 0) _pending.RemoveAt(existing);   // a new pick-up replaces the old one
        if (!_instance!.TryGetEntity(targetId, out var target) || target.Kind != EntityKind.Item || target.GroundItem is not { } item)
        {
            Refuse(context, holder, MessageTypes.PickUp, ItemReasons.ItemGone, target: targetId.Value);
            return;
        }
        if (!LootRules.MayPickUp(item, player.Id, _nowMs))
        {
            Refuse(context, holder, MessageTypes.PickUp, ItemReasons.NotYours, target: targetId.Value);
            return;
        }
        if (InRange(player, target))
        {
            Take(context, holder, target);
            return;
        }
        var pending = new Pending(holder, targetId, target.Cell, _tick);
        var index = ~FindPending(player.Id.Value);
        _pending.Insert(index, pending);
        context.Instance.TryEnqueue(new MoveTo(player.Id, target.Cell));
    }

    private void RunPickups(TickContext context)
    {
        for (var i = 0; i < _pending.Count;)
        {
            var p = _pending[i];
            var player = p.Holder.Entity;
            string? reason = null;
            var done = false;
            if (player.Dead) reason = ItemReasons.Cancelled;
            else if (p.EndReason is { } ended) reason = ended;
            else if (!_instance!.TryGetEntity(p.Target, out var target) || target.GroundItem is null) reason = ItemReasons.ItemGone;
            else if (InRange(player, target))
            {
                if (p.Walking) context.Instance.TryEnqueue(new StopMove(player.Id));
                _pending.RemoveAt(i);
                Take(context, p.Holder, target);
                continue;
            }
            else if (!p.Walking && _tick - p.IssuedTick > PickupGraceTicks) reason = ItemReasons.Unreachable;
            else done = true;

            if (done)
            {
                i++;
                continue;
            }
            _pending.RemoveAt(i);
            Refuse(context, p.Holder, MessageTypes.PickUp, reason!, target: p.Target.Value);
        }
    }

    private void Take(TickContext context, Holder holder, Entity target)
    {
        var player = holder.Entity;
        if (target.GroundItem is not { } item)
        {
            Refuse(context, holder, MessageTypes.PickUp, ItemReasons.ItemGone, target: target.Id.Value);
            return;
        }
        if (!LootRules.MayPickUp(item, player.Id, _nowMs))
        {
            Refuse(context, holder, MessageTypes.PickUp, ItemReasons.NotYours, target: target.Id.Value);
            return;
        }
        _delta.Clear();
        var reason = _rules.TryAdd(holder.State, item.ItemId, item.Quantity, holder.State.WeightLimit, _newId, _delta, item.InstanceId, item.Cards, item.Refine);
        if (reason is not null)
        {
            Refuse(context, holder, MessageTypes.PickUp, reason, target: target.Id.Value);
            return;
        }
        target.GroundItem = null;   // taken: nobody else can pick it up this tick, and combat's expiry skips it
        context.Despawn(target.Id);
        context.Publish(new ItemAcquired(player.Id, item.ItemId, item.Quantity, target.Id));
        Changed(context, holder, InventoryChanges.PickedUp, item.ItemId);
    }

    private bool InRange(Entity player, Entity target) => GridDistance.Chebyshev(player.Cell, target.Cell) <= _content.Balance.Inventory.PickupRange;

    private int FindPending(long playerId)
    {
        int lo = 0, hi = _pending.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var value = _pending[mid].Holder.Entity.Id.Value;
            if (value == playerId) return mid;
            if (value < playerId) lo = mid + 1;
            else hi = mid - 1;
        }
        return ~lo;
    }

    private Pending? PendingOf(EntityId player)
    {
        var index = FindPending(player.Value);
        return index >= 0 ? _pending[index] : null;
    }

    // A pick-up's own MoveTo comes back as a MovementStarted to its goal; any other walk, or a stop, is the player's new order and ends it.

    private void OnMovementStarted(MovementStarted e)
    {
        if (PendingOf(e.Entity) is not { } p) return;
        if (e.Goal.X == p.Goal.X && e.Goal.Y == p.Goal.Y) p.Walking = true;
        else p.EndReason ??= ItemReasons.Cancelled;
    }

    private void OnMovementStopped(MovementStopped e)
    {
        if (PendingOf(e.Entity) is not { } p) return;
        p.Walking = false;
        if (e.Reason != MovementReasons.Arrived) p.EndReason ??= ItemReasons.Cancelled;
    }

    private void OnMoveRejected(MoveRejected e)
    {
        if (PendingOf(e.Entity) is { } p && e.Target.X == p.Goal.X && e.Target.Y == p.Goal.Y) p.EndReason ??= ItemReasons.Unreachable;
    }

    // ---- weight and messages -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Re-reads <c>weight_limit</c> when the sheet changed and keeps the weight state on the entity. With <paramref name="publish"/>, a new limit or
    /// state is told to the owner (<c>weight</c>). Allocates nothing when nothing changed.
    /// </summary>
    private void SyncLimit(TickContext? context, Holder holder, bool publish)
    {
        var state = holder.State;
        if (holder.Entity.Stats is not { } sheet) return;
        if (sheet.Version != holder.SheetVersion)
        {
            holder.SheetVersion = sheet.Version;
            state.WeightLimit = Math.Max(0, sheet.GetInt(WeightLimitStat));
        }
        var load = _rules.LoadOf(state.Weight, state.WeightLimit);
        var changed = state.WeightLimit != holder.ToldLimit || !string.Equals(load, state.Load, StringComparison.Ordinal);
        state.Load = load;
        holder.Entity.Load = load;
        if (!changed) return;
        if (publish && context is not null)
        {
            _delta.Clear();
            Changed(context, holder, InventoryChanges.Weight, null);
        }
        else holder.ToldLimit = state.WeightLimit;
    }

    private void Changed(TickContext context, Holder holder, string cause, string? itemId)
    {
        var state = holder.State;
        if (holder.Entity.Stats is { } sheet && sheet.Version != holder.SheetVersion)
        {
            holder.SheetVersion = sheet.Version;
            state.WeightLimit = Math.Max(0, sheet.GetInt(WeightLimitStat));
        }
        state.Load = _rules.LoadOf(state.Weight, state.WeightLimit);
        holder.Entity.Load = state.Load;
        holder.ToldLimit = state.WeightLimit;
        state.Revision++;
        context.Publish(new InventoryChanged(holder.Entity.Id, state.Revision, cause, itemId, _delta.Upserted.ToArray(), _delta.Removed.ToArray(),
            state.Weight, state.WeightLimit, state.Load, state.Currency));
        _delta.Clear();
    }

    private void Refuse(TickContext context, Holder holder, string intent, string reason, long? target = null, long? entryId = null, string? slot = null) =>
        context.Publish(new ItemRefused(holder.Entity.Id, intent, reason, target, entryId, slot));

    private sealed class Holder(Entity entity, InventoryState state)
    {
        public readonly Entity Entity = entity;
        public readonly InventoryState State = state;
        public long SheetVersion = -1;
        public int ToldLimit = -1;   // the weight limit the owner was last told (or will be, in the full message)
    }

    private sealed class Pending(Holder holder, EntityId target, GridCell goal, long issuedTick)
    {
        public readonly Holder Holder = holder;
        public readonly EntityId Target = target;
        public readonly GridCell Goal = goal;
        public readonly long IssuedTick = issuedTick;
        public bool Walking;
        public string? EndReason;
    }
}
