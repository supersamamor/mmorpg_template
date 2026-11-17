using System.Globalization;
using Game.Content.Loading;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Checks <c>refine.yaml</c> (ARCHITECTURE §6.8): <c>max_level</c> is 0 to <see cref="MaxLevelCap"/> and there is exactly one step per level, in order;
/// each step has a success chance of 1–100 percent, a material quantity of at least 1, a <c>failure</c> that is a "Refine failures" term, and a
/// material that exists (a missing id is <c>ref.missing</c>) and is a <c>material</c> item.
/// </summary>
public sealed class RefineRule : IValidationRule
{
    /// <summary>The highest <c>max_level</c> the rule accepts (a sanity bound, not a design limit).</summary>
    public const int MaxLevelCap = 100;

    private const string File = ContentLayout.RefineFile;

    /// <inheritdoc />
    public string Name => "refine";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var refine = context.Database.Refine;
        if (refine.MaxLevel < 0 || refine.MaxLevel > MaxLevelCap)
            Range(context, "max_level", $"must be from 0 to {MaxLevelCap}.");
        if (refine.Levels.Count != Math.Max(0, refine.MaxLevel))
            Range(context, "levels", $"must have one step per level from 1 to max_level ({refine.MaxLevel}), but has {refine.Levels.Count}.");

        for (var i = 0; i < refine.Levels.Count; i++)
        {
            var step = refine.Levels[i];
            var at = "levels[" + i.ToString(CultureInfo.InvariantCulture) + "]";
            if (step.Level != i + 1)
                Range(context, at + ".level", $"must be {i + 1}: the steps are levels 1, 2, 3, ... in order.");
            if (step.SuccessPercent < 1 || step.SuccessPercent > 100)
                Range(context, at + ".success_percent", "must be from 1 to 100.");
            if (step.MaterialQuantity < 1)
                Range(context, at + ".material_quantity", "must be at least 1.");
            if (!RefineFailures.All.Contains(step.Failure))
                Range(context, at + ".failure", $"must be one of {string.Join(", ", RefineFailures.All.Ordered)}.");
            if (!context.Database.Items.TryGet(step.MaterialItemId, out var material))
                context.ErrorInFile(File, at + ".material_item_id", ValidationCodes.RefMissing,
                    $"{File} refers to unknown item '{step.MaterialItemId}'.");
            else if (material.Kind != ItemKind.Material)
                Range(context, at + ".material_item_id", $"'{step.MaterialItemId}' must be a material item, not {material.Kind.ToString().ToLowerInvariant()}.");
        }
    }

    private static void Range(ValidationContext context, string path, string message) =>
        context.ErrorInFile(File, path, ValidationCodes.RefineRange, $"{File} {path} {message}");
}
