namespace Game.Shared.Vocabulary
{
    /// <summary>Prefixes of asset keys. Only <c>themes/&lt;theme&gt;/asset-manifest.json</c> maps keys to files. VOCABULARY.md: "Asset key prefixes".</summary>
    public static class AssetKeyPrefixes
    {
        /// <summary><c>model.</c>: 3D model (<c>.glb</c>): character, monster, equipment, prop.</summary>
        public const string Model = "model.";

        /// <summary><c>anim.</c>: Animation library for one rig.</summary>
        public const string Anim = "anim.";

        /// <summary><c>scene.</c>: Godot scene, e.g. a map's visual scene.</summary>
        public const string Scene = "scene.";

        /// <summary><c>tex.</c>: Standalone texture.</summary>
        public const string Tex = "tex.";

        /// <summary><c>mat.</c>: Material.</summary>
        public const string Mat = "mat.";

        /// <summary><c>vfx.</c>: Visual effect (particles, decals).</summary>
        public const string Vfx = "vfx.";

        /// <summary><c>sprite.</c>: 2D image used in the 3D world (billboard, ground marker).</summary>
        public const string Sprite = "sprite.";

        /// <summary><c>icon.</c>: UI icon.</summary>
        public const string Icon = "icon.";

        /// <summary><c>ui.</c>: Other UI art.</summary>
        public const string Ui = "ui.";

        /// <summary><c>portrait.</c>: Dialogue portrait.</summary>
        public const string Portrait = "portrait.";

        /// <summary><c>sfx.</c>: Sound effect.</summary>
        public const string Sfx = "sfx.";

        /// <summary><c>music.</c>: Music track.</summary>
        public const string Music = "music.";

        /// <summary><c>font.</c>: Font.</summary>
        public const string Font = "font.";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Model,
            Anim,
            Scene,
            Tex,
            Mat,
            Vfx,
            Sprite,
            Icon,
            Ui,
            Portrait,
            Sfx,
            Music,
            Font);
    }
}
