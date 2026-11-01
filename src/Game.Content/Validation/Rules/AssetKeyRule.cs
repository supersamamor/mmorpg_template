namespace Game.Content.Validation.Rules;

/// <summary>Every asset key used by content exists in the active theme's manifest. Only the key is checked, not the file it maps to.</summary>
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
    }
}
