using Game.Client.MapAuthoring;
using Game.Content.Loading;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.MapAuthoring;

/// <summary>The map plugin's pure part (T-208): bake rules, paint, marker checks, canonical output. The real rays run in client/export-maps.ps1.</summary>
public class MapAuthoringTests
{
    private static readonly byte Open = CellFlagBits.Walkable;

    private static readonly CellRayHit Flat = new(true, 1, true, false);

    private sealed class FakeIds : IContentIds
    {
        public bool Exists(string id) => id is "monster.a" or "npc.a" or "map.other" or "map.self";

        public bool IsWalkable(string mapId, int x, int y) => mapId == "map.other" && x == 2 && y == 2;
    }

    private static MapSettings Settings(params string[] overrides)
    {
        var errors = new List<string>();
        var parsed = CellOverrides.Parse(overrides, errors);
        errors.ShouldBeEmpty();
        return new MapSettings("map.self", 4, 3, 40, parsed);
    }

    private static byte[] AllOpen() => Enumerable.Repeat(Open, 12).ToArray();

    private static MarkerNode Marker(string path, int x, int y, params (string Key, object? Value)[] meta) =>
        new(path, x, y, meta.ToDictionary(m => m.Key, m => m.Value));

    private static MarkerNode Spawn(string monster = "monster.a", int x = 0, int y = 0, long w = 2, long h = 2, long count = 1, double respawn = 5, string path = "Markers/S") =>
        Marker(path, x, y, (MapMeta.Marker, MapObjectTypes.Spawn), (MapMeta.MonsterId, monster), (MapMeta.AreaWidth, w), (MapMeta.AreaHeight, h), (MapMeta.Count, count), (MapMeta.RespawnSeconds, respawn));

    private static MarkerNode Warp(int x, int y, string map = "map.other", long tx = 2, long ty = 2, string path = "Markers/W") =>
        Marker(path, x, y, (MapMeta.Marker, MapObjectTypes.Warp), (MapMeta.TargetMapId, map), (MapMeta.TargetX, tx), (MapMeta.TargetY, ty));

    private static MarkerNode Npc(int x, int y, string npc = "npc.a", string facing = Directions.S, string path = "Markers/N") =>
        Marker(path, x, y, (MapMeta.Marker, MapObjectTypes.Npc), (MapMeta.NpcId, npc), (MapMeta.Facing, facing));

    private static MarkerNode Zone(int x, int y, long w, long h, string kind = ZoneKinds.Safe, string path = "Markers/Z") =>
        Marker(path, x, y, (MapMeta.Marker, MapObjectTypes.Zone), (MapMeta.ZoneKind, kind), (MapMeta.AreaWidth, w), (MapMeta.AreaHeight, h));

    private static MapExportResult Build(MapSettings settings, byte[] baked, params MarkerNode[] markers) =>
        MapExporter.Build(settings, baked, markers, "hash", new FakeIds());

    // ---- bake ------------------------------------------------------------------------------------------------

    [Fact]
    public void Bake_casts_one_ray_through_each_cell_centre_in_row_order()
    {
        var rays = new List<(double, double)>();

        MapBaker.Bake(2, 2, 40, (x, z) =>
        {
            rays.Add((x, z));
            return Flat;
        });

        rays.ShouldBe(new[] { (0.5, 0.5), (1.5, 0.5), (0.5, 1.5), (1.5, 1.5) });
    }

    [Fact]
    public void Bake_is_deterministic()
    {
        static CellRayHit Terrain(double x, double z) => new(((int)x + (int)z) % 3 != 0, 0.5 + x / 100, (int)z % 2 == 0, (int)x % 4 == 0);

        var first = MapBaker.Bake(64, 48, 40, Terrain);
        var second = MapBaker.Bake(64, 48, 40, Terrain);

        second.ShouldBe(first);
    }

    [Theory]
    [InlineData(1.0, 40, true)]
    [InlineData(0.8, 40, true)]   // about 36.9 degrees
    [InlineData(0.7, 40, false)]  // about 45.6 degrees
    [InlineData(0.7, 50, true)]
    public void Slope_limit_decides_walkable(double normalY, double limit, bool walkable)
    {
        var flags = MapBaker.Flags(new CellRayHit(true, normalY, true, false), limit);

        ((flags & CellFlagBits.Walkable) != 0).ShouldBe(walkable);
    }

    [Fact]
    public void A_surface_exactly_at_the_limit_does_not_depend_on_float_noise()
    {
        var atLimit = Math.Cos(40 * Math.PI / 180);

        MapBaker.Flags(new CellRayHit(true, (float)atLimit - 1e-7, true, false), 40).ShouldBe(Open);
    }

    [Fact]
    public void No_ground_is_blocked_and_does_not_block_sight()
    {
        MapBaker.Flags(CellRayHit.Miss, 40).ShouldBe((byte)0);
    }

    [Fact]
    public void Surface_metadata_sets_the_flags()
    {
        MapBaker.Flags(new CellRayHit(true, 1, false, false), 40).ShouldBe((byte)0);                       // water
        MapBaker.Flags(new CellRayHit(true, 1, false, true), 40).ShouldBe(CellFlagBits.BlocksLos);         // a wall's flat top
    }

    // ---- paint -----------------------------------------------------------------------------------------------

    [Fact]
    public void Overrides_survive_a_rebake_whatever_the_bake_found()
    {
        var settings = Settings("1,0:block", "2,0:walk", "3,0:los", "0,1:open", "1,1:block+los");
        var firstBake = AllOpen();
        var secondBake = new byte[12]; // the geometry changed: nothing is walkable now
        secondBake[4] = CellFlagBits.BlocksLos;
        var errors = new List<string>();

        var first = settings.Overrides.Apply(firstBake, 4, 3, errors);
        var second = settings.Overrides.Apply(secondBake, 4, 3, errors);

        errors.ShouldBeEmpty();
        foreach (var flags in new[] { first, second })
        {
            (flags[1] & CellFlagBits.Walkable).ShouldBe(0);
            (flags[2] & CellFlagBits.Walkable).ShouldBe((int)CellFlagBits.Walkable);
            (flags[3] & CellFlagBits.BlocksLos).ShouldBe((int)CellFlagBits.BlocksLos);
            (flags[4] & CellFlagBits.BlocksLos).ShouldBe(0);
            flags[5].ShouldBe(CellFlagBits.BlocksLos);
        }
        firstBake.ShouldBe(AllOpen()); // the bake itself is left alone, for the overlay
    }

    [Fact]
    public void Paint_order_does_not_change_the_saved_lines()
    {
        var a = new CellOverrides();
        a.Set(3, 2, WalkPaint.Blocked, LosPaint.None);
        a.Set(0, 2, WalkPaint.None, LosPaint.Blocks);
        a.Set(1, 0, WalkPaint.Walkable, LosPaint.Open);
        var b = new CellOverrides();
        b.Set(1, 0, WalkPaint.Walkable, LosPaint.Open);
        b.Set(3, 2, WalkPaint.Blocked, LosPaint.None);
        b.Set(0, 2, WalkPaint.None, LosPaint.Blocks);

        a.ToLines().ShouldBe(b.ToLines());
        a.ToLines().ShouldBe(new[] { "1,0:walk+open", "0,2:los", "3,2:block" });
    }

    [Fact]
    public void Saved_lines_read_back_the_same_and_clearing_removes_a_cell()
    {
        var errors = new List<string>();
        var overrides = CellOverrides.Parse(new[] { "1,0:walk+open", "0,2:los" }, errors);

        errors.ShouldBeEmpty();
        overrides.ToLines().ShouldBe(new[] { "1,0:walk+open", "0,2:los" });
        overrides.Set(1, 0, WalkPaint.None, LosPaint.None);
        overrides.ToLines().ShouldBe(new[] { "0,2:los" });
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("1,2")]
    [InlineData("1,2:fly")]
    [InlineData("a,2:walk")]
    [InlineData("1,2:walk+block")]
    public void A_bad_override_line_is_an_error(string line)
    {
        var errors = new List<string>();

        CellOverrides.Parse(new[] { line }, errors);

        errors.ShouldHaveSingleItem().ShouldContain(line);
    }

    [Fact]
    public void An_override_outside_the_map_is_an_error()
    {
        var result = Build(Settings("9,9:block"), AllOpen());

        result.File.ShouldBeNull();
        result.Problems.ShouldHaveSingleItem().Message.ShouldContain("outside");
    }

    // ---- markers ---------------------------------------------------------------------------------------------

    [Fact]
    public void Markers_reach_the_grid_with_their_fields()
    {
        var result = Build(Settings(), AllOpen(), Spawn(x: 1, y: 1), Warp(0, 0), Npc(3, 0), Zone(0, 0, 4, 3));

        result.Problems.ShouldBeEmpty();
        var file = result.File!;
        file.Spawns.ShouldHaveSingleItem().ShouldBe(new GridSpawn { MonsterId = "monster.a", Area = new GridRect(1, 1, 2, 2), Count = 1, RespawnSeconds = 5 });
        file.Warps.ShouldHaveSingleItem().ShouldBe(new GridWarp { Cell = new GridCell(0, 0), TargetMapId = "map.other", TargetCell = new GridCell(2, 2) });
        file.Npcs.ShouldHaveSingleItem().ShouldBe(new GridNpc { NpcId = "npc.a", Cell = new GridCell(3, 0), Facing = Directions.S });
        file.Zones.ShouldHaveSingleItem().ShouldBe(new GridZone { Kind = ZoneKinds.Safe, Area = new GridRect(0, 0, 4, 3) });
    }

    public static TheoryData<string, MarkerNode, string> BadMarkers => new()
    {
        { "unknown monster", Spawn(monster: "monster.none"), "monster.none" },
        { "spawn out of bounds", Spawn(x: 3, y: 0, w: 5), "outside" },
        { "spawn count", Spawn(count: 0), MapMeta.Count },
        { "spawn respawn", Spawn(respawn: -1), MapMeta.RespawnSeconds },
        { "warp on a blocked cell", Warp(0, 0), "not walkable" },
        { "warp outside", Warp(9, 9), "outside" },
        { "warp to an unknown map", Warp(1, 1, map: "map.none"), "map.none" },
        { "warp target blocked", Warp(1, 1, tx: 0, ty: 0), "not walkable on map.other" },
        { "npc on a blocked cell", Npc(0, 0), "not walkable" },
        { "unknown npc", Npc(1, 1, npc: "npc.none"), "npc.none" },
        { "bad facing", Npc(1, 1, facing: "up"), "up" },
        { "bad zone kind", Zone(0, 0, 1, 1, kind: "lava"), "lava" },
        { "zone out of bounds", Zone(0, 0, 9, 1), "outside" },
        { "unknown marker kind", Marker("Markers/Q", 1, 1, (MapMeta.Marker, "treasure")), "treasure" },
        { "missing field", Marker("Markers/Q", 1, 1, (MapMeta.Marker, MapObjectTypes.Npc), (MapMeta.Facing, Directions.S)), MapMeta.NpcId },
        { "mistyped field", Marker("Markers/Q", 1, 1, (MapMeta.Marker, MapObjectTypes.Warp), (MapMeta.TargetMapId, "map.other"), (MapMeta.TargetX, "two"), (MapMeta.TargetY, 2L)), MapMeta.TargetX },
    };

    [Theory]
    [MemberData(nameof(BadMarkers))]
    public void A_bad_marker_is_an_error_naming_the_node_and_nothing_is_written(string what, MarkerNode marker, string fragment)
    {
        var result = Build(Settings("0,0:block"), AllOpen(), marker);

        result.File.ShouldBeNull(what);
        var problem = result.Problems.ShouldHaveSingleItem();
        problem.Path.ShouldBe(marker.Path);
        problem.Message.ShouldContain(fragment);
    }

    [Fact]
    public void A_painted_cell_counts_for_markers()
    {
        Build(Settings("1,1:block"), AllOpen(), Npc(1, 1)).File.ShouldBeNull();
        var blocked = new byte[12];
        blocked[0] = Open; // spawn areas need a walkable cell
        var cells = Settings("1,1:walk");
        Build(cells, blocked, Npc(1, 1)).File.ShouldNotBeNull();
    }

    [Fact]
    public void A_spawn_area_with_no_walkable_cell_is_an_error()
    {
        Build(Settings(), new byte[12], Spawn()).Problems.ShouldHaveSingleItem().Message.ShouldContain("no walkable cell");
    }

    [Fact]
    public void Two_warps_on_a_cell_are_an_error_and_a_warp_may_target_its_own_map()
    {
        Build(Settings(), AllOpen(), Warp(1, 1, path: "A"), Warp(1, 1, path: "B")).Problems.ShouldHaveSingleItem().Path.ShouldBe("B");
        Build(Settings(), AllOpen(), Warp(1, 1, map: "map.self", tx: 3, ty: 2)).Problems.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0L, 3L, 40.0, "size")]
    [InlineData(5000L, 3L, 40.0, "size")]
    [InlineData(4L, 3L, 0.0, MapMeta.SlopeLimit)]
    [InlineData(4L, 3L, 91.0, MapMeta.SlopeLimit)]
    public void Bad_root_settings_are_errors(long width, long height, double slope, string fragment)
    {
        var problems = new List<AuthoringProblem>();
        var meta = new Dictionary<string, object?> { [MapMeta.MapId] = "map.self", [MapMeta.Width] = width, [MapMeta.Height] = height, [MapMeta.SlopeLimit] = slope };

        MapExporter.ReadSettings(meta, problems).ShouldBeNull();

        problems.ShouldHaveSingleItem().Message.ShouldContain(fragment);
    }

    [Fact]
    public void Root_settings_read_the_saved_paint()
    {
        var problems = new List<AuthoringProblem>();
        var meta = new Dictionary<string, object?>
        {
            [MapMeta.MapId] = "map.self", [MapMeta.Width] = 4L, [MapMeta.Height] = 3L, [MapMeta.SlopeLimit] = 40.0, [MapMeta.Overrides] = new[] { "1,1:block" },
        };

        var settings = MapExporter.ReadSettings(meta, problems)!;

        problems.ShouldBeEmpty();
        settings.Overrides.Get(1, 1).Walk.ShouldBe(WalkPaint.Blocked);
    }

    // ---- canonical output ------------------------------------------------------------------------------------

    private static MapGridFile Sample(params MarkerNode[] markers) => Build(Settings(), AllOpen(), markers).File!;

    [Fact]
    public void The_order_of_marker_nodes_does_not_change_the_output()
    {
        var a = Spawn(monster: "monster.a", x: 0, y: 0, path: "A");
        var b = Spawn(monster: "monster.a", x: 1, y: 0, path: "B");
        var w1 = Warp(0, 2, path: "C");
        var w2 = Warp(1, 0, path: "D");

        var one = GridJsonWriter.Write(Sample(a, b, w1, w2));
        var two = GridJsonWriter.Write(Sample(w2, b, w1, a));

        two.ShouldBe(one);
    }

    [Fact]
    public void The_writer_matches_the_content_loader_byte_for_byte()
    {
        var file = Sample(Spawn(respawn: 7.5), Spawn(x: 2, respawn: 10, path: "Other"), Warp(0, 0), Npc(3, 0), Zone(0, 0, 4, 3));

        var text = GridJsonWriter.Write(file);

        text.ShouldBe(GridFileJson.Write(GridFileJson.Read(text)));
        text.ShouldNotContain("\r");
        text.ShouldEndWith("}\n");
    }

    [Fact]
    public void The_writer_matches_the_content_loader_with_empty_lists()
    {
        var text = GridJsonWriter.Write(Sample());

        text.ShouldBe(GridFileJson.Write(GridFileJson.Read(text)));
    }

    [Fact]
    public void A_second_export_of_the_same_input_is_identical()
    {
        byte[] Export() => GridJsonWriter.WriteBytes(Sample(Spawn(), Warp(0, 0), Npc(3, 0), Zone(0, 0, 4, 3)));

        Export().ShouldBe(Export());
    }

    [Fact]
    public void The_report_round_trips()
    {
        var line = new ExportReportLine(false, "a_map", "Markers/S", "bad\tvalue");

        var read = ExportReportLine.ParseAll(line.Format() + "\n").ShouldHaveSingleItem();

        read.ShouldBe(line with { Message = "bad value" });
    }

    [Fact]
    public void The_template_is_a_scene_the_export_can_read()
    {
        var text = AuthoringTemplate.Scene("map.new", 8, 6, 40, "res://themes/default/maps/new.tscn");

        var sections = Game.Content.Maps.TscnFile.Parse(text);

        var root = sections.First(s => s.Tag == "node");
        root.Properties.ShouldContain(new KeyValuePair<string, string>("metadata/" + MapMeta.MapId, "\"map.new\""));
        root.Properties.ShouldContain(new KeyValuePair<string, string>("metadata/" + MapMeta.SlopeLimit, "40.0"));
        AuthoringTemplate.StemOf("res://themes/default/maps/new.authoring.tscn").ShouldBe("new");
        AuthoringTemplate.StemOf("res://themes/default/maps/new.tscn").ShouldBeNull();
    }
}
