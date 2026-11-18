using System;
using System.Collections.Generic;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Random;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Shared.Effects
{
    /// <summary>
    /// The one effect runner (ARCHITECTURE §6.3): runs compiled effect lists for a caster, a primary target (an entity, a ground cell, or both)
    /// and a skill level, and drives statuses (apply, stack, tick, expire, remove). Every number comes from the effects' formulas and
    /// <c>formulas.yaml</c> through <see cref="CombatResolver"/>; every roll from the injected <see cref="IRandom"/>; every time from
    /// <see cref="IEffectWorld.NowMs"/>. So it is deterministic for a given seed. It decides; the <see cref="IEffectWorld"/> applies and raises the
    /// events. One runner per simulation thread (it reuses its contexts and buffers): not thread-safe.
    /// </summary>
    /// <remarks>
    /// <para><b>Order.</b> Effects run in list order on the primary target; an effect on a dead or vanished target is skipped, so a killing
    /// <c>damage</c> ends the list for that target. <c>area</c> collects its targets first (<see cref="IEffectWorld.CollectArea"/>: by distance
    /// from its center, then entity id) and runs all its children on one target before the next. Its center is the ground cell, else the
    /// target's cell, else the caster's.</para>
    /// <para><b>Per type.</b> <c>damage</c>: the resolver with the effect's multiplier, element and the skill level (a status tick always lands,
    /// <see cref="AttackContext.AlwaysHits"/>). <c>heal</c> / <c>restore_sp</c>: the amount formula, floored, never below 0. <c>apply_status</c>:
    /// the <c>chance</c> formula (a roll is drawn only when it is strictly between 0 and 1: the status applies when roll &lt; chance), then the
    /// <c>duration_ms</c> formula (0 or less applies nothing); stacking per <see cref="StatusSet"/>. <c>remove_status</c>: every active status
    /// whose id is listed or that carries a listed tag. <c>modify_stat</c>: only in a status's <c>on_apply</c>, where it adds its modifier with the
    /// source <c>status:&lt;id&gt;</c> until the status ends. <c>knockback</c>: pushes the target away from the caster (or the area's center) along
    /// the nearest of the 8 directions, one cell at a time, stopping before a cell that is not walkable or a diagonal that would cut a corner.
    /// <c>teleport</c> / <c>summon</c>: handed to the world. Formulas see the caster as caster variables, the target as <c>t_</c> variables and
    /// <c>level</c>.</para>
    /// <para><b>Nesting.</b> A status's <c>on_apply</c> may apply other statuses; past <see cref="MaxDepth"/> nested applications the runner
    /// stops going deeper (content validation already refuses a status that applies itself).</para>
    /// </remarks>
    public sealed class EffectRunner
    {
        /// <summary>How deep status applications may nest (a status whose <c>on_apply</c> applies a status, and so on).</summary>
        public const int MaxDepth = 4;

        private const double FloorTolerance = 1e-9;
        private static readonly int WeaponAtkSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.WeaponAtk);
        private static readonly int RefineSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.Refine);
        private static readonly int LevelSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.SkillLevel);

        private readonly EffectLibrary _library;
        private readonly IEffectWorld _world;
        private readonly IRandom _random;
        private readonly CombatResolver _resolver;
        private readonly AttackContext _attack = new AttackContext();
        private readonly FormulaContext _formula = new FormulaContext(FormulaSymbols.Default);
        private readonly Stack<List<long>> _areaPool = new Stack<List<long>>();
        private readonly Stack<List<ActiveStatus>> _statusPool = new Stack<List<ActiveStatus>>();

        /// <summary>A runner over <paramref name="world"/>, drawing from <paramref name="random"/>.</summary>
        public EffectRunner(CombatRules rules, EffectLibrary library, IEffectWorld world, IRandom random)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            _library = library ?? throw new ArgumentNullException(nameof(library));
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _resolver = new CombatResolver(rules, random);
        }

        /// <summary>The compiled content it runs.</summary>
        public EffectLibrary Library => _library;

        /// <summary>
        /// Runs <paramref name="effects"/> for <paramref name="caster"/> on <paramref name="target"/> (an entity id, or 0 for none) and/or the ground
        /// cell <paramref name="cell"/>, at skill level <paramref name="level"/>.
        /// </summary>
        public void Run(IReadOnlyList<CompiledEffect> effects, in EffectActor caster, long target, GridCell? cell, int level)
        {
            if (effects == null) throw new ArgumentNullException(nameof(effects));
            if (!caster.IsSet) throw new ArgumentException("The caster has no stats.", nameof(caster));
            var origin = cell ?? caster.Cell;
            for (var i = 0; i < effects.Count; i++) RunOne(effects[i], caster, target, cell, origin, level, false, 0);
        }

        /// <summary>
        /// One status step for one bearer at <see cref="IEffectWorld.NowMs"/>: every <c>on_tick</c> that is due (at or before its end; once per
        /// stack, its applier as the caster), then expiry of every status whose end has come. Call it once per tick for each entity with statuses.
        /// </summary>
        public void TickStatuses(long bearer)
        {
            var set = _world.StatusesOf(bearer, false);
            if (set == null || set.IsEmpty) return;
            var now = _world.NowMs;
            var list = _statusPool.Count > 0 ? _statusPool.Pop() : new List<ActiveStatus>();
            set.CopyTo(list);
            for (var i = 0; i < list.Count; i++)
            {
                var active = list[i];
                var interval = active.Status.Def.TickIntervalMs;
                while (active.NextTickMs <= now && active.NextTickMs <= active.EndsAtMs && set.Contains(active))
                {
                    for (var s = 0; s < active.Stacks; s++)
                    {
                        var ticks = active.Status.OnTick;
                        for (var e = 0; e < ticks.Count; e++)
                            RunOne(ticks[e], active.Source, bearer, null, active.Source.Cell, active.Level, true, 1);
                    }
                    active.NextTickMs += interval;
                    if (!_world.TryGetActor(bearer, out var self) || self.Dead) break;   // died of it: the world ended its statuses
                }
                if (set.Contains(active) && now >= active.EndsAtMs) End(bearer, set, active, null);
            }
            list.Clear();
            _statusPool.Push(list);
        }

        /// <summary>Ends every status of <paramref name="bearer"/> for a <see cref="StatusEndReasons"/> term (death, a warp to another map).</summary>
        public void EndAll(long bearer, string reason)
        {
            var set = _world.StatusesOf(bearer, false);
            if (set == null || set.IsEmpty) return;
            var list = _statusPool.Count > 0 ? _statusPool.Pop() : new List<ActiveStatus>();
            set.CopyTo(list);
            for (var i = 0; i < list.Count; i++) End(bearer, set, list[i], reason);
            list.Clear();
            _statusPool.Push(list);
        }

        private void RunOne(CompiledEffect effect, in EffectActor caster, long targetId, GridCell? cell, GridCell origin, int level, bool tick, int depth)
        {
            switch (effect)
            {
                case CompiledArea area:
                    RunArea(area, caster, targetId, cell, level, tick, depth);
                    return;
                case CompiledPlain plain when plain.Def is TeleportEffect teleport:
                    _world.Teleport(caster.Id, teleport);
                    return;
                case CompiledPlain plain when plain.Def is SummonEffect summon:
                    _world.Summon(caster.Id, summon, cell ?? (TryTarget(targetId, out var near) ? near.Cell : caster.Cell));
                    return;
            }

            if (!TryTarget(targetId, out var target)) return;   // entity effects need a living target
            switch (effect)
            {
                case CompiledDamage damage:
                    Damage(caster, target, damage, level, tick);
                    break;
                case CompiledAmount amount when amount.Def is HealEffect:
                    Fill(caster, target, level);
                    _world.Heal(caster.Id, target.Id, Amount(amount.Amount.Evaluate(_formula)));
                    break;
                case CompiledAmount amount:
                    Fill(caster, target, level);
                    _world.RestoreSp(caster.Id, target.Id, Amount(amount.Amount.Evaluate(_formula)));
                    break;
                case CompiledApplyStatus apply:
                    Apply(apply, caster, target, level, depth);
                    break;
                case CompiledPlain plain when plain.Def is RemoveStatusEffect remove:
                    Remove(target.Id, remove);
                    break;
                case CompiledPlain plain when plain.Def is KnockbackEffect knockback:
                    Knockback(caster, target, origin, knockback.Cells);
                    break;
                // modify_stat outside a status's on_apply is refused at load (EffectSupport); nothing to do here.
            }
        }

        private bool TryTarget(long id, out EffectActor target)
        {
            if (id != 0 && _world.TryGetActor(id, out target) && !target.Dead) return true;
            target = default;
            return false;
        }

        private void RunArea(CompiledArea area, in EffectActor caster, long targetId, GridCell? cell, int level, bool tick, int depth)
        {
            var center = cell ?? (_world.TryGetActor(targetId, out var t) ? t.Cell : caster.Cell);
            var targets = _areaPool.Count > 0 ? _areaPool.Pop() : new List<long>();
            _world.CollectArea(caster, center, Math.Max(0, area.Radius), area.Friendly, targets);
            for (var i = 0; i < targets.Count; i++)
                for (var c = 0; c < area.Children.Count; c++)
                    RunOne(area.Children[c], caster, targets[i], null, center, level, tick, depth);
            _world.AreaApplied(caster.Id, center, area.Radius, targets.Count);
            targets.Clear();
            _areaPool.Push(targets);
        }

        private void Damage(in EffectActor caster, in EffectActor target, CompiledDamage damage, int level, bool tick)
        {
            var a = _attack;
            a.Attacker = caster.Stats;
            a.Defender = target.Stats;
            a.WeaponAtk = caster.WeaponAtk;
            a.Refine = caster.Refine;
            a.AttackerElement = caster.AttackElement;
            a.Effect = damage.Damage;
            a.SkillLevel = level;
            a.DefenderElement = target.DefenseElement;
            a.DefenderRace = target.Race;
            a.DefenderSize = target.Size;
            a.AlwaysHits = tick;
            var result = _resolver.Resolve(a);
            _world.Damage(caster, target.Id, result, tick);
        }

        private void Apply(CompiledApplyStatus apply, in EffectActor caster, in EffectActor target, int level, int depth)
        {
            if (depth >= MaxDepth || !_library.TryGetStatus(apply.StatusId, out var status)) return;
            Fill(caster, target, level);
            var chance = apply.Chance.Evaluate(_formula);
            if (!(chance > 0)) return;
            if (chance < 1 && _random.NextDouble() >= chance) return;
            var duration = Math.Floor(apply.DurationMs.Evaluate(_formula) + FloorTolerance);
            if (!(duration >= 1)) return;
            var set = _world.StatusesOf(target.Id, true);
            if (set == null) return;
            var active = set.Apply(status, caster, level, _world.NowMs, (long)Math.Min(duration, long.MaxValue / 4), out var change);
            _world.StatusStarted(caster.Id, target.Id, active, change);
            if (change == StatusChange.Refreshed) return;
            var onApply = status.OnApply;
            for (var i = 0; i < onApply.Count; i++)
            {
                if (onApply[i] is CompiledModifyStat modify)
                {
                    Fill(caster, target, level);
                    target.Stats.Modifiers.Add(status.ModifierSource, modify.Modifier, _formula);
                }
                else
                {
                    RunOne(onApply[i], caster, target.Id, null, caster.Cell, level, false, depth + 1);
                }
            }
        }

        private void Remove(long bearer, RemoveStatusEffect remove)
        {
            var set = _world.StatusesOf(bearer, false);
            if (set == null || set.IsEmpty) return;
            var list = _statusPool.Count > 0 ? _statusPool.Pop() : new List<ActiveStatus>();
            set.CopyTo(list);
            for (var i = 0; i < list.Count; i++)
                if (Matches(list[i].Status.Def, remove)) End(bearer, set, list[i], StatusEndReasons.Removed);
            list.Clear();
            _statusPool.Push(list);
        }

        private static bool Matches(StatusDef status, RemoveStatusEffect remove)
        {
            for (var i = 0; i < remove.StatusIds.Count; i++)
                if (string.Equals(remove.StatusIds[i], status.Id, StringComparison.Ordinal)) return true;
            for (var i = 0; i < remove.Tags.Count; i++)
                for (var j = 0; j < status.Tags.Count; j++)
                    if (string.Equals(remove.Tags[i], status.Tags[j], StringComparison.Ordinal)) return true;
            return false;
        }

        private void End(long bearer, StatusSet set, ActiveStatus active, string? reason)
        {
            if (!set.Remove(active)) return;
            if (_world.TryGetActor(bearer, out var self)) self.Stats.Modifiers.RemoveBySource(active.Status.ModifierSource);
            _world.StatusEnded(bearer, active, reason);
        }

        private void Knockback(in EffectActor caster, in EffectActor target, GridCell origin, int cells)
        {
            var grid = _world.Grid;
            if (grid == null || cells <= 0) return;
            var to = KnockbackPath.Destination(grid, origin, target.Cell, caster.Facing, cells);
            if (to.X != target.Cell.X || to.Y != target.Cell.Y) _world.Knockback(caster.Id, target.Id, target.Cell, to);
        }

        private void Fill(in EffectActor caster, in EffectActor target, int level)
        {
            var f = _formula;
            f.Clear();
            caster.Stats.WriteTo(f, asTarget: false);
            if (target.IsSet) target.Stats.WriteTo(f, asTarget: true);
            f[WeaponAtkSlot] = caster.WeaponAtk;
            f[RefineSlot] = caster.Refine;
            f[LevelSlot] = level;
        }

        private static int Amount(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 0;
            return (int)Math.Min(int.MaxValue, Math.Floor(value + FloorTolerance));
        }
    }

    /// <summary>Where a knockback ends (pure grid geometry, ARCHITECTURE §4).</summary>
    public static class KnockbackPath
    {
        /// <summary>
        /// The cell <paramref name="target"/> is pushed to, up to <paramref name="cells"/> steps away from <paramref name="origin"/> along the nearest
        /// of the 8 directions (<paramref name="facing"/> when both cells are the same). Each step needs a walkable cell, and a diagonal step also
        /// both orthogonal neighbours (no corner cutting, as in pathfinding); the push stops before the first step that fails.
        /// </summary>
        public static GridCell Destination(MapGrid grid, GridCell origin, GridCell target, string facing, int cells)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            var direction = GridDirection.Facing(origin, target) ?? facing;
            if (!GridDirection.TryGetStep(direction, out var dx, out var dy)) return target;
            var at = target;
            for (var i = 0; i < cells; i++)
            {
                var nx = at.X + dx;
                var ny = at.Y + dy;
                if (!grid.IsWalkable(nx, ny)) break;
                if (dx != 0 && dy != 0 && (!grid.IsWalkable(at.X + dx, at.Y) || !grid.IsWalkable(at.X, at.Y + dy))) break;
                at = new GridCell(nx, ny);
            }
            return at;
        }
    }
}
