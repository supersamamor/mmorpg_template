using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// A quest: <c>prerequisites → steps[objectives] → rewards</c> (ARCHITECTURE §6.4).
    /// Per-character state is <c>{questId, stepIndex, counters}</c>, so step order matters.
    /// </summary>
    public sealed record QuestDef : IDefinition
    {
        /// <summary>Id namespace for quests.</summary>
        public const string IdPrefix = IdPrefixes.Quest;

        /// <summary>Quest id, e.g. <c>quest.first_hunt</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Display description.</summary>
        public string? Description { get; init; }

        /// <summary>NPC that offers the quest (<c>npc.*</c>); null if started another way.</summary>
        public string? GiverNpcId { get; init; }

        /// <summary>True if the quest can be taken again after completion.</summary>
        public bool Repeatable { get; init; }

        /// <summary>Conditions to start the quest.</summary>
        public QuestPrerequisitesDef Prerequisites { get; init; } = new QuestPrerequisitesDef();

        /// <summary>Steps in order. A step completes when all its objectives are met.</summary>
        public IReadOnlyList<QuestStepDef> Steps { get; init; } = Empty.List<QuestStepDef>();

        /// <summary>Rewards given when the last step completes.</summary>
        public IReadOnlyList<RewardDef> Rewards { get; init; } = Empty.List<RewardDef>();
    }

    /// <summary>Conditions to start a quest. All listed conditions must hold.</summary>
    public sealed record QuestPrerequisitesDef
    {
        /// <summary>Minimum base level. 0 means none.</summary>
        public int MinBaseLevel { get; init; }

        /// <summary>Allowed current jobs (<c>job.*</c>). Empty means any.</summary>
        public IReadOnlyList<string> JobIds { get; init; } = Empty.List<string>();

        /// <summary>Quests that must be completed first (<c>quest.*</c>).</summary>
        public IReadOnlyList<string> QuestIds { get; init; } = Empty.List<string>();
    }

    /// <summary>One quest step.</summary>
    public sealed record QuestStepDef
    {
        /// <summary>Display text for the quest log.</summary>
        public string? Description { get; init; }

        /// <summary>Objectives; all must be met to finish the step.</summary>
        public IReadOnlyList<QuestObjectiveDef> Objectives { get; init; } = Empty.List<QuestObjectiveDef>();
    }
}
