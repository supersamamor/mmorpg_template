using System.Collections.Generic;
using Game.Shared.Guild;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Guild;

/// <summary>An invite this character has (T-904): who asks, to which guild, and when it ends (seconds of the client's wall clock).</summary>
public sealed record GuildInviteView(string Name, string Title, string Emblem, double EndsAt);

/// <summary>
/// The guild as the client holds it (T-904): what the server last said (<c>guild</c>, <c>guild_invited</c>, <c>guild_tags</c>), in memory only and emptied at every login. The client decides
/// nothing: it only hides what this character's rank cannot do (<see cref="May"/>, <see cref="MayActOn"/>); the server checks every permission again in its database. Pure; the HUD redraws
/// when <see cref="Version"/> moves.
/// </summary>
public sealed class GuildBook
{
    private readonly Dictionary<long, string> _tags = new Dictionary<long, string>();

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
        return Say(UiText.GuildNews(news.News, news.Name));
    }

    /// <summary>A refusal: its words; an answer refused leaves no invite to answer.</summary>
    public string Apply(GuildRefusedMessage refused)
    {
        if (refused.Intent == MessageTypes.GuildAnswer) Invite = null;
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
        Invite = null;
        Notice = null;
        SeenNotice = string.Empty;
        _tags.Clear();
        TagsVersion++;
        Version++;
    }

    private string Say(string words)
    {
        Notice = words;
        Version++;
        return words;
    }
}
