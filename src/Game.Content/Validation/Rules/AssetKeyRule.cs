using Game.Content.Maps;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Every asset key used by content exists in the active theme's manifest, and so does every map's minimap picture (<see cref="MinimapKeys"/>, T-812: the key follows from the map's
/// id, and the map export draws the picture). Only the key is checked, not the file it maps to.
/// </summary>
public sealed class AssetKeyRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "asset-keys";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        foreach (var a in ContentWalker.Assets(context.Database))
        {
            if (string.IsNullOrEmpty(a.Key))
            {
                if (a.Required)
                    context.Error(a.Owner, a.Path, ValidationCodes.AssetEmpty, $"{a.Owner} must set '{a.Path}'");
                continue;
            }
            if (!context.AssetKeys.Contains(a.Key!))
                context.Error(a.Owner, a.Path, ValidationCodes.AssetMissing,
                    $"Asset key '{a.Key}' is not in the theme's asset-manifest.json");
        }
        foreach (var map in context.Database.Maps.All)
        {
            var key = MinimapKeys.For(map.Id);
            if (!context.AssetKeys.Contains(key))
                context.Error(map.Id, "minimap", ValidationCodes.AssetMissing,
                    $"The minimap picture's asset key '{key}' is not in the theme's asset-manifest.json (the map export writes maps/<name>.minimap.png; add the key beside the map's scene key)");
        }
    }
}
