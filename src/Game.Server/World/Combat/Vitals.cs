using Game.Shared.Stats;
using Game.Shared.Vocabulary;

namespace Game.Server.World.Combat;

/// <summary>
/// Current HP and SP of one combat-capable entity, kept on <see cref="Entity.Vitals"/> next to its <see cref="StatSheet"/>. Starts full when
/// combat first sees the entity, never goes below 0, and is clamped to the new <c>max_hp</c> / <c>max_sp</c> whenever the sheet changes
/// (a lower maximum lowers the current value; a higher one leaves it). SP is tracked but nothing spends it yet. Changed only by combat on
/// the instance's tick.
/// </summary>
public sealed class Vitals
{
    private static readonly int MaxHpStat = StatIndex.IndexOf(DerivedStats.MaxHp);
    private static readonly int MaxSpStat = StatIndex.IndexOf(DerivedStats.MaxSp);

    internal Vitals(StatSheet stats)
    {
        StatsVersion = stats.Version;
        MaxHp = Math.Max(0, stats.GetInt(MaxHpStat));
        MaxSp = Math.Max(0, stats.GetInt(MaxSpStat));
        Hp = MaxHp;
        Sp = MaxSp;
    }

    /// <summary>Current HP, 0..<see cref="MaxHp"/>. 0 means dead (combat sets <see cref="Entity.Dead"/> in the same step).</summary>
    public int Hp { get; internal set; }

    /// <summary>Current SP, 0..<see cref="MaxSp"/>.</summary>
    public int Sp { get; internal set; }

    /// <summary><c>max_hp</c> of the sheet as of the last sync.</summary>
    public int MaxHp { get; private set; }

    /// <summary><c>max_sp</c> of the sheet as of the last sync.</summary>
    public int MaxSp { get; private set; }

    /// <summary>The sheet's <see cref="StatSheet.Version"/> at the last sync.</summary>
    internal long StatsVersion { get; private set; }

    /// <summary>Re-reads the maxima when the sheet changed and clamps the current values to them. Allocates nothing.</summary>
    internal void Sync(StatSheet stats)
    {
        if (stats.Version == StatsVersion) return;
        StatsVersion = stats.Version;
        MaxHp = Math.Max(0, stats.GetInt(MaxHpStat));
        MaxSp = Math.Max(0, stats.GetInt(MaxSpStat));
        if (Hp > MaxHp) Hp = MaxHp;
        if (Sp > MaxSp) Sp = MaxSp;
    }
}

/// <summary>
/// What an <see cref="EntityKind.Item"/> entity holds: the item, how many, who has loot priority until when, and when it disappears. Times
/// are the instance's simulated milliseconds. Immutable; picking up (T-401) despawns the entity.
/// </summary>
/// <param name="ItemId">The item (<c>item.*</c>); also the entity's <see cref="Entity.DefinitionId"/>.</param>
/// <param name="Quantity">How many, at least 1.</param>
/// <param name="Monster">The monster that dropped it.</param>
/// <param name="Owner">The player with loot priority (the top damager), or null when anyone may take it at once.</param>
/// <param name="DroppedAtMs">When it was dropped.</param>
/// <param name="OwnerUntilMs">Until when only <paramref name="Owner"/> may take it (<c>loot.priority_ms</c> after the drop).</param>
/// <param name="ExpiresAtMs">When it disappears (<c>loot.lifetime_ms</c> after the drop).</param>
public sealed record GroundItem(string ItemId, int Quantity, EntityId Monster, EntityId? Owner, long DroppedAtMs, long OwnerUntilMs, long ExpiresAtMs);

/// <summary>The loot rules as pure functions, so the pickup system (T-401) and the tests use the same answer.</summary>
public static class LootRules
{
    /// <summary>
    /// May <paramref name="picker"/> pick <paramref name="item"/> up at <paramref name="nowMs"/>? Not once it has expired; otherwise yes when it
    /// has no owner, when the picker is the owner, or when the priority time is over.
    /// </summary>
    public static bool MayPickUp(GroundItem item, EntityId picker, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (nowMs >= item.ExpiresAtMs) return false;
        return item.Owner is not { } owner || owner == picker || nowMs >= item.OwnerUntilMs;
    }
}
