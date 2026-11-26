using System;
using System.Collections.Generic;

namespace Game.Shared.Vocabulary
{
    /// <summary>
    /// The slots an outfit covers (T-708, ARCHITECTURE §7.5.2). A set of their own, not the equipment slots: they are the parts of a character that <i>look</i> like one thing, so one
    /// of them may stand for several equipment slots, and there is none for what must stay visible (weapon and shield) or has no look (accessories). Vocabulary and not data, because
    /// which equipment a slot covers is part of how a character is drawn. VOCABULARY.md: "Outfit slots".
    /// </summary>
    public static class OutfitSlots
    {
        /// <summary><c>head</c>: hats and hoods. Covers <c>head_top</c>.</summary>
        public const string Head = "head";

        /// <summary><c>face</c>: masks and glasses. Covers <c>head_mid</c> and <c>head_low</c>.</summary>
        public const string Face = "face";

        /// <summary><c>body</c>: what is worn on the torso. Covers <c>body</c>.</summary>
        public const string Body = "body";

        /// <summary><c>back</c>: cloaks and wings. Covers <c>garment</c>.</summary>
        public const string Back = "back";

        /// <summary><c>feet</c>: shoes. Covers <c>footgear</c>.</summary>
        public const string Feet = "feet";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Head,
            Face,
            Body,
            Back,
            Feet);

        private static readonly Dictionary<string, string> s_byEquipSlot = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EquipSlots.HeadTop] = Head,
            [EquipSlots.HeadMid] = Face,
            [EquipSlots.HeadLow] = Face,
            [EquipSlots.Body] = Body,
            [EquipSlots.Garment] = Back,
            [EquipSlots.Footgear] = Feet,
        };

        /// <summary>The outfit slot that covers an equipment slot, or null for one that no outfit may cover (<c>weapon</c>, <c>shield</c>, the accessories).</summary>
        public static string? Covering(string equipSlot) => s_byEquipSlot.TryGetValue(equipSlot, out var slot) ? slot : null;
    }

    /// <summary>
    /// Why a <c>set_outfit</c> was refused (T-708), in <c>OutfitRefused</c> and on the wire as <c>rejected</c> (<c>intent</c> <c>set_outfit</c>, with <c>slot</c> and <c>item</c>, the
    /// outfit). Nothing changed. Checked in this order. VOCABULARY.md: "Outfit reasons".
    /// </summary>
    public static class OutfitReasons
    {
        /// <summary><c>unknown_slot</c>: not an "Outfit slots" term.</summary>
        public const string UnknownSlot = "unknown_slot";

        /// <summary><c>unknown_outfit</c>: the content has no such outfit.</summary>
        public const string UnknownOutfit = "unknown_outfit";

        /// <summary><c>not_owned</c>: the character does not own it.</summary>
        public const string NotOwned = "not_owned";

        /// <summary><c>slot_not_covered</c>: the outfit has nothing for the slot.</summary>
        public const string SlotNotCovered = "slot_not_covered";

        /// <summary><c>wrong_gender</c>: what it has for the slot is for the other gender only.</summary>
        public const string WrongGender = "wrong_gender";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            UnknownSlot,
            UnknownOutfit,
            NotOwned,
            SlotNotCovered,
            WrongGender);
    }
}
