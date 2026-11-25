using Game.Client.Core.Bundle;
using Game.Client.Core.Quests;
using Game.Client.Core.Skills;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Autopilot;

/// <summary>
/// The quest autopilot as the client sees it (T-605, ARCHITECTURE §6.4.2, §7.4): which active quests the player ticked and in what order, and the server's last word on the
/// run (<c>autopilot</c>: mode, quest, what it does, why it paused or ended). The ticks are this player's own wish and go nowhere until Start; they are never saved. The run state
/// is only ever what the server said: nothing here decides whether a run goes on, pauses or ends (CLAUDE.md rule 1). <see cref="Version"/> moves on every change, so the
/// HUD and the log redraw only then.
/// </summary>
public sealed class AutopilotModel
{
    private readonly List<string> _ticked = new();

    /// <summary>Moves on every change to the ticks or the run.</summary>
    public int Version { get; private set; }

    /// <summary>An "Autopilot modes" term; <c>off</c> until the server says otherwise (a fresh login gets no message: it is off).</summary>
    public string Mode { get; private set; } = AutopilotModes.Off;

    /// <summary>The quest it plays now, or null.</summary>
    public string? Quest { get; private set; }

    /// <summary>An "Autopilot activities" term while running, else null.</summary>
    public string? Doing { get; private set; }

    /// <summary>An "Autopilot pauses" term while paused, an "Autopilot stops" term from the last <c>off</c>, else null.</summary>
    public string? Reason { get; private set; }

    /// <summary>True while a run is running or paused.</summary>
    public bool On => Mode != AutopilotModes.Off;

    /// <summary>True while it plays.</summary>
    public bool Running => Mode == AutopilotModes.Running;

    /// <summary>True while it waits for the player.</summary>
    public bool Paused => Mode == AutopilotModes.Paused;

    /// <summary>The ticked quests in run order.</summary>
    public IReadOnlyList<string> Ticked => _ticked;

    /// <summary>The place of a ticked quest in the run, 1 for the first; 0 when it is not ticked.</summary>
    public int OrderOf(string quest) => _ticked.IndexOf(quest) + 1;

    /// <summary>True when the quest is ticked.</summary>
    public bool IsTicked(string quest) => _ticked.Contains(quest);

    /// <summary>
    /// Ticks or unticks an active quest; a newly ticked one goes to the end of the run. A quest the character does not have cannot be ticked (it would only be refused).
    /// Returns true when the ticks changed.
    /// </summary>
    public bool Toggle(string quest, QuestBook book)
    {
        var at = _ticked.IndexOf(quest);
        if (at >= 0) _ticked.RemoveAt(at);
        else if (book.Find(quest) is not null) _ticked.Add(quest);
        else return false;
        Version++;
        return true;
    }

    /// <summary>Moves a ticked quest one place earlier in the run. Returns true when it moved.</summary>
    public bool MoveEarlier(string quest) => Move(quest, -1);

    /// <summary>Moves a ticked quest one place later in the run. Returns true when it moved.</summary>
    public bool MoveLater(string quest) => Move(quest, 1);

    private bool Move(string quest, int step)
    {
        var at = _ticked.IndexOf(quest);
        var to = at + step;
        if (at < 0 || to < 0 || to >= _ticked.Count) return false;
        (_ticked[at], _ticked[to]) = (_ticked[to], _ticked[at]);
        Version++;
        return true;
    }

    /// <summary>Drops the ticks of quests that are no longer active (completed or abandoned). Returns true when any went.</summary>
    public bool Prune(QuestBook book)
    {
        var before = _ticked.Count;
        _ticked.RemoveAll(id => book.Find(id) is null);
        if (_ticked.Count == before) return false;
        Version++;
        return true;
    }

    /// <summary>
    /// Takes the server's <c>autopilot</c> message. Returns true when a run that was on is now off, so the caller can say why on the flash line.
    /// </summary>
    public bool Apply(AutopilotMessage message)
    {
        var ended = On && message.Mode == AutopilotModes.Off;
        Mode = message.Mode;
        Quest = message.Mode == AutopilotModes.Off ? null : message.Quest;
        Doing = message.Mode == AutopilotModes.Running ? message.Doing : null;
        Reason = message.Reason;
        Version++;
        return ended;
    }

    /// <summary>
    /// The level, job or item a pause waits for, taken from the first unmet objective of the matching type in the quest it plays (the server's pause carries only the reason);
    /// null when there is none to name.
    /// </summary>
    public string? PauseDetail(DisplayBundle bundle, QuestBook book)
    {
        if (!Paused || Quest is not { } id || book.Find(id) is not { } entry) return null;
        var type = Reason switch
        {
            AutopilotPauses.NeedsLevel => QuestObjectiveTypes.Level,
            AutopilotPauses.NeedsJob => QuestObjectiveTypes.Job,
            AutopilotPauses.NeedsEquip => QuestObjectiveTypes.Equip,
            AutopilotPauses.NeedsItemUse => QuestObjectiveTypes.UseItem,
            _ => null,
        };
        if (type is null) return null;
        foreach (var objective in entry.Objectives)
        {
            if (objective.Type != type || objective.Met) continue;
            return type == QuestObjectiveTypes.Level ? objective.Need.ToString(System.Globalization.CultureInfo.InvariantCulture) : objective.Target is { } target ? bundle.NameOf(target) : null;
        }
        return null;
    }

    /// <summary>Forgets the ticks and the run (a new login, or back on the login screen).</summary>
    public void Reset()
    {
        _ticked.Clear();
        Mode = AutopilotModes.Off;
        Quest = null;
        Doing = null;
        Reason = null;
        Version++;
    }

    /// <summary>
    /// The <c>start_autopilot</c> to send: the ticked quests in run order and the skill bar's skills in slot order (empty slots left out, level 0 = the learned level).
    /// Null with nothing ticked. A passive or unlearned skill never reaches the bar, so none is sent (the server would refuse the whole start).
    /// </summary>
    public StartAutopilotMessage? BuildStart(SkillBook skills, int slotCount)
    {
        if (_ticked.Count == 0) return null;
        var bar = new List<SkillLevelRecord>();
        for (var i = 0; i < slotCount; i++)
            if (skills.SkillAt(i) is { } skill) bar.Add(new SkillLevelRecord(skill, 0));
        return new StartAutopilotMessage(_ticked.ToArray(), bar);
    }
}
