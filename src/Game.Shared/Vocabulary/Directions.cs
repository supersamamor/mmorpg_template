namespace Game.Shared.Vocabulary
{
    /// <summary>Facing directions on the 8-direction grid. VOCABULARY.md: "Directions (facing)".</summary>
    public static class Directions
    {
        /// <summary><c>n</c>: North.</summary>
        public const string N = "n";

        /// <summary><c>ne</c>: North-east.</summary>
        public const string Ne = "ne";

        /// <summary><c>e</c>: East.</summary>
        public const string E = "e";

        /// <summary><c>se</c>: South-east.</summary>
        public const string Se = "se";

        /// <summary><c>s</c>: South.</summary>
        public const string S = "s";

        /// <summary><c>sw</c>: South-west.</summary>
        public const string Sw = "sw";

        /// <summary><c>w</c>: West.</summary>
        public const string W = "w";

        /// <summary><c>nw</c>: North-west.</summary>
        public const string Nw = "nw";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            N,
            Ne,
            E,
            Se,
            S,
            Sw,
            W,
            Nw);
    }
}
