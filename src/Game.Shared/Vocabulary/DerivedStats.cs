namespace Game.Shared.Vocabulary
{
    /// <summary>Derived stat ids. Their values come from formulas in <c>content/formulas.yaml</c>. VOCABULARY.md: "Derived stats".</summary>
    public static class DerivedStats
    {
        /// <summary><c>max_hp</c>: Maximum HP.</summary>
        public const string MaxHp = "max_hp";

        /// <summary><c>max_sp</c>: Maximum SP.</summary>
        public const string MaxSp = "max_sp";

        /// <summary><c>atk</c>: Physical attack.</summary>
        public const string Atk = "atk";

        /// <summary><c>matk</c>: Magic attack.</summary>
        public const string Matk = "matk";

        /// <summary><c>def</c>: Physical defense.</summary>
        public const string Def = "def";

        /// <summary><c>mdef</c>: Magic defense.</summary>
        public const string Mdef = "mdef";

        /// <summary><c>hit</c>: Accuracy.</summary>
        public const string Hit = "hit";

        /// <summary><c>flee</c>: Evasion.</summary>
        public const string Flee = "flee";

        /// <summary><c>crit</c>: Critical rate.</summary>
        public const string Crit = "crit";

        /// <summary><c>aspd</c>: Attack speed.</summary>
        public const string Aspd = "aspd";

        /// <summary><c>move_speed</c>: Movement speed in cells per second.</summary>
        public const string MoveSpeed = "move_speed";

        /// <summary><c>cast_time_mod</c>: Cast time modifier.</summary>
        public const string CastTimeMod = "cast_time_mod";

        /// <summary><c>hp_regen</c>: HP regeneration.</summary>
        public const string HpRegen = "hp_regen";

        /// <summary><c>sp_regen</c>: SP regeneration.</summary>
        public const string SpRegen = "sp_regen";

        /// <summary><c>weight_limit</c>: Carry weight limit.</summary>
        public const string WeightLimit = "weight_limit";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            MaxHp,
            MaxSp,
            Atk,
            Matk,
            Def,
            Mdef,
            Hit,
            Flee,
            Crit,
            Aspd,
            MoveSpeed,
            CastTimeMod,
            HpRegen,
            SpRegen,
            WeightLimit);
    }
}
