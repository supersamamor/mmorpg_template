using System.Text.Json;
using Game.Content.ClientExport;
using Game.Content.Loading;
using Game.Content.Schema;
using Game.Content.Validation;
using Game.Content.Validation.Rules;
using Game.Shared.Audio;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;

namespace Game.Tests.Audio;

/// <summary>Audio data (T-304): the manifest format, its validation, the content fields, the resolver and the client bundle.</summary>
public class AudioTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "audio_" + Guid.NewGuid().ToString("N"));

    public AudioTests() => CopyDir(Path.Combine(RepoRoot(), "content"), Path.Combine(_temp, "content"));

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private string ContentDir => Path.Combine(_temp, "content");

    private static string RealManifestText() => File.ReadAllText(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default"));

    /// <summary>Replaces <paramref name="from"/> once in the real manifest text; fails if it is absent.</summary>
    private static string EditManifest(string from, string to)
    {
        var text = RealManifestText();
        text.ShouldContain(from, customMessage: $"test setup: '{from}' not found in the manifest");
        var at = text.IndexOf(from, StringComparison.Ordinal);
        return text[..at] + to + text[(at + from.Length)..];
    }

    private void Edit(string relativeFile, string from, string to)
    {
        var path = Path.Combine(ContentDir, relativeFile);
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        text.ShouldContain(from, customMessage: $"test setup: '{from}' not found in {relativeFile}");
        var at = text.IndexOf(from, StringComparison.Ordinal);
        File.WriteAllText(path, text[..at] + to + text[(at + from.Length)..]);
    }

    private ValidationReport Validate(string? manifestText = null, IEnumerable<IValidationRule>? rules = null)
    {
        var manifestPath = Path.Combine(_temp, "asset-manifest.json");
        File.WriteAllText(manifestPath, manifestText ?? RealManifestText());
        var load = ContentLoader.Load(ContentDir);
        load.Errors.ShouldBeEmpty("test setup: the edit must still load: " + string.Join("\n", load.Errors));
        var keys = AssetManifest.LoadKeys(manifestPath);
        var audio = AudioManifest.Load(manifestPath, "themes/default/asset-manifest.json");
        return ContentValidator.Validate(load.Database!, ContentDir, keys, rules ?? ContentValidator.DefaultRules, audio);
    }

    private ValidationReport ValidateAudio(string? manifestText = null) => Validate(manifestText, [new AudioRule()]);

    private static ValidationIssue Single(ValidationReport report, string code)
    {
        var hits = report.Issues.Where(i => i.Code == code).ToList();
        hits.Count.ShouldBe(1, string.Join("\n", report.Issues));
        return hits[0];
    }

    // ---- vocabulary ----

    [Fact]
    public void Vocabulary_terms_match_the_doc()
    {
        AudioBuses.All.Ordered.ShouldBe(["music", "sfx", "ui", "ambient"]);
        AudioCategories.Fixed.Ordered.ShouldBe(
            ["monster.aggro", "monster.hit", "monster.die", "item.use", "item.equip", "ui.tap", "ui.open", "ui.close", "ui.error"]);
        AudioCategories.All.Count.ShouldBe(AudioCategories.Fixed.Count + WeaponTypes.All.Count * 2);
        AudioCategories.All.Contains("weapon.sword_1h.swing").ShouldBeTrue();
        AudioCategories.All.Contains("weapon.bow.impact").ShouldBeTrue();
        AudioCategories.All.Contains("weapon.wand.swing").ShouldBeFalse();
        AssetKeyPrefixes.Ambience.ShouldBe("ambience.");
    }

    // ---- manifest parsing ----

    [Fact]
    public void Simple_and_rich_entries_both_parse()
    {
        var manifest = AudioManifest.Parse("""
            {
              "sfx.simple": { "file": "a.ogg" },
              "sfx.rich": { "files": ["a.ogg", "b.ogg"], "volume_db": -3.5, "pitch": [0.9, 1.1], "bus": "ui", "loop": true },
              "model.ignored": { "file": "x.glb" },
              "audio": { "defaults": { "ui.tap": "sfx.simple" }, "events": { "LevelUp": "sfx.rich" } }
            }
            """, "m.json");

        manifest.Problems.ShouldBeEmpty();
        manifest.Sounds.Select(s => s.Key).ShouldBe(["sfx.simple", "sfx.rich"]);
        var simple = manifest.Sounds[0];
        simple.Files.ShouldBe(["a.ogg"]);
        simple.VolumeDb.ShouldBeNull();
        simple.PitchMin.ShouldBeNull();
        simple.Bus.ShouldBeNull();
        simple.Loop.ShouldBeFalse();
        var rich = manifest.Sounds[1];
        rich.Files.ShouldBe(["a.ogg", "b.ogg"]);
        rich.VolumeDb.ShouldBe(-3.5);
        (rich.PitchMin, rich.PitchMax).ShouldBe((0.9, 1.1));
        rich.Bus.ShouldBe("ui");
        rich.Loop.ShouldBeTrue();
        manifest.Defaults.ShouldBe([new KeyValuePair<string, string>("ui.tap", "sfx.simple")]);
        manifest.Events.ShouldBe([new KeyValuePair<string, string>("LevelUp", "sfx.rich")]);
    }

    [Fact]
    public void The_audio_section_is_not_an_asset_key()
    {
        AssetManifest.LoadKeys(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default")).ShouldNotContain("audio");
    }

    [Theory]
    [InlineData("""{ "sfx.a": { "file": 3 } }""", "sfx.a.file")]
    [InlineData("""{ "sfx.a": { "files": "a.ogg" } }""", "sfx.a.files")]
    [InlineData("""{ "sfx.a": { "file": "a.ogg", "pitch": [1] } }""", "sfx.a.pitch")]
    [InlineData("""{ "sfx.a": { "file": "a.ogg", "volume_db": "loud" } }""", "sfx.a.volume_db")]
    [InlineData("""{ "sfx.a": { "file": "a.ogg", "loop": "yes" } }""", "sfx.a.loop")]
    [InlineData("""{ "sfx.a": { "file": "a.ogg", "volum_db": 1 } }""", "sfx.a.volum_db")]
    [InlineData("""{ "sfx.a": { "file": "a.ogg", "files": ["b.ogg"] } }""", "sfx.a")]
    [InlineData("""{ "sfx.a": "a.ogg" }""", "sfx.a")]
    [InlineData("""{ "audio": { "defaults": [] } }""", "audio.defaults")]
    [InlineData("""{ "audio": { "events": { "LevelUp": 5 } } }""", "audio.events.LevelUp")]
    [InlineData("""{ "audio": { "default": {} } }""", "audio.default")]
    public void Malformed_shapes_are_reported_with_their_path(string json, string path)
    {
        AudioManifest.Parse(json, "m.json").Problems.Select(p => p.Path).ShouldContain(path);
    }

    // ---- the real theme and content ----

    [Fact]
    public void Sample_content_and_theme_pass_the_audio_rule()
    {
        var report = Validate();

        report.IsValid.ShouldBeTrue(string.Join("\n", report.Errors));
    }

    [Fact]
    public void The_sample_theme_covers_every_weapon_type_and_the_required_events()
    {
        var audio = AudioManifest.Load(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default"));
        var defaults = audio.Defaults.Select(d => d.Key).ToList();

        defaults.ShouldBe(AudioCategories.All.Ordered, ignoreOrder: true);
        audio.Events.Select(e => e.Key).ShouldBeSubsetOf(EventNames.All);
        audio.Events.Select(e => e.Key).ShouldContain("LevelUp");
        audio.Events.Select(e => e.Key).ShouldContain("JobLevelUp");
        audio.Events.Select(e => e.Key).ShouldContain("QuestCompleted");
        audio.Events.Select(e => e.Key).ShouldContain("ItemAcquired");
        audio.Sounds.ShouldContain(s => s.Files.Count > 1 && s.VolumeDb != null && s.PitchMin != null, "one entry uses the rich form with variations");
    }

    [Fact]
    public void Sample_content_names_sounds()
    {
        var db = ContentLoader.Load(ContentDir).Database!;

        db.Monsters.All.ShouldAllBe(m => m.Sounds != null);
        db.Items.All.Count(i => i.UseSfx != null || i.EquipSfx != null).ShouldBeGreaterThanOrEqualTo(2);
        db.Maps.All.ShouldAllBe(m => m.AmbienceAssetKey != null);
    }

    // ---- validation errors ----

    [Fact]
    public void A_missing_optional_content_key_is_not_an_error()
    {
        Edit("monsters/gel_slime.yaml", "sounds:\n  hit: sfx.monster.hit.slime\n  die: sfx.monster.die.slime\n", "");
        Edit("maps/starter_meadow.yaml", "ambience_asset_key: ambience.map.starter_meadow\n", "");

        var report = Validate();

        report.IsValid.ShouldBeTrue(string.Join("\n", report.Errors));
    }

    [Fact]
    public void A_content_sound_key_that_is_not_in_the_manifest_is_an_asset_error()
    {
        Edit("monsters/gel_slime.yaml", "hit: sfx.monster.hit.slime", "hit: sfx.monster.hit.nope");

        var issue = Single(Validate(), ValidationCodes.AssetMissing);

        issue.File.ShouldBe("monsters/gel_slime.yaml");
        issue.Path.ShouldBe("sounds.hit");
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("monsters/gel_slime.yaml", "die: sfx.monster.die.slime", "die: ambience.map.starter_meadow", "sounds.die")]
    [InlineData("items/healing_draught.yaml", "use_sfx: sfx.item.use.potion", "use_sfx: music.map.starter_meadow", "use_sfx")]
    [InlineData("items/iron_blade.yaml", "equip_sfx: sfx.item.equip.metal", "equip_sfx: icon.item.iron_blade", "equip_sfx")]
    [InlineData("maps/starter_meadow.yaml", "ambience_asset_key: ambience.map.starter_meadow", "ambience_asset_key: sfx.ui.tap", "ambience_asset_key")]
    [InlineData("maps/starter_meadow.yaml", "music_asset_key: music.map.starter_meadow", "music_asset_key: sfx.ui.tap", "music_asset_key")]
    [InlineData("skills/ember_bolt.yaml", "sfx_asset_key: sfx.skill.fire", "sfx_asset_key: vfx.skill.ember_bolt", "sfx_asset_key")]
    public void A_content_key_with_the_wrong_prefix_is_an_error(string file, string from, string to, string path)
    {
        Edit(file, from, to);

        var issue = Single(Validate(), ValidationCodes.AudioPrefix);

        issue.File.ShouldBe(file);
        issue.Path.ShouldBe(path);
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_default_for_an_unknown_category_is_an_error_naming_the_manifest_line()
    {
        var issue = Single(ValidateAudio(EditManifest("\"ui.tap\": \"sfx.ui.tap\"", "\"ui.tapp\": \"sfx.ui.tap\"")), ValidationCodes.VocabUnknown);

        issue.File.ShouldBe("themes/default/asset-manifest.json");
        issue.Path.ShouldBe("audio.defaults.ui.tapp");
        issue.Line.ShouldBeGreaterThan(300);
    }

    [Fact]
    public void A_default_for_an_unknown_weapon_type_is_an_error()
    {
        Single(ValidateAudio(EditManifest("\"weapon.bow.swing\"", "\"weapon.crossbow.swing\"")), ValidationCodes.VocabUnknown)
            .Path.ShouldBe("audio.defaults.weapon.crossbow.swing");
    }

    [Fact]
    public void An_event_that_is_not_in_the_vocabulary_is_an_error()
    {
        Single(ValidateAudio(EditManifest("\"LevelUp\"", "\"LevelUpp\"")), ValidationCodes.VocabUnknown)
            .Path.ShouldBe("audio.events.LevelUpp");
    }

    [Fact]
    public void A_default_or_event_pointing_at_a_missing_key_is_an_error()
    {
        Single(ValidateAudio(EditManifest("\"monster.die\": \"sfx.monster.die.generic\"", "\"monster.die\": \"sfx.monster.die.gone\"")), ValidationCodes.AssetMissing)
            .Path.ShouldBe("audio.defaults.monster.die");
        Single(ValidateAudio(EditManifest("\"LevelUp\": \"sfx.event.level_up\"", "\"LevelUp\": \"sfx.event.gone\"")), ValidationCodes.AssetMissing)
            .Path.ShouldBe("audio.events.LevelUp");
    }

    [Fact]
    public void A_default_pointing_at_a_key_with_another_prefix_is_an_error()
    {
        Single(ValidateAudio(EditManifest("\"ui.tap\": \"sfx.ui.tap\"", "\"ui.tap\": \"music.map.starter_meadow\"")), ValidationCodes.AudioPrefix)
            .Path.ShouldBe("audio.defaults.ui.tap");
    }

    [Theory]
    [InlineData("\"bus\": \"sfx\"", "\"bus\": \"effects\"", ValidationCodes.VocabUnknown, "sfx.weapon.impact.slash.bus")]
    [InlineData("\"pitch\": [0.95, 1.05]", "\"pitch\": [1.2, 0.8]", ValidationCodes.AudioRange, "sfx.weapon.impact.slash.pitch")]
    [InlineData("\"pitch\": [0.95, 1.05]", "\"pitch\": [0, 1]", ValidationCodes.AudioRange, "sfx.weapon.impact.slash.pitch")]
    [InlineData("\"pitch\": [0.95, 1.05]", "\"pitch\": [-1, 1]", ValidationCodes.AudioRange, "sfx.weapon.impact.slash.pitch")]
    [InlineData("\"volume_db\": -3", "\"volume_db\": 40", ValidationCodes.AudioRange, "sfx.weapon.impact.slash.volume_db")]
    [InlineData("\"volume_db\": -3", "\"volume_db\": -90", ValidationCodes.AudioRange, "sfx.weapon.impact.slash.volume_db")]
    [InlineData("\"files\": [\"placeholder.ogg\", \"placeholder.ogg\", \"placeholder.ogg\"]", "\"files\": []", ValidationCodes.AudioFile, "sfx.weapon.impact.slash.files")]
    [InlineData("\"files\": [\"placeholder.ogg\", \"placeholder.ogg\", \"placeholder.ogg\"]", "\"files\": [\"a.ogg\", \"b.wav\", \"c.ogg\"]", ValidationCodes.AudioFile, "sfx.weapon.impact.slash.files[1]")]
    [InlineData("\"files\": [\"placeholder.ogg\", \"placeholder.ogg\", \"placeholder.ogg\"],", "", ValidationCodes.AudioFile, "sfx.weapon.impact.slash.file")]
    public void Bad_sound_entries_are_errors(string from, string to, string code, string path)
    {
        var issue = Single(ValidateAudio(EditManifest(from, to)), code);

        issue.Path.ShouldBe(path);
        issue.File.ShouldBe("themes/default/asset-manifest.json");
        issue.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_plain_file_that_is_not_ogg_is_an_error()
    {
        var text = EditManifest("\"sfx.ui.tap\": {\n    \"file\": \"placeholder.ogg\"", "\"sfx.ui.tap\": {\n    \"file\": \"tap.mp3\"");

        Single(ValidateAudio(text), ValidationCodes.AudioFile).Path.ShouldBe("sfx.ui.tap.file");
    }

    [Fact]
    public void Shape_problems_are_errors()
    {
        var text = EditManifest("\"bus\": \"sfx\"", "\"bus\": \"sfx\", \"volum_db\": 1");

        Single(ValidateAudio(text), ValidationCodes.AudioShape).Path.ShouldBe("sfx.weapon.impact.slash.volum_db");
    }

    [Fact]
    public void Without_a_manifest_only_the_content_keys_are_checked()
    {
        var load = ContentLoader.Load(ContentDir);
        var keys = AssetManifest.LoadKeys(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default"));

        ContentValidator.Validate(load.Database!, ContentDir, keys, [new AudioRule()]).Issues.ShouldBeEmpty();
    }

    // ---- resolver ----

    private static AudioResolver Resolver() => new(
        new Dictionary<string, string>
        {
            ["monster.die"] = "sfx.default.die",
            ["weapon.dagger.swing"] = "sfx.dagger.swing",
            ["weapon.dagger.impact"] = "sfx.dagger.hit",
        },
        new Dictionary<string, string> { ["LevelUp"] = "sfx.level_up" });

    [Fact]
    public void A_content_key_wins_over_the_category_default()
    {
        Resolver().Resolve("sfx.slime.die", AudioCategories.MonsterDie).ShouldBe("sfx.slime.die");
    }

    [Fact]
    public void Without_a_content_key_the_category_default_is_used()
    {
        Resolver().Resolve(null, AudioCategories.MonsterDie).ShouldBe("sfx.default.die");
        Resolver().Resolve("", AudioCategories.MonsterDie).ShouldBe("sfx.default.die");
    }

    [Fact]
    public void With_neither_there_is_silence()
    {
        Resolver().Resolve(null, AudioCategories.MonsterAggro).ShouldBeNull();
        Resolver().ForEvent("QuestCompleted").ShouldBeNull();
        Resolver().WeaponSwing("bow").ShouldBeNull();
    }

    [Fact]
    public void Weapon_and_event_sounds_come_from_the_theme_tables()
    {
        Resolver().WeaponSwing("dagger").ShouldBe("sfx.dagger.swing");
        Resolver().WeaponImpact("dagger").ShouldBe("sfx.dagger.hit");
        Resolver().ForEvent("LevelUp").ShouldBe("sfx.level_up");
    }

    [Fact]
    public void The_real_theme_resolves_every_category_it_lists()
    {
        var audio = AudioManifest.Load(AssetManifest.PathFor(Path.Combine(RepoRoot(), "themes"), "default"));
        var resolver = new AudioResolver(audio.Defaults.ToDictionary(d => d.Key, d => d.Value), audio.Events.ToDictionary(e => e.Key, e => e.Value));

        foreach (var category in AudioCategories.All) resolver.Resolve(null, category).ShouldNotBeNull(category);
        foreach (var type in WeaponTypes.All)
        {
            resolver.WeaponSwing(type).ShouldNotBeNull(type);
            resolver.WeaponImpact(type).ShouldNotBeNull(type);
        }
        resolver.ForEvent(EventNames.LevelUp).ShouldNotBeNull();
    }

    // ---- client bundle ----

    [Fact]
    public void The_client_bundle_carries_the_content_sound_keys()
    {
        var db = ContentLoader.Load(ContentDir).Database!;
        using var doc = JsonDocument.Parse(ClientBundleExporter.Export(db).FileBytes);
        var root = doc.RootElement;

        var slime = root.GetProperty("monsters").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "monster.gel_slime");
        slime.GetProperty("sounds").GetProperty("hit").GetString().ShouldBe("sfx.monster.hit.slime");
        slime.GetProperty("sounds").TryGetProperty("aggro", out _).ShouldBeFalse();
        var draught = root.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "item.healing_draught");
        draught.GetProperty("use_sfx").GetString().ShouldBe("sfx.item.use.potion");
        var meadow = root.GetProperty("maps").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "map.starter_meadow");
        meadow.GetProperty("ambience").GetString().ShouldBe("ambience.map.starter_meadow");
    }

    [Fact]
    public void A_monster_without_sounds_has_no_sounds_key_in_the_bundle()
    {
        Edit("monsters/mossling.yaml", "sounds:\n  die: sfx.monster.die.generic\n", "");
        var db = ContentLoader.Load(ContentDir).Database!;
        using var doc = JsonDocument.Parse(ClientBundleExporter.Export(db).FileBytes);

        doc.RootElement.GetProperty("monsters").EnumerateArray()
            .Single(m => m.GetProperty("id").GetString() == "monster.mossling").TryGetProperty("sounds", out _).ShouldBeFalse();
    }

    [Fact]
    public void Changing_a_sound_key_changes_the_content_hash()
    {
        var before = ClientBundleExporter.Hash(ContentLoader.Load(ContentDir).Database!);
        Edit("monsters/gel_slime.yaml", "die: sfx.monster.die.slime", "die: sfx.monster.die.generic");

        ClientBundleExporter.Hash(ContentLoader.Load(ContentDir).Database!).ShouldNotBe(before);
    }

    // ---- schema ----

    [Fact]
    public void Sound_fields_get_asset_key_patterns_in_the_schemas()
    {
        var generator = new SchemaGenerator(XmlDocReader.ForAssembly(typeof(ItemDef).Assembly));

        var item = JsonDocument.Parse(generator.Generate(typeof(ItemDef))).RootElement.GetProperty("properties");
        item.GetProperty("use_sfx").GetProperty("pattern").GetString().ShouldBe(@"^sfx\.[a-z0-9_]+(\.[a-z0-9_]+)*$");
        item.GetProperty("equip_sfx").GetProperty("pattern").GetString().ShouldBe(@"^sfx\.[a-z0-9_]+(\.[a-z0-9_]+)*$");

        var map = JsonDocument.Parse(generator.Generate(typeof(MapDef))).RootElement.GetProperty("properties");
        map.GetProperty("ambience_asset_key").GetProperty("pattern").GetString().ShouldBe(@"^ambience\.[a-z0-9_]+(\.[a-z0-9_]+)*$");

        var monster = JsonDocument.Parse(generator.Generate(typeof(MonsterDef))).RootElement.ToString();
        monster.ShouldContain("\"sounds\"");
        monster.ShouldContain(@"^sfx\\.[a-z0-9_]+(\\.[a-z0-9_]+)*$");
    }
}
