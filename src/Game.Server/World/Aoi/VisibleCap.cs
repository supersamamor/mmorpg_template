using Game.Shared.World;

namespace Game.Server.World.Aoi;

/// <summary>
/// The crowd cap (ARCHITECTURE §3): cuts an observer's window down to at most <c>aoi.max_visible</c> entities. Some are
/// <see cref="AlwaysVisible">always visible</see>; the rest are ranked by Chebyshev distance from the observer, then by entity id, so
/// the result depends only on the world. An entity enters at rank <c>max_visible - cap_hysteresis</c> or better (the cutoff) and, once
/// shown, stays until it ranks past <c>max_visible</c> or leaves the window. So anything near is always shown, the hard cap is never
/// passed, and an entity sitting at the cutoff does not flicker.
/// </summary>
/// <remarks>
/// <para>An observer becomes <em>capped</em> when its window first holds more than <c>max_visible</c> and stops being capped when it
/// holds no more than the cutoff. Until then it sees its whole window, so a view that never reaches the cap is untouched, and a crowd
/// hovering around the cap cannot flicker its farthest member.</para>
/// <para>Always-visible entities count toward the cap but are never cut, so more of them than <c>max_visible</c> go over it. Melee
/// attackers are limited by the 8 cells around the observer, so that takes a very large number of ranged attackers.</para>
/// <para>Cost: O(window), by partial selection, and only for an observer whose window is over the cap. No allocation after the buffer
/// has grown to the largest window seen. Single-threaded, like its instance.</para>
/// </remarks>
internal sealed class VisibleCap
{
    private readonly int _maxVisible;
    private readonly int _cutoff;
    private Ranked[] _rest = new Ranked[64];

    public VisibleCap(int maxVisible, int hysteresis)
    {
        _maxVisible = maxVisible;
        _cutoff = maxVisible - hysteresis;
    }

    /// <summary>
    /// Reduces <paramref name="visible"/>, which holds the observer's whole window in any order, to what the observer sees this tick.
    /// <see cref="ObserverState.Known"/> must still be what it saw last, sorted by id. The result is in no particular order.
    /// </summary>
    public void Apply(AoiEntry observer, ObserverState state, List<AoiEntry> visible)
    {
        var count = visible.Count;
        if (count <= _cutoff)
        {
            state.Capped = false;
            return;
        }
        if (count <= _maxVisible && !state.Capped) return;
        state.Capped = true;

        if (_rest.Length < count) _rest = new Ranked[Math.Max(count, _rest.Length * 2)];
        var rest = _rest;
        int pinned = 0, others = 0;
        for (var i = 0; i < count; i++)
        {
            var candidate = visible[i];
            if (AlwaysVisible(observer, candidate)) visible[pinned++] = candidate;
            else rest[others++] = new Ranked(GridDistance.Chebyshev(observer.Cell, candidate.Cell), candidate);
        }
        visible.RemoveRange(pinned, count - pinned);

        // Partial selection, twice: first who may be shown at all, then among those who gets in without having been shown already.
        var stay = Math.Min(others, Math.Max(0, _maxVisible - pinned));
        var enter = Math.Min(stay, Math.Max(0, _cutoff - pinned));
        SelectSmallest(rest, others, stay);
        SelectSmallest(rest, stay, enter);
        for (var i = 0; i < enter; i++) visible.Add(rest[i].Entry);
        for (var i = enter; i < stay; i++)
            if (AoiSystem.Sees(state.Known, rest[i].Entry.Id)) visible.Add(rest[i].Entry);
        Array.Clear(rest, 0, others);   // do not keep entries of entities that may leave
    }

    /// <summary>
    /// Never cut: the observer itself, its current attack target, and any living entity attacking it.
    /// </summary>
    /// <remarks>
    /// Party members (no party system yet) belong here. Whoever adds them must also touch the observer's bucket when its party changes,
    /// as <see cref="AoiSystem"/> does when an attack link changes: the fast path otherwise would not notice until something moves.
    /// </remarks>
    private static bool AlwaysVisible(AoiEntry observer, AoiEntry candidate) =>
        ReferenceEquals(candidate, observer)
        || candidate.Id == observer.AttackingId
        || (candidate.AttackingId == observer.Id && !candidate.Entity.Dead);

    /// <summary>
    /// Quickselect: reorders the first <paramref name="count"/> items so the <paramref name="k"/> smallest come first, in no particular
    /// order. Which items those are depends only on the keys, which are unique, never on the pivots, so this is deterministic.
    /// </summary>
    internal static void SelectSmallest(Ranked[] items, int count, int k)
    {
        if (k <= 0 || k >= count) return;
        var last = k - 1;
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            var pivot = MedianOfThree(items[lo], items[lo + ((hi - lo) >> 1)], items[hi]);
            int i = lo, j = hi;
            while (i <= j)
            {
                while (items[i].IsBefore(pivot)) i++;
                while (pivot.IsBefore(items[j])) j--;
                if (i > j) break;
                (items[i], items[j]) = (items[j], items[i]);
                i++;
                j--;
            }
            if (last <= j) hi = j;
            else if (last >= i) lo = i;
            else return;
        }
    }

    private static Ranked MedianOfThree(in Ranked a, in Ranked b, in Ranked c)
    {
        if (a.IsBefore(b)) return b.IsBefore(c) ? b : a.IsBefore(c) ? c : a;
        return a.IsBefore(c) ? a : b.IsBefore(c) ? c : b;
    }

    /// <summary>A candidate and its rank key: nearer first, then lower id.</summary>
    internal readonly struct Ranked(int distance, AoiEntry entry)
    {
        public readonly int Distance = distance;
        public readonly long Id = entry.Id;
        public readonly AoiEntry Entry = entry;

        public bool IsBefore(in Ranked other) => Distance != other.Distance ? Distance < other.Distance : Id < other.Id;
    }
}
