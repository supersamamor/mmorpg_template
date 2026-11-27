using System;
using System.Collections.Generic;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Chat;

/// <summary>One line in the chat log: a player's line as the server sent it, or a line of the client's own (a refusal, an announcement) under <c>system</c>.</summary>
/// <param name="Line">The server's number of the line (what a report names), or 0 for the client's own lines.</param>
/// <param name="Channel">A "Chat channels" term.</param>
/// <param name="From">The sender's entity id, or 0.</param>
/// <param name="Name">The sender's character name (drawn apart from the text, never shaped by it), or empty for a system line.</param>
/// <param name="Text">What was said.</param>
/// <param name="To">For a whisper, whom it went to.</param>
/// <param name="Mine">This client's own character said it (it cannot be reported or blocked).</param>
public sealed record ChatEntry(long Line, string Channel, long From, string Name, string Text, string? To, bool Mine);

/// <summary>
/// The chat as the client holds it (T-901): the last <see cref="MaxLines"/> lines of this session, in memory only (nothing is written to the device, and it is emptied when the player signs
/// out), the channel and whisper target the player chose, the longest line and the blocked names the server sent (<c>chat_state</c>). The client never shows a line of its own before the
/// server sent it back. Pure; the HUD redraws when <see cref="Version"/> moves.
/// </summary>
public sealed class ChatBook
{
    /// <summary>Lines kept: what the open panel shows; older ones are dropped.</summary>
    public const int MaxLines = 50;

    private readonly List<ChatEntry> _lines = new List<ChatEntry>();

    /// <summary>Moves on every change.</summary>
    public int Version { get; private set; }

    /// <summary>The lines, oldest first.</summary>
    public IReadOnlyList<ChatEntry> Lines => _lines;

    /// <summary>The longest line the server takes, in UTF-16 code units (0 until the server said).</summary>
    public int MaxLength { get; private set; }

    /// <summary>The names this account blocks.</summary>
    public IReadOnlyList<string> Blocked { get; private set; } = Array.Empty<string>();

    /// <summary>Where the next line goes: <c>map</c> or <c>whisper</c>.</summary>
    public string Channel { get; private set; } = ChatChannels.Map;

    /// <summary>The whisper target's name, as the player typed or picked it.</summary>
    public string WhisperTo { get; private set; } = string.Empty;

    /// <summary>This character's entity id (lines it said are its own).</summary>
    public long OwnId { get; set; }

    /// <summary>A line or a system line has come since the log was last read (<see cref="MarkRead"/>).</summary>
    public bool Unread { get; private set; }

    /// <summary>Switches between map and whisper.</summary>
    public void SetChannel(string channel)
    {
        if (channel != ChatChannels.Map && channel != ChatChannels.Whisper) return;
        Channel = channel;
        Version++;
    }

    /// <summary>The whisper target (also switches to whisper when it is not empty).</summary>
    public void SetWhisperTo(string name)
    {
        WhisperTo = (name ?? string.Empty).Trim();
        if (WhisperTo.Length > 0) Channel = ChatChannels.Whisper;
        Version++;
    }

    /// <summary>
    /// What would go out for <paramref name="typed"/>: the trimmed text, or null with a reason the player can read when it cannot go (nothing typed, too long, no whisper target). The
    /// server checks again; this only spares a round trip and the gateway's message limit.
    /// </summary>
    public string? Outgoing(string typed, out string? why)
    {
        why = null;
        var text = (typed ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (MaxLength > 0 && text.Length > MaxLength)
        {
            why = UiText.ChatRefused(ChatReasons.TooLong, null, null);
            return null;
        }
        if (Channel == ChatChannels.Whisper && WhisperTo.Length == 0)
        {
            why = UiText.ChatNoWhisperTarget;
            return null;
        }
        if (Channel == ChatChannels.Whisper && WhisperTo.Length > Game.Shared.Accounts.CharacterNameRules.LongestAllowed)
        {
            why = UiText.ChatRefused(ChatReasons.NotOnline, null, null);   // no name is that long; a longer message could pass the gateway's limit
            return null;
        }
        return text;
    }

    /// <summary>A line the server sent.</summary>
    public void Apply(ChatLineMessage line)
    {
        Add(new ChatEntry(line.Line, line.Channel, line.From, line.Name, line.Text, line.To, OwnId != 0 && line.From == OwnId));
        Unread = true;
    }

    /// <summary>The server's chat state: the longest line and the blocked names.</summary>
    public void Apply(ChatStateMessage state)
    {
        MaxLength = state.MaxLength;
        Blocked = state.Blocked;
        Version++;
    }

    /// <summary>A refusal: one system line saying why (how long is left for a mute).</summary>
    public void Apply(ChatRefusedMessage refused) => System(UiText.ChatRefused(refused.Reason, refused.RetryAfterSeconds, refused.To));

    /// <summary>A report was stored.</summary>
    public void Apply(ChatReportedMessage reported) => System(UiText.ChatReported);

    /// <summary>A server announcement (T-806) also goes into the log, under <c>system</c>.</summary>
    public void Announcement(string text)
    {
        if (text.Length > 0) System(text);
    }

    /// <summary>A line of the client's own in the log (why a line could not go).</summary>
    public void Note(string text) => System(text);

    /// <summary>True when <paramref name="name"/> is blocked (any letter case).</summary>
    public bool IsBlocked(string name)
    {
        foreach (var blocked in Blocked)
            if (string.Equals(blocked, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The player read the log.</summary>
    public void MarkRead()
    {
        if (!Unread) return;
        Unread = false;
        Version++;
    }

    /// <summary>Forgets everything (signing out): no chat outlives the session on the device.</summary>
    public void Clear()
    {
        _lines.Clear();
        Blocked = Array.Empty<string>();
        MaxLength = 0;
        Channel = ChatChannels.Map;
        WhisperTo = string.Empty;
        OwnId = 0;
        Unread = false;
        Version++;
    }

    private void System(string text)
    {
        Add(new ChatEntry(0, ChatChannels.System, 0, string.Empty, text, null, false));
        Unread = true;
    }

    private void Add(ChatEntry entry)
    {
        if (_lines.Count >= MaxLines) _lines.RemoveAt(0);
        _lines.Add(entry);
        Version++;
    }
}
