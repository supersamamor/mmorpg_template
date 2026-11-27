using System.Collections.Concurrent;
using Game.Shared.World;

namespace Game.Server.World.Party;

/// <summary>One party as the tick threads see it (T-902): its number, its leader and its members in party order (join order). Immutable: a change makes a new one.</summary>
public sealed record PartyView(long Id, EntityId Leader, IReadOnlyList<EntityId> Members);

/// <summary>Where a member is and how it is, as its instance last reported it (T-902): map instance, cell, HP and SP in whole percent (a living member never shows 0), dead or not.</summary>
public sealed record MemberStatus(string MapId, GridCell Cell, int HpPercent, int SpPercent, bool Dead);

/// <summary>
/// The party data the tick threads and the party service share (T-902, ARCHITECTURE §6.9). Plain data, like the progression store: the party service (off the tick) writes who is in
/// which party; each instance's <see cref="PartySystem"/> writes its members' <see cref="MemberStatus"/>; combat reads <see cref="Of"/> to share a kill. Nobody calls another system
/// through it (hard rule 7). Thread-safe and lock-free for readers: a party is an immutable <see cref="PartyView"/>.
/// </summary>
public sealed class PartyRoster
{
    private readonly ConcurrentDictionary<long, PartyView> _byMember = new();
    private readonly ConcurrentDictionary<long, MemberStatus> _status = new();

    /// <summary>The party of <paramref name="member"/>, or null when it is in none.</summary>
    public PartyView? Of(EntityId member) => _byMember.TryGetValue(member.Value, out var party) ? party : null;

    /// <summary>True when anybody is in a party (the quick way past the party rules).</summary>
    public bool Any => !_byMember.IsEmpty;

    /// <summary>The last status <paramref name="member"/>'s instance reported, or null (none yet, or not in a party).</summary>
    public MemberStatus? StatusOf(EntityId member) => _status.TryGetValue(member.Value, out var status) ? status : null;

    /// <summary>Instance tick thread: <paramref name="member"/>'s status now. Ignored for a character in no party.</summary>
    public void Report(EntityId member, MemberStatus status)
    {
        if (_byMember.ContainsKey(member.Value)) _status[member.Value] = status;
    }

    /// <summary>Party service: every member of <paramref name="party"/> is in it now.</summary>
    public void Set(PartyView party)
    {
        foreach (var member in party.Members) _byMember[member.Value] = party;
    }

    /// <summary>Party service: <paramref name="member"/> is in no party now.</summary>
    public void Remove(EntityId member)
    {
        _byMember.TryRemove(member.Value, out _);
        _status.TryRemove(member.Value, out _);
    }
}
