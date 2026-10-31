using System.Collections.Generic;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// Element multipliers, the root of <c>content/elements.yaml</c>. A single table per game, so it has no id.
    /// </summary>
    public sealed record ElementTableDef
    {
        /// <summary>Optional editor-facing name.</summary>
        public string? Name { get; init; }

        /// <summary>
        /// Attacking element → defending element → damage multiplier (1 = normal, 0 = immune,
        /// negative heals). Keys are <see cref="Vocabulary.Elements"/> ids.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Multipliers { get; init; } =
            Empty.Dictionary<string, IReadOnlyDictionary<string, double>>();
    }
}
