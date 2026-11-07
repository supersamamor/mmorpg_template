using Game.Shared.Definitions;
using Game.Shared.World;

namespace Game.Server.World.Aoi;

/// <summary>
/// How the crowd cap shares its slots out by kind (<c>balance.yaml</c> <c>aoi.cap_*</c>). The default reserves nothing and pins no chaser,
/// which leaves a cap ranked by distance alone.
/// </summary>
/// <param name="Monsters">Slots kept for the nearest monsters, while there are that many.</param>
/// <param name="Npcs">Slots kept for the nearest NPCs.</param>
/// <param name="Items">Slots kept for the nearest ground items.</param>
/// <param name="Margin">Ranks an entity already shown may slip past its kind's reservation (or past the chaser limit) and keep its slot.</param>
/// <param name="Chasers">How many monsters chasing the observer, nearest first, are always visible before they have hit it.</param>
public readonly record struct CapReservations(int Monsters = 0, int Npcs = 0, int Items = 0, int Margin = 0, int Chasers = 0)
{
    /// <summary>The numbers of <c>balance.yaml</c> <c>aoi</c>.</summary>
    public static CapReservations From(AoiBalanceDef aoi) =>
        new(aoi.CapReservedMonsters, aoi.CapReservedNpcs, aoi.CapReservedItems, aoi.CapReservedMargin, aoi.CapPinnedChasers);
}

/// <summary>
/// The crowd cap (ARCHITECTURE §3): cuts an observer's window down to at most <c>aoi.max_visible</c> entities, in four steps:
/// <list type="number">
/// <item>Some are always visible: see <see cref="AlwaysVisible"/>, and the nearest monsters chasing the observer, up to <c>cap_pinned_chasers</c>.</item>
/// <item>Each of monsters, NPCs and ground items then gets its reservation, filled with its nearest. What is already shown of that kind counts
/// toward it. A reservation has no more claim than there are entities of the kind, so unused slots stay with the others.</item>
/// <item>The remaining slots go nearest first, whatever the kind.</item>
/// <item>An entity already shown holds on for longer than a new one gets in: <c>cap_reserved_margin</c> ranks past its kind's reservation,
/// and <c>cap_hysteresis</c> ranks past the cutoff of the remaining slots. So nothing at either edge flickers.</item>
/// </list>
/// Rank is Chebyshev distance from the observer, then entity id, so the result depends only on the world. One exception: a ground item
/// another player has the loot priority on ranks behind everything else, so the observer's own and free loot goes first.
/// </summary>
/// <remarks>
/// <para>An observer becomes <em>capped</em> when its window first holds more than <c>max_visible</c> and stops being capped when it
/// holds no more than the cutoff (<c>max_visible - cap_hysteresis</c>). Until then it sees its whole window, so a view that never reaches
/// the cap is untouched, and a crowd hovering around the cap cannot flicker its farthest member.</para>
/// <para>Always-visible entities count toward the cap but are never cut, so more of them than <c>max_visible</c> go over it. A train is bounded:
/// chasing alone pins at most <c>cap_pinned_chasers + cap_reserved_margin</c> monsters; the others rank like any monster until they land a hit.
/// That also means the only monster that can hit from out of view is one with that many chasers nearer to the observer, and it arrives
/// in the same delta as its hit. Attackers themselves have no bound (T-215), and monsters do not block each other, so a pile of them can go over the cap.</para>
/// <para>Cost: O(window), by partial selection, and only for an observer whose window is over the cap. No allocation after the buffers
/// have grown to the largest window seen. Single-threaded, like its instance.</para>
/// </remarks>
internal sealed class VisibleCap
{
    private readonly int _maxVisible;
    private readonly int _cutoff;
    private readonly CapReservations _reservations;
    private readonly RankedList _chasers = new();
    private readonly RankedList _monsters = new();
    private readonly RankedList _npcs = new();
    private readonly RankedList _items = new();
    private readonly RankedList _open = new();      // competes for the remaining slots
    private int _enterRoom;                         // slots left that a new entity can take
    private int _stayRoom;                          // slots left for an entity already shown

    public VisibleCap(int maxVisible, int hysteresis, CapReservations reservations = default)
    {
        _maxVisible = maxVisible;
        _cutoff = maxVisible - hysteresis;
        _reservations = reservations;
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

        int pinned = 0, pinnedMonsters = 0, pinnedNpcs = 0;
        for (var i = 0; i < count; i++)
        {
            var candidate = visible[i];
            if (AlwaysVisible(observer, candidate))
            {
                visible[pinned++] = candidate;
                Count(candidate, ref pinnedMonsters, ref pinnedNpcs);
                continue;
            }
            var ranked = new Ranked(GridDistance.Chebyshev(observer.Cell, candidate.Cell), candidate, BehindOthers(observer, candidate));
            if (_reservations.Chasers > 0 && Chasing(observer, candidate)) _chasers.Add(ranked);
            else ListOf(candidate.Kind).Add(ranked);
        }
        visible.RemoveRange(pinned, count - pinned);

        PinChasers(state, visible, ref pinnedMonsters, ref pinnedNpcs);

        _enterRoom = Math.Max(0, _cutoff - visible.Count);
        _stayRoom = Math.Max(0, _maxVisible - visible.Count);
        Reserve(_npcs, _reservations.Npcs, pinnedNpcs, state, visible);
        Reserve(_monsters, _reservations.Monsters, pinnedMonsters, state, visible);
        Reserve(_items, _reservations.Items, 0, state, visible);

        // Partial selection, twice: first who may be shown at all, then among those who gets in without having been shown already.
        var open = _open;
        var stay = Math.Min(open.Count, _stayRoom);
        var enter = Math.Min(stay, _enterRoom);
        SelectSmallest(open.Items, open.Count, stay);
        SelectSmallest(open.Items, stay, enter);
        for (var i = 0; i < enter; i++) visible.Add(open.Items[i].Entry);
        for (var i = enter; i < stay; i++)
            if (AoiSystem.Sees(state.Known, open.Items[i].Entry.Id)) visible.Add(open.Items[i].Entry);

        // Do not keep entries of entities that may leave.
        _chasers.Clear();
        _monsters.Clear();
        _npcs.Clear();
        _items.Clear();
        _open.Clear();
    }

    /// <summary>
    /// Pins the nearest <c>cap_pinned_chasers</c> chasers, and those shown already up to <c>cap_reserved_margin</c> ranks further. A chaser past
    /// that goes back to the monsters, where it ranks like any other.
    /// </summary>
    private void PinChasers(ObserverState state, List<AoiEntry> visible, ref int pinnedMonsters, ref int pinnedNpcs)
    {
        var chasers = _chasers;
        if (chasers.Count == 0) return;
        var keep = Math.Min(chasers.Count, _reservations.Chasers + _reservations.Margin);
        var pin = Math.Min(keep, _reservations.Chasers);
        SelectSmallest(chasers.Items, chasers.Count, keep);
        SelectSmallest(chasers.Items, keep, pin);
        for (var i = 0; i < chasers.Count; i++)
        {
            var entry = chasers.Items[i].Entry;
            if (i < pin || (i < keep && AoiSystem.Sees(state.Known, entry.Id)))
            {
                visible.Add(entry);
                Count(entry, ref pinnedMonsters, ref pinnedNpcs);
            }
            else
            {
                ListOf(entry.Kind).Add(chasers.Items[i]);
            }
        }
    }

    /// <summary>
    /// Gives one kind its reservation less what is already pinned of it: new entities get in up to the reservation, those shown already up to
    /// <c>cap_reserved_margin</c> ranks further. All the others of the kind go on to compete for the remaining slots.
    /// </summary>
    private void Reserve(RankedList kind, int reserved, int pinned, ObserverState state, List<AoiEntry> visible)
    {
        if (reserved <= 0)
        {
            _open.AddRange(kind);
            return;
        }
        var keep = Math.Min(kind.Count, Math.Min(_stayRoom, Math.Max(0, reserved + _reservations.Margin - pinned)));
        var enter = Math.Min(keep, Math.Min(_enterRoom, Math.Max(0, reserved - pinned)));
        SelectSmallest(kind.Items, kind.Count, keep);
        SelectSmallest(kind.Items, keep, enter);
        var taken = enter;
        for (var i = 0; i < kind.Count; i++)
        {
            ref readonly var ranked = ref kind.Items[i];
            if (i < enter) visible.Add(ranked.Entry);
            else if (i < keep && AoiSystem.Sees(state.Known, ranked.Entry.Id))
            {
                visible.Add(ranked.Entry);
                taken++;
            }
            else
            {
                _open.Add(ranked);
            }
        }
        _enterRoom = Math.Max(0, _enterRoom - taken);
        _stayRoom -= taken;
    }

    private RankedList ListOf(EntityKind kind) => kind switch
    {
        EntityKind.Monster => _monsters,
        EntityKind.Npc => _npcs,
        EntityKind.Item => _items,
        _ => _open,
    };

    private static void Count(AoiEntry entry, ref int monsters, ref int npcs)
    {
        if (entry.Kind == EntityKind.Monster) monsters++;
        else if (entry.Kind == EntityKind.Npc) npcs++;
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

    /// <summary>A living monster that has taken this observer as its target and has not hit it yet.</summary>
    private static bool Chasing(AoiEntry observer, AoiEntry candidate) => candidate.ChasingId == observer.Id && !candidate.Entity.Dead;

    /// <summary>A ground item this observer may not pick up yet: <see cref="AoiEntry.LootOwner"/> is set only while the priority lasts.</summary>
    private static bool BehindOthers(AoiEntry observer, AoiEntry candidate) =>
        candidate.LootOwner is { } owner && owner.Value != observer.Id;

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

    /// <summary>A candidate and its rank key: anything ahead of what is <paramref name="behind"/>, then nearer first, then lower id.</summary>
    internal readonly struct Ranked(int distance, AoiEntry entry, bool behind = false)
    {
        public readonly bool Behind = behind;
        public readonly int Distance = distance;
        public readonly long Id = entry.Id;
        public readonly AoiEntry Entry = entry;

        public bool IsBefore(in Ranked other)
        {
            if (Behind != other.Behind) return other.Behind;
            return Distance != other.Distance ? Distance < other.Distance : Id < other.Id;
        }
    }

    /// <summary>A reusable array of candidates. It grows to the largest window seen and never shrinks.</summary>
    private sealed class RankedList
    {
        public Ranked[] Items = new Ranked[64];
        public int Count;

        public void Add(in Ranked ranked)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
            Items[Count++] = ranked;
        }

        public void AddRange(RankedList other)
        {
            for (var i = 0; i < other.Count; i++) Add(other.Items[i]);
        }

        public void Clear()
        {
            Array.Clear(Items, 0, Count);
            Count = 0;
        }
    }
}
