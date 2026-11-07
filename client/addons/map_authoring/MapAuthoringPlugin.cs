using System.Text.Json;
using Game.Content.Maps;
using Game.Shared.World;
using Godot;

namespace Game.Client.MapAuthoring;

/// <summary>
/// The Map dock (docs/MAP_AUTHORING.md): turn a visual scene into a map, bake, paint overrides on a grid overlay, export. It holds no bake logic. Bake and Export save the scene and run the headless
/// <see cref="ExportRunner"/> as a separate process, so the editor's camera, selection and other open scenes cannot reach a grid. Paint changes only the root's <c>map_overrides</c>, through undo.
/// </summary>
[Tool]
public partial class MapAuthoringPlugin : EditorPlugin
{
    private const string ConfigPath = "res://addons/map_authoring/config.json";
    private const string WorkDir = "res://.godot/map_authoring";
    private const float OverlayLift = 0.06f;

    private static readonly Color WalkableColor = new(0.2f, 0.9f, 0.3f, 0.22f);
    private static readonly Color BlockedColor = new(0.95f, 0.2f, 0.15f, 0.4f);
    private static readonly Color LosColor = new(0.65f, 0.25f, 0.95f, 0.55f);
    private static readonly Color UnbakedColor = new(0.7f, 0.7f, 0.7f, 0.15f);
    private static readonly Color PaintedEdge = new(1f, 1f, 0.2f, 0.9f);

    private EditorDock? _dock;
    private Label? _title;
    private SpinBox? _widthBox;
    private SpinBox? _heightBox;
    private Button? _createButton;
    private CheckBox? _overlayToggle;
    private OptionButton? _brush;
    private ItemList? _problems;
    private MeshInstance3D? _overlay;
    private byte[]? _preview;
    private string? _previewStem;
    private readonly List<string> _problemPaths = new();
    private bool _painting;

    /// <inheritdoc />
    public override void _EnterTree()
    {
        _dock = BuildDock();
        AddDock(_dock);
        SetInputEventForwardingAlwaysEnabled();
        SceneChanged += OnSceneChanged;
        OnSceneChanged(EditorInterface.Singleton.GetEditedSceneRoot());
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        SceneChanged -= OnSceneChanged;
        RemoveOverlay();
        if (_dock is null) return;
        RemoveDock(_dock);
        _dock.QueueFree();
        _dock = null;
    }

    /// <summary>
    /// Paints with the left button when a brush is picked. The cell comes from the camera's ray through the ground plane; only the painted line in the scene changes.
    /// </summary>
    public override int _Forward3DGuiInput(Camera3D viewportCamera, InputEvent @event)
    {
        if (_brush is null || _brush.Selected <= 0 || MapRoot() is not { } root) return (int)AfterGuiInput.Pass;
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _painting = button.Pressed;
                if (button.Pressed) PaintAt(root, viewportCamera, button.Position);
                return (int)AfterGuiInput.Stop;
            case InputEventMouseMotion motion when _painting:
                PaintAt(root, viewportCamera, motion.Position);
                return (int)AfterGuiInput.Stop;
            default:
                return (int)AfterGuiInput.Pass;
        }
    }

    /// <summary>Paints one cell with the picked brush (also what a test drive of the plugin calls).</summary>
    public void PaintCell(Node root, int x, int y)
    {
        var (walk, los) = BrushPaint(_brush?.Selected ?? 0);
        var errors = new List<string>();
        var overrides = CellOverrides.Parse(ReadOverrideLines(root), errors);
        var (oldWalk, oldLos) = overrides.Get(x, y);
        var newWalk = (_brush?.Selected ?? 0) == BrushClear ? WalkPaint.None : walk == WalkPaint.None ? oldWalk : walk;
        var newLos = (_brush?.Selected ?? 0) == BrushClear ? LosPaint.None : los == LosPaint.None ? oldLos : los;
        if (newWalk == oldWalk && newLos == oldLos) return;

        var before = overrides.ToLines();
        overrides.Set(x, y, newWalk, newLos);
        var undo = GetUndoRedo();
        undo.CreateAction($"Paint map cell ({x}, {y})", UndoRedo.MergeMode.Disable, root);
        undo.AddDoMethod(this, MethodName.SetOverrideLines, root, overrides.ToLines());
        undo.AddUndoMethod(this, MethodName.SetOverrideLines, root, before);
        undo.CommitAction();
    }

    /// <summary>Stores the painted lines on the root (undo calls this too).</summary>
    public void SetOverrideLines(Node root, string[] lines)
    {
        if (lines.Length == 0) root.RemoveMeta(MapMeta.Overrides);
        else root.SetMeta(MapMeta.Overrides, lines);
        RebuildOverlay();
    }

    /// <summary>Runs the bake for the open map and refreshes the overlay.</summary>
    public void BakeOpenMap() => RunOnOpenMap(bakeOnly: true);

    /// <summary>Runs the export for the open map.</summary>
    public void ExportOpenMap() => RunOnOpenMap(bakeOnly: false);

    private const int BrushClear = 5;

    private static (WalkPaint Walk, LosPaint Los) BrushPaint(int brush) => brush switch
    {
        1 => (WalkPaint.Walkable, LosPaint.None),
        2 => (WalkPaint.Blocked, LosPaint.None),
        3 => (WalkPaint.None, LosPaint.Blocks),
        4 => (WalkPaint.None, LosPaint.Open),
        _ => (WalkPaint.None, LosPaint.None),
    };

    private EditorDock BuildDock()
    {
        var dock = new EditorDock { Title = "Map", DefaultSlot = EditorDock.DockSlot.RightUl };
        var box = new VBoxContainer();
        dock.AddChild(box);

        _title = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_title);

        var size = new HBoxContainer();
        size.AddChild(new Label { Text = "Size" });
        _widthBox = new SpinBox { MinValue = 1, MaxValue = MapGridFile.MaxSize, Value = 32 };
        _heightBox = new SpinBox { MinValue = 1, MaxValue = MapGridFile.MaxSize, Value = 32 };
        size.AddChild(_widthBox);
        size.AddChild(_heightBox);
        box.AddChild(size);
        _createButton = new Button { Text = "Create authoring scene from this scene" };
        _createButton.Pressed += CreateAuthoringScene;
        box.AddChild(_createButton);

        _overlayToggle = new CheckBox { Text = "Show grid overlay", ButtonPressed = true };
        _overlayToggle.Toggled += _ => RebuildOverlay();
        box.AddChild(_overlayToggle);

        box.AddChild(new Label { Text = "Brush (left mouse in the 3D view)" });
        _brush = new OptionButton();
        foreach (var name in new[] { "Off", "Force walkable", "Force blocked", "Blocks line of sight", "Line of sight open", "Clear paint" }) _brush.AddItem(name);
        box.AddChild(_brush);

        var buttons = new HBoxContainer();
        var bake = new Button { Text = "Bake" };
        bake.Pressed += BakeOpenMap;
        buttons.AddChild(bake);
        var export = new Button { Text = "Export" };
        export.Pressed += ExportOpenMap;
        buttons.AddChild(export);
        var all = new Button { Text = "Export all" };
        all.Pressed += () => RunExport(new[] { "--all" });
        buttons.AddChild(all);
        box.AddChild(buttons);

        _problems = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 160), AutoHeight = false };
        _problems.ItemSelected += OnProblemSelected;
        box.AddChild(_problems);
        return dock;
    }

    private void OnSceneChanged(Node? root)
    {
        _preview = null;
        _previewStem = null;
        _painting = false;
        RemoveOverlay();
        if (_title is null) return;
        var path = root?.SceneFilePath ?? string.Empty;
        var isMap = MapRoot() is not null;
        _title.Text = isMap ? "Map: " + path.GetFile() : root is null ? "No scene open" : path.Length == 0 ? "Save this scene first" : "Not an authoring scene: " + path.GetFile();
        _createButton!.Disabled = isMap || path.Length == 0 || path.EndsWith(MapSceneHash.AuthoringSuffix, StringComparison.Ordinal);
        if (isMap)
        {
            LoadPreview();
            RebuildOverlay();
        }
    }

    private Node? MapRoot()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        return root is not null && root.HasMeta(MapMeta.MapId) ? root : null;
    }

    private void CreateAuthoringScene()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root is null || root.SceneFilePath.Length == 0) return;
        var visual = root.SceneFilePath;
        var authoring = MapSceneHash.AuthoringPathOf(visual);
        if (Godot.FileAccess.FileExists(authoring))
        {
            EditorInterface.Singleton.OpenSceneFromPath(authoring);
            return;
        }
        var text = AuthoringTemplate.Scene("map." + visual.GetFile().GetBaseName(), (int)_widthBox!.Value, (int)_heightBox!.Value, ConfiguredSlopeLimit(), visual);
        using (var file = Godot.FileAccess.Open(authoring, Godot.FileAccess.ModeFlags.Write))
        {
            if (file is null)
            {
                GD.PushError($"[map] cannot write {authoring}: {Godot.FileAccess.GetOpenError()}");
                return;
            }
            file.StoreString(text);
        }
        EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(authoring);
        EditorInterface.Singleton.OpenSceneFromPath(authoring);
    }

    private static double ConfiguredSlopeLimit()
    {
        using var config = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(ConfigPath));
        return config.RootElement.GetProperty("default_slope_limit_degrees").GetDouble();
    }

    private static string RunnerScene()
    {
        using var config = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(ConfigPath));
        return config.RootElement.GetProperty("export_runner_scene").GetString()!;
    }

    private void RunOnOpenMap(bool bakeOnly)
    {
        if (MapRoot() is not { } root || AuthoringTemplate.StemOf(root.SceneFilePath) is not { } stem)
        {
            ShowProblems(new[] { new ExportReportLine(false, string.Empty, string.Empty, "Open a map's .authoring.tscn first.") });
            return;
        }
        EditorInterface.Singleton.SaveScene();
        RunExport(bakeOnly ? new[] { "--map", stem, "--bake-only" } : new[] { "--map", stem });
        LoadPreview();
        RebuildOverlay();
    }

    private void RunExport(string[] selection)
    {
        var work = ProjectSettings.GlobalizePath(WorkDir);
        Directory.CreateDirectory(work);
        var report = work + "/report.tsv";
        File.Delete(report);
        var arguments = new List<string> { "--headless", "--path", ProjectSettings.GlobalizePath("res://"), RunnerScene(), "--" };
        arguments.AddRange(selection);
        arguments.AddRange(new[] { "--report", report, "--preview-dir", work });
        var output = new Godot.Collections.Array();
        var exit = OS.Execute(OS.GetExecutablePath(), arguments.ToArray(), output, true);

        var lines = File.Exists(report)
            ? ExportReportLine.ParseAll(File.ReadAllText(report))
            : new List<ExportReportLine> { new(false, string.Empty, string.Empty, $"The export process wrote no report (exit code {exit}). Build the C# project first.") };
        ShowProblems(lines);
    }

    private void ShowProblems(IReadOnlyList<ExportReportLine> lines)
    {
        _problems!.Clear();
        _problemPaths.Clear();
        foreach (var line in lines)
        {
            var at = line.Path.Length > 0 ? line.Path + ": " : string.Empty;
            var index = _problems.AddItem($"{(line.Ok ? "OK" : "ERROR")} {line.Map} {at}{line.Message}");
            _problems.SetItemCustomFgColor(index, line.Ok ? Colors.LightGreen : Colors.Salmon);
            _problemPaths.Add(line.Path);
            if (!line.Ok) GD.PushError($"[map] {line.Map} {at}{line.Message}");
        }
    }

    private void OnProblemSelected(long index)
    {
        var path = _problemPaths[(int)index];
        if (path.Length == 0 || EditorInterface.Singleton.GetEditedSceneRoot()?.GetNodeOrNull(path) is not { } node) return;
        var selection = EditorInterface.Singleton.GetSelection();
        selection.Clear();
        selection.AddNode(node);
    }

    private void LoadPreview()
    {
        _preview = null;
        _previewStem = null;
        if (MapRoot() is not { } root || AuthoringTemplate.StemOf(root.SceneFilePath) is not { } stem) return;
        var file = ProjectSettings.GlobalizePath(WorkDir) + "/" + stem + ".bake";
        if (!File.Exists(file)) return;
        _preview = File.ReadAllBytes(file);
        _previewStem = stem;
    }

    private void PaintAt(Node root, Camera3D camera, Vector2 at)
    {
        var origin = camera.ProjectRayOrigin(at);
        var direction = camera.ProjectRayNormal(at);
        if (direction.Y >= -1e-4f) return;
        var ground = origin + direction * (-origin.Y / direction.Y);
        var x = Mathf.FloorToInt(ground.X);
        var y = Mathf.FloorToInt(ground.Z);
        if (x < 0 || y < 0 || x >= root.GetMeta(MapMeta.Width).AsInt32() || y >= root.GetMeta(MapMeta.Height).AsInt32()) return;
        PaintCell(root, x, y);
    }

    private static string[] ReadOverrideLines(Node root) => root.HasMeta(MapMeta.Overrides) ? root.GetMeta(MapMeta.Overrides).AsStringArray() : Array.Empty<string>();

    private void RemoveOverlay()
    {
        _overlay?.QueueFree();
        _overlay = null;
    }

    // Quads for every cell under the scene root. No owner and an internal child, so the overlay is never saved into the scene.
    private void RebuildOverlay()
    {
        RemoveOverlay();
        if (MapRoot() is not { } root || _overlayToggle is not { ButtonPressed: true }) return;
        var width = root.GetMeta(MapMeta.Width).AsInt32();
        var height = root.GetMeta(MapMeta.Height).AsInt32();
        var errors = new List<string>();
        var overrides = CellOverrides.Parse(ReadOverrideLines(root), errors);
        var baked = _preview is { } preview && preview.Length == width * height && _previewStem == AuthoringTemplate.StemOf(root.SceneFilePath) ? preview : null;
        var flags = baked is null ? null : overrides.Apply(baked, width, height, errors);

        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = UnbakedColor;
                if (flags is not null)
                {
                    var cell = flags[y * width + x];
                    color = (cell & CellFlagBits.BlocksLos) != 0 ? LosColor : (cell & CellFlagBits.Walkable) != 0 ? WalkableColor : BlockedColor;
                }
                Quad(surface, x + 0.04f, y + 0.04f, x + 0.96f, y + 0.96f, color);
                if (overrides.Get(x, y) != (WalkPaint.None, LosPaint.None)) Quad(surface, x + 0.38f, y + 0.38f, x + 0.62f, y + 0.62f, PaintedEdge);
            }
        }
        _overlay = new MeshInstance3D
        {
            Name = "MapGridOverlay",
            Mesh = surface.Commit(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        root.AddChild(_overlay, false, Node.InternalMode.Back);
    }

    private static void Quad(SurfaceTool surface, float x0, float z0, float x1, float z1, Color color)
    {
        surface.SetColor(color);
        surface.AddVertex(new Vector3(x0, OverlayLift, z0));
        surface.AddVertex(new Vector3(x1, OverlayLift, z0));
        surface.AddVertex(new Vector3(x1, OverlayLift, z1));
        surface.AddVertex(new Vector3(x0, OverlayLift, z0));
        surface.AddVertex(new Vector3(x1, OverlayLift, z1));
        surface.AddVertex(new Vector3(x0, OverlayLift, z1));
    }
}
