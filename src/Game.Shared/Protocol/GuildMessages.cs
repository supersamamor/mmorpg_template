using System.Collections.Generic;

namespace Game.Shared.Protocol
{
    // ---- client to server (T-904): intents only; the server checks every permission in the database ---------------------------------------------

    /// <summary><c>guild_found</c> (T-904): <c>{"type":"guild_found","name":"Iron Oaks","emblem":"icon.emblem.oak"}</c>. Founds a guild led by this character, its cost paid.</summary>
    /// <param name="Name">The guild's name, as typed.</param>
    /// <param name="Emblem">One of the theme's emblem asset keys (bundle <c>guild_emblems</c>).</param>
    public sealed record GuildFoundMessage(string Name, string Emblem) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildFound;
    }

    /// <summary>
    /// <c>guild_invite</c> (T-904): invite a character into this character's guild, by name (<c>{"type":"guild_invite","name":"Bo"}</c>) or by tapping a player on this map
    /// (<c>{"type":"guild_invite","target":43}</c>): exactly one of the two. Needs the <c>invite</c> permission.
    /// </summary>
    /// <param name="Name">The character's name, as typed.</param>
    /// <param name="Target">The entity id of a player on this map.</param>
    public sealed record GuildInviteMessage(string? Name, long? Target) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildInvite;
    }

    /// <summary><c>guild_answer</c> (T-904): <c>{"type":"guild_answer","accept":true}</c>. Answers the invite this character has (<see cref="GuildInvitedMessage"/>).</summary>
    /// <param name="Accept">True joins at the lowest rank; false declines.</param>
    public sealed record GuildAnswerMessage(bool Accept) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildAnswer;
    }

    /// <summary><c>guild_leave</c> (T-904): <c>{"type":"guild_leave"}</c>. Leaves the guild; a leader hands over first by the rule in ARCHITECTURE §6.11.</summary>
    public sealed record GuildLeaveMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildLeave;
    }

    /// <summary><c>guild_kick</c> (T-904): <c>{"type":"guild_kick","member":12}</c>. Takes a member of a lower rank out (<c>kick</c> permission).</summary>
    /// <param name="Member">The member's number (<see cref="GuildMemberRecord.Member"/>).</param>
    public sealed record GuildKickMessage(long Member) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildKick;
    }

    /// <summary><c>guild_rank</c> (T-904): <c>{"type":"guild_rank","member":12,"rank":1}</c>. Gives a member of a lower rank another rank below one's own (<c>set_rank</c> permission).</summary>
    /// <param name="Member">The member's number.</param>
    /// <param name="Rank">The rank's place (1 is the highest below the leader's 0).</param>
    public sealed record GuildRankMessage(long Member, int Rank) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildRank;
    }

    /// <summary>
    /// <c>guild_ranks</c> (T-904, leader only): <c>{"type":"guild_ranks","ranks":[{"name":"Officer","permissions":["invite","kick"]},{"name":"Member","permissions":[]}]}</c>. The ranks below the
    /// leader, highest first: place 1, 2, ... Members keep their place; those at a place the new list no longer has go to its last rank.
    /// </summary>
    /// <param name="Ranks">The ranks, the leader's not included.</param>
    public sealed record GuildRanksMessage(IReadOnlyList<GuildRankRecord> Ranks) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildRanks;
    }

    /// <summary><c>guild_lead</c> (T-904, leader only): <c>{"type":"guild_lead","member":12}</c>. Hands the leader rank to a member; the old leader takes the new leader's old rank.</summary>
    /// <param name="Member">The member's number.</param>
    public sealed record GuildLeadMessage(long Member) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildLead;
    }

    /// <summary><c>guild_notice</c> (T-904): <c>{"type":"guild_notice","text":"Meet at the well"}</c>. Writes the notice (<c>edit_notice</c> permission); an empty text clears it.</summary>
    /// <param name="Text">The notice, as typed: cleaned like a chat line.</param>
    public sealed record GuildNoticeMessage(string Text) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildNotice;
    }

    // ---- server to client ---------------------------------------------------------------------------------------------------------------------

    /// <summary>One rank (T-904): its name and its "Guild permissions" terms. The leader's (place 0) holds every permission whatever it lists.</summary>
    public sealed record GuildRankRecord(string Name, IReadOnlyList<string> Permissions);

    /// <summary>One member in <see cref="GuildMessage"/> (T-904): its number in the guild, character name, rank place, base level as last saved, and whether it plays on this server process now.</summary>
    public sealed record GuildMemberRecord(long Member, string Name, int Rank, int Level, bool Online);

    /// <summary>
    /// <c>guild</c> (T-904, members only): the guild as it is now, after every change, every welcome and when a member comes or goes:
    /// <c>{"type":"guild","guild":{"id":3,"name":"Iron Oaks","emblem":"icon.emblem.oak","notice":"...","own":12,"ranks":[...],"members":[...]}}</c>. Not in a guild: <c>{"type":"guild"}</c>.
    /// No member's map or cell is sent (ARCHITECTURE §6.11).
    /// </summary>
    /// <param name="Guild">The guild's id, or null when in none (then the rest is empty).</param>
    /// <param name="Name">The guild's name.</param>
    /// <param name="Emblem">Its emblem's asset key.</param>
    /// <param name="Notice">The notice (empty when none).</param>
    /// <param name="Own">This character's member number.</param>
    /// <param name="Ranks">The ranks by place, the leader's first.</param>
    /// <param name="Members">The members, highest rank first, then by when they joined.</param>
    public sealed record GuildMessage(long? Guild, string Name, string Emblem, string Notice, long Own, IReadOnlyList<GuildRankRecord> Ranks, IReadOnlyList<GuildMemberRecord> Members)
        : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.Guild;

        /// <summary>Not in a guild.</summary>
        public static GuildMessage None { get; } = new GuildMessage(null, "", "", "", 0, new GuildRankRecord[0], new GuildMemberRecord[0]);
    }

    /// <summary>
    /// <c>guild_invited</c> (T-904, the invited only): <c>{"type":"guild_invited","name":"Ada","title":"Iron Oaks","emblem":"icon.emblem.oak","expires_in":30}</c>. Answer with
    /// <see cref="GuildAnswerMessage"/>.
    /// </summary>
    /// <param name="Name">The inviter's character name.</param>
    /// <param name="Title">The guild's name.</param>
    /// <param name="Emblem">The guild's emblem.</param>
    /// <param name="ExpiresInSeconds">Seconds until the invite expires.</param>
    public sealed record GuildInvitedMessage(string Name, string Title, string Emblem, long ExpiresInSeconds) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildInvited;
    }

    /// <summary><c>guild_news</c> (T-904): <c>{"type":"guild_news","news":"joined","name":"Bo"}</c>.</summary>
    /// <param name="News">A "Guild news" term.</param>
    /// <param name="Name">The character it is about, if any.</param>
    public sealed record GuildNewsMessage(string News, string? Name = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildNews;
    }

    /// <summary><c>guild_refused</c> (T-904, own only): <c>{"type":"guild_refused","intent":"guild_kick","reason":"rank_too_high"}</c>. Nothing changed.</summary>
    /// <param name="Intent">The message refused: a <c>guild_*</c> client type.</param>
    /// <param name="Reason">A "Guild reasons" term.</param>
    /// <param name="RetryAfterSeconds">For <c>rate_limited</c>: when the next may go.</param>
    /// <param name="Name">The name an invite named, if any.</param>
    public sealed record GuildRefusedMessage(string Intent, string Reason, long? RetryAfterSeconds = null, string? Name = null) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildRefused;
    }

    /// <summary>One player's emblem in <see cref="GuildTagsMessage"/>: its entity id and its guild's emblem.</summary>
    public sealed record GuildTagRecord(long Entity, string Emblem);

    /// <summary>
    /// <c>guild_tags</c> (T-904): <c>{"type":"guild_tags","tags":[{"entity":42,"emblem":"icon.emblem.oak"}]}</c>. Every guild member playing on this map instance, so its emblem shows above its
    /// name; sent when the list changes for this player (a join, a leave, a warp).
    /// </summary>
    /// <param name="Tags">The members on the map with their emblems.</param>
    public sealed record GuildTagsMessage(IReadOnlyList<GuildTagRecord> Tags) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildTags;
    }
    // ---- guild storage (T-905) -----------------------------------------------------------------------------------------------------------------

    /// <summary><c>guild_storage_open</c> (T-905): <c>{"type":"guild_storage_open"}</c>. Asks for the storage (<see cref="GuildStorageMessage"/>); any member may look.</summary>
    public sealed record GuildStorageOpenMessage : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildStorageOpen;
    }

    /// <summary>
    /// <c>guild_deposit</c> (T-905, <c>storage_deposit</c>): an entry of the bag, whole or part of a stack (<c>{"type":"guild_deposit","entry":981,"quantity":5}</c>), or currency
    /// (<c>{"type":"guild_deposit","currency":300}</c>): exactly one of the two. Worn items and items content marks not tradeable stay in the bag.
    /// </summary>
    /// <param name="Entry">The bag entry's id, or null for currency.</param>
    /// <param name="Quantity">Units of that entry (all of it, or part of a stack).</param>
    /// <param name="Currency">Currency, when no entry is named.</param>
    public sealed record GuildDepositMessage(long? Entry, int Quantity, long Currency) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildDeposit;
    }

    /// <summary>
    /// <c>guild_withdraw</c> (T-905): a storage entry, whole or part of a stack (<c>{"type":"guild_withdraw","entry":981,"quantity":5}</c>, <c>storage_items</c>), or currency
    /// (<c>{"type":"guild_withdraw","currency":300}</c>, <c>storage_currency</c>): exactly one of the two.
    /// </summary>
    /// <param name="Entry">The storage entry's id, or null for currency.</param>
    /// <param name="Quantity">Units of that entry.</param>
    /// <param name="Currency">Currency, when no entry is named.</param>
    public sealed record GuildWithdrawMessage(long? Entry, int Quantity, long Currency) : ClientMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildWithdraw;
    }

    /// <summary>
    /// One move in the storage's log (T-905): when (unix seconds), by whom (the character's name now; empty when that character no longer exists), which "Guild storage moves" term, the item,
    /// its entry id and quantity, or currency. Nothing else about the person is kept.
    /// </summary>
    public sealed record GuildStorageLogRecord(long At, string Name, string Move, string? Item, long? Entry, int Quantity, long Currency);

    /// <summary>
    /// <c>guild_storage</c> (T-905, own only): <c>{"type":"guild_storage","size":50,"currency":1200,"entries":[{"id":981,"item":"item.herb","quantity":5}],"log":[...]}</c>, the answer to
    /// <see cref="GuildStorageOpenMessage"/> and to every move that landed. <c>log</c> is there only for a rank with <c>storage_log</c>.
    /// </summary>
    /// <param name="Size">Most entries the storage holds (<c>guild.storage.size</c>).</param>
    /// <param name="Currency">The storage's currency.</param>
    /// <param name="Entries">Its entries, oldest first.</param>
    /// <param name="Log">The newest moves first, or null when this character's rank may not read the log.</param>
    public sealed record GuildStorageMessage(int Size, long Currency, IReadOnlyList<InventoryEntryRecord> Entries, IReadOnlyList<GuildStorageLogRecord>? Log) : ServerMessage
    {
        /// <inheritdoc />
        public override string Type => MessageTypes.GuildStorage;
    }
}
