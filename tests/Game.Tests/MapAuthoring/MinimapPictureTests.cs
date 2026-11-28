using System.IO.Compression;
using System.Text.Json;
using Game.Client.MapAuthoring;
using Game.Content.Maps;
using Game.Content.Validation;
using Game.Content.Validation.Rules;
using Game.Shared.World;
using Game.Tests.World;

namespace Game.Tests.MapAuthoring;

/// <summary>
/// T-812: the minimap picture the map export draws from the baked grid, without Godot. The same flags give the same bytes, every cell is its colour, a large map is held to the side
/// limit, the committed pictures are what the committed grids give with the plugin's style, and <c>validate</c> wants each map's picture key in the manifest.
/// </summary>
public sealed class MinimapPictureTests
{
    private static readonly MinimapStyle Style = new(3, 2048, 0x11223344, 0x55667788, 0x99AABBCC);

    [Fact]
    public void Every_cell_is_drawn_in_its_colour_as_a_square_with_north_at_the_top()
    {
        var grid = TestGrids.Parse(
            ".#~",
            "b..");
        var flags = Flags(grid);
        var png = MinimapPicture.Draw(flags, grid.Width, grid.Height, Style);

        var read = MinimapPicture.Read(png)!.Value;
        (read.Width, read.Height).ShouldBe((9, 6));
        for (var y = 0; y < 6; y++)
            for (var x = 0; x < 9; x++)
            {
                var cell = flags[y / 3 * grid.Width + x / 3];
                var expected = (cell & CellFlagBits.BlocksLos) != 0 ? 2 : (cell & CellFlagBits.Walkable) != 0 ? 1 : 0;
                read.Indices[y * 9 + x].ShouldBe((byte)expected, $"pixel {x},{y}");
            }
    }

    [Fact]
    public void The_bytes_are_a_valid_png_with_the_style_s_colours_and_the_same_every_time()
    {
        var grid = TestGrids.Parse("..#", "~.b");
        var png = MinimapPicture.Draw(Flags(grid), grid.Width, grid.Height, Style);
        MinimapPicture.Draw(Flags(grid), grid.Width, grid.Height, Style).ShouldBe(png, "canonical: an unchanged grid exports to identical bytes");

        png.Take(8).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var chunks = Chunks(png);
        chunks.Select(c => c.Type).ShouldBe(["IHDR", "PLTE", "tRNS", "IDAT", "IEND"]);
        foreach (var chunk in chunks) chunk.CrcOk.ShouldBeTrue(chunk.Type + " CRC");
        chunks[1].Data.ShouldBe(new byte[] { 0x11, 0x22, 0x33, 0x55, 0x66, 0x77, 0x99, 0xAA, 0xBB });
        chunks[2].Data.ShouldBe(new byte[] { 0x44, 0x88, 0xCC });
        // Any zlib reader takes the image data: the stored blocks are plain deflate.
        using var inflate = new ZLibStream(new MemoryStream(chunks[3].Data), CompressionMode.Decompress);
        var raw = new MemoryStream();
        inflate.CopyTo(raw);
        raw.Length.ShouldBe((3 * 3 + 1) * 2 * 3);
    }

    [Fact]
    public void A_large_map_gets_fewer_pixels_per_cell_never_fewer_than_one()
    {
        MinimapPicture.PixelsPerCell(48, 32, MinimapStyle.Default).ShouldBe(4);
        MinimapPicture.PixelsPerCell(1000, 200, MinimapStyle.Default).ShouldBe(2);
        MinimapPicture.PixelsPerCell(4096, 4096, MinimapStyle.Default).ShouldBe(1, "beyond the limit a cell is a pixel: the map's own size is the bound");
        MinimapStyle.ParseColor("#c9d8a8").ShouldBe(0xC9D8A8FFu);
        MinimapStyle.ParseColor("#c9d8a8e6").ShouldBe(0xC9D8A8E6u);
        MinimapStyle.ParseColor("c9d8a8").ShouldBeNull();
        MinimapStyle.ParseColor("#zz0000").ShouldBeNull();

        // More than one stored block (65535 bytes each) still reads back.
        var flags = Enumerable.Repeat(CellFlagBits.Walkable, 300 * 300).ToArray();
        var big = MinimapPicture.Read(MinimapPicture.Draw(flags, 300, 300, Style))!.Value;
        (big.Width, big.Height).ShouldBe((900, 900));
        big.Indices.ShouldAllBe(i => i == 1);
    }

    [Fact]
    public void The_committed_pictures_are_what_the_committed_grids_give()
    {
        // The plugin's style, read as ExportRunner reads it, and every sample map's grid: the bytes must equal the committed picture (what export-maps.ps1 -Check also checks with Godot).
        var root = TestGrids.RepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "client/addons/map_authoring/config.json")));
        var section = config.RootElement.GetProperty("minimap");
        var style = new MinimapStyle(section.GetProperty("pixels_per_cell").GetInt32(), section.GetProperty("max_side_px").GetInt32(),
            MinimapStyle.ParseColor(section.GetProperty("blocked").GetString())!.Value, MinimapStyle.ParseColor(section.GetProperty("walkable").GetString())!.Value,
            MinimapStyle.ParseColor(section.GetProperty("blocks_los").GetString())!.Value);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "themes/default/asset-manifest.json"))).RootElement;
        foreach (var (id, grid) in TestGrids.SampleContent.Grids)
        {
            var entry = manifest.GetProperty(MinimapKeys.For(id));
            var path = Path.Combine(root, "themes/default", entry.GetProperty("file").GetString()!);
            var committed = File.ReadAllBytes(path);
            if (committed.Length < 200 && System.Text.Encoding.ASCII.GetString(committed).StartsWith("version https://git-lfs", StringComparison.Ordinal)) continue;   // an LFS pointer (CI without LFS)
            MinimapPicture.Draw(Flags(grid), grid.Width, grid.Height, style).ShouldBe(committed, $"{path} is stale: run client/export-maps.ps1");
        }
    }

    [Fact]
    public void Validate_wants_every_map_s_picture_key_in_the_manifest()
    {
        var content = TestGrids.SampleContent;
        var root = Path.Combine(TestGrids.RepoRoot(), "content");
        var maps = content.Maps.All.Select(m => m.Id).ToList();
        maps.ShouldNotBeEmpty();

        var none = ContentValidator.Validate(content, root, new HashSet<string>(), [new AssetKeyRule()]);
        var missing = none.Issues.Where(i => i.Path.EndsWith("minimap", StringComparison.Ordinal)).ToList();
        missing.Count.ShouldBe(maps.Count);
        missing.ShouldAllBe(i => i.Code == ValidationCodes.AssetMissing && i.Severity == ValidationSeverity.Error);
        foreach (var id in maps) missing.ShouldContain(i => i.Message.Contains("'" + MinimapKeys.For(id) + "'"));

        var keys = new HashSet<string>(maps.Select(MinimapKeys.For));
        ContentValidator.Validate(content, root, keys, [new AssetKeyRule()]).Issues.ShouldNotContain(i => i.Path.EndsWith("minimap", StringComparison.Ordinal));
        MinimapKeys.For("map.starter_meadow").ShouldBe("minimap.map.starter_meadow");
    }

    private static byte[] Flags(MapGrid grid)
    {
        var flags = new byte[grid.Width * grid.Height];
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
                flags[y * grid.Width + x] = (byte)((grid.IsWalkable(x, y) ? CellFlagBits.Walkable : 0) | (grid.BlocksLos(x, y) ? CellFlagBits.BlocksLos : 0));
        return flags;
    }

    private static List<(string Type, byte[] Data, bool CrcOk)> Chunks(byte[] png)
    {
        var list = new List<(string, byte[], bool)>();
        var at = 8;
        while (at < png.Length)
        {
            var length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.Skip(at + 8).Take(length).ToArray();
            var stored = (uint)((png[at + 8 + length] << 24) | (png[at + 9 + length] << 16) | (png[at + 10 + length] << 8) | png[at + 11 + length]);
            var crc = Crc(png.AsSpan(at + 4, 4 + length));
            list.Add((type, data, crc == stored));
            at += 12 + length;
        }
        return list;
    }

    // The PNG specification's CRC-32, bit by bit (independent of the writer's table).
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
