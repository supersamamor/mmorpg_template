using System;
using System.Collections.Generic;

namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// The parts of a character's model that take a colour of its own (T-706, ARCHITECTURE §7.5.1): a theme's manifest names the painted colours of each role
    /// (<c>recolor</c>), and the client replaces them with the character's. VOCABULARY.md: "Appearance colour roles".
    /// </summary>
    public static class AppearanceColorRoles
    {
        /// <summary><c>skin</c>: takes the skin tone.</summary>
        public const string Skin = "skin";

        /// <summary><c>hair</c>: takes the hair colour.</summary>
        public const string Hair = "hair";

        /// <summary><c>cloth</c>: takes the job's cloth colour.</summary>
        public const string Cloth = "cloth";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Skin,
            Hair,
            Cloth);
    }

    /// <summary>
    /// The theme contract's painted colours (docs/THEMES.md "Appearance"): what a body or face model's vertices are painted with where a character's own colour goes,
    /// when its manifest entry does not say otherwise (<c>recolor</c>). They are what <c>tools/themegen</c> paints the placeholders with. A list is a family: its first
    /// colour becomes the character's colour, the others keep their relation to it (a cheek, a shadow). Colours are sRGB <c>#rrggbb</c> of the vertex colour.
    /// </summary>
    public static class AppearancePaint
    {
        /// <summary>Skin: the base, and the cheek.</summary>
        public static readonly IReadOnlyList<string> Skin = Array.AsReadOnly(new[] { "#f1c7a1", "#e9a08a" });

        /// <summary>Cloth: the base, and its shade.</summary>
        public static readonly IReadOnlyList<string> Cloth = Array.AsReadOnly(new[] { "#efe3c8", "#bbb19c" });

        /// <summary>Hair painted on a body or face (a hairstyle model is recoloured whole): each is a family of its own.</summary>
        public static readonly IReadOnlyList<string> Hair = Array.AsReadOnly(new[] { "#7a4a2a", "#c9b79a", "#3a3040", "#b5643a" });
    }
}
