using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Quests;
using Game.Shared.Vocabulary;

namespace Game.Shared.Autopilot
{
    /// <summary>Why a <c>start_autopilot</c> is refused: an <see cref="AutopilotReasons"/> term and the quest or skill it is about.</summary>
    public readonly struct AutopilotRefusal
    {
        /// <summary>Creates a refusal.</summary>
        public AutopilotRefusal(string reason, string? quest = null, string? skill = null)
        {
            Reason = reason;
            Quest = quest;
            Skill = skill;
        }

        /// <summary>An <see cref="AutopilotReasons"/> term.</summary>
        public string Reason { get; }

        /// <summary>The quest it is about, if any.</summary>
        public string? Quest { get; }

        /// <summary>The skill it is about, if any.</summary>
        public string? Skill { get; }
    }

    /// <summary>
    /// What the quest autopilot decides from data alone (T-604, ARCHITECTURE §6.4.2): whether a run may start, which objective is next, and which dialogue choice it may pick. Pure and
    /// deterministic: no clock, no randomness, no state. The server's <c>AutopilotSystem</c> asks here and then queues the intents a player would send.
    /// </summary>
    public static class AutopilotRules
    {
        /// <summary>Milliseconds in the day <c>autopilot.daily_limit_minutes</c> counts in.</summary>
        public const long DayMs = 24L * 60 * 60 * 1000;

        /// <summary>The daily limit in milliseconds, or <see cref="long.MaxValue"/> without one.</summary>
        public static long DailyLimitMs(AutopilotBalanceDef balance) => balance.DailyLimitMinutes <= 0 ? long.MaxValue : balance.DailyLimitMinutes * 60_000L;

        // ---- starting -----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Why a run may not start, or null when it may. Checked in the order of <see cref="AutopilotReasons"/>. <paramref name="usedMs"/> is what today's runs took so far;
        /// <paramref name="skillOf"/> finds a skill definition (null for an unknown id).
        /// </summary>
        public static AutopilotRefusal? CheckStart(AutopilotBalanceDef balance, bool dead, long usedMs, ProgressionState state, QuestRules quests, Func<string, SkillDef?> skillOf,
            IReadOnlyList<string> questIds, IReadOnlyList<SkillLevelRecord> skills)
        {
            if (!balance.Enabled) return new AutopilotRefusal(AutopilotReasons.Disabled);
            if (dead) return new AutopilotRefusal(AutopilotReasons.Dead);
            if (usedMs >= DailyLimitMs(balance)) return new AutopilotRefusal(AutopilotReasons.TimeLimit);
            if (questIds.Count == 0) return new AutopilotRefusal(AutopilotReasons.EmptyQueue);
            if (questIds.Count > balance.MaxQuests) return new AutopilotRefusal(AutopilotReasons.TooManyQuests);
            for (var i = 0; i < questIds.Count; i++)
            {
                var id = questIds[i];
                if (!quests.TryGet(id, out _)) return new AutopilotRefusal(AutopilotReasons.UnknownQuest, quest: id);
                for (var j = 0; j < i; j++)
                    if (string.Equals(questIds[j], id, StringComparison.Ordinal)) return new AutopilotRefusal(AutopilotReasons.DuplicateQuest, quest: id);
                if (!state.ActiveQuests.ContainsKey(id)) return new AutopilotRefusal(AutopilotReasons.NotActive, quest: id);
            }
            if (skills.Count > balance.MaxSkills) return new AutopilotRefusal(AutopilotReasons.TooManySkills);
            for (var i = 0; i < skills.Count; i++)
            {
                var id = skills[i].Skill;
                var def = skillOf(id);
                if (def == null) return new AutopilotRefusal(AutopilotReasons.UnknownSkill, skill: id);
                var learned = state.SkillLevel(id);
                if (learned < 1) return new AutopilotRefusal(AutopilotReasons.NotLearned, skill: id);
                if (def.Passive) return new AutopilotRefusal(AutopilotReasons.PassiveSkill, skill: id);
                if (skills[i].Level > learned) return new AutopilotRefusal(AutopilotReasons.LevelTooHigh, skill: id);
            }
            return null;
        }

        // ---- objectives ---------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The index of the objective to play now, or -1 when none is left. In list order, but first the ones the autopilot can play (<c>kill</c>, <c>collect</c>, <c>reach</c>), then the
        /// ones it must leave to the player (<see cref="PauseFor"/>), then <c>talk</c>, which only counts once everything else of the step is met.
        /// </summary>
        public static int NextObjective(IReadOnlyList<QuestObjectiveDef> objectives, IReadOnlyList<int> counters)
        {
            var best = -1;
            var bestClass = int.MaxValue;
            for (var i = 0; i < objectives.Count; i++)
            {
                if (counters[i] >= QuestRules.Need(objectives[i])) continue;
                var kind = objectives[i] is TalkObjective ? 2 : PauseFor(objectives[i]) != null ? 1 : 0;
                if (kind >= bestClass) continue;
                best = i;
                bestClass = kind;
            }
            return best;
        }

        /// <summary>The pause an objective means because only the player may do it (<see cref="AutopilotPauses"/>), or null for one the autopilot plays.</summary>
        public static string? PauseFor(QuestObjectiveDef objective)
        {
            switch (objective)
            {
                case LevelObjective _: return AutopilotPauses.NeedsLevel;
                case JobObjective _: return AutopilotPauses.NeedsJob;
                case EquipObjective _: return AutopilotPauses.NeedsEquip;
                case UseItemObjective _: return AutopilotPauses.NeedsItemUse;
                case KillObjective _:
                case CollectObjective _:
                case ReachObjective _:
                case TalkObjective _: return null;
                default: return AutopilotPauses.NoSource;   // an objective type this build cannot play
            }
        }

        // ---- dialogue -----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The choice to pick at a node, by its original index, or null when nothing is safe. <paramref name="shown"/> are the indices on screen; at a node that shows none the answer is
        /// 0 (its plain "continue") or null.
        /// </summary>
        /// <remarks>
        /// <para>A choice is <b>wanted</b> when it starts a ticked quest (it has actions, and every one of them is <c>start_quest</c> for a ticked quest) or is about turning one in (it has no
        /// action at all and one of its conditions is <c>quest_step</c>, or <c>quest_state</c> <c>active</c>, of a ticked quest). The first wanted choice on screen is picked.</para>
        /// <para>Otherwise the first <b>plain</b> choice (no action at all) that leads, over plain choices and continues only, to a node with a wanted choice. A choice with any other action
        /// (a service, effects such as a reset, a save point, an item given or taken, a quest that was not ticked) is never picked, whatever it leads to.</para>
        /// </remarks>
        public static int? ChooseDialogue(DialogueDef dialogue, string nodeKey, IReadOnlyList<int> shown, ICollection<string> ticked)
        {
            if (dialogue == null) throw new ArgumentNullException(nameof(dialogue));
            if (!dialogue.Nodes.TryGetValue(nodeKey, out var node)) return null;
            if (shown.Count == 0)
                return node.Next != null && Leads(dialogue, node.Next, ticked, new HashSet<string>(StringComparer.Ordinal) { nodeKey }) ? 0 : (int?)null;
            for (var i = 0; i < shown.Count; i++)
                if (shown[i] >= 0 && shown[i] < node.Choices.Count && IsWanted(node.Choices[shown[i]], ticked)) return shown[i];
            for (var i = 0; i < shown.Count; i++)
            {
                if (shown[i] < 0 || shown[i] >= node.Choices.Count) continue;
                var choice = node.Choices[shown[i]];
                if (choice.Actions.Count == 0 && choice.Next != null && Leads(dialogue, choice.Next, ticked, new HashSet<string>(StringComparer.Ordinal) { nodeKey })) return shown[i];
            }
            return null;
        }

        private static bool IsWanted(DialogueChoiceDef choice, ICollection<string> ticked)
        {
            if (choice.Actions.Count > 0)
            {
                for (var i = 0; i < choice.Actions.Count; i++)
                    if (!(choice.Actions[i] is StartQuestAction start) || !ticked.Contains(start.QuestId)) return false;
                return true;
            }
            for (var i = 0; i < choice.Conditions.Count; i++)
            {
                if (choice.Conditions[i] is QuestStepCondition step && ticked.Contains(step.QuestId)) return true;
                if (choice.Conditions[i] is QuestStateCondition state && state.State == QuestStates.Active && ticked.Contains(state.QuestId)) return true;
            }
            return false;
        }

        /// <summary>True when a wanted choice can be reached from the node over plain choices and continues (conditions are not looked at: the server checks them when it shows a node).</summary>
        private static bool Leads(DialogueDef dialogue, string nodeKey, ICollection<string> ticked, HashSet<string> seen)
        {
            if (!seen.Add(nodeKey) || !dialogue.Nodes.TryGetValue(nodeKey, out var node)) return false;
            for (var i = 0; i < node.Choices.Count; i++)
                if (IsWanted(node.Choices[i], ticked)) return true;
            if (node.Choices.Count == 0) return node.Next != null && Leads(dialogue, node.Next, ticked, seen);
            for (var i = 0; i < node.Choices.Count; i++)
                if (node.Choices[i].Actions.Count == 0 && node.Choices[i].Next is { } next && Leads(dialogue, next, ticked, seen)) return true;
            return false;
        }
    }
}
