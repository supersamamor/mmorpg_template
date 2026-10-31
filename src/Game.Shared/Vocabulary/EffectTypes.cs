namespace Game.Shared.Vocabulary
{
    /// <summary>Effect types (v1): the <c>type</c> discriminator of <see cref="Game.Shared.Definitions.EffectDef"/>. VOCABULARY.md: "Effect types (v1)".</summary>
    public static class EffectTypes
    {
        /// <summary><c>damage</c>: Physical/magic damage via formula + element.</summary>
        public const string Damage = "damage";

        /// <summary><c>heal</c>: Restore HP.</summary>
        public const string Heal = "heal";

        /// <summary><c>restore_sp</c>: Restore SP.</summary>
        public const string RestoreSp = "restore_sp";

        /// <summary><c>apply_status</c>: Apply a <c>status.*</c> with chance/duration.</summary>
        public const string ApplyStatus = "apply_status";

        /// <summary><c>remove_status</c>: Cleanse statuses by id or tag.</summary>
        public const string RemoveStatus = "remove_status";

        /// <summary><c>modify_stat</c>: Temporary stat modifier (used by statuses).</summary>
        public const string ModifyStat = "modify_stat";

        /// <summary><c>knockback</c>: Push target N cells.</summary>
        public const string Knockback = "knockback";

        /// <summary><c>teleport</c>: Move caster (random/save point/map).</summary>
        public const string Teleport = "teleport";

        /// <summary><c>summon</c>: Spawn a monster.</summary>
        public const string Summon = "summon";

        /// <summary><c>area</c>: Wrap child effects over an AoE shape.</summary>
        public const string Area = "area";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Damage,
            Heal,
            RestoreSp,
            ApplyStatus,
            RemoveStatus,
            ModifyStat,
            Knockback,
            Teleport,
            Summon,
            Area);
    }
}
