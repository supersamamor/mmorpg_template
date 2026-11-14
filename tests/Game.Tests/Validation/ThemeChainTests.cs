using System.Text.Json.Nodes;
using Game.Content.Loading;
using Game.Content.Validation;

namespace Game.Tests.Validation;

/// <summary>
/// The asset-key, <c>fallbacks</c> and audio-table checks resolve keys the way the client does (T-219, ARCHITECTURE §7.7): the active theme, then <c>default</c>.
/// Each test builds a temp repo layout (<c>content/</c> beside <c>themes/</c>) with a partial theme that lacks one key the content uses.
/// </summary>
public class ThemeChainTests : IDisposable
{
    private const string Key = "model.monster.gel_slime"; // used by content and by the default fallbacks table
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "themechain_" + Guid.NewGuid().ToString("N"));

    public ThemeChainTests()
    {
        Copy(Path.Combine(Root(), "content"), Path.Combine(_temp, "content"));
        Directory.CreateDirectory(Path.Combine(_temp, "themes", "default"));
        File.Copy(ManifestPath(Path.Combine(Root(), "themes"), "default"), ManifestPath(Path.Combine(_temp, "themes"), "default"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static string ManifestPath(string themes, string theme) => AssetManifest.PathFor(themes, theme);

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Writes theme "partial": the default manifest without <see cref="Key"/>, and with <paramref name="fallbackMonster"/> as its monster fallback (null keeps the default's).</summary>
    private void WritePartialTheme(string? fallbackMonster = null)
    {
        var node = JsonNode.Parse(File.ReadAllText(ManifestPath(Path.Combine(_temp, "themes"), "default")))!.AsObject();
        node.Remove(Key).ShouldBeTrue("test setup: the key must be in the default manifest");
        if (fallbackMonster is not null) node["fallbacks"]!.AsObject()["monster"] = fallbackMonster;
        Directory.CreateDirectory(Path.Combine(_temp, "themes", "partial"));
        File.WriteAllText(ManifestPath(Path.Combine(_temp, "themes"), "partial"), node.ToJsonString());
    }

    private IReadOnlyList<ValidationIssue> Errors(string theme) =>
        ContentBoot.Check(Path.Combine(_temp, "content"), theme).Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();

    [Fact]
    public void A_partial_theme_may_use_and_fall_back_to_a_key_only_default_has()
    {
        WritePartialTheme(); // content uses the key, and fallbacks.monster names it, but only default declares it

        Errors("partial").ShouldBeEmpty();
    }

    [Fact]
    public void The_theme_alone_would_not_have_passed()
    {
        WritePartialTheme();
        var keys = AssetManifest.LoadKeys(ManifestPath(Path.Combine(_temp, "themes"), "partial"));

        keys.ShouldNotContain(Key);
        AssetManifest.LoadChainKeys(Path.Combine(_temp, "themes"), "partial").ShouldContain(Key);
    }

    [Fact]
    public void A_fallback_in_neither_theme_fails_with_file_and_path()
    {
        WritePartialTheme("model.monster.nowhere");

        var issue = Errors("partial").Single(i => i.Code == ValidationCodes.AssetMissing && i.Path == "fallbacks.monster");

        issue.File.ShouldBe("themes/partial/asset-manifest.json");
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_content_key_in_neither_theme_fails_with_file_and_path()
    {
        WritePartialTheme();
        var path = ManifestPath(Path.Combine(_temp, "themes"), "default");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove(Key);
        File.WriteAllText(path, node.ToJsonString()); // now neither theme has it
        node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["fallbacks"]!.AsObject().Remove("monster");
        File.WriteAllText(path, node.ToJsonString());
        node = JsonNode.Parse(File.ReadAllText(ManifestPath(Path.Combine(_temp, "themes"), "partial")))!.AsObject();
        node["fallbacks"]!.AsObject().Remove("monster");
        File.WriteAllText(ManifestPath(Path.Combine(_temp, "themes"), "partial"), node.ToJsonString());

        var issue = Errors("partial").First(i => i.Code == ValidationCodes.AssetMissing && i.Message.Contains(Key));

        issue.File.ShouldEndWith(".yaml");
        issue.Path.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// Writes theme "sounds": a truly partial manifest with no entries of its own except one new sound, and audio tables that point at
    /// <paramref name="dieKey"/> (category <c>monster.die</c>) and <paramref name="levelUpKey"/> (event <c>LevelUp</c>). Everything else comes from <c>default</c>.
    /// </summary>
    private void WriteSoundOnlyTheme(string dieKey, string levelUpKey)
    {
        var node = new JsonObject
        {
            ["sfx.monster.die.custom"] = new JsonObject { ["file"] = "audio/sfx/custom_die.ogg" },
            ["audio"] = new JsonObject
            {
                ["defaults"] = new JsonObject { ["monster.die"] = dieKey, ["monster.hit"] = "sfx.monster.die.custom" },
                ["events"] = new JsonObject { ["LevelUp"] = levelUpKey },
            },
        };
        Directory.CreateDirectory(Path.Combine(_temp, "themes", "sounds"));
        File.WriteAllText(ManifestPath(Path.Combine(_temp, "themes"), "sounds"), node.ToJsonString());
    }

    [Fact]
    public void A_partial_theme_s_audio_tables_may_name_sounds_only_default_has()
    {
        // T-220, ADR-002 row 149: the audio rule resolves table targets through the same chain as LoadChainKeys.
        WriteSoundOnlyTheme("sfx.monster.die.slime", "sfx.event.level_up");
        AssetManifest.LoadKeys(ManifestPath(Path.Combine(_temp, "themes"), "sounds")).ShouldNotContain("sfx.monster.die.slime");

        Errors("sounds").ShouldBeEmpty(); // content sound keys (gel slime's sounds.hit/die) also resolve through default
    }

    [Fact]
    public void A_partial_theme_s_audio_default_in_neither_theme_fails_with_file_and_path()
    {
        WriteSoundOnlyTheme("sfx.monster.die.nowhere", "sfx.event.level_up");

        var issue = Errors("sounds").Single();

        issue.Code.ShouldBe(ValidationCodes.AssetMissing);
        issue.Path.ShouldBe("audio.defaults.monster.die");
        issue.File.ShouldBe("themes/sounds/asset-manifest.json");
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_partial_theme_s_audio_event_in_neither_theme_fails()
    {
        WriteSoundOnlyTheme("sfx.monster.die.slime", "sfx.event.nowhere");

        Errors("sounds").Single().Path.ShouldBe("audio.events.LevelUp");
    }

    [Fact]
    public void A_content_sound_key_in_neither_theme_fails()
    {
        WriteSoundOnlyTheme("sfx.monster.die.slime", "sfx.event.level_up");
        var path = ManifestPath(Path.Combine(_temp, "themes"), "default");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("sfx.monster.hit.slime").ShouldBeTrue("test setup: gel slime's hit sound must be in the default manifest");
        File.WriteAllText(path, node.ToJsonString());

        Errors("sounds").ShouldContain(i => i.Code == ValidationCodes.AssetMissing && i.Message.Contains("sfx.monster.hit.slime") && i.File.EndsWith(".yaml"));
    }

    [Fact]
    public void The_default_theme_alone_behaves_as_before()
    {
        Errors("default").ShouldBeEmpty();
    }
}
