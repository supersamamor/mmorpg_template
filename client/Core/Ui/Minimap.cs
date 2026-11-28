using System;
using System.Collections.Generic;
using Game.Client.Core.Bundle;
using Game.Client.Core.Party;
using Game.Client.Core.Quests;
using Game.Client.Core.World;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Client.Core.Ui;

/// <summary>
/// Which part of a map a minimap shows and how large (T-812): <see cref="Left"/> and <see cref="Top"/> are the world point (in cells: x east, z south, 1 cell = 1 unit) at the frame's
/// top-left corner, <see cref="CellPx"/> the size of a cell. North is up, as the fixed-yaw camera looks (ARCHITECTURE §7.2), so a mark is where the player sees it on the screen.
/// </summary>
public readonly struct MinimapFrame
{
    /// <summary>Creates a frame.</summary>
    public MinimapFrame(double left, double top, double cellPx, double widthPx, double heightPx)
    {
        Left = left;
        Top = top;
        CellPx = cellPx;
        WidthPx = widthPx;
        HeightPx = heightPx;
    }

    /// <summary>The world x at the frame's left edge (may be negative when a small map is centred).</summary>
    public double Left { get; }

    /// <summary>The world z at the frame's top edge.</summary>
    public double Top { get; }

    /// <summary>Pixels (canvas units) per cell.</summary>
    public double CellPx { get; }

    /// <summary>The frame's width.</summary>
    public double WidthPx { get; }

    /// <summary>The frame's height.</summary>
    public double HeightPx { get; }

    /// <summary>Where a world point lands in the frame, from its top-left corner.</summary>
    public (double X, double Y) ToPx(double x, double z) => ((x - Left) * CellPx, (z - Top) * CellPx);

    /// <summary>True when a world point lands inside the frame.</summary>
    public bool Shows(double x, double z)
    {
        var (px, py) = ToPx(x, z);
        return px >= 0 && py >= 0 && px <= WidthPx && py <= HeightPx;
    }

    /// <summary>The cell under a point of the frame (it may be outside the map: the server decides).</summary>
    public GridCell CellAt(double px, double py) => new GridCell((int)Math.Floor(Left + px / CellPx), (int)Math.Floor(Top + py / CellPx));

    /// <summary>
    /// The part of a <paramref name="mapWidth"/> x <paramref name="mapHeight"/> map the frame shows, in cells (x, y, w, h), clipped to the map; empty when none. The view draws that part of
    /// the picture and nothing else, so a corner map costs one texture rectangle whatever the map's size.
    /// </summary>
    public (double X, double Y, double W, double H) VisibleCells(int mapWidth, int mapHeight)
    {
        var x0 = Math.Max(0, Left);
        var y0 = Math.Max(0, Top);
        var x1 = Math.Min(mapWidth, Left + WidthPx / CellPx);
        var y1 = Math.Min(mapHeight, Top + HeightPx / CellPx);
        return x1 <= x0 || y1 <= y0 ? (0, 0, 0, 0) : (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// The corner map: a square of <paramref name="sidePx"/> showing <paramref name="windowCells"/> cells across around the player, kept inside the map; a map smaller than that is shown
    /// whole, its longer side filling the square and the shorter one centred.
    /// </summary>
    public static MinimapFrame Corner(int mapWidth, int mapHeight, double playerX, double playerZ, double sidePx, int windowCells)
    {
        var cells = Math.Max(1, Math.Min(windowCells, Math.Max(mapWidth, mapHeight)));
        double Start(int size, double at) => size <= cells ? (size - cells) / 2.0 : Math.Max(0, Math.Min(size - cells, at - cells / 2.0));
        return new MinimapFrame(Start(mapWidth, playerX), Start(mapHeight, playerZ), sidePx / cells, sidePx, sidePx);
    }

    /// <summary>The full map: the whole map, as large as fits <paramref name="widthPx"/> x <paramref name="heightPx"/> with square cells.</summary>
    public static MinimapFrame Full(int mapWidth, int mapHeight, double widthPx, double heightPx)
    {
        var cellPx = Math.Max(0.01, Math.Min(widthPx / Math.Max(1, mapWidth), heightPx / Math.Max(1, mapHeight)));
        return new MinimapFrame(0, 0, cellPx, mapWidth * cellPx, mapHeight * cellPx);
    }
}

/// <summary>What a minimap mark stands for, in drawing order (later ones on top).</summary>
public enum MinimapMarkKind
{
    /// <summary>A warp cell (display bundle).</summary>
    Warp,

    /// <summary>An NPC's place (display bundle).</summary>
    Npc,

    /// <summary>An NPC the server marks as offering a quest (the quest markers, T-602).</summary>
    QuestOffer,

    /// <summary>An NPC the server marks as taking a quest in.</summary>
    QuestTurnIn,

    /// <summary>A living monster in the area of interest.</summary>
    Monster,

    /// <summary>Another player in the area of interest.</summary>
    Player,

    /// <summary>A party member on this map (seen, or where <c>party_members</c> says).</summary>
    Party,

    /// <summary>The player, with its facing.</summary>
    Own,
}

/// <summary>One mark: what, where (world x and z, cell centres for cells), which way it faces (the player's only), and its entity id (0 for warps and NPC places).</summary>
public readonly record struct MinimapMark(MinimapMarkKind Kind, double X, double Z, double Yaw, long Id);

/// <summary>
/// The minimap's marks (T-812): only what the client already has (hard rule 11). Warps and NPC places from the display bundle, quest marks from the markers the server sends,
/// monsters and other players from the world view (the area of interest, nothing else), party members from <c>party_members</c>, the player last. No spawn areas, no routes.
/// Pure, so the tests hold the rule.
/// </summary>
public static class MinimapMarks
{
    /// <summary>
    /// Fills <paramref name="into"/> (cleared first) in drawing order. <paramref name="members"/> is the party as <see cref="PartyBook.Members"/> gives it (null or empty: no party); the
    /// caller keeps it between frames, since that property builds a new list. Index loops only: called every frame, it allocates nothing once the list has grown.
    /// </summary>
    public static void Collect(List<MinimapMark> into, MapData map, WorldView world, IReadOnlyList<PartyMemberView>? members, QuestBook? quests)
    {
        into.Clear();
        for (var i = 0; i < map.Warps.Count; i++) into.Add(new MinimapMark(MinimapMarkKind.Warp, map.Warps[i].X + 0.5, map.Warps[i].Y + 0.5, 0, 0));
        for (var i = 0; i < map.Npcs.Count; i++)
        {
            var npc = map.Npcs[i];
            var kind = quests?.MarkerFor(npc.NpcId) switch
            {
                QuestMarkers.TurnIn => MinimapMarkKind.QuestTurnIn,
                QuestMarkers.Offer => MinimapMarkKind.QuestOffer,
                _ => MinimapMarkKind.Npc,
            };
            into.Add(new MinimapMark(kind, npc.Cell.X + 0.5, npc.Cell.Y + 0.5, 0, 0));
        }
        if (world.MapId != map.Id) return;   // the world view is another map's (a warp in flight): its entities are not on this one

        var start = into.Count;
        var entities = world.Entities;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            if (entity.Id == world.OwnId || entity.Dead) continue;
            if (entity.Kind == EntityKinds.Monster) into.Add(new MinimapMark(MinimapMarkKind.Monster, entity.X, entity.Z, 0, entity.Id));
            else if (entity.Kind == EntityKinds.Player)
                into.Add(new MinimapMark(IsMember(members, entity.Id) ? MinimapMarkKind.Party : MinimapMarkKind.Player, entity.X, entity.Z, 0, entity.Id));
        }
        for (var i = 0; members is not null && i < members.Count; i++)
        {
            var member = members[i];
            if (member.Mine || !member.Known || member.Away || member.Map != map.Id || member.Cell is not { } cell || world.Find(member.Id) is not null) continue;
            into.Add(new MinimapMark(MinimapMarkKind.Party, cell.X + 0.5, cell.Y + 0.5, 0, member.Id));
        }
        // Monsters under players, players under party members: a stable sort by kind keeps the order within each kind.
        Sort(into, start);
        if (world.Own is { } own) into.Add(new MinimapMark(MinimapMarkKind.Own, own.X, own.Z, own.Yaw, own.Id));
    }

    /// <summary>The direction a mark with this yaw points on the map, as a unit (x right, y down): the camera's yaw is fixed, so north is up (ARCHITECTURE §7.2).</summary>
    public static (double X, double Y) Heading(double yaw) => (-Math.Sin(yaw), -Math.Cos(yaw));

    private static bool IsMember(IReadOnlyList<PartyMemberView>? members, long id)
    {
        for (var i = 0; members is not null && i < members.Count; i++)
            if (members[i].Id == id && !members[i].Mine) return true;
        return false;
    }

    private static void Sort(List<MinimapMark> marks, int start)
    {
        // Insertion sort: tens of marks, and stable.
        for (var i = start + 1; i < marks.Count; i++)
        {
            var mark = marks[i];
            var j = i - 1;
            while (j >= start && marks[j].Kind > mark.Kind)
            {
                marks[j + 1] = marks[j];
                j--;
            }
            marks[j + 1] = mark;
        }
    }
}
