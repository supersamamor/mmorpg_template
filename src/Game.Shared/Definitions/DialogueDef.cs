using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Definitions
{
    /// <summary>A dialogue graph: nodes keyed by a local node id, starting at <see cref="Start"/>.</summary>
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

        /// <summary>Quest to start when chosen (<c>quest.*</c>); the server checks its prerequisites.</summary>
        public string? StartQuestId { get; init; }
    }
}
