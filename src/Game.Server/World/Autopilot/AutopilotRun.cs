using Game.Shared.Autopilot;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Autopilot;

/// <summary>How long a character's runs took in the current day (T-604). Saved with the character (T-702), so signing in again does not reset it.</summary>
public sealed class AutopilotUsage
{
    private long _dayStartedMs;
    private bool _started;

    /// <summary>Simulated milliseconds run since the day started.</summary>
    public long UsedMs { get; private set; }

    /// <summary>Starts a new day when there is none, or the last one began 24 hours ago or more. The server clock has no calendar, so a day starts with its first run.</summary>
    public void Roll(long nowMs)
    {
        if (_started && nowMs - _dayStartedMs < AutopilotRules.DayMs) return;
        _started = true;
        _dayStartedMs = nowMs;
        UsedMs = 0;
    }

    /// <summary>How long ago the day started, at <paramref name="nowMs"/>; null when none has.</summary>
    public long? DayAgeMs(long nowMs) => _started ? Math.Max(0, nowMs - _dayStartedMs) : null;

    /// <summary>A loaded character's usage (T-702): <paramref name="usedMs"/> in the day that began <paramref name="dayAgeMs"/> before <paramref name="nowMs"/>.</summary>
    public static AutopilotUsage Restore(long usedMs, long dayAgeMs, long nowMs) => new() { _started = true, _dayStartedMs = nowMs - Math.Max(0, dayAgeMs), UsedMs = Math.Max(0, usedMs) };

    /// <summary>Adds one tick of running.</summary>
    public void Add(int deltaMs) => UsedMs += deltaMs;
}

/// <summary>
/// One autopilot run of one character (T-604): the ticked quests (or, for auto-battle, T-811, the filter and anchor) and shortcut skills, where in them it is, and what it is in the middle of. Changed only by the
/// <see cref="AutopilotSystem"/> of the instance that holds the character; it is on <see cref="Entity.Autopilot"/>, so it follows the character through warps.
/// </summary>
public sealed class AutopilotRun
{
    internal AutopilotRun(Entity player, string[] quests, Slot[] skills, AutopilotUsage usage)
    {
        Player = player;
        Quests = quests;
        Ticked = new HashSet<string>(quests, StringComparer.Ordinal);
        Skills = skills;
        Usage = usage;
    }

    /// <summary>What it is doing. Its owner is told at the end of the tick, when it differs from what it was told last.</summary>
    public AutopilotStatus Status { get; internal set; } = new(AutopilotModes.Off, null, null, null);

    internal AutopilotStatus? Told;

    /// <summary>True while it plays or is paused.</summary>
    public bool Active => Status.Mode != AutopilotModes.Off;

    internal bool Running => Status.Mode == AutopilotModes.Running;

    internal readonly Entity Player;
    internal readonly string[] Quests;
    internal readonly HashSet<string> Ticked;
    internal readonly Slot[] Skills;
    internal readonly AutopilotUsage Usage;

    /// <summary>Auto-battle (T-811): what it fights and around which cell; null for a quest run.</summary>
    internal AutoBattle? Battle;
    internal bool Retaliating;        // auto-battle: its target hit it, so it may stand a little outside the radius

    // Where it is in the list, and how far the current quest was when last looked at.
    internal int Index;
    internal int Step = -1;
    internal long Counted;
    internal bool QuestDone;
    internal bool QuestAbandoned;
    internal int GoneTicks;
    internal int IdleMs;              // running without progress

    // When it thinks next.
    internal int ThinkMs;
    internal bool Wake;

    // The journey.
    internal TravelTarget? Goal;
    internal TravelCost Best = TravelCost.None;
    internal GridCell? Leg;           // where its last MoveTo went
    internal bool Walking;            // movement took it up
    internal int LegMs;               // how long to wait for that
    internal int LegCells;
    internal int SearchMs;            // the last search found nothing in reach: no new one before this is over
    internal bool Stopping;           // a StopMove for that walk is on its way

    // The fight.
    internal HuntPlan? Plan;
    internal bool Approaching;        // its walk goes toward a monster still out of sight
    internal EntityId? Target;
    internal bool Attacking;
    internal int AttackMs;            // how long to wait for AttackStarted
    internal bool Casting;
    internal int CastMs;              // how long to wait for the cast to end
    internal EntityId? Attacker;      // the last monster that hit the character
    internal int AttackerMs;          // how long that counts
    internal bool BagFull;
    internal readonly List<(EntityId Id, long UntilMs)> Avoid = new();

    // The item being fetched.
    internal EntityId? Picking;
    internal int PickMs;

    // The talk.
    internal TalkState Talk;
    internal int TalkMs;
    internal int TalkTries;
    internal int TalkPicks;
    internal string? DialogueId;
    internal string? NodeKey;
    internal readonly List<int> Shown = new();

    /// <summary>The quest it plays now; null for auto-battle.</summary>
    internal string? QuestId => Quests.Length > 0 ? Quests[Index] : null;

    /// <summary>Forgets what it was in the middle of (a new quest, a new map, a pause): the next thought starts from what is true then.</summary>
    internal void ResetPlan(int legCells)
    {
        Goal = null;
        Best = TravelCost.None;
        Leg = null;
        Walking = false;
        Stopping = false;
        LegCells = legCells;
        SearchMs = 0;
        Plan = null;
        Approaching = false;
        Target = null;
        Attacking = false;
        AttackMs = 0;
        Casting = false;
        Attacker = null;
        Retaliating = false;
        Avoid.Clear();
        Picking = null;
        Talk = TalkState.None;
        TalkTries = 0;
        TalkPicks = 0;
        Wake = true;
    }

    internal enum TalkState
    {
        None,
        Asked,   // TalkTo is on its way
        Open,    // the dialogue runs
    }

    /// <summary>An auto-battle run's filter (an <see cref="AutoBattleFilters"/> term) and its anchor: the map and cell it was started on.</summary>
    internal sealed record AutoBattle(string Filter, string MapId, GridCell Anchor);

    internal sealed class Slot(string skillId, int level, AutopilotSkillContent content)
    {
        public readonly string SkillId = skillId;
        public readonly int Level = level;
        public readonly AutopilotSkillContent Content = content;
        public int RetryMs;           // refused: not again for this long
    }
}
