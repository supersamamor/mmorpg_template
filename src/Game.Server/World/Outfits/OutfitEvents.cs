using Game.Server.Events;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Server.World.Outfits;

/// <summary>
/// A player asks (T-708) what shows in <c>Slot</c>: <c>Outfit</c>, or nothing when it is null. Refused with <see cref="OutfitRefused"/>. Looks only, so a dead character may ask too, and
/// so may one that is fighting.
/// </summary>
public sealed record SetOutfit(EntityId Entity, string Slot, string? Outfit) : IIntent, IDeadEntityIntent;

/// <summary>A player turns the switch that hides every outfit (T-708); the choices stay.</summary>
public sealed record HideOutfits(EntityId Entity, bool Hidden) : IIntent, IDeadEntityIntent;

/// <summary>
/// The server gives a player in the world an outfit (T-708). Never sent by a client: it is for whatever grants outfits outside a quest, such as T-709's store. Granting what is owned
/// does nothing, so it may be repeated; like every intent it is dropped when the player is between maps or has left, so a caller that must not lose a grant keeps it owed until it
/// has seen it saved.
/// </summary>
public sealed record GrantOutfit(EntityId Entity, string Outfit) : IIntent, IDeadEntityIntent;

/// <summary><c>OutfitGranted</c> (T-708): <c>Player</c> owns <c>Outfit</c> now and did not before.</summary>
public sealed record OutfitGranted(EntityId Player, string Outfit) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.OutfitGranted;
}

/// <summary><c>WardrobeChanged</c> (T-708): <c>Player</c> chose what shows in a slot, or turned the hide switch.</summary>
public sealed record WardrobeChanged(EntityId Player) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.WardrobeChanged;
}

/// <summary><c>OutfitRefused</c> (T-708): the <c>set_outfit</c> of <c>Player</c> was refused with an "Outfit reasons" term; nothing changed.</summary>
public sealed record OutfitRefused(EntityId Player, string Slot, string? Outfit, string Reason) : IGameEvent
{
    /// <inheritdoc />
    public string Name => EventNames.OutfitRefused;
}

/// <summary>
/// The outfits others see on a player, on <see cref="Entity.Outfit"/> (set by <see cref="OutfitSystem"/>, read by the AOI). Display data only. Immutable; a new object means a change.
/// </summary>
public sealed record OutfitLook(IReadOnlyList<ShownOutfit> Shown)
{
    /// <summary>A player that shows none.</summary>
    public static readonly OutfitLook None = new([]);

    /// <inheritdoc />
    public bool Equals(OutfitLook? other) => other is not null && Shown.SequenceEqual(other.Shown);

    /// <inheritdoc />
    public override int GetHashCode() => Shown.Count;
}
