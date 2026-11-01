using Game.Shared.Definitions;
using Game.Shared.Random;
using Game.Shared.World;

namespace Game.Server.World.Combat;

/// <summary>One rolled drop: which item and how many.</summary>
public readonly record struct RolledDrop(string ItemId, int Quantity);

/// <summary>
/// Drop rolling and placement as pure functions over an injected <see cref="IRandom"/>, so a seed replays the same drops in the same places.
/// </summary>
public static class DropRoller
{
    /// <summary>
    /// Rolls one table into <paramref name="into"/>: every entry independently, in table order, with one <see cref="IRandom.NextDouble"/>
    /// against <c>chance</c> (so 1 always drops and 0 never does), then, when it dropped and <c>max</c> is above <c>min</c>, one
    /// <see cref="IRandom.Next"/> for a quantity uniform in <c>min..max</c>. A quantity below 1 is raised to 1. Allocates nothing once
    /// <paramref name="into"/> has grown.
    /// </summary>
    public static void Roll(DropTableDef table, IRandom random, List<RolledDrop> into)
    {
        var entries = table.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!(random.NextDouble() < entry.Chance)) continue;
            var min = Math.Max(1, entry.Min);
            var max = Math.Max(min, entry.Max);
            var quantity = max > min ? min + random.Next(max - min + 1) : min;
            into.Add(new RolledDrop(entry.ItemId, quantity));
        }
    }

    /// <summary>
    /// A walkable cell within <paramref name="radius"/> (Chebyshev) of <paramref name="center"/>, picked uniformly among the walkable cells of
    /// that square (the center included), with one <see cref="IRandom.Next"/>. False when none is walkable. Allocates nothing.
    /// </summary>
    public static bool TryPlace(MapGrid grid, GridCell center, int radius, IRandom random, out GridCell cell)
    {
        radius = Math.Max(0, radius);
        var count = 0;
        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        for (var x = center.X - radius; x <= center.X + radius; x++)
            if (grid.IsWalkable(x, y)) count++;
        cell = center;
        if (count == 0) return false;

        var pick = count == 1 ? 0 : random.Next(count);
        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        for (var x = center.X - radius; x <= center.X + radius; x++)
        {
            if (!grid.IsWalkable(x, y)) continue;
            if (pick-- == 0)
            {
                cell = new GridCell(x, y);
                return true;
            }
        }
        return false;   // not reached
    }
}
