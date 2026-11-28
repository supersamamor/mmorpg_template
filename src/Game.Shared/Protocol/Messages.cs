using System;
using System.Collections.Generic;
using Game.Shared.World;

namespace Game.Shared.Protocol
{
    // Message contracts shared by the server gateway and the Godot client (ARCHITECTURE §10 has the JSON shape of each). Plain data, no
    // I/O and no serializer attributes: the JSON mapping is written by hand (Game.Server/Net/ProtocolJson), so neither side needs
    // runtime reflection. Display data only (CLAUDE.md rule 11): no stats, AI, drops or paths beyond the next cell.

    /// <summary>A message a client sends. <see cref="Type"/> is its <see cref="MessageTypes"/> name.</summary>
    public abstract record ClientMessage
    {
        /// <summary>The <c>type</c> field.</summary>
        public abstract string Type { get; }
    }

    /// <summary>
    /// <c>hello</c>: the first message of a session. It carries exactly one of <see cref="Name"/> (a new login) and <see cref="Resume"/> (take
    /// back a character after a drop, ARCHITECTURE §10). <c>{"type":"hello","version":1,"name":"Ada","content":"ab12..."}</c> or
    /// <c>{"type":"hello","version":1,"resume":"Qk3x...","content":"ab12..."}</c>.
    /// </summary>
    /// <param name="Version">The client's <see cref="ProtocolVersion"/>.</param>
    /// <param name="Name">Display name for the dev login (no password, development only), or null for the other two forms.</param>
    /// <param name="Content">The content hash of the client's bundle (ARCHITECTURE §5.4), or null when the client has none. A hash that differs from the server's ends in <c>bye</c> <c>update_required</c>; a missing one is allowed only in development.</param>
    /// <param name="Resume">The resume token from the last <see cref="WelcomeMessage"/>, or null for a new login. Single use: a successful resume
    /// returns a new one; an unknown, used or expired token ends in <c>bye</c> <c>resume_failed</c>.</param>
    /// <param name="Session">The session token of the player's account (T-701), or null for the other two forms. Never log it.</param>
    /// <param name="Character">The account's character to play (its permanent id, a GUID), with <paramref name="Session"/>. A session that is unknown, expired or
    /// revoked, or a character that is not the account's, ends in <c>bye</c> <c>login_refused</c>.</param>
    public sealed record HelloMessage(int Version, string? Name, string? Content = null, string? Resume = null, string? Session = null, string? Character = null) : ClientMessage
    {
        /// <summary>No token in a log line or an exception.</summary>
        public override string ToString() => $"HelloMessage {{ {Version}, {(Resume is not null ? "resume" : Session is not null ? "session" : "name")} }}";

        /// <inheritdoc />
        public override string Type => MessageTypes.Hello;
    }

    /// <summary><c>move</c>: walk to <see cref="Cell"/>. A request: the server may still refuse it (<see cref="RejectedMessage"/>). <c>{"type":"move","cell":[12,9]}</c>.</summary>
    /// <param name="Cell">Target cell on the current map.</param>
    public sealed record MoveMessage(GridCell Cell) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Move;
    }

    /// <summary><c>stop</c>: stop walking at the next cell, and stop auto-attacking. <c>{"type":"stop"}</c>.</summary>
    public sealed record StopMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Stop;
    }

    /// <summary>
    /// <c>attack</c>: auto-attack <see cref="Target"/> until it dies or leaves, or the client stops. A request: the target must be an entity the
    /// client sees, and combat may still refuse it (<see cref="RejectedMessage"/> with an "Attack reasons" term). <c>{"type":"attack","target":7}</c>.
    /// </summary>
    /// <param name="Target">Entity id of the target (a monster in v1).</param>
    public sealed record AttackMessage(long Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Attack;
    }

    /// <summary>
    /// <c>use_skill</c> (T-502): cast <see cref="Skill"/> at <see cref="Level"/> (null: the learned level) on the entity <see cref="Target"/> or the ground
    /// <see cref="Cell"/>, as the skill's target says; a friendly entity skill without a target is cast on the caster. The server walks the caster into range,
    /// casts, and may refuse (<see cref="RejectedMessage"/> with a "Skill reasons" term). <c>{"type":"use_skill","skill":"skill.twin_strike","target":7}</c>.
    /// </summary>
    public sealed record UseSkillMessage(string Skill, int? Level = null, long? Target = null, GridCell? Cell = null) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.UseSkill;
    }

    /// <summary><c>learn_skill</c> (T-502): spend one skill point on <see cref="Skill"/>. <c>{"type":"learn_skill","skill":"skill.twin_strike"}</c>.</summary>
    public sealed record LearnSkillMessage(string Skill) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.LearnSkill;
    }

    /// <summary><c>change_job</c> (T-502): change to <see cref="Job"/> when its requirements are met. <c>{"type":"change_job","job":"job.spellbinder"}</c>.</summary>
    public sealed record ChangeJobMessage(string Job) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ChangeJob;
    }

    /// <summary>
    /// <c>raise_stat</c> (T-503): spend stat points to raise primary stat <see cref="Stat"/> by <see cref="Amount"/> steps of one. The server prices every step
    /// and may refuse (<see cref="RejectedMessage"/> with a "Stat reasons" term). <c>{"type":"raise_stat","stat":"str","amount":1}</c>.
    /// </summary>
    public sealed record RaiseStatMessage(string Stat, int Amount = 1) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.RaiseStat;
    }


    /// <summary>
    /// <c>talk</c> (T-601): walk up to the NPC <see cref="Target"/> (an entity the client sees) and start its dialogue. May come back
    /// <see cref="RejectedMessage"/> with a "Dialogue reasons" term. <c>{"type":"talk","target":12}</c>.
    /// </summary>
    public sealed record TalkMessage(long Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Talk;
    }

    /// <summary>
    /// <c>dialogue_choice</c> (T-601): pick the shown choice <see cref="Index"/> of the current node (the index the <c>dialogue</c> message gave), or, at a
    /// node without choices, continue with index 0. The server decides where it leads. <c>{"type":"dialogue_choice","index":1}</c>.
    /// </summary>
    public sealed record DialogueChoiceMessage(int Index) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.DialogueChoice;
    }

    /// <summary><c>dialogue_close</c> (T-601): end the dialogue and any NPC service it opened. <c>{"type":"dialogue_close"}</c>.</summary>
    public sealed record DialogueCloseMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.DialogueClose;
    }

    /// <summary>
    /// <c>shop_buy</c> (T-603): buy <see cref="Quantity"/> units of <see cref="Item"/> from the shop opened by an <c>open_service</c> action, if the NPC is still in talking range.
    /// The client names the item and the count only; the price is the server's. Comes back as <c>inventory_changed</c> (cause <c>shop_buy</c>, with the new currency) or
    /// <see cref="RejectedMessage"/> with an "Item reasons" term (<c>service_required</c>, <c>out_of_range</c>, <c>not_in_stock</c>, <c>invalid_quantity</c>, <c>quantity_limit</c>,
    /// <c>cannot_afford</c>, <c>inventory_full</c>, <c>too_heavy</c>, <c>dead</c>). <c>{"type":"shop_buy","item":"item.healing_draught","quantity":5}</c>.
    /// </summary>
    public sealed record ShopBuyMessage(string Item, int Quantity) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ShopBuy;
    }

    /// <summary>
    /// <c>shop_sell</c> (T-603): sell <see cref="Quantity"/> units of the carried inventory entry <see cref="Entry"/> to the open shop, at the item's sell price (server data). Refused with
    /// an "Item reasons" term: the shop ones above plus <c>item_gone</c>, <c>item_equipped</c>, <c>not_sellable</c>, <c>currency_full</c>. <c>{"type":"shop_sell","entry":12,"quantity":3}</c>.
    /// </summary>
    public sealed record ShopSellMessage(long Entry, int Quantity) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ShopSell;
    }

    /// <summary>
    /// <c>abandon_quest</c> (T-602): drop the active quest <see cref="Quest"/>: its progress is gone and nothing comes back. May come back
    /// <see cref="RejectedMessage"/> with a "Quest reasons" term (<c>not_active</c>). <c>{"type":"abandon_quest","quest":"quest.first_hunt"}</c>.
    /// </summary>
    public sealed record AbandonQuestMessage(string Quest) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.AbandonQuest;
    }

    /// <summary>
    /// <c>start_autopilot</c> (T-604): the server plays the active quests <see cref="Quests"/>, in this order, casting <see cref="Skills"/> (the shortcut slots, in slot order) as their
    /// rules allow. Replaces a run in progress. May come back <see cref="RejectedMessage"/> with an "Autopilot reasons" term and the <c>quest</c> or <c>skill</c> it is about.
    /// <c>{"type":"start_autopilot","quests":["quest.first_hunt"],"skills":[["skill.twin_strike",2]]}</c>.
    /// </summary>
    public sealed record StartAutopilotMessage(IReadOnlyList<string> Quests, IReadOnlyList<SkillLevelRecord> Skills) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.StartAutopilot;
    }

    /// <summary><c>stop_autopilot</c> (T-604): switch the autopilot off; the character stops where it is. Ignored when it is off. <c>{"type":"stop_autopilot"}</c>.</summary>
    public sealed record StopAutopilotMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.StopAutopilot;
    }

    /// <summary>
    /// <c>set_outfit</c> (T-708): either what shows in one slot (<c>slot</c>, and <c>outfit</c> or nothing) or the switch that hides every outfit (<c>hidden</c>). Exactly one of
    /// <paramref name="Slot"/> and <paramref name="Hidden"/> is given. A request: the server may refuse it (<c>rejected</c>, an "Outfit reasons" term).
    /// </summary>
    /// <param name="Slot">An "Outfit slots" term.</param>
    /// <param name="Outfit">The outfit to show there; null: nothing.</param>
    /// <param name="Hidden">Hide every outfit, or show the choices again.</param>
    public sealed record SetOutfitMessage(string? Slot, string? Outfit = null, bool? Hidden = null) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.SetOutfit;
    }

    /// <summary><c>stop_attack</c>: stop auto-attacking (a walk goes on). <c>{"type":"stop_attack"}</c>.</summary>
    public sealed record StopAttackMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.StopAttack;
    }

    /// <summary><c>respawn</c>: a dead character asks to come back at the respawn point (ignored while alive). <c>{"type":"respawn"}</c>.</summary>
    public sealed record RespawnMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Respawn;
    }

    /// <summary>
    /// <c>logout</c>: leave the game on purpose. The server answers <c>bye</c> <c>logout</c>, closes, and removes the character at once, with no
    /// grace period; its resume token stops working. <c>{"type":"logout"}</c>.
    /// </summary>
    public sealed record LogoutMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Logout;
    }

    /// <summary><c>ping</c>: keep-alive; the server answers with <see cref="PongMessage"/> carrying the same number. <c>{"type":"ping","seq":7}</c>.</summary>
    /// <param name="Seq">Any number the client picks (a counter or its clock).</param>
    public sealed record PingMessage(long Seq) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Ping;
    }

    /// <summary>
    /// <c>pick_up</c>: walk to the ground item <see cref="Target"/> (an entity the client sees) and put it in the inventory. A request: it may come back
    /// <see cref="RejectedMessage"/> with an "Item reasons" term. <c>{"type":"pick_up","target":9}</c>.
    /// </summary>
    /// <param name="Target">Entity id of the ground item.</param>
    public sealed record PickUpMessage(long Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PickUp;
    }

    /// <summary><c>use_item</c>: use the consumable in inventory entry <see cref="Entry"/>. <c>{"type":"use_item","entry":12}</c>.</summary>
    /// <param name="Entry">The inventory entry id (from <see cref="InventoryMessage"/>).</param>
    public sealed record UseItemMessage(long Entry) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.UseItem;
    }

    /// <summary>
    /// <c>equip</c>: put on the equipment in entry <see cref="Entry"/>, in <see cref="Slot"/> or, without one, the first of its slots that is free.
    /// Whatever it displaces comes off. <c>{"type":"equip","entry":12,"slot":"accessory_2"}</c>.
    /// </summary>
    /// <param name="Entry">The inventory entry id.</param>
    /// <param name="Slot">An "Equipment slots" term, or null to let the server choose.</param>
    public sealed record EquipMessage(long Entry, string? Slot = null) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Equip;
    }

    /// <summary><c>unequip</c>: take off what is worn in <see cref="Slot"/>. <c>{"type":"unequip","slot":"weapon"}</c>.</summary>
    /// <param name="Slot">An "Equipment slots" term.</param>
    public sealed record UnequipMessage(string Slot) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Unequip;
    }

    /// <summary>
    /// <c>drop_item</c>: put <see cref="Quantity"/> units of entry <see cref="Entry"/> on the ground at the character's cell, for anyone to take.
    /// <c>{"type":"drop_item","entry":12,"quantity":1}</c>.
    /// </summary>
    /// <param name="Entry">The inventory entry id (not a worn one).</param>
    /// <param name="Quantity">How many, 1..the entry's quantity.</param>
    public sealed record DropItemMessage(long Entry, int Quantity) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.DropItem;
    }

    /// <summary>
    /// <c>insert_card</c>: socket the card in entry <see cref="Card"/> into a free card slot of the equipment entry <see cref="Entry"/>, for good (one card
    /// unit is used up). <c>{"type":"insert_card","entry":7,"card":12}</c>.
    /// </summary>
    /// <param name="Entry">The equipment entry (carried or worn).</param>
    /// <param name="Card">The card entry.</param>
    public sealed record InsertCardMessage(long Entry, long Card) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.InsertCard;
    }

    /// <summary>
    /// <c>refine</c>: try to raise the refine level of equipment entry <see cref="Entry"/> by one, using the next level's material; the outcome comes
    /// back as <see cref="RefineResultMessage"/>. <c>{"type":"refine","entry":7}</c>.
    /// </summary>
    /// <param name="Entry">The equipment entry (carried or worn).</param>
    public sealed record RefineMessage(long Entry) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Refine;
    }

    /// <summary>
    /// <c>chat</c> (T-901): say <see cref="Text"/> on <see cref="Channel"/>. <c>{"type":"chat","channel":"map","text":"hello"}</c>, or a whisper:
    /// <c>{"type":"chat","channel":"whisper","to":"Ada","text":"hello"}</c>. An intent: the server checks it (mute, rate, text) and sends the line back to everyone it reaches, the sender
    /// included; nothing is shown before that. A refusal comes back as <see cref="ChatRefusedMessage"/>.
    /// </summary>
    /// <param name="Channel">A "Chat channels" term a player may send on.</param>
    /// <param name="To">For a whisper: the character's name (any letter case); else null.</param>
    /// <param name="Text">What the player typed.</param>
    public sealed record ChatMessage(string Channel, string? To, string Text) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Chat;
    }

    /// <summary><c>block</c> (T-901): <c>{"type":"block","name":"Ada"}</c>. That character's lines no longer reach this account; its whispers are refused as if nobody of the name were online.</summary>
    /// <param name="Name">The character's name.</param>
    public sealed record BlockMessage(string Name) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Block;
    }

    /// <summary><c>unblock</c> (T-901): <c>{"type":"unblock","name":"Ada"}</c>.</summary>
    /// <param name="Name">The character's name, as <see cref="ChatStateMessage.Blocked"/> lists it.</param>
    public sealed record UnblockMessage(string Name) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Unblock;
    }

    /// <summary>
    /// <c>report</c> (T-901): <c>{"type":"report","line":812}</c>. Reports a line this client received, by the number its <see cref="ChatLineMessage"/> carried. The client never sends the text:
    /// the server keeps recent lines for a while and stores what was really said.
    /// </summary>
    /// <param name="Line">The line's number.</param>
    public sealed record ReportMessage(long Line) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Report;
    }

    /// <summary>
    /// <c>party_invite</c> (T-902): invite a character to this character's party (or to a new one, led by this character). By name, <c>{"type":"party_invite","name":"Bo"}</c>, or by
    /// tapping a player in view, <c>{"type":"party_invite","target":43}</c>: exactly one of the two. An intent: a refusal comes back as <see cref="PartyRefusedMessage"/>.
    /// </summary>
    /// <param name="Name">The character's name, as typed.</param>
    /// <param name="Target">The entity id of a player on this map.</param>
    public sealed record PartyInviteMessage(string? Name, long? Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyInvite;
    }

    /// <summary><c>party_answer</c> (T-902): <c>{"type":"party_answer","accept":true}</c>. Answers the invite this character has (<see cref="PartyInvitedMessage"/>).</summary>
    /// <param name="Accept">True joins the inviter's party; false declines.</param>
    public sealed record PartyAnswerMessage(bool Accept) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyAnswer;
    }

    /// <summary><c>party_leave</c> (T-902): <c>{"type":"party_leave"}</c>. Leaves the party.</summary>
    public sealed record PartyLeaveMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyLeave;
    }

    /// <summary><c>party_kick</c> (T-902, leader only): <c>{"type":"party_kick","target":43}</c>. Takes a member out of the party.</summary>
    /// <param name="Target">The member's entity id (<see cref="PartyMemberRecord.Id"/>).</param>
    public sealed record PartyKickMessage(long Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyKick;
    }

    /// <summary><c>party_lead</c> (T-902, leader only): <c>{"type":"party_lead","target":43}</c>. Hands the leader to a member.</summary>
    /// <param name="Target">The member's entity id.</param>
    public sealed record PartyLeadMessage(long Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyLead;
    }

    /// <summary>
    /// <c>trade_ask</c> (T-903): <c>{"type":"trade_ask","target":43}</c>. Asks a player on this map instance to trade, by tapping it or from the party panel. An intent: a refusal comes back
    /// as <see cref="TradeRefusedMessage"/>, the ask as <see cref="TradeNoticeMessage"/> <c>ask_sent</c>.
    /// </summary>
    /// <param name="Target">The entity id of the player asked.</param>
    public sealed record TradeAskMessage(long Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeAsk;
    }

    /// <summary><c>trade_answer</c> (T-903): <c>{"type":"trade_answer","accept":true}</c>. Answers the ask this character has (<see cref="TradeAskedMessage"/>).</summary>
    /// <param name="Accept">True opens the trade; false declines.</param>
    public sealed record TradeAnswerMessage(bool Accept) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeAnswer;
    }

    /// <summary>
    /// <c>trade_offer</c> (T-903): <c>{"type":"trade_offer","entry":1207,"quantity":3}</c>. Puts <see cref="Quantity"/> units of the carried entry in this side's offer, in place of any
    /// units of it there; 0 takes it out. Any change unlocks both sides.
    /// </summary>
    /// <param name="Entry">The inventory entry's id.</param>
    /// <param name="Quantity">Units offered, 0 to the entry's quantity.</param>
    public sealed record TradeOfferMessage(long Entry, int Quantity) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeOffer;
    }

    /// <summary><c>trade_currency</c> (T-903): <c>{"type":"trade_currency","currency":250}</c>. The currency in this side's offer, 0 to what it carries. Any change unlocks both sides.</summary>
    /// <param name="Currency">The amount.</param>
    public sealed record TradeCurrencyMessage(long Currency) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeCurrency;
    }

    /// <summary><c>trade_lock</c> (T-903): <c>{"type":"trade_lock"}</c>. Locks this side's offer as it stands.</summary>
    public sealed record TradeLockMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeLock;
    }

    /// <summary><c>trade_unlock</c> (T-903): <c>{"type":"trade_unlock"}</c>. Unlocks both offers (and any confirm).</summary>
    public sealed record TradeUnlockMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeUnlock;
    }

    /// <summary>
    /// <c>trade_confirm</c> (T-903): <c>{"type":"trade_confirm","version":7}</c>. Confirms the two locked offers of <see cref="TradeMessage.Version"/> <see cref="Version"/>; refused
    /// <c>stale</c> when they changed since. The swap happens when both confirmed the same version.
    /// </summary>
    /// <param name="Version">The version of the <c>trade</c> message whose offers the player saw.</param>
    public sealed record TradeConfirmMessage(int Version) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeConfirm;
    }

    /// <summary><c>trade_cancel</c> (T-903): <c>{"type":"trade_cancel"}</c>. Ends this character's ask or trade; nothing moves.</summary>
    public sealed record TradeCancelMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeCancel;
    }

    /// <summary>A message the server sends. <see cref="Type"/> is its <see cref="MessageTypes"/> name.</summary>
    public abstract record ServerMessage
    {
        /// <summary>The <c>type</c> field.</summary>
        public abstract string Type { get; }
    }

    /// <summary>
    /// <c>welcome</c>: the login worked and the character is in the world. A <see cref="SnapshotMessage"/> follows.
    /// <c>{"type":"welcome","version":1,"id":42,"map":"map.x","tick_rate":10,"content":"ab12...","token":"Qk3x..."}</c>.
    /// </summary>
    /// <param name="Version">The server's <see cref="ProtocolVersion"/>.</param>
    /// <param name="Id">The client's own entity id (the <c>id</c> of its record in snapshots and deltas).</param>
    /// <param name="MapId">The map the character is on.</param>
    /// <param name="TickRate">Server ticks per second; snapshots and deltas carry tick numbers at this rate.</param>
    /// <param name="Content">The server's content hash (the client bundle's <c>content_hash</c>), or null on a server that has none.</param>
    /// <param name="Token">The resume token for this character (ARCHITECTURE §10): keep it in memory only and send it in <c>hello</c> <c>resume</c>
    /// to take the character back after a drop. Every welcome carries a new one; the one used to resume no longer works.</param>
    public sealed record WelcomeMessage(int Version, long Id, string MapId, int TickRate, string? Content = null, string? Token = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Welcome;
    }

    /// <summary>
    /// One entity as a client sees it: <c>{"id":7,"kind":"monster","display_key":"monster.x","cell":[3,4],"facing":"e","next":[4,4],"speed":3.5,"hp":0.37}</c>.
    /// <c>display_key</c> is left out when there is none; <c>next</c> and <c>speed</c> only while walking; <c>hp</c> only for an entity that
    /// fights; <c>dead</c> only when true. A ground item (<c>kind</c> <c>item</c>, <c>display_key</c> = its item id) carries <c>quantity</c> and
    /// <c>mine</c>: <c>{"id":9,"kind":"item","display_key":"item.x","cell":[3,4],"facing":"s","quantity":2,"mine":true}</c>.
    /// </summary>
    /// <param name="Id">Entity id.</param>
    /// <param name="Kind">An <see cref="EntityKinds"/> term.</param>
    /// <param name="DisplayKey">The content id to look up in the display bundle, or null.</param>
    /// <param name="Cell">The last cell the entity fully reached.</param>
    /// <param name="Facing">A <c>Directions</c> term.</param>
    /// <param name="Next">The cell being walked to, or null when standing.</param>
    /// <param name="Speed">Cells per second while walking, 0 when standing.</param>
    /// <param name="Hp">HP as a fraction of max HP, 0–1 in steps of 0.01 (never 0 while alive, never 1 while hurt), or null for an entity
    /// without HP. Exact HP goes only to its owner (<see cref="VitalsRecord"/>).</param>
    /// <param name="Dead">True while the entity is dead.</param>
    /// <param name="Quantity">A ground item's quantity; 0 for everything else.</param>
    /// <param name="Mine">A ground item: true when this client may pick it up now (no loot priority, its own, or the priority is over); null for
    /// everything else. Who holds the priority, and until when, is never sent.</param>
    /// <param name="Weapon">A player: the "Weapon types" term of what it holds (<c>unarmed</c> for nothing); null for every other kind.</param>
    /// <param name="Worn">A player: the item ids it wears that have a worn visual, in "Equipment slots" order (the client finds each visual in its
    /// bundle); null or empty when none. Never stats.</param>
    /// <param name="Statuses">Its active statuses (T-501), in application order; null or empty when none. Left out of the JSON when empty.</param>
    /// <param name="Appearance">A player: its looks as ids (T-705), in <c>snapshot</c> entries and <c>appeared</c> entries only; null for every other kind. Never in a change.</param>
    /// <param name="Outfit">A player: the outfits it shows by slot (T-708), in "Outfit slots" order; null or empty when none. Left out of the JSON when empty. Display only.</param>
    public sealed record EntityRecord(long Id, string Kind, string? DisplayKey, GridCell Cell, string Facing, GridCell? Next, double Speed,
        double? Hp = null, bool Dead = false, int Quantity = 0, bool? Mine = null, string? Weapon = null, IReadOnlyList<string>? Worn = null,
        IReadOnlyList<StatusRecord>? Statuses = null, AppearanceRecord? Appearance = null, IReadOnlyList<ShownOutfit>? Outfit = null);

    /// <summary>One slot that shows an outfit (T-708): an "Outfit slots" term and the outfit id. On the wire a list of them is an object, <c>{"head":"outfit.x"}</c>.</summary>
    public readonly struct ShownOutfit : IEquatable<ShownOutfit>
    {
        /// <summary>Creates one.</summary>
        public ShownOutfit(string slot, string outfit)
        {
            Slot = slot;
            Outfit = outfit;
        }

        /// <summary>The slot.</summary>
        public string Slot { get; }

        /// <summary>The outfit id.</summary>
        public string Outfit { get; }

        /// <inheritdoc />
        public bool Equals(ShownOutfit other) => Slot == other.Slot && Outfit == other.Outfit;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ShownOutfit other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (Slot?.GetHashCode() ?? 0) * 397 ^ (Outfit?.GetHashCode() ?? 0);

        /// <inheritdoc />
        public override string ToString() => Slot + "=" + Outfit;
    }

    /// <summary>
    /// One status on an entity, as every observer is told (ARCHITECTURE §10, T-501): <c>{"id":"status.poison","stacks":1,"left_ms":7400,"debuff":true}</c>.
    /// <c>left_ms</c> is the simulated time it has left at the message's tick (the client counts it down itself; a change is sent only when the
    /// status starts, stacks, is refreshed or ends); <c>debuff</c> only when true. Never its chance, its numbers or who applied it.
    /// </summary>
    public readonly struct StatusRecord
    {
        /// <summary>Creates a record.</summary>
        public StatusRecord(string id, int stacks, int leftMs, bool debuff)
        {
            Id = id;
            Stacks = stacks;
            LeftMs = leftMs;
            Debuff = debuff;
        }

        /// <summary>The status id (<c>status.*</c>); the client finds its name, icon and vfx in its bundle.</summary>
        public string Id { get; }

        /// <summary>Stacks, at least 1.</summary>
        public int Stacks { get; }

        /// <summary>Milliseconds left at the message's tick, at least 0.</summary>
        public int LeftMs { get; }

        /// <summary>True for a debuff.</summary>
        public bool Debuff { get; }

        /// <inheritdoc />
        public override string ToString() => Id + " x" + Stacks + " " + LeftMs + "ms" + (Debuff ? " debuff" : "");
    }

    /// <summary>The fields of a <see cref="ChangeRecord"/> that are present (VOCABULARY.md "View fields").</summary>
    [Flags]
    public enum ChangedFields
    {
        /// <summary>None.</summary>
        None = 0,

        /// <summary><c>cell</c>.</summary>
        Cell = 1,

        /// <summary><c>facing</c>.</summary>
        Facing = 2,

        /// <summary><c>next</c> (present and null means the walk stopped).</summary>
        Next = 4,

        /// <summary><c>speed</c>.</summary>
        Speed = 8,

        /// <summary><c>display_key</c> (present and null means it was cleared).</summary>
        DisplayKey = 16,

        /// <summary><c>hp</c> (the rounded fraction changed).</summary>
        Hp = 32,

        /// <summary><c>dead</c> (died, or came back).</summary>
        Dead = 64,

        /// <summary><c>mine</c> (a ground item's loot priority ended).</summary>
        Mine = 128,

        /// <summary><c>weapon</c> and <c>worn</c> (a player put something on or took it off).</summary>
        Look = 256,

        /// <summary><c>statuses</c> (one started, stacked, was refreshed or ended; the whole list is sent, empty when none is left).</summary>
        Statuses = 512,

        /// <summary><c>outfit</c> (T-708).</summary>
        Outfit = 1024,
    }

    /// <summary>
    /// An entity already in view that changed: its id plus only the changed fields, e.g. <c>{"id":7,"cell":[4,4],"next":[5,4]}</c> or
    /// <c>{"id":7,"cell":[5,4],"next":null,"speed":0}</c>. Fields not in <see cref="Fields"/> keep their old value (and are default here).
    /// </summary>
    /// <param name="Id">Entity id.</param>
    /// <param name="Fields">Which of the other fields are present.</param>
    /// <param name="Cell">New cell, when <see cref="ChangedFields.Cell"/>.</param>
    /// <param name="Facing">New facing, when <see cref="ChangedFields.Facing"/>.</param>
    /// <param name="Next">New next cell (null: stopped), when <see cref="ChangedFields.Next"/>.</param>
    /// <param name="Speed">New speed, when <see cref="ChangedFields.Speed"/>.</param>
    /// <param name="DisplayKey">New display key, when <see cref="ChangedFields.DisplayKey"/>.</param>
    /// <param name="Hp">New HP fraction, when <see cref="ChangedFields.Hp"/>.</param>
    /// <param name="Dead">New dead flag, when <see cref="ChangedFields.Dead"/>.</param>
    /// <param name="Mine">New pick-up flag, when <see cref="ChangedFields.Mine"/>.</param>
    /// <param name="Weapon">New weapon type, when <see cref="ChangedFields.Look"/>.</param>
    /// <param name="Worn">New worn item ids, when <see cref="ChangedFields.Look"/> (empty: nothing visible).</param>
    /// <param name="Statuses">All its statuses now, when <see cref="ChangedFields.Statuses"/> (empty: none left).</param>
    /// <param name="Outfit">All the outfits it shows now, when <see cref="ChangedFields.Outfit"/> (empty: none).</param>
    public sealed record ChangeRecord(long Id, ChangedFields Fields, GridCell Cell, string? Facing, GridCell? Next, double Speed, string? DisplayKey,
        double Hp = 0, bool Dead = false, bool Mine = false, string? Weapon = null, IReadOnlyList<string>? Worn = null,
        IReadOnlyList<StatusRecord>? Statuses = null, IReadOnlyList<ShownOutfit>? Outfit = null);

    /// <summary>
    /// One attack seen in a tick, in a delta's <c>hits</c> as a compact array <c>[attacker, target, amount, result]</c>, e.g. <c>[42,7,13,"crit"]</c>.
    /// </summary>
    /// <param name="Attacker">Entity id of the attacker.</param>
    /// <param name="Target">Entity id of the target.</param>
    /// <param name="Amount">Damage dealt (0 on a miss).</param>
    /// <param name="Result">A "Hit results" term: <c>hit</c>, <c>crit</c> or <c>miss</c>.</param>
    public sealed record HitRecord(long Attacker, long Target, int Amount, string Result);

    /// <summary>
    /// The client's own exact HP and SP, in a snapshot's or delta's <c>vitals</c>: <c>{"hp":40,"max_hp":52,"sp":11,"max_sp":11}</c>. Only the
    /// owner gets it (others see the <c>hp</c> fraction); a delta carries it only when one of the four changed.
    /// </summary>
    public sealed record VitalsRecord(int Hp, int MaxHp, int Sp, int MaxSp);

    /// <summary>A cast that began in view (T-502): <c>[caster, skill, ms]</c>, the cast time in milliseconds (above 0). Never costs or numbers.</summary>
    public sealed record CastRecord(long Caster, string Skill, int Ms);

    /// <summary>A cast that ended in view (T-502): <c>[caster, skill, result]</c> with a "Cast results" term (<c>done</c>, <c>cancelled</c>, <c>interrupted</c>).</summary>
    public sealed record CastEndRecord(long Caster, string Skill, string Result);

    /// <summary>
    /// <c>snapshot</c>: everything the client sees at <see cref="Tick"/>; it replaces the client's view. A snapshot for another
    /// <see cref="MapId"/> than before means the character changed maps (a warp): load that map first.
    /// <c>{"type":"snapshot","tick":120,"map":"map.x","id":42,"entities":[...]}</c>.
    /// </summary>
    /// <param name="Tick">The map instance tick it describes.</param>
    /// <param name="MapId">The map the character is on.</param>
    /// <param name="Id">The client's own entity id (always among <see cref="Entities"/>).</param>
    /// <param name="Entities">Every visible entity, ordered by id.</param>
    /// <param name="Vitals">The client's own HP and SP, or null for a character without them.</param>
    public sealed record SnapshotMessage(long Tick, string MapId, long Id, IReadOnlyList<EntityRecord> Entities, VitalsRecord? Vitals = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Snapshot;
    }

    /// <summary>
    /// <c>delta</c>: one tick's changes, relative to the last snapshot or delta. Empty lists are left out.
    /// <c>{"type":"delta","tick":121,"appeared":[...],"changed":[...],"disappeared":[9],"hits":[[42,7,13,"hit"]],"vitals":{...}}</c>.
    /// </summary>
    /// <param name="Tick">The map instance tick it describes.</param>
    /// <param name="Appeared">Entities that came into view, as full records.</param>
    /// <param name="Changed">Entities in view that changed, with only their changed fields.</param>
    /// <param name="Disappeared">Ids of entities that went out of view.</param>
    /// <param name="Hits">This tick's attacks whose attacker or target is in view, in the order they were resolved; null or empty when none.</param>
    /// <param name="Vitals">The client's own HP and SP when they changed this tick, else null.</param>
    /// <param name="Casts">Casts that began this tick whose caster is in view (cast bars); null or empty when none.</param>
    /// <param name="CastEnds">Casts that ended this tick whose caster is in view; null or empty when none.</param>
    public sealed record DeltaMessage(long Tick, IReadOnlyList<EntityRecord> Appeared, IReadOnlyList<ChangeRecord> Changed, IReadOnlyList<long> Disappeared,
        IReadOnlyList<HitRecord>? Hits = null, VitalsRecord? Vitals = null, IReadOnlyList<CastRecord>? Casts = null, IReadOnlyList<CastEndRecord>? CastEnds = null)
        : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Delta;
    }

    /// <summary>
    /// <c>rejected</c>: an intent was refused and nothing changed. <c>{"type":"rejected","intent":"move","reason":"no_path","cell":[30,2]}</c> or
    /// <c>{"type":"rejected","intent":"attack","reason":"target_gone","target":99}</c>.
    /// </summary>
    /// <param name="Intent">The message type that was refused (<c>move</c>, <c>attack</c>, or an item intent: <c>pick_up</c>, <c>use_item</c>, <c>equip</c>,
    /// <c>unequip</c>, <c>drop_item</c>, <c>insert_card</c>, <c>refine</c>).</param>
    /// <param name="Reason">For <c>move</c> a VOCABULARY.md "Movement reasons" (<c>MoveRejected</c>) term, for <c>attack</c> an "Attack reasons" term,
    /// for an item intent an "Item reasons" term.</param>
    /// <param name="Cell">The target cell of the refused move, if any.</param>
    /// <param name="Target">The target of the refused attack, if any.</param>
    /// <param name="Entry">The inventory entry of a refused <c>use_item</c>, <c>equip</c> or <c>drop_item</c>, or the equipment entry of a refused
    /// <c>insert_card</c> or <c>refine</c>, if any.</param>
    /// <param name="Slot">The slot of a refused <c>equip</c> or <c>unequip</c>, if any.</param>
    /// <param name="Skill">The skill (or, for <c>change_job</c>, the job) of a refused <c>use_skill</c>, <c>learn_skill</c> or <c>change_job</c>, if any.</param>
    /// <param name="Stat">The primary stat of a refused <c>raise_stat</c> (reason: a "Stat reasons" term), if any.</param>
    /// <param name="Quest">The quest of a refused start (<c>intent</c> <c>dialogue_choice</c>, the choice that asked for it) or <c>abandon_quest</c> (reason: a "Quest reasons" term), if any (T-602).</param>
    /// <param name="Item">The item of a refused <c>shop_buy</c> (T-603), if any.</param>
    /// <remarks>A refused <c>start_autopilot</c> (T-604) has an "Autopilot reasons" term, with the <c>Quest</c> or <c>Skill</c> it is about.</remarks>
    public sealed record RejectedMessage(string Intent, string Reason, GridCell? Cell, long? Target = null, long? Entry = null, string? Slot = null, string? Skill = null, string? Stat = null,
        string? Quest = null, string? Item = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Rejected;
    }

    /// <summary>
    /// <c>autopilot</c> (T-604, owner only): sent when any of it changes, and once after the first snapshot of a connection that takes over a character that has had a run.
    /// <c>{"type":"autopilot","mode":"running","quest":"quest.first_hunt","doing":"hunting"}</c>, <c>{"type":"autopilot","mode":"off","reason":"finished"}</c>.
    /// </summary>
    /// <param name="Mode">An "Autopilot modes" term.</param>
    /// <param name="Quest">The quest it plays, while running or paused.</param>
    /// <param name="Doing">An "Autopilot activities" term, while running.</param>
    /// <param name="Reason">An "Autopilot pauses" term while paused, an "Autopilot stops" term when off.</param>
    public sealed record AutopilotMessage(string Mode, string? Quest, string? Doing, string? Reason) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Autopilot;
    }

    /// <summary><c>wardrobe</c> (T-708, owner only): after every snapshot and whenever something of it changes; it replaces what the client had. Ids only.</summary>
    /// <param name="Owned">The outfits the character owns, in id order.</param>
    /// <param name="Chosen">What is chosen by slot, also while it is hidden.</param>
    /// <param name="Hidden">Every outfit is hidden.</param>
    public sealed record WardrobeMessage(IReadOnlyList<string> Owned, IReadOnlyList<ShownOutfit> Chosen, bool Hidden) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Wardrobe;
    }

    /// <summary>
    /// <c>attack_stopped</c>: the client's auto-attack on <see cref="Target"/> ended. <c>{"type":"attack_stopped","target":7,"reason":"target_dead"}</c>.
    /// </summary>
    /// <param name="Target">The former target.</param>
    /// <param name="Reason">An "Attack reasons" term (<c>target_dead</c>, <c>unreachable</c>, <c>cancelled</c>, ...).</param>
    public sealed record AttackStoppedMessage(long Target, string Reason) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.AttackStopped;
    }

    /// <summary>
    /// <c>exp</c>: the client's levels and EXP, after every snapshot and whenever they change.
    /// <c>{"type":"exp","base_level":2,"base_exp":5,"base_next":24,"job_level":1,"job_exp":4,"job_next":10}</c>.
    /// </summary>
    /// <param name="BaseLevel">Base level.</param>
    /// <param name="BaseExp">Base EXP into the current level.</param>
    /// <param name="BaseNext">Base EXP the current level needs to level up; 0 at the max level.</param>
    /// <param name="JobLevel">Job level.</param>
    /// <param name="JobExp">Job EXP into the current job level.</param>
    /// <param name="JobNext">Job EXP the current job level needs; 0 at the job's max level.</param>
    public sealed record ExpMessage(int BaseLevel, long BaseExp, long BaseNext, int JobLevel, long JobExp, long JobNext) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Exp;
    }

    /// <summary>
    /// <c>level_up</c>: the client reached a level, one message per level, in order; exactly one of the two fields is present.
    /// <c>{"type":"level_up","base_level":3}</c> or <c>{"type":"level_up","job_level":2}</c>.
    /// </summary>
    /// <param name="BaseLevel">The base level reached, or null.</param>
    /// <param name="JobLevel">The job level reached, or null.</param>
    public sealed record LevelUpMessage(int? BaseLevel, int? JobLevel) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.LevelUp;
    }

    /// <summary><c>you_died</c>: the client's character died; it sends <c>respawn</c> to come back. <c>{"type":"you_died","killer":7}</c>.</summary>
    /// <param name="Killer">Entity id of the killer, or null.</param>
    public sealed record YouDiedMessage(long? Killer) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.YouDied;
    }

    /// <summary>
    /// <c>respawned</c>: the answer to <c>respawn</c>: the character is alive again with <see cref="Hp"/> on <see cref="Cell"/> of <see cref="MapId"/>
    /// (a snapshot of that place follows). <c>{"type":"respawned","map":"map.x","cell":[12,12],"hp":26}</c>.
    /// </summary>
    /// <param name="MapId">The respawn map.</param>
    /// <param name="Cell">The respawn cell.</param>
    /// <param name="Hp">HP it came back with.</param>
    public sealed record RespawnedMessage(string MapId, GridCell Cell, int Hp) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Respawned;
    }

    /// <summary>
    /// One inventory entry on the wire: <c>{"id":12,"item":"item.x","quantity":3}</c>, with <c>"slot":"weapon"</c> while worn, and for equipment
    /// <c>"cards":["item.c"]</c> and <c>"refine":2</c> when it has any (owner only: other players never see them).
    /// </summary>
    /// <param name="Id">Entry id (equipment: its instance id); what <c>use_item</c>, <c>equip</c> and <c>drop_item</c> name.</param>
    /// <param name="Item">The item id (name and icon come from the bundle).</param>
    /// <param name="Quantity">How many.</param>
    /// <param name="Slot">The "Equipment slots" term it is worn in, or null.</param>
    /// <param name="Cards">The card item ids socketed in it, in socket order; null or empty when none.</param>
    /// <param name="Refine">Its refine level (0 when not refined).</param>
    public sealed record InventoryEntryRecord(long Id, string Item, int Quantity, string? Slot = null, IReadOnlyList<string>? Cards = null, int Refine = 0)
    {
        /// <summary>How many cards it holds.</summary>
        public int CardCount => Cards?.Count ?? 0;
    }

    /// <summary>
    /// <c>inventory</c> (owner only): the whole inventory and equipment, after every snapshot (join, resume, warp, resync). It replaces what the
    /// client had. <c>{"type":"inventory","rev":4,"slots":100,"weight":85,"weight_limit":2040,"load":"normal","entries":[...]}</c>.
    /// </summary>
    /// <param name="Rev">The change counter: each <see cref="InventoryChangedMessage"/> after this carries the next value.</param>
    /// <param name="Slots">Most entries the inventory holds.</param>
    /// <param name="Weight">Carried weight (worn items included).</param>
    /// <param name="WeightLimit">The character's <c>weight_limit</c>.</param>
    /// <param name="Load">A "Weight states" term.</param>
    /// <param name="Entries">Every entry, by id.</param>
    /// <param name="Currency">The character's currency (T-602; engine id <c>currency</c>, its display name is in the display bundle).</param>
    public sealed record InventoryMessage(long Rev, int Slots, int Weight, int WeightLimit, string Load, IReadOnlyList<InventoryEntryRecord> Entries, long Currency = 0) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Inventory;
    }

    /// <summary>
    /// <c>inventory_changed</c> (owner only): one change, relative to the last <c>inventory</c> or <c>inventory_changed</c>; <see cref="Rev"/> is one
    /// more than the last one. A client that sees a gap keeps what it has and waits for the next <c>inventory</c>.
    /// <c>{"type":"inventory_changed","rev":5,"cause":"used","item":"item.x","weight":80,"weight_limit":2040,"load":"normal","entries":[...],"removed":[12]}</c>.
    /// </summary>
    /// <param name="Rev">The change counter.</param>
    /// <param name="Cause">An "Inventory changes" term.</param>
    /// <param name="Item">The item the change is about (picked up, used, equipped, taken off, dropped), or null for <c>weight</c>.</param>
    /// <param name="Weight">Carried weight now.</param>
    /// <param name="WeightLimit">The weight limit now.</param>
    /// <param name="Load">A "Weight states" term.</param>
    /// <param name="Entries">Entries added or changed, as they are now; empty when none.</param>
    /// <param name="Removed">Ids of entries that are gone; empty when none.</param>
    /// <param name="Currency">The character's currency now (T-602).</param>
    public sealed record InventoryChangedMessage(long Rev, string Cause, string? Item, int Weight, int WeightLimit, string Load,
        IReadOnlyList<InventoryEntryRecord> Entries, IReadOnlyList<long> Removed, long Currency = 0) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.InventoryChanged;
    }

    /// <summary>
    /// <c>refine_result</c> (owner only): how a <c>refine</c> came out, sent with the <c>inventory_changed</c> (<c>refined</c>) that applies it.
    /// <c>{"type":"refine_result","entry":7,"item":"item.x","result":"success","refine":3}</c>.
    /// </summary>
    /// <param name="Entry">The equipment entry (gone after a <c>break</c>).</param>
    /// <param name="Item">Its item id.</param>
    /// <param name="Result">A "Refine results" term: <c>success</c>, <c>keep</c>, <c>down</c> or <c>break</c>.</param>
    /// <param name="Refine">The refine level now (what it was when it broke).</param>
    public sealed record RefineResultMessage(long Entry, string Item, string Result, int Refine) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.RefineResult;
    }

    /// <summary>One skill the player has learned.</summary>
    public sealed record SkillLevelRecord(string Skill, int Level);

    /// <summary>One of the player's skills on cooldown: the milliseconds left when the message was made.</summary>
    public sealed record SkillCooldownRecord(string Skill, int Ms);

    /// <summary>
    /// <c>skills</c> (T-502, owner only): the player's job, unspent skill points, learned skills, cooldowns left, and the jobs it may change to right now.
    /// After every snapshot and after a learn, a job change or a job level.
    /// </summary>
    public sealed record SkillsMessage(string Job, int Points, IReadOnlyList<SkillLevelRecord> Levels, IReadOnlyList<SkillCooldownRecord> Cooldowns, IReadOnlyList<string> Jobs)
        : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Skills;
    }

    /// <summary>One primary stat in a <see cref="StatsMessage"/>: its base value (allocated points included) and what raising it by one costs (0: it cannot be raised).</summary>
    public sealed record StatRecord(string Stat, int Value, int NextCost);

    /// <summary>One derived stat in a <see cref="StatsMessage"/>: its final value (floored), computed by the server.</summary>
    public sealed record DerivedRecord(string Stat, int Value);

    /// <summary>
    /// <c>stats</c> (T-503, owner only): unspent stat points, the six primary stats with the cost of the next +1 (so the client never computes a cost), and the
    /// derived stats the stats panel shows (<c>DerivedStats.Panel</c>). After every snapshot and after anything that changes them.
    /// <c>{"type":"stats","points":4,"stats":[["str",3,2]],"derived":[["atk",12]]}</c>.
    /// </summary>
    public sealed record StatsMessage(int Points, IReadOnlyList<StatRecord> Stats, IReadOnlyList<DerivedRecord> Derived) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Stats;
    }

    /// <summary>One choice shown at a dialogue node: <see cref="Index"/> is what <c>dialogue_choice</c> sends back.</summary>
    public sealed record DialogueChoiceRecord(int Index, string Text);

    /// <summary>
    /// <c>dialogue</c> (T-601, owner only): the dialogue is at a node. <c>{"type":"dialogue","npc":12,"speaker":"npc.guide","text":"...","choices":[{"index":0,"text":"..."}]}</c>.
    /// </summary>
    /// <param name="Npc">The NPC entity being talked to.</param>
    /// <param name="Speaker">The speaking NPC's id (<c>npc.*</c>); the client looks up its name, title and portrait in the display bundle.</param>
    /// <param name="Portrait">A portrait asset key that overrides the speaker's, or null.</param>
    /// <param name="Text">The line.</param>
    /// <param name="Choices">The choices shown (those whose conditions hold), each with its original index. Empty: a single "continue".</param>
    /// <param name="More">With no choices: true when the dialogue goes on after this node (continue), false when it ends there (done).</param>
    public sealed record DialogueMessage(long Npc, string Speaker, string? Portrait, string Text, IReadOnlyList<DialogueChoiceRecord> Choices, bool More) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Dialogue;
    }

    /// <summary><c>dialogue_ended</c> (T-601, owner only): the dialogue, and every service it opened, ended for a "Dialogue end reasons" term.</summary>
    public sealed record DialogueEndedMessage(string Reason) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.DialogueEnded;
    }

    /// <summary><c>service_opened</c> (T-601, owner only): the NPC <see cref="Npc"/> opened <see cref="Service"/> (a "Service ids" term) for the character; the client opens its panel.</summary>
    public sealed record ServiceOpenedMessage(string Service, long Npc) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ServiceOpened;
    }

    /// <summary>One item and a price: what a shop sells it at (<c>stock</c>), or what it pays for it (<c>sell</c>).</summary>
    public sealed record ShopStockRecord(string Item, long Price);

    /// <summary>
    /// <c>shop</c> (T-603, owner only): the shop an <c>open_service</c> action just opened (sent right after <c>service_opened</c>). The client lists <see cref="Stock"/> as its buy tab; what
    /// it can sell is its own bag, priced by <see cref="Sell"/> (the display bundle holds no prices, ARCHITECTURE §5.4).
    /// <c>{"type":"shop","npc":12,"shop":"shop.meadow_goods","max_quantity":99,"stock":[{"item":"item.healing_draught","price":40}],"sell":[{"item":"item.gel_glob","price":4}]}</c>.
    /// </summary>
    /// <param name="Npc">The NPC entity the shop belongs to.</param>
    /// <param name="Shop">The shop id (<c>shop.*</c>).</param>
    /// <param name="MaxQuantity">Most units one <c>shop_buy</c> may ask for (<c>shop.max_buy_quantity</c>).</param>
    /// <param name="Stock">What it sells, in display order.</param>
    /// <param name="Sell">What it pays per unit for every item it buys (any shop buys every item with a sell price except quest items), by item id.</param>
    public sealed record ShopMessage(long Npc, string Shop, int MaxQuantity, IReadOnlyList<ShopStockRecord> Stock, IReadOnlyList<ShopStockRecord> Sell) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Shop;
    }

    /// <summary>
    /// One objective of a quest's current step, for its owner (T-602): <c>{"type":"kill","target":"monster.gel_slime","have":3,"need":5}</c>. The owner sees the counts
    /// of its own active quests only; the display bundle never holds objectives.
    /// </summary>
    /// <param name="Type">A "Quest objective types" term.</param>
    /// <param name="Target">What it is about, for the label: a monster, item, NPC, job or map id; null for <c>level</c>.</param>
    /// <param name="Have">How far it is (kills, items in the bag, the base level...), never above <see cref="Need"/>.</param>
    /// <param name="Need">What completes it (1 for talk, reach, job and equip).</param>
    public sealed record QuestObjectiveRecord(string Type, string? Target, int Have, int Need)
    {
        /// <summary>True when met.</summary>
        public bool Met => Have >= Need;
    }

    /// <summary>One active quest for its owner (T-602): the current step (an index into the bundle's step texts), its objectives, and why its turn-in is held, if it is.</summary>
    /// <param name="Quest">The quest id.</param>
    /// <param name="Step">The current step, 0-based (the display bundle's <c>steps</c> index).</param>
    /// <param name="Objectives">The current step's objectives, in content order.</param>
    /// <param name="Held">A "Quest reasons" term (<c>inventory_full</c>, <c>too_heavy</c>, <c>missing_item</c>) while the met step waits for room in the bag; null otherwise.</param>
    public sealed record QuestEntryRecord(string Quest, int Step, IReadOnlyList<QuestObjectiveRecord> Objectives, string? Held = null);

    /// <summary>
    /// <c>quest_log</c> (T-602, owner only): the whole quest log, after every snapshot and whenever the NPC markers change. It replaces what the client had.
    /// <c>{"type":"quest_log","active":[{"quest":"quest.first_hunt","step":0,"objectives":[...]}],"completed":["quest.x"],"offer":["npc.guide"],"turn_in":[]}</c>.
    /// </summary>
    /// <param name="Active">Active quests, by id.</param>
    /// <param name="Completed">Completed quest ids, by id.</param>
    /// <param name="Offer">NPC ids (<c>npc.*</c>) with a quest the character could start now.</param>
    /// <param name="TurnIn">NPC ids whose talk would complete the current step of an active quest.</param>
    public sealed record QuestLogMessage(IReadOnlyList<QuestEntryRecord> Active, IReadOnlyList<string> Completed, IReadOnlyList<string> Offer, IReadOnlyList<string> TurnIn)
        : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.QuestLog;
    }

    /// <summary>
    /// <c>quest</c> (T-602, owner only): one quest changed. <c>{"type":"quest","change":"progress","quest":"quest.first_hunt","entry":{...}}</c>. The entry is the quest as
    /// it is now (absent for <c>completed</c> and <c>abandoned</c>).
    /// </summary>
    /// <param name="Change">A "Quest changes" term.</param>
    /// <param name="Quest">The quest id.</param>
    /// <param name="Entry">The active quest now, or null when it is no longer active.</param>
    public sealed record QuestMessage(string Change, string Quest, QuestEntryRecord? Entry) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Quest;
    }

    /// <summary><c>cooldown</c> (T-502, owner only): <see cref="Skill"/> went on cooldown for <see cref="Ms"/> milliseconds.</summary>
    public sealed record CooldownMessage(string Skill, int Ms) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Cooldown;
    }

    /// <summary><c>pong</c>: answer to <see cref="PingMessage"/>. <c>{"type":"pong","seq":7}</c>.</summary>
    /// <param name="Seq">The ping's number.</param>
    public sealed record PongMessage(long Seq) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Pong;
    }

    /// <summary>
    /// <c>content_changed</c> (T-801): a development server with hot reload swapped in new content, and the display bundle it would export now has the hash
    /// <paramref name="Content"/>. <c>{"type":"content_changed","content":"&lt;sha-256&gt;"}</c>. Sent to every session that plays, only when that hash changed (a change to
    /// server-only data, such as drops or monster stats, sends nothing). The client cannot take a new bundle while it plays (v1): it says the content changed and that
    /// the game must be restarted to show it. A server that is not a development one with <c>--hot-reload</c> never sends it.
    /// </summary>
    /// <param name="Content">The new bundle hash (lowercase hex).</param>
    public sealed record ContentChangedMessage(string Content) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ContentChanged;
    }

    /// <summary>
    /// <c>announcement</c> (T-806): staff speak to everyone playing on this server. <c>{"type":"announcement","kind":"staff","text":"..."}</c>, or
    /// <c>{"type":"announcement","kind":"maintenance","in":300}</c>: the server goes into maintenance and every game ends in <c>in</c> seconds (saved). The client shows it on the HUD.
    /// </summary>
    /// <param name="Kind">An <see cref="AnnouncementKinds"/> term.</param>
    /// <param name="Text">Staff's words (for <c>staff</c>; at most <see cref="AnnouncementKinds.MaxTextLength"/> characters), else null.</param>
    /// <param name="InSeconds">Seconds until the games end (for <c>maintenance</c>), else null.</param>
    public sealed record AnnouncementMessage(string Kind, string? Text, long? InSeconds) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Announcement;
    }

    /// <summary>
    /// <c>chat_line</c> (T-901): a line the server accepted. <c>{"type":"chat_line","line":812,"channel":"map","from":42,"name":"Ada","text":"hello"}</c>; a whisper adds
    /// <c>"to":"Bo"</c>, and its sender gets the same message. The text is the server's cleaned copy; the name is the character's, which the client draws apart from the text.
    /// </summary>
    /// <param name="Line">The line's number on this server (what a <see cref="ReportMessage"/> names).</param>
    /// <param name="Channel">A "Chat channels" term.</param>
    /// <param name="From">The sender's entity id (this server's, like a view record's).</param>
    /// <param name="Name">The sender's character name.</param>
    /// <param name="Text">What was said.</param>
    /// <param name="To">For a whisper: the receiver's character name; else null.</param>
    public sealed record ChatLineMessage(long Line, string Channel, long From, string Name, string Text, string? To = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ChatLine;
    }

    /// <summary>
    /// <c>chat_refused</c> (T-901): <c>{"type":"chat_refused","intent":"chat","reason":"muted","retry_after":240}</c>. The line went nowhere (or the block, unblock or report was not made).
    /// </summary>
    /// <param name="Intent">The message refused: <c>chat</c>, <c>block</c>, <c>unblock</c> or <c>report</c>.</param>
    /// <param name="Reason">A "Chat reasons" term.</param>
    /// <param name="RetryAfterSeconds">For <c>muted</c> and <c>rate_limited</c>: seconds until a line may go out; else null.</param>
    /// <param name="Channel">The refused line's channel, for <c>chat</c>.</param>
    /// <param name="To">The name the whisper, block or unblock named, if any.</param>
    /// <param name="Line">The line a refused report named.</param>
    public sealed record ChatRefusedMessage(string Intent, string Reason, long? RetryAfterSeconds = null, string? Channel = null, string? To = null, long? Line = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ChatRefused;
    }

    /// <summary>
    /// <c>chat_state</c> (T-901): <c>{"type":"chat_state","max_length":120,"blocked":["Bo"]}</c>. After every welcome and every block or unblock; it replaces what the client had.
    /// </summary>
    /// <param name="MaxLength">The longest line the server takes, in UTF-16 code units after cleaning (server configuration).</param>
    /// <param name="Blocked">The names of the characters this account blocks, by name.</param>
    public sealed record ChatStateMessage(int MaxLength, IReadOnlyList<string> Blocked) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ChatState;
    }

    /// <summary><c>chat_reported</c> (T-901): <c>{"type":"chat_reported","line":812}</c>. The report is stored for staff.</summary>
    /// <param name="Line">The line reported.</param>
    public sealed record ChatReportedMessage(long Line) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.ChatReported;
    }

    /// <summary>One member of a party in <see cref="PartyMessage"/> (T-902): its entity id and character name.</summary>
    public sealed record PartyMemberRecord(long Id, string Name);

    /// <summary>
    /// One member's state in <see cref="PartyMembersMessage"/> (T-902): HP and SP in whole percent (a living member never shows 0), its map and cell, whether it is dead, and
    /// <see cref="Away"/> when it is in its disconnect grace period (then only the id is meaningful).
    /// </summary>
    public sealed record PartyMemberStatusRecord(long Id, int Hp, int Sp, string? Map, GridCell? Cell, bool Dead, bool Away);

    /// <summary>
    /// <c>party</c> (T-902, own only): the party as it is now, after every change and every welcome:
    /// <c>{"type":"party","party":3,"leader":42,"members":[{"id":42,"name":"Ada"},{"id":43,"name":"Bo"}]}</c>, members in party order. Not in a party: <c>{"type":"party","members":[]}</c>.
    /// </summary>
    /// <param name="Party">The party's number on this server, or null when in none.</param>
    /// <param name="Leader">The leader's entity id (0 when in none).</param>
    /// <param name="Members">The members, this character included; empty when in none.</param>
    public sealed record PartyMessage(long? Party, long Leader, IReadOnlyList<PartyMemberRecord> Members) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Party;
    }

    /// <summary>
    /// <c>party_members</c> (T-902, members only): how the other members are, at most once per <c>--party-status-interval-ms</c> and only when it changed:
    /// <c>{"type":"party_members","status":[{"id":43,"hp":80,"sp":100,"map":"map.meadow","cell":[3,4]}]}</c>; <c>"dead":true</c> and <c>"away":true</c> only when true.
    /// </summary>
    /// <param name="Status">One entry per other member with a known state.</param>
    public sealed record PartyMembersMessage(IReadOnlyList<PartyMemberStatusRecord> Status) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyMembers;
    }

    /// <summary><c>party_invited</c> (T-902, the invited only): <c>{"type":"party_invited","from":42,"name":"Ada","expires_in":30}</c>. Answer with <see cref="PartyAnswerMessage"/>.</summary>
    /// <param name="From">The inviter's entity id.</param>
    /// <param name="Name">The inviter's character name.</param>
    /// <param name="ExpiresInSeconds">Seconds until the invite expires.</param>
    public sealed record PartyInvitedMessage(long From, string Name, long ExpiresInSeconds) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyInvited;
    }

    /// <summary><c>party_notice</c> (T-902): <c>{"type":"party_notice","notice":"declined","name":"Bo"}</c>. News a <see cref="PartyMessage"/> alone does not carry.</summary>
    /// <param name="Notice">A "Party notices" term.</param>
    /// <param name="Name">The character it is about, if any.</param>
    public sealed record PartyNoticeMessage(string Notice, string? Name = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyNotice;
    }

    /// <summary><c>party_refused</c> (T-902, own only): <c>{"type":"party_refused","intent":"party_invite","reason":"full","name":"Bo"}</c>. Nothing changed.</summary>
    /// <param name="Intent">The message refused: a <c>party_*</c> client type.</param>
    /// <param name="Reason">A "Party reasons" term.</param>
    /// <param name="RetryAfterSeconds">For <c>rate_limited</c>: when the next invite may go.</param>
    /// <param name="Name">The name an invite named, if any.</param>
    public sealed record PartyRefusedMessage(string Intent, string Reason, long? RetryAfterSeconds = null, string? Name = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.PartyRefused;
    }

    /// <summary>
    /// <c>trade_asked</c> (T-903, the asked only): <c>{"type":"trade_asked","from":42,"name":"Ada","expires_in":20}</c>. Answer with <see cref="TradeAnswerMessage"/>.
    /// </summary>
    /// <param name="From">The asker's entity id.</param>
    /// <param name="Name">The asker's character name.</param>
    /// <param name="ExpiresInSeconds">Seconds until the ask expires.</param>
    public sealed record TradeAskedMessage(long From, string Name, long ExpiresInSeconds) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeAsked;
    }

    /// <summary>
    /// One offered entry in a <see cref="TradeSideRecord"/> (T-903): the entry id on the giver's side, the item, the units offered, and for equipment its cards and refine level (what the
    /// receiver would get).
    /// </summary>
    public sealed record TradeItemRecord(long Entry, string Item, int Quantity, IReadOnlyList<string>? Cards = null, int Refine = 0);

    /// <summary>One side of a <see cref="TradeMessage"/> (T-903): what it offers, and whether it locked and confirmed.</summary>
    public sealed record TradeSideRecord(IReadOnlyList<TradeItemRecord> Items, long Currency, bool Locked, bool Confirmed);

    /// <summary>
    /// <c>trade</c> (T-903, the two only): the open trade as the server holds it, after every change:
    /// <c>{"type":"trade","partner":43,"name":"Bo","version":7,"mine":{"items":[...],"currency":0,"locked":true},"theirs":{"items":[],"currency":250}}</c>
    /// (<c>locked</c>, <c>confirmed</c> only when true). A confirm names <see cref="Version"/>.
    /// </summary>
    /// <param name="Partner">The other side's entity id.</param>
    /// <param name="Name">The other side's character name.</param>
    /// <param name="Version">Goes up with every change of either offer or lock.</param>
    /// <param name="Mine">This character's side.</param>
    /// <param name="Theirs">The other side.</param>
    public sealed record TradeMessage(long Partner, string Name, int Version, TradeSideRecord Mine, TradeSideRecord Theirs) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Trade;
    }

    /// <summary><c>trade_notice</c> (T-903): <c>{"type":"trade_notice","notice":"declined","name":"Bo"}</c>. News about an ask.</summary>
    /// <param name="Notice">A "Trade notices" term.</param>
    /// <param name="Name">The other character, if known.</param>
    public sealed record TradeNoticeMessage(string Notice, string? Name = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeNotice;
    }

    /// <summary>
    /// <c>trade_closed</c> (T-903, the two only): <c>{"type":"trade_closed","reason":"too_far","name":"Bo"}</c>, <c>"own":true</c> when this side caused it (it cancelled, walked away, its bag
    /// was full). Only <c>done</c> moved anything; the <c>inventory_changed</c> of the swap comes before it.
    /// </summary>
    /// <param name="Reason">A "Trade ends" term.</param>
    /// <param name="Name">The other side's character name, if known.</param>
    /// <param name="Own">True when this side caused the end.</param>
    public sealed record TradeClosedMessage(string Reason, string? Name = null, bool Own = false) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeClosed;
    }

    /// <summary><c>trade_refused</c> (T-903, own only): <c>{"type":"trade_refused","intent":"trade_offer","reason":"not_tradeable"}</c>. Nothing changed.</summary>
    /// <param name="Intent">The message refused: a <c>trade_*</c> client type.</param>
    /// <param name="Reason">A "Trade reasons" term.</param>
    /// <param name="RetryAfterSeconds">For <c>rate_limited</c>: when the next ask may go.</param>
    public sealed record TradeRefusedMessage(string Intent, string Reason, long? RetryAfterSeconds = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.TradeRefused;
    }

    /// <summary>
    /// <c>bye</c>: the last message before the server closes the connection. <c>{"type":"bye","reason":"timeout"}</c>. A <c>banned</c> bye of a timed ban says how long is left:
    /// <c>{"type":"bye","reason":"banned","retry_after":86400}</c> (seconds; none for a ban without an end, T-806).
    /// </summary>
    /// <param name="Reason">A <see cref="ByeReasons"/> term.</param>
    /// <param name="RetryAfterSeconds">For <c>banned</c>: seconds until the ban ends, or null when it never does. Null for every other reason.</param>
    public sealed record ByeMessage(string Reason, long? RetryAfterSeconds = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Bye;
    }
}
