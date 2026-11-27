using System.Diagnostics;
using Game.Client.Core.Outfits;
using Game.Client.Core.World;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The scripted run for textured characters (T-711), against the textured test theme <c>tools/themegen --textured</c> writes (run with <c>--set theme=&lt;it&gt;</c>):
/// <c>--e2e-textured &lt;keys&gt;</c> names the parts that theme gave another image (ids from the script, never from code), <c>--e2e-textured-dress &lt;item ids&gt;</c>
/// the gear to put on. A character whose parts share the atlas is one textured surface with its colours baked in, dressed or in outfits; one with a part of the
/// other image is two; the two images are told apart by content though every file has its own copy; and job models were not loaded until a character needs one.
/// </summary>
public sealed class TexturedRuns
{
    private readonly Main _main;
    private readonly Func<string, Task> _shot;
    private readonly List<ModelVisual> _drawn = new();

    /// <summary>Prepares the run.</summary>
    public TexturedRuns(Main main, Func<string, Task> shot)
    {
        _main = main;
        _shot = shot;
    }

    private WorldView World => _main.Session!.World;

    /// <summary>The run, after login.</summary>
    public async Task RunAsync(IReadOnlyCollection<string> otherImage, IReadOnlyList<string> dress)
    {
        var entities = _main.Entities!;
        var appearance = _main.Bundle!.Appearance!;
        await UntilAsync(() => World.Entities.FirstOrDefault(e => e.Id == World.OwnId)?.Visual is ModelVisual { Settled: true }, 20, "the own character drawn");

        // Job models are only a fallback (ADR-002 row 314): none is loaded while every character has a body.
        var held = entities.JobModelsHeld();
        Check(World.Entities.Where(e => e.Kind == EntityKinds.Player).All(e => e.Visual is ModelVisual { Character: not null }), "every player is drawn by its looks");
        Check(held == 0, $"no job model is loaded while every character has its body, {held} were");
        Print($"textured: job models loaded at start: {held}");

        var stage = new Node3D { Name = "TexturedPreviews", Position = new Vector3(0, -100, 0) }; // out of the camera's sight
        _main.AddChild(stage);
        _drawn.Clear();
        try
        {
            foreach (var gender in appearance.Genders)
            {
                var atlasHair = gender.Hairstyles.First(h => !otherImage.Contains(h.ModelAssetKey));
                var otherHair = gender.Hairstyles.FirstOrDefault(h => otherImage.Contains(h.ModelAssetKey));
                var skin = appearance.SkinTones[^1];
                var shared = new AppearanceRecord(gender.Gender, skin.Id, gender.Faces[^1].Id, atlasHair.Id, appearance.HairColors[^1].Id);
                var atlas = await DrawAsync(stage, shared, null, null);
                Check(atlas.Surfaces == 1, $"{gender.Gender}: body, face and hairstyle on one atlas are one surface, were {atlas.Surfaces}");
                Check(Textured(atlas.DrawnMaterial(0)), $"{gender.Gender}: the merged surface draws the atlas");
                var want = new Color(skin.Color).SrgbToLinear();
                Check(atlas.DrawnColors().Any(c => Math.Abs(c.R - want.R) < 0.006f && Math.Abs(c.G - want.G) < 0.006f && Math.Abs(c.B - want.B) < 0.006f),
                    $"{gender.Gender}: the skin colour {skin.Color} is baked into the textured mesh's vertex colours");
                Print($"textured: {gender.Gender} with {atlasHair.ModelAssetKey}: one textured surface, skin colour kept");

                var outfits = OutfitSlots.All.Ordered
                    .Select(slot => _main.Bundle.Outfits.FirstOrDefault(o => OutfitPieces.Find(o, slot, gender.Gender) is not null) is { } o ? new ShownOutfit(slot, o.Id) : (ShownOutfit?)null)
                    .Where(s => s is not null).Select(s => s!.Value).ToList();
                var dressed = await DrawAsync(stage, shared, dress, outfits);
                Check(dressed.Surfaces == 1, $"{gender.Gender}: dressed ({string.Join(", ", dress)}) and in {outfits.Count} outfit piece(s), still one surface, were {dressed.Surfaces}");
                Print($"textured: {gender.Gender} dressed and in {outfits.Count} outfit piece(s): {dressed.Dressed.Count} parts, one surface");

                if (otherHair is null) continue;
                var split = await DrawAsync(stage, shared with { Hair = otherHair.Id }, null, null);
                Check(split.Surfaces == 2, $"{gender.Gender}: a hairstyle with another image is a second surface, were {split.Surfaces}");
                Print($"textured: {gender.Gender} with {otherHair.ModelAssetKey} (another image): two surfaces");
            }
            var (textures, images, ms) = entities.Images;
            Check(images == 2, $"the textures are two images (the atlas and the other), were {images} from {textures} textures");
            Print($"textured: {textures} textures read, {images} distinct images, {ms:0.0} ms of main thread to tell them apart");
            await _shot(string.Empty);
        }
        finally
        {
            foreach (var visual in _drawn) entities.Discard(visual);
            stage.QueueFree();
        }

        // A character drawn by its job's model (here: every one, with looks switched off) loads it then, hidden until it is in; no frame waits for the disk.
        var presenter = _main.Presenter!;
        var clock = Stopwatch.StartNew();
        var worst = 0.0;
        var last = 0.0;
        _main.Config.DrawAppearance = false;
        presenter.Redraw();
        var frames = 0;
        do
        {
            if (clock.Elapsed.TotalSeconds > 20) throw new TimeoutException("timed out after 20 s waiting for the job models");
            await FramesAsync(1); // every frame from the change on is timed: without a renderer the model loads inside one of them (ModelLoading)
            worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds - last);
            last = clock.Elapsed.TotalMilliseconds;
            frames++;
        }
        while (frames < 3 || World.Entities.Where(e => e.Kind == EntityKinds.Player).Any(e => e.Visual is ModelVisual { Settled: false } or ModelVisual { Character: not null }));
        Check(entities.JobModelsHeld() > 0, "the job model was loaded once a character needed it");
        Print($"textured: job model loaded on demand in {clock.Elapsed.TotalSeconds:0.00} s, worst frame {worst:0.0} ms, {entities.JobModelsHeld()} job model(s) now held");
        _main.Config.DrawAppearance = true;
        presenter.Redraw();
        await FramesAsync(2);
    }

    private static bool Textured(Material? material) => material switch
    {
        StandardMaterial3D standard => standard.AlbedoTexture is not null,
        ShaderMaterial shader => shader.GetShaderParameter("albedo_texture").VariantType == Variant.Type.Object && shader.GetShaderParameter("albedo_texture").AsGodotObject() is Texture2D,
        _ => false,
    };

    /// <summary>A preview of these looks, dressed as asked, once it is settled.</summary>
    private async Task<ModelVisual> DrawAsync(Node3D stage, AppearanceRecord looks, IReadOnlyList<string>? gear, IReadOnlyList<ShownOutfit>? outfits)
    {
        var visual = _main.Entities!.Preview(stage, null, looks);
        _drawn.Add(visual);
        if (gear is not null) visual.SetGear(null, gear, 1);
        if (outfits is not null) visual.SetOutfit(outfits);
        await UntilAsync(() => visual.Settled, 20, "the preview of " + looks);
        await FramesAsync(1);
        return visual;
    }

    private async Task FramesAsync(int count)
    {
        for (var i = 0; i < count; i++) await _main.ToSignal(_main.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task UntilAsync(Func<bool> condition, double seconds, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"timed out after {seconds} s waiting for {what}");
            await FramesAsync(1);
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + what);
    }

    private static void Print(string line) => GD.Print("[e2e] " + line);
}
