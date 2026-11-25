using System.Collections.Generic;
using Game.Shared.Formulas;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// The root of <c>content/balance.yaml</c>: tuning numbers and small formulas that are not per-entity stats
    /// (ARCHITECTURE §6.5). A single set per game, so it has no id. Progression and combat code reads its numbers
    /// from here instead of hardcoding them.
    /// </summary>
    public sealed record BalanceDef
    {
        /// <summary>Optional editor-facing name.</summary>
        public string? Name { get; init; }

        /// <summary>Display name of the currency (engine id <c>currency</c>), as the client shows it. Cosmetic: no code reads it as logic.</summary>
        public string CurrencyName { get; init; } = "crowns";

        /// <summary>Stat point, skill point and level cap numbers.</summary>
        public ProgressionBalanceDef Progression { get; init; } = new ProgressionBalanceDef();

        /// <summary>Damage and chance clamps applied after the combat formulas.</summary>
        public CombatBalanceDef Combat { get; init; } = new CombatBalanceDef();

        /// <summary>Limits on path searches (grid movement, ARCHITECTURE §4).</summary>
        public MovementBalanceDef Movement { get; init; } = new MovementBalanceDef();

        /// <summary>Area of interest: which entities each observer is sent (ARCHITECTURE §3).</summary>
        public AoiBalanceDef Aoi { get; init; } = new AoiBalanceDef();

        /// <summary>Where and as what a newly created character starts (the server's player join, T-205).</summary>
        public NewCharacterBalanceDef NewCharacter { get; init; } = new NewCharacterBalanceDef();

        /// <summary>Monster behavior timings shared by every monster (ARCHITECTURE §6.6); per-monster ranges are in <see cref="MonsterAiDef"/>.</summary>
        public AiBalanceDef Ai { get; init; } = new AiBalanceDef();

        /// <summary>What happens when a player dies and respawns (the server's combat loop).</summary>
        public DeathBalanceDef Death { get; init; } = new DeathBalanceDef();

        /// <summary>Ground items a monster drops: where they land, who may take them and how long they stay.</summary>
        public LootBalanceDef Loot { get; init; } = new LootBalanceDef();

        /// <summary>Natural HP and SP regeneration.</summary>
        public RegenBalanceDef Regen { get; init; } = new RegenBalanceDef();

        /// <summary>A character's inventory: entry count, weight thresholds, pick-up range, dropped items and two-handed weapons (ARCHITECTURE §6.8).</summary>
        public InventoryBalanceDef Inventory { get; init; } = new InventoryBalanceDef();

        /// <summary>Talking to NPCs and the services they open (T-601, ARCHITECTURE §6.4).</summary>
        public DialogueBalanceDef Dialogue { get; init; } = new DialogueBalanceDef();

        /// <summary>Quests (T-602, ARCHITECTURE §6.4).</summary>
        public QuestBalanceDef Quests { get; init; } = new QuestBalanceDef();

        /// <summary>Shops (T-603, ARCHITECTURE §6.8).</summary>
        public ShopBalanceDef Shop { get; init; } = new ShopBalanceDef();

        /// <summary>Character slots and what a character may be called (T-701, ARCHITECTURE §10).</summary>
        public CharactersBalanceDef Characters { get; init; } = new CharactersBalanceDef();
    }

    /// <summary>Character slots and names (T-701): the <c>characters</c> section of <c>balance.yaml</c>. Whether a name is free is decided by the database.</summary>
    public sealed record CharactersBalanceDef
    {
        /// <summary>Most characters an account has at once (deleted ones do not count). At least 1.</summary>
        public int MaxSlots { get; init; } = 3;

        /// <summary>Shortest name, at least 1.</summary>
        public int NameMinLength { get; init; } = 3;

        /// <summary>Longest name, at least <see cref="NameMinLength"/> and at most 32.</summary>
        public int NameMaxLength { get; init; } = 16;

        /// <summary>Letters a name may use: single characters (<c>x</c>) and ranges (<c>a-z</c>). Names that differ only in letter case are the same name.</summary>
        public IReadOnlyList<string> NameLetters { get; init; } = new[] { "a-z", "A-Z" };

        /// <summary>Single characters allowed between two letters only: never first, last, or two in a row.</summary>
        public IReadOnlyList<string> NameSeparators { get; init; } = Empty.List<string>();
    }

    /// <summary>Quests (T-602): the <c>quests</c> section of <c>balance.yaml</c>.</summary>
    public sealed record QuestBalanceDef
    {
        /// <summary>Most quests a character may have active at once; a start beyond it is refused <c>too_many_active</c>. At least 1.</summary>
        public int MaxActive { get; init; } = 10;
    }

    /// <summary>Player death and respawn. Percentages are whole-number percents (50 means half).</summary>
    public sealed record DeathBalanceDef
    {
        /// <summary>Share of <c>max_hp</c> a player respawns with, in percent (1–100). Rounded down, at least 1 HP.</summary>
        public int RespawnHpPercent { get; init; }

        /// <summary>
        /// Base EXP lost on death, in percent of the current base level's requirement (0 = no penalty). Rounded down; never takes EXP below
        /// 0, so a character never loses a level. Nothing is lost at the max level, where EXP is always 0.
        /// </summary>
        public int ExpPenaltyPercent { get; init; }

        /// <summary>
        /// How long a dead monster stays in the world (<c>Dead</c>, not targetable) before it is despawned, so clients can play its death
        /// animation. Simulated milliseconds from the tick it died in; 0 despawns it on the next tick. The monster's respawn timer counts
        /// from the death, not from the despawn.
        /// </summary>
        public int CorpseMs { get; init; }
    }

    /// <summary>
    /// Natural regeneration of HP and SP. The rates are the derived stats <c>hp_regen</c> and <c>sp_regen</c> (points per second,
    /// <c>formulas.yaml</c>); this is only when it pauses.
    /// </summary>
    public sealed record RegenBalanceDef
    {
        /// <summary>
        /// An entity that deals or takes damage does not regenerate for this many simulated milliseconds afterwards (and the dead never do).
        /// </summary>
        public int OutOfCombatDelayMs { get; init; }
    }

    /// <summary>Ground items. Times are simulated milliseconds; distances are cells (Chebyshev).</summary>
    public sealed record LootBalanceDef
    {
        /// <summary>How long after the drop only the top damager may pick an item up; after that anyone may.</summary>
        public int PriorityMs { get; init; }

        /// <summary>How long a ground item stays before it disappears.</summary>
        public int LifetimeMs { get; init; }

        /// <summary>
        /// Drops land on a random walkable cell within this many cells of the death cell (0 = always the death cell). Never on a blocked cell.
        /// </summary>
        public int SpreadRadius { get; init; }
    }

    /// <summary>
    /// Monster AI timings and distances that are the same for every monster. Times are simulated milliseconds (the 10 Hz tick rounds them up
    /// to whole ticks), distances are cells. Per-monster numbers (aggressive, aggro and chase range, attack range, aspd) are monster data.
    /// </summary>
    public sealed record AiBalanceDef
    {
        /// <summary>How often an awake aggressive monster looks for a player to attack.</summary>
        public int ThinkIntervalMs { get; init; }

        /// <summary>Shortest pause an idle monster waits before it wanders again.</summary>
        public int WanderPauseMinMs { get; init; }

        /// <summary>Longest pause an idle monster waits before it wanders again (picked uniformly between min and max).</summary>
        public int WanderPauseMaxMs { get; init; }

        /// <summary>How far from its home cell (Chebyshev, in cells) a wandering monster may walk.</summary>
        public int WanderRadius { get; init; }

        /// <summary>Shortest time between two path searches of a chasing or returning monster.</summary>
        public int RepathIntervalMs { get; init; }

        /// <summary>A returning monster that is not home after this long is put back on its home cell.</summary>
        public int ReturnTimeoutMs { get; init; }

        /// <summary>
        /// A monster that gave up a chase and arrives home is healed to full HP when true (the damage-share table resets either way).
        /// Defaults to true: otherwise a monster could be worn down by hit-and-run.
        /// </summary>
        public bool HealOnReturn { get; init; } = true;
    }

    /// <summary>
    /// A new character's starting place and job. Its stats come from <see cref="ProgressionBalanceDef"/> (starting stat value and
    /// points, level 1/1) plus the job's stat bonuses; the job id is also what observers are shown it as (its display key).
    /// </summary>
    public sealed record NewCharacterBalanceDef
    {
        /// <summary>Map (<c>map.*</c>) a new character appears on.</summary>
        public string StartMapId { get; init; } = string.Empty;

        /// <summary>Start cell X on that map's logic grid; the cell must be walkable.</summary>
        public int StartX { get; init; }

        /// <summary>Start cell Y on that map's logic grid; the cell must be walkable.</summary>
        public int StartY { get; init; }

        /// <summary>Facing at the start, a <c>Directions</c> term (<c>n</c>, <c>ne</c>, ... <c>nw</c>).</summary>
        public string StartFacing { get; init; } = string.Empty;

        /// <summary>Job (<c>job.*</c>) a new character starts in.</summary>
        public string StartJobId { get; init; } = string.Empty;

        /// <summary>What a new character carries (and wears) when it first joins. Ids are validated at load. Empty means nothing.</summary>
        public IReadOnlyList<StartItemDef> StartItems { get; init; } = Empty.List<StartItemDef>();
    }

    /// <summary>One item a new character starts with.</summary>
    public sealed record StartItemDef
    {
        /// <summary>The item (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>How many, at least 1. An equipment item becomes that many entries.</summary>
        public int Quantity { get; init; } = 1;

        /// <summary>True to wear it from the start (equipment only; the usual equip rules for the start job apply).</summary>
        public bool Equipped { get; init; }
    }

    /// <summary>
    /// The inventory (ARCHITECTURE §6.8). Weight is the sum of <c>weight × quantity</c> of every entry, worn ones included; the limit is the derived
    /// stat <c>weight_limit</c>. Percentages are whole-number percents of that limit.
    /// </summary>
    public sealed record InventoryBalanceDef
    {
        /// <summary>Most entries a character can hold (a stack counts once, each equipment item once, worn ones included).</summary>
        public int MaxEntries { get; init; }

        /// <summary>At or above this share of the weight limit natural HP and SP regeneration stops (<c>burdened</c>). 1–100.</summary>
        public int BurdenedPercent { get; init; }

        /// <summary>At or above this share the character also cannot attack (<c>overweight</c>). From <c>burdened_percent</c> to 100.</summary>
        public int OverweightPercent { get; init; }

        /// <summary>A player picks up a ground item within this many cells (Chebyshev) of it, walking there first. 0 or more.</summary>
        public int PickupRange { get; init; }

        /// <summary>How long an item a player dropped stays on the ground, in simulated milliseconds. Above 0.</summary>
        public int DroppedLifetimeMs { get; init; }

        /// <summary>
        /// Weapon types held in both hands (<c>WeaponTypes</c> terms): such a weapon in <c>weapon</c> also takes the <c>shield</c> slot, so equipping
        /// it takes a shield off, and equipping a shield takes it off.
        /// </summary>
        public IReadOnlyList<string> TwoHandedWeaponTypes { get; init; } = Empty.List<string>();
    }

    /// <summary>Progression numbers. The max base level is not stored here: it is the length of the base EXP table plus one.</summary>
    public sealed record ProgressionBalanceDef
    {
        /// <summary>EXP table (<c>exp.*</c>) for base levels. Its length sets the max base level (<see cref="ExpTableDef.Exp"/>).</summary>
        public string BaseExpTableId { get; init; } = string.Empty;

        /// <summary>Stat points awarded on reaching a base level. Variable: <c>base_level</c> (the level just reached).</summary>
        [Formula]
        public string StatPointsPerBaseLevel { get; init; } = string.Empty;

        /// <summary>Skill points awarded on reaching a job level. Variable: <c>job_level</c> (the level just reached).</summary>
        [Formula]
        public string SkillPointsPerJobLevel { get; init; } = string.Empty;

        /// <summary>Stat points a new character starts with.</summary>
        public int StartingStatPoints { get; init; }

        /// <summary>Value every primary stat starts at.</summary>
        public int StartingStatValue { get; init; }

        /// <summary>Highest value a primary stat can be raised to (by points, before equipment and buffs).</summary>
        public int MaxStatValue { get; init; }

        /// <summary>Stat points needed to raise a stat by one. Variable: <c>stat_value</c> (the stat's current value, before the raise).</summary>
        [Formula]
        public string StatPointCost { get; init; } = string.Empty;

        /// <summary>When a stat or skill reset (<c>reset_stats</c>, <c>reset_skills</c>) is free or refused (T-503).</summary>
        public ResetBalanceDef Reset { get; init; } = new ResetBalanceDef();
    }

    /// <summary>
    /// Rules for using a reset item (an item whose consumable effects include <c>reset_stats</c> or <c>reset_skills</c>). A refused use changes
    /// nothing and uses nothing up. There is no currency yet, so a reset costs the item itself.
    /// </summary>
    public sealed record ResetBalanceDef
    {
        /// <summary>
        /// A character below this base level resets for free: the item is not used up (its cooldown still runs). 0 means never free.
        /// </summary>
        public int FreeBelowBaseLevel { get; init; }

        /// <summary>True to refuse a reset while the character dealt or took damage within <c>regen.out_of_combat_delay_ms</c>.</summary>
        public bool RefuseInCombat { get; init; }

        /// <summary>True to refuse a reset while the character is dead.</summary>
        public bool RefuseWhenDead { get; init; }
    }

    /// <summary>
    /// Limits that keep one path search cheap. Both are in cells/nodes, never time, so they behave the same on every device.
    /// </summary>
    public sealed record MovementBalanceDef
    {
        /// <summary>Longest path, in cells (steps), a search may return; a longer route counts as "no path".</summary>
        public int MaxPathLength { get; init; }

        /// <summary>Most nodes a single search may expand before it gives up.</summary>
        public int MaxNodesExpanded { get; init; }
    }

    /// <summary>
    /// Area-of-interest numbers. The map is split into square buckets of <see cref="BucketSize"/> cells; an observer sees every entity
    /// whose bucket is within <see cref="ViewRadius"/> buckets (Chebyshev) of its own, so it sees at least
    /// <c>view_radius * bucket_size</c> cells in every direction.
    /// </summary>
    public sealed record AoiBalanceDef
    {
        /// <summary>Side of one AOI bucket, in cells.</summary>
        public int BucketSize { get; init; }

        /// <summary>How many buckets around its own an observer sees (Chebyshev distance in buckets).</summary>
        public int ViewRadius { get; init; }

        /// <summary>
        /// Most entities one observer sees from its window, itself included: the nearest, plus those always visible (itself, its attack
        /// target, its attackers), which are never cut.
        /// </summary>
        public int MaxVisible { get; init; }

        /// <summary>
        /// Ranks an entity already shown may slip before it is cut. A new entity enters at rank <c>max_visible - cap_hysteresis</c> or
        /// better; one already shown stays until it ranks past <c>max_visible</c> (nearest first, then lowest id). From 0 to below <c>max_visible</c>.
        /// </summary>
        public int CapHysteresis { get; init; }

        /// <summary>Slots of <c>max_visible</c> kept for the nearest monsters while there are that many. Slots a kind does not use go back to the others.</summary>
        public int CapReservedMonsters { get; init; }

        /// <summary>Slots of <c>max_visible</c> kept for the nearest NPCs while there are that many.</summary>
        public int CapReservedNpcs { get; init; }

        /// <summary>Slots of <c>max_visible</c> kept for the nearest ground items while there are that many; items the observer may pick up come first.</summary>
        public int CapReservedItems { get; init; }

        /// <summary>
        /// Ranks an entity already shown may slip past its kind's reservation, or past <c>cap_pinned_chasers</c>, and keep its slot. 0 or more.
        /// All three reservations together must leave at least one slot below the cutoff (<c>max_visible - cap_hysteresis</c>), for the observer itself.
        /// </summary>
        public int CapReservedMargin { get; init; }

        /// <summary>
        /// How many monsters chasing an observer, nearest first, are always visible to it before they have hit it. The others rank like any monster,
        /// so a long train cannot bypass the cap. 0 or more; with <c>cap_reserved_margin</c>, below <c>max_visible</c>.
        /// </summary>
        public int CapPinnedChasers { get; init; }
    }

    /// <summary>
    /// Combat clamps. Chances are probabilities on a 0–1 scale, the same scale as the hit and crit
    /// formulas of <see cref="FormulaSetDef"/>.
    /// </summary>
    public sealed record CombatBalanceDef
    {
        /// <summary>Lowest damage a hit that lands can deal.</summary>
        public int MinDamage { get; init; }

        /// <summary>Highest damage a single hit can deal.</summary>
        public int MaxDamage { get; init; }

        /// <summary>Lowest hit probability (0–1).</summary>
        public double MinHitChance { get; init; }

        /// <summary>Highest hit probability (0–1).</summary>
        public double MaxHitChance { get; init; }

        /// <summary>Lowest critical probability (0–1).</summary>
        public double MinCritChance { get; init; }

        /// <summary>Highest critical probability (0–1).</summary>
        public double MaxCritChance { get; init; }

        /// <summary>
        /// Damage multiplier on a critical hit, as a formula. Caster and <c>t_</c> variables are allowed
        /// (e.g. <c>1.5 + luk * 0.005</c>); a constant is fine.
        /// </summary>
        [Formula]
        public string CritMultiplier { get; init; } = string.Empty;

        /// <summary>A player's auto-attack range in cells (Chebyshev) while unarmed. An equipped weapon uses its own <c>attack_range</c> instead.</summary>
        public int PlayerAttackRange { get; init; }

        /// <summary>Shortest time between two path searches of a player walking into attack range of its target.</summary>
        public int PlayerRepathIntervalMs { get; init; }
    }
}
