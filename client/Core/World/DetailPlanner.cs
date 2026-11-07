using System;

namespace Game.Client.Core.World;

/// <summary>
/// Decides, from distance to the player, which animated characters get the full level of detail and the full animation rate (ARCHITECTURE §9): the
/// nearest <c>lod0Count</c> are LOD0, the rest LOD1; the nearest <c>fullRateCount</c> animate every frame, the rest at a reduced rate. Ties go to the
/// lower id, so the choice never flickers between equals. Reuses its arrays: planning allocates nothing once warm.
/// </summary>
public sealed class DetailPlanner
{
    private readonly int _lod0Count;
    private readonly int _fullRateCount;
    private double[] _distance = new double[64];
    private long[] _id = new long[64];
    private int[] _order = new int[64];
    private int[] _rank = new int[64];
    private int _count;

    /// <summary>Creates a planner with the two limits.</summary>
    public DetailPlanner(int lod0Count, int fullRateCount)
    {
        _lod0Count = lod0Count;
        _fullRateCount = fullRateCount;
    }

    /// <summary>Characters added so far in this round.</summary>
    public int Count => _count;

    /// <summary>Starts a round.</summary>
    public void Begin() => _count = 0;

    /// <summary>Adds one character at squared distance <paramref name="distanceSquared"/>; returns its slot, to ask about after <see cref="Plan"/>.</summary>
    public int Add(long id, double distanceSquared)
    {
        if (_count == _distance.Length)
        {
            var size = _count * 2;
            Array.Resize(ref _distance, size);
            Array.Resize(ref _id, size);
            Array.Resize(ref _order, size);
            Array.Resize(ref _rank, size);
        }
        _distance[_count] = distanceSquared;
        _id[_count] = id;
        return _count++;
    }

    /// <summary>Ranks everything added.</summary>
    public void Plan()
    {
        // Insertion sort: tens of entries, nearly sorted from one round to the next, and no allocation.
        for (var i = 0; i < _count; i++)
        {
            var slot = i;
            var at = i;
            while (at > 0 && Before(slot, _order[at - 1]))
            {
                _order[at] = _order[at - 1];
                at--;
            }
            _order[at] = slot;
        }
        for (var rank = 0; rank < _count; rank++) _rank[_order[rank]] = rank;
    }

    /// <summary>The level of detail of a slot: 0 for the nearest few, 1 for the rest.</summary>
    public int LodOf(int slot) => _rank[slot] < _lod0Count ? 0 : 1;

    /// <summary>True when a slot animates every frame.</summary>
    public bool FullRate(int slot) => _rank[slot] < _fullRateCount;

    private bool Before(int a, int b) => _distance[a] < _distance[b] || (_distance[a] == _distance[b] && _id[a] < _id[b]);
}
