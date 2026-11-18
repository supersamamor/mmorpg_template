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
| `--build str=..,agi=..` or `none` | **Final** primary stat values. Unlisted stats stay at the starting value. The build must be payable: the real stat point costs and the points earned by the level are checked (`ProgressionRules.RaiseStat`), otherwise the command fails with the reason. Without `--build`, the points are spent evenly across all six stats. |
| `--vs <monster>` | Damage table for one monster (default: every monster). |
| `--weapon`, `--refine` | Override the weapon (default: the best weapon the job can use at that level, chosen from data) and its refine. |
| `--format text\|json\|csv` | Output format. |
| `--monsters` | Every monster against every job's auto-attack at the **expected player level**: `max(monster.level, job.requirements.min_base_level)`, read from data. |
| `--duel <monster>` | One-on-one auto-attack fight between the character and a monster (T-309): expected swings, seconds and HP left from the exact hit/crit chances, plus a seeded simulation (`--fights`, default 1000, `--seed`, default 1) through the real `CombatResolver`. Add `--unarmed` (no weapon) and `--build none` (spend nothing). Monster skills run as the server's AI casts them (T-501). T-404 added `--gear item[:refine],...` (worn armour with refine), `--skills skill.x=level,...` (learned skills: passives go on the sheet, attack skills and heals are cast by a rotation, see section 5), `--spend str=2,vit=1` (spend the points by weight instead of `--build`) and `--job-level`. |
| `--leveling <level>` | Minutes from a new character to that base level, farming the best winnable monster at each level (T-404, section 5). |

Damage columns (one row per attack: `auto`, then each damaging skill of the job at its maximum level):
`hit_pct`, `crit_pct`, `min`/`max`/`avg` damage of a **landed** hit, `hits_to_kill` = landed hits of average damage to remove
the monster's max HP, `swings_to_kill` = hits_to_kill / hit chance (expected attacks including misses).

The numbers are **exact, not sampled**: hit and crit chances come from the combat resolver, and the two possible damages
(normal and critical) from resolving once with the crit roll forced below and once above the chance. Magic never misses or crits.

Critical damage is the normal damage × `combat.crit_multiplier` from `balance.yaml` (a formula; ×1.5 in the sample),
floored. `max` is the critical damage, and `avg` weights normal and critical damage by `crit_pct`.

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
     ~ job.wanderer/L01/monster.gel_slime/auto/avg: 14 -> 15
   ```
4. If the changes are what you meant, regenerate the snapshots. Never do this automatically or to "make the test pass":

   ```bash
   UPDATE_GOLDEN=1 dotnet test --filter "FullyQualifiedName~BalanceGoldenTests"
   ```
   On PowerShell: `$env:UPDATE_GOLDEN = "1"; dotnet test --filter "FullyQualifiedName~BalanceGoldenTests"; Remove-Item Env:UPDATE_GOLDEN`.
5. Review `git diff tests/Game.Tests/Golden`. Every changed line must be explained by your edit. Commit the YAML and the snapshots together.

The hand-computed matchups in `tests/Game.Tests/Combat/GoldenDamageTests.cs` still need their arithmetic redone by hand: the
snapshots lock the whole table, those explain individual numbers.


## 5. Early balance with weapons, skills and stat points (T-404)

T-309 balanced the meadow for an unarmed character. T-404 re-measured it with what has been added since: the Bone Dirk every character starts with,
refining and armour, Second Wind and Twin Strike, spent stat points. Every number below comes from the `stats` command and is pinned by
`tests/Game.Tests/Golden/EarlyBalanceTests.cs` and `WeaponBalanceTests.cs`.

**The model.** `stats --duel` is a seeded simulation (seed 1, 1,000 fights in the tests) through the real `CombatResolver` and `EffectRunner`. The player
casts by a fixed rotation: when its swing is due, the attack skill with the highest estimated damage (the mean of 200 real casts) that is off cooldown and
affordable; a heal skill only below half HP; else an auto-attack. A skill takes the place of a swing, its cast time (times `cast_time_mod`) is
spent standing, and the next swing comes one attack interval after it. SP is the full pool at the start of a fight and does not regenerate in it (regeneration is
paused in combat anyway). No kiting, no potions. Job level: a Wanderer follows its base level; a Warden who changed job at base level 10 has job level 4
(3 skill points: Twin Strike 3). Points are spent by a rule: `--spend str=2,vit=1` (two str raises per vit raise) is the simple rule; all-str is shown too.

**Time to level 10** (`stats --leveling 10`). At each level the Wanderer (Bone Dirk, str/vit rule, all skill points in Second Wind) farms the monster with the
best EXP per second among those it beats at least half the time and whose level is at most 2 above its own. A kill costs: 8 s walking and looting + the won fight +
the time natural regeneration needs to give back the HP it cost (5 s out-of-combat delay + HP lost / `hp_regen`; draughts are not counted); a lost fight costs
30 s and gives nothing, so seconds per kill = (8 + p x (fight + rest) + (1 - p) x 30) / p. Minutes per level = EXP to the next level / (EXP per kill x kills per minute).

| Level | Monster | EXP per kill | Seconds per kill | Kills per minute | EXP to next level | Minutes |
|---|---|---|---|---|---|---|
| 1 | Gel Slime | 14 | 8.2 | 7.29 | 65 | 0.64 |
| 2 | Gel Slime | 14 | 8.2 | 7.31 | 194 | 1.90 |
| 3 | Gel Slime | 14 | 8.2 | 7.32 | 400 | 3.91 |
| 4 | Mossling | 55 | 30.2 | 1.99 | 687 | 6.29 |
| 5 | Sting Wasp | 200 | 103.2 | 0.58 | 1,062 | 9.13 |
| 6 | Sting Wasp | 200 | 82.0 | 0.73 | 1,527 | 10.43 |
| 7 | Sting Wasp | 200 | 77.0 | 0.78 | 2,087 | 13.40 |
| 8 | Rock Beetle | 450 | 90.8 | 0.66 | 2,745 | 9.23 |
| 9 | Rock Beetle | 450 | 79.9 | 0.75 | 3,502 | 10.37 |
| | | | | | **12,269** | **65.3** (target 45-90) |

Before, with the old EXP (slime 4, mossling 12, wasp 30, beetle 70, boar 140) the same policy needed 412 minutes: the slime stayed the best EXP per second to
level 7, because regeneration makes a bigger monster cost far more than 3x the time. The boar (900 EXP) is never the best choice: it is the optional,
rewarding boss-like fight.

**Targets, before and after** (win rate = seeded simulation, 1,000 fights; "before" = the content at the start of T-404, measured the same way):

| Matchup | Target | Before | After |
|---|---|---|---|
| New character vs Gel Slime, 0 points, Bone Dirk | <= 12 swings, >= 30% HP | 1.25 swings, 93.9% HP | unchanged (the dirk one-shots the slime; unarmed it was 11.25 swings, 42.7%) |
| Same, 10 points (str 6 / str 4 vit 3) | <= 5 swings, >= 60% HP | 1.25 swings, 93.9% / 96.0% HP | unchanged |
| Level 3 vs Mossling (str 9 / str 6 vit 4) | >= 20% HP | 77.1% / 81.0% HP | unchanged |
| Level 5 / 6 vs Sting Wasp (str 11 / 12), Bone Dirk | 40-70% | 98.4% / 99.4% | 46.4% / 67.9% |
| Level 7 Wanderer, Bone Dirk +0, Second Wind 3, str/vit rule vs Rock Beetle | 50-75% | 0% (320 HP, atk 30) | 62.3% (all-str: 43%; without Second Wind: about 1%) |
| Level 10 Warden, Iron Blade +2, Padded Vest +2, Leather Cap, Twin Strike 3, str/vit rule vs Thornback Boar | 45-70% | 0% (620 HP, atk 48) | 56.2% (all-str: 52.4%; no armour or refine: below 30%) |
| Thistle Hollow's monsters | 50-80% each | only a Gel Slime | **not met**, see below |
| Time to level 10 | 45-90 min | 412 min | 65.3 min |

**Thistle Hollow** spawns only `monster.gel_slime` (4 of them, `thistle_hollow.grid.json`, generated from the authoring scene). The slime has to stay the trivial
first fight (T-309), so its win rate is 100% for any character, and no monster data can change that. The target needs map content that does not exist yet: 2-3
monsters (levels 8-12) added in the authoring scene, then tuned with the commands above (`--job job.warden --level 10 ... --duel <monster>`). This is the one
target this task could not meet; it is listed as a follow-up.

**Every data change and why**

| File | Change | Why |
|---|---|---|
| `monsters/sting_wasp.yaml` | `max_hp` 66 -> 90, `atk` 10 -> 13 | With the Bone Dirk it lost 98% of fights at level 5-6; now 46% / 68% with its Venom Sting. |
| `monsters/rock_beetle.yaml` | `max_hp` 320 -> 160, `atk` 30 -> 17, `def` 12 -> 9 | Unwinnable on purpose until weapons existed (ADR-002 row 117). Now 62% for a level 7 Wanderer with Second Wind. |
| `monsters/thornback_boar.yaml` | `max_hp` 620 -> 240, `atk` 48 -> 26, `def` 16 -> 10 | Same: 56% for a level 10 Warden in the best meadow gear. |
| `monsters/*.yaml` | `base_exp` slime 4 -> 14, mossling 12 -> 55, wasp 30 -> 200, beetle 70 -> 450, boar 140 -> 900; `job_exp` 3/8/20/45/90 -> 9/35/130/290/580 | Time to level 10 from 412 to 65 minutes, and the ladder slime, mossling, wasp, beetle that a player follows. Job EXP keeps its old ratio to base EXP (about 0.65). |
| `drops/thornback_boar.yaml` | Iron Blade and Oak Staff 1% -> 10% | A boar dies a few times an hour; at 1% the weapon the boar target assumes never came. |
| `drops/rock_beetle.yaml` | Padded Vest 1.5% -> 8% | 14 beetle kills take a character from level 8 to 10: a 69% chance of a vest on the way. |
| `drops/mossling.yaml` | Leather Cap 2% -> 5% | Same reason. |

No change to `formulas.yaml`, `balance.yaml`, jobs, skills or refine.

**Decisions.** *Row 219* (a weapon the new job cannot use stays worn after a job change): decided, **take it off**. The server unequips every worn item the new
job cannot use (a `job_ids` list without it, or a weapon type outside the job's) right after the job change, as if the player had taken it off (`ItemUnequipped`
and the inventory update; it stays in the bag). A character never stands in a job with a weapon it cannot use, and no skill checks are needed for it.
Code: `InventoryRules.JobCannotUse`, `InventorySystem.UnequipForNewJob`; test `A_job_change_takes_off_worn_gear_the_new_job_cannot_use`.
*Row 235* (do the reset items drop): decided, **no**. Every character starts with one of each, a reset is free and keeps the item below level 10, and the items
cannot be bought yet (no currency). A drop would make builds free to change at the levels where stat choices should matter; if respeccing turns out too stingy,
a shop (T-601 NPCs) is the place for them.

**Known limits.** The duel does not model kiting or potions, so a Spellbinder (ranged, magic) is not measured by it: a level 7 Spellbinder with the Bone Dirk and Ember Bolt 3
wins 0% against the beetle in melee, because the model never keeps it at range. The leveling estimate counts Second Wind only inside fights.
