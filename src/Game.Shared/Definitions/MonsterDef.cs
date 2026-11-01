using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// A monster. Server-only parts (<see cref="Ai"/>, <see cref="DropTableIds"/>) never go to the client bundle.
    /// Placement and respawn timing come from the map's logic grid, not from here.
    /// </summary>
    public sealed record MonsterDef : IDefinition
    {
        /// <summary>Id namespace for monsters.</summary>
        public const string IdPrefix = IdPrefixes.Monster;

        /// <summary>Monster id, e.g. <c>monster.slime</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Base level (formula variable <c>t_level</c> / <c>base_level</c>).</summary>
        public int Level { get; init; } = 1;

        /// <summary>Race (<see cref="MonsterRaces"/>).</summary>
        public string Race { get; init; } = string.Empty;

        /// <summary>Size (<see cref="MonsterSizes"/>).</summary>
        public string Size { get; init; } = string.Empty;

        /// <summary>Defending element, also its attack element (<see cref="Elements"/>).</summary>
        public string Element { get; init; } = string.Empty;

        /// <summary>
        /// Stat values by stat id (<see cref="Vocabulary.Stats"/>). Primary stats feed the formulas; a derived stat
        /// listed here (e.g. <c>max_hp</c>, <c>move_speed</c>) replaces the formula result.
        /// </summary>
        public IReadOnlyDictionary<string, double> Stats { get; init; } = Empty.Dictionary<string, double>();

        /// <summary>Auto-attack range in cells. Default 1.</summary>
        public int AttackRange { get; init; } = 1;

        /// <summary>Base EXP given on kill.</summary>
        public long BaseExp { get; init; }

        /// <summary>Job EXP given on kill.</summary>
        public long JobExp { get; init; }

        /// <summary>Drop tables rolled on death (<c>drop.*</c>).</summary>
        public IReadOnlyList<string> DropTableIds { get; init; } = Empty.List<string>();

        /// <summary>Behavior settings.</summary>
        public MonsterAiDef Ai { get; init; } = new MonsterAiDef();

        /// <summary>3D model (<c>model.*</c>); rig and animation library come from the manifest.</summary>
        public string? ModelAssetKey { get; init; }

        /// <summary>Optional sounds (<c>sfx.*</c>); a missing one falls back to the theme default (ARCHITECTURE §7.8).</summary>
        public MonsterSoundsDef? Sounds { get; init; }
    }

    /// <summary>Sounds of a monster, all optional. Asset keys only.</summary>
    public sealed record MonsterSoundsDef
    {
        /// <summary>Played when the monster notices a player (<c>sfx.*</c>).</summary>
        public string? Aggro { get; init; }

        /// <summary>Played when the monster is hit (<c>sfx.*</c>).</summary>
        public string? Hit { get; init; }

        /// <summary>Played when the monster dies (<c>sfx.*</c>).</summary>
        public string? Die { get; init; }
    }

    /// <summary>Monster behavior settings. Server only.</summary>
    public sealed record MonsterAiDef
    {
        /// <summary>True to attack players on sight; false waits until attacked.</summary>
        public bool Aggressive { get; init; }

        /// <summary>Distance in cells at which an aggressive monster notices players.</summary>
        public int AggroRange { get; init; }

        /// <summary>Distance in cells from its target beyond which the monster gives up.</summary>
        public int ChaseRange { get; init; }

        /// <summary>Skills the monster may use.</summary>
        public IReadOnlyList<MonsterSkillDef> Skills { get; init; } = Empty.List<MonsterSkillDef>();
    }

    /// <summary>A skill a monster may use.</summary>
    public sealed record MonsterSkillDef
    {
        /// <summary>Skill id (<c>skill.*</c>).</summary>
        public string SkillId { get; init; } = string.Empty;

        /// <summary>Skill level used. Default 1.</summary>
        public int Level { get; init; } = 1;

        /// <summary>Probability 0–1 that the monster uses it when it may act and the skill is ready.</summary>
        public double Chance { get; init; }
    }
}
