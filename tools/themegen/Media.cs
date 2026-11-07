using System.Numerics;
using System.Text;

namespace Game.ThemeGen;

/// <summary>Tiny, dependency-free writers for the non-model files of the pack. Output is deterministic (no compression library, no timestamps).</summary>
internal static class Media
{
    // ---------------- PNG: 64x64, 2 bits per pixel, palette, stored (uncompressed) deflate ----------------

    public static byte[] Png(Vector4 baseColor, uint hash)
    {
        const int size = 64;
        var palette = new[]
        {
            baseColor, new Vector4(0.1f, 0.1f, 0.12f, 1), new Vector4(1, 1, 1, 1), new Vector4(baseColor.Z, baseColor.X, baseColor.Y, 1),
        };
        var stripe = 8 + (int)(hash % 9);
        var raw = new List<byte>();
        for (var y = 0; y < size; y++)
        {
            raw.Add(0); // filter: none
            for (var x = 0; x < size; x += 4)
            {
                byte packed = 0;
                for (var i = 0; i < 4; i++)
                {
                    var px = x + i;
                    var edge = px < 3 || y < 3 || px >= size - 3 || y >= size - 3;
                    var index = edge ? 1 : ((px + y) % stripe < 3 ? 2 : (px > size / 2 && y > size / 2 ? 3 : 0));
                    packed |= (byte)(index << (6 - 2 * i));
                }
                raw.Add(packed);
            }
        }

        var idat = new List<byte> { 0x78, 0x01 };
        idat.Add(1); // final stored block
        idat.AddRange(BitConverter.GetBytes((ushort)raw.Count));
        idat.AddRange(BitConverter.GetBytes((ushort)~raw.Count));
        idat.AddRange(raw);
        idat.AddRange(BigEndian(Adler32(raw)));

        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var header = new List<byte>();
        header.AddRange(BigEndian(size));
        header.AddRange(BigEndian(size));
        header.AddRange(new byte[] { 2, 3, 0, 0, 0 }); // bit depth 2, colour type 3 (palette)
        Chunk(png, "IHDR", header.ToArray());
        Chunk(png, "PLTE", palette.SelectMany(c => new[] { Byte(c.X), Byte(c.Y), Byte(c.Z) }).ToArray());
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private static byte[] BigEndian(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] BigEndian(int v) => BigEndian((uint)v);

    private static void Chunk(List<byte> png, string type, byte[] data)
    {
        png.AddRange(BigEndian(data.Length));
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.AddRange(body);
        png.AddRange(BigEndian(Crc32(body)));
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static uint Adler32(List<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var v in data)
        {
            a = (a + v) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    // ---------------- OGG Vorbis: about 0.1 s of silence, written bit by bit ----------------

    /// <summary>A valid Ogg Vorbis stream: mono, 22050 Hz, one short-block mode, floors that are all "unused", so it decodes to silence. A few hundred bytes.</summary>
    public static byte[] SilentOgg()
    {
        const int packets = 18;
        var ident = new List<byte>();
        ident.Add(1);
        ident.AddRange(Encoding.ASCII.GetBytes("vorbis"));
        ident.AddRange(BitConverter.GetBytes(0u));           // version
        ident.Add(1);                                         // channels
        ident.AddRange(BitConverter.GetBytes(22050u));       // sample rate
        ident.AddRange(BitConverter.GetBytes(0));            // bitrate max
        ident.AddRange(BitConverter.GetBytes(0));            // nominal
        ident.AddRange(BitConverter.GetBytes(0));            // min
        ident.Add(0x88);                                      // both block sizes 256
        ident.Add(1);                                         // framing

        var comment = new List<byte>();
        comment.Add(3);
        comment.AddRange(Encoding.ASCII.GetBytes("vorbis"));
        var vendor = Encoding.ASCII.GetBytes("themegen");
        comment.AddRange(BitConverter.GetBytes((uint)vendor.Length));
        comment.AddRange(vendor);
        comment.AddRange(BitConverter.GetBytes(0u));
        comment.Add(1);

        var setup = new BitWriter();
        setup.Bits(5, 8);
        foreach (var c in "vorbis") setup.Bits(c, 8);
        setup.Bits(0, 8);                                     // one codebook
        setup.Bits(0x42, 8); setup.Bits(0x43, 8); setup.Bits(0x56, 8); // sync pattern "BCV"
        setup.Bits(1, 16);                                    // dimensions
        setup.Bits(2, 24);                                    // entries
        setup.Bits(0, 1);                                     // not ordered
        setup.Bits(0, 1);                                     // not sparse
        setup.Bits(0, 5); setup.Bits(0, 5);                   // both entries have length 1
        setup.Bits(0, 4);                                     // no lookup table
        setup.Bits(0, 6); setup.Bits(0, 16);                  // one time-domain transform, value 0
        setup.Bits(0, 6); setup.Bits(1, 16);                  // one floor, type 1
        setup.Bits(0, 5);                                     // no partitions
        setup.Bits(0, 2);                                     // multiplier 1
        setup.Bits(7, 4);                                     // range bits
        setup.Bits(0, 6); setup.Bits(0, 16);                  // one residue, type 0
        setup.Bits(0, 24); setup.Bits(0, 24); setup.Bits(0, 24); // begin, end, partition size - 1
        setup.Bits(0, 6);                                     // one classification
        setup.Bits(0, 8);                                     // classbook 0
        setup.Bits(0, 3); setup.Bits(0, 1);                   // cascade: no books
        setup.Bits(0, 6); setup.Bits(0, 16);                  // one mapping, type 0
        setup.Bits(0, 1); setup.Bits(0, 1); setup.Bits(0, 2); // one submap, no coupling, reserved
        setup.Bits(0, 8); setup.Bits(0, 8); setup.Bits(0, 8); // time (unused), floor 0, residue 0
        setup.Bits(0, 6);                                     // one mode
        setup.Bits(0, 1); setup.Bits(0, 16); setup.Bits(0, 16); setup.Bits(0, 8); // short block, window 0, transform 0, mapping 0
        setup.Bits(1, 1);                                     // framing
        var setupBytes = setup.ToArray();

        var ogg = new List<byte>();
        Page(ogg, 2, 0, 0, new List<byte[]> { ident.ToArray() });
        Page(ogg, 0, 0, 1, new List<byte[]> { comment.ToArray(), setupBytes });
        var audio = new List<byte[]>();
        for (var i = 0; i < packets; i++) audio.Add(new byte[] { 0 });  // packet type 0, floor "unused": silence
        Page(ogg, 4, (packets - 1) * 128L, 2, audio);
        return ogg.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bit;

        public void Bits(long value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (_bit == 0) _bytes.Add(0);
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(1 << _bit);
                _bit = (_bit + 1) % 8;
            }
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    private static void Page(List<byte> output, byte headerType, long granule, uint sequence, List<byte[]> packets)
    {
        var segments = new List<byte>();
        var data = new List<byte>();
        foreach (var packet in packets)
        {
            var left = packet.Length;
            while (left >= 255)
            {
                segments.Add(255);
                left -= 255;
            }
            segments.Add((byte)left);
            data.AddRange(packet);
        }
        var page = new List<byte>();
        page.AddRange(Encoding.ASCII.GetBytes("OggS"));
        page.Add(0);
        page.Add(headerType);
        page.AddRange(BitConverter.GetBytes(granule));
        page.AddRange(BitConverter.GetBytes(0x7E0207u)); // serial number
        page.AddRange(BitConverter.GetBytes(sequence));
        var checksumAt = page.Count;
        page.AddRange(new byte[4]);
        page.Add((byte)segments.Count);
        page.AddRange(segments);
        page.AddRange(data);
        var crc = OggCrc(page);
        page.RemoveRange(checksumAt, 4);
        page.InsertRange(checksumAt, BitConverter.GetBytes(crc));
        output.AddRange(page);
    }

    private static uint OggCrc(List<byte> data)
    {
        var crc = 0u;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var k = 0; k < 8; k++) crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0x04C11DB7u : crc << 1;
        }
        return crc;
    }

    // ---------------- Godot scenes ----------------

    public const string MapScene = "[gd_scene format=3]\n\n[node name=\"PlaceholderMap\" type=\"Node3D\"]\n";

    public const string VfxScene = "[gd_scene format=3]\n\n[node name=\"PlaceholderEffect\" type=\"Node3D\"]\n";
}
