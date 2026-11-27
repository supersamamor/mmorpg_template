using Game.Client.Core.Accounts;
using Game.Client.Core.Autopilot;
using Game.Client.Core.Bundle;
using Game.Client.Core.Combat;
using Game.Client.Core.Dialogue;
using Game.Client.Core.Items;
using Game.Client.Core.Net;
using Game.Client.Core.Outfits;
using Game.Client.Core.Quests;
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

    /// <summary>Creates a session on the login screen. Without <paramref name="accounts"/> only the dev login works.</summary>
    public ClientSession(ClientConfig config, DisplayBundle bundle, IClientSocket socket, IRandom random, IAccountTransport? accounts = null)
    {
        Accounts = new AccountFlow(config, accounts, new CharacterCreator(bundle.Appearance, random));
        _config = config;
        _bundle = bundle;
        _socket = socket;
        Connection = new ConnectionMachine(config, random);
        World = new WorldView(config);
        Ui = new UiState(config, bundle);
        Skills = new SkillBook(bundle);
        Stats = new StatBook();
        Wardrobe = new WardrobeBook(bundle);
        Hotbar = new HotbarModel(config.HotbarSlots);
        Hotbar.Bind(Skills, new SkillActivator(this));
        Numbers = new DamageNumberPool(config.MaxDamageNumbers, config.DamageNumberSeconds, config.DamageNumberLaneSeconds);
        World.Combat.Impact += Numbers.OnImpact;
    }

    /// <summary>Signing in and picking a character (T-701).</summary>
    public AccountFlow Accounts { get; }

    /// <summary>The own inventory and equipment, as the server last said, and the panel's selection.</summary>
    public InventoryView Inventory { get; } = new InventoryView();

    /// <summary>Raised for each inventory change applied (after <see cref="Inventory"/> took it): the audio hooks listen here.</summary>
    public event Action<InventoryChangedMessage>? InventoryChanged;

    /// <summary>The dialogue the server runs with the player and the NPC service it opened (T-601). The server decides every step; this only holds what it sent.</summary>
    public DialogueView Dialogue { get; } = new DialogueView();

    /// <summary>Raised for each <c>service_opened</c> (after <see cref="Dialogue"/> took it): the HUD opens the service's panel.</summary>
    public event Action<ServiceOpenedMessage>? ServiceOpened;

    /// <summary>The shop the server opened (T-603): its stock and prices, what it pays, the tab, pick and quantity the panel shows. The server prices and decides every trade.</summary>
    public Game.Client.Core.Shop.ShopBook Shop { get; } = new Game.Client.Core.Shop.ShopBook();

    /// <summary>The quest log, the tracked quest and the NPC markers (T-602). The server decides every count and step; this only holds what it sent.</summary>
    public QuestBook Quests { get; } = new QuestBook();

    /// <summary>The autopilot (T-605): the ticked quests and their order, and the server's last word on the run. The server decides every step of a run; this only holds what it said.</summary>
    public AutopilotModel Autopilot { get; } = new AutopilotModel();

    /// <summary>Raised for each <c>quest</c> change (after <see cref="Quests"/> took it).</summary>
    public event Action<QuestMessage>? QuestChanged;

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

    /// <summary>The player's own outfits and what shows of them (T-708).</summary>
    public WardrobeBook Wardrobe { get; }

    /// <summary>The chat of this session (T-901): in memory only, emptied at every new login and back on the login screen.</summary>
    public Game.Client.Core.Chat.ChatBook Chat { get; } = new Game.Client.Core.Chat.ChatBook();

    /// <summary>The party of this session (T-902): what the server last said, in memory only, emptied at every new login and back on the login screen.</summary>
    public Game.Client.Core.Party.PartyBook Party { get; } = new Game.Client.Core.Party.PartyBook();

    /// <summary>The trade (T-903): the ask, both offers as the server holds them, how the last one ended.</summary>
    public Game.Client.Core.Trade.TradeBook Trade { get; } = new Game.Client.Core.Trade.TradeBook();

    private readonly List<string> _gainedOutfits = new();

    /// <summary>Server messages applied so far (for measuring).</summary>
    public long MessagesApplied { get; private set; }

    /// <summary>Starts a login with the dev name (development servers only).</summary>
    public void Login(string name, double wallNow) => Start(name, null, wallNow);

    /// <summary>Enters the world with one of the account's characters (T-701); does nothing unless signed in.</summary>
    public void Play(string characterId, double wallNow)
    {
        if (Accounts.SignedIn) Start(string.Empty, characterId, wallNow);
    }

    /// <summary>Leaves a stop for the login screen.</summary>
    public void BackToLogin() => Connection.BackToLogin();

    private string? _characterId;   // the character of an account's login, or null for a dev login

    private void Start(string name, string? characterId, double wallNow)
    {
        if (Connection.Phase != ConnectionPhase.LoginScreen && Connection.Phase != ConnectionPhase.Stopped) return;
        _characterId = characterId;
        World.Reset();
        Ui.Reset();
        Inventory.Reset();
        Dialogue.Reset();
        Shop.Reset();
        Quests.Reset();
        Autopilot.Reset();
        Skills.Reset();
        Stats.Reset();
        Wardrobe.Reset();
        Chat.Clear();
        Party.Clear();
        Trade.Clear();
        Numbers.Clear();
        Connection.Login(name, wallNow);
        RunCommands();
    }

    private int _noticeVersion;

    /// <summary>Back on the login screen after playing an account's character: the list may have changed, or the server may have refused the session.</summary>
    private void WatchLogin()
    {
        if (Connection.Version == _noticeVersion) return;
        _noticeVersion = Connection.Version;
        if (Connection.Phase is ConnectionPhase.LoginScreen or ConnectionPhase.Stopped)
        {
            Autopilot.Reset();   // a run belongs to the session that ended
            Chat.Clear();        // T-901: no chat outlives its session on the device
            Party.Clear();       // T-902: nor a party
            Trade.Clear();       // T-903: nor a trade
        }
        if (_characterId is not null && Connection.Phase == ConnectionPhase.LoginScreen) Accounts.Refresh();
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

    /// <summary>
    /// The quest log's Start button (T-605): <c>start_autopilot</c> with the ticked quests in run order and the skill bar's skills in slot order. Does nothing with nothing ticked,
    /// while dead or offline; the server still decides and a refusal comes back as a flash line. Returns true when it was sent. Replaces a run in progress.
    /// </summary>
    public bool StartAutopilot()
    {
        if (Connection.Phase != ConnectionPhase.Online || Ui.Dead) return false;
        Autopilot.Prune(Quests);
        if (Autopilot.BuildStart(Skills, Hotbar.SlotCount) is not { } start) return false;
        Send(start);
        return true;
    }

    /// <summary>The HUD indicator's tap and the log's Stop button (T-605): <c>stop_autopilot</c>. Does nothing while no run is on (the server would ignore it).</summary>
    public bool StopAutopilot()
    {
        if (Connection.Phase != ConnectionPhase.Online || !Autopilot.On) return false;
        Send(new StopAutopilotMessage());
        return true;
    }

    /// <summary>The shop panel's Buy button (T-603): <paramref name="quantity"/> units of the stock item <paramref name="item"/>. Sends the item and the count, never a price; the server answers with the new bag and balance, or a refusal.</summary>
    public void ShopBuy(string item, int quantity) => SendItem(new ShopBuyMessage(item, quantity));

    /// <summary>The shop panel's Sell button (T-603): <paramref name="quantity"/> units of the carried entry <paramref name="entry"/> at the shop's price.</summary>
    public void ShopSell(long entry, int quantity) => SendItem(new ShopSellMessage(entry, quantity));

    /// <summary>The quest log's Abandon confirmation (T-602): the server drops the quest and its progress, or refuses.</summary>
    public void AbandonQuest(string questId) => SendItem(new AbandonQuestMessage(questId));

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

    /// <summary>The wardrobe panel's choice (T-708): show an outfit in a slot, or nothing (null). Looks only, so also while dead. The server decides and answers with the new wardrobe.</summary>
    public void SetOutfit(string slot, string? outfit)
    {
        if (Connection.Phase == ConnectionPhase.Online) Send(new SetOutfitMessage(slot, outfit));
    }

    /// <summary>The wardrobe panel's switch (T-708): hide every outfit, or show the choices again.</summary>
    public void HideOutfits(bool hidden)
    {
        if (Connection.Phase == ConnectionPhase.Online) Send(new SetOutfitMessage(null, null, hidden));
    }

    /// <summary>Asks to change job (T-502).</summary>
    public void ChangeJob(string jobId) => SendItem(new ChangeJobMessage(jobId));

    /// <summary>The panel's Use button.</summary>
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
        WatchLogin();
        Party.Expire(wallNow);   // T-902: an invite past its end is no longer offered
        Trade.Expire(wallNow);   // T-903: nor an ask
        Accounts.Update();
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
                Chat.OwnId = welcome.Id;
                Party.OwnId = welcome.Id;
                Trade.Clear();   // T-903: a new session never finds a trade under way (the server ended it)
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
            case WardrobeMessage wardrobe:
                Wardrobe.Apply(wardrobe, _gainedOutfits);
                foreach (var outfit in _gainedOutfits) Ui.OnOutfitGained(outfit, viewNow);
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
            case QuestLogMessage log:
                Quests.Apply(log);
                Autopilot.Prune(Quests);
                break;
            case QuestMessage quest:
                Quests.Apply(quest);
                Autopilot.Prune(Quests);
                Ui.OnQuest(quest, viewNow);
                QuestChanged?.Invoke(quest);
                break;
            case DialogueEndedMessage ended:
                Dialogue.Ended(ended.Reason);
                if (Dialogue.Service != Game.Shared.Vocabulary.ServiceIds.Shop) Shop.Close();   // a finished graph leaves its service open; anything else ends it
                break;
            case ShopMessage shop:
                Shop.Apply(shop);
                break;
            case ServiceOpenedMessage service:
                Dialogue.ServiceOpened(service.Service);
                ServiceOpened?.Invoke(service);
                break;
            case AutopilotMessage autopilot:
                if (Autopilot.Apply(autopilot)) Ui.OnAutopilotEnded(autopilot.Reason, viewNow);
                break;
            case RejectedMessage rejected:
                if (rejected.Intent == MessageTypes.Refine && rejected.Reason == Game.Shared.Vocabulary.ItemReasons.ServiceRequired) Dialogue.ServiceClosed();
                if (rejected.Intent is MessageTypes.ShopBuy or MessageTypes.ShopSell
                    && rejected.Reason is Game.Shared.Vocabulary.ItemReasons.ServiceRequired or Game.Shared.Vocabulary.ItemReasons.OutOfRange)
                {
                    Dialogue.ServiceClosed();   // the server says no shop is open or in reach: the panel closes
                    Shop.Close();
                }
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
            case AnnouncementMessage announcement:
                Ui.OnAnnouncement(announcement, viewNow);   // T-806
                Chat.Announcement(Ui.Announcement);   // T-901: the log keeps it under "Server" after the banner is gone
                AnnouncementReceived?.Invoke(announcement);
                break;
            case ChatLineMessage chatLine:
                Chat.Apply(chatLine);
                ChatLineReceived?.Invoke(chatLine);
                break;
            case ChatStateMessage chatState:
                Chat.Apply(chatState);
                break;
            case ChatRefusedMessage chatRefused:
                Chat.Apply(chatRefused);
                ChatRefusedReceived?.Invoke(chatRefused);
                break;
            case PartyMessage party:
                Party.Apply(party);
                if (!Party.InParty && Chat.Channel == Game.Shared.Vocabulary.ChatChannels.Party) Chat.SetChannel(Game.Shared.Vocabulary.ChatChannels.Map);
                PartyReceived?.Invoke(party);
                break;
            case PartyMembersMessage partyMembers:
                Party.Apply(partyMembers);
                PartyMembersReceived?.Invoke(partyMembers);
                break;
            case PartyInvitedMessage invited:
                Party.Apply(invited, wallNow);
                PartyInvitedReceived?.Invoke(invited);
                break;
            case PartyNoticeMessage partyNotice:
                Party.Apply(partyNotice);
                Ui.OnParty(UiText.PartyNotice(partyNotice.Notice, partyNotice.Name), viewNow);
                PartyNoticeReceived?.Invoke(partyNotice);
                break;
            case PartyRefusedMessage partyRefused:
                Party.Apply(partyRefused);
                Ui.OnParty(UiText.PartyRefused(partyRefused.Reason, partyRefused.RetryAfterSeconds, partyRefused.Name), viewNow);
                PartyRefusedReceived?.Invoke(partyRefused);
                break;
            case TradeAskedMessage tradeAsked:
                Trade.Apply(tradeAsked, wallNow);
                TradeReceived?.Invoke(tradeAsked);
                break;
            case TradeMessage trade:
                Trade.Apply(trade);
                TradeReceived?.Invoke(trade);
                break;
            case TradeNoticeMessage tradeNotice:
                Trade.Apply(tradeNotice);
                Ui.OnParty(Trade.Notice!, viewNow);
                TradeReceived?.Invoke(tradeNotice);
                break;
            case TradeClosedMessage tradeClosed:
                Trade.Apply(tradeClosed);
                Ui.OnParty(Trade.Notice!, viewNow);
                TradeReceived?.Invoke(tradeClosed);
                break;
            case TradeRefusedMessage tradeRefused:
                Trade.Apply(tradeRefused);
                Ui.OnParty(Trade.Notice!, viewNow);
                TradeReceived?.Invoke(tradeRefused);
                break;
            case ChatReportedMessage chatReported:
                Chat.Apply(chatReported);
                ChatReportedReceived?.Invoke(chatReported);
                break;
            case ByeMessage bye:
                Connection.OnBye(bye.Reason, wallNow, bye.RetryAfterSeconds);
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
                    // An account's login names the session and the character. A session that has ended meanwhile is refused by the server like any other.
                    Send(_characterId is null
                        ? new HelloMessage(ProtocolVersion.Current, Connection.LoginName, _bundle.ContentHash)
                        : new HelloMessage(ProtocolVersion.Current, null, _bundle.ContentHash, null, Accounts.SessionToken ?? string.Empty, _characterId));
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

    // ---- chat (T-901): intents only; a line shows when the server sends it back ----

    /// <summary>Raised for every chat line the server sends (for the scripted run).</summary>
    public event Action<ChatLineMessage>? ChatLineReceived;

    /// <summary>Raised for every chat refusal (for the scripted run).</summary>
    public event Action<ChatRefusedMessage>? ChatRefusedReceived;

    /// <summary>Raised when a report is stored (for the scripted run).</summary>
    public event Action<ChatReportedMessage>? ChatReportedReceived;

    /// <summary>Raised for every announcement (for the scripted run).</summary>
    public event Action<AnnouncementMessage>? AnnouncementReceived;

    /// <summary>
    /// Sends what the player typed on the chosen channel, or puts the reason it cannot go into the log (nothing typed, too long, no whisper target). True when it went to the server. Nothing
    /// is shown until the server sends the line back.
    /// </summary>
    public bool Say(string typed)
    {
        if (Connection.Phase != ConnectionPhase.Online) return false;
        if (Chat.Outgoing(typed, out var why) is not { } text)
        {
            if (why is not null) Chat.Note(why);
            return false;
        }
        Send(new ChatMessage(Chat.Channel, Chat.Channel == Game.Shared.Vocabulary.ChatChannels.Whisper ? Chat.WhisperTo : null, text));
        return true;
    }

    /// <summary>Blocks a character by name.</summary>
    public void Block(string name)
    {
        if (Connection.Phase == ConnectionPhase.Online && name.Length > 0) Send(new BlockMessage(name));
    }

    /// <summary>Takes a block back.</summary>
    public void Unblock(string name)
    {
        if (Connection.Phase == ConnectionPhase.Online && name.Length > 0) Send(new UnblockMessage(name));
    }

    /// <summary>Reports a line this client received, by its number: the server keeps what was said.</summary>
    public void Report(long line)
    {
        if (Connection.Phase == ConnectionPhase.Online && line > 0) Send(new ReportMessage(line));
    }

    // ---- party (T-902): intents only; the server decides and says ----

    /// <summary>Raised for every <c>party</c> message (for the scripted run).</summary>
    public event Action<PartyMessage>? PartyReceived;

    /// <summary>Raised for every <c>party_members</c> message (for the scripted run).</summary>
    public event Action<PartyMembersMessage>? PartyMembersReceived;

    /// <summary>Raised for every invite (for the scripted run).</summary>
    public event Action<PartyInvitedMessage>? PartyInvitedReceived;

    /// <summary>Raised for every party notice (for the scripted run).</summary>
    public event Action<PartyNoticeMessage>? PartyNoticeReceived;

    /// <summary>Raised for every party refusal (for the scripted run).</summary>
    public event Action<PartyRefusedMessage>? PartyRefusedReceived;

    /// <summary>Invites a character by name.</summary>
    public void InviteToParty(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (Connection.Phase == ConnectionPhase.Online && trimmed.Length > 0) Send(new PartyInviteMessage(trimmed, null));
    }

    /// <summary>Invites the player tapped in the world, by its entity id (the server checks it is on this map).</summary>
    public void InviteToParty(long entity)
    {
        if (Connection.Phase == ConnectionPhase.Online && entity > 0) Send(new PartyInviteMessage(null, entity));
    }

    /// <summary>Answers the invite this character has.</summary>
    public void AnswerParty(bool accept)
    {
        if (Connection.Phase != ConnectionPhase.Online || Party.Invite is null) return;
        Send(new PartyAnswerMessage(accept));
        Party.Answered();
    }

    /// <summary>Leaves the party.</summary>
    public void LeaveParty()
    {
        if (Connection.Phase == ConnectionPhase.Online && Party.InParty) Send(new PartyLeaveMessage());
    }

    /// <summary>The leader takes a member out.</summary>
    public void KickFromParty(long member)
    {
        if (Connection.Phase == ConnectionPhase.Online && Party.Leading) Send(new PartyKickMessage(member));
    }

    /// <summary>The leader hands the leader to a member.</summary>
    public void HandPartyLead(long member)
    {
        if (Connection.Phase == ConnectionPhase.Online && Party.Leading) Send(new PartyLeadMessage(member));
    }

    /// <summary>Raised for every trade message (for the scripted run).</summary>
    public event Action<ServerMessage>? TradeReceived;

    /// <summary>Asks a player in view (tapped, or a party member) to trade, by its entity id; the server decides the rest.</summary>
    public void AskTrade(long entity)
    {
        if (Connection.Phase == ConnectionPhase.Online && entity > 0) Send(new TradeAskMessage(entity));
    }

    /// <summary>Answers the ask this character has.</summary>
    public void AnswerTrade(bool accept)
    {
        if (Connection.Phase != ConnectionPhase.Online || Trade.Ask is null) return;
        Send(new TradeAnswerMessage(accept));
        Trade.Answered();
    }

    /// <summary>Puts <paramref name="quantity"/> units of a carried entry in this side's offer (0 takes it out).</summary>
    public void OfferTrade(long entry, int quantity)
    {
        if (Connection.Phase == ConnectionPhase.Online && Trade.Open is not null) Send(new TradeOfferMessage(entry, Math.Max(0, quantity)));
    }

    /// <summary>The currency in this side's offer.</summary>
    public void OfferTradeCurrency(long amount)
    {
        if (Connection.Phase == ConnectionPhase.Online && Trade.Open is not null) Send(new TradeCurrencyMessage(Math.Max(0, amount)));
    }

    /// <summary>Locks this side's offer, or unlocks both.</summary>
    public void LockTrade(bool locked)
    {
        if (Connection.Phase == ConnectionPhase.Online && Trade.Open is not null) Send(locked ? new TradeLockMessage() : new TradeUnlockMessage());
    }

    /// <summary>Confirms the two offers drawn now (their version): offers that changed since are refused by the server.</summary>
    public void ConfirmTrade()
    {
        if (Connection.Phase == ConnectionPhase.Online && Trade.Open is { } trade && Trade.CanConfirm) Send(new TradeConfirmMessage(trade.Version));
    }

    /// <summary>Ends the ask this character made, or the trade.</summary>
    public void CancelTrade()
    {
        if (Connection.Phase == ConnectionPhase.Online) Send(new TradeCancelMessage());
    }

    /// <summary>A player was tapped in the world: the party panel offers to invite it (nothing goes to the server yet).</summary>
    public void TapPlayer(long entity) => Party.Tap(entity);

    /// <summary>Binds the skill bar's slots to <see cref="PressSkill"/>.</summary>
    private sealed class SkillActivator(ClientSession session) : IHotbarActivator
    {
        public void Activate(int index)
        {
            if (session.Skills.SkillAt(index) is { } skill) session.PressSkill(skill, session._lastViewNow);
        }
    }
}
