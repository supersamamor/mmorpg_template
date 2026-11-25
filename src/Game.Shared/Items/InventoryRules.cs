using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Random;
using Game.Shared.Vocabulary;

namespace Game.Shared.Items
{
    /// <summary>Finds item definitions by id (the content registry on the server, a dictionary in tests).</summary>
    public interface IItemLookup
    {
        /// <summary>The item, or false when there is none with that id.</summary>
        bool TryGet(string itemId, out ItemDef item);
    }

    /// <summary>An <see cref="IItemLookup"/> over a dictionary.</summary>
    public sealed class ItemLookup : IItemLookup
    {
        private readonly IReadOnlyDictionary<string, ItemDef> _items;

        /// <summary>Looks items up in <paramref name="items"/> (keyed by id, ordinal).</summary>
        public ItemLookup(IReadOnlyDictionary<string, ItemDef> items) => _items = items ?? throw new ArgumentNullException(nameof(items));

        /// <inheritdoc />
        public bool TryGet(string itemId, out ItemDef item) => _items.TryGetValue(itemId, out item!);
    }

    /// <summary>Who wants to put something on: the facts the equip rules check, copied from the character.</summary>
    /// <param name="BaseLevel">Base level.</param>
    /// <param name="JobId">Job id (<c>job.*</c>).</param>
    /// <param name="JobWeaponTypes">Weapon types the job may use, or null to skip that check.</param>
    public sealed record Wearer(int BaseLevel, string JobId, IReadOnlyList<string>? JobWeaponTypes);

    /// <summary>An equip that passed every check: what goes where, and what comes off to make room.</summary>
    /// <param name="Entry">The entry to put on.</param>
    /// <param name="Item">Its item.</param>
    /// <param name="Slot">The slot it goes in.</param>
    /// <param name="Displaced">Entries that come off (the slot's occupant, and a shield or two-handed weapon it cannot be worn with).</param>
    public sealed record EquipPlan(InventoryEntry Entry, ItemDef Item, string Slot, IReadOnlyList<InventoryEntry> Displaced);

    /// <summary>A card socketing that passed every check.</summary>
    /// <param name="Card">The card entry (one unit is used up).</param>
    /// <param name="CardItem">The card item.</param>
    /// <param name="Target">The equipment entry it goes into.</param>
    /// <param name="TargetItem">The equipment item.</param>
    /// <param name="Socket">The socket it fills (0-based: the number of cards the equipment held).</param>
    public sealed record CardPlan(InventoryEntry Card, ItemDef CardItem, InventoryEntry Target, ItemDef TargetItem, int Socket);

    /// <summary>A refine attempt that passed every check: the entry, its item and the step it tries (<c>refine.yaml</c>).</summary>
    /// <param name="Entry">The equipment entry.</param>
    /// <param name="Item">Its item.</param>
    /// <param name="Step">The step to the next level.</param>
    public sealed record RefinePlan(InventoryEntry Entry, ItemDef Item, RefineLevelDef Step);

    /// <summary>
    /// The inventory rules as plain functions on an <see cref="InventoryState"/> (ARCHITECTURE §6.8), shared by the server and the tests. Every
    /// operation checks everything first and returns an <see cref="ItemReasons"/> term without touching the state when it refuses; only then does
    /// it change the state, in steps that cannot fail. So an operation applies fully or not at all, and the state is whole whenever a tick ends, which is when it
    /// is copied for a save (T-702). No I/O, no clock and no random source of its own: time, new ids and the refine roll's
    /// <see cref="IRandom"/> come in as arguments.
    /// </summary>
    public sealed class InventoryRules
    {
        private static readonly RefineDef NoRefine = new RefineDef();

        private readonly IItemLookup _items;
        private readonly InventoryBalanceDef _balance;
        private readonly RefineDef _refine;
        private readonly HashSet<string> _twoHanded;

        /// <summary>Rules over <paramref name="items"/> with the numbers of <paramref name="balance"/> and the refine table <paramref name="refine"/> (none: nothing refines).</summary>
        public InventoryRules(IItemLookup items, InventoryBalanceDef balance, RefineDef? refine = null)
        {
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _balance = balance ?? throw new ArgumentNullException(nameof(balance));
            _refine = refine ?? NoRefine;
            _twoHanded = new HashSet<string>(balance.TwoHandedWeaponTypes, StringComparer.Ordinal);
        }

        /// <summary>The balance numbers.</summary>
        public InventoryBalanceDef Balance => _balance;

        /// <summary>The refine table (<c>refine.yaml</c>).</summary>
        public RefineDef Refine => _refine;

        /// <summary>The item lookup.</summary>
        public IItemLookup Items => _items;

        /// <summary>Most units one entry of <paramref name="item"/> holds: 1 for equipment, else <c>max_stack</c> (at least 1).</summary>
        public static int StackLimit(ItemDef item) => item.Kind == ItemKind.Equip ? 1 : Math.Max(1, item.MaxStack);

        /// <summary>True when <paramref name="item"/> is a weapon held in both hands (its type is in <c>inventory.two_handed_weapon_types</c>).</summary>
        public bool IsTwoHanded(ItemDef item) => item.Equip?.WeaponType is { } type && _twoHanded.Contains(type);

        /// <summary>The sum of <c>weight × quantity</c> of every entry (an unknown item weighs nothing).</summary>
        public int WeightOf(InventoryState state)
        {
            long total = 0;
            var entries = state.Entries;
            for (var i = 0; i < entries.Count; i++)
                if (_items.TryGet(entries[i].ItemId, out var item)) total += (long)item.Weight * entries[i].Quantity;
            return (int)Math.Min(int.MaxValue, total);
        }

        /// <summary>Recomputes <see cref="InventoryState.Weight"/> (after loading a state, or when item weights may have changed).</summary>
        public void Refresh(InventoryState state) => state.Weight = WeightOf(state);

        /// <summary>
        /// The <see cref="WeightStates"/> term for <paramref name="weight"/> of <paramref name="limit"/>: <c>overweight</c> from
        /// <c>overweight_percent</c>, <c>burdened</c> from <c>burdened_percent</c>, else <c>normal</c>. A limit of 0 or less with any weight is overweight.
        /// </summary>
        public string LoadOf(int weight, int limit)
        {
            if (weight <= 0) return WeightStates.Normal;
            if (limit <= 0) return WeightStates.Overweight;
            var hundredths = (long)weight * 100;
            if (hundredths >= (long)limit * _balance.OverweightPercent) return WeightStates.Overweight;
            if (hundredths >= (long)limit * _balance.BurdenedPercent) return WeightStates.Burdened;
            return WeightStates.Normal;
        }

        // ---- add -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Puts <paramref name="quantity"/> × <paramref name="itemId"/> in: first into the item's carried stacks that have room (lowest id
        /// first), then into new entries. Refused, with nothing changed, when the weight would pass <paramref name="weightLimit"/>
        /// (<c>too_heavy</c>) or the new entries do not fit <c>inventory.max_entries</c> (<c>inventory_full</c>); also <c>item_gone</c> for an unknown
        /// item and <c>invalid_quantity</c> below 1. New entries take ids from <paramref name="newId"/>, except that a single equipment item
        /// keeps <paramref name="instanceId"/> when it is above 0 (an item dropped and picked up again). A single equipment item also gets
        /// <paramref name="cards"/> and <paramref name="refine"/> (what it had when it was dropped).
        /// </summary>
        public string? TryAdd(InventoryState state, string itemId, int quantity, int weightLimit, Func<long> newId, InventoryDelta delta, long instanceId = 0,
            IReadOnlyList<string>? cards = null, int refine = 0)
        {
            if (newId == null) throw new ArgumentNullException(nameof(newId));
            if (!_items.TryGet(itemId, out var item)) return ItemReasons.ItemGone;
            if (quantity < 1) return ItemReasons.InvalidQuantity;
            var added = (long)item.Weight * quantity;
            if (added > 0 && state.Weight + added > weightLimit) return ItemReasons.TooHeavy;

            var limit = StackLimit(item);
            var room = 0L;
            if (item.Kind != ItemKind.Equip)
            {
                var entries = state.Entries;
                for (var i = 0; i < entries.Count; i++)
                    if (entries[i].Slot == null && string.Equals(entries[i].ItemId, itemId, StringComparison.Ordinal)) room += limit - entries[i].Quantity;
            }
            var rest = Math.Max(0, quantity - room);
            var newEntries = (rest + limit - 1) / limit;
            if (state.Entries.Count + newEntries > _balance.MaxEntries) return ItemReasons.InventoryFull;

            // Checked: from here nothing can fail.
            var left = quantity;
            if (item.Kind != ItemKind.Equip)
            {
                for (var i = 0; i < state.Entries.Count && left > 0; i++)
                {
                    var entry = state.Entries[i];
                    if (entry.Slot != null || !string.Equals(entry.ItemId, itemId, StringComparison.Ordinal) || entry.Quantity >= limit) continue;
                    var put = Math.Min(left, limit - entry.Quantity);
                    var grown = entry with { Quantity = entry.Quantity + put };
                    state.Put(grown);
                    delta.Upsert(grown);
                    left -= put;
                }
            }
            var first = true;
            while (left > 0)
            {
                var put = Math.Min(left, limit);
                var id = first && instanceId > 0 && quantity == 1 && state.Find(instanceId) == null ? instanceId : newId();
                var entry = new InventoryEntry(id, itemId, put);
                if (first && quantity == 1 && item.Kind == ItemKind.Equip && (refine > 0 || cards is { Count: > 0 }))
                    entry = entry with { Cards = cards is { Count: > 0 } ? Copy(cards) : entry.Cards, Refine = Math.Max(0, refine) };
                first = false;
                state.Put(entry);
                delta.Upsert(entry);
                left -= put;
            }
            state.Weight = WeightOf(state);
            return null;
        }

        // ---- remove ----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Takes <paramref name="quantity"/> units out of entry <paramref name="entryId"/> (the whole entry when that is all of it). Refused:
        /// <c>item_gone</c> (no such entry), <c>item_equipped</c> (worn), <c>invalid_quantity</c> (below 1 or more than it holds).
        /// </summary>
        public string? TryRemove(InventoryState state, long entryId, int quantity, InventoryDelta delta)
        {
            var reason = CanRemove(state, entryId, quantity, out var entry);
            if (reason != null) return reason;
            Take(state, entry!, quantity, delta);
            return null;
        }

        /// <summary>The checks of <see cref="TryRemove"/> without changing anything.</summary>
        public string? CanRemove(InventoryState state, long entryId, int quantity, out InventoryEntry? entry)
        {
            entry = state.Find(entryId);
            if (entry == null) return ItemReasons.ItemGone;
            if (entry.Slot != null) return ItemReasons.ItemEquipped;
            if (quantity < 1 || quantity > entry.Quantity) return ItemReasons.InvalidQuantity;
            return null;
        }

        private void Take(InventoryState state, InventoryEntry entry, int quantity, InventoryDelta delta)
        {
            if (quantity >= entry.Quantity)
            {
                state.Remove(entry.Id);
                delta.Gone(entry.Id);
            }
            else
            {
                var shrunk = entry with { Quantity = entry.Quantity - quantity };
                state.Put(shrunk);
                delta.Upsert(shrunk);
            }
            state.Weight = WeightOf(state);
        }

        // ---- exchange (a dialogue's item actions) ----------------------------------------------------------------------------------

        /// <summary>
        /// Applies a dialogue choice's item actions as one transaction (T-601, hard rule 10): first every take (from carried stacks, lowest id first; worn
        /// pieces do not count), then every give. All or nothing: a refusal puts everything back and leaves <paramref name="delta"/> empty. Refusal terms are
        /// <c>DialogueReasons</c>: <c>missing_item</c> (a take finds less than it asks for), <c>inventory_full</c>, <c>too_heavy</c>; an unknown or
        /// non-positive give is <c>invalid_choice</c> (validated content has none).
        /// </summary>
        public string? TryExchange(InventoryState state, IReadOnlyList<KeyValuePair<string, int>> takes, IReadOnlyList<KeyValuePair<string, int>> gives,
            int weightLimit, Func<long> newId, InventoryDelta delta)
        {
            var before = state.Snapshot();
            delta.Clear();
            string? reason = null;
            for (var t = 0; t < takes.Count && reason == null; t++)
            {
                var carried = 0L;
                foreach (var entry in state.Entries)
                    if (entry.Slot == null && string.Equals(entry.ItemId, takes[t].Key, StringComparison.Ordinal)) carried += entry.Quantity;
                if (takes[t].Value < 1 || carried < takes[t].Value)
                {
                    reason = DialogueReasons.MissingItem;
                    break;
                }
                var left = takes[t].Value;
                var snapshot = state.Snapshot();
                foreach (var entry in snapshot)
                {
                    if (left == 0) break;
                    if (entry.Slot != null || !string.Equals(entry.ItemId, takes[t].Key, StringComparison.Ordinal)) continue;
                    var part = Math.Min(left, entry.Quantity);
                    Take(state, entry, part, delta);
                    left -= part;
                }
            }
            for (var g = 0; g < gives.Count && reason == null; g++)
            {
                var added = TryAdd(state, gives[g].Key, gives[g].Value, weightLimit, newId, delta);
                if (added == null) continue;
                reason = added == ItemReasons.TooHeavy ? DialogueReasons.TooHeavy
                    : added == ItemReasons.InventoryFull ? DialogueReasons.InventoryFull
                    : DialogueReasons.InvalidChoice;
            }
            if (reason != null)
            {
                state.Restore(before);
                state.Weight = WeightOf(state);
                delta.Clear();
            }
            return reason;
        }

        // ---- use -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The inventory's checks for using entry <paramref name="entryId"/> at <paramref name="nowMs"/>: it exists (<c>item_gone</c>), is a
        /// consumable (<c>not_usable</c>) and is off cooldown (<c>cooldown</c>). Whether its effects can run is the effect runner's check.
        /// </summary>
        public string? CanUse(InventoryState state, long entryId, long nowMs, out InventoryEntry? entry, out ItemDef? item)
        {
            item = null;
            entry = state.Find(entryId);
            if (entry == null || !_items.TryGet(entry.ItemId, out var def)) return ItemReasons.ItemGone;
            item = def;
            if (def.Kind != ItemKind.Consumable || def.Consumable == null) return ItemReasons.NotUsable;
            if (state.CooldownUntilMs.TryGetValue(def.Id, out var until) && nowMs < until) return ItemReasons.Cooldown;
            return null;
        }

        /// <summary>
        /// Records a use that passed <see cref="CanUse"/>: the item id goes on cooldown for <c>cooldown_ms</c> from <paramref name="nowMs"/>, and
        /// one unit is used up when the item is <c>consumed</c> (not when <paramref name="free"/>: a free reset, T-503).
        /// </summary>
        public void ApplyUse(InventoryState state, InventoryEntry entry, ItemDef item, long nowMs, InventoryDelta delta, bool free = false)
        {
            var consumable = item.Consumable!;
            if (consumable.CooldownMs > 0) state.CooldownUntilMs[item.Id] = nowMs + consumable.CooldownMs;
            if (consumable.Consumed && !free) Take(state, entry, 1, delta);
        }

        // ---- equip -----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Checks putting entry <paramref name="entryId"/> on, into <paramref name="slot"/> or, when null, the first of the item's slots that is
        /// free (else its first slot). Refused: <c>item_gone</c>, <c>not_equipment</c> (not an <c>equip</c> item), <c>wrong_slot</c>,
        /// <c>level_too_low</c>, <c>wrong_job</c> (<c>job_ids</c>, or a weapon type the job cannot use). The plan lists what comes off: the slot's
        /// occupant, a shield when a two-handed weapon goes on, a two-handed weapon when a shield goes on.
        /// </summary>
        public string? PlanEquip(InventoryState state, long entryId, string? slot, Wearer wearer, out EquipPlan? plan)
        {
            plan = null;
            var entry = state.Find(entryId);
            if (entry == null || !_items.TryGet(entry.ItemId, out var item)) return ItemReasons.ItemGone;
            if (item.Kind != ItemKind.Equip || item.Equip is not { } equip || equip.Slots.Count == 0) return ItemReasons.NotEquipment;

            string target;
            if (slot != null)
            {
                if (!Contains(equip.Slots, slot)) return ItemReasons.WrongSlot;
                target = slot;
            }
            else
            {
                target = equip.Slots[0];
                for (var i = 0; i < equip.Slots.Count; i++)
                {
                    var occupant = state.InSlot(equip.Slots[i]);
                    if (occupant == null || occupant.Id == entry.Id)
                    {
                        target = equip.Slots[i];
                        break;
                    }
                }
            }
            if (wearer.BaseLevel < equip.MinBaseLevel) return ItemReasons.LevelTooLow;
            if (equip.JobIds.Count > 0 && !Contains(equip.JobIds, wearer.JobId)) return ItemReasons.WrongJob;
            if (equip.WeaponType != null && wearer.JobWeaponTypes != null && !Contains(wearer.JobWeaponTypes, equip.WeaponType)) return ItemReasons.WrongJob;

            var displaced = new List<InventoryEntry>();
            AddDisplaced(displaced, state.InSlot(target), entry);
            if (string.Equals(target, EquipSlots.Weapon, StringComparison.Ordinal) && IsTwoHanded(item))
                AddDisplaced(displaced, state.InSlot(EquipSlots.Shield), entry);
            if (string.Equals(target, EquipSlots.Shield, StringComparison.Ordinal) && state.InSlot(EquipSlots.Weapon) is { } weapon
                && _items.TryGet(weapon.ItemId, out var weaponItem) && IsTwoHanded(weaponItem))
                AddDisplaced(displaced, weapon, entry);
            plan = new EquipPlan(entry, item, target, displaced);
            return null;
        }

        /// <summary>Applies a plan from <see cref="PlanEquip"/> on the same state: the displaced entries come off, then the entry goes on.</summary>
        public void ApplyEquip(InventoryState state, EquipPlan plan, InventoryDelta delta)
        {
            for (var i = 0; i < plan.Displaced.Count; i++)
            {
                var off = plan.Displaced[i] with { Slot = null };
                state.Put(off);
                delta.Upsert(off);
                delta.TakenOff.Add(off);
            }
            var on = plan.Entry with { Slot = plan.Slot };
            state.Put(on);
            delta.Upsert(on);
        }

        /// <summary>
        /// True when <paramref name="item"/> is equipment the wearer's <i>job</i> may not use: its <c>job_ids</c> list lacks the job, or its weapon type is not one
        /// of the job's. The level is not checked (a job change never lowers it). Used to take such items off after a job change (ADR-002 row 219).
        /// </summary>
        public bool JobCannotUse(ItemDef item, Wearer wearer)
        {
            if (item.Equip is not { } equip) return false;
            if (equip.JobIds.Count > 0 && !Contains(equip.JobIds, wearer.JobId)) return true;
            return equip.WeaponType != null && wearer.JobWeaponTypes != null && !Contains(wearer.JobWeaponTypes, equip.WeaponType);
        }

        /// <summary>Takes off what is worn in <paramref name="slot"/>. Refused with <c>not_equipped</c> when the slot is empty or unknown.</summary>
        public string? TryUnequip(InventoryState state, string slot, InventoryDelta delta)
        {
            if (slot == null || !EquipSlots.All.Contains(slot)) return ItemReasons.NotEquipped;
            var entry = state.InSlot(slot);
            if (entry == null) return ItemReasons.NotEquipped;
            var off = entry with { Slot = null };
            state.Put(off);
            delta.Upsert(off);
            delta.TakenOff.Add(off);
            return null;
        }

        // ---- cards ------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Checks socketing the card in entry <paramref name="cardEntryId"/> into equipment entry <paramref name="equipmentEntryId"/> (carried or worn).
        /// Refused: <c>item_gone</c> (either entry missing), <c>not_a_card</c>, <c>not_equipment</c>, <c>card_does_not_fit</c> (none of the card's
        /// <c>slots</c> is one of the equipment's), <c>no_free_socket</c> (its <c>card_slots</c> are all taken, or it has none).
        /// </summary>
        public string? PlanInsertCard(InventoryState state, long cardEntryId, long equipmentEntryId, out CardPlan? plan)
        {
            plan = null;
            var card = state.Find(cardEntryId);
            if (card == null || !_items.TryGet(card.ItemId, out var cardItem)) return ItemReasons.ItemGone;
            if (cardItem.Kind != ItemKind.Card || cardItem.Card is not { } cardDef) return ItemReasons.NotACard;
            var target = state.Find(equipmentEntryId);
            if (target == null || !_items.TryGet(target.ItemId, out var item)) return ItemReasons.ItemGone;
            if (item.Kind != ItemKind.Equip || item.Equip is not { } equip) return ItemReasons.NotEquipment;
            if (!Fits(cardDef, equip)) return ItemReasons.CardDoesNotFit;
            if (target.Cards.Count >= equip.CardSlots) return ItemReasons.NoFreeSocket;
            plan = new CardPlan(card, cardItem, target, item, target.Cards.Count);
            return null;
        }

        /// <summary>True when <paramref name="card"/> may go into equipment <paramref name="equip"/>: one of the card's slots is one of the item's.</summary>
        public static bool Fits(ItemCardDef card, ItemEquipDef equip)
        {
            for (var i = 0; i < card.Slots.Count; i++)
                if (Contains(equip.Slots, card.Slots[i])) return true;
            return false;
        }

        /// <summary>
        /// Applies a plan from <see cref="PlanInsertCard"/> on the same state: one card unit is used up and the card id is added to the equipment's
        /// cards, for good. The weight goes down by the card's (the equipment's own weight does not change).
        /// </summary>
        public void ApplyInsertCard(InventoryState state, CardPlan plan, InventoryDelta delta)
        {
            Take(state, plan.Card, 1, delta);
            var cards = new string[plan.Target.Cards.Count + 1];
            for (var i = 0; i < plan.Target.Cards.Count; i++) cards[i] = plan.Target.Cards[i];
            cards[cards.Length - 1] = plan.CardItem.Id;
            var socketed = plan.Target with { Cards = cards };
            state.Put(socketed);
            delta.Upsert(socketed);
        }

        // ---- refine -----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Checks refining equipment entry <paramref name="entryId"/> (carried or worn) by one level. Refused: <c>item_gone</c>, <c>not_equipment</c>,
        /// <c>not_refinable</c> (not <c>refinable</c>), <c>at_max_refine</c> (at <c>max_level</c>, or no step for the next level),
        /// <c>missing_material</c> (fewer carried units of the step's material than its quantity; the entry itself never counts).
        /// </summary>
        public string? PlanRefine(InventoryState state, long entryId, out RefinePlan? plan)
        {
            plan = null;
            var entry = state.Find(entryId);
            if (entry == null || !_items.TryGet(entry.ItemId, out var item)) return ItemReasons.ItemGone;
            if (item.Kind != ItemKind.Equip || item.Equip is not { } equip) return ItemReasons.NotEquipment;
            if (!equip.Refinable) return ItemReasons.NotRefinable;
            if (entry.Refine >= _refine.MaxLevel || _refine.StepTo(entry.Refine + 1) is not { } step) return ItemReasons.AtMaxRefine;
            if (CountCarried(state, step.MaterialItemId, entry.Id) < step.MaterialQuantity) return ItemReasons.MissingMaterial;
            plan = new RefinePlan(entry, item, step);
            return null;
        }

        /// <summary>
        /// Applies a plan from <see cref="PlanRefine"/> on the same state and returns the <see cref="RefineResults"/> term. The material is used up first
        /// (carried stacks, lowest id first), then one roll of <paramref name="random"/> decides: below <c>success_percent</c> the level goes up by one,
        /// otherwise the step's <c>failure</c> applies (<c>keep</c>, <c>down</c> one level but not below 0, or <c>break</c>: the entry is gone, cards and all).
        /// Exactly one value is drawn from <paramref name="random"/>, so a seeded source gives the same outcome every time.
        /// </summary>
        public string ApplyRefine(InventoryState state, RefinePlan plan, IRandom random, InventoryDelta delta)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            var left = plan.Step.MaterialQuantity;
            for (var i = 0; i < state.Entries.Count && left > 0;)
            {
                var entry = state.Entries[i];
                if (!IsMaterial(entry, plan.Step.MaterialItemId, plan.Entry.Id))
                {
                    i++;
                    continue;
                }
                var take = Math.Min(left, entry.Quantity);
                left -= take;
                Take(state, entry, take, delta);
                if (take < entry.Quantity) i++;   // a whole entry taken shifts the next one into place i
            }

            var success = random.Next(100) < plan.Step.SuccessPercent;
            var result = success ? RefineResults.Success : plan.Step.Failure;
            var current = state.Find(plan.Entry.Id) ?? plan.Entry;
            switch (result)
            {
                case RefineResults.Success:
                    Replace(state, current with { Refine = current.Refine + 1 }, delta);
                    break;
                case RefineResults.Down:
                    if (current.Refine > 0) Replace(state, current with { Refine = current.Refine - 1 }, delta);
                    break;
                case RefineResults.Break:
                    state.Remove(current.Id);
                    delta.Gone(current.Id);
                    state.Weight = WeightOf(state);
                    break;
            }
            return result;
        }

        private static void Replace(InventoryState state, InventoryEntry entry, InventoryDelta delta)
        {
            state.Put(entry);
            delta.Upsert(entry);
        }

        private static bool IsMaterial(InventoryEntry entry, string materialId, long refined) =>
            entry.Id != refined && entry.Slot == null && string.Equals(entry.ItemId, materialId, StringComparison.Ordinal);

        private static long CountCarried(InventoryState state, string materialId, long refined)
        {
            long count = 0;
            var entries = state.Entries;
            for (var i = 0; i < entries.Count; i++)
                if (IsMaterial(entries[i], materialId, refined)) count += entries[i].Quantity;
            return count;
        }

        private static string[] Copy(IReadOnlyList<string> list)
        {
            var copy = new string[list.Count];
            for (var i = 0; i < copy.Length; i++) copy[i] = list[i];
            return copy;
        }

        private static void AddDisplaced(List<InventoryEntry> list, InventoryEntry? occupant, InventoryEntry incoming)
        {
            if (occupant == null || occupant.Id == incoming.Id) return;
            for (var i = 0; i < list.Count; i++)
                if (list[i].Id == occupant.Id) return;
            list.Add(occupant);
        }

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (var i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
