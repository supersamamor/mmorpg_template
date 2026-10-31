using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// A buff or debuff. Duration and chance come from the <c>apply_status</c> effect that applies it.
    /// </summary>
    public sealed record StatusDef : IDefinition
    {
        /// <summary>Id namespace for statuses.</summary>
        public const string IdPrefix = IdPrefixes.Status;

        /// <summary>Status id, e.g. <c>status.stun</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Display description.</summary>
        public string? Description { get; init; }

        /// <summary>True for a debuff (harmful), false for a buff.</summary>
        public bool Debuff { get; init; }

        /// <summary>Free-form tags for group cleansing via <see cref="RemoveStatusEffect.Tags"/>.</summary>
        public IReadOnlyList<string> Tags { get; init; } = Empty.List<string>();

        /// <summary>Maximum simultaneous stacks. Default 1 (re-applying refreshes).</summary>
        public int MaxStacks { get; init; } = 1;

        /// <summary>Effects applied once when the status starts; <c>modify_stat</c> effects last until it ends.</summary>
        public IReadOnlyList<EffectDef> OnApply { get; init; } = Empty.List<EffectDef>();

        /// <summary>Interval in milliseconds for <see cref="OnTick"/>. 0 means no ticking.</summary>
        public int TickIntervalMs { get; init; }

        /// <summary>Effects applied every <see cref="TickIntervalMs"/> while active (e.g. poison damage).</summary>
        public IReadOnlyList<EffectDef> OnTick { get; init; } = Empty.List<EffectDef>();

        /// <summary>True if the affected entity cannot move.</summary>
        public bool PreventsMove { get; init; }

        /// <summary>True if the affected entity cannot auto-attack.</summary>
        public bool PreventsAttack { get; init; }

        /// <summary>True if the affected entity cannot use skills.</summary>
        public bool PreventsSkills { get; init; }

        /// <summary>UI icon (<c>icon.*</c>).</summary>
        public string? IconAssetKey { get; init; }

        /// <summary>Looping visual on the affected entity (<c>vfx.*</c>).</summary>
        public string? VfxAssetKey { get; init; }
    }
}
