using Game.Server.World.Combat;
using Game.Server.World.Inventory;
using Game.Shared.Autopilot;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Quests;
using Game.Shared.Skills;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Autopilot;

// Hunting and gathering (T-604): which monster, which item, which skill. Combat and the inventory do the rest, as for any player.
public sealed partial class AutopilotSystem
{
    /// <summary>
    /// One thought of a hunt, the first of these that applies: wait for a cast; go on with the fight; go on fetching an item; fetch a wanted item lying near; help oneself (a supportive
    /// skill); fight back; the nearest living wanted monster that can be walked to; or go to (or wait at) the nearest place it spawns.
    /// </summary>
    private void Hunt(AutopilotRun run, ProgressionState state, QuestProgress progress, IReadOnlyList<QuestObjectiveDef> objectives, HuntPlan plan)
    {
        run.Plan = plan;
        if (run.Casting) return;
        if (plan.Grounds.Count == 0)
        {
            Pause(run, AutopilotPauses.NoSource);
            return;
        }

        if (run.Target is { } current)
        {
            if (TryTarget(current, out var target))
            {
                Fight(run, state, target);
                return;
            }
            run.Target = null;
            run.Attacking = false;
        }

        if (run.Picking is { } picking)
        {
            if (run.PickMs > 0 && _instance!.TryGetEntity(picking, out _)) return;
            run.Picking = null;
        }
        if (Gather(run, progress, objectives)) return;
        if (run.Leg is null && TryCast(run, state, null)) return;

        if (run.Player.Load == WeightStates.Overweight)
        {
            Stop(run, AutopilotStops.BagFull);   // nothing left to fetch, and it cannot fight
            return;
        }
        if (FightBack(run) is { } attacker)
        {
            Engage(run, state, attacker);
            return;
        }

        if (run.Approaching)
        {
            if (run.Leg is not null && (run.Walking || run.LegMs > 0) && run.SearchMs > 0) return;   // on the way to one: look again in a while
            run.Approaching = false;
            run.SearchMs = 0;
        }
        Candidates(run, plan);
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
                if (GridDistance.Chebyshev(run.Player.Cell, found.Cell) <= _content.Sight) Engage(run, state, found);
                else Approach(run);
                return;
            }
            run.SearchMs = SearchAgainMs;   // all too far: nearer first
        }
        GoToGround(run, plan);
    }

    // ---- targets ------------------------------------------------------------------------------------------------------------------------------

    private void Engage(AutopilotRun run, ProgressionState state, Entity target)
    {
        run.Target = target.Id;
        run.Attacking = false;
        run.AttackMs = 0;
        run.Goal = null;
        Doing(run, AutopilotActivities.Hunting);
        Fight(run, state, target);
    }

    /// <summary>Walks along the path just found toward a monster that is out of sight: it is aimed at only once the player could see it.</summary>
    private void Approach(AutopilotRun run)
    {
        run.Goal = null;
        Doing(run, AutopilotActivities.Hunting);
        Walk(run, _path[Math.Min(_path.Count, run.LegCells) - 1]);
        run.Approaching = true;
        run.SearchMs = ApproachMs;
    }

    /// <summary>Steps out of a safe zone (nothing can be attacked from one), casts the first shortcut skill that can be cast, or keeps the auto-attack going.</summary>
    private void Fight(AutopilotRun run, ProgressionState state, Entity target)
    {
        Doing(run, AutopilotActivities.Hunting);
        if (_content.IsSafe(_instance!.Grid, run.Player.Cell))
        {
            StepOut(run, target);
            return;
        }
        if (!Stand(run)) return;
        if (TryCast(run, state, target)) return;
        if (run.Attacking || run.AttackMs > 0 || run.Player.CannotAttack) return;
        if (!Send(new AttackTarget(run.Player.Id, target.Id))) return;
        run.AttackMs = AnswerMs;
    }

    /// <summary>
    /// Ends the walk of a journey before a fight or an item: combat and the inventory take any walk that is not their own (and its end) for a new order. True once it stands.
    /// </summary>
    private bool Stand(AutopilotRun run)
    {
        if (run.Leg is null) return true;
        if (!run.Walking && run.LegMs <= 0)
        {
            run.Leg = null;   // never taken up
            return true;
        }
        if (!run.Stopping && Send(new StopMove(run.Player.Id)))
        {
            run.Stopping = true;
            run.LegMs = AnswerMs;
        }
        else if (run.Stopping && run.LegMs <= 0)
        {
            run.Leg = null;   // it had ended by itself
            run.Walking = false;
            run.Stopping = false;
            return true;
        }
        return false;
    }

    private void StepOut(AutopilotRun run, Entity target)
    {
        if (run.Leg is not null && (run.Walking || run.LegMs > 0)) return;
        if (!TakeSearch())
        {
            run.Wake = true;
            return;
        }
        var grid = _instance!.Grid;
        if (_pathfinder.FindPath(grid, run.Player.Cell, target.Cell, _content.Limits, _path) == PathStatus.Found)
        {
            for (var i = 0; i < _path.Count; i++)
            {
                if (_content.IsSafe(grid, _path[i])) continue;
                Walk(run, _path[i]);
                return;
            }
        }
        Avoid(run, target.Id);
        run.Target = null;
    }

    /// <summary>A living monster of the other side on this map.</summary>
    private bool TryTarget(EntityId id, out Entity target) =>
        _instance!.TryGetEntity(id, out target) && target.Kind == EntityKind.Monster && !target.Dead && !target.OnPlayerSide && target.Vitals is not null;

    /// <summary>
    /// The monster that hit the character within <c>autopilot.retaliate_ms</c>, if it is still close. Only asked at the hunting ground, between fights: there an attacker blocks the plan
    /// (it interrupts casts and keeps hitting while the character waits or looks for its next target). On a journey, to an item or an NPC, attackers are walked away from.
    /// </summary>
    private Entity? FightBack(AutopilotRun run)
    {
        if (run.Attacker is not { } id || run.AttackerMs <= 0 || run.Status.Doing == AutopilotActivities.Travelling) return null;
        if (!TryTarget(id, out var attacker) || IsAvoided(run, id) || _content.IsSafe(_instance!.Grid, attacker.Cell)) return null;
        return GridDistance.Chebyshev(run.Player.Cell, attacker.Cell) <= _balance.GatherRange ? attacker : null;
    }

    /// <summary>Every living wanted monster of this map that can be walked to and stands outside its safe zones, in entity order.</summary>
    private void Candidates(AutopilotRun run, HuntPlan plan)
    {
        _cells.Clear();
        _ids.Clear();
        var entities = _instance!.Entities;
        for (var i = 0; i < entities.Count; i++)   // not foreach: an interface's enumerator is an allocation
        {
            var entity = entities[i];
            if (entity.Kind != EntityKind.Monster || entity.DefinitionId is null || !plan.Monsters.Contains(entity.DefinitionId)) continue;
            if (!TryTarget(entity.Id, out _) || IsAvoided(run, entity.Id) || _content.IsSafe(_instance.Grid, entity.Cell)) continue;
            if (!InReach(run.Player, entity)) continue;
            _cells.Add(entity.Cell);
            _ids.Add(entity.Id);
        }
    }

    /// <summary>
    /// Nothing to fight in reach: to the nearest spawn area (fewest warps, then the shortest walk). In one already, to the area nearest to a wanted monster that is alive elsewhere on the
    /// map; else it waits for a respawn.
    /// </summary>
    private void GoToGround(AutopilotRun run, HuntPlan plan)
    {
        var ground = Nearest(run, plan.Grounds, out var route);
        if (ground is null)
        {
            Pause(run, AutopilotPauses.Unreachable);
            return;
        }
        if (There(route) && _cells.Count > 0)
        {
            var prey = 0;
            for (var i = 1; i < _cells.Count; i++)
                if (GridDistance.Chebyshev(run.Player.Cell, _cells[i]) < GridDistance.Chebyshev(run.Player.Cell, _cells[prey])) prey = i;
            for (var i = 0; i < plan.Grounds.Count; i++)
            {
                var other = plan.Grounds[i];
                if (!string.Equals(other.MapId, _instance!.MapId, StringComparison.Ordinal) || other.Field.At(_cells[prey]) >= ground.Field.At(_cells[prey])) continue;
                var way = _content.Travel.Plan(_instance.MapId, run.Player.Cell, other);
                if (!way.Cost.Reachable) continue;
                ground = other;
                route = way;
            }
        }
        if (!There(route))
        {
            Travel(run, ground, route);
            return;
        }
        run.Leg = null;
        Doing(run, AutopilotActivities.Waiting);
    }

    // ---- items ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fetches the nearest item lying within <c>autopilot.gather_range</c> that an unmet <c>collect</c> objective of the step wants (and no other item) and that the character may take now
    /// under the loot priority. True when it is on it (or waits for a search).
    /// </summary>
    private bool Gather(AutopilotRun run, QuestProgress progress, IReadOnlyList<QuestObjectiveDef> objectives)
    {
        var wanted = false;
        for (var i = 0; i < objectives.Count && !wanted; i++) wanted = objectives[i] is CollectObjective c && progress.Counters[i] < QuestRules.Need(c);
        if (!wanted) return false;

        _cells.Clear();
        _ids.Clear();
        var entities = _instance!.Entities;
        for (var i = 0; i < entities.Count; i++)   // not foreach: an interface's enumerator is an allocation
        {
            var entity = entities[i];
            if (entity.Kind != EntityKind.Item || entity.GroundItem is not { } item || !Wants(objectives, progress, item.ItemId)) continue;
            if (GridDistance.Chebyshev(run.Player.Cell, entity.Cell) > _balance.GatherRange || IsAvoided(run, entity.Id)) continue;
            if (!LootRules.MayPickUp(item, run.Player.Id, _nowMs) || !InReach(run.Player, entity)) continue;
            _cells.Add(entity.Cell);
            _ids.Add(entity.Id);
        }
        if (_cells.Count == 0) return false;
        if (!TakeSearch())
        {
            run.Wake = true;
            return true;
        }
        if (_pathfinder.FindNearest(_instance.Grid, run.Player.Cell, _cells, _content.Limits, _path, out var nearest) != PathStatus.Found) return false;
        if (!Stand(run)) return true;
        // As the gateway does for a player's pick-up: the auto-attack stops first, so combat's walk does not end the walk to the item.
        if (!Send(new StopAttack(run.Player.Id)) || !Send(new PickUp(run.Player.Id, _ids[nearest]))) return true;
        run.Picking = _ids[nearest];
        run.PickMs = PickUpMs;
        run.Goal = null;
        Doing(run, AutopilotActivities.Gathering);
        return true;
    }

    private static bool Wants(IReadOnlyList<QuestObjectiveDef> objectives, QuestProgress progress, string itemId)
    {
        for (var i = 0; i < objectives.Count; i++)
            if (objectives[i] is CollectObjective c && progress.Counters[i] < QuestRules.Need(c) && string.Equals(c.ItemId, itemId, StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- skills --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Casts the first shortcut skill, in slot order, that the server would accept now (learned, weapon, cooldown, SP) and that has a use (<see cref="AutopilotSkill"/>): offensive ones
    /// on <paramref name="target"/> (none between fights), supportive ones on the player. True when one is on its way.
    /// </summary>
    private bool TryCast(AutopilotRun run, ProgressionState state, Entity? target)
    {
        var player = run.Player;
        if (run.Skills.Length == 0 || player.CannotUseSkills || player.Stats is not { } stats || player.Vitals is not { } vitals) return false;
        for (var i = 0; i < run.Skills.Length; i++)
        {
            var slot = run.Skills[i];
            var skill = slot.Content.Skill;
            if (slot.RetryMs > 0 || skill.Use == AutopilotSkillUse.Never) continue;
            if (target is null && skill.Use != AutopilotSkillUse.OnSelf) continue;
            var learned = state.SkillLevel(slot.SkillId);
            var level = slot.Level <= 0 ? learned : slot.Level;
            if (level < 1 || level > learned) continue;   // forgotten since the start
            if (!SkillRules.WeaponAllowed(skill.Def, player.Weapon?.WeaponType ?? WeaponTypes.Unarmed)) continue;
            if (player.Cooldowns is { } cooldowns && !cooldowns.IsReady(slot.SkillId)) continue;
            if (SkillRules.SpCost(stats, slot.Content.SpCost, level, _formula) > vitals.Sp) continue;

            UseSkill use;
            switch (skill.Use)
            {
                case AutopilotSkillUse.OnTarget: use = new UseSkill(player.Id, slot.SkillId, level, target!.Id, null); break;
                case AutopilotSkillUse.OnTargetCell: use = new UseSkill(player.Id, slot.SkillId, level, null, target!.Cell); break;
                case AutopilotSkillUse.AroundSelf:
                    if (GridDistance.Chebyshev(player.Cell, target!.Cell) > skill.Radius) continue;
                    use = new UseSkill(player.Id, slot.SkillId, level, null, null);
                    break;
                default:
                    if (!skill.IsUseful(vitals.Hp, vitals.MaxHp, vitals.Sp, vitals.MaxSp, player.Statuses, _balance.SupportBelowPercent)) continue;
                    use = new UseSkill(player.Id, slot.SkillId, level, null, null);
                    break;
            }
            if (!Send(use)) return false;
            run.Casting = true;
            run.CastMs = CastMs;
            run.Attacking = false;   // a cast replaces the auto-attack
            return true;
        }
        return false;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Rules out, without a search, what no walk leads to and what is farther than a path may be long (a step covers one cell).</summary>
    private bool InReach(Entity player, Entity other) =>
        GridDistance.Chebyshev(player.Cell, other.Cell) <= _content.Limits.MaxPathLength && _content.Travel.Connected(_instance!.MapId, player.Cell, other.Cell);

    private bool TakeSearch()
    {
        if (_searches <= 0) return false;
        _searches--;
        Searches++;
        return true;
    }

    private void Avoid(AutopilotRun run, EntityId id)
    {
        if (_balance.AvoidMs > 0) run.Avoid.Add((id, _nowMs + _balance.AvoidMs));
    }

    private bool IsAvoided(AutopilotRun run, EntityId id)
    {
        for (var i = run.Avoid.Count - 1; i >= 0; i--)
        {
            if (run.Avoid[i].UntilMs <= _nowMs) run.Avoid.RemoveAt(i);
            else if (run.Avoid[i].Id == id) return true;
        }
        return false;
    }
}
