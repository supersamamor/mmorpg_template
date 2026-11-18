using Game.Client.Core.Bundle;
using Game.Client.Core.Combat;
using Game.Client.Core.Dialogue;
using Game.Client.Core.Items;
using Game.Client.Core.Net;
using Game.Client.Core.Skills;
using Game.Client.Core.Stats;
using Game.Client.Core.Ui;
using Game.Client.Core.World;
using Game.Server.Net;
using Game.Shared.Protocol;
using Game.Shared.Random;
using Game.Shared.World;

namespace Game.Client.Core;

/// <summary>
/// The whole client without an engine: socket events go in, the machine, view and UI state change, intents go out. It sends only intents
/// (CLAUDE.md rule 1). It runs on the main thread, and time comes in as parameters: <c>wallNow</c> keeps running while the app is suspended and
/// drives the connection; <c>viewNow</c> is smooth and drives the view.
/// </summary>
public sealed class ClientSession
{
    private readonly ClientConfig _config;
    private readonly DisplayBundle _bundle;
    private readonly IClientSocket _socket;

    /// <summary>Creates a session on the login screen.</summary>
    public ClientSession(ClientConfig config, DisplayBundle bundle, IClientSocket socket, IRandom random)
    {
        _config = config;
        _bundle = bundle;
        _socket = socket;
        Connection = new ConnectionMachine(config, random);
        World = new WorldView(config);
        Ui = new UiState(config, bundle);
        Skills = new SkillBook(bundle);
        Stats = new StatBook();
        Hotbar = new HotbarModel(config.HotbarSlots);
        Hotbar.Bind(Skills, new SkillActivator(this));
        Numbers = new DamageNumberPool(config.MaxDamageNumbers, config.DamageNumberSeconds, config.DamageNumberLaneSeconds);
        World.Combat.Impact += Numbers.OnImpact;
    }

    /// <summary>The own inventory and equipment, as the server last said, and the panel's selection.</summary>
    public InventoryView Inventory { get; } = new InventoryView();

    /// <summary>Raised for each inventory change applied (after <see cref="Inventory"/> took it): the audio hooks listen here.</summary>
    public event Action<InventoryChangedMessage>? InventoryChanged;

    /// <summary>The dialogue the server runs with the player and the NPC service it opened (T-601). The server decides every step; this only holds what it sent.</summary>
    public DialogueView Dialogue { get; } = new DialogueView();

    /// <summary>Raised for each <c>service_opened</c> (after <see cref="Dialogue"/> took it): the HUD opens the service's panel.</summary>
    public event Action<ServiceOpenedMessage>? ServiceOpened;

    /// <summary>The floating damage numbers, started at each hit's impact moment.</summary>
    public DamageNumberPool Numbers { get; }

    /// <summary>The connection.</summary>
    public ConnectionMachine Connection { get; }

    /// <summary>The world as last told by the server.</summary>
    public WorldView World { get; }

    /// <summary>What the player's actions put on screen.</summary>
    public UiState Ui { get; }

    /// <summary>The hotbar's slots. Empty until T-502 binds skills; the client sends no skill intent yet.</summary>
    public HotbarModel Hotbar { get; }

    /// <summary>The player's own skills, cooldowns and skill tree rows (T-502).</summary>
    public SkillBook Skills { get; }

    /// <summary>The player's own stat points, stats and derived stats (T-503).</summary>
    public StatBook Stats { get; }

    /// <summary>Server messages applied so far (for measuring).</summary>
    public long MessagesApplied { get; private set; }

    /// <summary>Starts a login with the dev name.</summary>
    public void Login(string name, double wallNow)
    {
        World.Reset();
        Ui.Reset();
        Inventory.Reset();
        Dialogue.Reset();
        Skills.Reset();
        Stats.Reset();
        Numbers.Clear();
        Connection.Login(name, wallNow);
        RunCommands();
    }

    /// <summary>Asks to leave (§10 step 6).</summary>
    public void Logout(double wallNow)
    {
        Connection.Logout(wallNow);
        RunCommands();
    }

    /// <summary>The app went to the background.</summary>
    public void OnBackgrounded() => Connection.OnBackgrounded();

    /// <summary>The app came back.</summary>
    public void OnForegrounded(double wallNow)
    {
        Connection.OnForegrounded(wallNow);
        RunCommands();
    }

    /// <summary>A ground tap: ask to walk. The server may refuse; the client checks nothing.</summary>
    public void TapGround(GridCell cell, double viewNow)
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead) return;
        if (Ui.PendingSkill is { } pending)
        {
            if (_bundle.Skill(pending) is { Ground: true })
            {
                CastSkill(pending, null, cell);
                return;
            }
            Ui.ClearPending();   // a ground tap is not the target the skill waits for: it is a walk
        }
        Send(new MoveMessage(cell));
        Ui.OnMoveSent(cell, viewNow);
    }

    /// <summary>A monster tap: ask to attack. The server may refuse; the client checks nothing.</summary>
    public void TapEntity(long id)
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead) return;
        if (Ui.PendingSkill is { } pending && _bundle.Skill(pending) is { } waiting)
        {
            if (waiting.Ground && World.Find(id) is { } at) CastSkill(pending, null, at.Cell);
            else CastSkill(pending, id, null);
            return;
        }
        Send(new AttackMessage(id));
        Ui.OnAttackSent(id);
    }

    /// <summary>A ground item tap: ask to walk there and pick it up. The server checks range, loot priority, weight and room.</summary>
    public void TapItem(long id)
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead) return;
        Send(new PickUpMessage(id));
        Ui.OnPickUpSent();
    }

    /// <summary>An NPC tap (T-601): ask to walk up and talk. The server checks range, view and that it has something to say; the client checks nothing.</summary>
    public void TapNpc(long id)
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead) return;
        Send(new TalkMessage(id));
    }

    /// <summary>A dialogue choice button: <paramref name="index"/> is the index the server showed (0 at a node without choices). The server decides where it leads.</summary>
    public void ChooseDialogue(int index) => SendItem(new DialogueChoiceMessage(index));

    /// <summary>The dialogue panel's Close button: end the dialogue and any service it opened. The panel closes when the server says it ended.</summary>
    public void CloseDialogue() => SendItem(new DialogueCloseMessage());

    /// <summary>The panel's Use button.</summary>
    /// <summary>
    /// A skill bar press (T-502). A skill on the caster goes out at once; one that needs an enemy goes out on the current target if there is a living one,
    /// else waits for a tap on one; a ground skill waits for a tap on the ground. A second press of a waiting skill cancels it. The server checks everything.
    /// </summary>
    public void PressSkill(string skillId, double viewNow)
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead || _bundle.Skill(skillId) is not { Passive: false } skill) return;
        if (Ui.PendingSkill == skillId)
        {
            Ui.ClearPending();
            return;
        }
        if (skill.OnSelf || (skill.Friendly && !skill.Ground))
        {
            CastSkill(skillId, null, null);
            return;
        }
        if (!skill.Ground && Ui.Target is { } target && World.Find(target) is { Dead: false })
        {
            CastSkill(skillId, target, null);
            return;
        }
        Ui.SetPending(skillId, skill.Ground ? UiText.TapGround(skill.Name) : UiText.TapTarget(skill.Name), viewNow);
    }

    private void CastSkill(string skillId, long? target, GridCell? cell)
    {
        Send(new UseSkillMessage(skillId, null, target, cell));
        Ui.OnSkillSent(target);
    }

    /// <summary>Spends a skill point on a skill (T-502); the server answers with the new list or a refusal.</summary>
    public void LearnSkill(string skillId) => SendItem(new LearnSkillMessage(skillId));

    /// <summary>Spends stat points on one primary stat (T-503); the server prices the step and answers with the new stats or a refusal.</summary>
    public void RaiseStat(string statId, int amount = 1) => SendItem(new RaiseStatMessage(statId, amount));

    /// <summary>Asks to change job (T-502).</summary>
    public void ChangeJob(string jobId) => SendItem(new ChangeJobMessage(jobId));

    public void UseItem(long entry) => SendItem(new UseItemMessage(entry));

    /// <summary>The panel's Equip button (the server picks the slot).</summary>
    public void EquipItem(long entry) => SendItem(new EquipMessage(entry));

    /// <summary>The panel's Unequip button, or a tap on a worn slot.</summary>
    public void UnequipSlot(string slot) => SendItem(new UnequipMessage(slot));

    /// <summary>The panel's Drop button.</summary>
    public void DropItem(long entry, int quantity) => SendItem(new DropItemMessage(entry, quantity));

    /// <summary>The panel's socket choice: put the card in entry <paramref name="card"/> into equipment entry <paramref name="equipment"/>.</summary>
    public void InsertCard(long equipment, long card) => SendItem(new InsertCardMessage(equipment, card));

    /// <summary>The refine confirmation: try to refine equipment entry <paramref name="entry"/> by one level (the server rolls).</summary>
    public void Refine(long entry) => SendItem(new RefineMessage(entry));

    /// <summary>Raised for each <c>refine_result</c> (after the flash line took it).</summary>
    public event Action<RefineResultMessage>? RefineResultReceived;

    private void SendItem(ClientMessage message)
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead) return;
        Send(message);
    }

    /// <summary>The death screen's button.</summary>
    public void Respawn()
    {
        if (Connection.Phase == ConnectionPhase.Online) Send(new RespawnMessage());
    }

    /// <summary>Raised when a <c>level_up</c> arrived.</summary>
    public event Action<LevelUpMessage>? LevelUpReceived;

    /// <summary>One frame: applies what arrived, runs timers, moves the view. Allocates nothing when nothing arrived and no timer fired.</summary>
    public void Update(double wallNow, double viewNow)
    {
        while (_socket.TryDequeue(out var socketEvent))
        {
            switch (socketEvent.Kind)
            {
                case SocketEventKind.Opened:
                    Connection.OnSocketOpened();
                    break;
                case SocketEventKind.Message:
                    Connection.OnHeard(wallNow);
                    Apply(socketEvent.Message!, wallNow, viewNow);
                    break;
                case SocketEventKind.UnknownType:
                    Connection.OnHeard(wallNow);
                    break;
                case SocketEventKind.Malformed:
                    _socket.Close();
                    Connection.OnSocketClosed(wallNow);
                    break;
                case SocketEventKind.Closed:
                    Connection.OnSocketClosed(wallNow);
                    break;
            }
            RunCommands();
        }
        Connection.Tick(wallNow);
        RunCommands();
        _lastViewNow = viewNow;
        World.Advance(viewNow);
        Skills.Update(viewNow);
        Ui.Tick(World, viewNow);
    }

    private double _lastViewNow;

    private void Apply(ServerMessage message, double wallNow, double viewNow)
    {
        _lastViewNow = viewNow;
        MessagesApplied++;
        switch (message)
        {
            case DeltaMessage delta:
                if (delta.Hits is { Count: > 0 } hits)
                {
                    Ui.OnHits(hits, World, viewNow); // before the deltas: a target that died here is still named
                    World.ApplyHits(hits, viewNow, delta.Tick);
                }
                World.ApplyDelta(delta, viewNow);
                if (delta.Appeared.Count > 0) Ui.OnAppeared(delta.Appeared, viewNow);
                if (delta.Vitals is { } vitals) Ui.OnVitals(vitals);
                break;
            case SnapshotMessage snapshot:
                World.ApplySnapshot(snapshot, viewNow);
                if (snapshot.Vitals is { } own) Ui.OnVitals(own);
                break;
            case WelcomeMessage welcome:
                Connection.OnWelcome(welcome, wallNow);
                World.ApplyWelcome(welcome);
                break;
            case ExpMessage exp:
                World.ApplyExp(exp);
                break;
            case SkillsMessage skills:
                Skills.Apply(skills, viewNow);
                break;
            case StatsMessage stats:
                Stats.Apply(stats);
                break;
            case CooldownMessage cooldown:
                Skills.ApplyCooldown(cooldown, viewNow);
                break;
            case InventoryMessage inventory:
                Inventory.Apply(inventory);
                break;
            case InventoryChangedMessage change:
                if (Inventory.Apply(change))
                {
                    Ui.OnInventoryChanged(change, viewNow);
                    InventoryChanged?.Invoke(change);
                }
                break;
            case RefineResultMessage refined:
                Ui.OnRefineResult(refined, viewNow);
                RefineResultReceived?.Invoke(refined);
                break;
            case DialogueMessage dialogue:
                Dialogue.Apply(dialogue);
                break;
            case DialogueEndedMessage ended:
                Dialogue.Ended(ended.Reason);
                break;
            case ServiceOpenedMessage service:
                Dialogue.ServiceOpened(service.Service);
                ServiceOpened?.Invoke(service);
                break;
            case RejectedMessage rejected:
                if (rejected.Intent == MessageTypes.Refine && rejected.Reason == Game.Shared.Vocabulary.ItemReasons.ServiceRequired) Dialogue.ServiceClosed();
                Ui.OnRejected(rejected, viewNow);
                break;
            case AttackStoppedMessage stopped:
                Ui.OnAttackStopped(stopped);
                break;
            case YouDiedMessage:
                Ui.OnYouDied();
                break;
            case RespawnedMessage:
                Ui.OnRespawned();
                break;
            case LevelUpMessage level:
                Ui.OnLevelUp(level, viewNow);
                LevelUpReceived?.Invoke(level);
                break;
            case ByeMessage bye:
                Connection.OnBye(bye.Reason, wallNow);
                break;
        }
    }

    private void RunCommands()
    {
        while (Connection.TryNextCommand(out var command))
        {
            switch (command)
            {
                case ConnectionCommandKind.OpenSocket:
                    // Main refuses a bad URL at start-up with a screen; this stays as the second lock. Nothing is opened, so the attempt times out into "could not reach".
                    if (TransportPolicy.Check(_config.ServerUrl, _config.AllowCleartext) == TransportVerdict.Allowed) _socket.Open(_config.ServerUrl);
                    break;
                case ConnectionCommandKind.CloseSocket:
                    _socket.Close();
                    break;
                case ConnectionCommandKind.SendLogin:
                    Send(new HelloMessage(ProtocolVersion.Current, Connection.LoginName, _bundle.ContentHash));
                    break;
                case ConnectionCommandKind.SendResume:
                    Send(new HelloMessage(ProtocolVersion.Current, null, _bundle.ContentHash, Connection.ResumeToken));
                    break;
                case ConnectionCommandKind.SendPing:
                    Send(new PingMessage(MessagesApplied));
                    break;
                case ConnectionCommandKind.SendLogout:
                    Send(new LogoutMessage());
                    break;
            }
        }
    }

    private void Send(ClientMessage message) => _socket.Send(ProtocolJson.ToBytes(message));

    /// <summary>Binds the skill bar's slots to <see cref="PressSkill"/>.</summary>
    private sealed class SkillActivator(ClientSession session) : IHotbarActivator
    {
        public void Activate(int index)
        {
            if (session.Skills.SkillAt(index) is { } skill) session.PressSkill(skill, session._lastViewNow);
        }
    }
}
