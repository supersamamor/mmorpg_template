using Game.Client.Core.Bundle;
using Game.Client.Core.World;
using Game.Shared.Protocol;

namespace Game.Client.Core.Ui;

/// <summary>
/// The long-press info panel's words. They come from the display bundle and the client's own text only (ARCHITECTURE §5.4): no stats, drop rates or
/// AI, which the client does not have.
/// </summary>
public static class EntityInfo
{
    /// <summary>The panel's title: the entity's name, with a stack count for a pile on the ground.</summary>
    public static string Title(EntityView entity, DisplayBundle bundle)
    {
        var name = bundle.NameOf(entity.DisplayKey);
        if (entity.Kind == EntityKinds.Item && entity.Quantity > 1) return name + " x" + entity.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return name;
    }

    /// <summary>The panel's second line: what kind of thing it is, and whether it is dead.</summary>
    public static string Subtitle(EntityView entity) => UiText.EntityKind(entity.Kind, entity.Dead);
}
