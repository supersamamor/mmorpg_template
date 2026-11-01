using Game.Content.Loading;
using Game.Content.Registry;
using Game.Shared.Definitions;

namespace Game.Tests.Content;

public class ContentLoaderTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static string ContentRoot => Path.Combine(RepoRoot(), "content");

    /// <summary>Copies /content to a temp dir and overlays Fixtures/bad/&lt;case&gt; on top, so each case adds only its broken file(s).</summary>
    private static LoadResult LoadWithFixture(string fixture)
    {
        var temp = Path.Combine(Path.GetTempPath(), "content_" + Guid.NewGuid().ToString("N"));
        try
        {
            Copy(ContentRoot, temp);
            Copy(Path.Combine(RepoRoot(), "tests", "Game.Tests", "Fixtures", "bad", fixture), temp);
            return ContentLoader.Load(temp);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    [Fact]
    public void Sample_content_loads_without_errors()
    {
        var result = ContentLoader.Load(ContentRoot);

        result.Errors.ShouldBeEmpty(string.Join("\n", result.Errors));
        result.Success.ShouldBeTrue();
        var db = result.Database!;
        db.Jobs.Count.ShouldBe(3);
        db.Skills.Count.ShouldBe(11);
        db.Items.Count.ShouldBe(17);
        db.Monsters.Count.ShouldBe(5);
        db.Statuses.Count.ShouldBe(4);
        db.Quests.Count.ShouldBe(4);
        db.Npcs.Count.ShouldBe(2);
        db.Dialogue.Count.ShouldBe(2);
        db.Drops.Count.ShouldBe(8);
        db.Maps.Count.ShouldBe(2);
        db.ExpTables.Count.ShouldBe(3);
    }

    [Fact]
    public void Multi_document_exp_tables_load_as_separate_defs()
    {
        var db = ContentLoader.Load(ContentRoot).Database!;

        db.ExpTables.Get("exp.base").Exp.Count.ShouldBe(98);
        db.ExpTables.Get("exp.job").Exp.Count.ShouldBe(49);
    }

    [Fact]
    public void Polymorphic_effects_bind_to_concrete_records_and_singletons_load()
    {
        var db = ContentLoader.Load(ContentRoot).Database!;

        var twin = db.Skills.Get("skill.twin_strike");
        var damage = twin.Effects[0].ShouldBeOfType<DamageEffect>();
        damage.Hits.ShouldBe(2);
        twin.MaxLevel.ShouldBe(5);
        twin.WeaponTypes.ShouldBe(new[] { "dagger", "sword_1h", "axe" });
        db.Quests.All.SelectMany(q => q.Steps).SelectMany(s => s.Objectives).ShouldNotBeEmpty();
        db.Quests.All.SelectMany(q => q.Rewards).ShouldNotBeEmpty();
        db.Formulas.PhysicalDamage.ShouldNotBeNullOrWhiteSpace();
        db.Elements.Multipliers.ShouldNotBeEmpty();
        db.SourceOf("skill.twin_strike")!.Value.File.ShouldBe("skills/twin_strike.yaml");
    }

    [Fact]
    public void Registry_Get_throws_helpfully_and_TryGet_does_not()
    {
        var skills = ContentLoader.Load(ContentRoot).Database!.Skills;

        skills.TryGet("skill.twin_strike", out var found).ShouldBeTrue();
        found.Id.ShouldBe("skill.twin_strike");
        skills.TryGet("skill.nope", out _).ShouldBeFalse();
        var ex = Should.Throw<KeyNotFoundException>(() => skills.Get("skill.twin_strik"));
        ex.Message.ShouldContain("skill.twin_strik");
        ex.Message.ShouldContain("SkillDef");
        skills.All.Select(s => s.Id).ShouldBe(skills.All.Select(s => s.Id).OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public void Registry_rejects_duplicates_at_construction()
    {
        var a = new SkillDef { Id = "skill.a" };
        Should.Throw<ArgumentException>(() => new Registry<SkillDef>(new[] { a, a }));
    }

    [Fact]
    public void Missing_root_is_an_error()
    {
        var result = ContentLoader.Load(Path.Combine(Path.GetTempPath(), "no_such_content_root_" + Guid.NewGuid().ToString("N")));

        result.Database.ShouldBeNull();
        result.Errors.Single().Code.ShouldBe(ContentErrorCodes.RootMissing);
    }

    [Fact]
    public void Unknown_key_reports_file_line_column_and_path()
    {
        var result = LoadWithFixture("unknown_key");

        result.Database.ShouldBeNull();
        var e = result.Errors.Single();
        e.File.ShouldBe("skills/typo.yaml");
        e.Line.ShouldBe(8);
        e.Column.ShouldBeGreaterThan(0);
        e.Code.ShouldBe(ContentErrorCodes.UnknownKey);
        e.Path.ShouldBe("effects[0].mutliplier");
        e.Message.ShouldContain("mutliplier");
    }

    [Fact]
    public void Wrong_prefix_is_an_error()
    {
        var e = LoadWithFixture("wrong_prefix").Errors.Single();

        e.File.ShouldBe("items/wrong_prefix.yaml");
        e.Line.ShouldBe(1);
        e.Code.ShouldBe(ContentErrorCodes.IdWrongPrefix);
        e.Message.ShouldContain("item.");
    }

    [Fact]
    public void Duplicate_id_across_files_names_the_first_definition()
    {
        var e = LoadWithFixture("duplicate_id").Errors.Single();

        e.File.ShouldBe("items/antidote_again.yaml");
        e.Code.ShouldBe(ContentErrorCodes.IdDuplicate);
        e.Message.ShouldContain("items/antidote.yaml");
    }

    [Fact]
    public void Duplicate_id_inside_a_multi_document_file_points_at_the_second_document()
    {
        var e = LoadWithFixture("multi_doc_dup").Errors.Single();

        e.File.ShouldBe("items/twice.yaml");
        e.Line.ShouldBe(5);
        e.Code.ShouldBe(ContentErrorCodes.IdDuplicate);
    }

    [Fact]
    public void Unknown_effect_type_reports_the_effect_position()
    {
        var e = LoadWithFixture("unknown_effect_type").Errors.Single();

        e.File.ShouldBe("skills/bad_type.yaml");
        e.Line.ShouldBe(6);
        e.Path.ShouldBe("effects[1]");
        e.Code.ShouldBe(ContentErrorCodes.UnknownType);
        e.Message.ShouldContain("explode");
    }

    [Fact]
    public void Missing_effect_type_reports_the_effect_position()
    {
        var e = LoadWithFixture("missing_effect_type").Errors.Single();

        e.File.ShouldBe("skills/no_type.yaml");
        e.Line.ShouldBe(4);
        e.Path.ShouldBe("effects[0]");
        e.Code.ShouldBe(ContentErrorCodes.MissingType);
    }

    [Fact]
    public void Wrong_value_type_reports_line_and_path()
    {
        var e = LoadWithFixture("invalid_value").Errors.Single();

        e.File.ShouldBe("skills/bad_value.yaml");
        e.Line.ShouldBe(3);
        e.Path.ShouldBe("max_level");
        e.Code.ShouldBe(ContentErrorCodes.InvalidValue);
    }

    [Fact]
    public void Yaml_syntax_error_reports_position()
    {
        var e = LoadWithFixture("yaml_syntax").Errors.Single();

        e.File.ShouldBe("items/broken.yaml");
        e.Code.ShouldBe(ContentErrorCodes.YamlSyntax);
        e.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void All_errors_are_collected_not_just_the_first()
    {
        var errors = LoadWithFixture("many_errors").Errors;

        errors.Select(e => (e.File, e.Code)).ShouldBe(new[]
        {
            ("items/b_wrong_prefix.yaml", ContentErrorCodes.IdWrongPrefix),
            ("skills/a_unknown_key.yaml", ContentErrorCodes.UnknownKey),
        });
        errors[1].Line.ShouldBe(3);
    }

    [Fact]
    public void Yaml_outside_the_layout_is_an_error()
    {
        var e = LoadWithFixture("unexpected_file").Errors.Single();

        e.File.ShouldBe("misc/stray.yaml");
        e.Code.ShouldBe(ContentErrorCodes.FileUnexpected);
    }
}
