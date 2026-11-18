using Game.Client.Core;
using Game.Client.Core.Theme;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The storybook toon look (ADR-003, T-222), from the theme: the environment scene (sky gradient, flat ambient, haze, the warm key light with the player's one
/// shadow), the toon material that replaces the plain materials of models and map meshes, and the outline material drawn over nearby characters. Which files
/// those are is theme data (<see cref="RenderPlan"/>, the manifest's <c>"render"</c> tags); a theme without them keeps the look from before T-222, with one warning.
/// <para>Conversion keeps one surface per character (§9): an untextured opaque <see cref="BaseMaterial3D"/> becomes a toon material with the same colour, vertex
/// colours and texture, and identical looks share one material. Anything else (unshaded, transparent, already a shader) is left as it is.</para>
/// </summary>
public sealed class ToonLook
{
    private readonly ShaderMaterial? _toon;
    private readonly Dictionary<ulong, Material> _bySource = new();
    private readonly Dictionary<string, ShaderMaterial> _byLook = new(StringComparer.Ordinal);

    private ToonLook(RenderPlan plan, ShaderMaterial? toon, Material? outline)
    {
        Plan = plan;
        _toon = toon;
        Outline = outline;
    }

    /// <summary>What the theme gave.</summary>
    public RenderPlan Plan { get; }

    /// <summary>True when models and map meshes are drawn with the toon material.</summary>
    public bool Enabled => _toon is not null;

    /// <summary>The outline overlay material, or null (no outlines).</summary>
    public Material? Outline { get; }

    /// <summary>Distinct toon materials made so far (for measuring: identical looks share one).</summary>
    public int Materials => _byLook.Count;

    /// <summary>
    /// Reads the theme's render entries, puts the environment under <paramref name="parent"/> (the theme's scene, or the built-in light and background), and
    /// loads the materials. With <c>toon_shading</c> off everything is the look from before T-222.
    /// </summary>
    public static ToonLook Mount(Node3D parent, ClientConfig config, ThemeCatalog catalog)
    {
        var plan = config.ToonShading ? RenderPlan.Resolve(catalog) : RenderPlan.Off;
        if (plan.Environment is { } environment && ResourceLoader.Load<PackedScene>("res://" + environment)?.Instantiate() is Node node) parent.AddChild(node);
        else AddBuiltInEnvironment(parent, plan.Environment is not null);
        var toon = plan.Toon is { } t ? Load<ShaderMaterial>(t) : null;
        var outline = plan.Outline is { } o ? Load<Material>(o) : null;
        GD.Print($"[client] look: {(toon is null ? "plain (before T-222)" : "toon")}, outlines {(outline is null ? "off" : $"within {config.OutlineDistanceCells:0.#} cells, nearest {config.OutlineMaxCount}")}, environment {(plan.Environment ?? "built-in")}");
        return new ToonLook(plan, toon, outline);
    }

    private static T? Load<T>(string path) where T : Resource
    {
        if (ResourceLoader.Load("res://" + path) is T loaded) return loaded;
        GD.PushWarning($"[theme] {path} is not a {typeof(T).Name}; drawing the look from before T-222 for it");
        return null;
    }

    /// <summary>The light and background from before T-222: what a theme without an environment gets (and the refusal screens, which come before the theme).</summary>
    public static void AddBuiltInEnvironment(Node3D parent, bool broken = false)
    {
        if (broken) GD.PushWarning("[theme] the environment scene could not be instanced; using the built-in light");
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = PlaceholderColors.Background,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = 0.45f,
        };
        parent.AddChild(new WorldEnvironment { Environment = environment });
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -30, 0),
            LightEnergy = 0.75f,
            ShadowEnabled = true, // only the player casts (§9)
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = 40f,
        });
    }

    /// <summary>The toon material for <paramref name="source"/>, or <paramref name="source"/> itself when it is not one to convert (or the look is off).</summary>
    /// <remarks>
    /// Vertex colours always count: glTF says COLOR_0 multiplies the base colour, but Godot's importer leaves the material's vertex-colour flag off, and the
    /// generated models keep their palette colours there (a mesh without vertex colours reads white, so nothing changes for it). With the look off, a model's
    /// material only gets that flag turned on, so the placeholders keep their colours in the look from before T-222.
    /// </remarks>
    public Material? Convert(Material? source)
    {
        if (source is not BaseMaterial3D plain) return source;
        var id = plain.GetInstanceId();
        if (_bySource.TryGetValue(id, out var known)) return known;
        if (plain.Transparency != BaseMaterial3D.TransparencyEnum.Disabled || plain.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded || plain.VertexColorIsSrgb)
            return _bySource[id] = plain;
        if (_toon is null)
        {
            if (plain.VertexColorUseAsAlbedo) return _bySource[id] = plain;
            var coloured = (BaseMaterial3D)plain.Duplicate();
            coloured.VertexColorUseAsAlbedo = true;
            return _bySource[id] = coloured;
        }
        var texture = plain.AlbedoTexture;
        var look = $"{plain.AlbedoColor.ToHtml()}:{texture?.GetInstanceId() ?? 0}";
        if (!_byLook.TryGetValue(look, out var toon))
        {
            toon = (ShaderMaterial)_toon.Duplicate();
            toon.SetShaderParameter("albedo", plain.AlbedoColor);
            toon.SetShaderParameter("use_vertex_color", true);
            if (texture is not null) toon.SetShaderParameter("albedo_texture", texture);
            _byLook[look] = toon;
        }
        return _bySource[id] = toon;
    }

    /// <summary>Gives every surface of <paramref name="node"/> its toon material (as a surface override; the mesh is shared and untouched). Call again after the mesh changes.</summary>
    public void Apply(MeshInstance3D? node)
    {
        if (node?.Mesh is not { } mesh) return;
        var count = mesh.GetSurfaceCount();
        for (var s = 0; s < count; s++)
        {
            var source = mesh.SurfaceGetMaterial(s);
            var toon = Convert(source);
            node.SetSurfaceOverrideMaterial(s, ReferenceEquals(toon, source) ? null : toon);
        }
    }

    /// <summary>Converts every mesh under <paramref name="root"/> (a map scene, ADR-003 point 6: terrain and props use the same ramp, without outlines).</summary>
    public void ApplyTree(Node root)
    {
        if (_toon is null) return;
        if (root is MeshInstance3D node) Apply(node);
        foreach (var child in root.GetChildren()) ApplyTree(child);
    }
}
