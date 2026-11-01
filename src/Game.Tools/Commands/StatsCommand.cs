using System.CommandLine;
using System.Globalization;
using Game.Content.Loading;
using Game.Content.Simulation;
using Game.Shared.Definitions;
using Game.Tools.Reports;

namespace Game.Tools.Commands;

/// <summary>
/// <c>stats --job &lt;id&gt; --level &lt;n&gt; [--build str=..,agi=..] [--vs &lt;monster&gt;] [--format text|json|csv]</c> prints a character's
/// stats and a damage table; <c>stats --monsters</c> prints every monster's time to kill at its expected player level.
/// <c>--build</c> gives final primary stat values and must be payable with the points earned by the level; without it
/// the points are spread evenly by weight. Exit code 0 on success, 1 on bad input.
/// </summary>
internal static class StatsCommand
{
    internal static Command Create()
    {
        var content = new Option<DirectoryInfo?>("--content") { Description = "Content directory (default: <repo>/content)" };
        var job = new Option<string?>("--job") { Description = "Job id, e.g. job.warden" };
        var level = new Option<int>("--level") { Description = "Base level", DefaultValueFactory = _ => 1 };
        var build = new Option<string?>("--build") { Description = "Final primary stats, e.g. str=40,vit=20 (default: points spread evenly)" };
        var vs = new Option<string?>("--vs") { Description = "Monster id for the damage table (default: all monsters)" };
        var weapon = new Option<string?>("--weapon") { Description = "Weapon item id (default: best weapon the job can use)" };
        var refine = new Option<int>("--refine") { Description = "Weapon refine level", DefaultValueFactory = _ => 0 };
        var monsters = new Option<bool>("--monsters") { Description = "Print monster time-to-kill at the expected player level" };
        var format = new Option<string>("--format") { Description = "Output format: text, json or csv", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json", "csv");

        var command = new Command("stats", "Print derived stats and damage tables for balance work")
            { content, job, level, build, vs, weapon, refine, monsters, format };
        command.SetAction(p => Run(p.GetValue(content), p.GetValue(job), p.GetValue(level), p.GetValue(build), p.GetValue(vs),
            p.GetValue(weapon), p.GetValue(refine), p.GetValue(monsters), p.GetValue(format)!));
        return command;
    }

    private static int Run(DirectoryInfo? contentDir, string? jobId, int level, string? build, string? vs, string? weapon,
        int refine, bool monstersMode, string format)
    {
        try
        {
            var dir = contentDir?.FullName ?? Path.Combine(FindRepoRoot(), "content");
            var load = ContentLoader.Load(dir);
            if (load.Database is null)
            {
                foreach (var e in load.Errors) Console.Error.WriteLine(e);
                return 1;
            }
            var sim = new BalanceSimulator(load.Database);
            var exact = build == null ? null : ParseBuild(build);

            IReadOnlyList<ReportTable> tables;
            if (monstersMode)
            {
                tables = StatsReport.ForMonsters(sim, MonsterTtk.Compute(sim));
            }
            else
            {
                if (jobId == null) return Fail("--job is required (or use --monsters).");
                if (level < 1 || level > sim.Progression.MaxBaseLevel) return Fail("--level must be 1.." + sim.Progression.MaxBaseLevel + ".");
                if (vs != null && !sim.Db.Monsters.Contains(vs)) return Fail("Unknown monster '" + vs + "'.");
                if (weapon != null && !sim.Db.Items.Contains(weapon)) return Fail("Unknown item '" + weapon + "'.");
                var character = sim.Character(jobId, level, exact, weaponId: weapon, refine: refine);
                var targets = vs != null
                    ? new[] { sim.Db.Monsters.Get(vs) }
                    : sim.Db.Monsters.All.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
                tables = StatsReport.ForCharacter(sim, character, targets);
            }
            Console.WriteLine(ReportTable.Render(tables, format));
            return 0;
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
    }

    private static Dictionary<string, int> ParseBuild(string text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException("--build expects stat=value pairs, got '" + part + "'.");
            result[kv[0].Trim()] = value;
        }
        return result;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
        throw new InvalidOperationException("Game.sln not found above the current directory; run from the repo or pass --content.");
    }
}
