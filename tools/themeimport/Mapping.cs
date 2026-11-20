using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Game.ThemeImport;

/// <summary>
/// A theme import mapping (docs/THEMES.md "Import mapping"): which pack file serves each asset key, and the fixes that make it meet the theme contract. YAML, read
/// strictly: an unknown field is an error, so a typo never passes silently.
/// </summary>
public sealed class Mapping
{
    /// <summary>The theme folder to write under the themes directory (<c>eval-*</c> is git-ignored: use it for trials).</summary>
    public string Theme { get; set; } = "";

    /// <summary>The pack folder, relative to the mapping file (the <c>--pack</c> option overrides it).</summary>
    public string? Pack { get; set; }

    /// <summary>Per rig name: fixes that apply to every file on that rig (models, garments and the animation library alike).</summary>
    public Dictionary<string, RigMapping> Rigs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Asset key to what serves it.</summary>
    public Dictionary<string, AssetMapping> Assets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The manifest's <c>fallbacks</c> table, copied as it is (entity kind or <c>missing</c> to an asset key).</summary>
    public Dictionary<string, string>? Fallbacks { get; set; }

    /// <summary>Reads a mapping file. Throws <see cref="MappingException"/> with the line of the problem.</summary>
    public static Mapping Load(string path)
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
        try
        {
            var mapping = deserializer.Deserialize<Mapping>(File.ReadAllText(path)) ?? throw new MappingException($"{path}: the mapping is empty");
            if (string.IsNullOrWhiteSpace(mapping.Theme)) throw new MappingException($"{path}: 'theme' (the theme folder to write) is required");
            return mapping;
        }
        catch (YamlException e)
        {
            throw new MappingException($"{path}:{e.Start.Line}:{e.Start.Column}: {(e.InnerException ?? e).Message}");
        }
    }
}

/// <summary>Fixes for every file on one rig.</summary>
public sealed class RigMapping
{
    /// <summary>The pack's bone name to the contract's (e.g. <c>mixamorig:RightHand: hand_r</c>). Applied to joints and to the nodes animations move.</summary>
    public Dictionary<string, string> Bones { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What serves one asset key.</summary>
public sealed class AssetMapping
{
    /// <summary>The file in the pack folder (a <c>.glb</c> or <c>.gltf</c> for models and animations; any file for icons, sounds and the rest).</summary>
    public string? Source { get; set; }

    /// <summary>A uniform scale that brings the model to 1 cell = 1 unit (e.g. 0.01 for centimetres).</summary>
    public double Scale { get; set; } = 1;

    /// <summary>The way the source model faces: <c>-z</c> (already right), <c>+z</c>, <c>+x</c> or <c>-x</c>. The tool turns it to face -Z.</summary>
    public string Facing { get; set; } = "-z";

    /// <summary>The manifest's <c>rig</c>: the rig this model or library is on.</summary>
    public string? Rig { get; set; }

    /// <summary>The manifest's <c>anims</c>: the animation library a living model plays.</summary>
    public string? Anims { get; set; }

    /// <summary>The manifest's <c>attach</c>: the attachment bone of a rigid worn piece.</summary>
    public string? Attach { get; set; }

    /// <summary>The manifest's <c>skinned</c>: the rig a garment is skinned to.</summary>
    public string? Skinned { get; set; }

    /// <summary>Animation renames for a library: animation id to the clip's name in the source (e.g. <c>idle: Idle_Loop</c>). Written as the manifest's <c>clips</c>.</summary>
    public Dictionary<string, string>? Clips { get; set; }

    /// <summary>The manifest's <c>impact_delay</c>.</summary>
    public double? ImpactDelay { get; set; }

    /// <summary>Which mesh nodes are the levels of detail, when they are not already named <c>Body_LOD0</c> and <c>Body_LOD1</c>.</summary>
    public MeshMapping? Meshes { get; set; }

    /// <summary>Write outline directions (the smoothed normals the outline pushes along, glTF <c>TANGENT</c>). On by default for models.</summary>
    public bool OutlineDirections { get; set; } = true;

    /// <summary>Instead of a source: this key draws another key's model (a manifest variant).</summary>
    public VariantMapping? Variant { get; set; }

    /// <summary>More manifest fields, copied as they are (e.g. <c>effect</c>, <c>bus</c>, <c>loop</c>, <c>volume_db</c>).</summary>
    public Dictionary<string, object>? Extra { get; set; }
}

/// <summary>Mesh node names in the source.</summary>
public sealed class MeshMapping
{
    /// <summary>The node that becomes <c>Body_LOD0</c>.</summary>
    public string? Lod0 { get; set; }

    /// <summary>The node that becomes <c>Body_LOD1</c>.</summary>
    public string? Lod1 { get; set; }
}

/// <summary>A manifest variant (docs/THEMES.md "Variants").</summary>
public sealed class VariantMapping
{
    /// <summary>The base key.</summary>
    public string Of { get; set; } = "";

    /// <summary><c>#rrggbb</c> the base's colours are multiplied by.</summary>
    public string? Tint { get; set; }

    /// <summary>A uniform scale on top of the base's.</summary>
    public double? Scale { get; set; }

    /// <summary><c>false</c> for no outline, or an outline colour <c>#rrggbb</c>.</summary>
    public string? Outline { get; set; }
}

/// <summary>A mapping that cannot be used, with a message that says where and why.</summary>
public sealed class MappingException : Exception
{
    /// <summary>Creates one.</summary>
    public MappingException(string message) : base(message) { }
}
