using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>A non-player character. Placement and facing come from the map's logic grid.</summary>
    public sealed record NpcDef : IDefinition
    {
        /// <summary>Id namespace for NPCs.</summary>
        public const string IdPrefix = IdPrefixes.Npc;

        /// <summary>NPC id, e.g. <c>npc.guide</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Display title shown under the name, e.g. "Guide".</summary>
        public string? Title { get; init; }

        /// <summary>Dialogue started when a player talks to the NPC (<c>dialogue.*</c>).</summary>
        public string? DialogueId { get; init; }

        /// <summary>3D model (<c>model.*</c>).</summary>
        public string? ModelAssetKey { get; init; }

        /// <summary>Default dialogue portrait (<c>portrait.*</c>).</summary>
        public string? PortraitAssetKey { get; init; }
    }
}
