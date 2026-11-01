namespace Game.Content.Validation.Rules;

/// <summary>
/// Warnings for content nobody uses: items and statuses that no definition references, and skills that are in no job's
/// tree. "Monsters never spawned" is not checked yet: maps carry no spawn data until the map work (T-208) lands.
/// </summary>
public sealed class UnusedRule : IValidationRule
{
    /// <inheritdoc />
    public string Name => "unused";

    /// <inheritdoc />
    public void Validate(ValidationContext context)
    {
        var db = context.Database;
        var refs = ContentWalker.Refs(db).ToList();

        var usedItems = refs.Where(r => r.Kind == RefKind.Item).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in db.Items.All.Where(i => !usedItems.Contains(i.Id)))
            context.Warn(item.Id, "id", ValidationCodes.WarnUnusedItem, $"{item.Id} is not referenced by any drop table, quest or objective");

        var usedStatuses = refs.Where(r => r.Kind == RefKind.Status).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var status in db.Statuses.All.Where(s => !usedStatuses.Contains(s.Id)))
            context.Warn(status.Id, "id", ValidationCodes.WarnUnusedStatus, $"{status.Id} is not applied or granted by any skill, item, status or quest");

        var inTrees = db.Jobs.All.SelectMany(j => j.Skills.Select(s => s.SkillId)).ToHashSet(StringComparer.Ordinal);
        foreach (var skill in db.Skills.All.Where(s => !inTrees.Contains(s.Id)))
            context.Warn(skill.Id, "id", ValidationCodes.WarnSkillNoJob, $"{skill.Id} is not in any job's skill tree");
    }
}
