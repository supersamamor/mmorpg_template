using Game.Content.Validation;
using Game.Tools.Validation;

namespace Game.Tests.Tools;

/// <summary>The schema check of <c>validate</c>: YAML documents against the generated <c>schemas/*.schema.json</c>.</summary>
public class SchemaCheckTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "schemacheck_" + Guid.NewGuid().ToString("N"));
    private readonly string _schemas = Path.Combine(RepoRoot(), "schemas");

    public SchemaCheckTests()
    {
        var content = Path.Combine(RepoRoot(), "content");
        foreach (var file in Directory.GetFiles(content, "*.yaml", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_temp, Path.GetRelativePath(content, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

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

    private void Edit(string relativeFile, string from, string to)
    {
        var path = Path.Combine(_temp, relativeFile);
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        text.ShouldContain(from, customMessage: $"test setup: '{from}' not found in {relativeFile}");
        File.WriteAllText(path, text.Replace(from, to));
    }

    [Fact]
    public void Sample_content_matches_its_schemas()
    {
        SchemaCheck.Check(_temp, _schemas).ShouldBeEmpty();
    }

    [Fact]
    public void A_number_where_a_formula_string_is_expected_is_reported_with_file_line_and_path()
    {
        Edit("skills/call_seedling.yaml", "cast_time_ms: \"2500\"", "cast_time_ms: 2500");

        var issue = SchemaCheck.Check(_temp, _schemas).ShouldHaveSingleItem();

        issue.Code.ShouldBe(ValidationCodes.SchemaViolation);
        issue.Severity.ShouldBe(ValidationSeverity.Error);
        issue.File.ShouldBe("skills/call_seedling.yaml");
        issue.Path.ShouldBe("cast_time_ms");
        issue.Line.ShouldBe(8);
        issue.Column.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_wrong_value_inside_a_list_names_the_indexed_path()
    {
        Edit("skills/call_seedling.yaml", "count: 2", "count: two");

        var issue = SchemaCheck.Check(_temp, _schemas).ShouldHaveSingleItem();

        issue.File.ShouldBe("skills/call_seedling.yaml");
        issue.Path.ShouldBe("effects[0].count");
    }

    [Fact]
    public void An_unknown_effect_type_is_reported_at_the_effect()
    {
        Edit("skills/call_seedling.yaml", "type: summon", "type: bogus");

        var issues = SchemaCheck.Check(_temp, _schemas);

        issues.ShouldNotBeEmpty();
        issues.ShouldAllBe(i => i.File == "skills/call_seedling.yaml" && i.Path.StartsWith("effects[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void A_root_file_is_checked_too()
    {
        Edit("balance.yaml", "skill_points_per_job_level: \"1\"", "skill_points_per_job_level: 1");

        var issue = SchemaCheck.Check(_temp, _schemas).ShouldHaveSingleItem();

        issue.File.ShouldBe("balance.yaml");
        issue.Path.ShouldBe("progression.skill_points_per_job_level");
    }

    [Fact]
    public void A_missing_schema_directory_is_one_error()
    {
        var issue = SchemaCheck.Check(_temp, Path.Combine(_temp, "no_schemas")).ShouldHaveSingleItem();

        issue.Message.ShouldContain("schema directory");
    }
}
