using Game.Content.Loading;
using Game.Content.Validation;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.World;

/// <summary>Grid loading and validation against a temp copy of the sample content, one broken thing per test.</summary>
public class GridContentTests : IDisposable
{
    private const string Meadow = "maps/starter_meadow.grid.json";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "grid_" + Guid.NewGuid().ToString("N"));

    public GridContentTests() => CopyDir(Path.Combine(RepoRoot(), "content"), _temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private string PathOf(string rel) => Path.Combine(_temp, rel.Replace('/', Path.DirectorySeparatorChar));

    private MapGridFile ReadGrid(string rel = Meadow) => GridFileJson.Read(File.ReadAllText(PathOf(rel)));

    private void EditGrid(Func<MapGridFile, MapGridFile> edit, string rel = Meadow) =>
        File.WriteAllText(PathOf(rel), GridFileJson.Write(edit(ReadGrid(rel))));

    private void EditText(string rel, string from, string to)
    {
        var text = File.ReadAllText(PathOf(rel)).Replace("\r\n", "\n");
        text.ShouldContain(from);
        File.WriteAllText(PathOf(rel), text.Replace(from, to));
    }

    private static MapGridFile SetCell(MapGridFile g, int x, int y, byte value)
    {
        var cells = Convert.FromBase64String(g.Cells);
        cells[y * g.Width + x] = value;
        return g with { Cells = Convert.ToBase64String(cells) };
    }

    private static List<T> Replace<T>(IReadOnlyList<T> list, int index, Func<T, T> change)
    {
        var copy = list.ToList();
        copy[index] = change(copy[index]);
        return copy;
    }

    private IReadOnlyList<ContentError> LoadErrors() => ContentLoader.Load(_temp).Errors;

    private ValidationReport Validate()
    {
        var load = ContentLoader.Load(_temp);
        load.Errors.ShouldBeEmpty(string.Join("\n", load.Errors));
        var keys = AssetManifest.LoadKeys(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default"));
        return ContentValidator.Validate(load.Database!, _temp, keys);
    }

    private void ExpectIssue(string code, string path, string messagePart = "")
    {
        var report = Validate();
        var hits = report.Issues.Where(i => i.Code == code).ToList();
        hits.Count.ShouldBe(1, string.Join("\n", report.Issues));
        var issue = hits[0];
        issue.File.ShouldBe(Meadow);
        issue.Path.ShouldBe(path);
        issue.Severity.ShouldBe(ValidationSeverity.Error);
        issue.Line.ShouldBeGreaterThan(0);
        issue.Message.ShouldContain(messagePart);
    }

    // ---- sample content ----------------------------------------------------------------------------------------

    [Fact]
    public void Sample_maps_load_and_validate_clean()
    {
        var report = Validate();

        report.Errors.ShouldBeEmpty(string.Join("\n", report.Errors));
        var db = ContentLoader.Load(_temp).Database!;
        db.Grids.Count.ShouldBe(db.Maps.All.Count());
        var meadow = db.GetGrid("map.starter_meadow");
        meadow.Width.ShouldBe(48);
        meadow.Height.ShouldBe(32);
        meadow.Spawns.ShouldNotBeEmpty();
        meadow.Npcs.Count.ShouldBe(db.Npcs.All.Count());
        meadow.Zones.ShouldContain(z => z.Kind == ZoneKinds.Safe);
        meadow.Warps.ShouldNotBeEmpty();
    }

    [Fact]
    public void Sample_meadow_has_blocking_fence_and_see_through_pond()
    {
        var g = ContentLoader.Load(_temp).Database!.GetGrid("map.starter_meadow");

        g.IsWalkable(30, 5).ShouldBeFalse();
        g.BlocksLos(30, 5).ShouldBeTrue();
        g.IsWalkable(14, 22).ShouldBeFalse();
        g.BlocksLos(14, 22).ShouldBeFalse();
        g.IsWalkable(5, 5).ShouldBeTrue();
    }

    [Fact]
    public void Sample_maps_have_a_warp_pair_in_each_direction()
    {
        var db = ContentLoader.Load(_temp).Database!;

        foreach (var map in db.Maps.All)
            foreach (var warp in db.GetGrid(map.Id).Warps)
                db.GetGrid(warp.TargetMapId).Warps.ShouldContain(w => w.TargetMapId == map.Id,
                    $"{map.Id} warps to {warp.TargetMapId}, which has no way back");
    }

    [Fact]
    public void Committed_grids_match_their_ascii_sources()
    {
        foreach (var txt in Directory.GetFiles(Path.Combine(RepoRoot(), "content", "maps", "src"), "*.txt"))
        {
            var expected = GridFileJson.Write(AsciiGridConverter.Convert(File.ReadAllText(txt)));
            var committed = Path.Combine(RepoRoot(), "content", "maps", Path.GetFileNameWithoutExtension(txt) + ".grid.json");
            File.ReadAllText(committed).Replace("\r\n", "\n").ShouldBe(expected, $"{Path.GetFileName(committed)} is stale; run `grid from-ascii`");
        }
    }

    [Fact]
    public void Ascii_sources_next_to_grids_are_ignored_by_the_loader()
    {
        File.Exists(PathOf("maps/src/starter_meadow.txt")).ShouldBeTrue();

        LoadErrors().ShouldBeEmpty();
    }

    // ---- load errors -------------------------------------------------------------------------------------------

    [Fact]
    public void Missing_grid_file_is_a_load_error_naming_the_file()
    {
        File.Delete(PathOf(Meadow));

        var e = LoadErrors().ShouldHaveSingleItem();

        e.Code.ShouldBe(ContentErrorCodes.GridMissing);
        e.Message.ShouldContain(Meadow);
    }

    [Theory]
    [InlineData("sub/starter_meadow.grid.json")]
    [InlineData("../starter_meadow.grid.json")]
    [InlineData("..\\starter_meadow.grid.json")]
    [InlineData("C:\\x.grid.json")]
    [InlineData("/etc/x.grid.json")]
    public void Grid_file_must_be_a_bare_file_name(string name)
    {
        EditText("maps/starter_meadow.yaml", "grid_file: starter_meadow.grid.json", $"grid_file: '{name}'");

        var e = LoadErrors().ShouldHaveSingleItem();

        e.Code.ShouldBe(ContentErrorCodes.GridFileName);
        e.File.ShouldBe("maps/starter_meadow.yaml");
        e.Path.ShouldBe("grid_file");
    }

    [Fact]
    public void Malformed_json_is_a_load_error_with_a_position()
    {
        File.WriteAllText(PathOf(Meadow), "{ \"format_version\": 1, ");

        var e = LoadErrors().ShouldHaveSingleItem();

        e.Code.ShouldBe(ContentErrorCodes.GridMalformed);
        e.File.ShouldBe(Meadow);
        e.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Unknown_key_is_a_load_error()
    {
        EditText(Meadow, "\"width\": 48,", "\"width\": 48, \"wdith\": 1,");

        var e = LoadErrors().ShouldHaveSingleItem();

        e.Code.ShouldBe(ContentErrorCodes.GridMalformed);
        e.Message.ShouldContain("wdith");
    }

    [Fact]
    public void Malformed_base64_is_a_load_error()
    {
        EditGrid(g => g with { Cells = "***not base64***" });

        var e = LoadErrors().ShouldHaveSingleItem();

        e.Code.ShouldBe(ContentErrorCodes.GridMalformed);
        e.File.ShouldBe(Meadow);
        e.Message.ShouldContain("base64");
    }

    [Fact]
    public void Wrong_cell_count_is_a_load_error()
    {
        EditGrid(g => g with { Width = g.Width + 1 });

        var e = LoadErrors().ShouldHaveSingleItem();

        e.Code.ShouldBe(ContentErrorCodes.GridMalformed);
        e.Message.ShouldContain("width * height");
    }

    [Fact]
    public void Wrong_map_id_is_a_load_error()
    {
        EditGrid(g => g with { MapId = "map.thistle_hollow" });

        var e = LoadErrors().ShouldHaveSingleItem();

        e.Code.ShouldBe(ContentErrorCodes.GridMapId);
        e.File.ShouldBe(Meadow);
    }

    [Fact]
    public void Unknown_format_version_is_a_load_error()
    {
        EditGrid(g => g with { FormatVersion = 99 });

        var e = LoadErrors().ShouldHaveSingleItem();

        e.Code.ShouldBe(ContentErrorCodes.GridVersion);
        e.File.ShouldBe(Meadow);
    }

    // ---- validation rule ---------------------------------------------------------------------------------------

    [Fact]
    public void Unknown_monster_in_spawn()
    {
        EditGrid(g => g with { Spawns = Replace(g.Spawns, 2, s => s with { MonsterId = "monster.nope" }) });

        ExpectIssue(ValidationCodes.RefMissing, "spawns[2].monster_id", "monster.nope");
    }

    [Fact]
    public void Unknown_npc_in_placement()
    {
        EditGrid(g => g with { Npcs = Replace(g.Npcs, 1, n => n with { NpcId = "npc.nope" }) });

        ExpectIssue(ValidationCodes.RefMissing, "npcs[1].npc_id", "npc.nope");
    }

    [Fact]
    public void Unknown_warp_target_map()
    {
        EditGrid(g => g with { Warps = Replace(g.Warps, 0, w => w with { TargetMapId = "map.nope" }) });

        ExpectIssue(ValidationCodes.RefMissing, "warps[0].target_map_id", "map.nope");
    }

    [Theory]
    [InlineData(30, 5, "not walkable")]
    [InlineData(48, 0, "outside")]
    public void Respawn_cell_must_be_walkable_on_the_maps_grid(int x, int y, string reason)
    {
        EditText("maps/starter_meadow.yaml", "respawn:\n  x: 12\n  y: 12", $"respawn:\n  x: {x}\n  y: {y}");

        var issue = Validate().Issues.Single(i => i.Code == ValidationCodes.GridCellBlocked);

        issue.Path.ShouldBe("respawn");
        issue.Message.ShouldContain(reason);
        issue.Severity.ShouldBe(ValidationSeverity.Error);
    }

    [Fact]
    public void Warp_on_a_non_walkable_cell()
    {
        EditGrid(g => g with { Warps = Replace(g.Warps, 0, w => w with { Cell = new GridCell(30, 5) }) });

        ExpectIssue(ValidationCodes.GridCellBlocked, "warps[0].cell", "not walkable");
    }

    [Fact]
    public void Warp_out_of_bounds()
    {
        EditGrid(g => g with { Warps = Replace(g.Warps, 0, w => w with { Cell = new GridCell(48, 0) }) });

        ExpectIssue(ValidationCodes.GridCellBlocked, "warps[0].cell", "outside");
    }

    [Fact]
    public void Warp_target_on_a_non_walkable_cell_of_the_other_map()
    {
        EditGrid(g => g with { Warps = Replace(g.Warps, 0, w => w with { TargetCell = new GridCell(0, 0) }) });

        ExpectIssue(ValidationCodes.GridCellBlocked, "warps[0].target_cell", "map.thistle_hollow");
    }

    [Fact]
    public void Warp_may_target_its_own_map()
    {
        EditGrid(g => g with { Warps = Replace(g.Warps, 0, w => w with { TargetMapId = g.MapId, TargetCell = new GridCell(5, 5) }) });

        Validate().Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Two_warps_on_the_same_cell()
    {
        EditGrid(g => g with { Warps = g.Warps.Concat(new[] { g.Warps[0] }).ToList() });

        ExpectIssue(ValidationCodes.GridWarpDuplicate, "warps[1].cell", "warps[0]");
    }

    [Fact]
    public void Npc_on_a_non_walkable_cell()
    {
        EditGrid(g => g with { Npcs = Replace(g.Npcs, 0, n => n with { Cell = new GridCell(14, 22) }) });

        ExpectIssue(ValidationCodes.GridCellBlocked, "npcs[0].cell", "not walkable");
    }

    [Fact]
    public void Npc_out_of_bounds()
    {
        EditGrid(g => g with { Npcs = Replace(g.Npcs, 0, n => n with { Cell = new GridCell(-1, 3) }) });

        ExpectIssue(ValidationCodes.GridCellBlocked, "npcs[0].cell", "outside");
    }

    [Fact]
    public void Unknown_facing()
    {
        EditGrid(g => g with { Npcs = Replace(g.Npcs, 0, n => n with { Facing = "up" }) });

        ExpectIssue(ValidationCodes.VocabUnknown, "npcs[0].facing", "up");
    }

    [Fact]
    public void Unknown_zone_kind()
    {
        EditGrid(g => g with { Zones = Replace(g.Zones, 0, z => z with { Kind = "lava" }) });

        ExpectIssue(ValidationCodes.VocabUnknown, "zones[0].kind", "lava");
    }

    [Theory]
    [InlineData(40, 0, 10, 4)]    // sticks out east
    [InlineData(-1, 0, 4, 4)]     // starts west of the map
    [InlineData(5, 5, 0, 3)]      // empty
    public void Spawn_rect_out_of_bounds_or_empty(int x, int y, int w, int h)
    {
        EditGrid(g => g with { Spawns = Replace(g.Spawns, 0, s => s with { Area = new GridRect(x, y, w, h) }) });

        ExpectIssue(ValidationCodes.GridRect, "spawns[0].area");
    }

    [Fact]
    public void Spawn_rect_without_a_walkable_cell()
    {
        // (13..15, 22) is the middle of the pond: water only.
        EditGrid(g => g with { Spawns = Replace(g.Spawns, 0, s => s with { Area = new GridRect(13, 22, 3, 1) }) });

        ExpectIssue(ValidationCodes.GridRect, "spawns[0].area", "no walkable");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Spawn_count_below_one(int count)
    {
        EditGrid(g => g with { Spawns = Replace(g.Spawns, 1, s => s with { Count = count }) });

        ExpectIssue(ValidationCodes.GridValueRange, "spawns[1].count");
    }

    [Fact]
    public void Negative_respawn()
    {
        EditGrid(g => g with { Spawns = Replace(g.Spawns, 1, s => s with { RespawnSeconds = -1 }) });

        ExpectIssue(ValidationCodes.GridValueRange, "spawns[1].respawn_seconds");
    }

    [Fact]
    public void Zone_rect_out_of_bounds()
    {
        EditGrid(g => g with { Zones = Replace(g.Zones, 0, z => z with { Area = new GridRect(45, 30, 5, 5) }) });

        ExpectIssue(ValidationCodes.GridRect, "zones[0].area");
    }

    [Fact]
    public void Unknown_flag_bits()
    {
        EditGrid(g => SetCell(g, 3, 4, 0b1000_0101));

        ExpectIssue(ValidationCodes.GridUnknownFlags, "cells", "(3, 4)");
    }
}
