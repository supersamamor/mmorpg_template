using Game.Content.Loading;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Tests.World;

public class AsciiGridTests
{
    private const string Sample =
        "// comment\n" +
        "map: map.test\n" +
        "scene_hash: abc123\n" +
        "spawn: monster.a 1 1 2 1 3 7.5\n" +
        "warp: 1 1 map.other 2 0\n" +
        "npc: npc.a 2 1 sw\n" +
        "zone: safe 0 0 2 2\n" +
        "---\n" +
        "#.#\n" +
        ".~.\n";

    [Fact]
    public void Converts_rows_legend_and_objects()
    {
        var file = AsciiGridConverter.Convert(Sample);
        var grid = MapGrid.Create(file);

        file.MapId.ShouldBe("map.test");
        file.SourceSceneHash.ShouldBe("abc123");
        grid.Width.ShouldBe(3);
        grid.Height.ShouldBe(2);
        grid.IsWalkable(0, 0).ShouldBeFalse();
        grid.BlocksLos(0, 0).ShouldBeTrue();
        grid.IsWalkable(1, 0).ShouldBeTrue();
        grid.BlocksLos(1, 0).ShouldBeFalse();
        grid.IsWalkable(1, 1).ShouldBeFalse();    // ~ water
        grid.BlocksLos(1, 1).ShouldBeFalse();
        grid.Spawns.ShouldHaveSingleItem().ShouldBe(new GridSpawn
        {
            MonsterId = "monster.a", Area = new GridRect(1, 1, 2, 1), Count = 3, RespawnSeconds = 7.5,
        });
        grid.Warps.ShouldHaveSingleItem().TargetCell.X.ShouldBe(2);
        grid.Npcs.ShouldHaveSingleItem().Facing.ShouldBe(Directions.Sw);
        grid.Zones.ShouldHaveSingleItem().Kind.ShouldBe(ZoneKinds.Safe);
    }

    [Fact]
    public void Json_round_trip_is_lossless_and_stable()
    {
        var file = AsciiGridConverter.Convert(Sample);

        var json = GridFileJson.Write(file);
        var back = GridFileJson.Read(json);

        back.MapId.ShouldBe(file.MapId);
        back.Cells.ShouldBe(file.Cells);
        back.Spawns.ShouldBe(file.Spawns);
        back.Warps.ShouldBe(file.Warps);
        back.Npcs.ShouldBe(file.Npcs);
        back.Zones.ShouldBe(file.Zones);
        GridFileJson.Write(back).ShouldBe(json);
        json.ShouldContain("\"format_version\": 1");
        json.ShouldEndWith("}\n");
    }

    [Theory]
    [InlineData("map: map.a\n#.\n", "---")]
    [InlineData("---\n#.\n", "map:")]
    [InlineData("map: map.a\n---\n", "No map rows")]
    [InlineData("map: map.a\n---\n#.\n#\n", "line 4")]
    [InlineData("map: map.a\n---\n#x\n", "unknown map character 'x'")]
    [InlineData("map: map.a\nbogus: 1\n---\n#\n", "unknown header key")]
    [InlineData("map: map.a\nwarp: 1 2\n---\n#\n", "takes 5")]
    [InlineData("map: map.a\nnpc: npc.a x 1 s\n---\n#\n", "not an integer")]
    public void Bad_input_is_a_format_error(string text, string fragment)
    {
        Should.Throw<FormatException>(() => AsciiGridConverter.Convert(text)).Message.ShouldContain(fragment);
    }
}
