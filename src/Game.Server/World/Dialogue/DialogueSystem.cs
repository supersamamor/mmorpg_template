using Game.Server.Events;
using Game.Server.World.Combat;
using Game.Server.World.Inventory;
using Game.Server.World.Quests;
using Game.Shared.Definitions;
using Game.Shared.Dialogue;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Server.World.Dialogue;

/// <summary>
/// NPCs in the world and the dialogues players have with them (ARCHITECTURE §6.4). Owns <see cref="TalkTo"/>, <see cref="DialogueChoose"/> and
/// <see cref="DialogueClose"/>. Runs after the monster AI and before combat.
/// </summary>
/// <remarks>
/// <para><b>NPC entities.</b> On the instance's first tick every <c>npcs</c> placement of the map grid becomes an <see cref="EntityKind.Npc"/> entity on its cell,
/// facing as placed, with <see cref="Entity.DefinitionId"/> the NPC id. An NPC has no stats and no vitals, so it never moves, is not a combatant (attacks and
/// skills refuse it as a target, areas skip it) and the AOI shows it like any entity (crowd cap included). It does <b>not</b> block its cell: entities never
/// block each other in v1 (see <c>MovementSystem</c>); a tap on an NPC is a <c>talk</c>, so players stop beside it.</para>
/// <para><b>Talking.</b> A <c>talk</c> is refused when the player is dead, the target is gone or not an NPC, or the NPC has no dialogue. From beyond
/// <c>dialogue.talk_range</c> (Chebyshev) the player is walked in first, like an auto-attack; a new order cancels the walk. One dialogue per player: a new talk
/// ends the old one (<c>replaced</c>). It ends when the player walks beyond the range, warps, dies or leaves the world, or the NPC is gone. <see cref="NpcTalked"/> is
/// raised when it starts.</para>
/// <para><b>The graph.</b> The system keeps the player's node and sends it (<see cref="DialogueShown"/>): the speaker, the text and the <i>shown</i> choices
/// (those whose conditions hold) with their original indices. <see cref="DialogueChoose"/> moves on; the client never decides where a choice leads. A node without
/// shown choices is a "continue": index 0 follows <c>next</c>, and a null <c>next</c> ends the dialogue (<c>finished</c>).</para>
/// <para><b>Actions</b> run in order when a choice is picked. A choice's <c>take_item</c> / <c>give_item</c> actions are one transaction the inventory applies
/// (<see cref="DialogueItemsRequested"/> / <see cref="DialogueItemsResolved"/>: all or nothing); until it answers the dialogue is busy. When it refuses, nothing
/// changed and the player stays at the node. The other actions (<c>start_quest</c>, <c>set_save_point</c>, <c>run_effects</c>, <c>open_service</c>) then run and
/// the dialogue moves on. <c>run_effects</c> is an event combat runs (rule 7), so its effects apply one tick later, like a consumable's.</para>
/// <para><b>Services.</b> <c>open_service</c> sets <see cref="Entity.OpenService"/>; it stays until the player leaves talking range, warps, dies, closes
/// or talks to someone else, even after the graph has finished.</para>
/// </remarks>
public sealed class DialogueSystem : ITickSystem
{
    private static readonly IReadOnlyCollection<Type> Owned = [typeof(TalkTo), typeof(DialogueChoose), typeof(DialogueClose)];

    private readonly DialogueContent _content;
    private readonly ISavePointStore _savePoints;
    private readonly IPlayerProgressionStore? _progressions;
    private readonly IInventoryStore? _inventories;
    private readonly SortedDictionary<long, Session> _sessions = new();
    private readonly SortedDictionary<long, Walk> _walks = new();
    private List<DialogueItemsResolved> _resolved = new();
    private List<DialogueItemsResolved> _resolving = new();
    private readonly List<Session> _scratch = new();
    private readonly PlayerView _view;
    private readonly SortedSet<long> _stale = new();   // players whose quest standing or levels changed: their open node's choices are checked again
    private MapInstance? _instance;
    private TickContext? _context;
    private long _nextToken = 1;

    /// <summary>
    /// Creates the dialogue system for one instance. <paramref name="savePoints"/> is shared by every instance; <paramref name="progressions"/> and
    /// <paramref name="inventories"/> are read (never changed) to evaluate conditions: without them level, job and item conditions see a new character.
    /// </summary>
    public DialogueSystem(DialogueContent content, ISavePointStore savePoints, IPlayerProgressionStore? progressions = null, IInventoryStore? inventories = null)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _savePoints = savePoints ?? throw new ArgumentNullException(nameof(savePoints));
        _progressions = progressions;
        _inventories = inventories;
        _view = new PlayerView(new Game.Shared.Quests.QuestRules(content.Quests, content.QuestBalance.MaxActive));
    }

    /// <inheritdoc />
    public string Name => "dialogue";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => Owned;

    /// <summary>Players in a dialogue (or holding an open service) on this instance.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>Players walking to an NPC to talk.</summary>
    public int WalkingCount => _walks.Count;

    /// <summary>NPC entities this system spawned.</summary>
    public int NpcCount { get; private set; }

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        _context = context;
        if (!ReferenceEquals(_instance, context.Instance)) Setup(context);

        (_resolved, _resolving) = (_resolving, _resolved);
        for (var i = 0; i < _resolving.Count; i++) OnItemsResolved(_resolving[i]);
        _resolving.Clear();

        var intents = context.Intents;
        for (var i = 0; i < intents.Count; i++)
        {
            if (!_instance!.TryGetEntity(intents[i].Entity, out var player)) continue;
            switch (intents[i])
            {
                case TalkTo talk: Talk(player, talk.Target); break;
                case DialogueChoose choose: Choose(player, choose.Index); break;
                case DialogueClose: Close(player); break;
            }
        }

        RunWalks();
        CheckSessions();
        RefreshStale();
    }

    // ---- setup ---------------------------------------------------------------------------------------------------------------------------

    private void Setup(TickContext context)
    {
        _instance = context.Instance;
        var events = _instance.Events;
        events.Subscribe<EntityLeftMap>(OnLeft);
        events.Subscribe<MovementStarted>(OnMovementStarted);
        events.Subscribe<MovementStopped>(OnMovementStopped);
        events.Subscribe<MoveRejected>(OnMoveRejected);
        events.Subscribe<DialogueItemsResolved>(e => _resolved.Add(e));
        // The choices on screen follow the quest log (T-602): a talk that turned a quest in, a start, an abandon, a level or a job change shows the node again.
        events.Subscribe<QuestStarted>(e => _stale.Add(e.Player.Value));
        events.Subscribe<QuestStepCompleted>(e => _stale.Add(e.Player.Value));
        events.Subscribe<QuestCompleted>(e => _stale.Add(e.Player.Value));
        events.Subscribe<QuestAbandoned>(e => _stale.Add(e.Player.Value));
        events.Subscribe<PlayerProgressed>(e => _stale.Add(e.Player.Value));

        // A placement of an NPC the content does not have is a boot error (hard rule 5); the validator reports it first.
        foreach (var placement in _instance.Grid.Npcs)
        {
            if (!_content.TryGetNpc(placement.NpcId, out _))
                throw new InvalidOperationException($"Map '{_instance.MapId}' places unknown npc '{placement.NpcId}'.");
            var npcId = placement.NpcId;
            context.Spawn(EntityKind.Npc, placement.Cell, placement.Facing, e => e.DefinitionId = npcId);
            NpcCount++;
        }
    }

    // ---- talking ------------------------------------------------------------------------------------------------------------------------

    private void Talk(Entity player, EntityId targetId)
    {
        if (player.Kind != EntityKind.Player) return;
        if (player.Dead)
        {
            Refuse(player, MessageTypes.Talk, DialogueReasons.Dead, targetId);
            return;
        }
        if (!_instance!.TryGetEntity(targetId, out var npc))
        {
            Refuse(player, MessageTypes.Talk, DialogueReasons.TargetGone, targetId);
            return;
        }
        if (npc.Kind != EntityKind.Npc || npc.DefinitionId is null || !_content.TryGetNpc(npc.DefinitionId, out var def))
        {
            Refuse(player, MessageTypes.Talk, DialogueReasons.InvalidTarget, targetId);
            return;
        }
        if (def.DialogueId is null || !_content.TryGetDialogue(def.DialogueId, out _))
        {
            Refuse(player, MessageTypes.Talk, DialogueReasons.NoDialogue, targetId);
            return;
        }

        _walks.Remove(player.Id.Value);   // a new talk replaces an earlier walk
        if (InRange(player, npc))
        {
            Begin(player, npc, def);
            return;
        }
        _walks.Add(player.Id.Value, new Walk(player, npc.Id, npc.Cell, _context!.TickNumber));
        _instance.TryEnqueue(new MoveTo(player.Id, npc.Cell));
    }

    private void RunWalks()
    {
        if (_walks.Count == 0) return;
        _scratch.Clear();
        var done = new List<(Walk Walk, string? Reason)>();
        foreach (var walk in _walks.Values)
        {
            string? reason = null;
            var finished = false;
            if (walk.Player.Dead) reason = DialogueReasons.Dead;
            else if (walk.EndReason is { } ended) reason = ended;
            else if (!_instance!.TryGetEntity(walk.Npc, out var npc)) reason = DialogueReasons.TargetGone;
            else if (InRange(walk.Player, npc))
            {
                if (walk.Walking) _instance.TryEnqueue(new StopMove(walk.Player.Id));
                finished = true;
            }
            else if (!walk.Walking && _context!.TickNumber - walk.IssuedTick > WalkGraceTicks) reason = DialogueReasons.Unreachable;
            if (finished || reason is not null) done.Add((walk, reason));
        }
        foreach (var (walk, reason) in done)
        {
            _walks.Remove(walk.Player.Id.Value);
            if (reason is not null)
            {
                Refuse(walk.Player, MessageTypes.Talk, reason, walk.Npc.Value);
                continue;
            }
            if (_instance!.TryGetEntity(walk.Npc, out var npc) && npc.DefinitionId is { } id && _content.TryGetNpc(id, out var def)) Begin(walk.Player, npc, def);
        }
    }

    private const int WalkGraceTicks = 3;   // a walk that never started within this many ticks has no way there

    private void Begin(Entity player, Entity npc, NpcDef def)
    {
        End(player.Id.Value, DialogueEndReasons.Replaced);
        _content.TryGetDialogue(def.DialogueId!, out var dialogue);
        var session = new Session(player, npc.Id, def, dialogue);
        _sessions[player.Id.Value] = session;
        _context!.Publish(new NpcTalked(player.Id, npc.Id, def.Id));
        Show(session, dialogue.Start);
    }

    // ---- the graph ----------------------------------------------------------------------------------------------------------------------

    private void Show(Session session, string nodeKey)
    {
        session.NodeKey = nodeKey;
        var node = session.Dialogue.Nodes[nodeKey];
        SetView(session.Player);
        var shown = new List<DialogueChoiceRecord>(node.Choices.Count);
        for (var i = 0; i < node.Choices.Count; i++)
            if (DialogueRules.Matches(node.Choices[i].Conditions, _view)) shown.Add(new DialogueChoiceRecord(i, node.Choices[i].Text));
        session.Shown = shown.Select(c => c.Index).ToArray();
        _context!.Publish(new DialogueShown(session.Player.Id, session.Npc, session.Dialogue.Id, nodeKey, node.SpeakerNpcId ?? session.NpcDef.Id,
            node.PortraitAssetKey, node.Text, shown, More: node.Next is not null));
    }

    private void Choose(Entity player, int index)
    {
        if (!_sessions.TryGetValue(player.Id.Value, out var session) || session.NodeKey is null)
        {
            Refuse(player, MessageTypes.DialogueChoice, DialogueReasons.NoDialogue);
            return;
        }
        if (session.Token != 0)
        {
            Refuse(player, MessageTypes.DialogueChoice, DialogueReasons.Busy);
            return;
        }

        var node = session.Dialogue.Nodes[session.NodeKey];
        SetView(player);
        var anyShown = false;
        for (var i = 0; i < node.Choices.Count && !anyShown; i++) anyShown = DialogueRules.Matches(node.Choices[i].Conditions, _view);
        if (!anyShown)
        {
            // A "continue": the only valid index is 0, and the node's own next decides.
            if (index != 0)
            {
                Refuse(player, MessageTypes.DialogueChoice, DialogueReasons.InvalidChoice);
                return;
            }
            Advance(session, node.Next);
            return;
        }
        if ((uint)index >= (uint)node.Choices.Count || !DialogueRules.Matches(node.Choices[index].Conditions, _view))
        {
            Refuse(player, MessageTypes.DialogueChoice, DialogueReasons.InvalidChoice);
            return;
        }

        var choice = node.Choices[index];
        session.ChoiceIndex = index;
        List<ItemQuantity>? takes = null, gives = null;
        foreach (var action in choice.Actions)
        {
            if (action is TakeItemAction take) (takes ??= new()).Add(new ItemQuantity(take.ItemId, take.Quantity));
            else if (action is GiveItemAction give) (gives ??= new()).Add(new ItemQuantity(give.ItemId, give.Quantity));
        }
        if (takes is not null || gives is not null)
        {
            session.Token = _nextToken++;
            _context!.Publish(new DialogueItemsRequested(player.Id, session.Token, takes ?? (IReadOnlyList<ItemQuantity>)[], gives ?? (IReadOnlyList<ItemQuantity>)[]));
            return;
        }
        Apply(session, node, index);
    }

    /// <summary>The inventory answered a choice's item transaction: on success the other actions run and the dialogue moves on; on refusal nothing changed.</summary>
    private void OnItemsResolved(DialogueItemsResolved e)
    {
        if (!_sessions.TryGetValue(e.Player.Value, out var session) || session.Token != e.Token) return;
        session.Token = 0;
        if (e.Reason is not null)
        {
            Refuse(session.Player, MessageTypes.DialogueChoice, e.Reason);
            return;
        }
        if (session.NodeKey is null) return;
        Apply(session, session.Dialogue.Nodes[session.NodeKey], session.ChoiceIndex);
    }

    /// <summary>Runs the choice's actions that are not item actions, in order, then moves to its <c>next</c>.</summary>
    private void Apply(Session session, DialogueNodeDef node, int index)
    {
        var choice = node.Choices[index];
        var nodeKey = session.NodeKey!;
        for (var a = 0; a < choice.Actions.Count; a++)
        {
            switch (choice.Actions[a])
            {
                case StartQuestAction quest:
                    _context!.Publish(new QuestStartRequested(session.Player.Id, quest.QuestId, session.NpcDef.Id));
                    break;
                case SetSavePointAction save:
                    var point = new SavePoint(_instance!.MapId, new GridCell(save.X, save.Y));
                    _savePoints.Set(session.Player.Id, point);
                    _context!.Publish(new SavePointSet(session.Player.Id, point.MapId, point.Cell));
                    break;
                case RunEffectsAction:
                    _context!.Publish(new DialogueEffectsRequested(session.Player.Id, DialogueActionKeys.Of(session.Dialogue.Id, nodeKey, index, a)));
                    break;
                case OpenServiceAction service:
                    session.Player.OpenService = service.Service;
                    session.Player.OpenShopId = service.ShopId;
                    session.Player.OpenServiceNpc = session.Npc;
                    session.Service = service.Service;
                    _context!.Publish(new ServiceOpened(session.Player.Id, session.Npc, service.Service, service.ShopId));
                    break;
            }
        }
        Advance(session, choice.Next);
    }

    private void Advance(Session session, string? next)
    {
        if (next is not null && session.Dialogue.Nodes.ContainsKey(next))
        {
            Show(session, next);
            return;
        }
        if (session.Service is null)
        {
            End(session.Player.Id.Value, DialogueEndReasons.Finished);
            return;
        }
        // The graph is over but its service stays open until the player walks away: the client closes the panel, the session goes on without a node.
        session.NodeKey = null;
        _context!.Publish(new DialogueEnded(session.Player.Id, session.Npc, DialogueEndReasons.Finished));
    }

    /// <summary>
    /// T-602: a player whose quest log or levels changed sees its open node again when the choices it shows changed (a turn-in by talking completes a step a tick or two after
    /// the node was first shown). A choice that was on screen and no longer holds is refused <c>invalid_choice</c> as before.
    /// </summary>
    private void RefreshStale()
    {
        if (_stale.Count == 0) return;
        foreach (var id in _stale)
        {
            if (!_sessions.TryGetValue(id, out var session) || session.NodeKey is null || session.Token != 0) continue;
            var node = session.Dialogue.Nodes[session.NodeKey];
            SetView(session.Player);
            var shown = new List<int>(node.Choices.Count);
            for (var i = 0; i < node.Choices.Count; i++)
                if (DialogueRules.Matches(node.Choices[i].Conditions, _view)) shown.Add(i);
            if (session.Shown is { } before && before.SequenceEqual(shown)) continue;
            Show(session, session.NodeKey);
        }
        _stale.Clear();
    }

    private void SetView(Entity player) => _view.Set(player, _progressions, _inventories, _context?.SimTimeMs ?? 0);

    private void Close(Entity player)
    {
        _walks.Remove(player.Id.Value);
        End(player.Id.Value, DialogueEndReasons.Closed);
    }

    // ---- ending -------------------------------------------------------------------------------------------------------------------------

    private void End(long playerId, string reason)
    {
        if (!_sessions.Remove(playerId, out var session)) return;
        session.Player.OpenService = null;
        session.Player.OpenShopId = null;
        session.Player.OpenServiceNpc = null;
        _context!.Publish(new DialogueEnded(session.Player.Id, session.Npc, reason));
    }

    /// <summary>Every tick: a session ends when its player died, its NPC is gone or the player is farther than the talk range.</summary>
    private void CheckSessions()
    {
        if (_sessions.Count == 0) return;
        _scratch.Clear();
        _scratch.AddRange(_sessions.Values);
        foreach (var session in _scratch)
        {
            string? reason = null;
            if (session.Player.Dead) reason = DialogueEndReasons.Died;
            else if (!_instance!.TryGetEntity(session.Npc, out var npc)) reason = DialogueEndReasons.NpcGone;
            else if (!InRange(session.Player, npc)) reason = DialogueEndReasons.MovedAway;
            if (reason is not null) End(session.Player.Id.Value, reason);
        }
        _scratch.Clear();
    }

    private void OnLeft(EntityLeftMap e)
    {
        if (_instance!.TryGetEntity(e.Entity, out _)) return;   // a same-map warp leaves and comes back at once: the range check decides
        _walks.Remove(e.Entity.Value);
        if (e.Kind != EntityKind.Player) return;
        var gone = _instance.World.LocationOf(e.Entity) is null;
        if (_sessions.TryGetValue(e.Entity.Value, out var session)) End(e.Entity.Value, gone ? DialogueEndReasons.Disconnected : DialogueEndReasons.Warped);
        if (gone) _savePoints.Release(e.Entity);
    }

    // ---- walking to the NPC -------------------------------------------------------------------------------------------------------------

    private void OnMovementStarted(MovementStarted e)
    {
        if (!_walks.TryGetValue(e.Entity.Value, out var walk)) return;
        if (e.Goal.X == walk.Goal.X && e.Goal.Y == walk.Goal.Y) walk.Walking = true;
        else walk.EndReason ??= DialogueReasons.Cancelled;
    }

    private void OnMovementStopped(MovementStopped e)
    {
        if (!_walks.TryGetValue(e.Entity.Value, out var walk)) return;
        walk.Walking = false;
        if (e.Reason != MovementReasons.Arrived) walk.EndReason ??= DialogueReasons.Cancelled;
    }

    private void OnMoveRejected(MoveRejected e)
    {
        if (_walks.TryGetValue(e.Entity.Value, out var walk) && e.Target.X == walk.Goal.X && e.Target.Y == walk.Goal.Y) walk.EndReason ??= DialogueReasons.Unreachable;
    }

    private bool InRange(Entity player, Entity npc) => GridDistance.Chebyshev(player.Cell, npc.Cell) <= _content.Balance.TalkRange;

    private void Refuse(Entity player, string intent, string reason, EntityId? target = null) => Refuse(player, intent, reason, target?.Value);

    private void Refuse(Entity player, string intent, string reason, long? target) =>
        _context!.Publish(new DialogueRefused(player.Id, intent, reason, target));

    private sealed class Session(Entity player, EntityId npc, NpcDef npcDef, DialogueDef dialogue)
    {
        public readonly Entity Player = player;
        public readonly EntityId Npc = npc;
        public readonly NpcDef NpcDef = npcDef;
        public readonly DialogueDef Dialogue = dialogue;
        public string? NodeKey;       // null once the graph has finished but a service is still open
        public string? Service;
        public long Token;            // the item transaction being waited for, or 0
        public int ChoiceIndex;
        public int[]? Shown;          // the choice indices last sent for the current node (T-602: shown again when they change)
    }

    private sealed class Walk(Entity player, EntityId npc, GridCell goal, long issuedTick)
    {
        public readonly Entity Player = player;
        public readonly EntityId Npc = npc;
        public readonly GridCell Goal = goal;
        public readonly long IssuedTick = issuedTick;
        public bool Walking;
        public string? EndReason;
    }

    /// <summary>
    /// The talking player as the conditions see it: progression, quests and bag from the shared stores, statuses and the combat window from the entity (on the instance's
    /// simulated clock, the clock combat sets <see cref="Entity.InCombatUntilMs"/> with).
    /// </summary>
    private sealed class PlayerView(Game.Shared.Quests.QuestRules quests) : IDialoguePlayer
    {
        private Entity? _entity;
        private Game.Shared.Progression.ProgressionState? _progression;
        private Game.Shared.Items.InventoryState? _bag;
        private long _nowMs;

        public void Set(Entity player, IPlayerProgressionStore? progressions, IInventoryStore? inventories, long nowMs)
        {
            _entity = player;
            _progression = progressions is not null && progressions.TryGet(player.Id, out var p) ? p : null;
            _bag = inventories is not null && inventories.TryGet(player.Id, out var b) ? b : null;
            _nowMs = nowMs;
        }

        public Game.Shared.Quests.QuestStanding Quest(string questId) =>
            _progression is null ? new Game.Shared.Quests.QuestStanding(QuestStates.NotStarted, 0, false) : quests.Standing(_progression, questId);

        public bool InCombat => _entity is not null && _entity.InCombatUntilMs > _nowMs;

        public int BaseLevel => _progression?.BaseLevel ?? 1;

        public int JobLevel => _progression?.JobLevel ?? 1;

        public string JobId => _progression?.JobId ?? string.Empty;

        public int ItemCount(string itemId)
        {
            if (_bag is null) return 0;
            var total = 0;
            foreach (var entry in _bag.Entries)
                if (entry.Slot is null && string.Equals(entry.ItemId, itemId, StringComparison.Ordinal)) total += entry.Quantity;
            return total;
        }

        public bool HasStatus(string statusId) => _entity?.Statuses?.Find(statusId) is not null;
    }
}
