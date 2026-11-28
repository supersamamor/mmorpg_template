using System.Collections.Generic;
using Game.Shared.Guild;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Guild;

/// <summary>An invite this character has (T-904): who asks, to which guild, and when it ends (seconds of the client's wall clock).</summary>
public sealed record GuildInviteView(string Name, string Title, string Emblem, double EndsAt);

/// <summary>What the server said to the rank editor's Save (T-810): its words, and whether the ranks were taken.</summary>
public sealed record GuildRankAnswer(bool Saved, string Words);

/// <summary>
/// The guild as the client holds it (T-904): what the server last said (<c>guild</c>, <c>guild_invited</c>, <c>guild_tags</c>), in memory only and emptied at every login. The client decides
/// nothing: it only hides what this character's rank cannot do (<see cref="May"/>, <see cref="MayActOn"/>); the server checks every permission again in its database. Pure; the HUD redraws
/// when <see cref="Version"/> moves.
/// </summary>
public sealed class GuildBook
{
    /// <summary>
    /// How often an open storage window opens it again (T-810), in seconds: that renews the server's lease (<c>--guild-storage-lease-ms</c>, 15 s), so others' moves keep arriving.
    /// </summary>
    public const double StorageRenewSeconds = 5;

    private readonly Dictionary<long, string> _tags = new Dictionary<long, string>();
    private bool _watching;
    private double? _renewAt;
    private bool _savingRanks;

    /// <summary>Moves on every change.</summary>
    public int Version { get; private set; }

    /// <summary>Moves when the emblem tags change.</summary>
    public int TagsVersion { get; private set; }

    /// <summary>The guild as last sent, or <see cref="GuildMessage.None"/>.</summary>
    public GuildMessage State { get; private set; } = GuildMessage.None;

    /// <summary>True while in a guild.</summary>
    public bool InGuild => State.Guild is not null;

    /// <summary>This character's member record, or null.</summary>
    public GuildMemberRecord? Own
    {
        get
        {
            foreach (var m in State.Members)
                if (m.Member == State.Own) return m;
            return null;
        }
    }

    /// <summary>This character's rank place (-1 when in no guild).</summary>
    public int OwnRank => Own?.Rank ?? -1;

    /// <summary>The invite waiting for an answer, or null.</summary>
    public GuildInviteView? Invite { get; private set; }

    /// <summary>The notice last seen, to tell a new one (shown once in the flash line).</summary>
    public string SeenNotice { get; private set; } = string.Empty;

    /// <summary>The last news or refusal, as words.</summary>
    public string? Notice { get; private set; }

    /// <summary>The guild's storage as last sent (T-905), or null before it was opened.</summary>
    public GuildStorageMessage? Storage { get; private set; }

    /// <summary>Moves with every <c>guild_storage</c> (T-810: apart from <see cref="Version"/>, so a storage that arrives does not redraw the guild panel).</summary>
    public int StorageVersion { get; private set; }

    /// <summary>The storage as it is now (the answer to opening it, to a move that landed, and to others' moves while the window is open).</summary>
    public void Apply(GuildStorageMessage storage)
    {
        Storage = storage;
        StorageVersion++;
    }

    /// <summary>The storage window opened or closed (T-810).</summary>
    public void WatchStorage(bool open)
    {
        _watching = open;
        _renewAt = null;
    }

    /// <summary>
    /// True when the open window should open the storage again now (every <see cref="StorageRenewSeconds"/> of the client's wall clock, counted from the first look after it opened).
    /// </summary>
    public bool StorageRenewalDue(double wallNow)
    {
        if (!_watching || !InGuild) return false;
        if (_renewAt is not { } due)
        {
            _renewAt = wallNow + StorageRenewSeconds;
            return false;
        }
        if (wallNow < due) return false;
        _renewAt = wallNow + StorageRenewSeconds;
        return true;
    }

    /// <summary>The answer to the rank editor's last Save, or null while none came (T-810).</summary>
    public GuildRankAnswer? RankAnswer { get; private set; }

    /// <summary>The rank editor sent its ranks (or opened): the next <c>ranks</c> news or <c>guild_ranks</c> refusal is the answer.</summary>
    public void AwaitRanks(bool sent = true)
    {
        _savingRanks = sent;
        RankAnswer = null;
        Version++;
    }

    /// <summary>May this character's rank do <paramref name="permission"/>? (Only for what the panel shows.)</summary>
    public bool May(string permission) => InGuild && GuildRules.Has(State.Ranks, OwnRank, permission);

    /// <summary>True for the leader.</summary>
    public bool Leading => InGuild && OwnRank == GuildRules.LeaderRank;

    /// <summary>May this character act on <paramref name="member"/> with <paramref name="permission"/>: it holds it and the member's rank is below its own.</summary>
    public bool MayActOn(GuildMemberRecord member, string permission) => member.Member != State.Own && May(permission) && member.Rank > OwnRank;

    /// <summary>The emblem shown above <paramref name="entity"/>'s name, or null.</summary>
    public string? TagOf(long entity) => _tags.TryGetValue(entity, out var emblem) ? emblem : null;

    /// <summary>The guild as it is now. Returns the notice's words when it is new (sign-in, or changed), else null.</summary>
    public string? Apply(GuildMessage guild)
    {
        State = guild;
        Version++;
        if (guild.Guild is null)
        {
            SeenNotice = string.Empty;
            Storage = null;   // T-905: out of the guild, no storage
            WatchStorage(false);
            return null;
        }
        if (guild.Notice.Length == 0 || guild.Notice == SeenNotice)
        {
            SeenNotice = guild.Notice;
            return null;
        }
        SeenNotice = guild.Notice;
        return UiText.GuildNoticeShown(guild.Name, guild.Notice);
    }

    /// <summary>An invite came; it ends <c>expires_in</c> seconds from <paramref name="wallNow"/>.</summary>
    public void Apply(GuildInvitedMessage invited, double wallNow)
    {
        Invite = new GuildInviteView(invited.Name, invited.Title, invited.Emblem, wallNow + invited.ExpiresInSeconds);
        Version++;
    }

    /// <summary>News: its words; an expired invite is gone.</summary>
    public string Apply(GuildNewsMessage news)
    {
        if (news.News == GuildNews.Expired && Invite is { } invite && invite.Name == news.Name) Invite = null;
        if (news.News == GuildNews.Ranks && _savingRanks) Answer(true, UiText.GuildRanksSaved);   // the guild with them came just before
        return Say(UiText.GuildNews(news.News, news.Name));
    }

    /// <summary>A refusal: its words; an answer refused leaves no invite to answer.</summary>
    public string Apply(GuildRefusedMessage refused)
    {
        if (refused.Intent == MessageTypes.GuildAnswer) Invite = null;
        if (refused.Intent == MessageTypes.GuildRanks) Answer(false, UiText.GuildRefused(refused.Reason, refused.RetryAfterSeconds, refused.Name));
        return Say(UiText.GuildRefused(refused.Reason, refused.RetryAfterSeconds, refused.Name));
    }

    /// <summary>The emblems of the guild members on this map.</summary>
    public void Apply(GuildTagsMessage tags)
    {
        _tags.Clear();
        foreach (var t in tags.Tags) _tags[t.Entity] = t.Emblem;
        TagsVersion++;
        Version++;
    }

    /// <summary>The invite was answered (the answer is on its way): it is no longer shown.</summary>
    public void Answered()
    {
        Invite = null;
        Version++;
    }

    /// <summary>The invite is past its end on the client's clock (the server's word follows).</summary>
    public void Expire(double wallNow)
    {
        if (Invite is { } invite && wallNow >= invite.EndsAt) Answered();
    }

    /// <summary>Forgets everything (a new login, back on the login screen).</summary>
    public void Clear()
    {
        State = GuildMessage.None;
        Storage = null;
        WatchStorage(false);
        _savingRanks = false;
        RankAnswer = null;
        Invite = null;
        Notice = null;
        SeenNotice = string.Empty;
        _tags.Clear();
        TagsVersion++;
        Version++;
    }

    private void Answer(bool saved, string words)
    {
        _savingRanks = false;
        RankAnswer = new GuildRankAnswer(saved, words);
    }

    private string Say(string words)
    {
        Notice = words;
        Version++;
        return words;
    }
}
