using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>A drop table. Server only; never in the client bundle. Each entry rolls independently.</summary>
    public sealed record DropTableDef : IDefinition
    {
        /// <summary>Id namespace for drop tables.</summary>
        public const string IdPrefix = IdPrefixes.Drop;

        /// <summary>Drop table id, e.g. <c>drop.slime</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Optional editor-facing name.</summary>
        public string? Name { get; init; }

        /// <summary>Entries, each rolled once per drop.</summary>
        public IReadOnlyList<DropEntryDef> Entries { get; init; } = Empty.List<DropEntryDef>();
    }

    /// <summary>One possible drop.</summary>
    public sealed record DropEntryDef
    {
        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>Probability 0–1 of dropping. The caller rolls with injected RNG.</summary>
        public double Chance { get; init; }

        /// <summary>Minimum quantity when dropped. Default 1.</summary>
        public int Min { get; init; } = 1;

        /// <summary>Maximum quantity when dropped. Default 1.</summary>
        public int Max { get; init; } = 1;
    }
}
