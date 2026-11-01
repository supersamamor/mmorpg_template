using System.CommandLine;
using System.Text.Json;
using Game.Content.Loading;
using Game.Content.Simulation;
using Game.Tools.Commands;
using Game.Tools.Reports;

namespace Game.Tests.Tools;

/// <summary>
/// End-to-end tests of the <c>stats</c> command: parse the arguments, load the real content, print. The tests reference Game.Tools
/// (internals are visible to this project) and capture the console, so they run in the non-parallel collection.
/// </summary>
[Collection(TimingCollection.Name)]
public class StatsCommandTests
{
    private static readonly string Content = Path.Combine(RepoRoot(), "content");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Game.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Game.sln not found above " + AppContext.BaseDirectory);
    }

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var root = new RootCommand();
        root.Subcommands.Add(StatsCommand.Create());
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = root.Parse(new[] { "stats", "--content", Content }.Concat(args).ToArray()).Invoke();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public void Character_report_prints_stats_and_a_damage_table_with_time_to_kill()
    {
        var (exit, output, err) = Run("--job", "job.warden", "--level", "10", "--vs", "monster.gel_slime");

        exit.ShouldBe(0, err);
        output.ShouldContain("== character ==");
        output.ShouldContain("== stats ==");
        output.ShouldContain("== damage ==");
        output.ShouldContain("ttk_s");
        output.ShouldContain("monster.gel_slime");
    }

    [Fact]
    public void Time_to_kill_is_swings_to_kill_over_aspd()
    {
        var (exit, output, err) = Run("--job", "job.warden", "--level", "10", "--vs", "monster.gel_slime", "--format", "json");
        exit.ShouldBe(0, err);

        var sim = new BalanceSimulator(ContentLoader.Load(Content).Database!);
        var aspd = sim.Character("job.warden", 10).Sheet.Get("aspd");
        using var json = JsonDocument.Parse(output);
        var auto = json.RootElement.GetProperty("damage").EnumerateArray().Single(r => r.GetProperty("attack").GetString() == "auto");
        var swings = auto.GetProperty("swings_to_kill").GetDouble();

        aspd.ShouldBeGreaterThan(0);
        auto.GetProperty("ttk_s").GetDouble().ShouldBe(swings / aspd, 1e-3);
    }

    [Fact]
    public void Skills_have_no_time_to_kill()
    {
        var (exit, output, err) = Run("--job", "job.spellbinder", "--level", "20", "--vs", "monster.gel_slime", "--format", "json");
        exit.ShouldBe(0, err);

        using var json = JsonDocument.Parse(output);
        var skills = json.RootElement.GetProperty("damage").EnumerateArray().Where(r => r.GetProperty("attack").GetString() != "auto").ToList();
        skills.ShouldNotBeEmpty();
        skills.ShouldAllBe(r => r.GetProperty("ttk_s").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void Monster_report_lists_every_monster_with_time_to_kill_in_seconds()
    {
        var (exit, output, err) = Run("--monsters", "--format", "csv");

        exit.ShouldBe(0, err);
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.ShouldContain("monster,monster_level,max_hp,job,player_level,avg,hit_pct,hits_to_kill,swings_to_kill,ttk_s");
        var db = ContentLoader.Load(Content).Database!;
        lines.Count(l => l.StartsWith("monster.", StringComparison.Ordinal)).ShouldBe(db.Monsters.All.Count * db.Jobs.All.Count);
    }

    [Theory]
    [InlineData("--job", "job.nobody")]
    [InlineData("--job", "job.warden", "--level", "0")]
    [InlineData("--job", "job.warden", "--vs", "monster.nothing")]
    [InlineData("--job", "job.warden", "--build", "str=oops")]
    public void Bad_input_exits_1_with_a_message(params string[] args)
    {
        var (exit, output, err) = Run(args);

        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        err.ShouldNotBeEmpty();
    }

    [Fact]
    public void Missing_job_exits_1()
    {
        var (exit, _, err) = Run();

        exit.ShouldBe(1);
        err.ShouldContain("--job");
    }

    [Fact]
    public void Time_to_kill_helper_ignores_skills_and_non_kills()
    {
        var auto = new DamageRow("auto", 0, "neutral", 1, 1, 0, 1, 1, 1, 4, 4);
        StatsReport.TimeToKillSeconds(auto, 2).ShouldBe(2);
        StatsReport.TimeToKillSeconds(auto, 0).ShouldBeNull();
        StatsReport.TimeToKillSeconds(auto with { SwingsToKill = null }, 2).ShouldBeNull();
        StatsReport.TimeToKillSeconds(auto with { Attack = "skill.bash" }, 2).ShouldBeNull();
    }
}
