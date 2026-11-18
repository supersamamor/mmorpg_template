using System.Diagnostics;
using Game.Client.Core.Bundle;
using Game.Client.Core.Theme;
using Game.Client.Core.World;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The map's look from the theme (§5.3): the bundle's scene key, resolved by the catalog (active theme, then default), instanced under the world. A scene that cannot be found or loaded falls back to
/// <see cref="PlaceholderMap"/>, with one warning per key. Once the scene's collision reaches the physics server, one downward ray per walkable cell fills <see cref="GroundHeights"/>, so entities stand on the terrain with
/// no per-frame query (§4, §9). Height is only for looking at: the simulation never sees it. The authoring scene (markers, paint) is not in the app.
/// </summary>
public sealed class ThemeMapView : IMapView
{
    // Frames for new bodies to reach the physics server's space.
    private const int SettleFrames = 3;
    private const float RayTop = 512f;
    private const float RayBottom = -512f;

    private readonly Node3D _parent;
    private readonly ThemeCatalog _catalog;
    private readonly PlaceholderMap _placeholder;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly ToonLook? _look;
    private Node3D? _scene;
    private int _loads;

    /// <summary>Works under <paramref name="parent"/>, with the theme's <paramref name="catalog"/>.</summary>
    public ThemeMapView(Node3D parent, ThemeCatalog catalog, ToonLook? look = null)
    {
        _parent = parent;
        _catalog = catalog;
        _look = look;
        _placeholder = new PlaceholderMap(parent);
    }

    /// <summary>The ground's height, for placing things on it.</summary>
    public GroundHeights Ground { get; } = new();

    /// <summary>Triangles of the current map's meshes (for reporting).</summary>
    public int Triangles { get; private set; }

    /// <summary>True when the current map came from the theme, false when it is the placeholder.</summary>
    public bool ShowsScene => _scene is not null;

    /// <summary>The theme's map scene while one is shown (for measuring), else null.</summary>
    public Node3D? Scene => _scene;

    /// <inheritdoc />
    public void Load(MapData map)
    {
        var load = ++_loads;
        if (_scene is not null)
        {
            _parent.RemoveChild(_scene);
            _scene.QueueFree();
            _scene = null;
        }
        Ground.Clear();

        var clock = Stopwatch.StartNew();
        var scene = Instantiate(map.SceneKey);
        if (scene is null)
        {
            _placeholder.Load(map);
            _placeholder.Visible = true;
            Triangles = _placeholder.Triangles;
            return;
        }

        _placeholder.Visible = false;
        _parent.AddChild(scene);
        _scene = scene;
        Triangles = Prepare(scene);
        _look?.ApplyTree(scene); // terrain and props in the same three-step ramp, never outlined (ADR-003 point 6)
        GD.Print($"[client] map {map.Id}: scene {map.SceneKey} in {clock.Elapsed.TotalMilliseconds:0.0} ms, {Triangles} triangles");
        _ = SampleHeightsAsync(load, scene, map);
    }

    private Node3D? Instantiate(string key)
    {
        if (!_catalog.ResolveScene(key, out var path)) return null;
        var packed = ResourceLoader.Load<PackedScene>("res://" + path);
        if (packed?.Instantiate() is Node3D scene) return scene;
        if (_warned.Add(key)) GD.PushWarning($"[theme] asset '{key}': {path} is not a 3D scene; using a fallback");
        return null;
    }

    // Shadows are for the player only (§9), whatever the scene says. Counts the triangles while walking the tree.
    private static int Prepare(Node node)
    {
        var triangles = 0;
        if (node is MeshInstance3D { Mesh: { } mesh } instance)
        {
            instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            triangles += mesh.GetFaces().Length / 3;
        }
        foreach (var child in node.GetChildren()) triangles += Prepare(child);
        return triangles;
    }

    private async Task SampleHeightsAsync(int load, Node3D scene, MapData map)
    {
        for (var i = 0; i < SettleFrames; i++) await _parent.ToSignal(_parent.GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (load != _loads || !GodotObject.IsInstanceValid(scene)) return;

        var clock = Stopwatch.StartNew();
        var space = scene.GetWorld3D().DirectSpaceState;
        var heights = new float[map.Width * map.Height];
        var query = new PhysicsRayQueryParameters3D { CollideWithAreas = false };
        var rays = 0;
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                if (!map.IsWalkable(x, y)) continue;
                query.From = new Vector3(x + 0.5f, RayTop, y + 0.5f);
                query.To = new Vector3(x + 0.5f, RayBottom, y + 0.5f);
                var hit = space.IntersectRay(query);
                rays++;
                if (hit.Count > 0) heights[y * map.Width + x] = hit["position"].AsVector3().Y;
            }
        }
        Ground.Set(map.Width, map.Height, heights, map.Cells);
        GD.Print($"[client] map {map.Id}: {rays} ground rays in {clock.Elapsed.TotalMilliseconds:0.0} ms");
    }
}
