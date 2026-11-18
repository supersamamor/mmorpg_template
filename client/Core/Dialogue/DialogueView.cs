using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Dialogue;

/// <summary>
/// The dialogue the server is running with the player (T-601, ARCHITECTURE §6.4): the node it last sent and the NPC service it last opened. A plain holder the
/// panel draws from; it never decides where a choice leads, the server does. <see cref="Version"/> moves on every change, so the panel redraws only then.
/// </summary>
public sealed class DialogueView
{
    /// <summary>Moves on every change.</summary>
    public int Version { get; private set; }

    /// <summary>True while the server has sent a node and not ended the dialogue.</summary>
    public bool Open { get; private set; }

    /// <summary>The NPC entity talked to.</summary>
    public long Npc { get; private set; }

    /// <summary>The speaking NPC's id (<c>npc.*</c>): the panel looks up its name, title and portrait in the display bundle.</summary>
    public string Speaker { get; private set; } = string.Empty;

    /// <summary>A portrait key overriding the speaker's, or null.</summary>
    public string? Portrait { get; private set; }

    /// <summary>The line.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>The choices the server shows; each <see cref="DialogueChoiceRecord.Index"/> is what to send back. Empty means "continue" (or "done" when <see cref="More"/> is false).</summary>
    public IReadOnlyList<DialogueChoiceRecord> Choices { get; private set; } = Array.Empty<DialogueChoiceRecord>();

    /// <summary>With no choices: the dialogue goes on after this node.</summary>
    public bool More { get; private set; }

    /// <summary>The NPC service the server last opened and has not closed (a <c>ServiceIds</c> term), or null.</summary>
    public string? Service { get; private set; }

    /// <summary>A <c>dialogue</c> arrived: show the node.</summary>
    public void Apply(DialogueMessage message)
    {
        Open = true;
        Npc = message.Npc;
        Speaker = message.Speaker;
        Portrait = message.Portrait;
        Text = message.Text;
        Choices = message.Choices;
        More = message.More;
        Version++;
    }

    /// <summary>
    /// A <c>dialogue_ended</c> arrived: the panel closes. A <c>finished</c> graph leaves the service it opened running; any other reason ends the service too.
    /// </summary>
    public void Ended(string reason)
    {
        Open = false;
        if (reason != DialogueEndReasons.Finished) Service = null;
        Version++;
    }

    /// <summary>A <c>service_opened</c> arrived.</summary>
    public void ServiceOpened(string service)
    {
        Service = service;
        Version++;
    }

    /// <summary>The server refused something that needs the service (refine without a smith in reach): it is closed.</summary>
    public void ServiceClosed()
    {
        if (Service is null) return;
        Service = null;
        Version++;
    }

    /// <summary>Forgets everything (a new login).</summary>
    public void Reset()
    {
        Open = false;
        Service = null;
        Choices = Array.Empty<DialogueChoiceRecord>();
        Version++;
    }
}
