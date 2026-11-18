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
        var duel = new Option<string?>("--duel") { Description = "Monster id: one-on-one auto-attack fight (expected swings, HP left, seeded win rate)" };
        var unarmed = new Option<bool>("--unarmed") { Description = "No weapon (what the server gives every character until T-401)" };
        var fights = new Option<int>("--fights") { Description = "Seeded fights for --duel", DefaultValueFactory = _ => 1000 };
        var seed = new Option<ulong>("--seed") { Description = "Random seed for --duel", DefaultValueFactory = _ => 1UL };
        var gear = new Option<string?>("--gear") { Description = "Worn non-weapon items with refine, e.g. item.padded_vest:2,item.leather_cap (T-404)" };
        var skills = new Option<string?>("--skills") { Description = "Learned skills and levels, e.g. skill.twin_strike=3,skill.tough_body=2; --duel casts the active ones (T-404)" };
        var spend = new Option<string?>("--spend") { Description = "Spend the points by weight instead of --build, e.g. str=1 (all str) or str=2,vit=1 (T-404)" };
        var jobLevel = new Option<int?>("--job-level") { Description = "Job level (default: the base level capped at the job's maximum)" };
        var leveling = new Option<int>("--leveling") { Description = "Minutes from a new character to this base level, killing the best winnable monster at each level (T-404)", DefaultValueFactory = _ => 0 };
        var format = new Option<string>("--format") { Description = "Output format: text, json or csv", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json", "csv");

        var command = new Command("stats", "Print derived stats and damage tables for balance work")
            { content, job, level, build, vs, weapon, refine, monsters, duel, unarmed, fights, seed, gear, skills, spend, jobLevel, leveling, format };
        command.SetAction(p => Run(p.GetValue(content), p.GetValue(job), p.GetValue(level), p.GetValue(build), p.GetValue(vs),
            p.GetValue(weapon), p.GetValue(refine), p.GetValue(monsters), p.GetValue(duel), p.GetValue(unarmed), p.GetValue(fights), p.GetValue(seed), p.GetValue(gear), p.GetValue(skills), p.GetValue(spend), p.GetValue(jobLevel), p.GetValue(leveling), p.GetValue(format)!));
        return command;
    }

    private static int Run(DirectoryInfo? contentDir, string? jobId, int level, string? build, string? vs, string? weapon,
        int refine, bool monstersMode, string? duelId, bool unarmed, int fights, ulong seed, string? gearText, string? skillsText, string? spendText, int? jobLevelOverride, int levelingTo, string format)
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
            if (levelingTo > 0)
            {
                var w = spendText == null ? new Dictionary<string, int> { ["str"] = 2, ["vit"] = 1 } : ParseBuild(spendText);
                var rows = LevelingEstimate.Run(sim, jobId ?? "job.wanderer", w, l => new Dictionary<string, int> { ["skill.second_wind"] = Math.Min(5, l - 1) }, levelingTo, 8, 0.5, 30, 2, fights, seed);
                var t = new ReportTable("leveling", "level", "monster", "base_exp", "seconds_per_kill", "kills_per_min", "exp_needed", "minutes");
                foreach (var row in rows) t.Add(row.Level, row.MonsterId, row.BaseExp, row.SecondsPerKill, row.KillsPerMinute, row.ExpNeeded, row.Minutes);
                t.Add("total", "", "", "", "", rows.Sum(x => x.ExpNeeded), rows.Sum(x => x.Minutes));
                tables = [t];
            }
            else if (monstersMode)
            {
                tables = StatsReport.ForMonsters(sim, MonsterTtk.Compute(sim));
            }
            else
            {
                if (jobId == null) return Fail("--job is required (or use --monsters).");
                if (level < 1 || level > sim.Progression.MaxBaseLevel) return Fail("--level must be 1.." + sim.Progression.MaxBaseLevel + ".");
                if (vs != null && !sim.Db.Monsters.Contains(vs)) return Fail("Unknown monster '" + vs + "'.");
                if (weapon != null && !sim.Db.Items.Contains(weapon)) return Fail("Unknown item '" + weapon + "'.");
                var gearList = gearText == null ? null : ParseGear(sim, gearText);
                var skillMap = skillsText == null ? null : ParseBuild(skillsText);
                if (skillMap != null)
                    foreach (var id in skillMap.Keys)
                        if (!sim.Db.Skills.Contains(id)) return Fail("Unknown skill '" + id + "'.");
                var weights = spendText == null || exact != null ? null : ParseBuild(spendText);
                var character = sim.Character(jobId, level, exact, weights, weaponId: weapon, refine: refine, unarmed: unarmed,
                    gear: gearList, skills: skillMap, jobLevel: jobLevelOverride);
                if (duelId != null)
                {
                    if (!sim.Db.Monsters.Contains(duelId)) return Fail("Unknown monster '" + duelId + "'.");
                    if (fights < 1) return Fail("--fights must be at least 1.");
                    Console.WriteLine(ReportTable.Render(StatsReport.ForDuel(character, sim.Db.Monsters.Get(duelId), Duel.Run(sim, character, sim.Db.Monsters.Get(duelId), fights, seed)), format));
                    return 0;
                }
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
        if (text.Trim() == "none") return result;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new ArgumentException("--build expects stat=value pairs, got '" + part + "'.");
            result[kv[0].Trim()] = value;
        }
        return result;
    }

    private static List<GearPiece> ParseGear(BalanceSimulator sim, string text)
    {
        var list = new List<GearPiece>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':');
            var refine = 0;
            if (kv.Length > 2 || (kv.Length == 2 && !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out refine)))
                throw new ArgumentException("--gear expects item[:refine] pairs, got '" + part + "'.");
            if (!sim.Db.Items.Contains(kv[0])) throw new ArgumentException("Unknown item '" + kv[0] + "'.");
            list.Add(new GearPiece(sim.Db.Items.Get(kv[0]), refine));
        }
        return list;
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
