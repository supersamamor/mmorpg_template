namespace Game.Shared.Vocabulary
{
    /// <summary>Object lists in an exported map logic grid. VOCABULARY.md: "Map object types".</summary>
    public static class MapObjectTypes
    {
        /// <summary><c>spawn</c>: Monster spawn area.</summary>
        public const string Spawn = "spawn";

        /// <summary><c>warp</c>: Warp to another map or cell.</summary>
        public const string Warp = "warp";

        /// <summary><c>npc</c>: NPC placement.</summary>
        public const string Npc = "npc";

        /// <summary><c>zone</c>: Rule zone; see <see cref="ZoneKinds"/>.</summary>
        public const string Zone = "zone";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Spawn,
            Warp,
            Npc,
            Zone);
    }
}
