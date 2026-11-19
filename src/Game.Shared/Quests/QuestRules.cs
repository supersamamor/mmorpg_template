using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Shared.Quests
{
    /// <summary>
    /// One active quest of one character: <c>{questId, stepIndex, counters}</c> (ARCHITECTURE §6.4). <see cref="Counters"/> has one value per objective of the
    /// current step, never above its need. Changed only by <see cref="QuestRules"/>. Not thread-safe; persistence (T-702) maps it to and from storage.
    /// </summary>
    public sealed class QuestProgress
    {
        /// <summary>Creates progress at step 0.</summary>
        public QuestProgress(string questId, int objectives)
        {
            QuestId = questId;
            Counters = new int[objectives];
        }

        /// <summary>The quest.</summary>
        public string QuestId { get; }

        /// <summary>The current step, 0-based.</summary>
        public int Step { get; internal set; }

        /// <summary>One value per objective of the current step (kills, uses, items in the bag, the base level, 0/1 for talk, reach, job and equip).</summary>
        public int[] Counters { get; internal set; }

        /// <summary>
        /// Not persisted: a "Quest reasons" term while the met step waits for its turn-in (the bag has no room for its items); null otherwise.
        /// Set and cleared by the host, never by the rules.
        /// </summary>
        public string? Held { get; set; }

        /// <summary>Not persisted: the inventory transaction the host is waiting for, or 0.</summary>
        public long Token { get; set; }
    }

    /// <summary>What the objectives read about the player besides its progression (the server adapts its stores; tests use a plain object).</summary>
    public interface IQuestPlayer
    {
        /// <summary>How many of the item the bag holds, worn pieces excluded.</summary>
        int ItemCount(string itemId);

        /// <summary>True when an entry of the item is worn.</summary>
        bool IsWearing(string itemId);

        /// <summary>The map the player is on.</summary>
        string MapId { get; }

        /// <summary>The player's cell.</summary>
        GridCell Cell { get; }
    }

    /// <summary>How a character stands with one quest, for the dialogue conditions (T-602).</summary>
    public readonly struct QuestStanding
    {
        /// <summary>Creates a standing.</summary>
        public QuestStanding(string state, int step, bool available)
        {
            State = state;
            Step = step;
            Available = available;
        }

        /// <summary>A "Quest states" term other than <c>available</c>: <c>not_started</c>, <c>active</c> or <c>completed</c>.</summary>
        public string State { get; }

        /// <summary>The current step counted from 1 while active; 0 otherwise.</summary>
        public int Step { get; }

        /// <summary>True when a start would be accepted now (<see cref="QuestRules.CheckStart"/>).</summary>
        public bool Available { get; }
    }

    /// <summary>A quest's rewards, split by who pays them: the inventory (items, currency) and the progression host (the rest).</summary>
    public sealed class QuestRewardSplit
    {
        /// <summary>Items to give, merged by item id in content order.</summary>
        public List<KeyValuePair<string, int>> Items { get; } = new List<KeyValuePair<string, int>>();

        /// <summary>Currency to add.</summary>
        public long Currency { get; set; }

        /// <summary>Base EXP.</summary>
        public long BaseExp { get; set; }

        /// <summary>Job EXP.</summary>
        public long JobExp { get; set; }

        /// <summary>Unspent stat points.</summary>
        public int StatPoints { get; set; }

        /// <summary>Unspent skill points.</summary>
        public int SkillPoints { get; set; }

        /// <summary>Jobs unlocked.</summary>
        public List<string> UnlockJobs { get; } = new List<string>();

        /// <summary>Statuses applied, as indices into the quest's rewards (their compiled effects are keyed by <see cref="QuestRules.RewardKey"/>).</summary>
        public List<int> StatusRewards { get; } = new List<int>();
    }

    /// <summary>
    /// The quest state machine (ARCHITECTURE §6.4, T-602): pure and deterministic over a <see cref="ProgressionState"/> and an <see cref="IQuestPlayer"/>. The server's quest
    /// system feeds it what the event bus says; it decides starts, counters, step order, completion and markers.
    /// </summary>
    /// <remarks>
    /// <para><b>Counting.</b> Objectives count only while their step is current. <c>kill</c> and <c>use_item</c> count events; <c>collect</c> is the bag's quantity (live, so
    /// dropping items lowers it); <c>level</c> is the base level (met when the step starts if already reached); <c>job</c> is the current job; <c>reach</c> and <c>equip</c> latch
    /// once the player stands within the radius or wears the item (both are checked when the step starts too).</para>
    /// <para><b>Turning in.</b> A <c>talk</c> objective counts only when every other objective of its step is met: talking to the NPC is the step's turn-in, so a step with a
    /// <c>talk</c> completes the moment the player talks with everything else done. A step completes when every objective is met.</para>
    /// </remarks>
    public sealed class QuestRules
    {
        private readonly IReadOnlyDictionary<string, QuestDef> _quests;
        private readonly List<QuestDef> _ordered;

        /// <summary>Creates the rules over every quest; <paramref name="maxActive"/> is <c>quests.max_active</c>.</summary>
        public QuestRules(IEnumerable<QuestDef> quests, int maxActive)
        {
            if (quests == null) throw new ArgumentNullException(nameof(quests));
            var map = new Dictionary<string, QuestDef>(StringComparer.Ordinal);
            foreach (var quest in quests) map[quest.Id] = quest;
            _quests = map;
            _ordered = new List<QuestDef>(map.Values);
            _ordered.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            MaxActive = Math.Max(1, maxActive);
        }

        /// <summary><c>quests.max_active</c>.</summary>
        public int MaxActive { get; }

        /// <summary>Every quest, by id.</summary>
        public IReadOnlyList<QuestDef> All => _ordered;

        /// <summary>The key a <c>status</c> reward's compiled effect is stored under: reward <paramref name="index"/> of the quest.</summary>
        public static string RewardKey(string questId, int index) => questId + "#reward#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Finds a quest.</summary>
        public bool TryGet(string questId, out QuestDef quest) => _quests.TryGetValue(questId, out quest!);

        // ---- starting and abandoning --------------------------------------------------------------------------------------------------

        /// <summary>
        /// Why <paramref name="questId"/> cannot start now, or null when it can. Checked in this order: the quest exists, it is not active, it is not completed (unless
        /// <c>repeatable</c>), the base level, the job, the prerequisite quests, then <c>quests.max_active</c>.
        /// </summary>
        public string? CheckStart(ProgressionState state, string questId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (questId == null || !_quests.TryGetValue(questId, out var quest)) return QuestReasons.UnknownQuest;
            if (state.ActiveQuests.ContainsKey(questId)) return QuestReasons.AlreadyActive;
            if (!quest.Repeatable && state.CompletedQuests.Contains(questId)) return QuestReasons.AlreadyCompleted;
            var pre = quest.Prerequisites;
            if (state.BaseLevel < pre.MinBaseLevel) return QuestReasons.BaseLevelTooLow;
            if (pre.JobIds.Count > 0 && !Contains(pre.JobIds, state.JobId)) return QuestReasons.WrongJob;
            for (var i = 0; i < pre.QuestIds.Count; i++)
                if (!state.CompletedQuests.Contains(pre.QuestIds[i])) return QuestReasons.QuestNotCompleted;
            if (state.ActiveQuests.Count >= MaxActive) return QuestReasons.TooManyActive;
            return null;
        }

        /// <summary>Starts a quest that <see cref="CheckStart"/> accepted (it throws otherwise) at its first step, counting what is already true (level, job, bag, place, gear).</summary>
        public QuestProgress Start(ProgressionState state, string questId, IQuestPlayer player)
        {
            var reason = CheckStart(state, questId);
            if (reason != null) throw new InvalidOperationException("Quest '" + questId + "' cannot start: " + reason + ".");
            var quest = _quests[questId];
            var progress = new QuestProgress(questId, quest.Steps.Count > 0 ? quest.Steps[0].Objectives.Count : 0);
            state.ActiveQuests[questId] = progress;
            Refresh(progress, state, player);
            return progress;
        }

        /// <summary>Drops an active quest: its progress is gone and nothing comes back (the items a step would consume stay). False when it was not active.</summary>
        public bool Abandon(ProgressionState state, string questId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return questId != null && state.ActiveQuests.Remove(questId);
        }

        /// <summary>How the character stands with a quest (for <c>quest_state</c> / <c>quest_step</c> dialogue conditions).</summary>
        public QuestStanding Standing(ProgressionState state, string questId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var available = CheckStart(state, questId) == null;
            if (state.ActiveQuests.TryGetValue(questId, out var progress)) return new QuestStanding(QuestStates.Active, progress.Step + 1, available);
            if (state.CompletedQuests.Contains(questId)) return new QuestStanding(QuestStates.Completed, 0, available);
            return new QuestStanding(QuestStates.NotStarted, 0, available);
        }

        /// <summary>True when a <c>quest_state</c> term holds for the standing.</summary>
        public static bool IsInState(QuestStanding standing, string state) =>
            state == QuestStates.Available ? standing.Available : string.Equals(standing.State, state, StringComparison.Ordinal);

        // ---- counting -------------------------------------------------------------------------------------------------------------------

        /// <summary>The current step's objectives, or an empty list for a quest that has none.</summary>
        public IReadOnlyList<QuestObjectiveDef> Objectives(QuestProgress progress)
        {
            var quest = _quests[progress.QuestId];
            return progress.Step < quest.Steps.Count ? quest.Steps[progress.Step].Objectives : Empty.List<QuestObjectiveDef>();
        }

        /// <summary>What completes an objective: its count, the base level, or 1.</summary>
        public static int Need(QuestObjectiveDef objective)
        {
            switch (objective)
            {
                case KillObjective k: return Math.Max(1, k.Count);
                case CollectObjective c: return Math.Max(1, c.Count);
                case UseItemObjective u: return Math.Max(1, u.Count);
                case LevelObjective l: return Math.Max(1, l.BaseLevel);
                default: return 1;
            }
        }

        /// <summary>What an objective is about, for its label (a monster, item, NPC, job or map id); null for <c>level</c>.</summary>
        public static string? TargetOf(QuestObjectiveDef objective)
        {
            switch (objective)
            {
                case KillObjective k: return k.MonsterId;
                case CollectObjective c: return c.ItemId;
                case TalkObjective t: return t.NpcId;
                case ReachObjective r: return r.MapId;
                case JobObjective j: return j.JobId;
                case UseItemObjective u: return u.ItemId;
                case EquipObjective e: return e.ItemId;
                default: return null;
            }
        }

        /// <summary>A monster the player has kill credit for died: counts every <c>kill</c> objective of the current step for it. True when a counter moved.</summary>
        public bool OnKill(QuestProgress progress, string monsterId) => Bump(progress, o => o is KillObjective k && k.MonsterId == monsterId);

        /// <summary>The player used an item: counts every <c>use_item</c> objective of the current step for it.</summary>
        public bool OnUse(QuestProgress progress, string itemId) => Bump(progress, o => o is UseItemObjective u && u.ItemId == itemId);

        /// <summary>The player put an item on: latches every <c>equip</c> objective of the current step for it.</summary>
        public bool OnEquip(QuestProgress progress, string itemId) => Bump(progress, o => o is EquipObjective e && e.ItemId == itemId);

        /// <summary>
        /// The player started a dialogue with the NPC: latches each <c>talk</c> objective of the current step for it, but only when every other objective of the step is
        /// met (talking is the turn-in). True when a counter moved.
        /// </summary>
        public bool OnTalk(QuestProgress progress, string npcId)
        {
            var objectives = Objectives(progress);
            var changed = false;
            for (var i = 0; i < objectives.Count; i++)
            {
                if (!(objectives[i] is TalkObjective t) || t.NpcId != npcId || progress.Counters[i] >= 1) continue;
                if (!OthersMet(progress, objectives, i)) continue;
                progress.Counters[i] = 1;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Re-reads the live objectives of the current step: <c>collect</c> (the bag), <c>level</c>, <c>job</c>, and latches <c>reach</c> (the player within the radius on the map)
        /// and <c>equip</c> (worn). True when a counter moved.
        /// </summary>
        public bool Refresh(QuestProgress progress, ProgressionState state, IQuestPlayer player)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (player == null) throw new ArgumentNullException(nameof(player));
            var objectives = Objectives(progress);
            var changed = false;
            for (var i = 0; i < objectives.Count; i++)
            {
                var value = progress.Counters[i];
                switch (objectives[i])
                {
                    case CollectObjective c: value = Math.Min(Need(c), player.ItemCount(c.ItemId)); break;
                    case LevelObjective l: value = Math.Min(Need(l), state.BaseLevel); break;
                    case JobObjective j: value = string.Equals(state.JobId, j.JobId, StringComparison.Ordinal) ? 1 : 0; break;
                    case ReachObjective r:
                        if (value == 0 && string.Equals(player.MapId, r.MapId, StringComparison.Ordinal) &&
                            GridDistance.Chebyshev(player.Cell, new GridCell(r.X, r.Y)) <= Math.Max(0, r.Radius)) value = 1;
                        break;
                    case EquipObjective e:
                        if (value == 0 && player.IsWearing(e.ItemId)) value = 1;
                        break;
                }
                if (value == progress.Counters[i]) continue;
                progress.Counters[i] = value;
                changed = true;
            }
            return changed;
        }

        /// <summary>True when every objective of the current step is met (a step without objectives is met at once).</summary>
        public bool StepMet(QuestProgress progress)
        {
            var objectives = Objectives(progress);
            for (var i = 0; i < objectives.Count; i++)
                if (progress.Counters[i] < Need(objectives[i])) return false;
            return true;
        }

        /// <summary>True when the current step is the last.</summary>
        public bool IsLastStep(QuestProgress progress) => progress.Step >= _quests[progress.QuestId].Steps.Count - 1;

        /// <summary>The items the current step takes when it completes: its <c>collect</c> objectives with <c>consume</c>, merged by item id.</summary>
        public List<KeyValuePair<string, int>> Consumed(QuestProgress progress)
        {
            var list = new List<KeyValuePair<string, int>>();
            foreach (var objective in Objectives(progress))
                if (objective is CollectObjective { Consume: true } c) Add(list, c.ItemId, Need(c));
            return list;
        }

        /// <summary>Splits a quest's rewards by who pays them.</summary>
        public QuestRewardSplit Rewards(string questId)
        {
            var split = new QuestRewardSplit();
            var rewards = _quests[questId].Rewards;
            for (var i = 0; i < rewards.Count; i++)
            {
                switch (rewards[i])
                {
                    case ExpReward e:
                        split.BaseExp += Math.Max(0, e.Base);
                        split.JobExp += Math.Max(0, e.Job);
                        break;
                    case ItemReward item: Add(split.Items, item.ItemId, Math.Max(1, item.Count)); break;
                    case CurrencyReward c: split.Currency += Math.Max(0, c.Amount); break;
                    case SkillPointReward s: split.SkillPoints += Math.Max(0, s.Amount); break;
                    case StatPointReward s: split.StatPoints += Math.Max(0, s.Amount); break;
                    case UnlockJobReward u:
                        if (!split.UnlockJobs.Contains(u.JobId)) split.UnlockJobs.Add(u.JobId);
                        break;
                    case StatusReward _: split.StatusRewards.Add(i); break;
                }
            }
            return split;
        }

        /// <summary>
        /// The current step was turned in (its items taken, and for the last step every reward paid): moves to the next step and counts what is already true there, or
        /// completes the quest (it leaves the active quests and joins <c>CompletedQuests</c>). Returns true when the quest is completed.
        /// </summary>
        public bool Advance(ProgressionState state, QuestProgress progress, IQuestPlayer player)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            progress.Held = null;
            if (IsLastStep(progress))
            {
                state.ActiveQuests.Remove(progress.QuestId);
                state.CompletedQuests.Add(progress.QuestId);
                return true;
            }
            progress.Step++;
            progress.Counters = new int[_quests[progress.QuestId].Steps[progress.Step].Objectives.Count];
            Refresh(progress, state, player);
            return false;
        }

        // ---- what the owner sees --------------------------------------------------------------------------------------------------------

        /// <summary>The wire record of an active quest: its step, each objective with have and need, and why it is held.</summary>
        public QuestEntryRecord Entry(QuestProgress progress)
        {
            var objectives = Objectives(progress);
            var records = new QuestObjectiveRecord[objectives.Count];
            for (var i = 0; i < objectives.Count; i++)
                records[i] = new QuestObjectiveRecord(objectives[i].Type, TargetOf(objectives[i]), Math.Min(progress.Counters[i], Need(objectives[i])), Need(objectives[i]));
            return new QuestEntryRecord(progress.QuestId, progress.Step, records, progress.Held);
        }

        /// <summary>
        /// The NPC markers for the character: <c>offer</c> for the giver of every quest that could start now, <c>turn_in</c> for the NPC of every unmet <c>talk</c> objective
        /// whose other objectives are met. Each list sorted, without duplicates.
        /// </summary>
        public void Markers(ProgressionState state, SortedSet<string> offer, SortedSet<string> turnIn)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            offer.Clear();
            turnIn.Clear();
            foreach (var quest in _ordered)
                if (!string.IsNullOrEmpty(quest.GiverNpcId) && CheckStart(state, quest.Id) == null) offer.Add(quest.GiverNpcId!);
            foreach (var progress in state.ActiveQuests.Values)
            {
                var objectives = Objectives(progress);
                for (var i = 0; i < objectives.Count; i++)
                    if (objectives[i] is TalkObjective t && progress.Counters[i] < 1 && OthersMet(progress, objectives, i)) turnIn.Add(t.NpcId);
            }
        }

        // ---- helpers --------------------------------------------------------------------------------------------------------------------

        private bool Bump(QuestProgress progress, Func<QuestObjectiveDef, bool> matches)
        {
            var objectives = Objectives(progress);
            var changed = false;
            for (var i = 0; i < objectives.Count; i++)
            {
                if (!matches(objectives[i])) continue;
                var need = Need(objectives[i]);
                if (progress.Counters[i] >= need) continue;
                progress.Counters[i]++;
                changed = true;
            }
            return changed;
        }

        private static bool OthersMet(QuestProgress progress, IReadOnlyList<QuestObjectiveDef> objectives, int talk)
        {
            for (var j = 0; j < objectives.Count; j++)
                if (j != talk && !(objectives[j] is TalkObjective) && progress.Counters[j] < Need(objectives[j])) return false;
            return true;
        }

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (var i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void Add(List<KeyValuePair<string, int>> list, string itemId, int quantity)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Key != itemId) continue;
                list[i] = new KeyValuePair<string, int>(itemId, list[i].Value + quantity);
                return;
            }
            list.Add(new KeyValuePair<string, int>(itemId, quantity));
        }
    }
}
