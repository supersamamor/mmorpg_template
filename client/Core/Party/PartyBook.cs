using System;
using System.Collections.Generic;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Client.Core.Party;

/// <summary>One member as the client shows it (T-902): the server's id and name, the last HP and SP percent, map and cell it sent, and whether it is dead or away.</summary>
public sealed record PartyMemberView(long Id, string Name, bool Leader, bool Mine, int Hp, int Sp, string? Map, GridCell? Cell, bool Dead, bool Away, bool Known);

/// <summary>An invite this character has: who asks, and when it ends (seconds of the client's wall clock).</summary>
public sealed record PartyInviteView(long From, string Name, double EndsAt);

/// <summary>
/// The party as the client holds it (T-902): what the server last said (<c>party</c>, <c>party_members</c>, <c>party_invited</c>), in memory only and emptied at every login. The client
/// decides nothing: who is in range, who gets EXP and who killed what are the server's. Pure; the HUD redraws when <see cref="Version"/> moves.
/// </summary>
public sealed class PartyBook
{
    private readonly List<PartyMemberRecord> _members = new List<PartyMemberRecord>();
    private readonly Dictionary<long, PartyMemberStatusRecord> _status = new Dictionary<long, PartyMemberStatusRecord>();

    /// <summary>Moves on every change.</summary>
    public int Version { get; private set; }

    /// <summary>This character's entity id.</summary>
    public long OwnId { get; set; }

    /// <summary>The party's number, or null when in none.</summary>
    public long? PartyId { get; private set; }

    /// <summary>The leader's entity id (0 when in none).</summary>
    public long Leader { get; private set; }

    /// <summary>True while in a party.</summary>
    public bool InParty => PartyId is not null;

    /// <summary>True when this character leads its party.</summary>
    public bool Leading => InParty && Leader == OwnId;

    /// <summary>The invite waiting for an answer, or null.</summary>
    public PartyInviteView? Invite { get; private set; }

    /// <summary>A player tapped in the world (to invite), or 0.</summary>
    public long Tapped { get; private set; }

    /// <summary>The last notice or refusal, as words (shown once in the flash line).</summary>
    public string? Notice { get; private set; }

    /// <summary>Moves when <see cref="Notice"/> is new.</summary>
    public int NoticeVersion { get; private set; }

    /// <summary>The members in party order, this character included.</summary>
    public IReadOnlyList<PartyMemberView> Members
    {
        get
        {
            var list = new List<PartyMemberView>(_members.Count);
            foreach (var m in _members)
            {
                var known = _status.TryGetValue(m.Id, out var s);
                list.Add(new PartyMemberView(m.Id, m.Name, m.Id == Leader, m.Id == OwnId, known ? s!.Hp : 0, known ? s!.Sp : 0, known ? s!.Map : null, known ? s!.Cell : null,
                    known && s!.Dead, known && s!.Away, known));
            }
            return list;
        }
    }

    /// <summary>The party as it is now (replaces what was there; members' states of those who left are forgotten).</summary>
    public void Apply(PartyMessage party)
    {
        PartyId = party.Party;
        Leader = party.Party is null ? 0 : party.Leader;
        _members.Clear();
        _members.AddRange(party.Members);
        var ids = new HashSet<long>();
        foreach (var m in party.Members) ids.Add(m.Id);
        foreach (var id in new List<long>(_status.Keys))
            if (!ids.Contains(id)) _status.Remove(id);
        if (InParty && Tapped != 0 && ids.Contains(Tapped)) Tapped = 0;
        Version++;
    }

    /// <summary>The other members' states (those it does not name keep theirs).</summary>
    public void Apply(PartyMembersMessage members)
    {
        foreach (var s in members.Status) _status[s.Id] = s;
        Version++;
    }

    /// <summary>An invite came; it ends <c>expires_in</c> seconds from <paramref name="wallNow"/>.</summary>
    public void Apply(PartyInvitedMessage invited, double wallNow)
    {
        Invite = new PartyInviteView(invited.From, invited.Name, wallNow + invited.ExpiresInSeconds);
        Version++;
    }

    /// <summary>A notice: its words for the flash line; an expired invite is gone.</summary>
    public void Apply(PartyNoticeMessage notice)
    {
        if (notice.Notice == Game.Shared.Vocabulary.PartyNotices.Expired && Invite is { } invite && invite.Name == notice.Name) Invite = null;
        Say(UiText.PartyNotice(notice.Notice, notice.Name));
    }

    /// <summary>A refusal: its words for the flash line; an answer refused leaves no invite to answer.</summary>
    public void Apply(PartyRefusedMessage refused)
    {
        if (refused.Intent == MessageTypes.PartyAnswer) Invite = null;
        Say(UiText.PartyRefused(refused.Reason, refused.RetryAfterSeconds, refused.Name));
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

    /// <summary>A player was tapped in the world (0: none): the party panel offers to invite it.</summary>
    public void Tap(long entity)
    {
        Tapped = entity == OwnId ? 0 : entity;
        Version++;
    }

    /// <summary>Forgets everything (a new login, back on the login screen).</summary>
    public void Clear()
    {
        PartyId = null;
        Leader = 0;
        _members.Clear();
        _status.Clear();
        Invite = null;
        Tapped = 0;
        Notice = null;
        Version++;
    }

    private void Say(string words)
    {
        Notice = words;
        NoticeVersion++;
        Version++;
    }
}
