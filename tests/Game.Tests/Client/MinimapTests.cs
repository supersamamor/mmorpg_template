using Game.Client.Core;
using Game.Client.Core.Bundle;
using Game.Client.Core.Party;
using Game.Client.Core.Quests;
using Game.Client.Core.Ui;
using Game.Client.Core.World;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.Client;

/// <summary>
/// T-812's client side without the engine: where the corner and full maps put a world point and which cell a tap names, which part of the picture they show, the marks (only what the
/// client already has: the bundle's warps and NPC places, the server's quest markers, the area of interest, <c>party_members</c>), and the switch kept with the preferences.
/// </summary>
public sealed class MinimapTests
{
    // A 10 x 6 map with a warp at (9, 5) and two NPCs, one of which the server marks as offering a quest.
    private static MapData Map()
    {
        var json = "{\"format_version\":" + ClientBundle.CurrentFormatVersion + ",\"content_hash\":\"h\",\"items\":[],\"skills\":[],\"jobs\":[],\"statuses\":[],\"monsters\":[]," +
            "\"npcs\":[],\"quests\":[],\"maps\":[{\"id\":\"map.a\",\"name\":\"A\",\"scene\":\"scene.map.a\",\"width\":10,\"height\":6,\"cells\":\"" +
            Convert.ToBase64String(Enumerable.Repeat(CellFlagBits.Walkable, 60).ToArray()) + "\"," +
            "\"warps\":[{\"cell\":[9,5],\"map\":\"map.b\",\"target\":[1,1]}],\"npcs\":[{\"npc\":\"npc.guide\",\"cell\":[2,1],\"facing\":\"s\"},{\"npc\":\"npc.smith\",\"cell\":[4,4],\"facing\":\"n\"}],\"zones\":[]}]}";
        DisplayBundle.TryRead(System.Text.Encoding.UTF8.GetBytes(json), out var bundle, out _).ShouldBe(BundleStatus.Ok);
        bundle!.TryGetMap("map.a", out var map).ShouldBeTrue();
        return map!;
    }

    private static WorldView World(params EntityRecord[] others)
    {
        var world = new WorldView(new ClientConfig());
        world.ApplyWelcome(new WelcomeMessage(1, 1, "map.a", 10));
        var records = new List<EntityRecord> { new(1, EntityKinds.Player, "job.wanderer", new GridCell(3, 2), Directions.E, null, 0, 1) };
        records.AddRange(others);
        world.ApplySnapshot(new SnapshotMessage(0, "map.a", 1, records), 0);
        return world;
    }

    [Fact]
    public void The_bundle_gives_a_map_its_npc_places()
    {
        var map = Map();
        map.Npcs.ShouldBe([new MapNpc("npc.guide", new GridCell(2, 1)), new MapNpc("npc.smith", new GridCell(4, 4))]);
    }

    [Fact]
    public void The_corner_map_follows_the_player_inside_the_map_and_shows_a_small_map_whole()
    {
        // 48 x 32 cells, 24 across in a 96 px square: 4 px a cell, the player in the middle until the map's edge stops the window.
        var middle = MinimapFrame.Corner(48, 32, 20.5, 15.5, 96, 24);
        (middle.Left, middle.Top, middle.CellPx).ShouldBe((8.5, 3.5, 4.0));
        middle.ToPx(20.5, 15.5).ShouldBe((48.0, 48.0));
        var corner = MinimapFrame.Corner(48, 32, 1.5, 30.5, 96, 24);
        (corner.Left, corner.Top).ShouldBe((0.0, 8.0), "kept inside the map at its south-west corner");
        corner.ToPx(1.5, 30.5).ShouldBe((6.0, 90.0));
        corner.Shows(1.5, 30.5).ShouldBeTrue();
        corner.Shows(30, 30).ShouldBeFalse("east of the window: off the corner map");
        corner.VisibleCells(48, 32).ShouldBe((0.0, 8.0, 24.0, 24.0));

        // A 20 x 14 map is smaller than the window: shown whole, its longer side filling the square, the shorter one centred.
        var small = MinimapFrame.Corner(20, 14, 2.5, 7.5, 80, 24);
        (small.Left, small.Top, small.CellPx).ShouldBe((0.0, -3.0, 4.0));
        small.VisibleCells(20, 14).ShouldBe((0.0, 0.0, 20.0, 14.0));
        small.ToPx(0, 0).ShouldBe((0.0, 12.0));
    }

    [Fact]
    public void The_full_map_fits_the_whole_map_and_a_tap_names_the_cell_under_it()
    {
        var full = MinimapFrame.Full(48, 32, 600, 300);
        full.CellPx.ShouldBe(300.0 / 32);
        (full.WidthPx, full.HeightPx).ShouldBe((48 * 300.0 / 32, 300.0));
        full.CellAt(0.1, 0.1).ShouldBe(new GridCell(0, 0));
        var (x, y) = full.ToPx(12.5, 7.5);
        full.CellAt(x, y).ShouldBe(new GridCell(12, 7));
        full.CellAt(full.WidthPx - 0.01, full.HeightPx - 0.01).ShouldBe(new GridCell(47, 31));
        full.CellAt(-1, 5).X.ShouldBe(-1, "a point off the map names a cell off the map: the view does not send it");
    }

    [Fact]
    public void The_marks_are_the_bundle_s_places_the_server_s_quest_markers_and_what_is_in_view_with_the_player_last()
    {
        var map = Map();
        var world = World(
            new EntityRecord(5, EntityKinds.Monster, "monster.a", new GridCell(6, 2), Directions.S, null, 0, 1),
            new EntityRecord(6, EntityKinds.Monster, "monster.a", new GridCell(7, 2), Directions.S, null, 0, 0, Dead: true),
            new EntityRecord(7, EntityKinds.Player, "job.wanderer", new GridCell(5, 5), Directions.S, null, 0, 1),
            new EntityRecord(8, EntityKinds.Player, "job.wanderer", new GridCell(1, 5), Directions.S, null, 0, 1),
            new EntityRecord(9, EntityKinds.Item, "item.a", new GridCell(3, 3), Directions.S, null, 0),
            new EntityRecord(10, EntityKinds.Npc, "npc.guide", new GridCell(2, 1), Directions.S, null, 0));
        var quests = new QuestBook();
        quests.Apply(new QuestLogMessage([], [], ["npc.guide"], []));
        var party = new PartyBook { OwnId = 1 };
        party.Apply(new PartyMessage(1, 1, [new PartyMemberRecord(1, "Ada"), new PartyMemberRecord(8, "Bo"), new PartyMemberRecord(11, "Cy"), new PartyMemberRecord(12, "Di")]));
        party.Apply(new PartyMembersMessage([
            new PartyMemberStatusRecord(8, 50, 50, "map.a", new GridCell(1, 5), false, false),
            new PartyMemberStatusRecord(11, 50, 50, "map.a", new GridCell(8, 0), false, false),   // out of view: where party_members says
            new PartyMemberStatusRecord(12, 50, 50, "map.b", new GridCell(1, 1), false, false),   // on another map: not on this one
        ]));

        var marks = new List<MinimapMark>();
        MinimapMarks.Collect(marks, map, world, party.Members, quests);
        marks.Select(m => (m.Kind, m.X, m.Z, m.Id)).ShouldBe([
            (MinimapMarkKind.Warp, 9.5, 5.5, 0L),
            (MinimapMarkKind.QuestOffer, 2.5, 1.5, 0L),
            (MinimapMarkKind.Npc, 4.5, 4.5, 0L),
            (MinimapMarkKind.Monster, 6.5, 2.5, 5L),
            (MinimapMarkKind.Player, 5.5, 5.5, 7L),
            (MinimapMarkKind.Party, 1.5, 5.5, 8L),
            (MinimapMarkKind.Party, 8.5, 0.5, 11L),
            (MinimapMarkKind.Own, 3.5, 2.5, 1L),
        ]);
        // The player's facing: east is to the right on the map.
        var (hx, hy) = MinimapMarks.Heading(marks[^1].Yaw);
        hx.ShouldBe(1, 1e-9);
        hy.ShouldBe(0, 1e-9);
        MinimapMarks.Heading(0).Y.ShouldBe(-1, 1e-9, "facing north points up");
    }

    [Fact]
    public void Nothing_unknown_is_marked_and_another_map_s_world_view_marks_only_the_bundle_s_places()
    {
        var map = Map();
        var marks = new List<MinimapMark>();
        // No party, no quest markers, nobody in view: the warp, the two NPC places and the player. Spawn areas are not in the bundle at all, so they cannot be marked.
        MinimapMarks.Collect(marks, map, World(), null, null);
        marks.Select(m => m.Kind).ShouldBe([MinimapMarkKind.Warp, MinimapMarkKind.Npc, MinimapMarkKind.Npc, MinimapMarkKind.Own]);

        var elsewhere = new WorldView(new ClientConfig());
        elsewhere.ApplyWelcome(new WelcomeMessage(1, 1, "map.b", 10));
        elsewhere.ApplySnapshot(new SnapshotMessage(0, "map.b", 1, [new(1, EntityKinds.Player, null, new GridCell(1, 1), Directions.N, null, 0, 1)]), 0);
        MinimapMarks.Collect(marks, map, elsewhere, null, null);
        marks.Select(m => m.Kind).ShouldBe([MinimapMarkKind.Warp, MinimapMarkKind.Npc, MinimapMarkKind.Npc], "a warp in flight: no entity of the other map");
    }

    [Fact]
    public void Collecting_the_marks_again_allocates_nothing()
    {
        var map = Map();
        var world = World(new EntityRecord(5, EntityKinds.Monster, "monster.a", new GridCell(6, 2), Directions.S, null, 0, 1));
        var quests = new QuestBook();
        IReadOnlyList<PartyMemberView> members = [];
        var marks = new List<MinimapMark>();
        MinimapMarks.Collect(marks, map, world, members, quests);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) MinimapMarks.Collect(marks, map, world, members, quests);
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0, "the HUD collects them every frame while a map shows");
    }

    [Fact]
    public void The_minimap_switch_is_kept_with_the_preferences_and_on_by_default()
    {
        new UserPreferences().Minimap.ShouldBeTrue();
        UserPreferences.Parse("{}"u8).Minimap.ShouldBeTrue();
        UserPreferences.Parse("not json"u8).Minimap.ShouldBeTrue();
        UserPreferences.Parse("{\"minimap\": 0}"u8).Minimap.ShouldBeTrue("a damaged value keeps the default");
        var prefs = new UserPreferences { UiScale = 1.25, Minimap = false };
        var back = UserPreferences.Parse(prefs.ToJson());
        (back.Minimap, back.UiScale).ShouldBe((false, 1.25));
        UserPreferences.Parse("{\"ui_scale\": 1.5, \"minimap\": false, \"sound_muted\": true}"u8).Minimap.ShouldBeFalse();
        new ClientConfig().MinimapCornerCells.ShouldBe(24);
        UiText.MinimapSetting(true).ShouldNotBe(UiText.MinimapSetting(false));
    }
}
