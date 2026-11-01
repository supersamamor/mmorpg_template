using System.Globalization;
using Game.Shared.Definitions;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Warns about a percent modifier whose constant value is −100 or less: the stat is multiplied by <c>1 + Σpercent</c>, so
/// the stat drops to zero or below. Formulas that are not plain numbers are not evaluated.
/// </summary>
public sealed class ModifierRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "modifier";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        foreach (var m in ContentWalker.Modifiers(context.Database))
        {
            if (m.Modifier.Op != ModifierOp.Percent) continue;
            if (!double.TryParse(m.Modifier.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value > -100) continue;
            context.Warn(m.Owner, m.Path + ".value", ValidationCodes.WarnModifierPercentFloor,
                $"percent modifier on '{m.Modifier.Stat}' is {m.Modifier.Value}: values of -100 or less set the stat to zero or below");
        }
    }
}
