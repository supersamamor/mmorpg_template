using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// Item kind. Serialized in snake_case (<see cref="ItemKinds"/>). The matching optional block on
    /// <see cref="ItemDef"/> holds kind-specific data.
    /// </summary>
    public enum ItemKind
    {
        /// <summary>Used up for effects; uses <see cref="ItemDef.Consumable"/>.</summary>
        Consumable,

        /// <summary>Worn in equipment slots; uses <see cref="ItemDef.Equip"/>.</summary>
        Equip,

        /// <summary>Socketed into equipment; uses <see cref="ItemDef.Card"/>.</summary>
        Card,

        /// <summary>Crafting / trade material. No block.</summary>
        Material,

        /// <summary>Quest item. No block.</summary>
        Quest,

        /// <summary>Ammunition. No block in v1.</summary>
        Ammo,
    }

    /// <summary>
    /// Any item. Kind-specific data lives in optional sub-blocks rather than subclasses, so YAML stays
    /// flat and the schema simple. Exactly the block matching <see cref="Kind"/> should be set (validator rule).
    /// </summary>
    public sealed record ItemDef : IDefinition
    {
        /// <summary>Id namespace for items.</summary>
        public const string IdPrefix = IdPrefixes.Item;

        /// <summary>Item id, e.g. <c>item.red_potion</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Display description.</summary>
        public string? Description { get; init; }

        /// <summary>Item kind.</summary>
        public ItemKind Kind { get; init; }

        /// <summary>Weight per unit (counts toward <c>weight_limit</c>).</summary>
        public int Weight { get; init; }

        /// <summary>NPC shop buy price in currency.</summary>
        public long BuyPrice { get; init; }

        /// <summary>NPC shop sell price in currency.</summary>
        public long SellPrice { get; init; }

        /// <summary>Maximum units per inventory stack. Default 1 (not stackable).</summary>
        public int MaxStack { get; init; } = 1;

        /// <summary>UI icon (<c>icon.*</c>).</summary>
        public string? IconAssetKey { get; init; }

        /// <summary>Visual of the item lying on the ground (<c>model.*</c> or <c>sprite.*</c>).</summary>
        public string? GroundAssetKey { get; init; }

        /// <summary>Consumable data; set when <see cref="Kind"/> is <see cref="ItemKind.Consumable"/>.</summary>
        public ItemConsumableDef? Consumable { get; init; }

        /// <summary>Equipment data; set when <see cref="Kind"/> is <see cref="ItemKind.Equip"/>.</summary>
        public ItemEquipDef? Equip { get; init; }

        /// <summary>Card data; set when <see cref="Kind"/> is <see cref="ItemKind.Card"/>.</summary>
        public ItemCardDef? Card { get; init; }
    }

    /// <summary>Consumable block of an <see cref="ItemDef"/>. Used on the user.</summary>
    public sealed record ItemConsumableDef
    {
        /// <summary>Effects applied to the user.</summary>
        public IReadOnlyList<EffectDef> Effects { get; init; } = Empty.List<EffectDef>();

        /// <summary>Cooldown in milliseconds before this item can be used again.</summary>
        public int CooldownMs { get; init; }

        /// <summary>True (default) to remove one unit on use.</summary>
        public bool Consumed { get; init; } = true;
    }

    /// <summary>Equipment block of an <see cref="ItemDef"/>.</summary>
    public sealed record ItemEquipDef
    {
        /// <summary>
        /// Slots the item occupies when equipped (<see cref="EquipSlots"/>), e.g. <c>[weapon, shield]</c>
        /// for a two-handed weapon.
        /// </summary>
        public IReadOnlyList<string> Slots { get; init; } = Empty.List<string>();

        /// <summary>Weapon type (<see cref="WeaponTypes"/>); null for non-weapons.</summary>
        public string? WeaponType { get; init; }

        /// <summary>Weapon attack; the formula variable <c>weapon_atk</c>.</summary>
        public int WeaponAtk { get; init; }

        /// <summary>Auto-attack range in cells for weapons. Default 1 (melee).</summary>
        public int AttackRange { get; init; } = 1;

        /// <summary>Attack element of a weapon (<see cref="Elements"/>); null means neutral.</summary>
        public string? Element { get; init; }

        /// <summary>Minimum base level to equip. 0 means none.</summary>
        public int MinBaseLevel { get; init; }

        /// <summary>Jobs that may equip it (<c>job.*</c>). Empty means all.</summary>
        public IReadOnlyList<string> JobIds { get; init; } = Empty.List<string>();

        /// <summary>Number of card sockets.</summary>
        public int CardSlots { get; init; }

        /// <summary>True if the item can be refined (formula variable <c>refine</c>).</summary>
        public bool Refinable { get; init; }

        /// <summary>Modifiers while equipped; values may use <c>refine</c>.</summary>
        public IReadOnlyList<ModifierDef> Modifiers { get; init; } = Empty.List<ModifierDef>();

        /// <summary>
        /// Worn visual (<c>model.*</c>). The manifest entry says whether it is skinned to the rig
        /// or attached to an attach point (ARCHITECTURE §7.5). Null means no visible change.
        /// </summary>
        public string? VisualAssetKey { get; init; }
    }

    /// <summary>Card block of an <see cref="ItemDef"/>.</summary>
    public sealed record ItemCardDef
    {
        /// <summary>Equipment slots whose items may hold this card (<see cref="EquipSlots"/>).</summary>
        public IReadOnlyList<string> Slots { get; init; } = Empty.List<string>();

        /// <summary>Modifiers while socketed in equipped gear.</summary>
        public IReadOnlyList<ModifierDef> Modifiers { get; init; } = Empty.List<ModifierDef>();
    }
}
