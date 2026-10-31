namespace Game.Shared.Vocabulary
{
    /// <summary>Animation ids shared by every model on the same rig (ARCHITECTURE §7.6). VOCABULARY.md: "Animation ids".</summary>
    public static class AnimationIds
    {
        /// <summary><c>idle</c>: Standing still.</summary>
        public const string Idle = "idle";

        /// <summary><c>walk</c>: Moving.</summary>
        public const string Walk = "walk";

        /// <summary><c>attack</c>: Auto-attack or physical skill.</summary>
        public const string Attack = "attack";

        /// <summary><c>cast</c>: Casting a skill.</summary>
        public const string Cast = "cast";

        /// <summary><c>hit</c>: Taking a hit.</summary>
        public const string Hit = "hit";

        /// <summary><c>die</c>: Dying.</summary>
        public const string Die = "die";

        /// <summary><c>sit</c>: Sitting.</summary>
        public const string Sit = "sit";

        /// <summary><c>pick_up</c>: Picking up an item.</summary>
        public const string PickUp = "pick_up";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Idle,
            Walk,
            Attack,
            Cast,
            Hit,
            Die,
            Sit,
            PickUp);
    }
}
