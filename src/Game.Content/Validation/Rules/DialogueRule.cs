using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Dialogue graph: <c>start</c> and every <c>next</c> must name an existing node (error); nodes that cannot be reached
/// from <c>start</c> are a warning. A null <c>next</c> ends the conversation.
/// </summary>
public sealed class DialogueRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "dialogue";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        foreach (var d in context.Database.Dialogue.All)
        {
            if (!d.Nodes.ContainsKey(d.Start))
                context.Error(d.Id, "start", ValidationCodes.DialogueDangling, $"Start node '{d.Start}' does not exist in {d.Id}");

            foreach (var (key, node) in d.Nodes)
            {
                if (!string.IsNullOrEmpty(node.Next) && !d.Nodes.ContainsKey(node.Next!))
                    context.Error(d.Id, $"nodes.{key}.next", ValidationCodes.DialogueDangling, $"Node '{key}' leads to missing node '{node.Next}'");
                for (var i = 0; i < node.Choices.Count; i++)
                {
                    var next = node.Choices[i].Next;
                    if (!string.IsNullOrEmpty(next) && !d.Nodes.ContainsKey(next!))
                        context.Error(d.Id, $"nodes.{key}.choices[{i}].next", ValidationCodes.DialogueDangling, $"Choice {i} of node '{key}' leads to missing node '{next}'");
                }
            }

            CheckChoices(context, d);
            var reached = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            if (d.Nodes.ContainsKey(d.Start)) pending.Push(d.Start);
            while (pending.Count > 0)
            {
                var key = pending.Pop();
                if (!reached.Add(key)) continue;
                var node = d.Nodes[key];
                foreach (var next in node.Choices.Select(c => c.Next).Append(node.Next))
                    if (!string.IsNullOrEmpty(next) && d.Nodes.ContainsKey(next!)) pending.Push(next!);
            }
            foreach (var key in d.Nodes.Keys.Where(k => !reached.Contains(k)))
                context.Warn(d.Id, $"nodes.{key}", ValidationCodes.WarnDialogueUnreachable, $"Node '{key}' is not reachable from start '{d.Start}'");
        }
    }

    /// <summary>
    /// The values of conditions and actions (T-601): level bounds in order, quantities of at least 1, services that exist, save point cells that are
    /// walkable on every map where an NPC with this dialogue stands.
    /// </summary>
    private static void CheckChoices(ValidationContext context, DialogueDef d)
    {
        var db = context.Database;
        var maps = db.Grids.Values.Where(g => g.Npcs.Any(n => db.Npcs.TryGet(n.NpcId, out var npc) && string.Equals(npc.DialogueId, d.Id, StringComparison.Ordinal))).ToList();
        foreach (var (key, node) in d.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal))
            for (var i = 0; i < node.Choices.Count; i++)
            {
                var choice = node.Choices[i];
                var p = $"nodes.{key}.choices[{i}]";
                for (var c = 0; c < choice.Conditions.Count; c++)
                {
                    var cp = $"{p}.conditions[{c}]";
                    switch (choice.Conditions[c])
                    {
                        case BaseLevelCondition { Min: null, Max: null }:
                        case JobLevelCondition { Min: null, Max: null }:
                            context.Error(d.Id, cp, ValidationCodes.DialogueInvalid, "give min, max or both");
                            break;
                        case BaseLevelCondition b when b.Min > b.Max:
                        case JobLevelCondition j when j.Min > j.Max:
                            context.Error(d.Id, cp, ValidationCodes.DialogueInvalid, "min is above max, so the condition never holds");
                            break;
                        case HasItemCondition { Quantity: < 1 }:
                            context.Error(d.Id, cp + ".quantity", ValidationCodes.DialogueInvalid, "quantity must be at least 1");
                            break;
                        case QuestStateCondition qs when !QuestStates.All.Contains(qs.State):
                            context.Error(d.Id, cp + ".state", ValidationCodes.VocabUnknown, $"'{qs.State}' is not a quest state ({string.Join(", ", QuestStates.All.Ordered)})");
                            break;
                        case QuestStepCondition step when db.Quests.TryGet(step.QuestId, out var quest) && (step.Step < 1 || step.Step > quest.Steps.Count):
                            context.Error(d.Id, cp + ".step", ValidationCodes.DialogueInvalid, $"step must be from 1 to {quest.Steps.Count}, the steps of {quest.Id}");
                            break;
                    }
                }
                for (var a = 0; a < choice.Actions.Count; a++)
                {
                    var ap = $"{p}.actions[{a}]";
                    switch (choice.Actions[a])
                    {
                        case TakeItemAction { Quantity: < 1 }:
                        case GiveItemAction { Quantity: < 1 }:
                            context.Error(d.Id, ap + ".quantity", ValidationCodes.DialogueInvalid, "quantity must be at least 1");
                            break;
                        case OpenServiceAction s when !ServiceIds.All.Contains(s.Service):
                            context.Error(d.Id, ap + ".service", ValidationCodes.VocabUnknown, $"'{s.Service}' is not a service ({string.Join(", ", ServiceIds.All.Ordered)})");
                            break;
                        case SetSavePointAction save:
                            foreach (var grid in maps)
                                if (!grid.IsWalkable(save.X, save.Y))
                                    context.Error(d.Id, ap, ValidationCodes.DialogueInvalid, $"save point ({save.X},{save.Y}) is not a walkable cell of {grid.MapId}, where an NPC with this dialogue stands");
                            if (maps.Count == 0)
                                context.Error(d.Id, ap, ValidationCodes.DialogueInvalid, "set_save_point needs an NPC with this dialogue placed on a map: the save point is a cell of the NPC's map");
                            break;
                    }
                }
            }
    }
}
