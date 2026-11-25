using Game.Shared.Vocabulary;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Outfits (T-708): every outfit has at least one piece, its pieces are <c>model.*</c> keys, each slot has one piece for everyone or at most one per gender, and no colour role is
/// listed twice. Terms are the vocabulary rule's, keys the asset-keys rule's, and that a piece can be worn at all <see cref="ThemeCheck"/>'s. That an outfit has nothing but looks is
/// the loader's and the schema's: they know no other field.
/// </summary>
public sealed class OutfitRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "outfits";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        foreach (var outfit in context.Database.Outfits.All)
        {
            void Error(string path, string message) => context.Error(outfit.Id, path, ValidationCodes.OutfitInvalid, $"{outfit.Id} {message}");

            if (string.IsNullOrWhiteSpace(outfit.Name)) Error("name", "needs a name: the wardrobe shows it");
            if (!string.IsNullOrEmpty(outfit.IconAssetKey) && !outfit.IconAssetKey.StartsWith(AssetKeyPrefixes.Icon, StringComparison.Ordinal))
                Error("icon_asset_key", $"icon '{outfit.IconAssetKey}' must be an '{AssetKeyPrefixes.Icon}' asset key");
            if (outfit.Pieces.Count == 0) Error("pieces", "has no pieces, so it covers no slot");
            if (outfit.TakesColors.Distinct(StringComparer.Ordinal).Count() != outfit.TakesColors.Count) Error("takes_colors", "lists a colour role twice");

            var pieces = new HashSet<(string Slot, string? Gender)>();
            for (var i = 0; i < outfit.Pieces.Count; i++)
            {
                var piece = outfit.Pieces[i];
                if (!string.IsNullOrEmpty(piece.ModelAssetKey) && !piece.ModelAssetKey.StartsWith(AssetKeyPrefixes.Model, StringComparison.Ordinal))
                    Error($"pieces[{i}].model_asset_key", $"piece '{piece.ModelAssetKey}' must be a '{AssetKeyPrefixes.Model}' asset key");
                var clash = piece.Gender is null ? pieces.Any(p => p.Slot == piece.Slot) : pieces.Contains((piece.Slot, piece.Gender)) || pieces.Contains((piece.Slot, null));
                if (clash) Error($"pieces[{i}].slot", $"has two pieces for '{piece.Slot}'{(piece.Gender is null ? "" : $" on a {piece.Gender} character")}: one for everyone, or at most one per gender");
                pieces.Add((piece.Slot, piece.Gender));
            }
        }
    }
}
