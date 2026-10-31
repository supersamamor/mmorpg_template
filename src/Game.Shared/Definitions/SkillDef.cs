using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// A skill. Active skills run <see cref="Effects"/> on their target; passive skills apply
    /// <see cref="PassiveModifiers"/> while learned. Costs and timings are formulas of <c>level</c>.
    /// </summary>
    public sealed record SkillDef : IDefinition
    {
        /// <summary>Id namespace for skills.</summary>
        public const string IdPrefix = IdPrefixes.Skill;

        /// <summary>Skill id, e.g. <c>skill.bash</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Display description.</summary>
        public string? Description { get; init; }

        /// <summary>Highest skill level. Default 1.</summary>
        public int MaxLevel { get; init; } = 1;

        /// <summary>True for a passive skill: no casting, only <see cref="PassiveModifiers"/>.</summary>
        public bool Passive { get; init; }

        /// <summary>What the skill can be used on (sent to the client for targeting UI).</summary>
        public SkillTargetDef Target { get; init; } = new SkillTargetDef();

        /// <summary>SP cost, as a formula.</summary>
        public string SpCost { get; init; } = "0";

        /// <summary>Cast time in milliseconds, as a formula (before <c>cast_time_mod</c>).</summary>
        public string CastTimeMs { get; init; } = "0";

        /// <summary>Cooldown in milliseconds, as a formula.</summary>
        public string CooldownMs { get; init; } = "0";

        /// <summary>Weapon types required to use the skill (<see cref="Vocabulary.WeaponTypes"/>). Empty means any.</summary>
        public IReadOnlyList<string> WeaponTypes { get; init; } = Empty.List<string>();

        /// <summary>Effects applied to the target when the skill resolves.</summary>
        public IReadOnlyList<EffectDef> Effects { get; init; } = Empty.List<EffectDef>();

        /// <summary>Modifiers applied while a passive skill is learned; values may scale with <c>level</c>.</summary>
        public IReadOnlyList<ModifierDef> PassiveModifiers { get; init; } = Empty.List<ModifierDef>();

        /// <summary>Animation played by the user (<see cref="AnimationIds"/>), e.g. <c>cast</c> or <c>attack</c>.</summary>
        public string? AnimationId { get; init; }

        /// <summary>UI icon (<c>icon.*</c>).</summary>
        public string? IconAssetKey { get; init; }

        /// <summary>Visual effect on the target (<c>vfx.*</c>).</summary>
        public string? VfxAssetKey { get; init; }

        /// <summary>Sound on use (<c>sfx.*</c>).</summary>
        public string? SfxAssetKey { get; init; }
    }

    /// <summary>
    /// Skill targeting. <see cref="Range"/> 0 targets the user; otherwise an entity within range
    /// (an enemy, or an ally when <see cref="Friendly"/>), or a cell when <see cref="Ground"/>.
    /// </summary>
    public sealed record SkillTargetDef
    {
        /// <summary>Range in cells (Chebyshev). 0 means self.</summary>
        public int Range { get; init; }

        /// <summary>True to target a ground cell instead of an entity.</summary>
        public bool Ground { get; init; }

        /// <summary>True to target allies; false (default) to target enemies.</summary>
        public bool Friendly { get; init; }
    }
}
