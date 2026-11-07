using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Shared.World;

namespace Game.Client.MapAuthoring;

/// <summary>What a paint stroke forces on a cell's walkability.</summary>
public enum WalkPaint
{
    /// <summary>The bake decides.</summary>
    None,

    /// <summary>Walkable, whatever the bake found.</summary>
    Walkable,

    /// <summary>Not walkable, whatever the bake found.</summary>
    Blocked,
}

/// <summary>What a paint stroke forces on a cell's line of sight.</summary>
public enum LosPaint
{
    /// <summary>The bake decides.</summary>
    None,

    /// <summary>Blocks line of sight.</summary>
    Blocks,

    /// <summary>Does not block line of sight, even if the surface the ray hit says so.</summary>
    Open,
}

/// <summary>
/// The designer's painted cells, saved in the authoring scene (root metadata <c>map_overrides</c>) and applied after every bake, so a re-bake keeps them. A cell is one line,
/// <c>x,y:walk</c>, <c>x,y:block</c>, <c>x,y:los</c>, <c>x,y:open</c>, or two joined with <c>+</c> (<c>x,y:block+los</c>). Lines are always written sorted (north to south, then west to
/// east), so the order of the strokes never changes the scene or its hash.
/// </summary>
public sealed class CellOverrides
{
    private const string TermWalk = "walk";
    private const string TermBlock = "block";
    private const string TermLos = "los";
    private const string TermOpen = "open";

    private readonly SortedDictionary<(int Y, int X), (WalkPaint Walk, LosPaint Los)> _cells = new SortedDictionary<(int Y, int X), (WalkPaint Walk, LosPaint Los)>();

    /// <summary>Painted cells.</summary>
    public int Count => _cells.Count;

    /// <summary>Reads lines. Each line that is not understood adds a message to <paramref name="errors"/> and is skipped.</summary>
    public static CellOverrides Parse(IEnumerable<string> lines, ICollection<string> errors)
    {
        var result = new CellOverrides();
        foreach (var line in lines)
        {
            if (!TryParseLine(line, out var x, out var y, out var walk, out var los))
            {
                errors.Add($"override '{line}' is not 'x,y:walk|block|los|open' (two may be joined with +)");
                continue;
            }
            if (result._cells.ContainsKey((y, x))) errors.Add($"override '{line}': the cell ({x}, {y}) is painted twice");
            result._cells[(y, x)] = (walk, los);
        }
        return result;
    }

    /// <summary>The paint of a cell.</summary>
    public (WalkPaint Walk, LosPaint Los) Get(int x, int y) => _cells.TryGetValue((y, x), out var paint) ? paint : (WalkPaint.None, LosPaint.None);

    /// <summary>Paints a cell; <see cref="WalkPaint.None"/> and <see cref="LosPaint.None"/> together clear it.</summary>
    public void Set(int x, int y, WalkPaint walk, LosPaint los)
    {
        if (walk == WalkPaint.None && los == LosPaint.None) _cells.Remove((y, x));
        else _cells[(y, x)] = (walk, los);
    }

    /// <summary>The canonical lines.</summary>
    public string[] ToLines()
    {
        var lines = new string[_cells.Count];
        var i = 0;
        foreach (var cell in _cells)
        {
            var terms = cell.Value.Walk switch
            {
                WalkPaint.Walkable => TermWalk,
                WalkPaint.Blocked => TermBlock,
                _ => string.Empty,
            };
            var los = cell.Value.Los switch
            {
                LosPaint.Blocks => TermLos,
                LosPaint.Open => TermOpen,
                _ => string.Empty,
            };
            if (los.Length > 0) terms = terms.Length > 0 ? terms + "+" + los : los;
            lines[i++] = string.Create(CultureInfo.InvariantCulture, $"{cell.Key.X},{cell.Key.Y}:{terms}");
        }
        return lines;
    }

    /// <summary>The baked flags with the paint on top, as a new array. A painted cell outside the map adds a message to <paramref name="errors"/>.</summary>
    public byte[] Apply(byte[] baked, int width, int height, ICollection<string> errors)
    {
        var flags = (byte[])baked.Clone();
        foreach (var cell in _cells)
        {
            var (y, x) = cell.Key;
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                errors.Add($"override on ({x}, {y}) is outside the {width}x{height} map");
                continue;
            }
            var i = y * width + x;
            if (cell.Value.Walk == WalkPaint.Walkable) flags[i] |= CellFlagBits.Walkable;
            else if (cell.Value.Walk == WalkPaint.Blocked) flags[i] &= unchecked((byte)~CellFlagBits.Walkable);
            if (cell.Value.Los == LosPaint.Blocks) flags[i] |= CellFlagBits.BlocksLos;
            else if (cell.Value.Los == LosPaint.Open) flags[i] &= unchecked((byte)~CellFlagBits.BlocksLos);
        }
        return flags;
    }

    private static bool TryParseLine(string line, out int x, out int y, out WalkPaint walk, out LosPaint los)
    {
        x = y = 0;
        walk = WalkPaint.None;
        los = LosPaint.None;
        var colon = line.IndexOf(':');
        var comma = line.IndexOf(',');
        if (comma <= 0 || colon <= comma) return false;
        if (!int.TryParse(line.Substring(0, comma), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out x)) return false;
        if (!int.TryParse(line.Substring(comma + 1, colon - comma - 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out y)) return false;
        foreach (var term in line.Substring(colon + 1).Split('+'))
        {
            switch (term)
            {
                case TermWalk when walk == WalkPaint.None:
                    walk = WalkPaint.Walkable;
                    break;
                case TermBlock when walk == WalkPaint.None:
                    walk = WalkPaint.Blocked;
                    break;
                case TermLos when los == LosPaint.None:
                    los = LosPaint.Blocks;
                    break;
                case TermOpen when los == LosPaint.None:
                    los = LosPaint.Open;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
}
