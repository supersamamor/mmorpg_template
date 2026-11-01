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
    }
}
