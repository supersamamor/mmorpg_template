namespace Game.Shared.Definitions
{
    /// <summary>Quest autopilot (T-604): the <c>autopilot</c> section of <c>balance.yaml</c>. Times are simulated milliseconds, distances cells.</summary>
    public sealed record AutopilotBalanceDef
    {
        /// <summary>False refuses every <c>start_autopilot</c> (<c>disabled</c>).</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>Most quests one run may be given. At least 1.</summary>
        public int MaxQuests { get; init; } = 10;

        /// <summary>Most shortcut skills one run may be given (0 = auto-attack only).</summary>
        public int MaxSkills { get; init; } = 12;

        /// <summary>Minutes a character may run per 24 hours, counted from the first run of that day; 0 = no limit. Pauses do not count.</summary>
        public int DailyLimitMinutes { get; init; }

        /// <summary>
        /// A run with no progress for this long pauses (<c>no_progress</c>). Progress is a counter or step that moved, a credited kill of a hunted monster, or getting nearer
        /// on the way than ever before. Above 0.
        /// </summary>
        public int NoProgressMs { get; init; } = 120000;

        /// <summary>How often a run that waits for nothing looks at what to do next. Above 0.</summary>
        public int DecideIntervalMs { get; init; } = 300;

        /// <summary>Most path searches all the runs of one map may make in one tick; the others wait for the next. At least 1.</summary>
        public int SearchesPerTick { get; init; } = 4;

        /// <summary>Longest single walk of a journey, in cells, from 1 to <c>movement.max_path_length</c>; a longer way is walked in several.</summary>
        public int LegCells { get; init; } = 24;

        /// <summary>A wanted ground item within this many cells (Chebyshev) is fetched before the next fight. From 0 to <c>aoi.view_radius</c> x <c>aoi.bucket_size</c>.</summary>
        public int GatherRange { get; init; } = 12;

        /// <summary>A shortcut skill that heals (or restores SP) is cast below this share of the maximum, in percent (0 = never, 100 = whenever some is missing).</summary>
        public int SupportBelowPercent { get; init; } = 60;

        /// <summary>At the hunting ground, a monster that hit the character within this long is fought first. 0 = never.</summary>
        public int RetaliateMs { get; init; } = 3000;

        /// <summary>A target that could not be reached, or an item that could not be taken, is left alone for this long. 0 or more.</summary>
        public int AvoidMs { get; init; } = 10000;
    }
}
