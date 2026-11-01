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
    }

    /// <summary>Progression numbers. The max base level is not stored here: it is the length of the base EXP table plus one.</summary>
    public sealed record ProgressionBalanceDef
    {
        /// <summary>EXP table (<c>exp.*</c>) for base levels. Its length sets the max base level (<see cref="ExpTableDef.Exp"/>).</summary>
        public string BaseExpTableId { get; init; } = string.Empty;

        /// <summary>Stat points awarded on reaching a base level. Variable: <c>base_level</c> (the level just reached).</summary>
        public string StatPointsPerBaseLevel { get; init; } = string.Empty;

        /// <summary>Skill points awarded on reaching a job level. Variable: <c>job_level</c> (the level just reached).</summary>
        public string SkillPointsPerJobLevel { get; init; } = string.Empty;

        /// <summary>Stat points a new character starts with.</summary>
        public int StartingStatPoints { get; init; }

        /// <summary>Value every primary stat starts at.</summary>
        public int StartingStatValue { get; init; }

        /// <summary>Highest value a primary stat can be raised to (by points, before equipment and buffs).</summary>
        public int MaxStatValue { get; init; }

        /// <summary>Stat points needed to raise a stat by one. Variable: <c>stat_value</c> (the stat's current value, before the raise).</summary>
        public string StatPointCost { get; init; } = string.Empty;
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
        public string CritMultiplier { get; init; } = string.Empty;

        /// <summary>A player's auto-attack range in cells (Chebyshev) while unarmed. Weapons bring their own range later (T-401).</summary>
        public int PlayerAttackRange { get; init; }

        /// <summary>Shortest time between two path searches of a player walking into attack range of its target.</summary>
        public int PlayerRepathIntervalMs { get; init; }
    }
}
