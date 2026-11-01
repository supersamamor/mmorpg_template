using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Vocabulary;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Sanity checks on <c>balance.yaml</c>: ranges are ordered and non-negative, chances stay within 0–1,
/// the primary stat range holds its starting value, the base EXP table exists, and a new character starts on a walkable
/// cell of an existing map in an existing job.
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

        if (balance.Aoi.BucketSize <= 0)
            Range(context, "aoi.bucket_size", "must be above 0.");
        if (balance.Aoi.ViewRadius <= 0)
            Range(context, "aoi.view_radius", "must be above 0.");

        NewCharacter(context, balance.NewCharacter);
    }

    /// <summary>The start map and job exist, the start cell is walkable on the map's grid, and the facing is a direction.</summary>
    private static void NewCharacter(ValidationContext context, NewCharacterBalanceDef n)
    {
        var db = context.Database;
        if (!db.Jobs.Contains(n.StartJobId))
            context.ErrorInFile(File, "new_character.start_job_id", ValidationCodes.RefMissing,
                $"balance.yaml new_character refers to unknown job '{n.StartJobId}'.");
        if (!Directions.All.Contains(n.StartFacing))
            context.ErrorInFile(File, "new_character.start_facing", ValidationCodes.VocabUnknown,
                $"balance.yaml new_character.start_facing '{n.StartFacing}' is not a direction ({string.Join(", ", Directions.All.Ordered)}).");
        if (!db.Maps.Contains(n.StartMapId))
        {
            context.ErrorInFile(File, "new_character.start_map_id", ValidationCodes.RefMissing,
                $"balance.yaml new_character refers to unknown map '{n.StartMapId}'.");
            return;
        }
        if (!db.Grids.TryGetValue(n.StartMapId, out var grid)) return; // the loader reports a map without a grid
        if (!grid.InBounds(n.StartX, n.StartY))
            context.ErrorInFile(File, "new_character.start_x", ValidationCodes.GridCellBlocked,
                $"balance.yaml new_character start cell ({n.StartX}, {n.StartY}) is outside the {grid.Width}x{grid.Height} grid of {grid.MapId}.");
        else if (!grid.IsWalkable(n.StartX, n.StartY))
            context.ErrorInFile(File, "new_character.start_x", ValidationCodes.GridCellBlocked,
                $"balance.yaml new_character start cell ({n.StartX}, {n.StartY}) is not walkable on {grid.MapId}.");
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
