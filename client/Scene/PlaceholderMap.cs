using Game.Client.Core.Bundle;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The ground as one batched mesh from the bundle's grid: a quad per cell, darker for blocked cells, raised blocks for cells that block line of
/// sight. One surface and one material, so one draw call. It is only for looking at: picking uses the ground plane, and height stays visual (§4).
/// </summary>
public sealed class PlaceholderMap : IMapView
{
    private const float WallHeight = 0.7f;

    private readonly MeshInstance3D _ground = new() { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };

    /// <summary>Adds the mesh under <paramref name="parent"/>.</summary>
    public PlaceholderMap(Node3D parent)
    {
        parent.AddChild(_ground);
        _ground.MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1f, MetallicSpecular = 0f };
    }

    /// <summary>Triangles of the current ground (for reporting).</summary>
    public int Triangles { get; private set; }

    /// <inheritdoc />
    public void Load(MapData map)
    {
        var warp = new HashSet<(int, int)>();
        foreach (var cell in map.Warps) warp.Add((cell.X, cell.Y));
        var safe = new bool[map.Width * map.Height];
        foreach (var zone in map.Zones)
        {
            if (zone.Kind != ZoneKinds.Safe) continue;
            for (var y = zone.Area.Y; y < zone.Area.Y + zone.Area.H; y++)
                for (var x = zone.Area.X; x < zone.Area.X + zone.Area.W; x++)
                    if (map.Inside(x, y)) safe[y * map.Width + x] = true;
        }

        var builder = new Builder(map.Width * map.Height);
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                var even = (x + y) % 2 == 0;
                if (map.BlocksLos(x, y))
                {
                    builder.Block(x, y, map);
                }
                else if (!map.IsWalkable(x, y))
                {
                    builder.Flat(x, y, PlaceholderColors.Blocked);
                }
                else if (warp.Contains((x, y)))
                {
                    builder.Flat(x, y, PlaceholderColors.Warp);
                }
                else if (safe[y * map.Width + x])
                {
                    builder.Flat(x, y, even ? PlaceholderColors.SafeA : PlaceholderColors.SafeB);
                }
                else
                {
                    builder.Flat(x, y, even ? PlaceholderColors.GroundA : PlaceholderColors.GroundB);
                }
            }
        }
        _ground.Mesh = builder.ToMesh();
        Triangles = builder.Triangles;
    }

    private sealed class Builder
    {
        private readonly List<Vector3> _vertices;
        private readonly List<Vector3> _normals;
        private readonly List<Color> _colors;
        private readonly List<int> _indices;

        public Builder(int cells)
        {
            _vertices = new List<Vector3>(cells * 4);
            _normals = new List<Vector3>(cells * 4);
            _colors = new List<Color>(cells * 4);
            _indices = new List<int>(cells * 6);
        }

        public int Triangles => _indices.Count / 3;

        public void Flat(int x, int y, Color color) => Top(x, y, 0f, color);

        public void Block(int x, int y, MapData map)
        {
            Top(x, y, WallHeight, PlaceholderColors.WallTop);
            float x0 = x, x1 = x + 1, z0 = y, z1 = y + 1, h = WallHeight;
            var c = PlaceholderColors.Wall;
            // Only faces next to open cells; the camera looks north, so the north faces never show but are cheap.
            if (y + 1 < map.Height && !map.BlocksLos(x, y + 1)) Quad(new(x0, h, z1), new(x1, h, z1), new(x1, 0, z1), new(x0, 0, z1), Vector3.Back, c);
            if (y > 0 && !map.BlocksLos(x, y - 1)) Quad(new(x1, h, z0), new(x0, h, z0), new(x0, 0, z0), new(x1, 0, z0), Vector3.Forward, c);
            if (x + 1 < map.Width && !map.BlocksLos(x + 1, y)) Quad(new(x1, h, z1), new(x1, h, z0), new(x1, 0, z0), new(x1, 0, z1), Vector3.Right, c);
            if (x > 0 && !map.BlocksLos(x - 1, y)) Quad(new(x0, h, z0), new(x0, h, z1), new(x0, 0, z1), new(x0, 0, z0), Vector3.Left, c);
        }

        public ArrayMesh ToMesh()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }

        private void Top(int x, int y, float h, Color color) =>
            Quad(new(x, h, y), new(x + 1, h, y), new(x + 1, h, y + 1), new(x, h, y + 1), Vector3.Up, color);

        // Corners clockwise seen from the side the normal points at, which is Godot's front face.
        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color color)
        {
            var i = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _vertices.Add(d);
            for (var n = 0; n < 4; n++)
            {
                _normals.Add(normal);
                _colors.Add(color);
            }
            _indices.Add(i);
            _indices.Add(i + 1);
            _indices.Add(i + 2);
            _indices.Add(i);
            _indices.Add(i + 2);
            _indices.Add(i + 3);
        }
    }
}
