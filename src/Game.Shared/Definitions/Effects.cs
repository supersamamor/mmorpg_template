using Game.Shared.Formulas;
using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// One unit of gameplay behavior (ARCHITECTURE §6.3). Skills, consumables, cards and statuses are lists of effects.
    /// In content each effect is a map whose <c>type</c> key selects the concrete record; the
    /// discriminator → type table is <see cref="Discriminators.Effects"/>. <see cref="Type"/> is computed
    /// from the concrete record, so it can never disagree with the record's shape.
    /// </summary>
    public abstract record EffectDef
    {
        /// <summary>Effect type discriminator; one of <see cref="EffectTypes"/>.</summary>
        public abstract string Type { get; }
    }

    /// <summary>
    /// <c>damage</c>: physical or magic damage. The base amount comes from the physical or magic damage
    /// formula in <c>formulas.yaml</c>, scaled by <see cref="Multiplier"/>, then the element table applies.
    /// </summary>
    public sealed record DamageEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.Damage;

        /// <summary>True to use the magic damage formula; false (default) for physical.</summary>
        public bool Magic { get; init; }

        /// <summary>Formula scaling the base damage, e.g. <c>"1 + level * 0.3"</c>. Default <c>"1"</c>.</summary>
        [Formula]
        public string Multiplier { get; init; } = "1";

        /// <summary>Element id (<see cref="Elements"/>). Null uses the attacker's attack element.</summary>
        public string? Element { get; init; }

        /// <summary>Number of hits; the total is split visually only. Default 1.</summary>
        public int Hits { get; init; } = 1;
    }

    /// <summary><c>heal</c>: restore HP.</summary>
    public sealed record HealEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.Heal;

        /// <summary>HP restored, as a formula.</summary>
        [Formula]
        public string Amount { get; init; } = "0";
    }

    /// <summary><c>restore_sp</c>: restore SP.</summary>
    public sealed record RestoreSpEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.RestoreSp;

        /// <summary>SP restored, as a formula.</summary>
        [Formula]
        public string Amount { get; init; } = "0";
    }

    /// <summary><c>apply_status</c>: apply a status with a chance and a duration.</summary>
    public sealed record ApplyStatusEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.ApplyStatus;

        /// <summary>Status id (<c>status.*</c>).</summary>
        public string StatusId { get; init; } = string.Empty;

        /// <summary>Probability 0–1 as a formula. Default <c>"1"</c> (always). The caller rolls with injected RNG.</summary>
        [Formula]
        public string Chance { get; init; } = "1";

        /// <summary>Duration in milliseconds, as a formula.</summary>
        [Formula]
        public string DurationMs { get; init; } = "0";
    }

    /// <summary><c>remove_status</c>: cleanse statuses by id and/or tag.</summary>
    public sealed record RemoveStatusEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.RemoveStatus;

        /// <summary>Status ids to remove.</summary>
        public IReadOnlyList<string> StatusIds { get; init; } = Empty.List<string>();

        /// <summary>Remove every active status carrying any of these tags (<see cref="StatusDef.Tags"/>).</summary>
        public IReadOnlyList<string> Tags { get; init; } = Empty.List<string>();
    }

    /// <summary>
    /// <c>modify_stat</c>: a temporary stat modifier. Used in <see cref="StatusDef.OnApply"/>; it lasts
    /// as long as the status and is removed with it.
    /// </summary>
    public sealed record ModifyStatEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.ModifyStat;

        /// <summary>The modifier to apply.</summary>
        public ModifierDef Modifier { get; init; } = new ModifierDef();
    }

    /// <summary><c>knockback</c>: push the target away from the source.</summary>
    public sealed record KnockbackEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.Knockback;

        /// <summary>Distance in cells. Stops early at unwalkable cells.</summary>
        public int Cells { get; init; } = 1;
    }

    /// <summary>
    /// <c>teleport</c>: move the caster. With <see cref="ToSavePoint"/> it goes to the save point;
    /// otherwise to <see cref="MapId"/> (or the current map) at <see cref="X"/>/<see cref="Y"/>
    /// (or a random walkable cell when they are omitted).
    /// </summary>
    public sealed record TeleportEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.Teleport;

        /// <summary>Go to the character's save point; other fields are ignored.</summary>
        public bool ToSavePoint { get; init; }

        /// <summary>Destination map id (<c>map.*</c>). Null means the current map.</summary>
        public string? MapId { get; init; }

        /// <summary>Destination cell X. Null (with <see cref="Y"/>) means a random walkable cell.</summary>
        public int? X { get; init; }

        /// <summary>Destination cell Y. Null (with <see cref="X"/>) means a random walkable cell.</summary>
        public int? Y { get; init; }
    }

    /// <summary><c>summon</c>: spawn monsters near the caster.</summary>
    public sealed record SummonEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.Summon;

        /// <summary>Monster id (<c>monster.*</c>).</summary>
        public string MonsterId { get; init; } = string.Empty;

        /// <summary>How many to spawn. Default 1.</summary>
        public int Count { get; init; } = 1;

        /// <summary>Lifetime in milliseconds; null means until killed.</summary>
        public int? DurationMs { get; init; }
    }

    /// <summary>
    /// <c>area</c>: apply child effects to every valid entity within <see cref="Radius"/> cells
    /// (Chebyshev, i.e. a square) of the effect's target.
    /// </summary>
    public sealed record AreaEffect : EffectDef
    {
        /// <inheritdoc />
        public override string Type => EffectTypes.Area;

        /// <summary>Radius in cells (Chebyshev). 0 is the target cell only.</summary>
        public int Radius { get; init; }

        /// <summary>True to affect allies (heals, buffs); false (default) to affect enemies.</summary>
        public bool Friendly { get; init; }

        /// <summary>Effects applied to each entity in the area.</summary>
        public IReadOnlyList<EffectDef> Effects { get; init; } = Empty.List<EffectDef>();
    }
}
