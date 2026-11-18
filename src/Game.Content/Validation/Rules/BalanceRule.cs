using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Vocabulary;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Sanity checks on <c>balance.yaml</c>: ranges are ordered and non-negative, chances stay within 0–1,
/// the primary stat range holds its starting value, AI timings are above 0, the combat loop's death and loot numbers are in range, the base EXP table exists, and a new character starts on a walkable
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

        if (p.Reset.FreeBelowBaseLevel < 0)
            Range(context, "progression.reset.free_below_base_level", "must not be negative.");

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
        if (balance.Aoi.MaxVisible <= 0)
            Range(context, "aoi.max_visible", "must be above 0.");
        else if (balance.Aoi.CapHysteresis < 0 || balance.Aoi.CapHysteresis >= balance.Aoi.MaxVisible)
            Range(context, "aoi.cap_hysteresis", $"must be from 0 to below max_visible ({balance.Aoi.MaxVisible}).");
        else
            CapShares(context, balance.Aoi);

        if (c.PlayerAttackRange < 1)
            Range(context, "combat.player_attack_range", "must be at least 1.");
        if (c.PlayerRepathIntervalMs <= 0)
            Range(context, "combat.player_repath_interval_ms", "must be above 0.");

        Ai(context, balance.Ai);
        Death(context, balance.Death);
        if (balance.Regen.OutOfCombatDelayMs < 0)
            Range(context, "regen.out_of_combat_delay_ms", "must not be negative.");
        Loot(context, balance.Loot);
        NewCharacter(context, balance.NewCharacter);
        Inventory(context, balance.Inventory);
        Dialogue(context, balance.Dialogue);
        StartItems(context, balance.NewCharacter);
    }

    /// <summary>The talking range is at least 1.</summary>
    private static void Dialogue(ValidationContext context, DialogueBalanceDef d)
    {
        if (d.TalkRange < 1)
            Range(context, "dialogue.talk_range", "must be at least 1.");
    }

    /// <summary>The inventory numbers are usable, and the two-handed list names weapon types.</summary>
    private static void Inventory(ValidationContext context, InventoryBalanceDef i)
    {
        if (i.MaxEntries < 1)
            Range(context, "inventory.max_entries", "must be at least 1.");
        if (i.BurdenedPercent < 1 || i.BurdenedPercent > 100)
            Range(context, "inventory.burdened_percent", "must be from 1 to 100.");
        else if (i.OverweightPercent < i.BurdenedPercent || i.OverweightPercent > 100)
            Range(context, "inventory.overweight_percent", $"must be from burdened_percent ({i.BurdenedPercent}) to 100.");
        if (i.PickupRange < 0)
            Range(context, "inventory.pickup_range", "must not be negative.");
        if (i.DroppedLifetimeMs <= 0)
            Range(context, "inventory.dropped_lifetime_ms", "must be above 0.");
        foreach (var type in i.TwoHandedWeaponTypes)
            if (!WeaponTypes.All.Contains(type))
                context.ErrorInFile(File, "inventory.two_handed_weapon_types", ValidationCodes.VocabUnknown,
                    $"balance.yaml inventory.two_handed_weapon_types '{type}' is not a weapon type ({string.Join(", ", WeaponTypes.All.Ordered)}).");
    }

    /// <summary>Every start item exists, has a quantity of at least 1, and only equipment is worn.</summary>
    private static void StartItems(ValidationContext context, NewCharacterBalanceDef n)
    {
        var db = context.Database;
        foreach (var start in n.StartItems)
        {
            if (!db.Items.TryGet(start.ItemId, out var item))
            {
                context.ErrorInFile(File, "new_character.start_items", ValidationCodes.RefMissing,
                    $"balance.yaml new_character.start_items refers to unknown item '{start.ItemId}'.");
                continue;
            }
            if (start.Quantity < 1)
                Range(context, "new_character.start_items", $"quantity of '{start.ItemId}' must be at least 1.");
            if (start.Equipped && item.Kind != ItemKind.Equip)
                Range(context, "new_character.start_items", $"'{start.ItemId}' is not equipment, so it cannot start equipped.");
        }
    }

    /// <summary>The crowd cap's shares (only with a valid cap): none negative, and they fit the cap with room for the observer itself.</summary>
    private static void CapShares(ValidationContext context, AoiBalanceDef a)
    {
        if (a.CapReservedMonsters < 0)
            Range(context, "aoi.cap_reserved_monsters", "must not be negative.");
        if (a.CapReservedNpcs < 0)
            Range(context, "aoi.cap_reserved_npcs", "must not be negative.");
        if (a.CapReservedItems < 0)
            Range(context, "aoi.cap_reserved_items", "must not be negative.");
        if (a.CapReservedMargin < 0)
            Range(context, "aoi.cap_reserved_margin", "must not be negative.");
        if (a.CapPinnedChasers < 0)
            Range(context, "aoi.cap_pinned_chasers", "must not be negative.");

        var cutoff = a.MaxVisible - a.CapHysteresis;
        if (a.CapReservedMonsters + a.CapReservedNpcs + a.CapReservedItems > cutoff - 1)
            Range(context, "aoi.cap_reserved_monsters", $"the three cap_reserved_* shares together must leave the observer's own slot below the cutoff: at most {cutoff - 1}.");
        if (a.CapPinnedChasers + a.CapReservedMargin > a.MaxVisible - 1)
            Range(context, "aoi.cap_pinned_chasers", $"with cap_reserved_margin, must stay below max_visible: at most {a.MaxVisible - 1}.");
    }

    /// <summary>The start map and job exist, the start cell is walkable on the map's grid, and the facing is a direction.</summary>
    private static void NewCharacter(ValidationContext context, NewCharacterBalanceDef n)
    {
        var db = context.Database;
        if (!db.Jobs.Contains(n.StartJobId))
            context.ErrorInFile(File, "new_character.start_job_id", ValidationCodes.RefMissing,
                $"balance.yaml new_character refers to unknown job '{n.StartJobId}'.");
        else if (!string.IsNullOrEmpty(db.Jobs.Get(n.StartJobId).ParentJobId))
            context.ErrorInFile(File, "new_character.start_job_id", ValidationCodes.BalanceRange,
                $"balance.yaml new_character.start_job_id '{n.StartJobId}' has a parent job; a new character starts as a root job.");
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

    /// <summary>Every AI timing and distance is above 0, and the wander pause range is ordered.</summary>
    private static void Ai(ValidationContext context, AiBalanceDef a)
    {
        if (a.ThinkIntervalMs <= 0)
            Range(context, "ai.think_interval_ms", "must be above 0.");
        if (a.WanderPauseMinMs <= 0)
            Range(context, "ai.wander_pause_min_ms", "must be above 0.");
        if (a.WanderPauseMaxMs <= 0)
            Range(context, "ai.wander_pause_max_ms", "must be above 0.");
        else if (a.WanderPauseMaxMs < a.WanderPauseMinMs)
            Range(context, "ai.wander_pause_max_ms", $"must not be below wander_pause_min_ms ({a.WanderPauseMinMs}).");
        if (a.WanderRadius <= 0)
            Range(context, "ai.wander_radius", "must be above 0.");
        if (a.RepathIntervalMs <= 0)
            Range(context, "ai.repath_interval_ms", "must be above 0.");
        if (a.ReturnTimeoutMs <= 0)
            Range(context, "ai.return_timeout_ms", "must be above 0.");
    }

    /// <summary>The respawn HP share is 1–100 percent, the EXP penalty 0–100 percent and the corpse time not negative.</summary>
    private static void Death(ValidationContext context, DeathBalanceDef d)
    {
        if (d.RespawnHpPercent < 1 || d.RespawnHpPercent > 100)
            Range(context, "death.respawn_hp_percent", "must be between 1 and 100.");
        if (d.ExpPenaltyPercent < 0 || d.ExpPenaltyPercent > 100)
            Range(context, "death.exp_penalty_percent", "must be between 0 and 100.");
        if (d.CorpseMs < 0)
            Range(context, "death.corpse_ms", "must not be negative.");
    }

    /// <summary>Loot priority is not negative, ground items live above 0 ms, and the spread radius is not negative.</summary>
    private static void Loot(ValidationContext context, LootBalanceDef l)
    {
        if (l.PriorityMs < 0)
            Range(context, "loot.priority_ms", "must not be negative.");
        if (l.LifetimeMs <= 0)
            Range(context, "loot.lifetime_ms", "must be above 0.");
        if (l.SpreadRadius < 0)
            Range(context, "loot.spread_radius", "must not be negative.");
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
