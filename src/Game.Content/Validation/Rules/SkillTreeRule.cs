using Game.Shared.Definitions;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Skill tree checks per job: a prerequisite must be a skill of the same job or of an ancestor job, its level must be
/// reachable (&lt;= the prerequisite's max level), and prerequisites must be acyclic.
/// </summary>
public sealed class SkillTreeRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "skill-tree";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;
        foreach (var job in db.Jobs.All)
        {
            var available = TreeSkills(db.Jobs, job);
            for (var i = 0; i < job.Skills.Count; i++)
                for (var j = 0; j < job.Skills[i].Requires.Count; j++)
                {
                    var req = job.Skills[i].Requires[j];
                    var path = $"skills[{i}].requires[{j}]";
                    if (!db.Skills.TryGet(req.SkillId, out var skill)) continue; // reported by ReferenceRule
                    if (!available.TryGetValue(req.SkillId, out var max))
                    {
                        context.Error(job.Id, path + ".skill_id", ValidationCodes.SkillPrereqNotInTree,
                            $"{job.Skills[i].SkillId} requires {req.SkillId}, which is not in {job.Id} or its parent jobs' skill trees");
                        continue;
                    }
                    var cap = max ?? skill.MaxLevel;
                    if (req.Level > cap)
                        context.Error(job.Id, path + ".level", ValidationCodes.SkillPrereqLevel,
                            $"{job.Skills[i].SkillId} requires {req.SkillId} level {req.Level}, but its max level is {cap}");
                }

            ReportCycle(context, job);
        }
    }

    /// <summary>Skill id → max-level override for the job's own and inherited skills.</summary>
    private static Dictionary<string, int?> TreeSkills(Registry.Registry<JobDef> jobs, JobDef job)
    {
        var map = new Dictionary<string, int?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (JobDef? j = job; j is not null && seen.Add(j.Id);
             j = !string.IsNullOrEmpty(j.ParentJobId) && jobs.TryGet(j.ParentJobId!, out var p) ? p : null)
            foreach (var s in j.Skills) map.TryAdd(s.SkillId, s.MaxLevel);
        return map;
    }

    private static void ReportCycle(ValidationContext context, JobDef job)
    {
        var edges = job.Skills.GroupBy(s => s.SkillId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(s => s.Requires.Select(r => r.SkillId)).ToList(), StringComparer.Ordinal);

        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in edges.Keys)
        {
            if (Visit(start, edges, done, new List<string>(), out var cycle))
            {
                var index = job.Skills.ToList().FindIndex(s => s.SkillId == cycle[0]);
                context.Error(job.Id, $"skills[{Math.Max(index, 0)}]", ValidationCodes.SkillCycle,
                    $"Skill prerequisites loop in {job.Id}: {string.Join(" -> ", cycle)}");
                return; // one report per job is enough
            }
        }
    }

    private static bool Visit(string node, Dictionary<string, List<string>> edges, HashSet<string> done, List<string> stack, out List<string> cycle)
    {
        cycle = new List<string>();
        var at = stack.IndexOf(node);
        if (at >= 0)
        {
            cycle = stack.Skip(at).Append(node).ToList();
            return true;
        }
        if (!done.Add(node)) return false;
        stack.Add(node);
        if (edges.TryGetValue(node, out var next))
            foreach (var n in next)
                if (Visit(n, edges, done, stack, out cycle)) return true;
        stack.RemoveAt(stack.Count - 1);
        return false;
    }
}
