using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Client.Core.Accounts;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core;

/// <summary>
/// The client's own words: the only place UI text is written (T-211, ADR-002 row 120: see ARCHITECTURE §7.4), so a later translation has one table to replace. Names of game things never
/// come from here; they come from the bundle by <c>display_key</c>.
/// </summary>
public static class UiText
{
    /// <summary>The login button.</summary>
    public const string Play = "Play";

    /// <summary>The logout button.</summary>
    public const string Logout = "Log out";

    /// <summary>The death screen's title.</summary>
    public const string YouDied = "You died";

    /// <summary>The respawn button.</summary>
    public const string Respawn = "Respawn";

    /// <summary>The button that leaves a stop for the login screen.</summary>
    public const string BackToLogin = "Back to login";

    /// <summary>The update button.</summary>
    public const string GoToStore = "Get the update";

    /// <summary>The screen when the packed bundle has another format.</summary>
    public static string BundleRefused(int version) =>
        "This build cannot read its game data (format " + version.ToString(CultureInfo.InvariantCulture) + "). Please update the app.";

    /// <summary>The screen when the packed bundle cannot be read at all.</summary>
    public const string BundleCorrupt = "The game data in this build is damaged. Please reinstall the app.";

    /// <summary>The screen when the server names a map the bundle does not have.</summary>
    public const string MapMissing = "This build does not know the map you are on. Please update the app.";

    /// <summary>The screen when a release build is pointed at a plain <c>ws://</c> server (ARCHITECTURE §10).</summary>
    public const string CleartextRefused = "This build only connects over a secure connection (wss://). The server address in this build is not secure. Please update the app.";

    /// <summary>The screen when the server address is not a <c>ws://</c> or <c>wss://</c> URL.</summary>
    public const string ServerAddressInvalid = "The server address in this build is not valid. Please update the app.";

    /// <summary>The refusal screen's text for a verdict that is not <see cref="TransportVerdict.Allowed"/>.</summary>
    public static string TransportRefused(TransportVerdict verdict) => verdict == TransportVerdict.CleartextRefused ? CleartextRefused : ServerAddressInvalid;

    /// <summary>The line for a connection notice.</summary>
    public static string Notice(ConnectionNotice notice) => Notice(notice, null);

    /// <summary>The line for a connection notice; <paramref name="seconds"/> is how long a ban had left (T-806; null: it has no end).</summary>
    public static string Notice(ConnectionNotice notice, long? seconds)
    {
        switch (notice)
        {
            case ConnectionNotice.Banned: return Banned(seconds);
            case ConnectionNotice.Maintenance: return "The server is closed for maintenance. Please try again later.";
            case ConnectionNotice.Reconnecting: return "Connection lost. Reconnecting...";
            case ConnectionNotice.SessionExpired: return "You were away too long. Please log in again.";
            case ConnectionNotice.ResumeFailed: return "Your session has ended. Please log in again.";
            case ConnectionNotice.ResumeRateLimited: return "Too many reconnect attempts. Please log in again.";
            case ConnectionNotice.LoginRefused: return "The server refused this login.";
            case ConnectionNotice.ServerFull: return "The server is full. Please try again soon.";
            case ConnectionNotice.LoadFailed: return "Your character could not be loaded. Nothing was lost. Please try again later.";
            case ConnectionNotice.SavePending: return "Your character is still being saved. Please try again in a moment.";
            case ConnectionNotice.SaveLost: return "The server lost touch with your character's save. Please log in again.";
            case ConnectionNotice.SessionEnded: return "You were signed out from another device, or your password was changed.";
            case ConnectionNotice.Kicked: return "A game master ended your session. Please sign in again.";
            case ConnectionNotice.ConnectionFailed: return "Could not reach the server.";
            case ConnectionNotice.UpdateRequired: return "A new version is required to play. Please update the app.";
            case ConnectionNotice.Replaced: return "You are playing on another connection.";
            case ConnectionNotice.LoggedOut: return "You have logged out.";
            case ConnectionNotice.ServerShutdown: return "The server is restarting. Trying again soon...";
            default: return string.Empty;
        }
    }

    /// <summary>The longest line <see cref="Notice(ConnectionNotice, long?)"/> can return, for the layout check (T-806: a ban's line is the longest).</summary>
    public static string LongestNotice
    {
        get
        {
            var longest = Banned(9_999L * 86_400);
            foreach (ConnectionNotice notice in Enum.GetValues(typeof(ConnectionNotice)))
                if (Notice(notice, null).Length > longest.Length) longest = Notice(notice, null);
            return longest;
        }
    }

    /// <summary>
    /// A ban's line (T-806): how long is left, or that it has no end, and where to turn. Staff's reason is never shown: it is written for staff and may name others.
    /// </summary>
    public static string Banned(long? seconds) => seconds is { } left && left > 0
        ? "This account is banned for " + BanTime(left) + ". If you think this is a mistake, contact support."
        : "This account is banned. If you think this is a mistake, contact support.";

    /// <summary>How long a ban has left, in its largest whole unit, rounded up: "3 days", "an hour", "12 minutes".</summary>
    public static string BanTime(long seconds)
    {
        if (seconds >= 86_400) return Plural((seconds + 86_399) / 86_400, "day");
        if (seconds >= 3_600) return Plural((seconds + 3_599) / 3_600, "hour");
        return Plural(Math.Max(1, (seconds + 59) / 60), "minute");
    }

    private static string Plural(long n, string unit) => n == 1 ? (unit == "hour" ? "an hour" : "a " + unit) : n.ToString(CultureInfo.InvariantCulture) + " " + unit + "s";

    /// <summary>An <c>announcement</c> on the HUD (T-806): staff's words as they wrote them, or when maintenance ends the games.</summary>
    public static string Announcement(string kind, string? text, long? inSeconds)
    {
        if (kind == AnnouncementKinds.Maintenance)
            return inSeconds is { } left && left > 0
                ? "The server closes for maintenance in " + BanTime(left) + ". Your character is saved."
                : "The server closes for maintenance now. Your character is saved.";
        return text ?? string.Empty;   // staff's words as they wrote them, on a line of their own
    }

    /// <summary>The longest announcement line, for the layout check: the most staff may write, in ordinary words (as the long names of the other samples are).</summary>
    public static string LongestAnnouncement
    {
        get
        {
            const string words = "The northern bridge closes for repairs at noon today; travellers should take the ferry from the harbour until the evening bell. ";
            var text = string.Concat(System.Linq.Enumerable.Repeat(words, 2)).Substring(0, AnnouncementKinds.MaxTextLength).TrimEnd();
            return Announcement(AnnouncementKinds.Staff, text, null);
        }
    }

    /// <summary>The flash line when staff gave or took items or currency (T-806).</summary>
    public const string StaffChangedItems = "A game master changed your items.";

    /// <summary>T-801: a development server reloaded content this build shows differently.</summary>
    public const string ContentChanged = "Content changed on the server. Restart the game to see it.";

    // ---- chat (T-901) ----

    /// <summary>The HUD button that opens the chat, and the panel's title.</summary>
    public const string Chat = "Chat";

    /// <summary>The chat button when lines came that were not read.</summary>
    public const string ChatUnread = "Chat •";

    /// <summary>The send button.</summary>
    public const string ChatSend = "Send";

    /// <summary>The text field's hint.</summary>
    public const string ChatHint = "Say something";

    /// <summary>The whisper target field's hint.</summary>
    public const string ChatWhisperHint = "Name";

    /// <summary>The line's action buttons.</summary>
    public const string ChatReport = "Report";

    /// <summary>Blocks the line's sender.</summary>
    public const string ChatBlock = "Block";

    /// <summary>Takes a block back.</summary>
    public const string ChatUnblock = "Unblock";

    /// <summary>Whispers back to the line's sender.</summary>
    public const string ChatReply = "Whisper";

    /// <summary>A whisper with no name to send it to.</summary>
    public const string ChatNoWhisperTarget = "Type the name of the one you whisper to.";

    /// <summary>A report was stored.</summary>
    public const string ChatReported = "Thank you. The line was reported to the game masters.";

    /// <summary>The channel switch's text: the channel the next line goes to.</summary>
    public static string ChatChannel(string channel) => channel switch
    {
        ChatChannels.Map => "Map",
        ChatChannels.Whisper => "Whisper",
        ChatChannels.Party => "Party",
        ChatChannels.Guild => "Guild",
        ChatChannels.System => "Server",
        _ => channel,
    };

    /// <summary>
    /// Who said a line, drawn apart from its text: "Ada", "Ada > Bo" (a whisper), "Server" for the server's lines. Only the server's name and channel go in it, never anything the sender
    /// typed.
    /// </summary>
    public static string ChatSpeaker(string channel, string name, string? to) => channel switch
    {
        ChatChannels.System => ChatChannel(ChatChannels.System),
        ChatChannels.Whisper => name + " > " + to,
        _ => name,
    };

    /// <summary>Why the server refused a chat line, block, unblock or report.</summary>
    public static string ChatRefused(string reason, long? retryAfterSeconds, string? to) => reason switch
    {
        ChatReasons.Muted => retryAfterSeconds is { } left ? "A game master muted you. You can talk again in " + BanTime(left) + "." : "A game master muted you.",
        ChatReasons.RateLimited => retryAfterSeconds is { } wait ? "You are talking too fast. Wait " + BanTimeSeconds(wait) + "." : "You are talking too fast.",
        ChatReasons.NotOnline => (to is { Length: > 0 } ? to : "That character") + " is not online.",
        ChatReasons.InvalidTarget => "You cannot do that to yourself.",
        ChatReasons.Empty => "There is nothing to send.",
        ChatReasons.TooLong => "That line is too long.",
        ChatReasons.InvalidChannel => "You cannot talk there.",
        ChatReasons.UnknownCharacter => "Nobody has that name.",
        ChatReasons.AlreadyBlocked => (to ?? "That character") + " is blocked already.",
        ChatReasons.NotBlocked => (to ?? "That character") + " is not blocked.",
        ChatReasons.BlockLimit => "You block as many characters as you can.",
        ChatReasons.UnknownLine => "That line can no longer be reported.",
        ChatReasons.AlreadyReported => "You reported that line already.",
        _ => "Chat is not available right now. Try again later.",   // unavailable, and anything newer
    };

    // ---- party (T-902) ----

    /// <summary>The chat panel's button that opens the party panel (the HUD has no room for one of its own).</summary>
    public const string Party = "Party";

    /// <summary>The party panel's heading while in no party.</summary>
    public const string PartyNone = "No party. Invite someone by name, or tap a player.";

    /// <summary>The party panel's name field.</summary>
    public const string PartyNameHint = "Name";

    /// <summary>The invite button beside the name field.</summary>
    public const string PartyInvite = "Invite";

    /// <summary>The button that invites the player tapped in the world.</summary>
    public const string PartyInviteTapped = "Invite tapped player";

    /// <summary>Leave the party.</summary>
    public const string PartyLeave = "Leave";

    /// <summary>The leader's button that takes a member out.</summary>
    public const string PartyKick = "Kick";

    /// <summary>The leader's button that hands the leader over.</summary>
    public const string PartyLead = "Lead";

    /// <summary>Accept an invite.</summary>
    public const string PartyAccept = "Join";

    /// <summary>Decline an invite.</summary>
    public const string PartyDecline = "No";

    /// <summary>The mark before the leader's name.</summary>
    public const string PartyLeaderMark = "★ ";

    /// <summary>The party panel's heading, e.g. <c>Party (3 / 5)</c>.</summary>
    public static string PartyTitle(int members, int max) =>
        "Party (" + members.ToString(CultureInfo.InvariantCulture) + (max > 0 ? " / " + max.ToString(CultureInfo.InvariantCulture) : string.Empty) + ")";

    /// <summary>The invite prompt, e.g. <c>Ada invites you to a party (25 s)</c>.</summary>
    public static string PartyInvited(string name, int secondsLeft) =>
        name + " invites you to a party (" + Math.Max(0, secondsLeft).ToString(CultureInfo.InvariantCulture) + " s)";

    /// <summary>Where a member is, under its name: its map (the bundle's name), dead, away, or not known yet.</summary>
    public static string PartyWhere(bool known, bool away, bool dead, string? mapName, bool sameMap) =>
        !known ? "..." : away ? "Away" : dead ? "Fallen" + (sameMap ? string.Empty : ", " + mapName) : sameMap ? "Here" : mapName ?? "Elsewhere";

    /// <summary>A member's line in the panel: the leader's mark, its name, and (you) for this character.</summary>
    public static string PartyMemberName(string name, bool leader, bool mine) => (leader ? PartyLeaderMark : string.Empty) + name + (mine ? " (you)" : string.Empty);

    /// <summary>A mark at the screen's edge for a member off the screen: an arrow toward it and its name.</summary>
    public static string PartyMark(string arrow, string name) => arrow + " " + name;

    /// <summary>The longest map name the layout check puts in the party panel.</summary>
    public const string LongestMapNameSample = "Extraordinarily Long Map Name of the North";

    /// <summary>The words of a "Party notices" term, e.g. <c>Bo joined the party.</c></summary>
    public static string PartyNotice(string notice, string? name) => notice switch
    {
        PartyNotices.InviteSent => "Invite sent to " + (name ?? "them") + ".",
        PartyNotices.Declined => (name ?? "They") + " said no.",
        PartyNotices.Expired => "The party invite ran out.",
        PartyNotices.Joined => (name ?? "Someone") + " joined the party.",
        PartyNotices.Left => (name ?? "Someone") + " left the party.",
        PartyNotices.Kicked => (name ?? "Someone") + " was taken out of the party.",
        PartyNotices.Leader => (name ?? "Someone") + " leads the party now.",
        PartyNotices.Disbanded => "The party has ended.",
        _ => "The party changed.",
    };

    /// <summary>The words of a "Party reasons" term (a <c>party_refused</c>).</summary>
    public static string PartyRefused(string reason, long? retryAfterSeconds, string? name) => reason switch
    {
        PartyReasons.NotOnline => (name is { Length: > 0 } ? name : "That character") + " is not online.",
        PartyReasons.InvalidTarget => "You cannot do that to yourself.",
        PartyReasons.Full => "The party is full.",
        PartyReasons.AlreadyInParty => (name is { Length: > 0 } ? name + " is" : "You are") + " in a party already.",
        PartyReasons.InvitePending => (name is { Length: > 0 } ? name : "That character") + " has an invite to answer first.",
        PartyReasons.NotLeader => "Only the party's leader can do that.",
        PartyReasons.NotInParty => "That is not in your party.",
        PartyReasons.NoInvite => "That invite is no longer there.",
        PartyReasons.RateLimited => retryAfterSeconds is { } wait ? "Too many invites. Wait " + BanTimeSeconds(wait) + "." : "Too many invites.",
        _ => "The party is not available right now.",
    };

    // ---- guilds (T-904) ----

    /// <summary>The chat panel's button that opens the guild panel, and the panel's heading when in none.</summary>
    public const string Guild = "Guild";

    /// <summary>The guild panel when in none.</summary>
    public const string GuildNone = "No guild. Found one with a name and an emblem, or join one when invited.";

    /// <summary>The name field's hint when founding.</summary>
    public const string GuildNameHint = "Guild name";

    /// <summary>Found, beside the name field.</summary>
    public const string GuildFound = "Found";

    /// <summary>The emblem picker's label.</summary>
    public const string GuildEmblem = "Emblem:";

    /// <summary>The invite field's hint.</summary>
    public const string GuildInviteHint = "Name";

    /// <summary>Invite, beside the name field.</summary>
    public const string GuildInvite = "Invite";

    /// <summary>Leave, in the heading.</summary>
    public const string GuildLeave = "Leave";

    /// <summary>A member row's Kick.</summary>
    public const string GuildKick = "Kick";

    /// <summary>A member row's Up (one rank higher, still below one's own).</summary>
    public const string GuildPromote = "Up";

    /// <summary>A member row's Down (one rank lower).</summary>
    public const string GuildDemote = "Down";

    /// <summary>A member row's Lead (the leader hands over).</summary>
    public const string GuildLead = "Lead";

    /// <summary>The notice field's hint.</summary>
    public const string GuildNoticeHint = "Notice";

    /// <summary>Save beside the notice field.</summary>
    public const string GuildNoticeSave = "Save";

    /// <summary>The invite prompt's Join.</summary>
    public const string GuildAccept = "Join";

    /// <summary>The invite prompt's No.</summary>
    public const string GuildDecline = "No";

    /// <summary>The guild panel's heading, e.g. <c>Iron Oaks (12 / 30)</c>.</summary>
    public static string GuildTitle(string name, int members, int online) =>
        name + " (" + online.ToString(CultureInfo.InvariantCulture) + " / " + members.ToString(CultureInfo.InvariantCulture) + " online)";

    /// <summary>One member's line: name, rank, level and whether it is online (a dot), e.g. <c>● Ada · Officer · Lv 12</c>.</summary>
    public static string GuildMemberLine(string name, string rank, int level, bool online, bool mine) =>
        (online ? "● " : "○ ") + name + (mine ? " (you)" : string.Empty) + " · " + rank + " · Lv " + level.ToString(CultureInfo.InvariantCulture);

    /// <summary>The invite prompt, e.g. <c>Ada invites you to Iron Oaks (28 s)</c>.</summary>
    public static string GuildInvited(string name, string title, int secondsLeft) =>
        name + " invites you to " + title + " (" + Math.Max(0, secondsLeft).ToString(CultureInfo.InvariantCulture) + " s)";

    /// <summary>The notice shown once when it is new (at sign-in, and when it changed).</summary>
    public static string GuildNoticeShown(string guild, string notice) => guild + ": " + notice;

    /// <summary>The longest guild name the layout check shows.</summary>
    public static readonly string LongestGuildNameSample = new string('W', 20);

    // ---- the rank editor (T-810) ----

    /// <summary>The guild panel's button that opens the rank editor (the leader's), and the editor's heading.</summary>
    public const string GuildRanks = "Ranks";

    /// <summary>The editor's button for a new rank.</summary>
    public const string GuildRankAdd = "Add";

    /// <summary>A rank row's button that takes the rank out of the list.</summary>
    public const string GuildRankRemove = "Remove";

    /// <summary>The editor's Save.</summary>
    public const string GuildRanksSave = "Save";

    /// <summary>A rank's name field, empty.</summary>
    public const string GuildRankNameHint = "Rank name";

    /// <summary>The name a new rank starts with.</summary>
    public const string GuildRankNew = "New rank";

    /// <summary>Under Save while the server has not answered.</summary>
    public const string GuildRanksSaving = "Saving...";

    /// <summary>The server took the ranks.</summary>
    public const string GuildRanksSaved = "Ranks saved.";

    /// <summary>What the server does with members when ranks move or go.</summary>
    public const string GuildRanksHint =
        "Members stay at their place in this list. If you move or remove a rank, the members at that place get the rank that is there then. Members past the end go to the last rank, where new members join too. Nothing changes until you save.";

    /// <summary>Under a rank's name: who holds that place once saved.</summary>
    public static string GuildRankMembers(int place, int count) =>
        "Place " + place.ToString(CultureInfo.InvariantCulture) + ": " + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " member" : " members");

    /// <summary>The box's words for a "Guild permissions" term.</summary>
    public static string GuildPermission(string permission) => permission switch
    {
        Game.Shared.Vocabulary.GuildPermissions.Invite => "Invite",
        Game.Shared.Vocabulary.GuildPermissions.Kick => "Kick",
        Game.Shared.Vocabulary.GuildPermissions.EditNotice => "Write notice",
        Game.Shared.Vocabulary.GuildPermissions.SetRank => "Set ranks",
        Game.Shared.Vocabulary.GuildPermissions.StorageDeposit => "Put in storage",
        Game.Shared.Vocabulary.GuildPermissions.StorageItems => "Take items",
        Game.Shared.Vocabulary.GuildPermissions.StorageCurrency => "Take money",
        Game.Shared.Vocabulary.GuildPermissions.StorageLog => "Read storage log",
        _ => permission,
    };

    // ---- guild storage (T-905) ----

    /// <summary>The guild panel's button that opens the storage, and the storage window's tab of its entries.</summary>
    public const string GuildStorage = "Storage";

    /// <summary>The storage window's tab of the bag (what can be put in).</summary>
    public const string GuildStorageBag = "Bag";

    /// <summary>The storage window's tab of the log.</summary>
    public const string GuildStorageLog = "Log";

    /// <summary>Take an entry (or coins) out.</summary>
    public const string GuildStorageTake = "Take";

    /// <summary>Put an entry (or coins) in.</summary>
    public const string GuildStoragePut = "Put";

    /// <summary>The amount field for coins.</summary>
    public const string GuildStorageAmountHint = "Amount";

    /// <summary>An empty list.</summary>
    public const string GuildStorageEmpty = "Nothing here.";

    /// <summary>The window's heading, e.g. <c>Storage 12/50 · Crowns: 1200</c>.</summary>
    public static string GuildStorageTitle(int entries, int size, string currencyName, long currency) =>
        "Storage " + entries.ToString(CultureInfo.InvariantCulture) + "/" + size.ToString(CultureInfo.InvariantCulture) + " · " + Currency(currencyName, currency);

    /// <summary>One log line, e.g. <c>11-28 09:14 Ada put Healing Draught x5</c> or <c>11-28 09:15 (gone) took 300 crowns</c>. The time is the device's local time.</summary>
    public static string GuildStorageLogLine(long at, string name, string move, string? itemName, int quantity, string currencyName, long currency)
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        var who = name.Length > 0 ? name : "(gone)";
        var verb = move == Game.Shared.Vocabulary.GuildStorageMoves.Deposit ? " put " : " took ";
        var what = itemName is not null ? itemName + (quantity > 1 ? " " + QuantityLabel(quantity) : string.Empty)
            : currency.ToString(CultureInfo.InvariantCulture) + " " + (currencyName.Length > 0 ? currencyName : "money");
        return when + " " + who + verb + what;
    }

    /// <summary>The longest log line the window shows (the layout check).</summary>
    public static string LongestGuildStorageLogLine => GuildStorageLogLine(0, new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed), Game.Shared.Vocabulary.GuildStorageMoves.Withdraw,
        new string('W', 24), 99, string.Empty, 0);

    /// <summary>The words of a "Guild news" term, e.g. <c>Bo joined the guild.</c></summary>
    public static string GuildNews(string news, string? name) => news switch
    {
        Game.Shared.Vocabulary.GuildNews.InviteSent => "Guild invite sent to " + (name ?? "them") + ".",
        Game.Shared.Vocabulary.GuildNews.Declined => (name ?? "They") + " said no.",
        Game.Shared.Vocabulary.GuildNews.Expired => "The guild invite ran out.",
        Game.Shared.Vocabulary.GuildNews.Founded => (name ?? "The guild") + " is founded.",
        Game.Shared.Vocabulary.GuildNews.Joined => (name ?? "Someone") + " joined the guild.",
        Game.Shared.Vocabulary.GuildNews.Left => (name ?? "Someone") + " left the guild.",
        Game.Shared.Vocabulary.GuildNews.Kicked => (name ?? "Someone") + " was taken out of the guild.",
        Game.Shared.Vocabulary.GuildNews.Rank => (name ?? "Someone") + " has a new rank.",
        Game.Shared.Vocabulary.GuildNews.Leader => (name ?? "Someone") + " leads the guild now.",
        Game.Shared.Vocabulary.GuildNews.Ranks => "The guild's ranks changed.",
        Game.Shared.Vocabulary.GuildNews.Notice => "The guild notice changed.",
        Game.Shared.Vocabulary.GuildNews.Disbanded => "The guild has ended.",
        _ => "Something happened in the guild.",
    };

    /// <summary>The words of a "Guild reasons" term (a <c>guild_refused</c>).</summary>
    public static string GuildRefused(string reason, long? retryAfterSeconds, string? name) => reason switch
    {
        GuildReasons.NotOnline => (name is { Length: > 0 } ? name : "That character") + " is not online.",
        GuildReasons.InvalidTarget => "You cannot do that to them.",
        GuildReasons.InvalidName => "That guild name is not allowed.",
        GuildReasons.NameTaken => "A guild has that name already.",
        GuildReasons.InvalidEmblem => "Pick one of the emblems.",
        GuildReasons.AlreadyInGuild => (name is { Length: > 0 } ? name + " is" : "You are") + " in a guild already.",
        GuildReasons.NotInGuild => "You are not in a guild.",
        GuildReasons.InvitePending => (name is { Length: > 0 } ? name : "That character") + " has an invite to answer first.",
        GuildReasons.NoInvite => "That invite is no longer there.",
        GuildReasons.Full => "The guild is full.",
        GuildReasons.NotAllowed => "Your rank cannot do that.",
        GuildReasons.RankTooHigh => "Their rank is not below yours.",
        GuildReasons.InvalidRanks => "Those ranks are not allowed. Each needs a short name, and a guild has only so many ranks.",
        GuildReasons.TooLong => "The notice is too long.",
        GuildReasons.CannotAfford => "You cannot pay for a guild.",
        GuildReasons.Busy => "Your guild is being founded.",
        GuildReasons.RateLimited => retryAfterSeconds is { } wait ? "Too fast. Wait " + BanTimeSeconds(wait) + "." : "Too fast.",
        GuildReasons.ItemGone => "That is not there any more.",
        GuildReasons.ItemEquipped => "Take it off first.",
        GuildReasons.NotTradeable => "That cannot go into the storage.",
        GuildReasons.StorageFull => "The guild storage is full.",
        GuildReasons.InventoryFull => "Your bag is full.",
        GuildReasons.TooHeavy => "That is too heavy to carry.",
        GuildReasons.CurrencyFull => "You cannot carry that much money.",
        GuildReasons.StorageNotEmpty => "Empty the guild storage first.",
        _ => "Guilds are not available right now.",
    };

    // ---- trade (T-903) ----

    /// <summary>The trade window's heading, e.g. <c>Trade with Bo</c>.</summary>
    public static string TradeTitle(string name) => "Trade with " + name;

    /// <summary>The Trade button beside a tapped player or a party member.</summary>
    public const string TradeAskButton = "Trade";

    /// <summary>The ask prompt's accept.</summary>
    public const string TradeAccept = "Trade";

    /// <summary>The ask prompt's decline.</summary>
    public const string TradeDecline = "No";

    /// <summary>The ask prompt, e.g. <c>Ada wants to trade (18 s)</c>.</summary>
    public static string TradeAsked(string name, int secondsLeft) =>
        name + " wants to trade (" + Math.Max(0, secondsLeft).ToString(CultureInfo.InvariantCulture) + " s)";

    /// <summary>This side's column heading.</summary>
    public const string TradeYouOffer = "You give";

    /// <summary>The other side's column heading, e.g. <c>Bo gives</c>.</summary>
    public static string TradeTheyOffer(string name) => name + " gives";

    /// <summary>An offer with nothing in it.</summary>
    public const string TradeNothing = "Nothing yet.";

    /// <summary>Opens the bag's list of what can be offered.</summary>
    public const string TradeAdd = "Add";

    /// <summary>Back from the list of what can be offered.</summary>
    public const string TradeBack = "Back";

    /// <summary>The list of what can be offered, when nothing can.</summary>
    public const string TradeNothingToOffer = "Nothing you carry can be traded.";

    /// <summary>One unit fewer in the quantity picker.</summary>
    public const string TradeLess = "-";

    /// <summary>One unit more in the quantity picker.</summary>
    public const string TradeMore = "+";

    /// <summary>Puts the chosen units in the offer.</summary>
    public const string TradeOfferButton = "Offer";

    /// <summary>The currency field's hint.</summary>
    public const string TradeCurrencyHint = "Amount";

    /// <summary>Puts the typed amount in the offer.</summary>
    public const string TradeSetCurrency = "Set";

    /// <summary>Lock.</summary>
    public const string TradeLock = "Lock";

    /// <summary>Unlock.</summary>
    public const string TradeUnlock = "Unlock";

    /// <summary>Confirm.</summary>
    public const string TradeConfirm = "Confirm";

    /// <summary>Cancel.</summary>
    public const string TradeCancel = "Cancel";

    /// <summary>One offered entry, e.g. <c>Iron Blade +4 [1]</c> or <c>Healing Draught x3</c>.</summary>
    public static string TradeLine(string itemName, int quantity, int refine, int cards) =>
        itemName + (refine > 0 ? " +" + refine.ToString(CultureInfo.InvariantCulture) : string.Empty) + (cards > 0 ? " [" + cards.ToString(CultureInfo.InvariantCulture) + "]" : string.Empty)
        + (quantity > 1 ? " " + QuantityLabel(quantity) : string.Empty);

    /// <summary>The quantity picker, e.g. <c>2 / 5</c>.</summary>
    public static string TradeQuantity(int chosen, int carried) => chosen.ToString(CultureInfo.InvariantCulture) + " / " + carried.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The state line under the offers: who has locked and confirmed, and what to do next. Locked offers are final; the confirm is for the two offers shown.
    /// </summary>
    public static string TradeState(string name, bool mineLocked, bool theirsLocked, bool mineConfirmed, bool theirsConfirmed)
    {
        if (mineConfirmed && theirsConfirmed) return "Both confirmed. Trading...";
        if (mineConfirmed) return "You confirmed these offers. Waiting for " + name + ".";
        if (theirsConfirmed) return name + " confirmed these offers. Check them, then confirm.";
        if (mineLocked && theirsLocked) return "Both locked. Check both offers, then confirm.";
        if (mineLocked) return "You locked. Waiting for " + name + " to lock.";
        if (theirsLocked) return name + " locked. Lock yours when it is ready.";
        return "Any change unlocks both offers.";
    }

    /// <summary>The words of a "Trade notices" term.</summary>
    public static string TradeNotice(string notice, string? name) => notice switch
    {
        TradeNotices.AskSent => "Trade asked of " + (name is { Length: > 0 } ? name : "them") + ".",
        TradeNotices.Declined => (name is { Length: > 0 } ? name : "They") + " said no to trading.",
        TradeNotices.Expired => "The trade request ran out.",
        _ => "Trade news.",
    };

    /// <summary>The words of a "Trade ends" term (a <c>trade_closed</c>); <paramref name="own"/> when this side caused it.</summary>
    public static string TradeClosed(string reason, string? name, bool own)
    {
        var who = name is { Length: > 0 } ? name : "The other player";
        return reason switch
        {
            TradeEnds.Done => "Trade done.",
            TradeEnds.Cancelled => own ? "You cancelled the trade." : who + " cancelled the trade.",
            TradeEnds.TooFar => "Trade cancelled: you moved too far apart.",
            TradeEnds.Dead => "Trade cancelled: " + (own ? "you were" : who + " was") + " knocked out.",
            TradeEnds.LeftMap => "Trade cancelled: " + (own ? "you" : who) + " left the area.",
            TradeEnds.Disconnected => "Trade cancelled: " + (own ? "your" : who + "'s") + " connection dropped.",
            TradeEnds.Left => "Trade cancelled: " + (own ? "you" : who) + " left the game.",
            TradeEnds.TooHeavy => "Trade cancelled: " + (own ? "you" : who) + " could not carry that much. Nothing moved.",
            TradeEnds.InventoryFull => "Trade cancelled: " + (own ? "your" : who + "'s") + " bag is full. Nothing moved.",
            TradeEnds.CurrencyFull => "Trade cancelled: " + (own ? "you" : who) + " cannot hold that much money. Nothing moved.",
            TradeEnds.OfferChanged => "Trade cancelled: " + (own ? "your" : who + "'s") + " offer changed. Nothing moved.",
            _ => "Trade cancelled. Nothing moved.",
        };
    }

    /// <summary>The words of a "Trade reasons" term (a <c>trade_refused</c>).</summary>
    public static string TradeRefused(string reason, long? retryAfterSeconds) => reason switch
    {
        TradeReasons.NotOnline => "That player is not online here.",
        TradeReasons.InvalidTarget => "You cannot trade with that.",
        TradeReasons.Busy => "That player is busy with another trade.",
        TradeReasons.TooFar => "Stand closer to trade.",
        TradeReasons.Dead => "Nobody can trade while knocked out.",
        TradeReasons.AskPending => "Wait for an answer to your last request.",
        TradeReasons.NoAsk => "That trade request is no longer there.",
        TradeReasons.RateLimited => retryAfterSeconds is { } wait ? "Too many trade requests. Wait " + BanTimeSeconds(wait) + "." : "Too many trade requests.",
        TradeReasons.NotTrading => "You are not trading.",
        TradeReasons.NotTradeable => "That item cannot be traded.",
        TradeReasons.ItemGone => "You no longer have that.",
        TradeReasons.ItemEquipped => "Take it off first.",
        TradeReasons.InvalidQuantity => "You do not have that many.",
        TradeReasons.OfferFull => "Your offer is full.",
        TradeReasons.NotLocked => "Both offers must be locked first.",
        TradeReasons.Stale => "The offers changed. Check them again.",
        TradeReasons.Swapping => "The trade is going through.",
        TradeReasons.CannotTrade => "You cannot trade with that player.",
        _ => "Trading is not available right now.",
    };

    /// <summary>The longest item line the trade window may show, for the layout check.</summary>
    public static string LongestTradeLine => TradeLine(new string('W', 24), 99, 10, 4);

    private static string BanTimeSeconds(long seconds) => seconds < 60 ? (seconds == 1 ? "a second" : seconds.ToString(CultureInfo.InvariantCulture) + " seconds") : BanTime(seconds);

    /// <summary>The longest speaker the log may show, for the layout check: a whisper between two names of the longest length.</summary>
    public static string LongestChatSpeaker => ChatSpeaker(ChatChannels.Whisper, new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed), new string('W', Game.Shared.Accounts.CharacterNameRules.LongestAllowed));

    // ---- sign-in and character select (T-701) ----

    /// <summary>The sign-in screen's title.</summary>
    public const string SignInTitle = "Sign in";

    /// <summary>The username field's hint.</summary>
    public const string Username = "Username";

    /// <summary>The password field's hint.</summary>
    public const string Password = "Password (at least 10 characters)";

    /// <summary>The sign-in button.</summary>
    public const string SignIn = "Sign in";

    /// <summary>The register button.</summary>
    public const string Register = "New account";

    /// <summary>The development login's button (debug builds only).</summary>
    public const string DevLogin = "Dev login";

    /// <summary>The character screen's title.</summary>
    public const string CharactersTitle = "Your characters";

    /// <summary>The button that creates a character.</summary>
    public const string CreateCharacter = "Create character";

    /// <summary>The button that opens the creation screen (T-706).</summary>
    public const string NewCharacter = "New character";

    /// <summary>The creation screen's random button.</summary>
    public const string RandomLook = "Random";

    /// <summary>The creation screen's way back to the character list.</summary>
    public const string Back = "Back";

    /// <summary>The title over the genders.</summary>
    public const string CreationGender = "Body";

    /// <summary>The title over the faces.</summary>
    public const string CreationFace = "Face";

    /// <summary>The title over the hairstyles.</summary>
    public const string CreationHairstyle = "Hairstyle";

    /// <summary>The title over the skin tones.</summary>
    public const string CreationSkin = "Skin tone";

    /// <summary>The title over the hair colours.</summary>
    public const string CreationHairColor = "Hair colour";

    /// <summary>The chosen option's button: marked in words as well as by its frame.</summary>
    public static string Chosen(string name) => "[ " + name + " ]";

    /// <summary>The button that deletes a character.</summary>
    public const string DeleteCharacter = "Delete";

    /// <summary>The same button, once pressed.</summary>
    public const string ConfirmDelete = "Really?";

    /// <summary>The button that leaves the account.</summary>
    public const string SignOut = "Sign out";

    /// <summary>The button that opens the password screen (T-710).</summary>
    public const string ChangePassword = "Change password";

    /// <summary>The password screen's title.</summary>
    public const string PasswordTitle = "Change password";

    /// <summary>The hint of the current password's field.</summary>
    public const string CurrentPassword = "Current password";

    /// <summary>The hint of the new password's field.</summary>
    public const string NewPassword = "New password (at least 10 characters)";

    /// <summary>The hint of the repeat's field.</summary>
    public const string RepeatPassword = "New password again";

    /// <summary>What a password change does besides.</summary>
    public const string PasswordHelp = "This signs out every other device and ends a game running there.";

    /// <summary>The refusal of a password change whose current password is wrong.</summary>
    public const string WrongCurrentPassword = "The current password is wrong.";

    /// <summary>The button that opens the deletion screen, and its title (T-703).</summary>
    public const string DeleteAccount = "Delete account";

    /// <summary>What deleting the account means.</summary>
    public const string DeleteAccountHelp = "This deletes your account and all your characters, with everything they own and have done. You are signed out on every device. The server says how long you have to change your mind; after that nothing can be brought back.";

    /// <summary>The hint of the password's field on the deletion screen.</summary>
    public const string DeleteAccountPassword = "Your password";

    /// <summary>The deletion's button, once pressed.</summary>
    public const string ConfirmDeleteAccount = "Really delete everything?";

    /// <summary>The sign-in screen's button that takes a deletion back.</summary>
    public const string KeepAccount = "Keep my account";

    /// <summary>How long until a deleted account is erased.</summary>
    public static string EraseTime(long seconds)
    {
        if (seconds <= 0) return "soon";
        var days = (seconds + 86_399) / 86_400;
        var hours = (seconds + 3_599) / 3_600;
        return seconds > 86_400 ? "in " + days.ToString(CultureInfo.InvariantCulture) + " days" : hours == 1 ? "in an hour" : "in " + hours.ToString(CultureInfo.InvariantCulture) + " hours";
    }

    /// <summary>The line while the server is asked.</summary>
    public const string AccountBusy = "One moment...";

    /// <summary>A character's line: its name and its job.</summary>
    public static string CharacterRow(string name, string job) => name + "\n" + job;

    /// <summary>The hint of the new character's name field.</summary>
    public static string NewCharacterName(int min, int max) =>
        "Name (" + min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture) + " letters)";

    /// <summary>The line for an account notice: for a refusal, the sentence of its "Account errors" term.</summary>
    public static string Account(AccountNotice notice, string error, long retryAfterSeconds)
    {
        switch (notice)
        {
            case AccountNotice.Unreachable: return "Could not reach the server.";
            case AccountNotice.SessionEnded: return "Your session has ended. Please sign in again.";
            case AccountNotice.PasswordsDiffer: return "The two new passwords are not the same.";
            case AccountNotice.PasswordChanged: return "Your password is changed.";
            case AccountNotice.AccountDeleted: return "Your account will be erased " + EraseTime(retryAfterSeconds) + ". Until then you can keep it.";
            case AccountNotice.Refused: break;
            default: return string.Empty;
        }
        switch (error)
        {
            case AccountErrors.InvalidUsername: return "A username has 3 to 24 letters, digits, dots, dashes or underscores, and starts with a letter or digit.";
            case AccountErrors.InvalidPassword: return "A password has 10 to 128 characters.";
            case AccountErrors.SamePassword: return "The new password is the same as the old one.";
            case AccountErrors.AccountDeleting: return "This account will be erased " + EraseTime(retryAfterSeconds) + ". You can still keep it.";
            case AccountErrors.AccountBanned: return Banned(retryAfterSeconds);   // T-806
            case AccountErrors.UsernameTaken: return "That username is taken.";
            case AccountErrors.InvalidSession: return Account(AccountNotice.SessionEnded, error, 0);
            case AccountErrors.InvalidCredentials: return "Wrong username or password.";
            case AccountErrors.RateLimited:
                return retryAfterSeconds > 0
                    ? "Too many tries. Please wait " + retryAfterSeconds.ToString(CultureInfo.InvariantCulture) + " seconds."
                    : "Too many tries. Please wait a moment.";
            case AccountErrors.InvalidName: return "That name cannot be used. Use letters or digits, with a single - ' or _ between them.";
            case AccountErrors.InvalidAppearance: return "That look is not available. Pick another.";
            case AccountErrors.NameTaken: return "That name is taken.";
            case AccountErrors.NoFreeSlot: return "You have no free character slot.";
            case AccountErrors.UnknownCharacter: return "That character no longer exists.";
            case AccountErrors.CharacterInUse: return "That character is still in the world. Please try again in a minute.";
            case AccountErrors.GuildStorageNotEmpty: return "That character is the last of its guild. Empty the guild storage first.";
            case AccountErrors.Unavailable: return "The server is busy. Please try again.";
            default: return "The server could not use that. Please try again.";
        }
    }

    /// <summary>The longest line <see cref="Account"/> can return, for the layout check.</summary>
    public static string LongestAccount
    {
        get
        {
            var a = Account(AccountNotice.Refused, AccountErrors.InvalidUsername, 0);
            var b = Account(AccountNotice.Refused, AccountErrors.AccountBanned, 9_999L * 86_400);   // T-806
            return a.Length >= b.Length ? a : b;
        }
    }

    /// <summary>The line while a connection is being made, with no notice.</summary>
    public const string Connecting = "Connecting...";

    /// <summary>The word a floating number shows for a missed attack.</summary>
    public const string MissWord = "Miss";

    /// <summary>The flash when this client hits something.</summary>
    public static string YouHit(string target, int amount, string result)
    {
        if (result == HitResults.Miss) return "You miss " + target;
        var verb = result == HitResults.Crit ? "You crit " : "You hit ";
        return verb + target + " for " + amount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The flash when something hits this client.</summary>
    public static string HitYou(string attacker, int amount, string result)
    {
        if (result == HitResults.Miss) return attacker + " misses you";
        var verb = result == HitResults.Crit ? " crits you for " : " hits you for ";
        return attacker + verb + amount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The flash when a drop this client may take appears.</summary>
    public static string Dropped(string item, int quantity) =>
        quantity > 1 ? "Dropped: " + item + " x" + quantity.ToString(CultureInfo.InvariantCulture) : "Dropped: " + item;

    /// <summary>
    /// The flash for a move or attack a status refused (T-501: a "Movement reasons" <c>cannot_move</c> or "Attack reasons" <c>cannot_attack</c> term);
    /// empty for every other reason, which needs no word.
    /// </summary>
    public static string Held(string reason) => reason switch
    {
        "cannot_move" => "You cannot move right now.",
        AttackReasons.CannotAttack => "You cannot attack right now.",
        _ => string.Empty,
    };

    /// <summary>
    /// Under a status icon in the HUD (T-501): its stacks when above 1 and the whole seconds left, e.g. <c>"x2 7s"</c>; a status with no time
    /// shown yet reads just its stacks.
    /// </summary>
    public static string StatusLeft(int stacks, int seconds) =>
        (stacks > 1 ? "x" + stacks.ToString(CultureInfo.InvariantCulture) + " " : string.Empty) + seconds.ToString(CultureInfo.InvariantCulture) + "s";

    /// <summary>The letter drawn for a status whose icon the theme does not have (§7.7 fallback): the first letter of its name.</summary>
    public static string StatusLetter(string name) => name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";

    /// <summary>The flash when something went into the bag.</summary>
    public static string PickedUp(string item) => "Picked up: " + item;

    /// <summary>The flash for a refused item intent (an "Item reasons" term); empty for one that needs no word.</summary>
    public static string ItemRefused(string reason) => reason switch
    {
        ItemReasons.ItemGone => "That item is gone.",
        ItemReasons.NotYours => "Someone else may take that first.",
        ItemReasons.Unreachable => "You cannot get there.",
        ItemReasons.TooHeavy => "Too heavy to carry.",
        ItemReasons.InventoryFull => "Your bag is full.",
        ItemReasons.NotUsable => "That cannot be used.",
        ItemReasons.Cooldown => "Not yet.",
        ItemReasons.NotEquipment => "That cannot be worn.",
        ItemReasons.WrongSlot => "That does not go there.",
        ItemReasons.LevelTooLow => "Your level is too low for that.",
        ItemReasons.WrongJob => "Your job cannot use that.",
        ItemReasons.NotEquipped => "Nothing is worn there.",
        ItemReasons.ItemEquipped => "Take it off first.",
        ItemReasons.InvalidQuantity => "You do not have that many.",
        ItemReasons.NotACard => "That is not a card.",
        ItemReasons.CardDoesNotFit => "That card does not fit there.",
        ItemReasons.NoFreeSocket => "No free card slot.",
        ItemReasons.NotRefinable => "That cannot be refined.",
        ItemReasons.AtMaxRefine => "It cannot be refined any further.",
        ItemReasons.MissingMaterial => "You do not have enough material.",
        ItemReasons.ResetInCombat => "Not while you are fighting.",
        ItemReasons.ResetDead => "You cannot do that now.",
        ItemReasons.NothingToReset => "There is nothing to take back.",
        ItemReasons.ServiceRequired => "Find a smith to refine your gear.",
        ItemReasons.OutOfRange => "Step closer to the shop.",
        ItemReasons.NotInStock => "The shop does not sell that.",
        ItemReasons.QuantityLimit => "That is more than the shop will sell at once.",
        ItemReasons.CannotAfford => "You cannot afford that.",
        ItemReasons.NotSellable => "The shop will not buy that.",
        ItemReasons.CurrencyFull => "You cannot carry any more money.",
        ItemReasons.Dead => "You cannot trade now.",
        _ => string.Empty,   // cancelled: the player moved on, nothing to say
    };

    /// <summary>The flash for a card socketed into <paramref name="item"/>.</summary>
    public static string CardInserted(string item) => "Card set in " + item + ".";

    /// <summary>The flash for a refine's outcome (a "Refine results" term), with the name the item has now (+level).</summary>
    public static string RefineResult(string item, string result, int level) => result switch
    {
        RefineResults.Success => "Refined: " + Refined(item, level) + "!",
        RefineResults.Keep => "The refine failed. " + Refined(item, level) + " is unchanged.",
        RefineResults.Down => "The refine failed. " + Refined(item, level) + " lost a level.",
        RefineResults.Break => "The refine failed and " + Refined(item, level) + " broke.",
        _ => string.Empty,
    };

    /// <summary>An item's name with its refine level, e.g. <c>+3 Bone Dirk</c> (just the name at 0).</summary>
    public static string Refined(string item, int level) => level > 0 ? "+" + level.ToString(CultureInfo.InvariantCulture) + " " + item : item;

    /// <summary>An equipment entry's line: name with refine level, then its cards (filled and free sockets), e.g. <c>+2 Padded Vest [Gelcap Card]</c>.</summary>
    public static string EquipmentLine(string item, int level, IReadOnlyList<string> cards, int cardSlots)
    {
        var line = Refined(item, level);
        if (cardSlots <= 0 && cards.Count == 0) return line;
        var sockets = new List<string>(cards);
        for (var i = cards.Count; i < cardSlots; i++) sockets.Add(EmptySocket);
        return line + " [" + string.Join(", ", sockets) + "]";
    }

    /// <summary>An empty card socket.</summary>
    public const string EmptySocket = "free slot";

    /// <summary>Socket a card: the action button that lists the equipment it fits.</summary>
    public const string InsertCard = "Set card";

    /// <summary>Refine: the action button that shows the next step.</summary>
    public const string Refine = "Refine";

    /// <summary>The refine confirmation button.</summary>
    public const string ConfirmRefine = "Refine now";

    /// <summary>The skills button (T-502).</summary>
    public const string Skills = "Skills";

    /// <summary>The button in the skill panel that opens the stats panel (T-503).</summary>
    public const string Stats = "Stats";

    /// <summary>The stats panel's title.</summary>
    public const string StatsTitle = "Stats";

    /// <summary>The button in the stats panel that goes back to the skill panel.</summary>
    public const string BackToSkills = "Skills";

    /// <summary>The label over the derived stats.</summary>
    public const string DerivedTitle = "Derived";

    /// <summary>Stat points left to spend, e.g. <c>Stat points: 4</c>.</summary>
    public static string StatPoints(int points) => "Stat points: " + points.ToString(CultureInfo.InvariantCulture);

    /// <summary>A primary stat's row, e.g. <c>STR 12</c>.</summary>
    public static string StatLine(string stat, int value) => StatName(stat) + " " + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A stat's + button with the server's cost, e.g. <c>+ (2)</c>; just <c>max</c> when it cannot be raised.</summary>
    public static string RaiseButton(int cost) => cost > 0 ? "+ (" + cost.ToString(CultureInfo.InvariantCulture) + ")" : "max";

    /// <summary>One derived stat, e.g. <c>ATK 24</c>.</summary>
    public static string DerivedLine(string stat, int value) => StatName(stat) + " " + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The short name of a primary or derived stat id.</summary>
    public static string StatName(string stat) => stat switch
    {
        "str" => "STR", "agi" => "AGI", "vit" => "VIT", "int" => "INT", "dex" => "DEX", "luk" => "LUK",
        "atk" => "ATK", "matk" => "MATK", "def" => "DEF", "mdef" => "MDEF", "hit" => "HIT", "flee" => "FLEE", "crit" => "CRIT",
        "aspd" => "ASPD", "max_hp" => "Max HP", "max_sp" => "Max SP", _ => stat.ToUpperInvariant(),
    };

    /// <summary>The flash for a refused <c>raise_stat</c> (a "Stat reasons" term).</summary>
    public static string StatRefused(string reason) => reason switch
    {
        StatReasons.NotEnoughPoints => "Not enough stat points.",
        StatReasons.AtMaxValue => "That stat is at its highest.",
        StatReasons.Dead => "You cannot do that now.",
        _ => "That is not possible.",
    };

    /// <summary>The confirm button for using a reset item (T-503).</summary>
    public const string ConfirmUse = "Use it";

    /// <summary>What the bag says before a reset item is used, e.g. <c>Use Realign Tonic? This cannot be undone.</c></summary>
    public static string UseOffer(string item, string? description) =>
        "Use " + item + "?" + (description is { Length: > 0 } ? " " + description : string.Empty) + " Tap Use it to confirm.";

    /// <summary>The skill panel's title.</summary>
    public const string SkillsTitle = "Skills";

    /// <summary>The skill panel's hint when nothing is selected.</summary>
    public const string PickASkill = "Tap a skill to read it or learn it.";

    /// <summary>The learn button.</summary>
    public const string Learn = "Learn";

    /// <summary>Points left to spend, e.g. <c>Skill points: 3</c>.</summary>
    public static string SkillPoints(int points) => "Skill points: " + points.ToString(CultureInfo.InvariantCulture);

    /// <summary>The player's job line in the skill panel.</summary>
    public static string JobLine(string job) => "Job: " + job;

    /// <summary>A skill's level in the tree, e.g. <c>Lv 2/5</c>.</summary>
    public static string SkillLevel(int level, int max) => "Lv " + level.ToString(CultureInfo.InvariantCulture) + "/" + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>A requirement that is not met, e.g. <c>Needs Ember Bolt Lv 2</c>.</summary>
    public static string Needs(string skill, int level) => "Needs " + skill + " Lv " + level.ToString(CultureInfo.InvariantCulture);

    /// <summary>A passive skill's tag.</summary>
    public const string Passive = "Passive";

    /// <summary>The job-change button for a job the player can change to.</summary>
    public static string BecomeJob(string job) => "Become " + job;

    /// <summary>The skill bar's cooldown text, whole seconds left rounded up.</summary>
    public static string CooldownLeft(double seconds) => Math.Ceiling(seconds).ToString(CultureInfo.InvariantCulture) + "s";

    /// <summary>The flash when a skill needs a target (T-502): tap an enemy.</summary>
    public static string TapTarget(string skill) => skill + ": tap a target";

    /// <summary>The flash when a skill needs a cell: tap the ground.</summary>
    public static string TapGround(string skill) => skill + ": tap the ground";

    /// <summary>The flash for a refused skill intent (a "Skill reasons" term); empty for a reason that needs no word.</summary>
    public static string SkillRefused(string reason) => reason switch
    {
        SkillReasons.NotLearned => "You have not learned that.",
        SkillReasons.LevelTooHigh => "You have not learned that level.",
        SkillReasons.PassiveSkill => "That works by itself.",
        SkillReasons.NotEnoughSp => "Not enough SP.",
        SkillReasons.OnCooldown => "Not ready yet.",
        SkillReasons.WrongWeapon => "Your weapon cannot do that.",
        SkillReasons.OutOfRange or "no_line_of_sight" => "You cannot reach that.",
        SkillReasons.SafeZone => "Not here.",
        SkillReasons.InvalidTarget => "That is not a target for it.",
        SkillReasons.TargetGone or SkillReasons.TargetDead => "That target is gone.",
        SkillReasons.CannotUseSkills => "You cannot use skills right now.",
        SkillReasons.NotEnoughPoints => "No skill points left.",
        SkillReasons.NotInTree => "Your job cannot learn that.",
        SkillReasons.AtMaxLevel => "Already at its highest level.",
        SkillReasons.PrerequisiteNotMet => "Learn the skill before it first.",
        SkillReasons.BaseLevelTooLow => "Your level is too low for that job.",
        SkillReasons.JobLevelTooLow => "Your job level is too low for that job.",
        SkillReasons.QuestNotCompleted => "A quest for that job is not done yet.",
        SkillReasons.NotUnlocked => "That job is not open to you yet.",
        SkillReasons.WrongParentJob or SkillReasons.SameJob or SkillReasons.UnknownJob or SkillReasons.UnknownSkill => "That is not possible.",
        _ => string.Empty,
    };

    /// <summary>Cancels a pending choice (card target or refine).</summary>
    public const string Cancel = "Cancel";

    /// <summary>The hint above the equipment a selected card fits.</summary>
    public const string PickEquipmentForCard = "Set the card in (it stays there for good):";

    /// <summary>A selected card that fits nothing the player has.</summary>
    public const string NothingFitsCard = "Nothing you have can hold this card.";

    /// <summary>A card target button: the equipment's line and, while worn, its slot.</summary>
    public static string CardTarget(string equipmentLine, string? slot) => slot is null ? equipmentLine : equipmentLine + " (" + SlotName(slot) + ")";

    /// <summary>
    /// What refining offers, from the bundle's refine step for the next level: the chance, the material (with how many are carried) and what a failure does.
    /// Every number comes from the bundle; nothing here is a client constant.
    /// </summary>
    public static string RefineOffer(string item, int nextLevel, int successPercent, string material, int quantity, int carried, string failure)
    {
        var outcome = failure switch
        {
            RefineFailures.Down => "it loses a level",
            RefineFailures.Break => "it breaks and is gone, with its cards",
            _ => "it stays as it is",
        };
        return "Refine " + item + " to +" + nextLevel.ToString(CultureInfo.InvariantCulture) + ": " + successPercent.ToString(CultureInfo.InvariantCulture)
            + "% chance. Uses " + material + " x" + quantity.ToString(CultureInfo.InvariantCulture) + " (you have " + carried.ToString(CultureInfo.InvariantCulture)
            + "), success or not. If it fails, " + outcome + ".";
    }

    /// <summary>Refining is over for this item.</summary>
    public static string RefineMaxed(string item) => item + " cannot be refined any further.";

    /// <summary>The button that opens the inventory panel.</summary>
    public const string Bag = "Bag";

    /// <summary>The inventory panel's title.</summary>
    public const string BagTitle = "Bag and equipment";

    /// <summary>Uses a consumable.</summary>
    public const string Use = "Use";

    /// <summary>Puts an item on.</summary>
    public const string Equip = "Equip";

    /// <summary>Takes an item off.</summary>
    public const string Unequip = "Take off";

    /// <summary>Puts an item on the ground.</summary>
    public const string Drop = "Drop";

    /// <summary>The panel's hint while nothing is selected.</summary>
    public const string PickAnItem = "Tap an item to use, wear, refine or drop it.";

    /// <summary>An empty equipment slot.</summary>
    public const string EmptySlot = "(empty)";

    /// <summary>An inventory button: the item's name and, for a stack, the count.</summary>
    public static string ItemCount(string name, int quantity) =>
        quantity > 1 ? name + " x" + quantity.ToString(CultureInfo.InvariantCulture) : name;

    /// <summary>The weight line, with the weight state when it limits anything.</summary>
    public static string Weight(int weight, int limit, string load)
    {
        var line = "Weight " + weight.ToString(CultureInfo.InvariantCulture) + " / " + limit.ToString(CultureInfo.InvariantCulture);
        return load switch
        {
            WeightStates.Burdened => line + " (heavy: no regeneration)",
            WeightStates.Overweight => line + " (too heavy to fight)",
            _ => line,
        };
    }

    /// <summary>The entry count, e.g. <c>12 / 100 items</c>.</summary>
    public static string EntryCount(int count, int slots) => count.ToString(CultureInfo.InvariantCulture) + " / " + slots.ToString(CultureInfo.InvariantCulture) + " items";

    /// <summary>An equipment slot's label (an "Equipment slots" term).</summary>
    public static string SlotName(string slot) => slot switch
    {
        EquipSlots.HeadTop => "Head",
        EquipSlots.HeadMid => "Face",
        EquipSlots.HeadLow => "Mouth",
        EquipSlots.Body => "Body",
        EquipSlots.Weapon => "Weapon",
        EquipSlots.Shield => "Shield",
        EquipSlots.Garment => "Cloak",
        EquipSlots.Footgear => "Feet",
        EquipSlots.Accessory1 => "Ring 1",
        EquipSlots.Accessory2 => "Ring 2",
        _ => slot,
    };

    /// <summary>An equipment slot button: its label and what is worn there.</summary>
    public static string SlotButton(string slot, string? item) => SlotName(slot) + ": " + (item ?? EmptySlot);

    /// <summary>The flash for a level reached.</summary>
    public static string LevelUp(int level) => "Level " + level.ToString(CultureInfo.InvariantCulture) + "!";

    /// <summary>The flash for a job level reached.</summary>
    public static string JobLevelUp(int level) => "Job level " + level.ToString(CultureInfo.InvariantCulture) + "!";

    /// <summary>The info panel's second line for a kind of entity (an <c>EntityKinds</c> term). A dead one says so.</summary>
    public static string EntityKind(string kind, bool dead)
    {
        var label = kind switch
        {
            EntityKinds.Player => "Adventurer",
            EntityKinds.Monster => "Monster",
            EntityKinds.Npc => "Villager",
            EntityKinds.Item => "Item on the ground",
            _ => string.Empty,
        };
        return dead && label.Length > 0 ? label + " (defeated)" : label;
    }

    /// <summary>The button that cycles the UI size, showing the current one (a percentage).</summary>
    /// <summary>The sound settings button.</summary>
    public const string Sound = "Sound";

    /// <summary>Closes the sound settings.</summary>
    public const string Close = "Close";

    /// <summary>The button of a dialogue node without choices that goes on (T-601).</summary>
    public const string DialogueContinue = "Continue";

    /// <summary>The button of a dialogue's last node, which ends it.</summary>
    public const string DialogueDone = "Goodbye";

    /// <summary>What an NPC without a name in the bundle is called (a speaker the display bundle does not know).</summary>
    public const string DialogueUnknownSpeaker = "Someone";

    /// <summary>The flash for a refused talk or dialogue choice (a "Dialogue reasons" term); empty for one that needs no word.</summary>
    public static string DialogueRefused(string reason) => reason switch
    {
        DialogueReasons.TargetGone => "They are not here.",
        DialogueReasons.InvalidTarget => "There is nobody to talk to.",
        DialogueReasons.NoDialogue => "They have nothing to say.",
        DialogueReasons.Unreachable => "You cannot get there.",
        DialogueReasons.InvalidChoice => "That is no longer possible.",
        DialogueReasons.Busy => "One moment.",
        DialogueReasons.MissingItem => "You do not have what that needs.",
        DialogueReasons.InventoryFull => "Your bag is full.",
        DialogueReasons.TooHeavy => "Too heavy to carry.",
        _ => string.Empty,   // dead, cancelled: nothing to say
    };

    /// <summary>A bus volume button, e.g. <c>Music 70%</c>.</summary>
    public static string BusVolume(string name, double volume) => name + " " + Math.Round(volume * 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>The mute switch.</summary>
    public static string Mute(bool muted) => muted ? "Sound off" : "Sound on";

    public static string UiSize(double scale) => "UI " + Math.Round(scale * 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>The small connection indicator: a phase in one or two words.</summary>
    public static string Connection(ConnectionPhase phase) => phase switch
    {
        ConnectionPhase.Online => "Online",
        ConnectionPhase.Connecting or ConnectionPhase.AwaitingWelcome => "Connecting",
        ConnectionPhase.Backoff => "Offline",
        ConnectionPhase.LoggingOut => "Leaving",
        _ => string.Empty,
    };

    /// <summary>The HUD's HP line.</summary>
    public static string Hp(int hp, int max) => "HP " + hp.ToString(CultureInfo.InvariantCulture) + " / " + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>The HUD's SP line.</summary>
    public static string Sp(int sp, int max) => "SP " + sp.ToString(CultureInfo.InvariantCulture) + " / " + max.ToString(CultureInfo.InvariantCulture);

    /// <summary>The HUD's EXP line (<paramref name="next"/> 0 means the cap).</summary>
    public static string Exp(int level, long exp, long next) =>
        "Lv " + level.ToString(CultureInfo.InvariantCulture) + "  EXP " + exp.ToString(CultureInfo.InvariantCulture) +
        (next > 0 ? " / " + next.ToString(CultureInfo.InvariantCulture) : string.Empty);

    // ---- quests (T-602) ----------------------------------------------------------------------------------------------------------------------

    /// <summary>The tracker when no quest is active (it opens the quest log like the tracker does).</summary>
    public const string QuestsButton = "Quests";

    /// <summary>The quest log's title.</summary>
    public const string QuestLogTitle = "Quests";

    /// <summary>The heading over the active quests.</summary>
    public const string ActiveQuests = "Active";

    /// <summary>The heading over the completed quests.</summary>
    public const string CompletedQuests = "Completed";

    /// <summary>The log line when no quest is active: where quests come from.</summary>
    public const string NoActiveQuests = "No quests yet. Talk to people with a ! over their heads.";

    /// <summary>The log line when nothing is completed yet.</summary>
    public const string NoCompletedQuests = "None yet.";

    /// <summary>The log's button that puts a quest in the tracker.</summary>
    public const string Track = "Track";

    /// <summary>The log's button that drops a quest (the server forgets its progress).</summary>
    public const string Abandon = "Abandon";

    /// <summary>The confirmation of Abandon: progress is lost and nothing comes back.</summary>
    public const string ConfirmAbandon = "Abandon it (progress is lost)";

    /// <summary>A step line of the log: <c>Step 2/3: text</c>.</summary>
    public static string QuestStep(int index, int count, string text) =>
        "Step " + (index + 1).ToString(CultureInfo.InvariantCulture) + "/" + Math.Max(1, count).ToString(CultureInfo.InvariantCulture) + ": " + text;

    /// <summary>One objective with its counter, e.g. <c>Gel Slime 3/5</c>; <paramref name="target"/> is the display name of what it is about.</summary>
    public static string QuestObjective(string type, string target, int have, int need)
    {
        var count = have.ToString(CultureInfo.InvariantCulture) + "/" + need.ToString(CultureInfo.InvariantCulture);
        var done = have >= need ? " (done)" : string.Empty;
        return type switch
        {
            QuestObjectiveTypes.Kill => "Defeat " + target + " " + count,
            QuestObjectiveTypes.Collect => target + " " + count,
            QuestObjectiveTypes.Talk => "Talk to " + target + done,
            QuestObjectiveTypes.Reach => "Reach the spot (" + target + ")" + done,
            QuestObjectiveTypes.Level => "Base level " + count,
            QuestObjectiveTypes.Job => "Become " + target + done,
            QuestObjectiveTypes.UseItem => "Use " + target + " " + count,
            QuestObjectiveTypes.Equip => "Wear " + target + done,
            _ => target + " " + count,
        };
    }

    /// <summary>Why a met step waits (a "Quest reasons" term): the turn-in's items need room.</summary>
    public static string QuestHeld(string reason) => reason switch
    {
        QuestReasons.InventoryFull => "Make room in your bag to turn this in.",
        QuestReasons.TooHeavy => "Carry less to turn this in.",
        QuestReasons.MissingItem => "Some items it needs are gone.",
        _ => "Waiting.",
    };

    /// <summary>The flash for a quest change (a "Quest changes" term) of the quest named <paramref name="quest"/>; empty for <c>progress</c>.</summary>
    public static string QuestChange(string change, string quest) => change switch
    {
        QuestChanges.Accepted => "New quest: " + quest,
        QuestChanges.StepDone => "Quest updated: " + quest,
        QuestChanges.Completed => "Quest complete: " + quest,
        QuestChanges.Abandoned => "Quest abandoned: " + quest,
        _ => string.Empty,
    };

    // ---- the autopilot (T-605) ----

    /// <summary>The log's tick on a quest that is not in the run.</summary>
    public const string AutopilotAdd = "Add to autopilot";

    /// <summary>The log's tick on a quest in the run, with its place (1 for the first).</summary>
    public static string AutopilotTicked(int order) => "Autopilot: #" + order.ToString(CultureInfo.InvariantCulture);

    /// <summary>Moves a ticked quest earlier in the run.</summary>
    public const string AutopilotEarlier = "Earlier";

    /// <summary>Moves a ticked quest later in the run.</summary>
    public const string AutopilotLater = "Later";

    /// <summary>The log's button that starts the run.</summary>
    public const string AutopilotStart = "Start autopilot";

    /// <summary>The log's button that stops a run.</summary>
    public const string AutopilotStop = "Stop autopilot";

    /// <summary>The log's line over Start and Stop: the run's order, as quest names.</summary>
    public static string AutopilotQueue(string names) => "Run order: " + names;

    /// <summary>The log's line while nothing is ticked.</summary>
    public const string AutopilotNothingTicked = "Nothing ticked yet.";

    /// <summary>What the autopilot does (an "Autopilot activities" term), for the indicator.</summary>
    public static string AutopilotDoing(string? doing) => doing switch
    {
        AutopilotActivities.Travelling => "travelling",
        AutopilotActivities.Hunting => "hunting",
        AutopilotActivities.Gathering => "gathering",
        AutopilotActivities.Talking => "talking",
        AutopilotActivities.Waiting => "waiting",
        AutopilotActivities.Fighting => "fighting",
        AutopilotActivities.Returning => "returning",
        _ => "playing",
    };

    /// <summary>The indicator's first line while a run plays: the tap stops it.</summary>
    public static string AutopilotRunning(string? doing) => "Stop autopilot: " + AutopilotDoing(doing);

    /// <summary>The indicator's first line while a run waits: the tap stops it.</summary>
    public const string AutopilotPausedTitle = "Stop autopilot: paused";

    /// <summary>
    /// What the player can do about a pause (an "Autopilot pauses" term); <paramref name="detail"/> is the level, job or item's name it waits for, when known.
    /// </summary>
    public static string AutopilotPause(string? reason, string? detail) => reason switch
    {
        AutopilotPauses.NeedsLevel => detail is null ? "Raise your level to go on." : "Reach level " + detail + " to go on.",
        AutopilotPauses.NeedsJob => detail is null ? "Change your job to go on." : "Become " + detail + " to go on.",
        AutopilotPauses.NeedsEquip => detail is null ? "Put on the gear it needs to go on." : "Wear " + detail + " to go on.",
        AutopilotPauses.NeedsItemUse => detail is null ? "Use the item it needs to go on." : "Use " + detail + " to go on.",
        AutopilotPauses.NoSource => "Nothing gives what it needs. Take over.",
        AutopilotPauses.Unreachable => "No way there. Take over.",
        AutopilotPauses.NoProgress => "Nothing is moving. Take over.",
        AutopilotPauses.NoSafeChoice => "It needs your choice. Take over.",
        _ => "Waiting for you.",
    };

    /// <summary>The flash when a run ends (an "Autopilot stops" term): always says why.</summary>
    public static string AutopilotEnded(string? reason) => reason switch
    {
        AutopilotStops.Finished => "Autopilot finished: every quest is done.",
        AutopilotStops.Stopped => "Autopilot stopped.",
        AutopilotStops.ManualInput => "Autopilot stopped: you took over.",
        AutopilotStops.Died => "Autopilot stopped: you died.",
        AutopilotStops.Logout or AutopilotStops.Disconnected => "Autopilot stopped: the connection ended.",
        AutopilotStops.BagFull => "Autopilot stopped: your bag is full.",
        AutopilotStops.TimeLimit => "Autopilot stopped: no time left today.",
        AutopilotStops.QuestAbandoned => "Autopilot stopped: you dropped its quest.",
        _ => "Autopilot stopped.",
    };

    /// <summary>The flash for a refused start (an "Autopilot reasons" term); <paramref name="about"/> is the display name of the quest or skill it is about, if any.</summary>
    public static string AutopilotRefused(string reason, string? about = null) => reason switch
    {
        AutopilotReasons.Disabled => "Autopilot is switched off here.",
        AutopilotReasons.Dead => "You cannot use autopilot while dead.",
        AutopilotReasons.TimeLimit => "No autopilot time left today.",
        AutopilotReasons.EmptyQueue => "Tick a quest first.",
        AutopilotReasons.TooManyQuests => "Too many quests ticked.",
        AutopilotReasons.UnknownQuest or AutopilotReasons.NotActive => about is null ? "A ticked quest is not active." : about + " is not active.",
        AutopilotReasons.DuplicateQuest => about is null ? "A quest is ticked twice." : about + " is ticked twice.",
        AutopilotReasons.TooManySkills => "Too many skills on the skill bar.",
        AutopilotReasons.UnknownSkill or AutopilotReasons.NotLearned => about is null ? "A skill on the bar is not learned." : about + " is not learned.",
        AutopilotReasons.PassiveSkill => about is null ? "A passive skill cannot be used." : about + " is passive.",
        AutopilotReasons.LevelTooHigh => about is null ? "A skill is above its learned level." : about + " is above its learned level.",
        _ => "Autopilot could not start.",
    };

    // ---- auto-battle (T-811) ----

    /// <summary>The quest log's button that starts auto-battle where the character stands.</summary>
    public const string AutoBattleStart = "Auto-battle here";

    /// <summary>The button beside it: what the next auto-battle fights; a tap picks the next choice.</summary>
    public static string AutoBattleFilterButton(string filter) => "Fight: " + AutoBattleFilter(filter);

    /// <summary>A fight choice by itself (an "Auto-battle filters" term).</summary>
    public static string AutoBattleFilter(string filter) => filter switch
    {
        AutoBattleFilters.Nearby => "Nearby",
        AutoBattleFilters.Attackers => "Attackers",
        AutoBattleFilters.WithBosses => "+ Bosses",
        _ => filter,
    };

    /// <summary>The longest fight choice, for the layout check.</summary>
    public static string AutoBattleLongestFilter => AutoBattleFilters.All.Ordered.Select(AutoBattleFilter).OrderByDescending(s => s.Length).First();

    /// <summary>The indicator's first line while auto-battle runs: the tap stops it.</summary>
    public static string AutoBattleRunning(string? doing) => "Stop auto-battle: " + AutopilotDoing(doing);

    /// <summary>The indicator's second line while auto-battle runs.</summary>
    public const string AutoBattleArea = "Around where it started";

    /// <summary>The indicator while an auto-battle run waits (none does in this build; the server's word is shown as it comes).</summary>
    public const string AutoBattlePausedTitle = "Stop auto-battle: paused";

    /// <summary>The flash when an auto-battle run ends (an "Autopilot stops" term): always says why.</summary>
    public static string AutoBattleEnded(string? reason) => reason switch
    {
        AutopilotStops.Stopped => "Auto-battle stopped.",
        AutopilotStops.ManualInput => "Auto-battle stopped: you took over.",
        AutopilotStops.Died => "Auto-battle stopped: you died.",
        AutopilotStops.Logout or AutopilotStops.Disconnected => "Auto-battle stopped: the connection ended.",
        AutopilotStops.BagFull => "Auto-battle stopped: your bag is full.",
        AutopilotStops.TimeLimit => "Auto-battle stopped: no autopilot time left today.",
        AutopilotStops.LowHp => "Auto-battle stopped: your HP is low.",
        AutopilotStops.LeftArea => "Auto-battle stopped: you left the area.",
        AutopilotStops.NothingToFight => "Auto-battle stopped: nothing to fight here.",
        _ => "Auto-battle stopped.",
    };

    /// <summary>The flash for a refused auto-battle start (an "Auto-battle reasons" or shared "Autopilot reasons" term); <paramref name="about"/> is the skill's name, if any.</summary>
    public static string AutoBattleRefused(string reason, string? about = null) => reason switch
    {
        AutoBattleReasons.BattleDisabled => "Auto-battle is switched off here.",
        AutoBattleReasons.UnknownFilter => "That fight choice is not known here.",
        AutoBattleReasons.QuestsGiven => "Auto-battle could not start.",
        AutopilotReasons.Dead => "You cannot use auto-battle while dead.",
        AutopilotReasons.TimeLimit => "No autopilot time left today.",
        AutopilotReasons.TooManySkills or AutopilotReasons.UnknownSkill or AutopilotReasons.NotLearned or AutopilotReasons.PassiveSkill or AutopilotReasons.LevelTooHigh =>
            AutopilotRefused(reason, about),
        _ => "Auto-battle could not start.",
    };

    /// <summary>The flash for a refused quest start or abandon (a "Quest reasons" term).</summary>
    public static string QuestRefused(string reason) => reason switch
    {
        QuestReasons.AlreadyActive => "You are already on that quest.",
        QuestReasons.AlreadyCompleted => "You have done that quest.",
        QuestReasons.BaseLevelTooLow => "You need a higher level for that quest.",
        QuestReasons.WrongJob => "That quest is not for your job.",
        QuestReasons.QuestNotCompleted => "Finish the quests before it first.",
        QuestReasons.TooManyActive => "You have too many quests. Finish or abandon one.",
        QuestReasons.NotActive => "That quest is not active.",
        _ => "You cannot take that quest.",
    };

    // ---- the shop (T-603) ----

    /// <summary>The shop panel's heading.</summary>
    public const string ShopTitle = "Shop";

    /// <summary>The tab that lists what the shop sells.</summary>
    public const string ShopBuyTab = "Buy";

    /// <summary>The tab that lists the player's bag with the shop's prices.</summary>
    public const string ShopSellTab = "Sell";

    /// <summary>The button that buys the picked item.</summary>
    public const string ShopBuyButton = "Buy";

    /// <summary>The button that sells the picked item.</summary>
    public const string ShopSellButton = "Sell";

    /// <summary>The shop's hint while nothing is picked on the buy tab.</summary>
    public const string ShopPickToBuy = "Tap something to buy.";

    /// <summary>The shop's hint while nothing is picked on the sell tab.</summary>
    public const string ShopPickToSell = "Tap something in your bag to sell.";

    /// <summary>The sell tab when the bag holds nothing the shop will take.</summary>
    public const string ShopNothingToSell = "You have nothing the shop will buy.";

    /// <summary>The minus button of the quantity picker.</summary>
    public const string QuantityLess = "-";

    /// <summary>The bag's button that opens the wardrobe (T-708).</summary>
    public const string WardrobeButton = "Outfits";

    /// <summary>The wardrobe's heading.</summary>
    public const string WardrobeTitle = "Outfits";

    /// <summary>The wardrobe's button back to the bag.</summary>
    public const string BackToBag = "Bag";

    /// <summary>The choice that shows the gear in a slot.</summary>
    public const string OutfitNone = "My gear";

    /// <summary>What the wardrobe says while nothing is owned.</summary>
    public const string WardrobeEmpty = "No outfits yet. They change only how you look.";

    /// <summary>The line under the choices.</summary>
    public const string WardrobeHint = "Only looks: outfits never change what you can do. Your weapon always shows.";

    /// <summary>The line under the choices while outfits are hidden.</summary>
    public const string WardrobeHidden = "Outfits are hidden. Everyone sees your gear.";

    /// <summary>The wardrobe's switch.</summary>
    public static string HideOutfits(bool hidden) => hidden ? "Show outfits" : "Hide outfits";

    /// <summary>An outfit slot's label (an "Outfit slots" term).</summary>
    public static string OutfitSlot(string slot) => slot switch
    {
        OutfitSlots.Head => "Head",
        OutfitSlots.Face => "Face",
        OutfitSlots.Body => "Body",
        OutfitSlots.Back => "Back",
        OutfitSlots.Feet => "Feet",
        _ => slot,
    };

    /// <summary>The flash when an outfit is gained.</summary>
    public static string OutfitGained(string name) => "New outfit: " + name;

    /// <summary>The flash for a refused choice (an "Outfit reasons" term).</summary>
    public static string OutfitRefused(string reason) => reason switch
    {
        OutfitReasons.NotOwned => "You do not own that outfit.",
        OutfitReasons.SlotNotCovered => "That outfit has nothing for that place.",
        OutfitReasons.WrongGender => "That outfit does not fit this character.",
        _ => "That outfit does not exist.",
    };

    /// <summary>The plus button of the quantity picker.</summary>
    public const string QuantityMore = "+";

    /// <summary>The quantity picker's jump to the most allowed.</summary>
    public const string QuantityMax = "Max";

    /// <summary>The flash when a purchase went through.</summary>
    public static string Bought(string item) => "Bought: " + item;

    /// <summary>The flash when a sale went through.</summary>
    public static string Sold(string item) => "Sold: " + item;

    /// <summary>A shop row: the item with its price per unit, e.g. <c>Healing Draught - Crowns: 40</c> (an item the shop will not buy has no price).</summary>
    public static string ShopRow(string name, string currencyName, long? price) =>
        price is { } unit ? name + "  -  " + Currency(currencyName, unit) : name + "  -  " + ShopNoPrice;

    /// <summary>What a bag row shows as its price when the shop will not take the item.</summary>
    public const string ShopNoPrice = "not bought";

    /// <summary>The picker's count between its minus and plus buttons, e.g. <c>x5</c>.</summary>
    public static string QuantityLabel(int quantity) => "x" + quantity.ToString(CultureInfo.InvariantCulture);

    /// <summary>The picker's line: how many, and the total, e.g. <c>x5   Crowns: 200</c>.</summary>
    public static string ShopQuantity(int quantity, string currencyName, long? total) =>
        QuantityLabel(quantity) + "   " + (total is { } sum ? Currency(currencyName, sum) : "-");

    /// <summary>The line under the list while something is picked: the item, how many and the total, e.g. <c>Antidote x2   Crowns: 60</c>.</summary>
    public static string ShopPick(string name, int quantity, string currencyName, long? total) => name + " " + ShopQuantity(quantity, currencyName, total);

    /// <summary>The shop panel's heading line: its title and the balance, e.g. <c>Shop   Crowns: 150</c>.</summary>
    public static string ShopHeader(string currencyName, long currency) => ShopTitle + "   " + Currency(currencyName, currency);

    /// <summary>The bag's first line: the entry count and the currency (T-602), e.g. <c>12 / 100 items   Crowns: 150</c>.</summary>
    public static string BagHeader(int count, int slots, string currencyName, long currency) => EntryCount(count, slots) + "   " + Currency(currencyName, currency);

    /// <summary>The bag's currency line, e.g. <c>Crowns: 150</c>; <paramref name="name"/> is the bundle's currency name.</summary>
    public static string Currency(string name, long amount) =>
        (name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : "Money") + ": " + amount.ToString(CultureInfo.InvariantCulture);
}
