using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// The root of <c>content/appearance.yaml</c>: the options offered at character creation (ARCHITECTURE §7.5.1).
    /// A single set per game, so it has no id. <b>Cosmetic only</b>: nothing here has a stat, rule or balance effect.
    /// Per gender it lists the body, faces and hairstyles; skin tones and hair colours are palettes shared by both genders.
    /// </summary>
    public sealed record AppearanceDef
    {
        /// <summary>Optional editor-facing name.</summary>
        public string? Name { get; init; }

        /// <summary>Options per gender. Keys are <see cref="Vocabulary.Genders"/> terms; every term must be present.</summary>
        public IReadOnlyDictionary<string, GenderAppearanceDef> Genders { get; init; } =
            Empty.Dictionary<string, GenderAppearanceDef>();

        /// <summary>Skin tone palette (<c>skin.*</c>). Applied as a colour on the body's single material.</summary>
        public IReadOnlyList<ColorOptionDef> SkinTones { get; init; } = Empty.List<ColorOptionDef>();

        /// <summary>Skin tone (<c>skin.*</c>) a new character starts with.</summary>
        public string DefaultSkinToneId { get; init; } = string.Empty;

        /// <summary>Hair colour palette (<c>haircolor.*</c>). Applied as a colour on the hair material.</summary>
        public IReadOnlyList<ColorOptionDef> HairColors { get; init; } = Empty.List<ColorOptionDef>();

        /// <summary>Hair colour (<c>haircolor.*</c>) a new character starts with.</summary>
        public string DefaultHairColorId { get; init; } = string.Empty;
    }

    /// <summary>What one gender offers at creation: its body, its faces and hairstyles, and the starting look.</summary>
    public sealed record GenderAppearanceDef
    {
        /// <summary>Display name of the gender option, e.g. "Male".</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Body model (<c>model.*</c>) of this gender. Its rig decides which faces and hairstyles fit.</summary>
        public string BodyModelAssetKey { get; init; } = string.Empty;

        /// <summary>Face variants (<c>face.*</c>).</summary>
        public IReadOnlyList<FaceOptionDef> Faces { get; init; } = Empty.List<FaceOptionDef>();

        /// <summary>Hairstyles (<c>hair.*</c>), attached at the <c>head</c> attach point.</summary>
        public IReadOnlyList<HairstyleOptionDef> Hairstyles { get; init; } = Empty.List<HairstyleOptionDef>();

        /// <summary>The face and hairstyle a new character of this gender starts with.</summary>
        public AppearanceDefaultDef Default { get; init; } = new AppearanceDefaultDef();
    }

    /// <summary>The starting face and hairstyle of one gender; both must be options of that gender.</summary>
    public sealed record AppearanceDefaultDef
    {
        /// <summary>Face (<c>face.*</c>).</summary>
        public string FaceId { get; init; } = string.Empty;

        /// <summary>Hairstyle (<c>hair.*</c>).</summary>
        public string HairstyleId { get; init; } = string.Empty;
    }

    /// <summary>A face variant: a head model that fits the gender's rig.</summary>
    public sealed record FaceOptionDef
    {
        /// <summary>Face id, <c>face.&lt;name&gt;</c>; unique in the file.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Head model (<c>model.*</c>); same rig as the gender's body, or a rig-less model attached at <c>head</c>.</summary>
        public string ModelAssetKey { get; init; } = string.Empty;
    }

    /// <summary>A hairstyle: a model attached at the <c>head</c> attach point.</summary>
    public sealed record HairstyleOptionDef
    {
        /// <summary>Hairstyle id, <c>hair.&lt;name&gt;</c>; unique in the file.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Hair model (<c>model.*</c>); same rig as the gender's body, or a rig-less model attached at <c>head</c>.</summary>
        public string ModelAssetKey { get; init; } = string.Empty;
    }

    /// <summary>A palette entry: a named colour (a skin tone or a hair colour).</summary>
    public sealed record ColorOptionDef
    {
        /// <summary>Id, <c>skin.&lt;name&gt;</c> or <c>haircolor.&lt;name&gt;</c> by palette; unique in the file.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Colour as <c>#RRGGBB</c> (hex digits, either case).</summary>
        public string Color { get; init; } = string.Empty;
    }
}
