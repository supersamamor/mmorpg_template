using Game.Server.World.Effects;
using Game.Shared.Effects;
using Game.Shared.Formulas;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Skills;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Combat;

// Player skills (T-502): learning, job change, passives, and casting with cast time, SP and cooldowns. A cast reuses the auto-attack's chase machinery
// (walking into range, the movement events that cancel it) and ends in a SkillRequested, which the effect runner's host (CombatSystem.Effects) resolves.
public sealed partial class CombatSystem
{
    private static readonly int CastTimeModStat = StatIndex.IndexOf(DerivedStats.CastTimeMod);
    private static readonly int SkillLevelSlot = FormulaSymbols.Default.SlotOf(FormulaVariables.SkillLevel);
    private readonly FormulaContext _skillFormula = new(FormulaSymbols.Default);

    /// <summary>The player's skill cooldown left, in milliseconds (0 when ready or not a player).</summary>
    public long CooldownLeftMs(EntityId player, string skillId) =>
        _combatants.TryGetValue(player.Value, out var c) && c.Entity.Cooldowns is { } cd ? cd.LeftMs(skillId) : 0;

    /// <summary>True while the player is walking toward a cast or casting.</summary>
    public bool IsCasting(EntityId player) => _chaseById.TryGetValue(player.Value, out var chase) && chase.Cast is not null;

    // ---- learning, job change, passives ---------------------------------------------------------------------------------------------

    private void Learn(TickContext context, Entity player, string skillId)
    {
        if (player.Kind != EntityKind.Player || !_progressions.TryGet(player.Id, out var state)) return;
        var result = _progression.LearnSkill(state, skillId);
        if (!result.Success)
        {
            context.Publish(new SkillLearnRefused(player.Id, skillId, SkillRules.ReasonFor(result.Error)));
            return;
        }
        foreach (var change in result.Events) context.Publish(new PlayerProgressed(player.Id, change));
        ApplyPassive(player, state, skillId);
    }

    /// <summary>
    /// Changes the job when its requirements are met. What carries over: base level and EXP, stat points and stats, unspent skill points and every
    /// learned skill (parent jobs' skills stay usable). What resets: job level to 1 and job EXP to 0 (<see cref="ProgressionRules.ChangeJob"/>). The job's
    /// stat bonuses are swapped (the old <c>job:&lt;id&gt;</c> modifiers go, the new job's come at job level 1), and the player's <see cref="Entity.DefinitionId"/>
    /// becomes the new job, which is what usable weapon types and the character's look follow.
    /// </summary>
    private void ChangeJobOf(TickContext context, Entity player, string jobId)
    {
        if (player.Kind != EntityKind.Player || !_progressions.TryGet(player.Id, out var state)) return;
        var from = state.JobId;
        var events = _progression.ChangeJob(state, jobId, out var check);
        if (!check.Allowed)
        {
            context.Publish(new JobChangeRefused(player.Id, jobId, SkillRules.ReasonFor(check)));
            return;
        }
        if (player.Stats is { } sheet) sheet.Modifiers.RemoveBySource("job:" + from);
        player.DefinitionId = state.JobId;
        Restat(player, state, jobLevelChanged: true);
        foreach (var change in events) context.Publish(new PlayerProgressed(player.Id, change));
    }

    private void ApplyPassive(Entity player, ProgressionState state, string skillId)
    {
        if (player.Stats is not { } sheet || !_content.Progression.TryGetSkill(skillId, out var skill) || skill is null || !skill.Passive) return;
        SkillRules.ApplyPassive(sheet, skill, state.SkillLevel(skillId), _skillFormula);
        player.Vitals?.Sync(sheet);
    }

    /// <summary>A player entering the map: its passives are on its sheet (idempotent), and its cooldowns count from this map's clock.</summary>
    private void SetUpPlayerSkills(Entity player)
    {
        if (player.Kind != EntityKind.Player) return;
        player.Cooldowns ??= new SkillCooldowns();
        player.Cooldowns.Resume(_nowMs);
        if (player.Stats is { } sheet && _progressions.TryGet(player.Id, out var state))
        {
            SkillRules.ApplyPassives(sheet, state.Skills, _content.Progression, _skillFormula);
            player.Vitals?.Sync(sheet);
        }
    }

    // ---- casting --------------------------------------------------------------------------------------------------------------------

    private void StartSkill(TickContext context, Entity player, UseSkill use)
    {
        var reason = CheckUse(player, use, out var skill, out var level, out var target);
        if (reason is not null)
        {
            context.Publish(new SkillRefused(player.Id, use.SkillId, use.Target, reason));
            return;
        }
        if (_chaseById.TryGetValue(player.Id.Value, out var old)) End(context, old, AttackReasons.Cancelled);   // a new order cancels the old one
        var def = skill!.Def.Target;
        var self = !def.Ground && def.Range <= 0;
        var chase = _chasePool.Count > 0 ? _chasePool.Pop() : new Chase();
        chase.Reset(player);
        chase.Cast = new PlayerCast(skill, level, def.Ground || self ? null : target, def.Ground ? use.Cell : null, self);
        chase.NextRepathMs = _nowMs;
        _chaseById.Add(player.Id.Value, chase);
        var index = _chases.BinarySearch(chase, ChaseOrder.Instance);
        _chases.Insert(index < 0 ? ~index : index, chase);
    }

    /// <summary>The checks of a <c>use_skill</c> on the tick it arrives (everything but range and sight, which the walk takes care of).</summary>
    private string? CheckUse(Entity player, UseSkill use, out CompiledSkill? skill, out int level, out EntityId? target)
    {
        skill = null;
        level = 0;
        target = null;
        if (player.Kind != EntityKind.Player || player.Dead || !_combatants.ContainsKey(player.Id.Value)) return SkillReasons.CasterDead;
        if (!_content.Effects.TryGetSkill(use.SkillId, out var compiled)) return SkillReasons.UnknownSkill;
        skill = compiled;
        var learned = _progressions.TryGet(player.Id, out var state) ? state.SkillLevel(use.SkillId) : 0;
        if (learned < 1) return SkillReasons.NotLearned;
        if (compiled.Def.Passive) return SkillReasons.PassiveSkill;
        level = use.Level <= 0 ? learned : use.Level;
        if (level > learned) return SkillReasons.LevelTooHigh;
        if (player.CannotUseSkills) return SkillReasons.CannotUseSkills;
        var reason = CheckResources(player, compiled, level);
        if (reason is not null) return reason;

        var aim = compiled.Def.Target;
        if (!aim.Friendly && InSafeZone(player.Cell)) return SkillReasons.SafeZone;   // a hostile skill, like an attack, is not cast from a safe zone
        if (aim.Ground)
        {
            if (use.Cell is not { } cell || !_instance!.Grid.InBounds(cell.X, cell.Y)) return SkillReasons.InvalidTarget;
        }
        else if (aim.Range > 0)
        {
            target = use.Target ?? (aim.Friendly ? player.Id : null);
            if (target is not { } id) return SkillReasons.InvalidTarget;
            if (!_combatants.TryGetValue(id.Value, out var t)) return SkillReasons.TargetGone;
            if (t.Entity.Dead) return SkillReasons.TargetDead;
            if (t.Entity.OnPlayerSide != aim.Friendly) return SkillReasons.InvalidTarget;
        }
        return null;
    }

    /// <summary>Weapon, cooldown and SP: the checks that can change between the order and the end of the cast.</summary>
    private string? CheckResources(Entity player, CompiledSkill skill, int level)
    {
        if (!SkillRules.WeaponAllowed(skill.Def, player.Weapon?.WeaponType ?? WeaponTypes.Unarmed)) return SkillReasons.WrongWeapon;
        if (player.Cooldowns is { } cooldowns && !cooldowns.IsReady(skill.Id)) return SkillReasons.OnCooldown;
        if (SpCostOf(player, skill, level) > (player.Vitals?.Sp ?? 0)) return SkillReasons.NotEnoughSp;
        return null;
    }

    private int SpCostOf(Entity player, CompiledSkill skill, int level)
    {
        if (player.Stats is not { } stats) return 0;
        _skillFormula.Clear();
        stats.WriteTo(_skillFormula);
        _skillFormula[SkillLevelSlot] = level;
        return (int)Math.Clamp(Math.Floor(skill.SpCost.Evaluate(_skillFormula) + 1e-9), 0, int.MaxValue);
    }

    /// <summary>One player's cast for one tick: walk into range and sight, stand and cast, then pay and request. False when it ended.</summary>
    private bool PursueCast(TickContext context, Chase chase)
    {
        var player = chase.Player;
        var cast = chase.Cast!;
        if (player.Dead) return AbortCast(context, chase, SkillReasons.CasterDead, CastResults.Interrupted);
        if (chase.EndReason is { } ended)
            return ended == AttackReasons.Cancelled
                ? AbortCast(context, chase, null, CastResults.Cancelled)
                : AbortCast(context, chase, SkillReasons.OutOfRange, CastResults.Interrupted);
        if (player.CannotUseSkills) return AbortCast(context, chase, SkillReasons.CannotUseSkills, CastResults.Interrupted);
        if (!_combatants.TryGetValue(player.Id.Value, out var self)) return AbortCast(context, chase, SkillReasons.CasterDead, CastResults.Interrupted);

        GridCell to;
        if (cast.Cell is { } ground)
        {
            to = ground;
        }
        else if (cast.Target is { } targetId)
        {
            if (!_combatants.TryGetValue(targetId.Value, out var target)) return AbortCast(context, chase, SkillReasons.TargetGone, CastResults.Interrupted);
            if (target.Entity.Dead) return AbortCast(context, chase, SkillReasons.TargetDead, CastResults.Interrupted);
            to = target.Entity.Cell;
        }
        else
        {
            to = player.Cell;
        }

        if (cast.EndsAtMs >= 0)
        {
            player.Facing = GridDirection.Facing(player.Cell, to) ?? player.Facing;
            return _nowMs < cast.EndsAtMs || FinishCast(context, chase, self);
        }

        var aim = cast.Skill.Def.Target;
        var range = cast.Self ? 0 : aim.Range;
        if (cast.Self || (GridDistance.Chebyshev(player.Cell, to) <= range && InSight(player.Cell, to, range)))
        {
            if (chase.Goal is not null && (chase.Walking || chase.MovePending(_tick))) Stop(context, chase);
            player.Facing = GridDirection.Facing(player.Cell, to) ?? player.Facing;
            return BeginCast(context, chase, self);
        }

        if (_nowMs < chase.NextRepathMs || player.CannotMove) return true;
        var walkingThere = chase.Goal is { } goal && Same(goal, to) && (chase.Walking || chase.MovePending(_tick));
        if (walkingThere) return true;
        Move(context, chase, to);
        chase.NextRepathMs = _nowMs + _content.Balance.Combat.PlayerRepathIntervalMs;
        return true;
    }

    /// <summary>In range: the cast time starts (the caster stands still); a skill with none is paid and requested at once.</summary>
    private bool BeginCast(TickContext context, Chase chase, Combatant self)
    {
        var player = chase.Player;
        var cast = chase.Cast!;
        var reason = CheckResources(player, cast.Skill, cast.Level);   // the walk took time: the SP or the cooldown may have changed
        if (reason is not null) return AbortCast(context, chase, reason, CastResults.Interrupted);
        _skillFormula.Clear();
        player.Stats!.WriteTo(_skillFormula);
        _skillFormula[SkillLevelSlot] = cast.Level;
        cast.CastMs = (int)Math.Clamp(Math.Floor(cast.Skill.CastTimeMs.Evaluate(_skillFormula) * player.Stats.Get(CastTimeModStat) + 1e-9), 0, int.MaxValue);
        cast.EndsAtMs = _nowMs + cast.CastMs;
        if (cast.CastMs <= 0) return FinishCast(context, chase, self);
        context.Publish(new SkillCastStarted(player.Id, cast.Skill.Id, cast.Level, cast.Target, cast.Cell, cast.CastMs));
        return true;
    }

    /// <summary>The cast time is up: pay the SP, start the cooldown, and ask for the skill to be resolved (re-checked on the next tick).</summary>
    private bool FinishCast(TickContext context, Chase chase, Combatant self)
    {
        var player = chase.Player;
        var cast = chase.Cast!;
        var reason = CheckResources(player, cast.Skill, cast.Level);
        if (reason is not null) return AbortCast(context, chase, reason, CastResults.Interrupted);
        var vitals = player.Vitals!;
        vitals.Sp -= SpCostOf(player, cast.Skill, cast.Level);
        _skillFormula.Clear();
        player.Stats!.WriteTo(_skillFormula);
        _skillFormula[SkillLevelSlot] = cast.Level;
        var cooldownMs = (long)Math.Floor(Math.Max(0, cast.Skill.CooldownMs.Evaluate(_skillFormula)) + 1e-9);
        player.Cooldowns ??= new SkillCooldowns();
        player.Cooldowns.Start(cast.Skill.Id, cooldownMs);
        if (cast.CastMs > 0) context.Publish(new SkillCastEnded(player.Id, cast.Skill.Id, CastResults.Done));
        if (cooldownMs > 0) context.Publish(new SkillCooldownStarted(player.Id, cast.Skill.Id, (int)Math.Min(cooldownMs, int.MaxValue)));
        context.Publish(new SkillRequested(player.Id, cast.Skill.Id, cast.Level, cast.Target, cast.Cell, _nowMs));
        Drop(chase);
        return false;
    }

    /// <summary>
    /// Ends a cast that did not finish: the cast bar closes (when one was shown) and, with a <paramref name="refusal"/>, the player is told why. Always false.
    /// </summary>
    private bool AbortCast(TickContext context, Chase chase, string? refusal, string result)
    {
        var cast = chase.Cast!;
        if (cast.CastMs > 0 && cast.EndsAtMs >= 0) context.Publish(new SkillCastEnded(chase.Player.Id, cast.Skill.Id, result));
        if (refusal is not null) context.Publish(new SkillRefused(chase.Player.Id, cast.Skill.Id, cast.Target, refusal));
        Drop(chase);
        return false;
    }

    private sealed class PlayerCast(CompiledSkill skill, int level, EntityId? target, GridCell? cell, bool self)
    {
        public readonly CompiledSkill Skill = skill;
        public readonly int Level = level;
        public readonly EntityId? Target = target;
        public readonly GridCell? Cell = cell;
        public readonly bool Self = self;
        public long EndsAtMs = -1;   // -1: not casting yet (walking into range)
        public int CastMs;
    }
}
