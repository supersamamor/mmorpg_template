using System.Text;
using Game.Client.Core.Bundle;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Quests;

/// <summary>
/// The player's quest log as the server last said (T-602, ARCHITECTURE §6.4): active quests with their step and counters, completed quests, the NPC markers, and the
/// quest the tracker follows. A plain holder: the server decides every count and every step, this only keeps what arrived (<c>quest_log</c> replaces it, each
/// <c>quest</c> changes one quest). <see cref="Version"/> moves on every change and <see cref="MarkersVersion"/> when the markers do, so the HUD and the world redraw only then.
/// </summary>
public sealed class QuestBook
{
    private readonly List<QuestEntryRecord> _active = new();
    private readonly List<string> _completed = new();
    private readonly HashSet<string> _offer = new(StringComparer.Ordinal);
    private readonly HashSet<string> _turnIn = new(StringComparer.Ordinal);
    private readonly StringBuilder _text = new();

    /// <summary>Moves on every change.</summary>
    public int Version { get; private set; }

    /// <summary>Moves when the NPC markers change.</summary>
    public int MarkersVersion { get; private set; }

    /// <summary>True once a <c>quest_log</c> arrived.</summary>
    public bool Known { get; private set; }

    /// <summary>Active quests, in the order they were accepted (a full log orders them by id).</summary>
    public IReadOnlyList<QuestEntryRecord> Active => _active;

    /// <summary>Completed quest ids, in the order the server listed them or they were completed.</summary>
    public IReadOnlyList<string> Completed => _completed;

    /// <summary>The active quest the tracker shows: the one last accepted, advanced or picked in the log; else the first active one; null when none is active.</summary>
    public string? Tracked
    {
        get
        {
            if (_tracked is { } id && Find(id) is not null) return id;
            return _active.Count > 0 ? _active[0].Quest : null;
        }
    }

    private string? _tracked;

    /// <summary>The marker over an NPC (<c>npc.*</c>) for this player: <c>turn_in</c> wins over <c>offer</c>; null for none.</summary>
    public string? MarkerFor(string? npcId)
    {
        if (npcId is null) return null;
        if (_turnIn.Contains(npcId)) return QuestMarkers.TurnIn;
        return _offer.Contains(npcId) ? QuestMarkers.Offer : null;
    }

    /// <summary>The active quest with this id, or null.</summary>
    public QuestEntryRecord? Find(string questId)
    {
        for (var i = 0; i < _active.Count; i++)
            if (_active[i].Quest == questId) return _active[i];
        return null;
    }

    /// <summary><c>quest_log</c>: replaces everything (after every snapshot and when the markers change).</summary>
    public void Apply(QuestLogMessage log)
    {
        _active.Clear();
        _active.AddRange(log.Active);
        _completed.Clear();
        _completed.AddRange(log.Completed);
        if (!SameSet(_offer, log.Offer) || !SameSet(_turnIn, log.TurnIn))
        {
            _offer.Clear();
            foreach (var npc in log.Offer) _offer.Add(npc);
            _turnIn.Clear();
            foreach (var npc in log.TurnIn) _turnIn.Add(npc);
            MarkersVersion++;
        }
        Known = true;
        Version++;
    }

    /// <summary><c>quest</c>: one quest changed. A started or advanced quest becomes the tracked one.</summary>
    public void Apply(QuestMessage message)
    {
        var index = -1;
        for (var i = 0; i < _active.Count; i++)
            if (_active[i].Quest == message.Quest) index = i;
        if (message.Entry is { } entry)
        {
            if (index >= 0) _active[index] = entry;
            else _active.Add(entry);
            if (message.Change is QuestChanges.Accepted or QuestChanges.StepDone) _tracked = message.Quest;
        }
        else if (index >= 0)
        {
            _active.RemoveAt(index);
        }
        if (message.Change == QuestChanges.Completed && !_completed.Contains(message.Quest)) _completed.Add(message.Quest);
        Version++;
    }

    /// <summary>Follows <paramref name="questId"/> in the tracker (the log's Track button).</summary>
    public void Track(string questId)
    {
        if (Find(questId) is null || _tracked == questId) return;
        _tracked = questId;
        Version++;
    }

    /// <summary>Forgets everything (a new login).</summary>
    public void Reset()
    {
        _active.Clear();
        _completed.Clear();
        _offer.Clear();
        _turnIn.Clear();
        _tracked = null;
        Known = false;
        Version++;
        MarkersVersion++;
    }

    /// <summary>The compact tracker's text: the tracked quest's name, then one line per objective with its counter; <see cref="UiText.QuestsButton"/> when none is active.</summary>
    public string TrackerText(DisplayBundle bundle)
    {
        if (Tracked is not { } id || Find(id) is not { } entry) return UiText.QuestsButton;
        _text.Clear().Append(bundle.Quest(id)?.Name ?? id);
        if (entry.Held is { } held) _text.Append('\n').Append(UiText.QuestHeld(held));
        foreach (var objective in entry.Objectives) _text.Append('\n').Append(ObjectiveLine(bundle, objective));
        return _text.ToString();
    }

    /// <summary>One objective as the tracker and the log show it, e.g. <c>Gel Slime 3/5</c>.</summary>
    public static string ObjectiveLine(DisplayBundle bundle, QuestObjectiveRecord objective)
    {
        var target = objective.Type == QuestObjectiveTypes.Reach && objective.Target is { } map ? bundle.NameOf(map) : bundle.NameOf(objective.Target);
        return UiText.QuestObjective(objective.Type, target, objective.Have, objective.Need);
    }

    private static bool SameSet(HashSet<string> set, IReadOnlyList<string> list)
    {
        if (set.Count != list.Count) return false;
        foreach (var item in list)
            if (!set.Contains(item)) return false;
        return true;
    }
}
