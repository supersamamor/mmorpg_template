using Game.Content.Registry;
using Game.Shared.Definitions;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Quests (T-602). Ids are <see cref="ReferenceRule"/>'s and reach cells <see cref="MapGridRule"/>'s; this rule checks that every quest can be played:
/// <list type="bullet">
/// <item>at least one step, each with at least one objective; counts, levels, radii and reward amounts in range;</item>
/// <item>prerequisite quests do not loop back to the quest;</item>
/// <item><b>the giver can be reached</b>: an NPC placed on a map, with a dialogue that has a <c>start_quest</c> for the quest on a node reachable from its start (a quest with no
/// giver needs some placed NPC's dialogue to start it), and every <c>start_quest</c> of the quest is in its giver's dialogue;</item>
/// <item><b>every turn-in NPC can be reached</b>: the NPC of each <c>talk</c> objective is placed on a map and has a dialogue (talking needs one).</item>
/// </list>
/// </summary>
public sealed class QuestRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "quest";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;
        var placed = new HashSet<string>(db.Grids.Values.SelectMany(g => g.Npcs).Select(n => n.NpcId), StringComparer.Ordinal);
        var starts = StartsByDialogue(db);
        var maxLevel = db.ExpTables.TryGet(db.Balance.Progression.BaseExpTableId, out var table) ? table.Exp.Count + 1 : int.MaxValue;

        foreach (var quest in db.Quests.All)
        {
            void Error(string path, string message) => context.Error(quest.Id, path, ValidationCodes.QuestInvalid, message);
            void Unreachable(string path, string message) => context.Error(quest.Id, path, ValidationCodes.QuestUnreachable, message);

            if (quest.Steps.Count == 0) Error("steps", "a quest needs at least one step");
            for (var s = 0; s < quest.Steps.Count; s++)
            {
                var step = quest.Steps[s];
                if (step.Objectives.Count == 0) Error($"steps[{s}].objectives", "a step needs at least one objective");
                for (var o = 0; o < step.Objectives.Count; o++)
                {
                    var p = $"steps[{s}].objectives[{o}]";
                    switch (step.Objectives[o])
                    {
                        case KillObjective { Count: < 1 }:
                        case CollectObjective { Count: < 1 }:
                        case UseItemObjective { Count: < 1 }:
                            Error(p + ".count", "count must be at least 1");
                            break;
                        case LevelObjective l when l.BaseLevel < 1 || l.BaseLevel > maxLevel:
                            Error(p + ".base_level", $"base_level must be from 1 to the max base level ({maxLevel})");
                            break;
                        case ReachObjective { Radius: < 0 }:
                            Error(p + ".radius", "radius must not be negative");
                            break;
                        case TalkObjective t:
                            if (!placed.Contains(t.NpcId))
                                Unreachable(p + ".npc_id", $"turn-in NPC {t.NpcId} is not placed on any map, so nobody can talk to it");
                            else if (db.Npcs.TryGet(t.NpcId, out var npc) && string.IsNullOrEmpty(npc.DialogueId))
                                Unreachable(p + ".npc_id", $"turn-in NPC {t.NpcId} has no dialogue, so a talk with it never starts");
                            break;
                    }
                }
            }

            for (var r = 0; r < quest.Rewards.Count; r++)
            {
                var p = $"rewards[{r}]";
                switch (quest.Rewards[r])
                {
                    case ExpReward e when e.Base < 0 || e.Job < 0: Error(p, "EXP must not be negative"); break;
                    case ItemReward { Count: < 1 }: Error(p + ".count", "count must be at least 1"); break;
                    case CurrencyReward { Amount: < 1 }: Error(p + ".amount", "amount must be at least 1"); break;
                    case SkillPointReward { Amount: < 1 }:
                    case StatPointReward { Amount: < 1 }:
                        Error(p + ".amount", "amount must be at least 1");
                        break;
                    case StatusReward { DurationMs: < 1 }: Error(p + ".duration_ms", "duration_ms must be above 0"); break;
                }
            }

            if (Loops(db, quest.Id)) Error("prerequisites.quest_ids", "the prerequisite quests lead back to this quest, so it can never start");

            // Who starts it: the dialogues with a start_quest for it, on a node reachable from their start, held by a placed NPC.
            var startedBy = starts.TryGetValue(quest.Id, out var list) ? list : [];
            var npcsStarting = db.Npcs.All.Where(n => n.DialogueId is { } d && startedBy.Contains(d)).Select(n => n.Id).ToList();
            if (!string.IsNullOrEmpty(quest.GiverNpcId))
            {
                var giver = quest.GiverNpcId!;
                if (!placed.Contains(giver))
                    Unreachable("giver_npc_id", $"giver {giver} is not placed on any map");
                else if (!npcsStarting.Contains(giver))
                    Unreachable("giver_npc_id", $"giver {giver} has no dialogue choice (reachable from its start) with a start_quest for {quest.Id}");
                foreach (var other in npcsStarting.Where(n => n != giver))
                    Unreachable("giver_npc_id", $"{other}'s dialogue starts {quest.Id}, but its giver is {giver}: the offer marker would be over the wrong NPC");
            }
            else if (!npcsStarting.Any(placed.Contains))
            {
                Unreachable("giver_npc_id", "no placed NPC's dialogue has a start_quest for it, so it can never start");
            }
        }
    }

    /// <summary>Quest id → the dialogues that start it on a node reachable from their start.</summary>
    private static Dictionary<string, HashSet<string>> StartsByDialogue(ContentDatabase db)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var d in db.Dialogue.All)
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            if (d.Nodes.ContainsKey(d.Start)) pending.Push(d.Start);
            while (pending.Count > 0)
            {
                var key = pending.Pop();
                if (!reached.Add(key)) continue;
                var node = d.Nodes[key];
                foreach (var choice in node.Choices)
                {
                    foreach (var action in choice.Actions)
                        if (action is StartQuestAction start)
                        {
                            if (!result.TryGetValue(start.QuestId, out var set)) result[start.QuestId] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(d.Id);
                        }
                    if (!string.IsNullOrEmpty(choice.Next) && d.Nodes.ContainsKey(choice.Next!)) pending.Push(choice.Next!);
                }
                if (!string.IsNullOrEmpty(node.Next) && d.Nodes.ContainsKey(node.Next!)) pending.Push(node.Next!);
            }
        }
        return result;
    }

    /// <summary>True when following prerequisite quests from <paramref name="questId"/> comes back to it.</summary>
    private static bool Loops(ContentDatabase db, string questId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (db.Quests.TryGet(questId, out var root)) foreach (var q in root.Prerequisites.QuestIds) pending.Push(q);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id == questId) return true;
            if (!seen.Add(id) || !db.Quests.TryGet(id, out var quest)) continue;
            foreach (var q in quest.Prerequisites.QuestIds) pending.Push(q);
        }
        return false;
    }
}
