using Game.Content.Registry;
using Game.Shared.Definitions;
using Game.Shared.Progression;

namespace Game.Content.Simulation;

/// <summary>Adapts a <see cref="ContentDatabase"/> to the <see cref="IProgressionContent"/> lookups of <see cref="ProgressionRules"/>.</summary>
public sealed class ContentProgression : IProgressionContent
{
    private readonly ContentDatabase _db;

    public ContentProgression(ContentDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public bool TryGetJob(string id, out JobDef job) => _db.Jobs.TryGet(id, out job!);

    public bool TryGetSkill(string id, out SkillDef skill) => _db.Skills.TryGet(id, out skill!);

    public bool TryGetExpTable(string id, out ExpTableDef table) => _db.ExpTables.TryGet(id, out table!);
}
