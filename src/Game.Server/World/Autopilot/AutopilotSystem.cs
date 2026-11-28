using Game.Server.World.Combat;
using Game.Server.World.Dialogue;
using Game.Server.World.Effects;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Shared.Autopilot;
using Game.Shared.Definitions;
using Game.Shared.Formulas;
using Game.Shared.Progression;
using Game.Shared.Protocol;
using Game.Shared.Quests;
using Game.Shared.Time;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Autopilot;

/// <summary>
/// Plays a player's ticked quests on the server (T-604, ARCHITECTURE §6.4.2): travels, hunts, gathers, walks, talks and turns in, one quest after the other. Owns
/// <see cref="StartAutopilot"/>, <see cref="StopAutopilot"/> and <see cref="ManualInput"/>. Runs after the quest system, before the AOI.
/// </summary>
/// <remarks>
/// <para><b>It only decides.</b> Everything it does is an intent a player could have sent (<see cref="MoveTo"/>, <see cref="AttackTarget"/>, <see cref="UseSkill"/>,
/// <see cref="PickUp"/>, <see cref="TalkTo"/>, <see cref="DialogueChoose"/>, <see cref="DialogueClose"/> and the stops), queued as <see cref="Automated"/> on the instance's own queue
/// and checked by the systems that own it. It calls no other system (rule 7): it reads entities, the quest log and the events. It draws no randomness and reads no clock but the
/// simulation's, except to tell one day from the next for the daily limit.</para>
/// <para><b>Plans are never kept.</b> Each thought starts from what is true now: the current step's next unmet objective, the character's map and cell. So a warp in the middle of a
/// journey, a stolen kill or a despawned item need no handling of their own. Between thoughts (<c>autopilot.decide_interval_ms</c>, or at once when an event it waits for comes)
/// a run costs a few comparisons per tick.</para>
/// <para><b>Searching.</b> Journeys walk along distance fields worked out at boot (<see cref="TravelMap"/>): no search. Picking the nearest of several targets is one bounded search
/// (<see cref="Pathfinder.FindNearest"/>, within <c>movement</c>'s limits), and all runs of an instance share <c>autopilot.searches_per_tick</c> of them.</para>
/// </remarks>
public sealed partial class AutopilotSystem : ITickSystem, Reload.IContentReloadable
{
    private const int AnswerMs = 600;        // how long to wait for another system to take an intent up (it takes two ticks) before trying again
    private const int PickUpMs = 10_000;     // a pick-up walks at most gather_range cells
    private const int CastMs = 10_000;       // a cast walks into range first
    private const int TalkCountsMs = 1_000;  // a talk that turns something in has done so by then
    private const int SearchAgainMs = 3_000; // after a search that found nothing in reach
    private const int ApproachMs = 1_000;    // walking up to a monster out of sight: how often its nearest is looked for again
    private const int MaxTalkTries = 5;
    private const int MaxTalkPicks = 16;
    private const int RefusedSkillMs = 1_000;
    private const int GoneTicks = 3;         // a quest that left the log says why (completed, abandoned) within this many ticks
    private static readonly IReadOnlyCollection<Type> Owned = [typeof(StartAutopilot), typeof(StopAutopilot), typeof(ManualInput)];

    private AutopilotContent _content;
    private AutopilotBalanceDef _balance;
    private readonly IPlayerProgressionStore _progressions;
    private readonly IClock _clock;
    private readonly Pathfinder _pathfinder = new();
    private readonly FormulaContext _formula = new(FormulaSymbols.Default);
    private readonly SortedList<long, AutopilotRun> _runs = new();   // active ones, by player id
    private readonly List<AutopilotRun> _stepping = new();
    private readonly List<AutopilotRun> _untold = new();             // their status may have changed since their owner was told
    private readonly List<GridCell> _path = new();
    private readonly List<GridCell> _cells = new();
    private readonly List<EntityId> _ids = new();
    private MapInstance? _instance;
    private TickContext? _context;
    private long _nowMs;
    private int _searches;

    /// <summary>Creates the system for one instance. <paramref name="progressions"/> holds the quest log and the learned skills (read only); <paramref name="clock"/> tells the days apart.</summary>
    public AutopilotSystem(AutopilotContent content, IPlayerProgressionStore progressions, IClock clock)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _progressions = progressions ?? throw new ArgumentNullException(nameof(progressions));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _balance = content.Balance;
    }

    /// <summary>T-801: the new autopilot numbers and journeys. Runs of players who left the map for the reload ended there, as on a map change.</summary>
    public void Reload(Reload.ServerContent next)
    {
        _content = next.Autopilot;
        _balance = _content.Balance;
    }

    /// <inheritdoc />
    public string Name => "autopilot";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Runs on this instance (playing or paused).</summary>
    public int RunCount => _runs.Count;

    /// <summary>Path searches made so far.</summary>
    public long Searches { get; private set; }

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        _context = context;
        _nowMs = context.SimTimeMs;
        if (!ReferenceEquals(_instance, context.Instance)) Setup(context.Instance);
        _searches = _balance.SearchesPerTick;

        var intents = context.Intents;
        for (var i = 0; i < intents.Count; i++)
        {
            if (!_instance!.TryGetEntity(intents[i].Entity, out var player) || player.Kind != EntityKind.Player) continue;
            switch (intents[i])
            {
                case StartAutopilot start: Start(player, start); break;
                case StopAutopilot stop:
                    if (RunOf(player.Id) is { } stopped) Stop(stopped, stop.Reason);
                    break;
                case ManualInput input:
                    if (RunOf(player.Id) is { } taken) TakeOver(taken, input.IsOrder);
                    break;
            }
        }

        if (_runs.Count > 0)
        {
            // A different run first each tick, so the search budget is shared fairly; a copy, because a run may end in its step.
            for (var i = 0; i < _runs.Count; i++) _stepping.Add(_runs.Values[i]);
            var first = (int)(context.TickNumber % _stepping.Count);
            for (var i = 0; i < _stepping.Count; i++)
            {
                var run = _stepping[(first + i) % _stepping.Count];
                if (run.Active) StepRun(run, context.DeltaMs);
            }
            _stepping.Clear();
        }
        TellOwners();
    }

    /// <summary>One event per run whose status differs from what its owner was told: what it went through within the tick does not matter.</summary>
    private void TellOwners()
    {
        for (var i = 0; i < _untold.Count; i++)
        {
            var run = _untold[i];
            if (run.Told == run.Status || !ReferenceEquals(run.Player.Autopilot, run)) continue;   // nothing new, or replaced by another run
            run.Told = run.Status;
            _context!.Publish(new AutopilotChanged(run.Player.Id, run.Status));
        }
        _untold.Clear();
    }

    // ---- starting and ending ----------------------------------------------------------------------------------------------------------

    private void Start(Entity player, StartAutopilot start)
    {
        if (!_progressions.TryGet(player.Id, out var state)) return;
        var usage = player.AutopilotUsage ??= new AutopilotUsage();
        usage.Roll(_clock.NowMilliseconds);
        var refusal = AutopilotRules.CheckStart(_balance, player.Dead, usage.UsedMs, state, _content.Quests, SkillOf, start.Quests, start.Skills);
        if (refusal is { } no)
        {
            _context!.Publish(new AutopilotRefused(player.Id, no.Reason, no.Quest, no.Skill));
            return;
        }
        var slots = new AutopilotRun.Slot[start.Skills.Count];
        for (var i = 0; i < slots.Length; i++)
        {
            _content.TryGetSkill(start.Skills[i].Skill, out var skill);
            slots[i] = new AutopilotRun.Slot(start.Skills[i].Skill, start.Skills[i].Level, skill);
        }
        var run = new AutopilotRun(player, start.Quests.ToArray(), slots, usage);
        run.ResetPlan(_balance.LegCells);
        if (RunOf(player.Id) is { Talk: not AutopilotRun.TalkState.None }) Send(new DialogueClose(player.Id));   // replaced
        _runs[player.Id.Value] = run;
        player.Autopilot = run;
        Tell(run, new AutopilotStatus(AutopilotModes.Running, run.QuestId, AutopilotActivities.Waiting, null));
    }

    private SkillDef? SkillOf(string id) => _content.TryGetSkill(id, out var skill) ? skill.Skill.Def : null;

    /// <summary>The player took over. Its own order ends the walk or the fight; anything else (an item, a dialogue) leaves a fight going like any auto-attack, but ends a journey.</summary>
    private void TakeOver(AutopilotRun run, bool isOrder)
    {
        if (!isOrder && (run.Leg is not null || run.Picking is not null)) Send(new StopMove(run.Player.Id));
        End(run, AutopilotStops.ManualInput);
    }

    /// <summary>The run is over and the character stops what the run had it do.</summary>
    private void Stop(AutopilotRun run, string reason)
    {
        if (!run.Player.Dead) Halt(run);
        End(run, reason);
    }

    private void End(AutopilotRun run, string reason)
    {
        _runs.Remove(run.Player.Id.Value);
        Tell(run, new AutopilotStatus(AutopilotModes.Off, null, null, reason));
    }

    private void Pause(AutopilotRun run, string reason)
    {
        Halt(run);
        run.ResetPlan(_balance.LegCells);
        Tell(run, new AutopilotStatus(AutopilotModes.Paused, run.QuestId, null, reason));
    }

    private void Resume(AutopilotRun run)
    {
        run.ResetPlan(_balance.LegCells);
        run.IdleMs = 0;
        Tell(run, new AutopilotStatus(AutopilotModes.Running, run.QuestId, AutopilotActivities.Waiting, null));
    }

    private void Halt(AutopilotRun run)
    {
        Send(new StopMove(run.Player.Id));
        Send(new StopAttack(run.Player.Id));
        if (run.Talk != AutopilotRun.TalkState.None) Send(new DialogueClose(run.Player.Id));
    }

    private void Doing(AutopilotRun run, string activity)
    {
        if (!string.Equals(run.Status.Doing, activity, StringComparison.Ordinal) || !string.Equals(run.Status.QuestId, run.QuestId, StringComparison.Ordinal))
            Tell(run, new AutopilotStatus(AutopilotModes.Running, run.QuestId, activity, null));
    }

    private void Tell(AutopilotRun run, AutopilotStatus status)
    {
        run.Status = status;
        _untold.Add(run);
    }

    /// <summary>Queues an intent as the player would. False when the queue is full: it is thought of again.</summary>
    private bool Send(IIntent intent) => _instance!.TryEnqueue(new Automated(intent)) == EnqueueResult.Accepted;

    // ---- one tick of one run -------------------------------------------------------------------------------------------------------------

    private void StepRun(AutopilotRun run, int deltaMs)
    {
        if (run.Player.Dead)
        {
            Stop(run, AutopilotStops.Died);
            return;
        }
        if (run.QuestAbandoned)
        {
            Stop(run, AutopilotStops.QuestAbandoned);
            return;
        }
        if (run.Running)
        {
            run.Usage.Roll(_clock.NowMilliseconds);
            run.Usage.Add(deltaMs);
            if (run.Usage.UsedMs >= AutopilotRules.DailyLimitMs(_balance))
            {
                Stop(run, AutopilotStops.TimeLimit);
                return;
            }
            run.IdleMs += deltaMs;
        }
        CountDown(run, deltaMs);

        if (!_progressions.TryGet(run.Player.Id, out var state)) return;
        if (!state.ActiveQuests.TryGetValue(run.QuestId, out var progress))
        {
            // The event that says why (completed or abandoned) comes at the end of this tick; without one (it came on another map) the log decides.
            if (!run.QuestDone && ++run.GoneTicks < GoneTicks) return;
            if (run.QuestDone || state.CompletedQuests.Contains(run.QuestId)) NextQuest(run, state);
            else Stop(run, AutopilotStops.QuestAbandoned);
            return;
        }
        run.GoneTicks = 0;

        if (Moved(run, progress))
        {
            run.IdleMs = 0;
            run.TalkTries = 0;
            run.TalkPicks = 0;
            run.Wake = true;
            if (run.Talk == AutopilotRun.TalkState.Open) CloseTalk(run);
            if (!run.Running) Resume(run);
        }
        if (!run.Running) return;
        if (run.BagFull)
        {
            Stop(run, AutopilotStops.BagFull);
            return;
        }
        if (run.IdleMs >= _balance.NoProgressMs)
        {
            Pause(run, AutopilotPauses.NoProgress);
            return;
        }
        if (!run.Wake && run.ThinkMs > 0) return;
        run.Wake = false;
        run.ThinkMs = _balance.DecideIntervalMs;
        Think(run, state, progress);
    }

    private static void CountDown(AutopilotRun run, int ms)
    {
        run.ThinkMs = Math.Max(0, run.ThinkMs - ms);
        run.LegMs = Math.Max(0, run.LegMs - ms);
        run.SearchMs = Math.Max(0, run.SearchMs - ms);
        run.AttackMs = Math.Max(0, run.AttackMs - ms);
        run.AttackerMs = Math.Max(0, run.AttackerMs - ms);
        run.PickMs = Math.Max(0, run.PickMs - ms);
        run.TalkMs = Math.Max(0, run.TalkMs - ms);
        if (run.Casting && (run.CastMs -= ms) <= 0) run.Casting = false;
        for (var i = 0; i < run.Skills.Length; i++) run.Skills[i].RetryMs = Math.Max(0, run.Skills[i].RetryMs - ms);
    }

    /// <summary>True when the quest's step or counters are not what they were when last looked at (the first look never is).</summary>
    private static bool Moved(AutopilotRun run, QuestProgress progress)
    {
        long counted = 0;
        for (var i = 0; i < progress.Counters.Length; i++) counted += progress.Counters[i];
        var first = run.Step < 0;
        var moved = !first && (run.Step != progress.Step || run.Counted != counted);
        run.Step = progress.Step;
        run.Counted = counted;
        return moved;
    }

    private void NextQuest(AutopilotRun run, ProgressionState state)
    {
        if (run.Talk == AutopilotRun.TalkState.Open) CloseTalk(run);
        do run.Index++;
        while (run.Index < run.Quests.Length && !state.ActiveQuests.ContainsKey(run.QuestId));   // done on the way, or dropped meanwhile
        if (run.Index >= run.Quests.Length)
        {
            run.Index = run.Quests.Length - 1;
            Stop(run, AutopilotStops.Finished);
            return;
        }
        run.Step = -1;
        run.QuestDone = false;
        run.GoneTicks = 0;
        run.IdleMs = 0;
        run.ResetPlan(_balance.LegCells);
        Tell(run, new AutopilotStatus(AutopilotModes.Running, run.QuestId, AutopilotActivities.Waiting, null));
    }

    // ---- what to do next ------------------------------------------------------------------------------------------------------------------

    private void Think(AutopilotRun run, ProgressionState state, QuestProgress progress)
    {
        if (run.Player.CannotMove) return;   // a stun: every order would be refused
        if (progress.Waiting) return;     // the turn-in is being settled
        if (progress.Held is QuestReasons.InventoryFull or QuestReasons.TooHeavy)
        {
            Stop(run, AutopilotStops.BagFull);
            return;
        }
        var objectives = _content.Quests.Objectives(progress);
        var index = AutopilotRules.NextObjective(objectives, progress.Counters);
        if (index < 0) return;               // met: the quest system turns it in on its next tick
        var objective = objectives[index];
        if (run.Talk == AutopilotRun.TalkState.Open && objective is not TalkObjective) CloseTalk(run);
        if (AutopilotRules.PauseFor(objective) is { } pause)
        {
            Pause(run, pause);
            return;
        }
        switch (objective)
        {
            case KillObjective kill: Hunt(run, state, progress, objectives, _content.HuntFor(kill)); break;
            case CollectObjective collect: Hunt(run, state, progress, objectives, _content.HuntFor(collect)); break;
            case ReachObjective reach: Reach(run, reach); break;
            case TalkObjective talk: Talk(run, talk); break;
        }
    }

    // ---- travelling --------------------------------------------------------------------------------------------------------------------------

    private void Reach(AutopilotRun run, ReachObjective reach)
    {
        if (_content.PlaceOf(reach) is not { } place)
        {
            Pause(run, AutopilotPauses.NoSource);
            return;
        }
        var route = _content.Travel.Plan(_instance!.MapId, run.Player.Cell, place);
        if (!route.Cost.Reachable) Pause(run, AutopilotPauses.Unreachable);
        else if (!There(route)) Travel(run, place, route);   // there: the quest system counts it on its next tick
    }

    /// <summary>The nearest of several places: the fewest warps, then the shortest walk, then the first. Null when none can be reached.</summary>
    private TravelTarget? Nearest(AutopilotRun run, IReadOnlyList<TravelTarget> places, out TravelRoute route)
    {
        route = default;
        TravelTarget? best = null;
        for (var i = 0; i < places.Count; i++)
        {
            var candidate = _content.Travel.Plan(_instance!.MapId, run.Player.Cell, places[i]);
            if (!candidate.Cost.Reachable || (best is not null && candidate.Cost.CompareTo(route.Cost) >= 0)) continue;
            best = places[i];
            route = candidate;
        }
        return best;
    }

    private static bool There(TravelRoute route) => route.Cost is { Warps: 0, Walk: 0 };

    /// <summary>One thought of a journey: walk on along the route. Getting nearer than ever before on this journey counts as progress, so a long one does not pause and one in circles does.</summary>
    private void Travel(AutopilotRun run, TravelTarget target, TravelRoute route)
    {
        if (!ReferenceEquals(run.Goal, target))
        {
            run.Goal = target;
            run.Best = TravelCost.None;
            run.Leg = null;
        }
        if (route.Cost.CompareTo(run.Best) < 0)
        {
            run.Best = route.Cost;
            run.IdleMs = 0;
        }
        run.Target = null;   // walking away ends an auto-attack
        Doing(run, AutopilotActivities.Travelling);

        var cell = run.Player.Cell;
        if (run.Leg is { } leg)
        {
            if (!run.Walking && run.LegMs > 0) return;   // movement has not taken it up yet
            // The next leg is asked for a little before this one ends, so the character does not stop in between.
            if (run.Walking && (route.Field!.At(leg) == 0 || GridDistance.Chebyshev(cell, leg) > 3)) return;
        }
        var stop = _content.Travel.NextStop(_instance!.MapId, cell, route.Field!, run.LegCells);
        if (!Same(stop, cell)) Walk(run, stop);
    }

    private void Walk(AutopilotRun run, GridCell cell)
    {
        if (!Send(new MoveTo(run.Player.Id, cell))) return;
        run.Leg = cell;
        run.Walking = false;
        run.Stopping = false;
        run.LegMs = AnswerMs;
    }

    // ---- talking -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Goes to the NPC and talks. A <c>talk</c> objective counts when the talk starts, so the dialogue is closed as soon as the quest moved. When it did not, the dialogue is followed
    /// over the choices <see cref="AutopilotRules.ChooseDialogue"/> allows; when it allows none the run closes it and pauses.
    /// </summary>
    private void Talk(AutopilotRun run, TalkObjective talk)
    {
        var place = Nearest(run, _content.PlacesOf(talk), out var route);
        if (place is null)
        {
            Pause(run, _content.PlacesOf(talk).Count == 0 ? AutopilotPauses.NoSource : AutopilotPauses.Unreachable);
            return;
        }
        if (!There(route))
        {
            run.Talk = AutopilotRun.TalkState.None;
            Travel(run, place, route);
            return;
        }
        run.Leg = null;
        switch (run.Talk)
        {
            case AutopilotRun.TalkState.None:
                if (NpcInReach(run.Player, talk.NpcId) is not { } npc) return;
                if (++run.TalkTries > MaxTalkTries)
                {
                    Pause(run, AutopilotPauses.NoSafeChoice);
                    return;
                }
                // As the gateway does for a player's talk: the auto-attack stops first, so combat's walk does not end the talk.
                if (!Send(new StopAttack(run.Player.Id)) || !Send(new TalkTo(run.Player.Id, npc))) return;
                run.Talk = AutopilotRun.TalkState.Asked;
                run.TalkMs = AnswerMs;
                run.NodeKey = null;
                Doing(run, AutopilotActivities.Talking);
                break;
            case AutopilotRun.TalkState.Asked:
                if (run.TalkMs <= 0) run.Talk = AutopilotRun.TalkState.None;
                break;
            case AutopilotRun.TalkState.Open:
                if (run.TalkMs > 0) return;
                var choice = run.NodeKey is not null && run.TalkPicks < MaxTalkPicks && _content.TryGetDialogue(run.DialogueId!, out var dialogue)
                    ? AutopilotRules.ChooseDialogue(dialogue, run.NodeKey, run.Shown, run.Ticked)
                    : null;
                if (choice is not { } index)
                {
                    Pause(run, AutopilotPauses.NoSafeChoice);   // closes it
                    return;
                }
                run.TalkPicks++;
                run.TalkMs = TalkCountsMs;
                Send(new DialogueChoose(run.Player.Id, index));
                break;
        }
    }

    private EntityId? NpcInReach(Entity player, string npcId)
    {
        EntityId? best = null;
        var bestDistance = int.MaxValue;
        var entities = _instance!.Entities;
        for (var i = 0; i < entities.Count; i++)   // not foreach: an interface's enumerator is an allocation
        {
            var entity = entities[i];
            if (entity.Kind != EntityKind.Npc || !string.Equals(entity.DefinitionId, npcId, StringComparison.Ordinal)) continue;
            var distance = GridDistance.Chebyshev(player.Cell, entity.Cell);
            if (distance > _content.TalkRange || distance >= bestDistance) continue;
            best = entity.Id;
            bestDistance = distance;
        }
        return best;
    }

    private void CloseTalk(AutopilotRun run)
    {
        Send(new DialogueClose(run.Player.Id));
        run.Talk = AutopilotRun.TalkState.None;
        run.NodeKey = null;
    }

    // ---- what the world tells it ----------------------------------------------------------------------------------------------------------

    private void Setup(MapInstance instance)
    {
        _instance = instance;
        var events = instance.Events;
        events.Subscribe<EntityEnteredMap>(OnEntered);
        events.Subscribe<EntityLeftMap>(OnLeft);
        events.Subscribe<MovementStarted>(e =>
        {
            if (RunOf(e.Entity) is { Leg: { } leg } run && Same(leg, e.Goal)) run.Walking = true;
        });
        events.Subscribe<MovementStopped>(e =>
        {
            if (RunOf(e.Entity) is not { Leg: { } leg } run) return;
            if (!run.Walking && !Same(leg, e.Cell)) return;   // the end of the walk before
            run.Leg = null;
            run.Walking = false;
            run.Stopping = false;
            run.Wake = true;
        });
        events.Subscribe<MoveRejected>(e =>
        {
            if (RunOf(e.Entity) is not { Leg: { } leg } run || !Same(leg, e.Target)) return;
            run.Leg = null;
            run.Walking = false;
            run.LegCells = Math.Max(1, run.LegCells / 2);   // too far for one search: in smaller pieces
        });
        events.Subscribe<AttackStarted>(e =>
        {
            if (RunOf(e.Attacker) is { } run && run.Target == e.Target) run.Attacking = true;
        });
        events.Subscribe<AttackStopped>(e => OnAttackOver(e.Attacker, e.Target, e.Reason));
        events.Subscribe<AttackRefused>(e => OnAttackOver(e.Attacker, e.Target, e.Reason));
        events.Subscribe<SkillRequested>(e => OnCastOver(e.Caster, null));
        events.Subscribe<SkillRefused>(e => OnCastOver(e.Caster, e.SkillId));
        events.Subscribe<SkillCastEnded>(e => OnCastOver(e.Caster, null));
        events.Subscribe<DamageDealt>(e =>
        {
            if (e.Amount <= 0 || RunOf(e.Target) is not { } run) return;
            run.Attacker = e.Source;
            run.AttackerMs = _balance.RetaliateMs;
        });
        events.Subscribe<MonsterKilled>(e =>
        {
            for (var i = 0; i < e.Credited.Count; i++)
                if (RunOf(e.Credited[i]) is { Plan: { } plan } run && plan.Monsters.Contains(e.MonsterId)) run.IdleMs = 0;
        });
        events.Subscribe<ItemAcquired>(e =>
        {
            if (RunOf(e.Player) is not { } run || run.Picking != e.Source) return;
            run.Picking = null;
            run.Wake = true;
        });
        events.Subscribe<ItemRefused>(OnItemRefused);
        events.Subscribe<NpcTalked>(e =>
        {
            if (RunOf(e.Player) is not { Talk: AutopilotRun.TalkState.Asked } run) return;
            run.Talk = AutopilotRun.TalkState.Open;
            run.TalkMs = TalkCountsMs;
        });
        events.Subscribe<DialogueShown>(e =>
        {
            if (RunOf(e.Player) is not { Talk: AutopilotRun.TalkState.Open } run) return;
            run.DialogueId = e.DialogueId;
            run.NodeKey = e.NodeKey;
            run.Shown.Clear();
            for (var i = 0; i < e.Choices.Count; i++) run.Shown.Add(e.Choices[i].Index);
        });
        events.Subscribe<DialogueEnded>(e =>
        {
            if (RunOf(e.Player) is not { Talk: AutopilotRun.TalkState.Open } run) return;
            run.Talk = AutopilotRun.TalkState.None;
            run.NodeKey = null;
        });
        events.Subscribe<DialogueRefused>(e =>
        {
            if (RunOf(e.Player) is { Talk: AutopilotRun.TalkState.Asked } run && e.Intent == MessageTypes.Talk) run.Talk = AutopilotRun.TalkState.None;
        });
        events.Subscribe<QuestCompleted>(e =>
        {
            if (RunOf(e.Player) is not { } run || !string.Equals(run.QuestId, e.QuestId, StringComparison.Ordinal)) return;
            run.QuestDone = true;
        });
        events.Subscribe<QuestAbandoned>(e =>
        {
            if (RunOf(e.Player) is { } run && string.Equals(run.QuestId, e.QuestId, StringComparison.Ordinal)) run.QuestAbandoned = true;
        });
    }

    /// <summary>The active run of a player that is on this instance now. A player that left in this tick's phase 3 belongs to another thread already: null.</summary>
    private AutopilotRun? RunOf(EntityId player) =>
        _runs.Count > 0 && _runs.TryGetValue(player.Value, out var run) && _instance!.TryGetEntity(player, out _) ? run : null;

    private void OnEntered(EntityEnteredMap e)
    {
        if (e.Kind != EntityKind.Player || !_instance!.TryGetEntity(e.Entity, out var player) || player.Autopilot is not { Active: true } run) return;
        _runs[e.Entity.Value] = run;
        run.ResetPlan(_balance.LegCells);   // whatever took it here, the next thought starts from here
        if (run.Status is { Mode: AutopilotModes.Paused, Reason: AutopilotPauses.Unreachable }) Resume(run);
    }

    private void OnLeft(EntityLeftMap e)
    {
        if (_runs.Count == 0 || _instance!.TryGetEntity(e.Entity, out _)) return;   // a same-map warp comes straight back
        if (!_runs.Remove(e.Entity.Value, out var run)) return;
        // Only a character that left the world is still ours to touch: one that warped is on the other instance's thread.
        if (_instance.World.LocationOf(e.Entity) is not null) return;
        run.Status = run.Told = new AutopilotStatus(AutopilotModes.Off, null, null, AutopilotStops.Logout);
        _instance.Events.Publish(new AutopilotChanged(e.Entity, run.Status));
    }

    private void OnAttackOver(EntityId attacker, EntityId target, string reason)
    {
        if (RunOf(attacker) is not { } run || run.Target != target) return;
        run.Attacking = false;
        run.AttackMs = 0;
        run.Wake = true;
        switch (reason)
        {
            case AttackReasons.Overweight: run.BagFull = true; break;
            case AttackReasons.Unreachable or AttackReasons.InvalidTarget:
                Avoid(run, target);
                run.Target = null;
                break;
            case AttackReasons.TargetDead or AttackReasons.TargetGone: run.Target = null; break;
        }
    }

    private void OnCastOver(EntityId caster, string? refused)
    {
        if (RunOf(caster) is not { Casting: true } run) return;
        run.Casting = false;
        run.Wake = true;
        if (refused is null) return;
        for (var i = 0; i < run.Skills.Length; i++)
            if (string.Equals(run.Skills[i].SkillId, refused, StringComparison.Ordinal)) run.Skills[i].RetryMs = RefusedSkillMs;
    }

    private void OnItemRefused(ItemRefused e)
    {
        if (e.Intent != MessageTypes.PickUp || RunOf(e.Player) is not { Picking: { } item } run || item.Value != e.Target) return;
        run.Picking = null;
        run.Wake = true;
        if (e.Reason is ItemReasons.InventoryFull or ItemReasons.TooHeavy) run.BagFull = true;
        else if (e.Reason is ItemReasons.Unreachable or ItemReasons.NotYours) Avoid(run, item);   // cancelled or gone: look again
    }

    private static bool Same(GridCell a, GridCell b) => a.X == b.X && a.Y == b.Y;
}
