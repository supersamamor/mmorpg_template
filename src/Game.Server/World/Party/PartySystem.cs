using Game.Server.World.Quests;
using Game.Shared.Definitions;
using Game.Shared.World;

namespace Game.Server.World.Party;

/// <summary>
/// The party's part of each map instance's tick (T-902, ARCHITECTURE §6.9). It owns no intents (parties are formed off the tick, by the party service) and does two things:
/// <list type="bullet">
/// <item><b>Kill credit</b>: on <see cref="MonsterKilled"/> it publishes <see cref="PartyKillCredit"/> for the party members of the credited players who are alive, on this instance and
/// within <see cref="PartyBalanceDef.KillCreditRange"/> of the death cell, and were not credited themselves. The quest system counts their <c>kill</c> objectives from it.</item>
/// <item><b>Members' status</b>: every <c>statusIntervalTicks</c> ticks it reports each party member on this instance (map, cell, HP and SP percent, dead) to the
/// <see cref="PartyRoster"/>, which the party service sends to the other members. A player in no party costs one dictionary read; nothing runs while nobody is in a party.</item>
/// </list>
/// </summary>
public sealed class PartySystem : ITickSystem, Reload.IContentReloadable
{
    private readonly PartyRoster _roster;
    private PartyBalanceDef _balance;
    private readonly int _statusIntervalTicks;
    private readonly List<EntityId> _credited = new();
    private MapInstance? _instance;

    /// <summary>T-801: the new party numbers (the party service outside the tick keeps its own until a restart; see ARCHITECTURE §5.5).</summary>
    public void Reload(Reload.ServerContent next) => _balance = next.Balance.Party;

    /// <summary>Creates the system over the shared <paramref name="roster"/>; members' status is reported every <paramref name="statusIntervalTicks"/> ticks (at least 1).</summary>
    public PartySystem(PartyRoster roster, PartyBalanceDef balance, int statusIntervalTicks)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _balance = balance ?? throw new ArgumentNullException(nameof(balance));
        _statusIntervalTicks = Math.Max(1, statusIntervalTicks);
    }

    /// <inheritdoc />
    public string Name => "party";

    /// <inheritdoc />
    public IReadOnlyCollection<Type> IntentTypes => [];

    /// <inheritdoc />
    public void Tick(TickContext context)
    {
        if (_instance is null)
        {
            _instance = context.Instance;
            _instance.Events.Subscribe<MonsterKilled>(e => OnKilled(context, e));
        }
        if (context.TickNumber % _statusIntervalTicks != 0 || !_roster.Any) return;
        var players = _instance.Players;
        for (var i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (_roster.Of(player.Id) is null) continue;
            _roster.Report(player.Id, StatusOf(_instance.MapId, player));
        }
    }

    /// <summary>The status of <paramref name="player"/>: HP and SP in whole percent, rounded up so a living member never shows 0; 0 when dead.</summary>
    public static MemberStatus StatusOf(string mapId, Entity player)
    {
        var vitals = player.Vitals;
        var hp = player.Dead ? 0 : Percent(vitals?.Hp ?? 0, vitals?.MaxHp ?? 0);
        var sp = player.Dead ? 0 : Percent(vitals?.Sp ?? 0, vitals?.MaxSp ?? 0);
        return new MemberStatus(mapId, player.Cell, hp, sp, player.Dead);
    }

    private static int Percent(int value, int max) => max <= 0 || value <= 0 ? 0 : (int)Math.Min(100, ((long)value * 100 + max - 1) / max);

    private void OnKilled(TickContext context, MonsterKilled e)
    {
        if (!_roster.Any || e.Credited.Count == 0) return;
        _credited.Clear();
        for (var i = 0; i < e.Credited.Count; i++)
        {
            if (_roster.Of(e.Credited[i]) is not { } party) continue;
            foreach (var member in party.Members)
            {
                if (_credited.Contains(member) || Contains(e.Credited, member)) continue;
                if (!_instance!.TryGetEntity(member, out var entity) || entity.Kind != EntityKind.Player || entity.Dead) continue;
                if (GridDistance.Chebyshev(entity.Cell, e.Cell) > _balance.KillCreditRange) continue;
                _credited.Add(member);
            }
        }
        if (_credited.Count > 0) context.Publish(new PartyKillCredit(e.Monster, e.MonsterId, e.Cell, _credited.ToArray()));
    }

    private static bool Contains(IReadOnlyList<EntityId> list, EntityId id)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == id) return true;
        return false;
    }
}
