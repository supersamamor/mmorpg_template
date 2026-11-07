namespace Game.Content.Validation.Rules;

/// <summary>
/// The theme manifest's <c>fallbacks</c> table (ARCHITECTURE §8): every value names an asset key the manifest has. The client would only warn and go further down
/// the chain at run time; a typo here should stop the build instead. Skipped when the validator was given no manifest data.
/// </summary>
public sealed class ThemeFallbackRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "theme_fallback";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        if (context.Audio is not { } manifest) return;

        foreach (var p in manifest.FallbackProblems)
            context.ErrorInManifest(manifest, p.Token, p.Path, ValidationCodes.ThemeFallback, p.Message);

        foreach (var (name, key) in manifest.Fallbacks)
            if (!context.AssetKeys.Contains(key))
                context.ErrorInManifest(manifest, key, "fallbacks." + name, ValidationCodes.AssetMissing, $"Fallback model '{key}' is not in the theme's asset-manifest.json");
    }
}
