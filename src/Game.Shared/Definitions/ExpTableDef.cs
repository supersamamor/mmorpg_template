using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>An EXP curve for base or job levels (ARCHITECTURE §6.5).</summary>
    public sealed record ExpTableDef : IDefinition
    {
        /// <summary>Id namespace for EXP tables.</summary>
        public const string IdPrefix = IdPrefixes.Exp;

        /// <summary>Table id, e.g. <c>exp.base</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Optional editor-facing name.</summary>
        public string? Name { get; init; }

        /// <summary>
        /// EXP needed to advance from level <c>i + 1</c> to <c>i + 2</c> at index <c>i</c>.
        /// The max level is <c>Exp.Count + 1</c>.
        /// </summary>
        public IReadOnlyList<long> Exp { get; init; } = Empty.List<long>();
    }
}
