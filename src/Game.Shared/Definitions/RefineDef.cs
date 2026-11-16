using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// The root of <c>content/refine.yaml</c>: how refining equipment works (ARCHITECTURE §6.8). One table for every <c>refinable</c> item, so it has
    /// no id. <see cref="Levels"/> has one step per level from 1 to <see cref="MaxLevel"/>, in order; step <c>n</c> is the attempt that takes an item
    /// from <c>n - 1</c> to <c>n</c>. A refined item's level is the formula variable <c>refine</c>: its own modifiers use it, and a worn weapon's reaches
    /// combat. The player sees each step's chance, material and failure outcome before trying (the display bundle); nothing here is a drop rate.
    /// </summary>
    public sealed record RefineDef
    {
        /// <summary>Optional editor-facing name.</summary>
        public string? Name { get; init; }

        /// <summary>Highest refine level. 0 turns refining off (then <see cref="Levels"/> is empty).</summary>
        public int MaxLevel { get; init; }

        /// <summary>One step per level, 1 to <see cref="MaxLevel"/>, in order.</summary>
        public IReadOnlyList<RefineLevelDef> Levels { get; init; } = Empty.List<RefineLevelDef>();

        /// <summary>The step that reaches <paramref name="level"/>, or null when there is none.</summary>
        public RefineLevelDef? StepTo(int level) => level >= 1 && level <= Levels.Count ? Levels[level - 1] : null;
    }

    /// <summary>One refine step of <see cref="RefineDef"/>: the attempt that reaches <see cref="Level"/>.</summary>
    public sealed record RefineLevelDef
    {
        /// <summary>The level this step reaches (1 to <c>max_level</c>; the list is in order).</summary>
        public int Level { get; init; }

        /// <summary>Chance of success in whole percent, 1 to 100 (100 always succeeds). Rolled with the instance's random source.</summary>
        public int SuccessPercent { get; init; }

        /// <summary>The material item (<c>item.*</c>) the attempt uses up, success or not.</summary>
        public string MaterialItemId { get; init; } = string.Empty;

        /// <summary>How many units of the material the attempt uses up. At least 1.</summary>
        public int MaterialQuantity { get; init; } = 1;

        /// <summary>What a failure does (<see cref="RefineFailures"/>): <c>keep</c> the level, go <c>down</c> one, or <c>break</c> the item.</summary>
        public string Failure { get; init; } = RefineFailures.Keep;
    }
}
