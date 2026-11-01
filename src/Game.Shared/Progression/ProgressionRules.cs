using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Vocabulary;

namespace Game.Shared.Progression
{
    /// <summary>
    /// Leveling, stat/skill point spending and job change rules (ARCHITECTURE §6.5). Deterministic and I/O-free: all numbers
    /// come from <c>balance.yaml</c> (<see cref="BalanceDef"/>), EXP tables and job/skill definitions. Operations change a
    /// <see cref="ProgressionState"/> only on success and return the events they caused as values.
    /// Formulas are compiled once in the constructor. Not thread-safe (it reuses formula contexts).
    /// </summary>
    /// <remarks>
    /// <para><b>EXP at the cap.</b> At the max base level (EXP table length + 1; the job's own table for job levels) EXP is
    /// <i>discarded</i>: <c>BaseExp</c>/<c>JobExp</c> stay 0 and the dropped amount is reported in
    /// <see cref="ExpGainResult.ExpDiscarded"/>. When a grant carries the character to the cap, the remainder past the last
    /// level-up is discarded the same way. So stored EXP is always below the next level's requirement, and 0 at the cap.</para>
    /// <para><b>Points.</b> Reaching level <c>n</c> awards the formula evaluated with <c>base_level = n</c> (stat points) or
    /// <c>job_level = n</c> (skill points), floored and never negative. A job change resets job level and job EXP to
    /// 1/0, keeps unspent skill points and learned skills.</para>
    /// <para><b>Skills.</b> One level per call, one skill point per level. A skill is learnable if it is in the current job's tree
    /// or an ancestor job's (parent chain). Max level is the smaller of the tree entry's and the skill's. Prerequisites are the
    /// tree entry's <c>requires</c>. Stat raises are steps of +1; the cost of each step is a formula of the value before it.</para>
    /// <para><b>Stats pipeline.</b> After a job level change the host re-adds job stat bonuses to the <c>StatSheet</c>
    /// (<c>RemoveBySource("job:id")</c>, add again with the new <c>job_level</c>) and sets <c>Base</c> from this state;
    /// the same for passive skill modifiers when a skill level changes.</para>
    /// </remarks>
    public sealed class ProgressionRules
    {
        private readonly IProgressionContent _content;
        private readonly ProgressionBalanceDef _balance;
        private readonly ExpTableDef _baseTable;
        private readonly Formula _statPointsPerLevel;
        private readonly Formula _skillPointsPerLevel;
        private readonly Formula _statCost;
        private readonly FormulaContext _baseLevelContext = new FormulaContext(FormulaSymbols.BaseLevelOnly);
        private readonly FormulaContext _jobLevelContext = new FormulaContext(FormulaSymbols.JobLevelOnly);
        private readonly FormulaContext _statValueContext = new FormulaContext(FormulaSymbols.StatValueOnly);

        /// <summary>Creates the rules. Throws if balance formulas do not compile or the base EXP table is missing (validated content never does).</summary>
        /// <param name="balance">Content <c>balance.yaml</c>.</param>
        /// <param name="content">Definition lookups.</param>
        public ProgressionRules(BalanceDef balance, IProgressionContent content)
        {
            if (balance == null) throw new ArgumentNullException(nameof(balance));
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _balance = balance.Progression;
            if (!content.TryGetExpTable(_balance.BaseExpTableId, out var baseTable) || baseTable == null)
                throw new ArgumentException("Base EXP table '" + _balance.BaseExpTableId + "' not found.", nameof(content));
            _baseTable = baseTable;
            _statPointsPerLevel = Formula.Compile(_balance.StatPointsPerBaseLevel, FormulaSymbols.BaseLevelOnly);
            _skillPointsPerLevel = Formula.Compile(_balance.SkillPointsPerJobLevel, FormulaSymbols.JobLevelOnly);
            _statCost = Formula.Compile(_balance.StatPointCost, FormulaSymbols.StatValueOnly);
        }

        /// <summary>Highest base level: base EXP table length + 1.</summary>
        public int MaxBaseLevel => _baseTable.Exp.Count + 1;

        /// <summary>Highest job level of a job (its EXP table length + 1); 0 if the job or table is unknown.</summary>
        /// <param name="jobId">Job id.</param>
        public int MaxJobLevel(string jobId)
        {
            var table = JobTable(jobId);
            return table == null ? 0 : table.Exp.Count + 1;
        }

        /// <summary>Creates a new character in <paramref name="jobId"/>: level 1/1, starting points, every primary stat at its start value.</summary>
        /// <param name="jobId">Starting job id.</param>
        public ProgressionState NewCharacter(string jobId)
        {
            var state = new ProgressionState { JobId = jobId, StatPoints = _balance.StartingStatPoints };
            foreach (var stat in PrimaryStats.All.Ordered) state.Stats[stat] = _balance.StartingStatValue;
            return state;
        }

        /// <summary>Stat points awarded for reaching base level <paramref name="level"/>.</summary>
        /// <param name="level">The level just reached.</param>
        public int StatPointsForBaseLevel(int level)
        {
            _baseLevelContext[FormulaVariables.BaseLevel] = level;
            return ToPoints(_statPointsPerLevel.Evaluate(_baseLevelContext));
        }

        /// <summary>Skill points awarded for reaching job level <paramref name="level"/>.</summary>
        /// <param name="level">The job level just reached.</param>
        public int SkillPointsForJobLevel(int level)
        {
            _jobLevelContext[FormulaVariables.JobLevel] = level;
            return ToPoints(_skillPointsPerLevel.Evaluate(_jobLevelContext));
        }

        /// <summary>Stat points needed to raise a stat from <paramref name="currentValue"/> to <c>currentValue + 1</c>.</summary>
        /// <param name="currentValue">The stat's value before the raise.</param>
        public int StatRaiseCost(int currentValue)
        {
            _statValueContext[FormulaVariables.StatValue] = currentValue;
            return ToPoints(_statCost.Evaluate(_statValueContext));
        }

        /// <summary>Adds base EXP, levelling up as many times as it pays for. See the class remarks for cap handling.</summary>
        /// <param name="state">The character; changed in place.</param>
        /// <param name="amount">EXP to add; zero or negative does nothing.</param>
        public ExpGainResult GainBaseExp(ProgressionState state, long amount)
        {
            return Gain(state, amount, _baseTable, true);
        }

        /// <summary>
        /// Takes base EXP away (the death penalty). Never below 0 EXP within the current level, so it never loses a level or EXP gathered
        /// toward an earlier one; the base EXP at the max level is always 0, so nothing is lost there.
        /// </summary>
        /// <param name="state">The character; changed in place.</param>
        /// <param name="amount">EXP to take; zero or negative does nothing.</param>
        /// <returns>The EXP actually lost, between 0 and <paramref name="amount"/>.</returns>
        public long LoseBaseExp(ProgressionState state, long amount)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (amount <= 0 || state.BaseExp <= 0) return 0;
            var lost = Math.Min(amount, state.BaseExp);
            state.BaseExp -= lost;
            return lost;
        }

        /// <summary>Adds job EXP for the current job, levelling up as many times as it pays for. See the class remarks for cap handling.</summary>
        /// <param name="state">The character; changed in place.</param>
        /// <param name="amount">EXP to add; zero or negative does nothing.</param>
        public ExpGainResult GainJobExp(ProgressionState state, long amount)
        {
            var table = JobTable(state.JobId);
            if (table == null) return new ExpGainResult(0, 0, 0, Array.Empty<IProgressionEvent>());
            return Gain(state, amount, table, false);
        }

        private ExpGainResult Gain(ProgressionState state, long amount, ExpTableDef table, bool isBase)
        {
            var events = new List<IProgressionEvent>();
            var level = isBase ? state.BaseLevel : state.JobLevel;
            var exp = isBase ? state.BaseExp : state.JobExp;
            var max = table.Exp.Count + 1;
            var gained = 0;
            var points = 0;
            long discarded = 0;
            if (amount > 0)
            {
                // Subtract level costs from the grant instead of summing, so a huge grant cannot overflow long.
                var remaining = amount;
                while (level < max)
                {
                    var need = table.Exp[level - 1] - exp;
                    if (remaining < need)
                    {
                        exp += remaining;
                        remaining = 0;
                        break;
                    }
                    remaining -= need;
                    exp = 0;
                    level++;
                    gained++;
                    if (isBase)
                    {
                        var award = StatPointsForBaseLevel(level);
                        points += award;
                        events.Add(new LevelUp(level, award));
                    }
                    else
                    {
                        var award = SkillPointsForJobLevel(level);
                        points += award;
                        events.Add(new JobLevelUp(state.JobId, level, award));
                    }
                }
                if (level >= max)
                {
                    discarded = remaining;
                    exp = 0;
                }
            }

            if (isBase)
            {
                state.BaseLevel = level;
                state.BaseExp = exp;
                state.StatPoints += points;
            }
            else
            {
                state.JobLevel = level;
                state.JobExp = exp;
                state.SkillPoints += points;
            }
            return new ExpGainResult(gained, points, discarded, events);
        }

        /// <summary>Raises a primary stat by <paramref name="amount"/> steps of one, spending the cost of each step. All or nothing.</summary>
        /// <param name="state">The character; changed only on success.</param>
        /// <param name="statId">Primary stat id.</param>
        /// <param name="amount">Number of +1 steps (at least 1).</param>
        public SpendResult RaiseStat(ProgressionState state, string statId, int amount = 1)
        {
            if (amount < 1) return Fail(SpendError.InvalidAmount, 0);
            if (statId == null || !PrimaryStats.All.Contains(statId)) return Fail(SpendError.UnknownStat, 0);
            var value = state.Stats.TryGetValue(statId, out var v) ? v : _balance.StartingStatValue;
            if ((long)value + amount > _balance.MaxStatValue) return Fail(SpendError.AtMaxStatValue, value);
            long cost = 0;
            for (var i = 0; i < amount; i++) cost += StatRaiseCost(value + i);
            if (cost > state.StatPoints) return Fail(SpendError.NotEnoughPoints, value);
            state.StatPoints -= (int)cost;
            state.Stats[statId] = value + amount;
            return new SpendResult(SpendError.None, (int)cost, value + amount, Array.Empty<IProgressionEvent>());
        }

        /// <summary>Learns a skill or raises it by one level, spending one skill point.</summary>
        /// <param name="state">The character; changed only on success.</param>
        /// <param name="skillId">Skill id.</param>
        public SpendResult LearnSkill(ProgressionState state, string skillId)
        {
            if (skillId == null || !_content.TryGetSkill(skillId, out var skill) || skill == null)
                return Fail(SpendError.UnknownSkill, 0);
            var current = state.SkillLevel(skillId);
            var entry = FindTreeEntry(state.JobId, skillId);
            if (entry == null) return Fail(SpendError.NotInSkillTree, current);
            var max = Math.Min(skill.MaxLevel, entry.MaxLevel ?? skill.MaxLevel);
            if (current >= max) return Fail(SpendError.AtMaxSkillLevel, current);
            foreach (var req in entry.Requires)
                if (state.SkillLevel(req.SkillId) < req.Level) return Fail(SpendError.PrerequisiteNotMet, current);
            if (state.SkillPoints < 1) return Fail(SpendError.NotEnoughPoints, current);
            state.SkillPoints--;
            state.Skills[skillId] = current + 1;
            return new SpendResult(SpendError.None, 1, current + 1, new IProgressionEvent[] { new SkillLearned(skillId, current + 1) });
        }

        /// <summary>Checks the requirements for changing into <paramref name="targetJobId"/>, returning every failing reason.</summary>
        /// <param name="state">The character; not changed.</param>
        /// <param name="targetJobId">Target job id.</param>
        public JobChangeCheck CanChangeJob(ProgressionState state, string targetJobId)
        {
            var failures = new List<JobChangeFailure>();
            if (targetJobId == null || !_content.TryGetJob(targetJobId, out var target) || target == null)
            {
                failures.Add(new JobChangeFailure(JobChangeFailureKind.UnknownJob, targetJobId ?? string.Empty));
                return new JobChangeCheck(failures);
            }
            if (string.Equals(state.JobId, targetJobId, StringComparison.Ordinal))
                failures.Add(new JobChangeFailure(JobChangeFailureKind.SameJob, targetJobId));
            if (!string.Equals(target.ParentJobId, state.JobId, StringComparison.Ordinal))
                failures.Add(new JobChangeFailure(JobChangeFailureKind.WrongParentJob, target.ParentJobId ?? string.Empty));
            var req = target.Requirements;
            if (state.BaseLevel < req.MinBaseLevel)
                failures.Add(new JobChangeFailure(JobChangeFailureKind.BaseLevelTooLow, state.BaseLevel + "/" + req.MinBaseLevel));
            if (state.JobLevel < req.MinJobLevel)
                failures.Add(new JobChangeFailure(JobChangeFailureKind.JobLevelTooLow, state.JobLevel + "/" + req.MinJobLevel));
            foreach (var quest in req.QuestIds)
                if (!state.CompletedQuests.Contains(quest))
                    failures.Add(new JobChangeFailure(JobChangeFailureKind.QuestNotCompleted, quest));
            if (req.RequiresUnlock && !state.UnlockedJobs.Contains(targetJobId))
                failures.Add(new JobChangeFailure(JobChangeFailureKind.NotUnlocked, targetJobId));
            return new JobChangeCheck(failures);
        }

        /// <summary>Changes job if <see cref="CanChangeJob"/> allows it: sets the job, resets job level/EXP to 1/0, emits <see cref="JobChanged"/>.</summary>
        /// <param name="state">The character; changed only on success.</param>
        /// <param name="targetJobId">Target job id.</param>
        /// <param name="check">The requirement check (failures when not allowed).</param>
        /// <returns>The events (one <see cref="JobChanged"/>), empty when the change was refused.</returns>
        public IReadOnlyList<IProgressionEvent> ChangeJob(ProgressionState state, string targetJobId, out JobChangeCheck check)
        {
            check = CanChangeJob(state, targetJobId);
            if (!check.Allowed) return Array.Empty<IProgressionEvent>();
            var from = state.JobId;
            state.JobId = targetJobId;
            state.JobLevel = 1;
            state.JobExp = 0;
            return new IProgressionEvent[] { new JobChanged(from, targetJobId) };
        }

        private ExpTableDef? JobTable(string jobId)
        {
            if (jobId == null || !_content.TryGetJob(jobId, out var job) || job == null) return null;
            return _content.TryGetExpTable(job.JobExpTableId, out var table) ? table : null;
        }

        private JobSkillDef? FindTreeEntry(string jobId, string skillId)
        {
            // Walk the parent chain; the guard stops a (validator-rejected) cycle from looping forever.
            string? id = jobId;
            for (var guard = 0; id != null && guard < 64; guard++)
            {
                if (!_content.TryGetJob(id, out var job) || job == null) return null;
                foreach (var entry in job.Skills)
                    if (string.Equals(entry.SkillId, skillId, StringComparison.Ordinal)) return entry;
                id = job.ParentJobId;
            }
            return null;
        }

        private static SpendResult Fail(SpendError error, int value) =>
            new SpendResult(error, 0, value, Array.Empty<IProgressionEvent>());

        private static int ToPoints(double value)
        {
            var floored = Math.Floor(value);
            if (floored < 0) return 0;
            return floored > int.MaxValue ? int.MaxValue : (int)floored;
        }
    }
}
