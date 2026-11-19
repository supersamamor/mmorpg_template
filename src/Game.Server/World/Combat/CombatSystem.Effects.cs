using Game.Server.World.Effects;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Combat;

// The effect runner's host (T-501): skills, consumables and statuses, through the same hit, death and EXP path as auto-attacks.
public sealed partial class CombatSystem
{
    /// <summary>The instance's effect runner (after the first tick); tests use it to run effects directly.</summary>
    public EffectRunner? Runner => _runner;

    /// <summary>Each living entity's statuses: due <c>on_tick</c> effects, then expiry (entity id order).</summary>
    private void TickStatuses()
    {
        for (var i = 0; i < _ordered.Count; i++)
        {
            var entity = _ordered[i].Entity;
            if (entity.Dead || entity.Statuses is not { IsEmpty: false }) continue;
            _runner!.TickStatuses(entity.Id.Value);
        }
    }

    /// <summary>The skill requests raised last tick, in raise order: re-checked, then their effects run.</summary>
    private void ResolveSkills(TickContext context)
    {
        (_skillRequests, _skillResolving) = (_skillResolving, _skillRequests);
        for (var i = 0; i < _skillResolving.Count; i++)
        {
            var request = _skillResolving[i];
            var reason = CheckSkill(request, out var caster, out var skill);
            if (reason is not null)
            {
                context.Publish(new SkillRefused(request.Caster, request.SkillId, request.Target, reason));
                continue;
            }
            var self = !skill!.Def.Target.Ground && skill.Def.Target.Range <= 0;
            var target = self ? caster!.Entity.Id : request.Target;
            _runner!.Run(skill.Effects, ActorOf(caster!), target?.Value ?? 0, skill.Def.Target.Ground ? request.Cell : null, Math.Max(1, request.Level));
            SkillsUsed++;
            context.Publish(new SkillUsed(request.Caster, request.SkillId, request.Level, target, request.Cell));
        }
        _skillResolving.Clear();
    }

    /// <summary>
    /// A skill request on the tick it is resolved: the caster alive here and free to cast; a ground skill's cell, or an entity skill's target alive here,
    /// on the right side, not a player in a safe zone for a hostile skill; within <c>target.range</c> (Chebyshev) and, above range 1, in line of sight.
    /// </summary>
    private string? CheckSkill(SkillRequested request, out Combatant? caster, out CompiledSkill? skill)
    {
        skill = null;
        if (!_combatants.TryGetValue(request.Caster.Value, out caster) || caster.Entity.Dead) return SkillReasons.CasterDead;
        if (!_content.Effects.TryGetSkill(request.SkillId, out var compiled)) return SkillReasons.InvalidTarget;   // validated content has every skill
        skill = compiled;
        if (caster.Entity.CannotUseSkills) return SkillReasons.CannotUseSkills;
        var target = compiled.Def.Target;
        var from = caster.Entity.Cell;
        GridCell to;
        if (target.Ground)
        {
            if (request.Cell is not { } cell || !_instance!.Grid.InBounds(cell.X, cell.Y)) return SkillReasons.InvalidTarget;
            to = cell;
        }
        else if (target.Range <= 0)
        {
            return null;   // on itself
        }
        else
        {
            if (request.Target is not { } id || !_combatants.TryGetValue(id.Value, out var t)) return SkillReasons.TargetGone;
            if (t.Entity.Dead) return SkillReasons.TargetDead;
            var sameSide = t.Entity.OnPlayerSide == caster.Entity.OnPlayerSide;
            if (sameSide != target.Friendly) return SkillReasons.InvalidTarget;
            if (!target.Friendly && t.Entity.Kind == EntityKind.Player && InSafeZone(t.Entity.Cell)) return SkillReasons.SafeZone;
            to = t.Entity.Cell;
        }
        if (GridDistance.Chebyshev(from, to) > target.Range) return SkillReasons.OutOfRange;
        if (!InSight(from, to, target.Range)) return SkillReasons.NoLineOfSight;
        return null;
    }

    /// <summary>The consumables used last tick: each one's effects on its user, at level 1 (ARCHITECTURE §6.8).</summary>
    private void UseItems()
    {
        (_itemUses, _itemResolving) = (_itemResolving, _itemUses);
        for (var i = 0; i < _itemResolving.Count; i++)
        {
            var use = _itemResolving[i];
            // A user who left the map or died in the tick between is not affected (the item was used up all the same).
            if (!_combatants.TryGetValue(use.Player.Value, out var user) || user.Entity.Dead) continue;
            if (_content.Effects.TryGetItem(use.ItemId, out var effects)) _runner!.Run(effects, ActorOf(user), user.Entity.Id.Value, null, 1);
        }
        _itemResolving.Clear();
        UseDialogueEffects();
        PayQuestRewards();
    }

    /// <summary>
    /// The rewards of quests completed last tick that the progression host pays (T-602): base and job EXP through <see cref="ProgressionRules"/> (levels reach the sheet as
    /// for a kill), stat and skill points, job unlocks (<c>requires_unlock</c> of a job change), and statuses through the runner. None of them can fail: the quest is
    /// already completed and its items are in the bag. The store is shared, so a player who left the map still gets the points; a status needs it here and alive.
    /// </summary>
    private void PayQuestRewards()
    {
        if (_questRewards.Count == 0) return;
        (_questRewards, _questRewardsRunning) = (_questRewardsRunning, _questRewards);
        foreach (var reward in _questRewardsRunning)
        {
            if (!_progressions.TryGet(reward.Player, out var state)) continue;
            var jobLevel = state.JobLevel;
            var baseResult = _progression.GainBaseExp(state, reward.BaseExp);
            var jobResult = _progression.GainJobExp(state, reward.JobExp);
            _progression.AwardPoints(state, reward.StatPoints, reward.SkillPoints);
            foreach (var job in reward.UnlockJobs) _progression.UnlockJob(state, job);
            foreach (var change in baseResult.Events) _context!.Publish(new PlayerProgressed(reward.Player, change));
            foreach (var change in jobResult.Events) _context!.Publish(new PlayerProgressed(reward.Player, change));
            _combatants.TryGetValue(reward.Player.Value, out var user);
            if (user is not null && (baseResult.LevelsGained > 0 || jobResult.LevelsGained > 0)) Restat(user.Entity, state, jobLevel != state.JobLevel);
            if (user is not null && !user.Entity.Dead)
                foreach (var key in reward.StatusKeys)
                    if (_content.Effects.TryGetQuestReward(key, out var effects)) _runner!.Run(effects, ActorOf(user), user.Entity.Id.Value, null, 1);
            _context!.Publish(new QuestRewarded(reward.Player, reward.QuestId, reward.BaseExp, reward.JobExp, reward.StatPoints, reward.SkillPoints, reward.UnlockJobs));
        }
        _questRewardsRunning.Clear();
    }

    /// <summary>
    /// The <c>run_effects</c> actions of dialogues raised last tick (T-601): each one's compiled effects on the talking player, who is caster and target as for a
    /// consumable. A player who left the map or died in the tick between is not affected.
    /// </summary>
    private void UseDialogueEffects()
    {
        if (_dialogueEffects.Count == 0) return;
        (_dialogueEffects, _dialogueEffectsRunning) = (_dialogueEffectsRunning, _dialogueEffects);
        for (var i = 0; i < _dialogueEffectsRunning.Count; i++)
        {
            var request = _dialogueEffectsRunning[i];
            if (!_combatants.TryGetValue(request.Player.Value, out var user) || user.Entity.Dead) continue;
            if (_content.Effects.TryGetDialogue(request.Key, out var effects)) _runner!.Run(effects, ActorOf(user), user.Entity.Id.Value, null, 1);
        }
        _dialogueEffectsRunning.Clear();
    }

    /// <summary>Puts a new <see cref="Entity.StatusLook"/> on every entity whose statuses changed this tick, for the AOI.</summary>
    private void RefreshStatusLooks()
    {
        for (var i = 0; i < _ordered.Count; i++) RefreshLook(_ordered[i].Entity);
    }

    private static void RefreshLook(Entity entity)
    {
        if (entity.Statuses is not { } set || set.Revision == (entity.StatusLook?.Revision ?? 0)) return;
        var active = set.Active;
        if (active.Count == 0)
        {
            entity.StatusLook = new StatusLook([], set.Revision);
            return;
        }
        var icons = new StatusIcon[active.Count];
        for (var i = 0; i < icons.Length; i++)
            icons[i] = new StatusIcon(active[i].Id, active[i].Stacks, active[i].EndsAtMs, active[i].Status.Def.Debuff);
        entity.StatusLook = new StatusLook(icons, set.Revision);
    }

    /// <summary>What the runner needs of a combatant (attack and defence element, race, size, weapon).</summary>
    private static EffectActor ActorOf(Combatant c)
    {
        var e = c.Entity;
        var weapon = e.Weapon;
        var def = c.Def;
        return new EffectActor(e.Id.Value, e.Stats!, e.Cell, e.Facing, e.Dead, e.OnPlayerSide,
            attackElement: weapon is not null ? weapon.Element : ElementOf(def),
            defenseElement: ElementOf(def),
            race: def is { Race.Length: > 0 } ? def.Race : null,
            size: def is { Size.Length: > 0 } ? def.Size : null,
            weaponAtk: weapon?.WeaponAtk ?? 0,
            refine: weapon?.Refine ?? 0);
    }

    /// <summary>
    /// The map instance as the runner sees it. Every change goes through combat's own state and is announced with an event (hard rule 7); moves are
    /// warps, applied in phase 3 of the tick.
    /// </summary>
    private sealed class EffectHost(CombatSystem combat) : IEffectWorld
    {
        public long NowMs => combat._nowMs;

        public MapGrid? Grid => combat._instance!.Grid;

        public bool TryGetActor(long id, out EffectActor actor)
        {
            if (combat._combatants.TryGetValue(id, out var c) && c.Entity.Stats is not null)
            {
                actor = ActorOf(c);
                return true;
            }
            actor = default;
            return false;
        }

        public StatusSet? StatusesOf(long id, bool create)
        {
            if (!combat._combatants.TryGetValue(id, out var c)) return null;
            var entity = c.Entity;
            if (entity.Statuses is null && create) entity.Statuses = new StatusSet { MapId = combat._instance!.MapId };
            if (entity.Statuses is { IsEmpty: true } set) set.MapId = combat._instance!.MapId;
            return entity.Statuses;
        }

        /// <summary>The caster's side or the other: living combatants within the square, in line of sight of its center; never a player in a safe zone for a hostile area.</summary>
        public void CollectArea(in EffectActor caster, GridCell center, int radius, bool friendly, List<long> into)
        {
            into.Clear();
            var area = combat._area;
            area.Clear();
            var grid = combat._instance!.Grid;
            var ordered = combat._ordered;
            for (var i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i].Entity;
                if (e.Dead || e.Stats is null) continue;
                var distance = GridDistance.Chebyshev(center, e.Cell);
                if (distance > radius) continue;
                var sameSide = e.OnPlayerSide == caster.Player;
                if (sameSide != friendly) continue;
                if (!friendly && e.Kind == EntityKind.Player && combat.InSafeZone(e.Cell)) continue;
                if (distance > 0 && !LineOfSight.HasLineOfSight(grid, center, e.Cell)) continue;
                area.Add((distance, e.Id.Value));
            }
            area.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Id.CompareTo(b.Id));
            for (var i = 0; i < area.Count; i++) into.Add(area[i].Id);
            area.Clear();
        }

        public void Damage(in EffectActor caster, long target, AttackResult result, bool fromStatus)
        {
            if (!combat._combatants.TryGetValue(target, out var t) || t.Entity.Dead) return;
            combat._combatants.TryGetValue(caster.Id, out var source);
            combat.Land(combat._context!, new EntityId(caster.Id), source, t, result, fromStatus);
        }

        public void Heal(long caster, long target, int amount)
        {
            if (!combat._combatants.TryGetValue(target, out var t) || t.Entity.Vitals is not { } vitals || t.Entity.Dead) return;
            var before = vitals.Hp;
            vitals.Hp = (int)Math.Min(vitals.MaxHp, (long)before + Math.Max(0, amount));
            if (vitals.Hp < before) vitals.Hp = before;   // never lowers HP above max
            if (vitals.Hp >= vitals.MaxHp && t.Entity.Kind == EntityKind.Monster) combat.ReleaseLog(t);
            Publish(new HpRestored(new EntityId(caster), t.Entity.Id, vitals.Hp - before, vitals.Hp));
        }

        public void RestoreSp(long caster, long target, int amount)
        {
            if (!combat._combatants.TryGetValue(target, out var t) || t.Entity.Vitals is not { } vitals || t.Entity.Dead) return;
            var before = vitals.Sp;
            vitals.Sp = (int)Math.Max(before, Math.Min(vitals.MaxSp, (long)before + Math.Max(0, amount)));
            Publish(new SpRestored(new EntityId(caster), t.Entity.Id, vitals.Sp - before, vitals.Sp));
        }

        public void StatusStarted(long caster, long target, ActiveStatus status, StatusChange change) =>
            Publish(new StatusApplied(new EntityId(caster), new EntityId(target), status.Id, change, status.Stacks, status.EndsAtMs));

        public void StatusEnded(long target, ActiveStatus status, string? reason)
        {
            if (reason is null) Publish(new StatusExpired(new EntityId(target), status.Id));
            else Publish(new StatusRemoved(new EntityId(target), status.Id, reason));
        }

        public void Knockback(long caster, long target, GridCell from, GridCell to)
        {
            var id = new EntityId(target);
            Publish(new KnockedBack(new EntityId(caster), id, from, to));
            combat._context!.Warp(id, combat._instance!.MapId, to);
        }

        /// <summary>
        /// The caster moves: to its save point (a player's is the one an NPC set, else the respawn point of the map it is on; a monster has none, so
        /// nothing happens), to a cell of a map, or to a random walkable cell of this map.
        /// </summary>
        public void Teleport(long caster, TeleportEffect effect)
        {
            if (!combat._combatants.TryGetValue(caster, out var c) || c.Entity.Dead) return;
            var instance = combat._instance!;
            string mapId;
            GridCell cell;
            if (effect.ToSavePoint)
            {
                if (c.Entity.Kind != EntityKind.Player) return;
                (mapId, cell) = combat.SavePointOf(c.Entity);
            }
            else if (effect.X is { } x && effect.Y is { } y)
            {
                mapId = string.IsNullOrEmpty(effect.MapId) ? instance.MapId : effect.MapId!;
                cell = new GridCell(x, y);
            }
            else if (!RandomCell(instance.Grid, combat._context!.Random, out cell))
            {
                return;
            }
            else
            {
                mapId = instance.MapId;
            }
            Publish(new Teleported(c.Entity.Id, mapId, cell));
            combat._context!.Warp(c.Entity.Id, mapId, cell);
        }

        public void Summon(long caster, SummonEffect effect, GridCell near) =>
            Publish(new SummonRequested(new EntityId(caster), effect.MonsterId, effect.Count, near, effect.DurationMs));

        public void ResetStats(long caster, long target)
        {
            if (combat._combatants.TryGetValue(target, out var t) && !t.Entity.Dead) combat.ResetStatsOf(combat._context!, t.Entity);
        }

        public void ResetSkills(long caster, long target)
        {
            if (combat._combatants.TryGetValue(target, out var t) && !t.Entity.Dead) combat.ResetSkillsOf(combat._context!, t.Entity);
        }

        public void AreaApplied(long caster, GridCell center, int radius, int targets) =>
            Publish(new AreaApplied(new EntityId(caster), center, radius, targets));

        private void Publish<T>(T e) where T : Game.Server.Events.IGameEvent => combat._instance!.Events.Publish(e);

        /// <summary>A uniformly random walkable cell of the map (one draw over the walkable cells).</summary>
        private static bool RandomCell(MapGrid grid, Game.Shared.Random.IRandom random, out GridCell cell)
        {
            var count = 0;
            for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
                if (grid.IsWalkable(x, y)) count++;
            cell = default;
            if (count == 0) return false;
            var pick = random.Next(count);
            for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                if (!grid.IsWalkable(x, y)) continue;
                if (pick-- == 0)
                {
                    cell = new GridCell(x, y);
                    return true;
                }
            }
            return false;
        }
    }
}
