using Game.Shared.Definitions;

namespace Game.Content.Validation.Rules;

/// <summary>
/// Warnings for content nobody uses: items and statuses that no definition references, skills that neither a job tree nor a
/// monster's <c>ai.skills</c> reaches, monsters that no map grid spawns (and no summon effect creates), and NPCs that no map
/// grid places.
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

        // A monster's ai.skills entry is a use: those skills belong to monsters and are in no job tree on purpose.
        var usedSkills = db.Jobs.All.SelectMany(j => j.Skills.Select(s => s.SkillId))
            .Concat(db.Monsters.All.SelectMany(m => m.Ai.Skills.Select(s => s.SkillId)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var skill in db.Skills.All.Where(s => !usedSkills.Contains(s.Id)))
            context.Warn(skill.Id, "id", ValidationCodes.WarnSkillNoJob, $"{skill.Id} is not in any job's skill tree or any monster's ai.skills");

        var spawned = db.Grids.Values.SelectMany(g => g.Spawns.Select(s => s.MonsterId))
            .Concat(refs.Where(r => r.Kind == RefKind.Monster && IsSummon(db, r)).Select(r => r.Id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var monster in db.Monsters.All.Where(m => !spawned.Contains(m.Id)))
            context.Warn(monster.Id, "id", ValidationCodes.WarnMonsterNotSpawned, $"{monster.Id} is not spawned by any map grid or summon effect");

        var placed = db.Grids.Values.SelectMany(g => g.Npcs.Select(n => n.NpcId)).ToHashSet(StringComparer.Ordinal);
        foreach (var npc in db.Npcs.All.Where(n => !placed.Contains(n.Id)))
            context.Warn(npc.Id, "id", ValidationCodes.WarnNpcNotPlaced, $"{npc.Id} is not placed by any map grid");
    }

    /// <summary>A monster reference counts as a spawn only when it comes from a summon effect (not from a kill objective).</summary>
    private static bool IsSummon(Registry.ContentDatabase db, IdRef reference) =>
        ContentWalker.Effects(db).Any(e => e.Owner == reference.Owner && reference.Path == e.Path + ".monster_id" && e.Effect is SummonEffect);
}
