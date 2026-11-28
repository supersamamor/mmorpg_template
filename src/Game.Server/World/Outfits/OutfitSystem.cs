using Game.Server.World.Combat;
using Game.Shared.Progression;
using Game.Shared.Wardrobe;

namespace Game.Server.World.Outfits;

/// <summary>
/// The wardrobe (T-708, ARCHITECTURE §7.5.2): what a player shows of the outfits it owns. Owns <see cref="SetOutfit"/>, <see cref="HideOutfits"/> and <see cref="GrantOutfit"/>. Every
/// check is <see cref="WardrobeRules"/>'; the state is the character's (<see cref="ProgressionState.Wardrobe"/>), so it travels and is saved with it.
/// </summary>
/// <remarks>
/// It sets <see cref="Entity.Outfit"/> for the AOI and nothing else. <b>It must stay that way:</b> no stat, effect, speed or rule may come from an outfit. A quest's outfit rewards are
/// granted where its other rewards are paid (combat, the progression host), through the same <see cref="WardrobeRules.Grant"/>.
/// </remarks>
public sealed class OutfitSystem(WardrobeRules rules, IPlayerProgressionStore progressions) : ITickSystem, Reload.IContentReloadable
{
    private WardrobeRules _rules = rules;

    /// <summary>T-801: the new outfits; a returning player's look is shown again from them.</summary>
    public void Reload(Reload.ServerContent next) => _rules = next.Outfits;

    private static readonly IReadOnlyCollection<Type> Owned = [typeof(SetOutfit), typeof(HideOutfits), typeof(GrantOutfit)];
    private MapInstance? _instance;

    /// <inheritdoc />
    public string Name => "outfits";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        if (!ReferenceEquals(_instance, context.Instance)) Setup(context);
        var intents = context.Intents;
        for (var i = 0; i < intents.Count; i++)
        {
            if (!_instance!.TryGetEntity(intents[i].Entity, out var player) || player.Kind != EntityKind.Player || !progressions.TryGet(player.Id, out var state)) continue;
            switch (intents[i])
            {
                case SetOutfit set: Set(context, player, state.Wardrobe, set); break;
                case HideOutfits hide:
                    if (state.Wardrobe.Hidden == hide.Hidden) break;
                    state.Wardrobe.Hidden = hide.Hidden;
                    Refresh(player, state.Wardrobe);
                    context.Publish(new WardrobeChanged(player.Id));
                    break;
                case GrantOutfit grant:
                    if (!WardrobeRules.Grant(state.Wardrobe, grant.Outfit)) break;
                    if (player.Save is { } ticket) ticket.SaveSoon = true;   // whoever granted it waits for it to land
                    context.Publish(new OutfitGranted(player.Id, grant.Outfit));
                    break;
            }
        }
    }

    private void Set(TickContext context, Entity player, WardrobeState wardrobe, SetOutfit set)
    {
        wardrobe.Chosen.TryGetValue(set.Slot, out var before);
        if (_rules.Choose(wardrobe, set.Slot, set.Outfit, player.Appearance?.Gender) is { } reason)
        {
            context.Publish(new OutfitRefused(player.Id, set.Slot, set.Outfit, reason));
            return;
        }
        if (string.Equals(before, set.Outfit, StringComparison.Ordinal)) return;   // what shows already
        Refresh(player, wardrobe);
        context.Publish(new WardrobeChanged(player.Id));
    }

    private void Setup(TickContext context)
    {
        _instance = context.Instance;
        // A character that enters (a join, a load, a warp): its look is set before the AOI first tells anyone about it.
        _instance.Events.Subscribe<EntityEnteredMap>(e =>
        {
            if (_instance.TryGetEntity(e.Entity, out var player) && player.Kind == EntityKind.Player && progressions.TryGet(player.Id, out var state)) Refresh(player, state.Wardrobe);
        });
    }

    /// <summary>Works out what others see; the object stays the same while it is the same, so the AOI sends a change once.</summary>
    private void Refresh(Entity player, WardrobeState wardrobe)
    {
        var shown = _rules.Shown(wardrobe, player.Appearance?.Gender);
        if (shown.Count == 0)
        {
            if (player.Outfit is { Shown.Count: > 0 }) player.Outfit = OutfitLook.None;
            return;
        }
        var look = new OutfitLook(shown);
        if (!look.Equals(player.Outfit)) player.Outfit = look;
    }
}
