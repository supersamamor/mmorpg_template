using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// Id prefix per registry definition type, so generic code (e.g. <c>Registry&lt;T&gt;</c>) can check
    /// prefixes without reflection. Each value equals that record's <c>IdPrefix</c> constant.
    /// </summary>
    public static class DefinitionIdPrefixes
    {
        /// <summary>Definition record type → id prefix.</summary>
        public static readonly IReadOnlyDictionary<System.Type, string> ByType =
            new ReadOnlyDictionary<System.Type, string>(new Dictionary<System.Type, string>
            {
                [typeof(JobDef)] = JobDef.IdPrefix,
                [typeof(SkillDef)] = SkillDef.IdPrefix,
                [typeof(ItemDef)] = ItemDef.IdPrefix,
                [typeof(MonsterDef)] = MonsterDef.IdPrefix,
                [typeof(StatusDef)] = StatusDef.IdPrefix,
                [typeof(QuestDef)] = QuestDef.IdPrefix,
                [typeof(NpcDef)] = NpcDef.IdPrefix,
                [typeof(DialogueDef)] = DialogueDef.IdPrefix,
                [typeof(DropTableDef)] = DropTableDef.IdPrefix,
                [typeof(MapDef)] = MapDef.IdPrefix,
                [typeof(ExpTableDef)] = ExpTableDef.IdPrefix,
            });
    }
}
