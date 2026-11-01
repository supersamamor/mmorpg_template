using System.Collections.Generic;

namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// Sound categories a theme gives a default for (<c>audio.defaults</c> in the manifest, ARCHITECTURE §7.8).
    /// VOCABULARY.md: "Audio default categories". Weapon categories are generated from <see cref="WeaponTypes"/>.
    /// </summary>
    public static class AudioCategories
    {
        /// <summary><c>monster.aggro</c>: A monster notices a player.</summary>
        public const string MonsterAggro = "monster.aggro";

        /// <summary><c>monster.hit</c>: A monster is hit.</summary>
        public const string MonsterHit = "monster.hit";

        /// <summary><c>monster.die</c>: A monster dies.</summary>
        public const string MonsterDie = "monster.die";

        /// <summary><c>item.use</c>: An item is used.</summary>
        public const string ItemUse = "item.use";

        /// <summary><c>item.equip</c>: An item is equipped.</summary>
        public const string ItemEquip = "item.equip";

        /// <summary><c>ui.tap</c>: A button is tapped.</summary>
        public const string UiTap = "ui.tap";

        /// <summary><c>ui.open</c>: A window opens.</summary>
        public const string UiOpen = "ui.open";

        /// <summary><c>ui.close</c>: A window closes.</summary>
        public const string UiClose = "ui.close";

        /// <summary><c>ui.error</c>: An action is refused.</summary>
        public const string UiError = "ui.error";

        /// <summary>Suffix of a weapon swing category: <c>weapon.&lt;weapon_type&gt;.swing</c>.</summary>
        public const string Swing = "swing";

        /// <summary>Suffix of a weapon impact category: <c>weapon.&lt;weapon_type&gt;.impact</c>.</summary>
        public const string Impact = "impact";

        /// <summary>The categories that do not depend on weapon types, in documentation order.</summary>
        public static readonly VocabularySet Fixed = new VocabularySet(
            MonsterAggro,
            MonsterHit,
            MonsterDie,
            ItemUse,
            ItemEquip,
            UiTap,
            UiOpen,
            UiClose,
            UiError);

        /// <summary>Every category: <see cref="Fixed"/>, then swing and impact for each weapon type in <see cref="WeaponTypes.All"/> order.</summary>
        public static readonly VocabularySet All = BuildAll();

        /// <summary>The swing category of a weapon type, e.g. <c>weapon.dagger.swing</c>.</summary>
        public static string WeaponSwing(string weaponType) => "weapon." + weaponType + "." + Swing;

        /// <summary>The impact category of a weapon type, e.g. <c>weapon.dagger.impact</c>.</summary>
        public static string WeaponImpact(string weaponType) => "weapon." + weaponType + "." + Impact;

        private static VocabularySet BuildAll()
        {
            var terms = new List<string>(Fixed);
            foreach (var type in WeaponTypes.All)
            {
                terms.Add(WeaponSwing(type));
                terms.Add(WeaponImpact(type));
            }
            return new VocabularySet(terms.ToArray());
        }
    }
}
