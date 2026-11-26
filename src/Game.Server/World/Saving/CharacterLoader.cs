using Game.Content.Registry;
using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Inventory;
using Game.Shared.Definitions;
using Game.Shared.Items;
using Game.Shared.Progression;
using Game.Shared.Quests;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Saving;

/// <summary>A character ready to enter the world: its saved state, checked against the current content (<see cref="CharacterLoader"/>).</summary>
public sealed class LoadedCharacter
{
    /// <summary>Levels, stats, skills, quests.</summary>
    public required ProgressionState Progression { get; init; }

    /// <summary>Items and currency.</summary>
    public required InventoryState Inventory { get; init; }

    /// <summary>Where it enters.</summary>
    public required string MapId { get; init; }

    /// <summary>The cell it enters on: walkable.</summary>
    public required GridCell Cell { get; init; }

    /// <summary>Its facing.</summary>
    public required string Facing { get; init; }

    /// <summary>Its HP, unless <see cref="HpPercent"/> is above 0.</summary>
    public int Hp { get; init; }

    /// <summary>For a character that had died: the share of its maximum HP it comes back with.</summary>
    public int HpPercent { get; init; }

    /// <summary>Its SP.</summary>
    public int Sp { get; init; }

    /// <summary>Its save point.</summary>
    public SavePoint? SavePoint { get; init; }

    /// <summary>Skill → milliseconds left.</summary>
    public required IReadOnlyDictionary<string, long> SkillCooldowns { get; init; }

    /// <summary>Item → milliseconds left.</summary>
    public required IReadOnlyDictionary<string, long> ItemCooldowns { get; init; }

    /// <summary>Autopilot time used in its day.</summary>
    public long AutopilotUsedMs { get; init; }

    /// <summary>How long ago that day began, or null before any run.</summary>
    public long? AutopilotDayAgeMs { get; init; }

    /// <summary>Items the content has no place for.</summary>
    public required IReadOnlyList<SetAsideItem> AsideItems { get; init; }

    /// <summary>Anything else it has no place for.</summary>
    public required IReadOnlyList<SetAside> Aside { get; init; }

    /// <summary>What the content made different from what was saved, for the log; empty when nothing.</summary>
    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Checks a saved character against the current content (T-702, ARCHITECTURE §3 "Loading"). Nothing is ever dropped silently, and nothing here throws for a state the store
/// returned: each thing the content no longer has a place for is kept aside, given back or moved by one rule, and noted. Thread-safe.
/// </summary>
public sealed class CharacterLoader
{
    private readonly ContentDatabase _content;
    private readonly InventoryContent _items;
    private readonly QuestRules _quests;
    private readonly IReadOnlyDictionary<string, (string MapId, GridCell Cell)> _respawns;
    private readonly Func<long> _newItemId;

    /// <summary>A loader over <paramref name="content"/>; <paramref name="newItemId"/> numbers the stacks a smaller <c>max_stack</c> splits off.</summary>
    public CharacterLoader(ContentDatabase content, Func<long> newItemId)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _newItemId = newItemId ?? throw new ArgumentNullException(nameof(newItemId));
        _items = InventoryContent.From(content);
        _quests = new QuestRules(content.Quests.All, content.Balance.Quests.MaxActive);
        _respawns = CombatContent.RespawnPoints(content.Maps.All, content.Grids);
    }

    /// <summary>The character as it enters now. <paramref name="now"/> (UTC) dates the times left.</summary>
    public LoadedCharacter Load(CharacterState saved, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var notes = new List<string>();
        var aside = new List<SetAside>();
        var progression = Progression(saved, aside, notes);
        var asideItems = new List<SetAsideItem>();
        var inventory = Inventory(saved, progression, asideItems, notes);
        Quests(saved, progression, aside, notes);
        Wardrobe(saved, progression, notes);

        SavePoint? savePoint = saved.SavePoint;
        if (savePoint is { } point && !Walkable(point.MapId, point.Cell))
        {
            notes.Add($"save point {point.MapId} {point.Cell} no longer exists: cleared");
            savePoint = null;
        }
        var (mapId, cell) = (saved.MapId, saved.Cell);
        var dead = saved.Hp <= 0;
        if (dead) (mapId, cell) = Respawn(savePoint, mapId);   // the death is paid for; it comes back as if it had asked to
        else if (!Walkable(mapId, cell))
        {
            (mapId, cell) = _content.Grids.ContainsKey(mapId) ? RespawnOf(mapId) : Respawn(savePoint, mapId);
            notes.Add($"{saved.MapId} {saved.Cell} cannot be stood on any more: moved to {mapId} {cell}");
        }

        var away = Math.Max(0, (long)(now - saved.TakenAt).TotalMilliseconds);   // a clock set back does not stretch anything
        return new LoadedCharacter
        {
            Progression = progression,
            Inventory = inventory,
            MapId = mapId,
            Cell = cell,
            Facing = Directions.All.Contains(saved.Facing) ? saved.Facing : Directions.S,
            Hp = saved.Hp,
            HpPercent = dead ? _content.Balance.Death.RespawnHpPercent : 0,
            Sp = saved.Sp,
            SavePoint = savePoint,
            SkillCooldowns = Left(saved.SkillCooldowns, away, id => _content.Skills.Contains(id)),
            ItemCooldowns = Left(saved.ItemCooldowns, away, id => _content.Items.Contains(id)),
            AutopilotUsedMs = saved.AutopilotUsedMs,
            AutopilotDayAgeMs = saved.AutopilotDayStartedAt is { } day ? Math.Max(0, (long)(now - day).TotalMilliseconds) : null,
            AsideItems = asideItems,
            Aside = aside,
            Notes = notes,
        };
    }

    // ---- progression --------------------------------------------------------------------------------------------------------------------

    private ProgressionState Progression(CharacterState saved, List<SetAside> aside, List<string> notes)
    {
        var state = new ProgressionState
        {
            BaseLevel = saved.BaseLevel, BaseExp = saved.BaseExp, JobId = saved.JobId, JobLevel = saved.JobLevel, JobExp = saved.JobExp,
            StatPoints = saved.StatPoints, SkillPoints = saved.SkillPoints, TurnInSequence = saved.TurnInSequence,
        };
        foreach (var kept in saved.Aside)
            if (kept.Kind is SetAsideKinds.Job or SetAsideKinds.Skill) aside.Add(kept);   // records for support
        if (!_content.Jobs.Contains(saved.JobId))
        {
            state.JobId = _content.Balance.NewCharacter.StartJobId;
            state.JobLevel = 1;
            state.JobExp = 0;
            aside.Add(new SetAside(SetAsideKinds.Job, saved.JobId, [saved.JobLevel, saved.JobExp], "unknown job"));
            notes.Add($"job {saved.JobId} (level {saved.JobLevel}) no longer exists: goes on as {state.JobId} level 1, with every skill and point it has");
        }
        foreach (var stat in PrimaryStats.All.Ordered)
            state.Stats[stat] = saved.Stats.TryGetValue(stat, out var value) ? value : _content.Balance.Progression.StartingStatValue;
        foreach (var (id, level) in saved.Skills)
        {
            if (level < 1) continue;
            if (!_content.Skills.TryGet(id, out var skill))
            {
                state.SkillPoints += level;   // one point a level, as a reset gives back
                aside.Add(new SetAside(SetAsideKinds.Skill, id, [level], "unknown skill; points given back"));
                notes.Add($"skill {id} (level {level}) no longer exists: {level} skill point(s) given back");
                continue;
            }
            var max = Math.Max(1, skill.MaxLevel);
            state.Skills[id] = Math.Min(level, max);
            if (level <= max) continue;
            state.SkillPoints += level - max;
            notes.Add($"skill {id} ends at level {max} now, was {level}: {level - max} skill point(s) given back");
        }
        foreach (var job in saved.UnlockedJobs.Concat(Kept(saved, SetAsideKinds.UnlockedJob)))
        {
            if (_content.Jobs.Contains(job)) state.UnlockedJobs.Add(job);
            else
            {
                aside.Add(new SetAside(SetAsideKinds.UnlockedJob, job, [], "unknown job"));
                if (saved.UnlockedJobs.Contains(job)) notes.Add($"unlocked job {job} no longer exists: set aside");
            }
        }
        foreach (var owed in saved.OwedRewards) state.OwedRewards.Add(owed);   // numbers, and status keys that do nothing when they are gone
        return state;
    }

    private static IEnumerable<string> Kept(CharacterState saved, string kind) => saved.Aside.Where(a => a.Kind == kind).Select(a => a.Id);

    private void Quests(CharacterState saved, ProgressionState state, List<SetAside> aside, List<string> notes)
    {
        foreach (var quest in saved.CompletedQuests.Concat(Kept(saved, SetAsideKinds.CompletedQuest)))
        {
            if (_quests.TryGet(quest, out _)) state.CompletedQuests.Add(quest);
            else aside.Add(new SetAside(SetAsideKinds.CompletedQuest, quest, [], "unknown quest"));
        }
        foreach (var quest in saved.ActiveQuests) Active(quest, true);
        foreach (var kept in saved.Aside.Where(a => a.Kind == SetAsideKinds.ActiveQuest && a.Data.Count >= 2))
            Active(new SavedQuest(kept.Id, (int)kept.Data[0], kept.Data.Skip(2).Select(c => (int)c).ToList(), kept.Data[1]), false);
        return;

        void Active(SavedQuest quest, bool wasActive)
        {
            if (!_quests.TryGet(quest.QuestId, out var def) || def.Steps.Count == 0)
            {
                aside.Add(new SetAside(SetAsideKinds.ActiveQuest, quest.QuestId, [quest.Step, quest.Token, .. quest.Counters], "unknown quest"));
                if (wasActive) notes.Add($"quest {quest.QuestId} no longer exists: set aside at step {quest.Step + 1}");
                return;
            }
            if (!wasActive)
            {
                if (state.ActiveQuests.ContainsKey(quest.QuestId) || (state.CompletedQuests.Contains(quest.QuestId) && !def.Repeatable)) return;
                notes.Add($"quest {quest.QuestId} is back: active again");
            }
            var step = Math.Clamp(quest.Step, 0, def.Steps.Count - 1);
            var objectives = def.Steps[step].Objectives;
            IReadOnlyList<int> counters = quest.Counters;
            var fits = step == quest.Step && counters.Count == objectives.Count;
            for (var i = 0; fits && i < counters.Count; i++) fits = counters[i] >= 0 && counters[i] <= QuestRules.Need(objectives[i]);
            if (!fits)
            {
                counters = new int[objectives.Count];
                notes.Add($"quest {quest.QuestId} has changed: step {step + 1} starts over");
            }
            state.ActiveQuests[quest.QuestId] = QuestProgress.Load(quest.QuestId, step, counters, quest.Token);
        }
    }

    /// <summary>The outfits as they were (T-708): an id the content has lost is kept where it is and counts for nothing, so it comes back with the content, like a look.</summary>
    private void Wardrobe(CharacterState saved, ProgressionState state, List<string> notes)
    {
        foreach (var outfit in saved.Outfits)
        {
            state.Wardrobe.Owned.Add(outfit);
            if (!_content.Outfits.Contains(outfit)) notes.Add($"outfit {outfit} no longer exists: kept, not shown");
        }
        foreach (var (slot, outfit) in saved.OutfitSlots) state.Wardrobe.Chosen[slot] = outfit;
        state.Wardrobe.Hidden = saved.OutfitsHidden;
    }

    // ---- items --------------------------------------------------------------------------------------------------------------------------

    private InventoryState Inventory(CharacterState saved, ProgressionState progression, List<SetAsideItem> aside, List<string> notes)
    {
        var entries = new List<InventoryEntry>();
        var worn = new List<InventoryEntry>();
        foreach (var entry in saved.Items) Place(entry, true);
        foreach (var kept in saved.AsideItems) Place(kept.Entry with { Slot = null }, false);

        var state = new InventoryState { Currency = Math.Max(0, saved.Currency) };
        foreach (var token in saved.SettledTurnIns) state.SettledTurnIns.Add(token);
        state.SetEntries(entries);
        var rules = _items.Rules;
        rules.Refresh(state);
        var wearer = new Wearer(progression.BaseLevel, progression.JobId, _items.WeaponTypesOf(progression.JobId));
        var delta = new InventoryDelta();
        foreach (var entry in worn.OrderBy(e => e.Id))
        {
            // What it wears goes on again by the rules, with everything else in the bag: what no longer may be worn, or no longer together, stays there.
            var reason = rules.PlanEquip(state, entry.Id, entry.Slot, wearer, out var plan);
            if (reason is null && plan!.Displaced.Count == 0) rules.ApplyEquip(state, plan, delta);
            else notes.Add($"{entry.ItemId} (#{entry.Id}) may not be worn as {entry.Slot} any more ({reason ?? "does not fit what else is worn"}): in the bag");
            delta.Clear();
        }
        return state;

        void Place(InventoryEntry entry, bool was)
        {
            if (!_items.TryGetItem(entry.ItemId, out var item))
            {
                aside.Add(new SetAsideItem(entry with { Slot = null }, "unknown item"));
                if (was) notes.Add($"{entry.ItemId} x{entry.Quantity} (#{entry.Id}) no longer exists: set aside");
                return;
            }
            if (!was) notes.Add($"{entry.ItemId} x{entry.Quantity} (#{entry.Id}) is back: in the bag");
            foreach (var card in entry.Cards)
                if (!_items.TryGetItem(card, out _)) notes.Add($"card {card} in {entry.ItemId} (#{entry.Id}) no longer exists: it stays in its socket and does nothing");
            var limit = InventoryRules.StackLimit(item);
            var first = entry with { Slot = null, Quantity = Math.Min(entry.Quantity, limit) };
            entries.Add(first);
            for (var left = entry.Quantity - limit; left > 0; left -= limit) entries.Add(new InventoryEntry(_newItemId(), entry.ItemId, Math.Min(left, limit)));
            if (entry.Quantity > limit) notes.Add($"{entry.ItemId} x{entry.Quantity} (#{entry.Id}) is more than a stack holds now ({limit}): split");
            if (entry.Slot is not null) worn.Add(first with { Slot = entry.Slot });
        }
    }

    // ---- places and times ---------------------------------------------------------------------------------------------------------------

    private bool Walkable(string mapId, GridCell cell) => _content.Grids.TryGetValue(mapId, out var grid) && grid.IsWalkable(cell.X, cell.Y);

    private (string MapId, GridCell Cell) Respawn(SavePoint? point, string mapId) => point is { } p ? (p.MapId, p.Cell) : RespawnOf(mapId);

    private (string MapId, GridCell Cell) RespawnOf(string mapId)
    {
        if (_respawns.TryGetValue(mapId, out var point) && Walkable(point.MapId, point.Cell)) return point;
        var start = _content.Balance.NewCharacter;
        return (start.StartMapId, new GridCell(start.StartX, start.StartY));   // validated content: walkable
    }

    private static Dictionary<string, long> Left(IReadOnlyDictionary<string, long> saved, long away, Func<string, bool> known)
    {
        var left = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (id, ms) in saved)
            if (ms > away && known(id)) left[id] = ms - away;   // a cooldown of something that is gone is nothing worth keeping
        return left;
    }
}
