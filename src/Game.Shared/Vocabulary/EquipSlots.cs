namespace Game.Shared.Vocabulary
{
    /// <summary>Equipment slot ids. VOCABULARY.md: "Equipment slots".</summary>
    public static class EquipSlots
    {
        /// <summary><c>head_top</c>: Head top slot.</summary>
        public const string HeadTop = "head_top";

        /// <summary><c>head_mid</c>: Head mid slot.</summary>
        public const string HeadMid = "head_mid";

        /// <summary><c>head_low</c>: Head low slot.</summary>
        public const string HeadLow = "head_low";

        /// <summary><c>body</c>: Body slot.</summary>
        public const string Body = "body";

        /// <summary><c>weapon</c>: Weapon slot.</summary>
        public const string Weapon = "weapon";

        /// <summary><c>shield</c>: Shield slot.</summary>
        public const string Shield = "shield";

        /// <summary><c>garment</c>: Garment slot.</summary>
        public const string Garment = "garment";

        /// <summary><c>footgear</c>: Footgear slot.</summary>
        public const string Footgear = "footgear";

        /// <summary><c>accessory_1</c>: Accessory 1 slot.</summary>
        public const string Accessory1 = "accessory_1";

        /// <summary><c>accessory_2</c>: Accessory 2 slot.</summary>
        public const string Accessory2 = "accessory_2";

        /// <summary>Every term in this category, in documentation order.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            HeadTop,
            HeadMid,
            HeadLow,
            Body,
            Weapon,
            Shield,
            Garment,
            Footgear,
            Accessory1,
            Accessory2);
    }
}
