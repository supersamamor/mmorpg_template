namespace Game.Shared.Vocabulary
{
    /// <summary>Namespace prefixes of content ids, one per definition type. VOCABULARY.md: "Id namespaces".</summary>
    public static class IdPrefixes
    {
        /// <summary><c>job.</c>: Job / class (<c>job.swordsman</c>).</summary>
        public const string Job = "job.";

        /// <summary><c>skill.</c>: Skill (<c>skill.bash</c>).</summary>
        public const string Skill = "skill.";

        /// <summary><c>item.</c>: Item of any kind (<c>item.red_potion</c>).</summary>
        public const string Item = "item.";

        /// <summary><c>monster.</c>: Monster (<c>monster.slime</c>).</summary>
        public const string Monster = "monster.";

        /// <summary><c>status.</c>: Buff / debuff (<c>status.stun</c>).</summary>
        public const string Status = "status.";

        /// <summary><c>quest.</c>: Quest (<c>quest.first_hunt</c>).</summary>
        public const string Quest = "quest.";

        /// <summary><c>npc.</c>: NPC (<c>npc.guide</c>).</summary>
        public const string Npc = "npc.";

        /// <summary><c>dialogue.</c>: Dialogue graph (<c>dialogue.guide_intro</c>).</summary>
        public const string Dialogue = "dialogue.";

        /// <summary><c>map.</c>: Map (<c>map.starter_town</c>).</summary>
        public const string Map = "map.";

        /// <summary><c>drop.</c>: Drop table (<c>drop.slime</c>).</summary>
        public const string Drop = "drop.";

        /// <summary><c>exp.</c>: EXP table (<c>exp.base</c>).</summary>
        public const string Exp = "exp.";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Job,
            Skill,
            Item,
            Monster,
            Status,
            Quest,
            Npc,
            Dialogue,
            Map,
            Drop,
            Exp);

        /// <summary>Regex for the part after the prefix: lowercase <c>a-z0-9_</c>.</summary>
        public const string LocalPartPattern = "[a-z0-9_]+";
    }
}
