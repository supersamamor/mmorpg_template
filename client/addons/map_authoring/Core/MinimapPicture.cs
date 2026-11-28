using System;
using System.Globalization;
using System.IO;
using Game.Shared.World;

namespace Game.Client.MapAuthoring;

/// <summary>How the export draws a map's minimap picture (T-812): from the plugin's <c>config.json</c> <c>minimap</c> section, so the colours are a theme's choice, not code.</summary>
/// <param name="PixelsPerCell">Square pixels per cell, before <paramref name="MaxSidePx"/> caps it.</param>
/// <param name="MaxSidePx">The longest side the picture may have (a phone's texture limit and memory); a large map gets fewer pixels per cell, at least one.</param>
/// <param name="Blocked">RGBA of a cell that is not walkable and does not block line of sight (water, a gap, outside the ground).</param>
/// <param name="Walkable">RGBA of a walkable cell.</param>
/// <param name="BlocksLos">RGBA of a cell that blocks line of sight (walls, rocks), walkable or not.</param>
public sealed record MinimapStyle(int PixelsPerCell, int MaxSidePx, uint Blocked, uint Walkable, uint BlocksLos)
{
    /// <summary>What the export uses when <c>config.json</c> has no <c>minimap</c> section.</summary>
    public static MinimapStyle Default { get; } = new(4, 2048, 0x00000000, 0xC9D8A8E6, 0x5B4636F0);

    /// <summary>Reads <c>#rrggbb</c> or <c>#rrggbbaa</c> as RGBA; null when it is neither.</summary>
    public static uint? ParseColor(string? text)
    {
        if (text is null || text.Length is not (7 or 9) || text[0] != '#') return null;
        if (!uint.TryParse(text.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;
        return text.Length == 7 ? (value << 8) | 0xFF : value;
    }
}

/// <summary>
/// A map's minimap picture (T-812, ARCHITECTURE §5.3), drawn once by the map export from the baked and painted cell flags, so the game never renders the world a second time:
/// one colour per kind of cell, <see cref="MinimapStyle.PixelsPerCell"/> square pixels per cell, row 0 at the top (north, as the camera looks). The picture shows exactly what
/// the display bundle's cell flags already tell the client, nothing of spawns or anything else server-only (hard rule 11). Pure, and the bytes are canonical: an indexed
/// PNG with stored (uncompressed) deflate blocks, so the same grid gives the same bytes on any machine and runtime, and <c>export-maps.ps1 -Check</c> compares them.
/// </summary>
public static class MinimapPicture
{
    private const byte IndexBlocked = 0, IndexWalkable = 1, IndexBlocksLos = 2;

    /// <summary>Pixels per cell for a map of this size: the style's, lowered until the longest side fits <see cref="MinimapStyle.MaxSidePx"/>, at least 1.</summary>
    public static int PixelsPerCell(int width, int height, MinimapStyle style)
    {
        var longest = Math.Max(1, Math.Max(width, height));
        return Math.Max(1, Math.Min(style.PixelsPerCell, style.MaxSidePx / longest));
    }

    /// <summary>The picture for <paramref name="flags"/> (row-major, <c>width * height</c> bytes, <see cref="CellFlagBits"/>).</summary>
    public static byte[] Draw(byte[] flags, int width, int height, MinimapStyle style)
    {
        if (width < 1 || height < 1 || flags.Length != width * height) throw new ArgumentException($"flags must be {width}x{height} bytes", nameof(flags));
        var scale = PixelsPerCell(width, height, style);
        var pixelsWide = width * scale;
        var rows = height * scale;
        var stride = pixelsWide + 1;   // a filter byte (0, none) before every scanline
        var raw = new byte[stride * rows];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var f = flags[y * width + x];
                var index = (f & CellFlagBits.BlocksLos) != 0 ? IndexBlocksLos : (f & CellFlagBits.Walkable) != 0 ? IndexWalkable : IndexBlocked;
                for (var py = 0; py < scale; py++)
                {
                    var at = (y * scale + py) * stride + 1 + x * scale;
                    for (var px = 0; px < scale; px++) raw[at + px] = index;
                }
            }
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        var header = new byte[13];
        BigEndian(header, 0, (uint)pixelsWide);
        BigEndian(header, 4, (uint)rows);
        header[8] = 8;   // bit depth
        header[9] = 3;   // indexed colour
        Chunk(png, "IHDR", header);
        var colors = new[] { style.Blocked, style.Walkable, style.BlocksLos };
        var palette = new byte[colors.Length * 3];
        var alpha = new byte[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            palette[i * 3] = (byte)(colors[i] >> 24);
            palette[i * 3 + 1] = (byte)(colors[i] >> 16);
            palette[i * 3 + 2] = (byte)(colors[i] >> 8);
            alpha[i] = (byte)colors[i];
        }
        Chunk(png, "PLTE", palette);
        Chunk(png, "tRNS", alpha);
        Chunk(png, "IDAT", StoredZlib(raw));
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>A picture this class wrote, read back (for the tests): its size in pixels and each pixel's palette index (0 blocked, 1 walkable, 2 blocks line of sight); null for anything else.</summary>
    public static (int Width, int Height, byte[] Indices)? Read(byte[] png)
    {
        try
        {
            if (png.Length < 8 + 25 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return null;
            var at = 8;
            int width = 0, height = 0;
            var data = new MemoryStream();
            while (at + 8 <= png.Length)
            {
                var length = (int)ReadBigEndian(png, at);
                var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
                if (type == "IHDR")
                {
                    width = (int)ReadBigEndian(png, at + 8);
                    height = (int)ReadBigEndian(png, at + 12);
                    if (png[at + 16] != 8 || png[at + 17] != 3) return null;
                }
                else if (type == "IDAT")
                {
                    data.Write(png, at + 8, length);
                }
                at += 12 + length;
            }
            var zlib = data.ToArray();
            var raw = new MemoryStream();
            // Stored blocks only: after the two zlib header bytes, each block is 1 flag byte, LEN, NLEN, then LEN bytes.
            var p = 2;
            while (p < zlib.Length - 4)
            {
                var last = zlib[p] & 1;
                var len = zlib[p + 1] | (zlib[p + 2] << 8);
                raw.Write(zlib, p + 5, len);
                p += 5 + len;
                if (last == 1) break;
            }
            var bytes = raw.ToArray();
            if (bytes.Length != (width + 1) * height) return null;
            var indices = new byte[width * height];
            for (var y = 0; y < height; y++) Array.Copy(bytes, y * (width + 1) + 1, indices, y * width, width);
            return (width, height, indices);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[] StoredZlib(byte[] raw)
    {
        using var z = new MemoryStream();
        z.WriteByte(0x78);
        z.WriteByte(0x01);
        var offset = 0;
        do
        {
            var length = Math.Min(65535, raw.Length - offset);
            var last = offset + length >= raw.Length;
            z.WriteByte((byte)(last ? 1 : 0));
            z.WriteByte((byte)length);
            z.WriteByte((byte)(length >> 8));
            z.WriteByte((byte)~length);
            z.WriteByte((byte)(~length >> 8));
            z.Write(raw, offset, length);
            offset += length;
        }
        while (offset < raw.Length);
        var adler = Adler32(raw);
        var tail = new byte[4];
        BigEndian(tail, 0, adler);
        z.Write(tail, 0, 4);
        return z.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        png.Write(head, 0, 8);
        png.Write(data, 0, data.Length);
        var crc = Crc32(head, 4, 4, 0xFFFFFFFFu);
        crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        BigEndian(tail, 0, crc);
        png.Write(tail, 0, 4);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data, int offset, int count, uint crc)
    {
        for (var i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static void BigEndian(byte[] into, int at, uint value)
    {
        into[at] = (byte)(value >> 24);
        into[at + 1] = (byte)(value >> 16);
        into[at + 2] = (byte)(value >> 8);
        into[at + 3] = (byte)value;
    }

    private static uint ReadBigEndian(byte[] from, int at) => ((uint)from[at] << 24) | ((uint)from[at + 1] << 16) | ((uint)from[at + 2] << 8) | from[at + 3];
}
