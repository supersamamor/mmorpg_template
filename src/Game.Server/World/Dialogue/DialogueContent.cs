using Game.Content.Registry;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Server.World.Dialogue;

/// <summary>The NPCs, dialogue graphs and talking numbers the dialogue system reads. Immutable after construction, so shared by every instance.</summary>
public sealed class DialogueContent
{
    private readonly Dictionary<string, NpcDef> _npcs;
    private readonly Dictionary<string, DialogueDef> _dialogues;

    /// <summary>
    /// Creates content from definitions. Throws <see cref="ArgumentOutOfRangeException"/> for a talk range below 1. <paramref name="quests"/> and
    /// <paramref name="questBalance"/> answer the quest conditions (T-602); without them every quest is unknown (never available, never active).
    /// </summary>
    public DialogueContent(IEnumerable<NpcDef> npcs, IEnumerable<DialogueDef> dialogues, DialogueBalanceDef balance, IEnumerable<QuestDef>? quests = null,
        QuestBalanceDef? questBalance = null)
    {
        Quests = (quests ?? []).ToList();
        QuestBalance = questBalance ?? new QuestBalanceDef();
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(dialogues);
        Balance = balance ?? throw new ArgumentNullException(nameof(balance));
        if (balance.TalkRange < 1) throw new ArgumentOutOfRangeException(nameof(balance), "dialogue.talk_range must be at least 1.");
        _npcs = npcs.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _dialogues = dialogues.ToDictionary(d => d.Id, StringComparer.Ordinal);
    }

    /// <summary>The loaded database's NPCs and dialogues.</summary>
    public static DialogueContent From(ContentDatabase content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new DialogueContent(content.Npcs.All, content.Dialogue.All, content.Balance.Dialogue, content.Quests.All, content.Balance.Quests);
    }

    /// <summary>The quests the quest conditions read (T-602).</summary>
    public IReadOnlyList<QuestDef> Quests { get; }

    /// <summary>The <c>quests</c> section of <c>balance.yaml</c> (the <c>available</c> state counts <c>max_active</c>).</summary>
    public QuestBalanceDef QuestBalance { get; }

    /// <summary>The <c>dialogue</c> section of <c>balance.yaml</c>.</summary>
    public DialogueBalanceDef Balance { get; }

    /// <summary>Finds an NPC.</summary>
    public bool TryGetNpc(string id, out NpcDef npc) => _npcs.TryGetValue(id, out npc!);

    /// <summary>Finds a dialogue graph.</summary>
    public bool TryGetDialogue(string id, out DialogueDef dialogue) => _dialogues.TryGetValue(id, out dialogue!);
}
