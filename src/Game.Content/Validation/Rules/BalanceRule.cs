using Game.Shared.Formulas;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Sanity checks on <c>balance.yaml</c>: ranges are ordered and non-negative, chances stay within 0–1,
/// the primary stat range holds its starting value, and the base EXP table exists.
/// Formulas are checked by <see cref="FormulaRule"/>.
/// </summary>
public sealed class BalanceRule : IValidationRule
{
    private const string File = "balance.yaml";

    /// <inheritdoc />
    public string Name => "balance";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var balance = context.Database.Balance;
        var p = balance.Progression;
        var c = balance.Combat;
        var m = balance.Movement;

        if (!context.Database.ExpTables.Contains(p.BaseExpTableId))
            context.ErrorInFile(File, "progression.base_exp_table_id", ValidationCodes.RefMissing,
                $"balance.yaml refers to unknown exp table '{p.BaseExpTableId}'.");

        if (p.StartingStatPoints < 0)
            Range(context, "progression.starting_stat_points", "must not be negative.");
        if (p.StartingStatValue < 1)
            Range(context, "progression.starting_stat_value", "must be at least 1.");
        if (p.MaxStatValue < p.StartingStatValue)
            Range(context, "progression.max_stat_value", $"must not be below starting_stat_value ({p.StartingStatValue}).");

        if (c.MinDamage < 0)
            Range(context, "combat.min_damage", "must not be negative.");
        if (c.MaxDamage < c.MinDamage)
            Range(context, "combat.max_damage", $"must not be below min_damage ({c.MinDamage}).");

        Chances(context, "hit", c.MinHitChance, c.MaxHitChance);
        Chances(context, "crit", c.MinCritChance, c.MaxCritChance);

        // Only a constant can be range-checked here; FormulaRule reports parse errors.
        if (Formula.Parse(c.CritMultiplier).Formula is { } crit && crit.TryGetConstant(out var multiplier) && multiplier < 1)
            Range(context, "combat.crit_multiplier", "must be at least 1, so a critical hit never deals less than a normal one.");

        if (m.MaxPathLength <= 0)
            Range(context, "movement.max_path_length", "must be above 0.");
        if (m.MaxNodesExpanded <= 0)
            Range(context, "movement.max_nodes_expanded", "must be above 0.");
    }

    private static void Chances(ValidationContext context, string kind, double min, double max)
    {
        if (!(min >= 0 && min <= 1))
            Range(context, $"combat.min_{kind}_chance", "must be between 0 and 1.");
        if (!(max >= 0 && max <= 1))
            Range(context, $"combat.max_{kind}_chance", "must be between 0 and 1.");
        else if (max < min)
            Range(context, $"combat.max_{kind}_chance", $"must not be below min_{kind}_chance ({min}).");
    }

    private static void Range(ValidationContext context, string path, string message) =>
        context.ErrorInFile(File, path, ValidationCodes.BalanceRange, $"balance.yaml {path} {message}");
}
