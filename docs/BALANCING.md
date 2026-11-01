# BALANCING.md — seeing and locking the effect of balance changes

Balance is data: `content/formulas.yaml`, `content/balance.yaml`, `content/elements.yaml`, plus monster, item and job stats.
Two tools show what a change does before anyone plays.

## 1. The `stats` report

```bash
dotnet run --project src/Game.Tools -- stats --job job.warden --level 10 --vs monster.gel_slime
dotnet run --project src/Game.Tools -- stats --job job.spellbinder --level 50 --build int=60,dex=30,vit=20 --format csv
dotnet run --project src/Game.Tools -- stats --monsters
```

| Option | Meaning |
|--------|---------|
| `--job`, `--level` | Job id and base level (job level = base level capped at the job's maximum). |
| `--build str=..,agi=..` | **Final** primary stat values. Unlisted stats stay at the starting value. The build must be payable: the real stat point costs and the points earned by the level are checked (`ProgressionRules.RaiseStat`), otherwise the command fails with the reason. Without `--build`, the points are spent evenly across all six stats. |
| `--vs <monster>` | Damage table for one monster (default: every monster). |
| `--weapon`, `--refine` | Override the weapon (default: the best weapon the job can use at that level, chosen from data) and its refine. |
| `--format text\|json\|csv` | Output format. |
| `--monsters` | Every monster against every job's auto-attack at the **expected player level**: `max(monster.level, job.requirements.min_base_level)`, read from data. |

Damage columns (one row per attack: `auto`, then each damaging skill of the job at its maximum level):
`hit_pct`, `crit_pct`, `min`/`max`/`avg` damage of a **landed** hit, `hits_to_kill` = landed hits of average damage to remove
the monster's max HP, `swings_to_kill` = hits_to_kill / hit chance (expected attacks including misses).

The numbers are **exact, not sampled**: hit and crit chances come from the combat resolver, and the two possible damages
(normal and critical) from resolving once with the crit roll forced below and once above the chance. Magic never misses or crits.

**Known gap:** content has no crit damage multiplier yet, so crits roll but deal ×1. `crit_pct` is reported as the formula says;
it does not change damage until a multiplier exists.

## 2. Golden snapshots

`tests/Game.Tests/Golden/*.json` are committed snapshots, compared by `BalanceGoldenTests`:

| File | Content |
|------|---------|
| `derived_stats.json` | Final primary and derived stats for each sample job at levels 1, 10, 50, 99 with a defined stat build. |
| `damage.json` | For each of those characters, min/max/avg, hit and crit chance and hits to kill against every monster, for the auto-attack and each skill. |
| `monster_ttk.json` | The `stats --monsters` table. |

**The stat builds** are defined in `BalanceGoldenTests` (`Builds`): per job, a weight per stat. Points are the ones the progression
rules award (`starting_stat_points` + the per-level award for each level above 1) and are spent with the real
`stat_point_cost` by `BalanceSimulator.SpendByWeight`: repeatedly raise the stat with the highest `weight / (raises + 1)` that is
still affordable. A job without a build gets equal weights. The weapon is the best usable one at refine 0.

### Changing a formula or a number on purpose

1. Edit the YAML (`formulas.yaml`, `balance.yaml`, a monster, ...).
2. `dotnet run --project src/Game.Tools -- validate`
3. `dotnet test`. The golden tests fail with one line per changed value, naming it and showing old and new:

   ```
   damage.json: 312 value(s) differ from the committed snapshot (key: old -> new).
     ~ job.novice/L01/monster.gel_slime/auto/avg: 14 -> 15
   ```
4. If the changes are what you meant, regenerate the snapshots. Never do this automatically or to "make the test pass":

   ```bash
   UPDATE_GOLDEN=1 dotnet test --filter "FullyQualifiedName~BalanceGoldenTests"
   ```
   On PowerShell: `$env:UPDATE_GOLDEN = "1"; dotnet test --filter "FullyQualifiedName~BalanceGoldenTests"; Remove-Item Env:UPDATE_GOLDEN`.
5. Review `git diff tests/Game.Tests/Golden`. Every changed line must be explained by your edit. Commit the YAML and the snapshots together.

The hand-computed matchups in `tests/Game.Tests/Combat/GoldenDamageTests.cs` still need their arithmetic redone by hand: the
snapshots lock the whole table, those explain individual numbers.
