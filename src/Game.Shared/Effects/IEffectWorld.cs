using System.Collections.Generic;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Stats;
using Game.Shared.World;

namespace Game.Shared.Effects
{
    /// <summary>
    /// What the <see cref="EffectRunner"/> knows of one entity: the numbers its formulas and the resolver read, and where it stands. A plain copy;
    /// <see cref="Stats"/> is the entity's live sheet. Statuses keep the copy of their applier as their source, so a caster that has left still
    /// has numbers for the ticks.
    /// </summary>
    public readonly struct EffectActor
    {
        /// <summary>Creates an actor.</summary>
        public EffectActor(long id, StatSheet stats, GridCell cell, string facing, bool dead, bool player, string? attackElement = null,
            string? defenseElement = null, string? race = null, string? size = null, int weaponAtk = 0, int refine = 0)
        {
            Id = id;
            Stats = stats;
            Cell = cell;
            Facing = facing;
            Dead = dead;
            Player = player;
            AttackElement = attackElement;
            DefenseElement = defenseElement;
            Race = race;
            Size = size;
            WeaponAtk = weaponAtk;
            Refine = refine;
        }

        /// <summary>The entity id (the server's <c>EntityId</c> value).</summary>
        public long Id { get; }

        /// <summary>Its stat sheet (caster variables, or <c>t_</c> target variables).</summary>
        public StatSheet Stats { get; }

        /// <summary>Where it stands.</summary>
        public GridCell Cell { get; }

        /// <summary>Its facing (a <c>Directions</c> term): the push direction of a knockback from its own cell.</summary>
        public string Facing { get; }

        /// <summary>Dead: entity effects skip it.</summary>
        public bool Dead { get; }

        /// <summary>A player (the side of the friendly / hostile filter: players and monsters are enemies in v1).</summary>
        public bool Player { get; }

        /// <summary>Its own attack element (weapon or monster element), null for neutral.</summary>
        public string? AttackElement { get; }

        /// <summary>Its defending element (monsters), null for neutral.</summary>
        public string? DefenseElement { get; }

        /// <summary>Its race (monsters), for damage modifiers.</summary>
        public string? Race { get; }

        /// <summary>Its size (monsters), for damage modifiers.</summary>
        public string? Size { get; }

        /// <summary>The formula variable <c>weapon_atk</c>.</summary>
        public int WeaponAtk { get; }

        /// <summary>The formula variable <c>refine</c>.</summary>
        public int Refine { get; }

        /// <summary>True for a filled actor (a default one has no sheet).</summary>
        public bool IsSet => Stats != null;
    }

    /// <summary>
    /// The world an <see cref="EffectRunner"/> acts on (the server's map instance, or a tool's duel). The runner decides every number; the world
    /// applies the outcome and raises its events (hard rule 7: the effects never call other systems; the world publishes, they listen).
    /// </summary>
    public interface IEffectWorld
    {
        /// <summary>Simulated time now, in ms (the instance's tick clock).</summary>
        long NowMs { get; }

        /// <summary>The logic grid for knockback, or null where there is no geometry (a duel): knockback then does nothing.</summary>
        MapGrid? Grid { get; }

        /// <summary>The entity <paramref name="id"/> if it is in this world and can be affected (has stats).</summary>
        bool TryGetActor(long id, out EffectActor actor);

        /// <summary>The entity's statuses; created when <paramref name="create"/> and it has none. Null when it cannot have any.</summary>
        StatusSet? StatusesOf(long id, bool create);

        /// <summary>
        /// Fills <paramref name="into"/> (cleared first) with the living entities an <c>area</c> of <paramref name="radius"/> cells (Chebyshev)
        /// around <paramref name="center"/> affects for <paramref name="caster"/>: its side (<paramref name="friendly"/>, the caster included) or the
        /// other side, ordered by distance from the center, then entity id.
        /// </summary>
        void CollectArea(in EffectActor caster, GridCell center, int radius, bool friendly, List<long> into);

        /// <summary>A <c>damage</c> effect landed (or missed) on <paramref name="target"/>: change HP, raise <c>DamageDealt</c>, handle a death.</summary>
        void Damage(in EffectActor caster, long target, AttackResult result, bool fromStatus);

        /// <summary>A <c>heal</c> of <paramref name="amount"/> (≥ 0): restore HP up to max, raise <c>HpRestored</c>.</summary>
        void Heal(long caster, long target, int amount);

        /// <summary>A <c>restore_sp</c> of <paramref name="amount"/> (≥ 0): restore SP up to max, raise <c>SpRestored</c>.</summary>
        void RestoreSp(long caster, long target, int amount);

        /// <summary>A status started, stacked or was refreshed on <paramref name="target"/>: raise <c>StatusApplied</c>.</summary>
        void StatusStarted(long caster, long target, ActiveStatus status, StatusChange change);

        /// <summary>A status ended: <paramref name="reason"/> null when it ran out (<c>StatusExpired</c>), else a <c>StatusEndReasons</c> term.</summary>
        void StatusEnded(long target, ActiveStatus status, string? reason);

        /// <summary>A <c>knockback</c> moves <paramref name="target"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
        void Knockback(long caster, long target, GridCell from, GridCell to);

        /// <summary>A <c>teleport</c> of its caster.</summary>
        void Teleport(long caster, TeleportEffect effect);

        /// <summary>A <c>summon</c> near <paramref name="near"/>, owned by <paramref name="caster"/>.</summary>
        void Summon(long caster, SummonEffect effect, GridCell near);

        /// <summary>A <c>reset_stats</c> on <paramref name="target"/> (always the item's user): reset its allocated stats and refund the points.</summary>
        void ResetStats(long caster, long target);

        /// <summary>A <c>reset_skills</c> on <paramref name="target"/> (always the item's user): reset its learned skills and refund the points.</summary>
        void ResetSkills(long caster, long target);

        /// <summary>An <c>area</c> ran its children on <paramref name="targets"/> entities.</summary>
        void AreaApplied(long caster, GridCell center, int radius, int targets);
    }
}
