namespace Game.Shared.Vocabulary
{
    /// <summary>Audio buses a sound plays on; bus volumes are player settings. VOCABULARY.md: "Audio buses".</summary>
    public static class AudioBuses
    {
        /// <summary><c>music</c>: Music tracks.</summary>
        public const string Music = "music";

        /// <summary><c>sfx</c>: Sound effects in the world (combat, skills, items).</summary>
        public const string Sfx = "sfx";

        /// <summary><c>ui</c>: Interface sounds (taps, windows, jingles).</summary>
        public const string Ui = "ui";

        /// <summary><c>ambient</c>: Ambience loops.</summary>
        public const string Ambient = "ambient";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Music,
            Sfx,
            Ui,
            Ambient);
    }
}
