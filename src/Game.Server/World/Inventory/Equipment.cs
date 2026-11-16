using Game.Shared.Vocabulary;

namespace Game.Server.World.Inventory;

/// <summary>
/// What combat needs of a player's weapon, on <see cref="Entity.Weapon"/> (set by the inventory, read by combat for <c>AttackContext</c>).
/// Immutable: a new one replaces it when the weapon changes.
/// </summary>
/// <param name="ItemId">The weapon item.</param>
/// <param name="WeaponType">Its <see cref="WeaponTypes"/> term.</param>
/// <param name="WeaponAtk">The formula variable <c>weapon_atk</c>.</param>
/// <param name="Refine">The formula variable <c>refine</c> (0 until T-402).</param>
/// <param name="AttackRange">Auto-attack range in cells (Chebyshev), at least 1.</param>
/// <param name="Element">Its attack element (<see cref="Elements"/>), or null for neutral.</param>
public sealed record EquippedWeapon(string ItemId, string WeaponType, int WeaponAtk, int Refine, int AttackRange, string? Element);

/// <summary>
/// What other players see a player wear, on <see cref="Entity.Look"/> (set by the inventory, read by the AOI): the weapon type and the worn
/// item ids that have a worn visual, in <see cref="EquipSlots"/> order. Display data only, never stats. Immutable, compared by value.
/// </summary>
public sealed record WornLook(string WeaponType, IReadOnlyList<string> Worn)
{
    /// <summary>A player with nothing on.</summary>
    public static readonly WornLook Unarmed = new(WeaponTypes.Unarmed, []);

    /// <inheritdoc />
    public bool Equals(WornLook? other) =>
        other is not null && string.Equals(WeaponType, other.WeaponType, StringComparison.Ordinal) && Worn.SequenceEqual(other.Worn, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => WeaponType.GetHashCode(StringComparison.Ordinal) ^ Worn.Count;
}
