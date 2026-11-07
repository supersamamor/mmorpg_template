using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Game.Content.Maps;

/// <summary>What <see cref="MapSceneHash.Compute"/> returns.</summary>
/// <param name="Hash">Lowercase hex SHA-256 of the canonical text.</param>
/// <param name="Problems">Resources the scene names that could not be read. A hash with problems is never a match.</param>
public sealed record MapSceneHashResult(string Hash, IReadOnlyList<string> Problems);

/// <summary>
/// The <c>source_scene_hash</c> of a map (ARCHITECTURE §5.3). It covers what can change the logic grid and nothing else, so a new material does not make a grid stale, and it is
/// computed from the scene's text, so <c>validate</c> needs no Godot. Compiled into the client too (the exporter), so both sides compute the same value.
/// <list type="bullet">
/// <item>Kept whole: the root, every physics or otherwise unknown node, every instanced scene, every node with <c>map_</c> metadata. Kept as a transform only: their ancestors.
/// Left out: anything else, which means purely visual nodes and empty containers (<see cref="VisualTypes"/>).</item>
/// <item>Left out as noise a re-save changes: the <c>gd_scene</c> header, <c>uid</c>, <c>unique_id</c>, section order, resource ids (a reference is replaced by what it refers to), white space, line endings.</item>
/// <item>An instanced <c>.tscn</c> in a theme is hashed by these same rules. Any other theme file is hashed by content: Git LFS's pointer holds the SHA-256 of the real file, so the value is
/// the same with or without the LFS files checked out. A binary file's own dependencies are not followed. Files outside the themes (scripts) count by path.</item>
/// </list>
/// </summary>
public static class MapSceneHash
{
    /// <summary>The authoring scene sits next to the map's visual scene, with this ending in place of <c>.tscn</c>. It holds markers and overrides and never ships (hard rule 11).</summary>
    public const string AuthoringSuffix = ".authoring.tscn";

    /// <summary>Metadata starting with this is read by the bake and the export.</summary>
    public const string MapMetadataPrefix = "metadata/map_";

    private const string ThemesRoot = "res://themes/";
    private const string LfsPointerStart = "version https://git-lfs";
    private const string LfsOid = "oid sha256:";
    private const int MaxDepth = 16;

    // Bumped when the canonical text or the way grids are baked changes, so old grids count as stale.
    private const string Version = "map-scene-hash 1\n";

    private static readonly Regex SubResourceReference = new Regex("SubResource\\(\"([^\"]+)\"\\)", RegexOptions.CultureInvariant);
    private static readonly Regex ExtResourceReference = new Regex("ExtResource\\(\"([^\"]+)\"\\)", RegexOptions.CultureInvariant);

    /// <summary>Node types that cannot change the bake: the ray sees collision only.</summary>
    public static readonly IReadOnlyCollection<string> VisualTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "MeshInstance3D", "MultiMeshInstance3D", "Sprite3D", "AnimatedSprite3D", "Label3D", "Decal", "GPUParticles3D", "CPUParticles3D",
        "OmniLight3D", "SpotLight3D", "DirectionalLight3D", "WorldEnvironment", "ReflectionProbe", "VoxelGI", "LightmapGI", "FogVolume",
        "AudioStreamPlayer", "AudioStreamPlayer3D", "AudioListener3D", "AnimationPlayer", "AnimationTree", "Camera3D",
    };

    private static readonly HashSet<string> Containers = new HashSet<string>(StringComparer.Ordinal) { "Node", "Node3D", "Marker3D" };
    private static readonly string[] TransformProperties = { "transform", "top_level", "position", "rotation", "scale", "quaternion", "basis", "rotation_order", "rotation_edit_mode" };

    /// <summary>The path of the authoring scene of a map's visual scene.</summary>
    public static string AuthoringPathOf(string scenePath) =>
        scenePath.EndsWith(".tscn", StringComparison.Ordinal) ? scenePath.Substring(0, scenePath.Length - ".tscn".Length) + AuthoringSuffix : scenePath + AuthoringSuffix;

    /// <summary>Hashes authoring scene text.</summary>
    /// <param name="sceneText">The <c>.tscn</c> text. A <see cref="FormatException"/> means it cannot be read as a scene.</param>
    /// <param name="readResource">The bytes of a <c>res://themes/...</c> file, or null when it does not exist.</param>
    public static MapSceneHashResult Compute(string sceneText, Func<string, byte[]?> readResource)
    {
        var problems = new List<string>();
        var canonical = Version + Canonical(sceneText, readResource, problems, 0);
        return new MapSceneHashResult(Sha256(Encoding.UTF8.GetBytes(canonical)), problems);
    }

    private static string Canonical(string sceneText, Func<string, byte[]?> read, List<string> problems, int depth)
    {
        var sections = TscnFile.Parse(sceneText);
        var scene = new SceneRefs(sections, read, problems, depth);
        var nodes = sections.Where(s => s.Tag == "node").ToList();
        var kept = new Dictionary<string, (TscnSection Node, bool Whole)>(StringComparer.Ordinal);
        foreach (var node in nodes)
            if (IsWhole(node)) kept[PathOf(node)] = (node, true);

        // Ancestors count only through their transforms. A second pass, so the order of the file does not matter.
        var known = new Dictionary<string, TscnSection>(StringComparer.Ordinal);
        foreach (var node in nodes) known[PathOf(node)] = node;
        foreach (var path in kept.Keys.ToList())
        {
            for (var at = ParentOf(path); at is not null; at = ParentOf(at))
                if (known.TryGetValue(at, out var ancestor) && !kept.ContainsKey(at)) kept[at] = (ancestor, false);
        }

        var text = new StringBuilder();
        foreach (var pair in kept.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var (node, whole) = pair.Value;
            text.Append("node ").Append(pair.Key).Append('|').Append(node.Text("type") ?? "");
            if (node.Attributes.TryGetValue("instance", out var instance)) text.Append("|instance=").Append(scene.Resolve(TscnFile.Squeeze(instance)));
            if (node.Attributes.TryGetValue("groups", out var groups)) text.Append("|groups=").Append(TscnFile.Squeeze(groups));
            text.Append('\n');
            foreach (var property in node.Properties.Where(p => whole || Array.IndexOf(TransformProperties, p.Key) >= 0).OrderBy(p => p.Key, StringComparer.Ordinal))
                text.Append(property.Key).Append('=').Append(scene.Resolve(TscnFile.Squeeze(property.Value))).Append('\n');
        }
        foreach (var editable in sections.Where(s => s.Tag == "editable").Select(s => s.Text("path") ?? "").OrderBy(p => p, StringComparer.Ordinal))
            text.Append("editable ").Append(editable).Append('\n');
        return text.ToString();
    }

    private static bool IsWhole(TscnSection node)
    {
        if (!node.Attributes.ContainsKey("parent") || node.Attributes.ContainsKey("instance")) return true;
        var type = node.Text("type");
        if (type is null) return true; // an override of a node inside an instanced scene
        if (node.Properties.Any(p => p.Key.StartsWith(MapMetadataPrefix, StringComparison.Ordinal))) return true;
        return !VisualTypes.Contains(type) && !Containers.Contains(type);
    }

    private static string PathOf(TscnSection node)
    {
        var name = node.Text("name") ?? "";
        var parent = node.Text("parent");
        if (parent is null) return ".";
        return parent == "." ? name : parent + "/" + name;
    }

    private static string? ParentOf(string path)
    {
        if (path == ".") return null;
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "." : path.Substring(0, slash);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashOfFile(string path, byte[] bytes)
    {
        var start = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        if (start.StartsWith(LfsPointerStart, StringComparison.Ordinal))
        {
            var at = start.IndexOf(LfsOid, StringComparison.Ordinal);
            if (at >= 0) return start.Substring(at + LfsOid.Length).Split('\n')[0].Trim();
        }
        if (!path.EndsWith(".tres", StringComparison.Ordinal)) return Sha256(bytes);

        // A text resource: its header carries a uid that a re-save may add, and its line endings depend on the machine.
        var lines = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("[gd_resource", StringComparison.Ordinal));
        return Sha256(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
    }

    /// <summary>Replaces references in values with what they refer to.</summary>
    private sealed class SceneRefs
    {
        private readonly Dictionary<string, TscnSection> _ext = new Dictionary<string, TscnSection>(StringComparer.Ordinal);
        private readonly Dictionary<string, TscnSection> _sub = new Dictionary<string, TscnSection>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Func<string, byte[]?> _read;
        private readonly List<string> _problems;
        private readonly int _depth;

        public SceneRefs(List<TscnSection> sections, Func<string, byte[]?> read, List<string> problems, int depth)
        {
            _read = read;
            _problems = problems;
            _depth = depth;
            foreach (var section in sections)
            {
                if (section.Tag == "ext_resource") _ext[section.Text("id") ?? ""] = section;
                else if (section.Tag == "sub_resource") _sub[section.Text("id") ?? ""] = section;
            }
        }

        public string Resolve(string value)
        {
            if (value.IndexOf("Resource(", StringComparison.Ordinal) < 0) return value;
            value = SubResourceReference.Replace(value, m => "sub{" + Sub(m.Groups[1].Value) + "}");
            return ExtResourceReference.Replace(value, m => "ext{" + Ext(m.Groups[1].Value) + "}");
        }

        private string Sub(string id)
        {
            if (_resolved.TryGetValue("s" + id, out var done)) return done;
            if (!_sub.TryGetValue(id, out var section))
            {
                _problems.Add($"sub_resource '{id}' is referenced but not defined");
                return "missing";
            }
            var text = new StringBuilder(section.Text("type") ?? "");
            foreach (var property in section.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                text.Append(';').Append(property.Key).Append('=').Append(Resolve(TscnFile.Squeeze(property.Value)));
            return _resolved["s" + id] = text.ToString();
        }

        private string Ext(string id)
        {
            if (_resolved.TryGetValue("e" + id, out var done)) return done;
            if (!_ext.TryGetValue(id, out var section))
            {
                _problems.Add($"ext_resource '{id}' is referenced but not defined");
                return "missing";
            }
            var path = section.Text("path") ?? "";
            return _resolved["e" + id] = path + "#" + ContentOf(path, section.Text("type"));
        }

        private string ContentOf(string path, string? type)
        {
            if (type == "Script" || !path.StartsWith(ThemesRoot, StringComparison.Ordinal)) return "";
            var bytes = _read(path);
            if (bytes is null)
            {
                _problems.Add($"{path} does not exist");
                return "missing";
            }
            if (!path.EndsWith(".tscn", StringComparison.Ordinal)) return HashOfFile(path, bytes);
            if (_depth >= MaxDepth)
            {
                _problems.Add($"{path}: scenes are instanced more than {MaxDepth} levels deep");
                return "too-deep";
            }
            return Sha256(Encoding.UTF8.GetBytes(Canonical(Encoding.UTF8.GetString(bytes), _read, _problems, _depth + 1)));
        }
    }
}
