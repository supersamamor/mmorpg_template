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
    internal MapData(string id, string name, string sceneKey, int width, int height, byte[] cells, IReadOnlyList<GridCell> warps, IReadOnlyList<MapZone> zones,
        string? musicKey = null, string? ambienceKey = null)
    {
        MusicKey = musicKey;
        AmbienceKey = ambienceKey;
        Id = id;
        Name = name;
        SceneKey = sceneKey;
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

    /// <summary>The asset key of its visual scene.</summary>
    public string SceneKey { get; }

    /// <summary>The asset key of its music, or null.</summary>
    public string? MusicKey { get; }

    /// <summary>The asset key of its ambience loop, or null.</summary>
    public string? AmbienceKey { get; }

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
    private readonly Dictionary<string, string?[]> _monsterSounds = new Dictionary<string, string?[]>(StringComparer.Ordinal);
    private readonly Dictionary<string, string?[]> _itemSounds = new Dictionary<string, string?[]>(StringComparer.Ordinal);
    private readonly Dictionary<string, string?[]> _skillSounds = new Dictionary<string, string?[]>(StringComparer.Ordinal);
    private readonly SortedSet<string> _soundKeys = new SortedSet<string>(StringComparer.Ordinal);
    private readonly SortedSet<string> _modelKeys = new SortedSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, MapData> _maps = new Dictionary<string, MapData>(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemDisplay> _items = new Dictionary<string, ItemDisplay>(StringComparer.Ordinal);
    private readonly List<RefineStep> _refineSteps = new List<RefineStep>();

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

    /// <summary>True when the bundle has a definition with this id.</summary>
    public bool IsKnownId(string id) => _names.ContainsKey(id);

    /// <summary>The model asset key a view record's <c>display_key</c> shows as (a job's body, a monster's or NPC's model, an item's ground visual), or null.</summary>
    public string? ModelKeyOf(string? displayKey) => displayKey is not null && _models.TryGetValue(displayKey, out var key) ? key : null;

    /// <summary>Every distinct model key the bundle names, in order (for preloading).</summary>
    public IReadOnlyCollection<string> ModelKeys => _modelKeys;

    /// <summary>Every distinct sound key the bundle names (monster, item, skill and map sounds), for preloading.</summary>
    public IReadOnlyCollection<string> SoundKeys => _soundKeys;

    /// <summary>The sound key content gave a monster for <paramref name="slot"/> (0 aggro, 1 hit, 2 die), or null.</summary>
    public string? MonsterSound(string? displayKey, int slot) => displayKey is not null && _monsterSounds.TryGetValue(displayKey, out var keys) ? keys[slot] : null;

    /// <summary>The use sound of an item (0) or its equip sound (1), or null.</summary>
    public string? ItemSound(string? itemId, int slot) => itemId is not null && _itemSounds.TryGetValue(itemId, out var keys) ? keys[slot] : null;

    /// <summary>A skill's sound key, or null.</summary>
    public string? SkillSound(string? skillId) => skillId is not null && _skillSounds.TryGetValue(skillId, out var keys) ? keys[0] : null;

    /// <summary>A map's display data.</summary>
    public bool TryGetMap(string id, out MapData? map) => _maps.TryGetValue(id, out map);

    /// <summary>What the bundle says about an item (kind, icon, slots, weapon type, worn visual), or null for an id it does not know.</summary>
    public ItemDisplay? Item(string? itemId) => itemId is not null && _items.TryGetValue(itemId, out var item) ? item : null;

    /// <summary>The highest refine level (<c>refine.yaml</c>); 0 when nothing refines.</summary>
    public int RefineMaxLevel { get; private set; }

    /// <summary>The refine step that reaches <paramref name="level"/> (chance, material, failure outcome), or null when there is none.</summary>
    public RefineStep? RefineStepTo(int level) => level >= 1 && level <= _refineSteps.Count && level <= RefineMaxLevel ? _refineSteps[level - 1] : null;

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
                    ReadSounds(result, list, id, entry);
                    if (list == "items") result._items[id] = ReadItem(id, result._names[id], entry);
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
                if (data.MusicKey is not null) result._soundKeys.Add(data.MusicKey);
                if (data.AmbienceKey is not null) result._soundKeys.Add(data.AmbienceKey);
                result._maps[data.Id] = data;
                result._names[data.Id] = data.Name;
            }
            ReadRefine(result, root);
            bundle = result;
            return BundleStatus.Ok;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return BundleStatus.Corrupt;
        }
    }

    private static void ReadSounds(DisplayBundle bundle, string list, string id, JsonElement entry)
    {
        string?[]? keys = null;
        if (list == "monsters" && entry.TryGetProperty("sounds", out var sounds) && sounds.ValueKind == JsonValueKind.Object)
        {
            keys = new[] { OptionalText(sounds, "aggro"), OptionalText(sounds, "hit"), OptionalText(sounds, "die") };
        }
        else if (list == "items")
        {
            keys = new[] { OptionalText(entry, "use_sfx"), OptionalText(entry, "equip_sfx") };
        }
        else if (list == "skills")
        {
            keys = new[] { OptionalText(entry, "sfx") };
        }
        if (keys is null) return;
        foreach (var key in keys)
            if (key is not null) bundle._soundKeys.Add(key);
        (list == "monsters" ? bundle._monsterSounds : list == "items" ? bundle._itemSounds : bundle._skillSounds)[id] = keys;
    }

    private static ItemDisplay ReadItem(string id, string name, JsonElement entry)
    {
        var slots = new List<string>();
        if (entry.TryGetProperty("slots", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var slot in list.EnumerateArray())
                if (slot.GetString() is { Length: > 0 } text) slots.Add(text);
        return new ItemDisplay(id, name, OptionalText(entry, "kind") ?? string.Empty, OptionalText(entry, "icon"), slots, OptionalText(entry, "weapon_type"),
            OptionalText(entry, "visual"))
        {
            CardSlots = entry.TryGetProperty("card_slots", out var sockets) && sockets.ValueKind == JsonValueKind.Number ? Math.Max(0, sockets.GetInt32()) : 0,
            Refinable = entry.TryGetProperty("refinable", out var refinable) && refinable.ValueKind == JsonValueKind.True,
        };
    }

    private static void ReadRefine(DisplayBundle bundle, JsonElement root)
    {
        if (!root.TryGetProperty("refine", out var refine)) return;   // no refine steps: nothing is offered for refining
        bundle.RefineMaxLevel = refine.GetProperty("max_level").GetInt32();
        foreach (var level in refine.GetProperty("levels").EnumerateArray())
            bundle._refineSteps.Add(new RefineStep(level.GetProperty("level").GetInt32(), level.GetProperty("success_percent").GetInt32(), Text(level, "material"),
                level.GetProperty("material_quantity").GetInt32(), Text(level, "failure")));
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
        return new MapData(Text(map, "id"), Text(map, "name"), Text(map, "scene"), width, height, cells, warps, zones, OptionalText(map, "music"), OptionalText(map, "ambience"));
    }

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? throw new FormatException(name);
}

/// <summary>An item as the display bundle describes it (ARCHITECTURE §5.4): never weight, stats, effects or prices.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">An "Item kinds" term.</param>
/// <param name="Icon">The icon's asset key, or null.</param>
/// <param name="Slots">Equipment slots it fits (card slots for a card).</param>
/// <param name="WeaponType">A weapon's "Weapon types" term, or null.</param>
/// <param name="Visual">The worn visual's asset key (T-403 draws it), or null.</param>
public sealed record ItemDisplay(string Id, string Name, string Kind, string? Icon, IReadOnlyList<string> Slots, string? WeaponType, string? Visual)
{
    /// <summary>How many cards the equipment holds (0 for anything else).</summary>
    public int CardSlots { get; init; }

    /// <summary>True when the equipment can be refined.</summary>
    public bool Refinable { get; init; }
}

/// <summary>One refine step as the bundle shows it (ARCHITECTURE §5.4): what a player needs to decide, never more.</summary>
/// <param name="Level">The level it reaches.</param>
/// <param name="SuccessPercent">Chance of success in whole percent.</param>
/// <param name="Material">The material item id it uses up.</param>
/// <param name="Quantity">How many units of it.</param>
/// <param name="Failure">A "Refine failures" term: <c>keep</c>, <c>down</c> or <c>break</c>.</param>
public sealed record RefineStep(int Level, int SuccessPercent, string Material, int Quantity, string Failure);
