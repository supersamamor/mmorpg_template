using System.Globalization;
using Game.Client.Core.Bundle;
using Game.Content.Maps;
using Game.Shared.World;
using Godot;

namespace Game.Client.MapAuthoring;

/// <summary>
/// The headless export (ARCHITECTURE §5.3): <c>godot --headless --path client res://addons/map_authoring/ExportRunner.tscn -- --all</c>, or <c>client/export-maps.ps1</c>. The editor's buttons run this
/// same process, so a grid never depends on the editor's camera, selection or open scenes. Each map gets its own physics world.
/// Arguments: <c>--map &lt;stem&gt;</c> or <c>--all</c>; <c>--content &lt;dir&gt;</c> writes there instead of <c>content/</c>; <c>--theme &lt;name&gt;</c>; <c>--report &lt;file&gt;</c>; <c>--preview-dir &lt;dir&gt;</c>
/// saves the raw bake for the editor's overlay; <c>--bake-only</c> writes no grid. Exit code 0 means every map was exported with no problem.
/// </summary>
public partial class ExportRunner : Node
{
    private const string BundlePath = "res://bundle/client-content.json";
    private const string ThemesRoot = "res://themes/";

    // Frames for new bodies to reach the physics server's space.
    private const int SettleFrames = 3;

    private readonly List<ExportReportLine> _report = new();

    /// <inheritdoc />
    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        var args = OS.GetCmdlineUserArgs();
        var failed = false;
        try
        {
            var theme = Value(args, "--theme") ?? "default";
            var content = Value(args, "--content") ?? ProjectSettings.GlobalizePath("res://").TrimEnd('/') + "/../content";
            var stems = Stems(args, theme);
            var ids = LoadIds();
            if (stems.Count == 0) Fail(string.Empty, string.Empty, "nothing to export: pass --map <name> or --all");
            foreach (var stem in stems)
                if (!await ExportAsync(stem, theme, content, ids, Value(args, "--preview-dir"), Array.IndexOf(args, "--bake-only") >= 0)) failed = true;
        }
        catch (Exception e)
        {
            Fail(string.Empty, string.Empty, e.ToString());
        }
        failed |= _report.Exists(l => !l.Ok);

        if (Value(args, "--report") is { } report)
            File.WriteAllText(report, string.Concat(_report.Select(l => l.Format() + "\n")));
        GD.Print($"[map-export] {(failed ? "FAILED" : "done")}: {_report.Count(l => l.Ok)} exported, {_report.Count(l => !l.Ok)} problem(s)");
        GetTree().Quit(failed ? 1 : 0);
    }

    private async Task<bool> ExportAsync(string stem, string theme, string content, IContentIds ids, string? previewDir, bool bakeOnly)
    {
        var path = ThemesRoot + theme + "/maps/" + stem + MapSceneHash.AuthoringSuffix;
        if (!Godot.FileAccess.FileExists(path))
        {
            Fail(stem, string.Empty, $"{path} does not exist");
            return false;
        }

        var hash = MapSceneHash.Compute(Godot.FileAccess.GetFileAsString(path), ReadResource);
        foreach (var problem in hash.Problems) Fail(stem, string.Empty, problem);

        var scene = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Ignore);
        if (scene is null)
        {
            Fail(stem, string.Empty, $"{path} cannot be loaded as a scene");
            return false;
        }

        // Its own world: only this scene's shapes are in the rays' way.
        var viewport = new SubViewport { OwnWorld3D = true, World3D = new World3D(), Disable3D = true };
        AddChild(viewport);
        var root = scene.Instantiate<Node>();
        viewport.AddChild(root);
        for (var i = 0; i < SettleFrames; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

        var problems = new List<AuthoringProblem>();
        var settings = MapExporter.ReadSettings(ReadMeta(root), problems);
        if (settings is not null)
        {
            var space = viewport.FindWorld3D().DirectSpaceState;
            var baked = MapBaker.Bake(settings.Width, settings.Height, settings.SlopeLimitDegrees, (x, z) => Ray(space, x, z));
            if (previewDir is not null) SavePreview(previewDir, stem, baked);
            var result = MapExporter.Build(settings, baked, FindMarkers(root), hash.Hash, ids);
            problems.AddRange(result.Problems);
            if (result.File is not null && !bakeOnly && hash.Problems.Count == 0)
            {
                Directory.CreateDirectory(content + "/maps");
                File.WriteAllBytes(content + "/maps/" + stem + ".grid.json", GridJsonWriter.WriteBytes(result.File));
                Report(new ExportReportLine(true, stem, string.Empty, $"exported {settings.Width}x{settings.Height}, {result.File.Spawns.Count} spawns, {result.File.Warps.Count} warps, {result.File.Npcs.Count} npcs, {result.File.Zones.Count} zones, hash {hash.Hash[..12]}"));
            }
        }
        foreach (var problem in problems) Fail(stem, problem.Path, problem.Message);

        viewport.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        return problems.Count == 0;
    }

    private static CellRayHit Ray(PhysicsDirectSpaceState3D space, double x, double z)
    {
        var query = PhysicsRayQueryParameters3D.Create(new Vector3((float)x, (float)MapBaker.RayTop, (float)z), new Vector3((float)x, (float)MapBaker.RayBottom, (float)z));
        query.CollideWithAreas = false;
        var hit = space.IntersectRay(query);
        if (hit.Count == 0) return CellRayHit.Miss;
        var normal = hit["normal"].AsVector3();
        var body = hit["collider"].AsGodotObject();
        var walkable = !body.HasMeta(MapMeta.SurfaceWalkable) || body.GetMeta(MapMeta.SurfaceWalkable).AsBool();
        var blocksLos = body.HasMeta(MapMeta.SurfaceBlocksLos) && body.GetMeta(MapMeta.SurfaceBlocksLos).AsBool();
        return new CellRayHit(true, normal.Y, walkable, blocksLos);
    }

    private static List<MarkerNode> FindMarkers(Node root)
    {
        var found = new List<MarkerNode>();
        Walk(root);
        return found;

        void Walk(Node node)
        {
            if (node is Node3D spatial && node.HasMeta(MapMeta.Marker))
            {
                var at = spatial.GlobalPosition;
                found.Add(new MarkerNode(root.GetPathTo(node).ToString(), Mathf.FloorToInt(at.X), Mathf.FloorToInt(at.Z), ReadMeta(node)));
            }
            foreach (var child in node.GetChildren()) Walk(child);
        }
    }

    private static Dictionary<string, object?> ReadMeta(Node node)
    {
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in node.GetMetaList())
        {
            var value = node.GetMeta(name);
            meta[name.ToString()] = value.VariantType switch
            {
                Variant.Type.String or Variant.Type.StringName => value.AsString(),
                Variant.Type.Int => value.AsInt64(),
                Variant.Type.Float => value.AsDouble(),
                Variant.Type.Bool => value.AsBool(),
                Variant.Type.PackedStringArray => value.AsStringArray(),
                _ => null,
            };
        }
        return meta;
    }

    private static void SavePreview(string directory, string stem, byte[] baked)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(directory + "/" + stem + ".bake", baked);
    }

    private static byte[]? ReadResource(string path) => Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsBytes(path) : null;

    private static List<string> Stems(string[] args, string theme)
    {
        var stems = new List<string>();
        if (Value(args, "--map") is { } one) stems.Add(one);
        if (Array.IndexOf(args, "--all") < 0) return stems;
        foreach (var file in DirAccess.GetFilesAt(ThemesRoot + theme + "/maps"))
            if (AuthoringTemplate.StemOf(file) is { } stem) stems.Add(stem);
        stems.Sort(StringComparer.Ordinal);
        return stems;
    }

    private static IContentIds LoadIds()
    {
        if (!Godot.FileAccess.FileExists(BundlePath) || DisplayBundle.TryRead(Godot.FileAccess.GetFileAsBytes(BundlePath), out var bundle, out _) != BundleStatus.Ok)
            throw new InvalidOperationException($"{BundlePath} cannot be read. Build the client first (dotnet build client/GameClient.csproj): the export checks marker ids against it.");
        return new BundleIds(bundle!);
    }

    private static string? Value(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private void Fail(string map, string path, string message) => Report(new ExportReportLine(false, map, path, message));

    private void Report(ExportReportLine line)
    {
        _report.Add(line);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[map-export] {(line.Ok ? "ok" : "ERROR")} {line.Map}{(line.Path.Length > 0 ? " " + line.Path : string.Empty)}: {line.Message}"));
    }

    private sealed class BundleIds : IContentIds
    {
        private readonly DisplayBundle _bundle;

        public BundleIds(DisplayBundle bundle) => _bundle = bundle;

        public bool Exists(string id) => _bundle.IsKnownId(id);

        public bool IsWalkable(string mapId, int x, int y) => _bundle.TryGetMap(mapId, out var map) && map!.IsWalkable(x, y);
    }
}
