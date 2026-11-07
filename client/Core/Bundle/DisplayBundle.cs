using System;
using System.Collections.Generic;
using System.Text.Json;
using Game.Shared.ClientContent;
using Game.Shared.World;

namespace Game.Client.Core.Bundle;

/// <summary>How reading a bundle ended.</summary>
public enum BundleStatus
{
    /// <summary>Read.</summary>
    Ok,

    /// <summary>A <c>format_version</c> this build does not know: refuse it (ARCHITECTURE §5.4).</summary>
    UnknownFormat,

    /// <summary>Not a bundle: unreadable JSON or missing fields.</summary>
    Corrupt,
}

/// <summary>One zone rectangle of a map.</summary>
public readonly struct MapZone
{
    /// <summary>Creates a zone.</summary>
    public MapZone(string kind, GridRect area)
    {
        Kind = kind;
        Area = area;
    }

    /// <summary>A <c>ZoneKinds</c> term.</summary>
    public string Kind { get; }

    /// <summary>Its cells.</summary>
    public GridRect Area { get; }
}

/// <summary>A map's display data: size, cell flags and the cells worth showing.</summary>
public sealed class MapData
{
    internal MapData(string id, string name, int width, int height, byte[] cells, IReadOnlyList<GridCell> warps, IReadOnlyList<MapZone> zones)
    {
        Id = id;
        Name = name;
        Width = width;
        Height = height;
        Cells = cells;
        Warps = warps;
        Zones = zones;
    }

    /// <summary>Map id.</summary>
    public string Id { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Width in cells.</summary>
    public int Width { get; }

    /// <summary>Height in cells.</summary>
    public int Height { get; }

    /// <summary>One flag byte per cell, row-major.</summary>
    public byte[] Cells { get; }

    /// <summary>Warp cells.</summary>
    public IReadOnlyList<GridCell> Warps { get; }

    /// <summary>Zones.</summary>
    public IReadOnlyList<MapZone> Zones { get; }

    /// <summary>True for a walkable cell inside the map. For drawing only: the server decides where anyone can walk.</summary>
    public bool IsWalkable(int x, int y) => Inside(x, y) && (Cells[y * Width + x] & CellFlagBits.Walkable) != 0;

    /// <summary>True for a cell that blocks line of sight; anything outside the map does.</summary>
    public bool BlocksLos(int x, int y) => !Inside(x, y) || (Cells[y * Width + x] & CellFlagBits.BlocksLos) != 0;

    /// <summary>True when the cell is inside the map.</summary>
    public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
}

/// <summary>
/// The packed client bundle (ARCHITECTURE §5.4): the part of it this client shows so far. Parsed with <see cref="JsonDocument"/>, with no
/// reflection, so it survives trimming and AOT. Fields a later task needs are read by that task.
/// </summary>
public sealed class DisplayBundle
{
    // Definition lists whose entries are display names: a view record's display_key is an id from one of these.
    private static readonly string[] NamedLists = { "items", "skills", "jobs", "statuses", "monsters", "npcs", "quests" };

    private readonly Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _models = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly SortedSet<string> _modelKeys = new SortedSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, MapData> _maps = new Dictionary<string, MapData>(StringComparer.Ordinal);

    private DisplayBundle(int formatVersion, string contentHash)
    {
        FormatVersion = formatVersion;
        ContentHash = contentHash;
    }

    /// <summary>The format version.</summary>
    public int FormatVersion { get; }

    /// <summary>The content hash, sent as <c>content</c> in <c>hello</c>.</summary>
    public string ContentHash { get; }

    /// <summary>The display name of a content id; the id itself if the bundle has none (never empty for a real key).</summary>
    public string NameOf(string? displayKey)
    {
        if (displayKey is null) return string.Empty;
        return _names.TryGetValue(displayKey, out var name) ? name : displayKey;
    }

    /// <summary>The model asset key a view record's <c>display_key</c> shows as (a job's body, a monster's or NPC's model, an item's ground visual), or null.</summary>
    public string? ModelKeyOf(string? displayKey) => displayKey is not null && _models.TryGetValue(displayKey, out var key) ? key : null;

    /// <summary>Every distinct model key the bundle names, in order (for preloading).</summary>
    public IReadOnlyCollection<string> ModelKeys => _modelKeys;

    /// <summary>A map's display data.</summary>
    public bool TryGetMap(string id, out MapData? map) => _maps.TryGetValue(id, out map);

    /// <summary>Reads a bundle file. The format version is checked first, so a newer shape is refused rather than misread.</summary>
    public static BundleStatus TryRead(ReadOnlySpan<byte> utf8, out DisplayBundle? bundle, out int formatVersion)
    {
        bundle = null;
        formatVersion = 0;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format_version", out var version) || !version.TryGetInt32(out formatVersion))
                return BundleStatus.Corrupt;
            if (formatVersion != ClientBundle.CurrentFormatVersion) return BundleStatus.UnknownFormat;

            var result = new DisplayBundle(formatVersion, root.GetProperty("content_hash").GetString() ?? throw new FormatException());
            foreach (var list in NamedLists)
            {
                // What an entity of this id looks like: a ground item's "ground" visual, or the "model" of a job (a player), monster or NPC.
                var modelField = list == "items" ? "ground" : "model";
                foreach (var entry in root.GetProperty(list).EnumerateArray())
                {
                    var id = Text(entry, "id");
                    result._names[id] = Text(entry, "name");
                    if (entry.TryGetProperty(modelField, out var model) && model.ValueKind == JsonValueKind.String && model.GetString() is { Length: > 0 } key)
                    {
                        result._models[id] = key;
                        result._modelKeys.Add(key);
                    }
                }
            }
            foreach (var map in root.GetProperty("maps").EnumerateArray())
            {
                var data = ReadMap(map);
                result._maps[data.Id] = data;
                result._names[data.Id] = data.Name;
            }
            bundle = result;
            return BundleStatus.Ok;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return BundleStatus.Corrupt;
        }
    }

    private static MapData ReadMap(JsonElement map)
    {
        var width = map.GetProperty("width").GetInt32();
        var height = map.GetProperty("height").GetInt32();
        var cells = Convert.FromBase64String(Text(map, "cells"));
        if (width <= 0 || height <= 0 || cells.Length != width * height) throw new FormatException("map size and cells differ");
        var warps = new List<GridCell>();
        foreach (var warp in map.GetProperty("warps").EnumerateArray())
        {
            var cell = warp.GetProperty("cell");
            warps.Add(new GridCell(cell[0].GetInt32(), cell[1].GetInt32()));
        }
        var zones = new List<MapZone>();
        foreach (var zone in map.GetProperty("zones").EnumerateArray())
        {
            var area = zone.GetProperty("area");
            zones.Add(new MapZone(Text(zone, "kind"), new GridRect(area[0].GetInt32(), area[1].GetInt32(), area[2].GetInt32(), area[3].GetInt32())));
        }
        return new MapData(Text(map, "id"), Text(map, "name"), width, height, cells, warps, zones);
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new FormatException(name);
}
