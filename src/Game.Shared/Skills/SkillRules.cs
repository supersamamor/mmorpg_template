using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Progression;
using Game.Shared.Stats;
using Game.Shared.Vocabulary;

namespace Game.Shared.Skills
{
    /// <summary>
    /// Pure rules of player skills (ARCHITECTURE §6.5, T-502): the reason terms of learning and job change, the weapon rule, and passives.
    /// </summary>
    public static class SkillRules
    {
        /// <summary>The "Skill reasons" term of a refused skill point spend.</summary>
        public static string ReasonFor(SpendError error)
        {
            switch (error)
            {
                case SpendError.UnknownSkill: return SkillReasons.UnknownSkill;
                case SpendError.NotInSkillTree: return SkillReasons.NotInTree;
                case SpendError.AtMaxSkillLevel: return SkillReasons.AtMaxLevel;
                case SpendError.PrerequisiteNotMet: return SkillReasons.PrerequisiteNotMet;
                case SpendError.NotEnoughPoints: return SkillReasons.NotEnoughPoints;
                default: return SkillReasons.InvalidTarget;
            }
        }

        /// <summary>The "Skill reasons" term of a job change failure.</summary>
        public static string ReasonFor(JobChangeFailureKind kind)
        {
            switch (kind)
            {
                case JobChangeFailureKind.UnknownJob: return SkillReasons.UnknownJob;
                case JobChangeFailureKind.SameJob: return SkillReasons.SameJob;
                case JobChangeFailureKind.WrongParentJob: return SkillReasons.WrongParentJob;
                case JobChangeFailureKind.BaseLevelTooLow: return SkillReasons.BaseLevelTooLow;
                case JobChangeFailureKind.JobLevelTooLow: return SkillReasons.JobLevelTooLow;
                case JobChangeFailureKind.QuestNotCompleted: return SkillReasons.QuestNotCompleted;
                default: return SkillReasons.NotUnlocked;
            }
        }

        /// <summary>The first failure's reason of a refused job change (the order of <see cref="ProgressionRules.CanChangeJob"/>).</summary>
        public static string ReasonFor(JobChangeCheck check) =>
            check.Failures.Count == 0 ? SkillReasons.InvalidTarget : ReasonFor(check.Failures[0].Kind);

        /// <summary>True when the skill has no weapon restriction or <paramref name="weaponType"/> (<c>unarmed</c> without a weapon) is listed.</summary>
        public static bool WeaponAllowed(SkillDef skill, string weaponType)
        {
            if (skill.WeaponTypes.Count == 0) return true;
            for (var i = 0; i < skill.WeaponTypes.Count; i++)
                if (string.Equals(skill.WeaponTypes[i], weaponType, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The modifier source of a passive skill: <c>skill:&lt;id&gt;</c>.</summary>
        public static string PassiveSource(string skillId) => "skill:" + skillId;

        /// <summary>
        /// Brings the sheet's passive modifiers in line with the learned levels: for every learned passive skill the <c>skill:&lt;id&gt;</c> modifiers are
        /// removed and added again with the skill's level (so a level change updates them). Values are formulas of <c>level</c> and the sheet's own variables.
        /// </summary>
        public static void ApplyPassives(StatSheet sheet, IReadOnlyDictionary<string, int> learned, IProgressionContent content, FormulaContext scratch)
        {
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));
            foreach (var pair in learned)
            {
                if (!content.TryGetSkill(pair.Key, out var skill) || skill == null || !skill.Passive) continue;
                ApplyPassive(sheet, skill, pair.Value, scratch);
            }
        }

        /// <summary>One passive skill at <paramref name="level"/> (0 removes it).</summary>
        public static void ApplyPassive(StatSheet sheet, SkillDef skill, int level, FormulaContext scratch)
        {
            var source = PassiveSource(skill.Id);
            sheet.Modifiers.RemoveBySource(source);
            if (level <= 0) return;
            scratch.Clear();
            sheet.WriteTo(scratch);
            scratch[FormulaSymbols.Default.SlotOf(FormulaVariables.SkillLevel)] = level;
            for (var i = 0; i < skill.PassiveModifiers.Count; i++)
                sheet.Modifiers.Add(source, CompiledModifier.Compile(skill.PassiveModifiers[i]), scratch);
        }
    }

    /// <summary>
    /// A player's skill cooldowns, in the simulated time of the map it is on. Combat keeps <see cref="NowMs"/> current; moving to another map
    /// instance (its own clock) goes through <see cref="Suspend"/> and <see cref="Resume"/>, so a warp neither resets nor lengthens a cooldown.
    /// </summary>
    public sealed class SkillCooldowns
    {
        private readonly Dictionary<string, long> _ready = new Dictionary<string, long>(StringComparer.Ordinal);
        private bool _suspended;

        /// <summary>The map clock at the last update.</summary>
        public long NowMs { get; private set; }

        /// <summary>Skills with a running or stored cooldown.</summary>
        public int Count => _ready.Count;

        /// <summary>Sets the map clock; drops cooldowns that are over.</summary>
        public void Advance(long nowMs)
        {
            NowMs = nowMs;
            if (_suspended || _ready.Count == 0) return;
            List<string>? over = null;
            foreach (var pair in _ready)
                if (pair.Value <= nowMs) (over ??= new List<string>()).Add(pair.Key);
            if (over != null)
                foreach (var id in over) _ready.Remove(id);
        }

        /// <summary>True when the skill may be used (no cooldown left).</summary>
        public bool IsReady(string skillId) => LeftMs(skillId) <= 0;

        /// <summary>Milliseconds left on the skill's cooldown (0 when ready).</summary>
        public long LeftMs(string skillId)
        {
            if (!_ready.TryGetValue(skillId, out var ready)) return 0;
            return _suspended ? ready : Math.Max(0, ready - NowMs);
        }

        /// <summary>Starts the skill's cooldown now.</summary>
        public void Start(string skillId, long durationMs)
        {
            if (durationMs <= 0) _ready.Remove(skillId);
            else _ready[skillId] = NowMs + durationMs;
        }

        /// <summary>Leaving a map: the cooldowns become times left.</summary>
        public void Suspend()
        {
            if (_suspended) return;
            _suspended = true;
            var ids = new List<string>(_ready.Keys);
            foreach (var id in ids) _ready[id] = Math.Max(0, _ready[id] - NowMs);
        }

        /// <summary>Arriving on a map whose clock reads <paramref name="nowMs"/>: the times left count from there.</summary>
        public void Resume(long nowMs)
        {
            NowMs = nowMs;
            if (!_suspended) return;
            _suspended = false;
            var ids = new List<string>(_ready.Keys);
            foreach (var id in ids) _ready[id] = nowMs + _ready[id];
        }

        /// <summary>Every cooldown still running as (skill, ms left), in id order.</summary>
        public List<KeyValuePair<string, long>> Running()
        {
            var list = new List<KeyValuePair<string, long>>();
            foreach (var pair in _ready)
            {
                var left = LeftMs(pair.Key);
                if (left > 0) list.Add(new KeyValuePair<string, long>(pair.Key, left));
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }
    }
}
