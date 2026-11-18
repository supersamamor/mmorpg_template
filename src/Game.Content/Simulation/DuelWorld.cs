using Game.Shared.Combat;
using Game.Shared.Definitions;
using Game.Shared.Effects;
using Game.Shared.Stats;
using Game.Shared.World;

namespace Game.Content.Simulation;

/// <summary>
/// The two fighters of a <see cref="Duel"/> as an <see cref="IEffectWorld"/>, so monster skills and the statuses they apply run through the real
/// <see cref="EffectRunner"/>. No geometry: knockback, teleport and summon do nothing here; an area holds the caster (friendly) or the other
/// fighter (hostile). HP is tracked here; damage taken from skills and from status ticks is counted for the report.
/// </summary>
internal sealed class DuelWorld : IEffectWorld
{
    public const long PlayerId = 1;
    public const long MonsterId = 2;

    private readonly EffectActor[] _actors = new EffectActor[3];
    private readonly StatusSet[] _statuses = [new(), new(), new()];
    private readonly int[] _hp = new int[3];

    public DuelWorld(StatSheet player, int weaponAtk, int refine, string? weaponElement, StatSheet monster, MonsterDef def)
    {
        _actors[PlayerId] = new EffectActor(PlayerId, player, new GridCell(0, 0), "e", false, true, weaponElement, weaponAtk: weaponAtk, refine: refine);
        _actors[MonsterId] = new EffectActor(MonsterId, monster, new GridCell(1, 0), "w", false, false, def.Element, def.Element, def.Race, def.Size);
    }

    public long NowMs { get; set; }

    public MapGrid? Grid => null;

    /// <summary>HP damage the player took from monster skill hits this fight.</summary>
    public long SkillDamage { get; private set; }

    /// <summary>HP damage the player took from status ticks this fight.</summary>
    public long StatusDamage { get; private set; }

    /// <summary>Statuses the player received (started, stacked or refreshed) this fight.</summary>
    public int StatusesApplied { get; private set; }

    public int Hp(long id) => _hp[id];

    public void Hit(long id, int damage) => _hp[id] = Math.Max(0, _hp[id] - damage);

    /// <summary>Starts a fight: full HP, no statuses (the caller ends the last fight's statuses first, which removes their modifiers).</summary>
    public void Reset(int playerHp, int monsterHp)
    {
        _hp[PlayerId] = playerHp;
        _hp[MonsterId] = monsterHp;
        NowMs = 0;
        SkillDamage = 0;
        StatusDamage = 0;
        StatusesApplied = 0;
    }

    /// <summary>The earliest time a status of <paramref name="id"/> ticks or ends, or <see cref="long.MaxValue"/>.</summary>
    public long NextStatusEvent(long id)
    {
        var next = long.MaxValue;
        foreach (var s in _statuses[id].Active) next = Math.Min(next, Math.Min(s.NextTickMs, s.EndsAtMs));
        return next;
    }

    public EffectActor Actor(long id) => _actors[id];

    public bool TryGetActor(long id, out EffectActor actor)
    {
        if (id is not (PlayerId or MonsterId))
        {
            actor = default;
            return false;
        }
        var a = _actors[id];
        actor = new EffectActor(a.Id, a.Stats, a.Cell, a.Facing, _hp[id] <= 0, a.Player, a.AttackElement, a.DefenseElement, a.Race, a.Size, a.WeaponAtk, a.Refine);
        return true;
    }

    public StatusSet? StatusesOf(long id, bool create) => id is PlayerId or MonsterId ? _statuses[id] : null;

    public void CollectArea(in EffectActor caster, GridCell center, int radius, bool friendly, List<long> into)
    {
        into.Clear();
        var id = friendly ? caster.Id : caster.Id == PlayerId ? MonsterId : PlayerId;
        if (_hp[id] > 0) into.Add(id);
    }

    public void Damage(in EffectActor caster, long target, AttackResult result, bool fromStatus)
    {
        var before = _hp[target];
        Hit(target, result.Damage);
        if (target != PlayerId) return;
        if (fromStatus) StatusDamage += before - _hp[target];
        else SkillDamage += before - _hp[target];
    }

    public void Heal(long caster, long target, int amount)
    {
    }

    public void RestoreSp(long caster, long target, int amount)
    {
    }

    public void StatusStarted(long caster, long target, ActiveStatus status, StatusChange change)
    {
        if (target == PlayerId) StatusesApplied++;
    }

    public void StatusEnded(long target, ActiveStatus status, string? reason)
    {
    }

    public void Knockback(long caster, long target, GridCell from, GridCell to)
    {
    }

    public void Teleport(long caster, TeleportEffect effect)
    {
    }

    public void Summon(long caster, SummonEffect effect, GridCell near)
    {
    }

    public void AreaApplied(long caster, GridCell center, int radius, int targets)
    {
    }
}
