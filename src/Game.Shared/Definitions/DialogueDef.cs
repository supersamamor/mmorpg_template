using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>A dialogue graph: nodes keyed by a local node id, starting at <see cref="Start"/>. The server runs it (ARCHITECTURE §6.4); the client never sees the graph.</summary>
    public sealed record DialogueDef : IDefinition
    {
        /// <summary>Id namespace for dialogues.</summary>
        public const string IdPrefix = IdPrefixes.Dialogue;

        /// <summary>Dialogue id, e.g. <c>dialogue.guide_intro</c>.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Editor-facing name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Key in <see cref="Nodes"/> of the first node.</summary>
        public string Start { get; init; } = string.Empty;

        /// <summary>Nodes by local node id (not namespaced; unique within this dialogue).</summary>
        public IReadOnlyDictionary<string, DialogueNodeDef> Nodes { get; init; } = Empty.Dictionary<string, DialogueNodeDef>();
    }

    /// <summary>One line of dialogue and the player's options.</summary>
    public sealed record DialogueNodeDef
    {
        /// <summary>Speaking NPC (<c>npc.*</c>); null means the NPC being talked to.</summary>
        public string? SpeakerNpcId { get; init; }

        /// <summary>Portrait override (<c>portrait.*</c>); null uses the speaker's.</summary>
        public string? PortraitAssetKey { get; init; }

        /// <summary>Display text.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>Node to continue to when there are no choices; null ends the dialogue.</summary>
        public string? Next { get; init; }

        /// <summary>Player choices. Empty means a single "continue".</summary>
        public IReadOnlyList<DialogueChoiceDef> Choices { get; init; } = Empty.List<DialogueChoiceDef>();
    }

    /// <summary>A player choice in a dialogue node.</summary>
    public sealed record DialogueChoiceDef
    {
        /// <summary>Display text of the choice.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>Node to go to; null ends the dialogue.</summary>
        public string? Next { get; init; }

        /// <summary>The choice is shown only when every condition holds (all of them; none means always).</summary>
        public IReadOnlyList<DialogueConditionDef> Conditions { get; init; } = Empty.List<DialogueConditionDef>();

        /// <summary>Run in order when the choice is picked, before the dialogue moves to <see cref="Next"/>.</summary>
        public IReadOnlyList<DialogueActionDef> Actions { get; init; } = Empty.List<DialogueActionDef>();
    }

    /// <summary>
    /// A test on the talking player that decides whether a choice is shown. In content each condition is a map whose <c>type</c> key selects the
    /// concrete record (<see cref="Discriminators.DialogueConditions"/>). T-602 added the quest conditions and <c>out_of_combat</c>.
    /// </summary>
    public abstract record DialogueConditionDef
    {
        /// <summary>Condition type discriminator; one of <see cref="DialogueConditionTypes"/>.</summary>
        public abstract string Type { get; }
    }

    /// <summary><c>base_level</c>: the base level is at least <see cref="Min"/> and at most <see cref="Max"/> (either may be left out).</summary>
    public sealed record BaseLevelCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.BaseLevel;

        /// <summary>Lowest base level that passes; null for no lower bound.</summary>
        public int? Min { get; init; }

        /// <summary>Highest base level that passes; null for no upper bound.</summary>
        public int? Max { get; init; }
    }

    /// <summary><c>job_level</c>: the job level is at least <see cref="Min"/> and at most <see cref="Max"/> (either may be left out).</summary>
    public sealed record JobLevelCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.JobLevel;

        /// <summary>Lowest job level that passes; null for no lower bound.</summary>
        public int? Min { get; init; }

        /// <summary>Highest job level that passes; null for no upper bound.</summary>
        public int? Max { get; init; }
    }

    /// <summary><c>job_is</c>: the character has this job.</summary>
    public sealed record JobIsCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.JobIs;

        /// <summary>Job id (<c>job.*</c>).</summary>
        public string JobId { get; init; } = string.Empty;
    }

    /// <summary><c>job_is_not</c>: the character does not have this job.</summary>
    public sealed record JobIsNotCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.JobIsNot;

        /// <summary>Job id (<c>job.*</c>).</summary>
        public string JobId { get; init; } = string.Empty;
    }

    /// <summary><c>has_item</c>: the bag holds at least <see cref="Quantity"/> of the item (worn equipment does not count).</summary>
    public sealed record HasItemCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.HasItem;

        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>How many. Default 1.</summary>
        public int Quantity { get; init; } = 1;
    }

    /// <summary><c>has_status</c>: the character has the status active.</summary>
    public sealed record HasStatusCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.HasStatus;

        /// <summary>Status id (<c>status.*</c>).</summary>
        public string StatusId { get; init; } = string.Empty;
    }

    /// <summary><c>quest_state</c> (T-602): the character stands with the quest as <see cref="State"/> says (a <c>QuestStates</c> term).</summary>
    public sealed record QuestStateCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.QuestState;

        /// <summary>Quest id (<c>quest.*</c>).</summary>
        public string QuestId { get; init; } = string.Empty;

        /// <summary>
        /// A <c>QuestStates</c> term: <c>not_started</c>, <c>available</c> (it would start now), <c>active</c> or <c>completed</c>.
        /// </summary>
        public string State { get; init; } = string.Empty;
    }

    /// <summary><c>quest_step</c> (T-602): the quest is active and its current step is <see cref="Step"/> (1 is the first step).</summary>
    public sealed record QuestStepCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.QuestStep;

        /// <summary>Quest id (<c>quest.*</c>).</summary>
        public string QuestId { get; init; } = string.Empty;

        /// <summary>The step, counted from 1. At least 1 and at most the quest's step count (validated at load).</summary>
        public int Step { get; init; } = 1;
    }

    /// <summary>
    /// <c>out_of_combat</c> (T-602, ADR-002 row 253): the character has neither dealt nor taken a hit within <c>regen.out_of_combat_delay_ms</c> (the same window
    /// as <c>progression.reset.refuse_in_combat</c>).
    /// </summary>
    public sealed record OutOfCombatCondition : DialogueConditionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueConditionTypes.OutOfCombat;
    }

    /// <summary>
    /// Something that happens when a choice is picked. In content each action is a map whose <c>type</c> key selects the concrete record
    /// (<see cref="Discriminators.DialogueActions"/>). A choice's item actions apply as one transaction; see ARCHITECTURE §6.4.
    /// </summary>
    public abstract record DialogueActionDef
    {
        /// <summary>Action type discriminator; one of <see cref="DialogueActionTypes"/>.</summary>
        public abstract string Type { get; }
    }

    /// <summary><c>start_quest</c>: raises <c>QuestStartRequested</c> for the quest. The quest system (T-602) checks prerequisites and starts it; until then nothing handles the event.</summary>
    public sealed record StartQuestAction : DialogueActionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueActionTypes.StartQuest;

        /// <summary>Quest id (<c>quest.*</c>).</summary>
        public string QuestId { get; init; } = string.Empty;
    }

    /// <summary><c>set_save_point</c>: the save point becomes this cell of the NPC's own map. Teleports to the save point and a respawn after death use it.</summary>
    public sealed record SetSavePointAction : DialogueActionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueActionTypes.SetSavePoint;

        /// <summary>Cell x; the cell must be walkable.</summary>
        public int X { get; init; }

        /// <summary>Cell y.</summary>
        public int Y { get; init; }
    }

    /// <summary><c>run_effects</c>: run the effects on the character, as a consumable would (it is caster and target). Resets are allowed here (a service).</summary>
    public sealed record RunEffectsAction : DialogueActionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueActionTypes.RunEffects;

        /// <summary>The effects, in order.</summary>
        public IReadOnlyList<EffectDef> Effects { get; init; } = Empty.List<EffectDef>();
    }

    /// <summary><c>take_item</c>: remove <see cref="Quantity"/> of the item from the bag. Refused (nothing changes) when the bag holds less.</summary>
    public sealed record TakeItemAction : DialogueActionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueActionTypes.TakeItem;

        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>How many. Default 1.</summary>
        public int Quantity { get; init; } = 1;
    }

    /// <summary><c>give_item</c>: put <see cref="Quantity"/> of the item in the bag. Refused (nothing changes) when it would not fit or be too heavy.</summary>
    public sealed record GiveItemAction : DialogueActionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueActionTypes.GiveItem;

        /// <summary>Item id (<c>item.*</c>).</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>How many. Default 1.</summary>
        public int Quantity { get; init; } = 1;
    }

    /// <summary>
    /// <c>open_service</c>: opens an NPC service for the character; it stays open until the character walks out of talking range, warps, dies or closes it. A
    /// service that needs data takes it from a field of this action (T-603: the <c>shop</c> service names its shop in <see cref="ShopId"/>; <c>refine</c> takes none).
    /// </summary>
    public sealed record OpenServiceAction : DialogueActionDef
    {
        /// <inheritdoc />
        public override string Type => DialogueActionTypes.OpenService;

        /// <summary>A <see cref="ServiceIds"/> term.</summary>
        public string Service { get; init; } = string.Empty;

        /// <summary>The shop to open (<c>shop.*</c>); required when <see cref="Service"/> is <c>shop</c>, not allowed otherwise (validated at load).</summary>
        public string? ShopId { get; init; }
    }
}
