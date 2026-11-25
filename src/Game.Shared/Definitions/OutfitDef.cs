using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// An outfit (T-708, ARCHITECTURE §7.5.2): something a character can own and show in place of its gear. <b>It only ever changes how a character looks.</b> There is no
    /// field here for a modifier, an effect, a requirement, a price or a drop, and none may be added: an unknown key is a load error, and no item, shop or drop table can name an outfit.
    /// </summary>
    public sealed record OutfitDef : IDefinition
    {
        /// <summary>Id namespace for outfits.</summary>
        public const string IdPrefix = IdPrefixes.Outfit;

        /// <summary>Outfit id, e.g. <c>outfit.rain_cloak</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Display description.</summary>
        public string? Description { get; init; }

        /// <summary>Icon (<c>icon.*</c>) in the wardrobe.</summary>
        public string IconAssetKey { get; init; } = string.Empty;

        /// <summary>
        /// The character's own colours its pieces take (<see cref="AppearanceColorRoles"/>: the skin tone, the hair colour, the job's cloth colour), where the theme's models are painted
        /// with them. Empty: the pieces keep their own colours.
        /// </summary>
        public IReadOnlyList<string> TakesColors { get; init; } = Empty.List<string>();

        /// <summary>Its pieces; the slots it covers are the ones that have a piece. At least one. A slot has one piece for everyone, or at most one per gender.</summary>
        public IReadOnlyList<OutfitPieceDef> Pieces { get; init; } = Empty.List<OutfitPieceDef>();
    }

    /// <summary>One piece of an outfit: what is drawn in one slot.</summary>
    public sealed record OutfitPieceDef
    {
        /// <summary>Outfit slot (<see cref="OutfitSlots"/>): equipment and job pieces of the equipment slots it covers are not drawn while this is.</summary>
        public string Slot { get; init; } = string.Empty;

        /// <summary>Piece model (<c>model.*</c>): <c>attach</c> or <c>skinned</c> in the theme manifest, like worn equipment.</summary>
        public string ModelAssetKey { get; init; } = string.Empty;

        /// <summary>Gender it is for (<see cref="Vocabulary.Genders"/>). Null: both.</summary>
        public string? Gender { get; init; }
    }
}
