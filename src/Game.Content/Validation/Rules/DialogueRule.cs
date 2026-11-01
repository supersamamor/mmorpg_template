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
}
