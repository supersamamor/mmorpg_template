namespace Game.Shared.Vocabulary
{
    /// <summary>Named rig attach points for attached equipment (ARCHITECTURE §7.5). VOCABULARY.md: "Attach points".</summary>
    public static class AttachPoints
    {
        /// <summary><c>head</c>: Head (top headgear).</summary>
        public const string Head = "head";

        /// <summary><c>face</c>: Face (mid headgear).</summary>
        public const string Face = "face";

        /// <summary><c>mouth</c>: Mouth (low headgear).</summary>
        public const string Mouth = "mouth";

        /// <summary><c>back</c>: Back (garment props).</summary>
        public const string Back = "back";

        /// <summary><c>hand_r</c>: Right hand (weapon).</summary>
        public const string HandR = "hand_r";

        /// <summary><c>hand_l</c>: Left hand (shield).</summary>
        public const string HandL = "hand_l";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Head,
            Face,
            Mouth,
            Back,
            HandR,
            HandL);
    }
}
