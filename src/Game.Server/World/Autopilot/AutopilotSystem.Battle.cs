using Game.Server.World.Combat;
using Game.Shared.Autopilot;
using Game.Shared.Progression;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Autopilot;

// Auto-battle (T-811, ARCHITECTURE §6.4.3): the autopilot's second mode. No quest: it fights the monsters around the cell it was started on (the anchor) until it ends, with the
// same skills, intents, searches and limits as a quest run's hunt. Combat and the inventory do the rest, as for any player.
public sealed partial class AutopilotSystem
{
    /// <summary>One tick of an auto-battle run, after the daily limit and the count-downs: the ends that need no thought (a full bag, nothing to fight, low HP), then a thought when due.</summary>
    private void StepBattle(AutopilotRun run, ProgressionState state)
    {
        if (!run.Running) return;   // it never pauses; a stun only waits (Battle)
        var battle = _content.Battle;
        if (run.BagFull)
        {
            Stop(run, AutopilotStops.BagFull);
            return;
        }
        if (battle.IdleStopMs > 0 && run.IdleMs >= battle.IdleStopMs)
        {
            Stop(run, AutopilotStops.NothingToFight);
            return;
        }
        if (LowHp(run))
        {
            // A supportive shortcut skill that still has a use (a heal) gets its chance first; when none has, the player takes over.
            if (run.Casting || TryCast(run, state, null)) return;
            Stop(run, AutopilotStops.LowHp);
            return;
        }
        if (!run.Wake && run.ThinkMs > 0) return;
        run.Wake = false;
        run.ThinkMs = _balance.DecideIntervalMs;
        Battle(run, state);
    }

    /// <summary>
    /// One thought of auto-battle, the first of these that applies: wait for a cast; go on with the fight while its target is still in the area and allowed; go on fetching an item;
    /// fetch one of its own drops lying in the area; help oneself (a supportive skill); fight back; the nearest allowed monster in the area by walking distance; or go back to the
    /// anchor and wait there.
    /// </summary>
    private void Battle(AutopilotRun run, ProgressionState state)
    {
        if (run.Player.CannotMove || run.Casting) return;   // a stun: every order would be refused
        var battle = run.Battle!;
        if (run.Target is { } current)
        {
            if (TryTarget(current, out var target) && InArea(battle, target.Cell, run.Retaliating ? _content.Battle.RetaliateExtraCells : 0)
                && MayFight(run, state, target, run.Retaliating))
            {
                Fight(run, state, target);
                return;
            }
            LetGo(run, target is not null && !target.Dead);
        }

        if (run.Picking is { } picking)
        {
            if (run.PickMs > 0 && _instance!.TryGetEntity(picking, out _)) return;
            run.Picking = null;
        }
        if (Loot(run)) return;
        if (run.Leg is null && TryCast(run, state, null)) return;

        if (run.Player.Load == WeightStates.Overweight)
        {
            Stop(run, AutopilotStops.BagFull);   // it cannot fight
            return;
        }
        if (BattleFightBack(run, state) is { } attacker)
        {
            run.Retaliating = true;
            Engage(run, state, attacker);
            return;
        }

        if (!string.Equals(battle.Filter, AutoBattleFilters.Attackers, StringComparison.Ordinal))
        {
            BattleCandidates(run, state);
            if (_cells.Count > 0 && run.SearchMs <= 0)
            {
                if (!TakeSearch())
                {
                    run.Wake = true;   // next tick
                    return;
                }
                if (_pathfinder.FindNearest(_instance!.Grid, run.Player.Cell, _cells, _content.Limits, _path, out var nearest) == PathStatus.Found
                    && TryTarget(_ids[nearest], out var found))
                {
                    run.Retaliating = false;
                    Engage(run, state, found);
                    return;
                }
                run.SearchMs = SearchAgainMs;   // none can be walked to now
            }
        }
        GoBack(run);
    }

    /// <summary>True below <c>auto_battle.stop_below_hp_percent</c> of the maximum HP.</summary>
    private bool LowHp(AutopilotRun run) =>
        run.Player.Vitals is { } vitals && vitals.MaxHp > 0 && (long)vitals.Hp * 100 < (long)_content.Battle.StopBelowHpPercent * vitals.MaxHp;

    /// <summary>Within <c>auto_battle.radius_cells</c> (plus <paramref name="extra"/>) of the anchor.</summary>
    private bool InArea(AutopilotRun.AutoBattle battle, GridCell cell, int extra) => GridDistance.Chebyshev(battle.Anchor, cell) <= _content.Battle.RadiusCells + extra;

    /// <summary>The filter (<see cref="AutopilotRules.Fights"/>): level band, bosses, and under <c>attackers</c> only what hit the character.</summary>
    private bool MayFight(AutopilotRun run, ProgressionState state, Entity monster, bool hitMe)
    {
        if (monster.DefinitionId is not { } id || !_content.TryGetMonster(id, out var level, out var boss)) return false;
        hitMe |= run.Attacker == monster.Id && run.AttackerMs > 0;
        return AutopilotRules.Fights(_content.Battle, run.Battle!.Filter, state.BaseLevel, level, boss, hitMe);
    }

    /// <summary>Drops the target: dead, gone, out of the area or refused by the filter. One that is still alive is let go of, so combat does not chase it out of the area.</summary>
    private void LetGo(AutopilotRun run, bool alive)
    {
        if (alive && (run.Attacking || run.AttackMs > 0)) Send(new StopAttack(run.Player.Id));
        run.Target = null;
        run.Attacking = false;
        run.AttackMs = 0;
        run.Retaliating = false;
    }

    /// <summary>
    /// The monster that hit the character within <c>autopilot.retaliate_ms</c>, if the filter allows it, it stands within <c>auto_battle.retaliate_extra_cells</c> beyond the radius and
    /// the player sees it. Asked between fights, as at a quest's hunting ground.
    /// </summary>
    private Entity? BattleFightBack(AutopilotRun run, ProgressionState state)
    {
        if (run.Attacker is not { } id || run.AttackerMs <= 0) return null;
        if (!TryTarget(id, out var attacker) || IsAvoided(run, id) || _content.IsSafe(_instance!.Grid, attacker.Cell)) return null;
        if (!InArea(run.Battle!, attacker.Cell, _content.Battle.RetaliateExtraCells) || GridDistance.Chebyshev(run.Player.Cell, attacker.Cell) > _content.Sight) return null;
        return MayFight(run, state, attacker, true) ? attacker : null;
    }

    /// <summary>Every living monster of this map in the area that the filter allows, outside safe zones, seen by the player and that can be walked to, in entity order.</summary>
    private void BattleCandidates(AutopilotRun run, ProgressionState state)
    {
        _cells.Clear();
        _ids.Clear();
        var battle = run.Battle!;
        var entities = _instance!.Entities;
        for (var i = 0; i < entities.Count; i++)   // not foreach: an interface's enumerator is an allocation
        {
            var entity = entities[i];
            if (entity.Kind != EntityKind.Monster || !InArea(battle, entity.Cell, 0)) continue;
            if (!TryTarget(entity.Id, out _) || IsAvoided(run, entity.Id) || _content.IsSafe(_instance.Grid, entity.Cell)) continue;
            if (GridDistance.Chebyshev(run.Player.Cell, entity.Cell) > _content.Sight || !MayFight(run, state, entity, false) || !InReach(run.Player, entity)) continue;
            _cells.Add(entity.Cell);
            _ids.Add(entity.Id);
        }
    }

    /// <summary>
    /// With <c>auto_battle.loot</c>: fetches the nearest item lying in the area and within <c>autopilot.gather_range</c> that is the character's own (its kills' drops, while it may
    /// take them). True when it is on it (or waits for a search). One that does not fit ends the run (<c>bag_full</c>, from the inventory's refusal).
    /// </summary>
    private bool Loot(AutopilotRun run)
    {
        if (!_content.Battle.Loot) return false;
        _cells.Clear();
        _ids.Clear();
        var battle = run.Battle!;
        var entities = _instance!.Entities;
        for (var i = 0; i < entities.Count; i++)   // not foreach: an interface's enumerator is an allocation
        {
            var entity = entities[i];
            if (entity.Kind != EntityKind.Item || entity.GroundItem is not { } item || item.Owner != run.Player.Id) continue;
            if (!InArea(battle, entity.Cell, 0) || GridDistance.Chebyshev(run.Player.Cell, entity.Cell) > _balance.GatherRange || IsAvoided(run, entity.Id)) continue;
            if (!LootRules.MayPickUp(item, run.Player.Id, _nowMs) || !InReach(run.Player, entity)) continue;
            _cells.Add(entity.Cell);
            _ids.Add(entity.Id);
        }
        return FetchNearest(run);
    }

    /// <summary>Nothing to fight: back to the anchor, and waiting there for a respawn. The run's own walks only ever go to cells inside the area.</summary>
    private void GoBack(AutopilotRun run)
    {
        var anchor = run.Battle!.Anchor;
        if (Same(run.Player.Cell, anchor))
        {
            run.Leg = null;
            Doing(run, AutopilotActivities.Waiting);
            return;
        }
        Doing(run, AutopilotActivities.Returning);
        if (run.Leg is { } leg && Same(leg, anchor) && (run.Walking || run.LegMs > 0)) return;
        Walk(run, anchor);
    }
}
