using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>
    /// One condition of a quest step (ARCHITECTURE §6.4). Objectives listen to event bus events.
    /// Selected by the <c>type</c> key; the table is <see cref="Discriminators.QuestObjectives"/>.
    /// </summary>
    public abstract record QuestObjectiveDef
    {
        /// <summary>Objective type discriminator; one of <see cref="QuestObjectiveTypes"/>.</summary>
        public abstract string Type { get; }
    }

    /// <summary><c>kill</c>: kill a number of one monster (event <c>MonsterKilled</c>).</summary>
    public sealed record KillObjective : QuestObjectiveDef
    {
        /// <inheritdoc />
        public override string Type => QuestObjectiveTypes.Kill;

        /// <summary>Monster id (<c>monster.*</c>).</summary>
        public string MonsterId { get; init; } = string.Empty;

        /// <summary>How many kills. Default 1.</summary>
        public int Count { get; init; } = 1;
    }

    /// <summary><c>collect</c>: hold a number of one item (event <c>ItemAcquired</c>).</summary>
    public sealed record CollectObjective : QuestObjectiveDef
    {
        /// <inheritdoc />
        public override string Type => QuestObjectiveTypes.Collect;

        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>How many. Default 1.</summary>
        public int Count { get; init; } = 1;

        /// <summary>True to remove the items from the inventory when the step completes.</summary>
        public bool Consume { get; init; }
    }

    /// <summary><c>talk</c>: talk to an NPC (event <c>NpcTalked</c>).</summary>
    public sealed record TalkObjective : QuestObjectiveDef
    {
        /// <inheritdoc />
        public override string Type => QuestObjectiveTypes.Talk;

        /// <summary>NPC id (<c>npc.*</c>).</summary>
        public string NpcId { get; init; } = string.Empty;
    }

    /// <summary><c>reach</c>: stand within <see cref="Radius"/> cells of a cell on a map.</summary>
    public sealed record ReachObjective : QuestObjectiveDef
    {
        /// <inheritdoc />
        public override string Type => QuestObjectiveTypes.Reach;

        /// <summary>Map id (<c>map.*</c>).</summary>
        public string MapId { get; init; } = string.Empty;

        /// <summary>Target cell X.</summary>
        public int X { get; init; }

        /// <summary>Target cell Y.</summary>
        public int Y { get; init; }

        /// <summary>Radius in cells (Chebyshev). Default 1.</summary>
        public int Radius { get; init; } = 1;
    }

    /// <summary><c>level</c>: reach a base level (event <c>LevelUp</c>).</summary>
    public sealed record LevelObjective : QuestObjectiveDef
    {
        /// <inheritdoc />
        public override string Type => QuestObjectiveTypes.Level;

        /// <summary>Minimum base level.</summary>
        public int BaseLevel { get; init; } = 1;
    }

    /// <summary><c>job</c>: have a job (event <c>JobChanged</c>).</summary>
    public sealed record JobObjective : QuestObjectiveDef
    {
        /// <inheritdoc />
        public override string Type => QuestObjectiveTypes.Job;

        /// <summary>Job id (<c>job.*</c>).</summary>
        public string JobId { get; init; } = string.Empty;
    }

    /// <summary><c>use_item</c>: use a number of one item (event <c>ItemUsed</c>).</summary>
    public sealed record UseItemObjective : QuestObjectiveDef
    {
        /// <inheritdoc />
        public override string Type => QuestObjectiveTypes.UseItem;

        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>How many uses. Default 1.</summary>
        public int Count { get; init; } = 1;
    }

    /// <summary><c>equip</c>: equip an item (event <c>ItemEquipped</c>).</summary>
    public sealed record EquipObjective : QuestObjectiveDef
    {
        /// <inheritdoc />
        public override string Type => QuestObjectiveTypes.Equip;

        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;
    }
}
