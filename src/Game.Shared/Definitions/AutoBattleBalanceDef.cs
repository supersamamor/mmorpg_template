namespace Game.Shared.Definitions
{
    /// <summary>
    /// Auto-battle (T-811, ARCHITECTURE §6.4.3): the <c>auto_battle</c> section of <c>balance.yaml</c>. The rest of a run's numbers (thinking, searches, skills, retaliation, the daily
    /// limit) are the quest autopilot's (<c>autopilot</c>): it is a second mode of the same run. Times are simulated milliseconds, distances cells.
    /// </summary>
    public sealed record AutoBattleBalanceDef
    {
        /// <summary>False refuses every auto-battle start (<c>battle_disabled</c>); the quest autopilot is not affected.</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Monsters within this many cells (Chebyshev) of the anchor, the cell the run started on, are fought; the run's own walks stay within it. From 1 to half of what every
        /// player is sure to see (<c>aoi.view_radius</c> x <c>aoi.bucket_size</c>), so a character anywhere in the area sees the whole of it.
        /// </summary>
        public int RadiusCells { get; init; } = 6;

        /// <summary>A monster that hit the character (retaliation) is fought up to this many cells beyond the radius. From 0 to the radius.</summary>
        public int RetaliateExtraCells { get; init; } = 3;

        /// <summary>A monster more than this many levels above the character's base level is never fought. 0 or more; lower monsters always are.</summary>
        public int MaxLevelsAbove { get; init; } = 5;

        /// <summary>True: the character picks up what it is first to take (its own kills' drops) within the area. A drop that does not fit ends the run (<c>bag_full</c>).</summary>
        public bool Loot { get; init; } = true;

        /// <summary>Below this share of the maximum HP, in percent, the run stops (<c>low_hp</c>) unless a shortcut skill heals. 0 = never; at most 100.</summary>
        public int StopBelowHpPercent { get; init; } = 20;

        /// <summary>A run without a kill for this long stops (<c>nothing_to_fight</c>). 0 = never.</summary>
        public int IdleStopMs { get; init; } = 300000;
    }
}
