using Game.Shared.Vocabulary;

namespace Game.Shared.Protocol
{
    /// <summary>The wire protocol's version. A client sends it in <c>hello</c>; any other value is refused with <c>bye</c> <c>bad_version</c>.</summary>
    public static class ProtocolVersion
    {
        /// <summary>The version this build speaks. Bump it on any change to a message's shape.</summary>
        public const int Current = 1;
    }

    /// <summary>Message type names, the <c>type</c> field of every message (VOCABULARY.md "Message types").</summary>
    public static class MessageTypes
    {
        /// <summary><c>hello</c> (client → server, first message): protocol version, content hash, and a dev display name or a resume token.</summary>
        public const string Hello = "hello";

        /// <summary><c>move</c> (client → server): walk to a cell.</summary>
        public const string Move = "move";

        /// <summary><c>stop</c> (client → server): stop walking at the next cell.</summary>
        public const string Stop = "stop";

        /// <summary><c>attack</c> (client → server): auto-attack an entity in view.</summary>
        public const string Attack = "attack";

        /// <summary><c>stop_attack</c> (client → server): stop auto-attacking.</summary>
        public const string StopAttack = "stop_attack";

        /// <summary><c>respawn</c> (client → server): a dead character comes back at the respawn point.</summary>
        public const string Respawn = "respawn";

        /// <summary><c>ping</c> (client → server): keep-alive and round-trip probe.</summary>
        public const string Ping = "ping";

        /// <summary><c>logout</c> (client → server): leave on purpose; the character is removed at once, without a grace period.</summary>
        public const string Logout = "logout";

        /// <summary><c>pick_up</c> (client to server): walk to a ground item and take it.</summary>
        public const string PickUp = "pick_up";

        /// <summary><c>use_item</c> (client to server): use a consumable from the inventory.</summary>
        public const string UseItem = "use_item";

        /// <summary><c>equip</c> (client to server): put on an equipment item from the inventory.</summary>
        public const string Equip = "equip";

        /// <summary><c>unequip</c> (client to server): take off what is worn in a slot.</summary>
        public const string Unequip = "unequip";

        /// <summary><c>drop_item</c> (client to server): put some of an inventory entry on the ground.</summary>
        public const string DropItem = "drop_item";

        /// <summary><c>insert_card</c> (client to server): socket a card from the inventory into an equipment entry, for good.</summary>
        public const string InsertCard = "insert_card";

        /// <summary><c>refine</c> (client to server): try to raise an equipment entry's refine level by one, using its material.</summary>
        public const string Refine = "refine";

        /// <summary><c>use_skill</c> (T-502).</summary>
        public const string UseSkill = "use_skill";

        /// <summary><c>learn_skill</c> (T-502).</summary>
        public const string LearnSkill = "learn_skill";

        /// <summary><c>change_job</c> (T-502).</summary>
        public const string ChangeJob = "change_job";

        /// <summary><c>raise_stat</c> (T-503, client to server): spend stat points on a primary stat.</summary>
        public const string RaiseStat = "raise_stat";

        /// <summary><c>talk</c> (T-601, client to server): walk up to an NPC in view and start its dialogue.</summary>
        public const string Talk = "talk";

        /// <summary><c>dialogue_choice</c> (T-601, client to server): pick a shown choice (or, at a node without choices, continue with index 0).</summary>
        public const string DialogueChoice = "dialogue_choice";

        /// <summary><c>dialogue_close</c> (T-601, client to server): end the dialogue and any service it opened.</summary>
        public const string DialogueClose = "dialogue_close";

        /// <summary><c>shop_buy</c> (T-603, client to server): buy units of an item from the open shop. The price is the server's, never the client's.</summary>
        public const string ShopBuy = "shop_buy";

        /// <summary><c>shop_sell</c> (T-603, client to server): sell units of a carried inventory entry to the open shop.</summary>
        public const string ShopSell = "shop_sell";

        /// <summary><c>shop</c> (T-603, server to client, owner only): the stock and prices of the shop that just opened.</summary>
        public const string Shop = "shop";

        /// <summary><c>abandon_quest</c> (client → server, T-602): drop an active quest; its progress is gone and nothing comes back.</summary>
        public const string AbandonQuest = "abandon_quest";

        /// <summary><c>quest_log</c> (server → client, owner only, T-602): every active quest with its step and counters, the completed quests and the NPC markers.</summary>
        public const string QuestLog = "quest_log";

        /// <summary><c>quest</c> (server → client, owner only, T-602): one quest changed (a "Quest changes" term).</summary>
        public const string Quest = "quest";

        /// <summary><c>dialogue</c> (T-601, server to client, owner only): the dialogue is at this node.</summary>
        public const string Dialogue = "dialogue";

        /// <summary><c>dialogue_ended</c> (T-601, server to client, owner only): the dialogue and its services ended.</summary>
        public const string DialogueEnded = "dialogue_ended";

        /// <summary><c>service_opened</c> (T-601, server to client, owner only): an NPC service opened for the character.</summary>
        public const string ServiceOpened = "service_opened";

        /// <summary><c>stats</c> (T-503, server to client, owner only): base stats, unspent stat points, each stat's next cost and the panel's derived stats.</summary>
        public const string Stats = "stats";

        /// <summary><c>skills</c> (T-502).</summary>
        public const string Skills = "skills";

        /// <summary><c>cooldown</c> (T-502).</summary>
        public const string Cooldown = "cooldown";

        /// <summary><c>welcome</c> (server → client): the client's entity, map, the server tick rate and a new resume token.</summary>
        public const string Welcome = "welcome";

        /// <summary><c>snapshot</c> (server → client): everything the client sees; replaces its view (also sent on a map change).</summary>
        public const string Snapshot = "snapshot";

        /// <summary><c>delta</c> (server → client): one tick's changes relative to the last snapshot or delta.</summary>
        public const string Delta = "delta";

        /// <summary><c>rejected</c> (server → client): an intent was refused.</summary>
        public const string Rejected = "rejected";

        /// <summary><c>attack_stopped</c> (server → client, owner only): the client's auto-attack ended.</summary>
        public const string AttackStopped = "attack_stopped";

        /// <summary><c>exp</c> (server → client, owner only): levels and EXP with the next requirements.</summary>
        public const string Exp = "exp";

        /// <summary><c>level_up</c> (server → client, owner only): a base or job level was reached.</summary>
        public const string LevelUp = "level_up";

        /// <summary><c>you_died</c> (server → client, owner only): the character died.</summary>
        public const string YouDied = "you_died";

        /// <summary><c>respawned</c> (server → client, owner only): the character came back after <c>respawn</c>.</summary>
        public const string Respawned = "respawned";

        /// <summary><c>inventory</c> (server to client, owner only): the whole inventory, after every snapshot.</summary>
        public const string Inventory = "inventory";

        /// <summary><c>inventory_changed</c> (server to client, owner only): one change to the inventory, with the next change counter.</summary>
        public const string InventoryChanged = "inventory_changed";

        /// <summary><c>refine_result</c> (server to client, owner only): how a <c>refine</c> came out.</summary>
        public const string RefineResult = "refine_result";

        /// <summary><c>start_autopilot</c> (T-604, client to server): play these quests, in this order, with these shortcut skills.</summary>
        public const string StartAutopilot = "start_autopilot";

        /// <summary><c>stop_autopilot</c> (T-604, client to server): switch the autopilot off.</summary>
        public const string StopAutopilot = "stop_autopilot";

        /// <summary><c>autopilot</c> (T-604, server to client, owner only): whether the autopilot runs, what it does, and why it paused or ended. On change only.</summary>
        public const string Autopilot = "autopilot";

        /// <summary><c>set_outfit</c> (T-708): choose what shows in an outfit slot, or turn the switch that hides outfits.</summary>
        public const string SetOutfit = "set_outfit";

        /// <summary><c>wardrobe</c> (T-708, owner only): the owned outfits and what is chosen.</summary>
        public const string Wardrobe = "wardrobe";

        /// <summary><c>pong</c> (server → client): answer to <c>ping</c>.</summary>
        public const string Pong = "pong";

        /// <summary><c>announcement</c> (server → client, T-806): staff's words, or a coming maintenance, for everyone playing on this server.</summary>
        public const string Announcement = "announcement";

        /// <summary><c>bye</c> (server → client, last message): why the server is closing the connection.</summary>
        public const string Bye = "bye";

        /// <summary>Types a client may send.</summary>
        public static readonly VocabularySet ClientToServer = new VocabularySet(Hello, Move, Stop, Attack, StopAttack, Respawn, Ping, Logout, PickUp, UseItem, Equip, Unequip, DropItem, InsertCard, Refine, UseSkill, LearnSkill, ChangeJob, RaiseStat, Talk, DialogueChoice, DialogueClose, AbandonQuest, ShopBuy, ShopSell, StartAutopilot, StopAutopilot, SetOutfit);

        /// <summary>Types the server sends.</summary>
        public static readonly VocabularySet ServerToClient = new VocabularySet(Welcome, Snapshot, Delta, Rejected, AttackStopped, Exp, LevelUp, YouDied, Respawned, Inventory, InventoryChanged, RefineResult, Skills, Stats, Cooldown, Dialogue, DialogueEnded, ServiceOpened, Shop, QuestLog, Quest, Autopilot, Wardrobe, Announcement, Pong, Bye);

        /// <summary>Every message type, client → server first.</summary>
        public static readonly VocabularySet All = VocabularySet.Union(ClientToServer, ServerToClient);
    }

    /// <summary>How a cast ended, in a delta's <c>cast_ends</c> (T-502).</summary>
    public static class CastResults
    {
        /// <summary><c>done</c>: the cast time ran out and the skill was requested.</summary>
        public const string Done = "done";

        /// <summary><c>cancelled</c>: the caster moved, gave a new order or stopped.</summary>
        public const string Cancelled = "cancelled";

        /// <summary><c>interrupted</c>: a stun, death, lost target or not enough SP ended it.</summary>
        public const string Interrupted = "interrupted";

        /// <summary>Every term in this category.</summary>
        public static readonly VocabularySet All = new VocabularySet(Done, Cancelled, Interrupted);
    }

    /// <summary>Reasons in a <c>bye</c> message (VOCABULARY.md "Bye reasons").</summary>
    public static class ByeReasons
    {
        /// <summary><c>timeout</c>: no <c>hello</c> in time, or no message at all for too long.</summary>
        public const string Timeout = "timeout";

        /// <summary><c>bad_version</c>: <c>hello</c> carried another protocol version.</summary>
        public const string BadVersion = "bad_version";

        /// <summary><c>malformed</c>: not a JSON object, a binary frame, or a known type with missing or wrong fields.</summary>
        public const string Malformed = "malformed";

        /// <summary><c>unknown_type</c>: a <c>type</c> the server does not accept from clients.</summary>
        public const string UnknownType = "unknown_type";

        /// <summary><c>oversize</c>: a message longer than the server's cap.</summary>
        public const string Oversize = "oversize";

        /// <summary><c>before_hello</c>: another message came before <c>hello</c>.</summary>
        public const string BeforeHello = "before_hello";

        /// <summary><c>duplicate_hello</c>: a second <c>hello</c> on a session that already joined.</summary>
        public const string DuplicateHello = "duplicate_hello";

        /// <summary><c>rate_limit</c>: the client kept sending faster than its rate limit allows.</summary>
        public const string RateLimit = "rate_limit";

        /// <summary><c>login_refused</c>: the login was refused (the dev login outside development, a bad name).</summary>
        public const string LoginRefused = "login_refused";

        /// <summary><c>server_full</c>: the server has as many sessions as it accepts.</summary>
        public const string ServerFull = "server_full";

        /// <summary><c>too_slow</c>: the client did not read what it was sent for too long.</summary>
        public const string TooSlow = "too_slow";

        /// <summary><c>shutdown</c>: the server is stopping.</summary>
        public const string Shutdown = "shutdown";

        /// <summary><c>update_required</c>: the client's content hash differs from the server's, or it sent none outside development (ARCHITECTURE §5.4).</summary>
        public const string UpdateRequired = "update_required";

        /// <summary><c>server_error</c>: the server failed to handle the session.</summary>
        public const string ServerError = "server_error";

        /// <summary><c>logout</c>: the client sent <c>logout</c>; its character left the world. Do not reconnect.</summary>
        public const string Logout = "logout";

        /// <summary><c>replaced</c>: another connection resumed this character (the same player on a new socket). Do not reconnect.</summary>
        public const string Replaced = "replaced";

        /// <summary><c>resume_failed</c>: the resume token is unknown, already used, or its character's grace period is over. Log in again.</summary>
        public const string ResumeFailed = "resume_failed";

        /// <summary><c>load_failed</c>: the character's saved state could not be read; nothing was changed. Log in again later (T-702).</summary>
        public const string LoadFailed = "load_failed";

        /// <summary><c>save_pending</c>: the character's last save has not landed yet, or another server holds it. Log in again in a moment (T-702).</summary>
        public const string SavePending = "save_pending";

        /// <summary><c>save_lost</c>: this server can no longer save the character (it is held elsewhere, or deleted), so its session ends. Log in again (T-702).</summary>
        public const string SaveLost = "save_lost";

        /// <summary><c>session_ended</c>: the account session this connection signed in with was ended (signed out, pushed out by a newer sign-in, or a password change); the character left the world. Sign in again (T-710).</summary>
        public const string SessionEnded = "session_ended";

        /// <summary><c>kicked</c>: staff ended this account's game through the admin API (T-804); every session of the account ended with it and the character left the world, saved. Sign in again.</summary>
        public const string Kicked = "kicked";

        /// <summary><c>banned</c>: staff banned the account (T-806); a timed ban says how long is left (<c>retry_after</c>, seconds). Sent at <c>hello</c> and to a game the ban ends. Do not reconnect.</summary>
        public const string Banned = "banned";

        /// <summary><c>maintenance</c>: the server is in maintenance (T-806): no new login now, or the game ended (saved) when maintenance began. Try again later.</summary>
        public const string Maintenance = "maintenance";

        /// <summary>Every bye reason.</summary>
        public static readonly VocabularySet All = new VocabularySet(
            Timeout, BadVersion, Malformed, UnknownType, Oversize, BeforeHello, DuplicateHello, RateLimit, LoginRefused, ServerFull, TooSlow,
            Shutdown, ServerError, UpdateRequired, Logout, Replaced, ResumeFailed, LoadFailed, SavePending, SaveLost, SessionEnded, Kicked, Banned, Maintenance);
    }

    /// <summary>What an <c>announcement</c> is (VOCABULARY.md "Announcement kinds", T-806).</summary>
    public static class AnnouncementKinds
    {
        /// <summary>The longest text staff may announce.</summary>
        public const int MaxTextLength = 80;

        /// <summary><c>staff</c>: staff's own words, in <c>text</c>.</summary>
        public const string Staff = "staff";

        /// <summary><c>maintenance</c>: the server goes into maintenance; every game ends (saved) in <c>in</c> seconds.</summary>
        public const string Maintenance = "maintenance";

        /// <summary>Every kind.</summary>
        public static readonly VocabularySet All = new VocabularySet(Staff, Maintenance);
    }

    /// <summary>Entity kinds in a view record's <c>kind</c> field (VOCABULARY.md "Entity kinds").</summary>
    public static class EntityKinds
    {
        /// <summary><c>player</c>.</summary>
        public const string Player = "player";

        /// <summary><c>monster</c>.</summary>
        public const string Monster = "monster";

        /// <summary><c>npc</c>.</summary>
        public const string Npc = "npc";

        /// <summary><c>item</c>: an item lying on the ground (a monster's drop); its <c>display_key</c> is the item id.</summary>
        public const string Item = "item";

        /// <summary>Every kind.</summary>
        public static readonly VocabularySet All = new VocabularySet(Player, Monster, Npc, Item);
    }
}
