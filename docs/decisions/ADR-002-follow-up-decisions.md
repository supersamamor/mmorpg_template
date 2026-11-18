# ADR-002 — Decisions on open questions and follow-ups from T-001 … T-704

| Field | Value |
|-------|-------|
| Status | Accepted |
| Date | 2026-11-02 |
| Decided by | Project lead (architect), at the owner's request: "follow your recommendation … so we don't have pending items" |

## Context
Every task reply ended with open questions and suggested follow-ups. Some of them were already settled by later tasks; the rest
were waiting. This record settles each one, so no question stays open.

Each decision has one outcome:
- **Done:** already true in the code.
- **Decided:** settled here, with nothing left to build.
- **→ T-xxx:** work assigned to an existing or new task.
- **Won't do (v1):** consciously dropped.

Future task prompts must carry the decisions listed under their task id (see the last section).

## Decisions

### Build, tooling and CI
| # | Question (source) | Decision |
|---|---|---|
| 1 | `.editorconfig` forces LF on Windows (T-001) | **Decided.** `.gitattributes` has `* text=auto eol=lf` with CRLF only for `.ps1/.bat/.sln`; they agree. |
| 2 | Package versions were picked, not looked up (T-001) | **→ T-110:** bump to current stable patch/minor versions, build and test. |
| 3 | CI workflow (T-001, T-102, T-209) | **→ T-110:** GitHub Actions: build, test, `validate`, schema-stale check, `export-client --check`. |
| 4 | Banned-API analyzer for rule 8 (T-001) | **→ T-110:** `Microsoft.CodeAnalysis.BannedApiAnalyzers` on `Game.Shared` (`DateTime.Now/UtcNow`, `new Random`, `Environment.TickCount*`, `Guid.NewGuid`, `System.IO`, `Stopwatch`). |
| 5 | `Game.Shared` iOS AOT safety (T-005) | **→ T-110:** `IsAotCompatible` + trim analyzers on the `net10.0` target, warnings as errors. |
| 6 | Validate YAML against the JSON Schemas in `validate` (T-003) | **→ T-110**, so the editor check and the runtime check can't disagree. |
| 7 | Marker so a new formula field can't be missed by `FormulaSources` (T-103) | **→ T-110:** a `[Formula]` attribute on formula properties plus a reflection test (test-only reflection; runtime stays reflection-free). |
| 8 | `StatsCommand` end-to-end test; time-to-kill in seconds now that `aspd` is attacks per second (T-107) | **→ T-110.** |
| 9 | Timing-sensitive tests in a non-parallel collection (T-205) | **→ T-110:** move every wall-clock or allocation-measuring test into a `DisableParallelization` collection. |
| 10 | Data-driven reference build per job for `stats` (T-107) | **Won't do (v1).** The test-side weights are enough; `--build` covers realistic runs. |

### Content and vocabulary
| # | Question (source) | Decision |
|---|---|---|
| 11 | Reward type `zeny` vs engine id `currency` (T-002) | **→ T-111:** rename the reward type to `currency`. "Zeny" is Ragnarok's currency name, and CLAUDE.md forbids copying its names, so the default display name becomes an original one (**"crowns"**), as data. |
| 12 | `MapEntered` is never raised (T-202) | **→ T-111:** remove it. Quests (T-602) listen to `EntityEnteredMap`, which the world already raises. |
| 13 | Who owns `EntitySpawned` (T-202) | **Done:** the monster spawner (T-301). |
| 14 | Skill target type list and status flags (T-002) | **→ T-502** (targets) and **T-501** (status flags), where they are first used. |
| 15 | Accessory `Slots` meaning (T-002) | **Decided:** "fits **any one** of the listed slots". T-111 documents it on the record and in VOCABULARY.md; T-401 implements it. |
| 16 | `summon.count` an int while other amounts are formulas (T-004) | **Won't do (v1).** Make it a formula when a summon needs to scale. |
| 17 | `skill.venom_sting` "in no job tree" warning (T-102) | **→ T-110:** a monster `ai.skills` reference counts as use; it is a monster skill. |
| 18 | `item.bone_dirk` unused (T-102) | **→ T-111:** add it to an existing drop table. After 17 and 18 `validate` reports 0 warnings. |
| 19 | Monsters never spawned / NPCs never placed warning (T-102, T-201) | **→ T-110:** now possible, because grids have spawns and NPC markers. |
| 20 | `start_job_id` may name a job that has a parent (T-105) | **→ T-111:** `BalanceRule` error; a new character starts as a root job. |
| 21 | Twin Strike level 1 deals 0.7× an auto-attack (T-106) | **→ T-111:** a skill must never be worse than an auto-attack at level 1. Raise its multiplier formula (skill content, not `formulas.yaml`) and update the golden values. |
| 22 | `MonsterAiDef.ChaseRange` doc comment says "from its target" (T-301) | **→ T-111:** it is measured from **home**. |
| 23 | Negative percent modifiers (T-104) | **→ T-110:** validation warning for a percent modifier ≤ −100. |
| 24 | Per-stat rounding flag (T-104) | **Won't do.** Formulas floor explicitly; HP pools read with `GetInt()`. |
| 25 | Clamps written both in `formulas.yaml` and in `balance.yaml` (T-106, T-109) | **Decided: keep both.** The resolver's clamp is authoritative and the duplicate is harmless; `formulas.yaml` (restricted) stays as it is. |
| 26 | Per-field formula whitelists (T-103) | **Done:** `FormulaRule` parses each source with its own symbol set. |
| 27 | `Math.Pow` precision across platforms (T-103) | **Decided: no change.** The server is authoritative and the client never evaluates balance formulas. |
| 28 | Monster element level (T-106) | **Won't do (v1).** Plain element only; v2 candidate. |
| 29 | `reach` objective coordinates were a guess (T-004) | **→ T-111:** check that every `reach` cell is walkable on its grid, and make that a `MapGridRule` check. |
| 30 | A quest giver must not hand out a quest the player already has (T-004) | **→ T-602:** the server guards it. |
| 31 | Placeholder asset files don't exist on disk (T-004) | **→ T-207:** the theme loader ships a generated placeholder pack (Git LFS). |

### Progression and combat
| # | Question (source) | Decision |
|---|---|---|
| 32 | Job change keeps or resets skill points and learned skills (T-105) | **Decided: keep both.** A reset is a separate respec feature (new backlog **T-503**). |
| 33 | Crit multiplier missing (T-106, T-107) | **Done:** `combat.crit_multiplier` in `balance.yaml`. |
| 34 | Shared monster stat-sheet helper, `ContentDatabase` → `IProgressionContent` adapter (T-105, T-106) | **Done** (`MonsterCatalog.CreateStats`, `ContentProgression`). |
| 35 | Ranged attacks without line of sight (T-301) | **→ T-302 (sent):** range > 1 needs line of sight when accepted and on every player swing; melee doesn't. |
| 36 | A character attacked during the disconnect grace period (T-210) | **→ T-302 (sent):** it stays attackable and can die, so disconnecting can't escape a fight. A resume shows it dead. |
| 37 | Natural HP/SP regeneration, monster heal on return (T-301) | **→ T-308.** |
| 38 | Dead monsters vanish before a death animation can play (T-301) | **→ T-308:** a `balance.yaml` corpse time before despawn. |
| 39 | Monsters can walk onto a warp cell and leave the map (T-301) | **→ T-308:** only players trigger warps; monsters treat warp cells as walkable but never warp. |

### World, movement and tick
| # | Question (source) | Decision |
|---|---|---|
| 40 | Snap back to the cell centre on re-path (T-203) | **Done:** T-212 keeps progress on the current step. |
| 41 | An entity standing on an unwalkable cell can't path out (T-203) | **Decided: no fallback.** Spawns, warps and the start cell are validated walkable, so it can't happen. |
| 42 | `MapGrid` neighbour mask, path cache (T-203) | **Won't do** unless a profile asks: 0.55 ms/tick with 200 walkers is about 0.5% of the budget. |
| 43 | Stats on the spawn path (T-212) | **Done:** `GameWorld.Spawn` takes an initializer that sets `Stats`. |
| 44 | Batched `MovementDelta` event (T-212) | **Won't do:** T-204 stayed within budget with per-cell events. |
| 45 | Entity-vs-entity blocking (T-212) | **Decided: no** in v1. Entities may share cells (typical for this genre, cheap, and no body-blocking griefing). |
| 46 | Intents for an entity mid-warp are dropped (T-202) | **Decided: acceptable.** The client re-sends after it receives the new snapshot. |
| 47 | Shared content load and validate between `Game.Tools` and the server (T-202) | **→ T-213.** |
| 48 | Server boot should validate the manifest's audio data (T-304) | **→ T-213:** yes, boot runs the same checks as `validate`. |
| 49 | Windows timer precision (~16 ms start lateness) (T-202) | **→ T-213:** high-resolution waitable timer on Windows. |
| 50 | Per-instance tick p50/p99 and counters (T-202) | **→ T-213** (data); shown by the admin panel in T-804/T-805. |
| 51 | Thread per instance vs worker pool (T-202) | **Won't do (v1):** revisit when a server runs more than ~16 instances. |
| 52 | AOI test for a crowd crossing bucket boundaries; a real Ctrl+C boot test (T-204) | **→ T-213.** |

### Maps
| # | Question (source) | Decision |
|---|---|---|
| 53 | `x/y/w/h` default to 0 when missing (T-201) | **Done:** `MapGridRule` requires `w ≥ 1`, `h ≥ 1` and in-bounds areas. |
| 54 | `map-grid.schema.json` uses a hyphen while other schemas use underscores (T-201) | **Decided: keep.** Renaming breaks editor mappings for no gain. |

### Network and sessions (restricted area `src/Game.Server/Net/**`)
| # | Question (source) | Decision |
|---|---|---|
| 55 | Lost rotated resume token (T-210) | **→ T-214:** the previous token stays valid until the client's first message on the new session, which is an implicit acknowledgement. It is still single use. |
| 56 | Characters in grace don't count toward `max-sessions` (T-210) | **→ T-214:** they count. A full server refuses new logins before it would evict anyone. |
| 57 | Memory and AOI cost with many characters in grace (T-210) | **→ T-214:** measured in its load test. |
| 58 | WebSocket compression and shared serialized deltas (T-204, T-205) | **→ T-214:** measure permessage-deflate with a 50-client load test and turn it on if it saves ≥ 30% at < 1 ms/tick CPU. Shared delta pieces only if the sink cost exceeds 2 ms/tick at 100 observers. MessagePack stays deferred. |
| 59 | A removal lost during a cross-map warp after disconnect (T-205) | **→ T-214:** fix it in the world, so the re-despawn workaround can go. |
| 60 | Wire side of combat: attack/respawn messages, HP and ground items in views, combat events to observers | **→ T-307.** |

### Client, mobile and art
| # | Question (source) | Decision |
|---|---|---|
| 61 | §9 contradiction: 40 characters × 5k > 150k triangles (T-005) | **Decided:** LOD0 ≤ 5k for the nearest 8 characters, LOD1 ≤ 2k for everyone else; worst case 104k, leaving ≥ 45k for the scene. ARCHITECTURE §9 is updated. |
| 62 | Rebase the spike before the phone run (T-005) | **Decided: yes** (sent to T-005). The probe builds real `Game.Shared` records. |
| 63 | .NET 8/9 lose support on 2026-11-10 while Godot's Android template needs net9 (T-005) | **Decided:** accepted for development builds. The client never holds secrets (rule 11), so the exposure is small. T-802 must ship on a supported runtime: move the client to net10 as soon as the Godot release supports it, and check this at every Godot upgrade. |
| 64 | Client refuses `ws://` outside debug, strip unused APK libraries, CI export job with timeout (T-005) | **→ new T-216** (after T-005). |
| 65 | AOI view-radius camera assumption (~22×13 visible cells) (T-204) | **→ T-206:** measure at default zoom on the reference phones, then adjust `aoi.*` if needed. |
| 66 | Progress within a step and client resume flow (T-204, T-210) | **→ T-206:** the client extrapolates from arrival time and implements §10. |
| 67 | Client bundle: format version check, hash in `hello` (T-209) | **→ T-206 / T-802.** |
| 68 | Price, weight and stack size in the client bundle (T-209) | **→ T-401:** added with the inventory UI, plus a format version bump. |
| 69 | Skill area shapes other than square (T-209) | **Won't do (v1):** square, matching Chebyshev distance. |
| 70 | Faces: separate head models or texture variants (T-704) | **Decided: texture variants on one shared head mesh.** That is one rig and one draw call, cheaper on mobile. Each face option maps to a texture key. |
| 71 | Hair colour is a single tint (T-704) | **Decided:** single tint in v1; a multi-colour hairstyle uses a mask channel later (v2). |
| 72 | Job outfits by gender, "hides hair" headgear (T-704) | **→ T-403** (modular equipment visuals): `hides_hair` flag and gendered meshes. |
| 73 | Shared sound-entry record so the client doesn't re-parse the manifest (T-304) | **→ T-305.** |
| 74 | Admin panel shows the loaded content hash (T-209) | **→ T-804** reuses `ClientBundleExporter.Hash`. |

### Setup and release
| # | Question (source) | Decision |
|---|---|---|
| 75 | Script accepts SDK licences and installs pinned Android packages (T-006) | **→ new T-007.** |
| 76 | `.gitattributes` for `*.keystore` / `*.jks` (T-006) | **→ T-802:** keystores are never committed; add them to `.gitignore` with the signing plan. |
| 77 | Save on disconnect in `Gateway.Release` (T-210) | **→ T-702.** |
| 78 | Local Docker stack (server + Postgres) and a Windows client build | **→ new T-807** (backlog, Phase 8). |

## Notes for future prompts
When writing these prompts, copy in the decisions listed:
- **T-206:** 65, 66, 67, 87, 102
- **T-207:** 31
- **T-305:** 73
- **T-401:** 15, 68, 85, 99 (`new_character.start_items`), 106, 117
- **T-403:** 72
- **T-501:** 14, 93, 118
- **T-502:** 14
- **T-503:** 32
- **T-602:** 12, 30
- **T-702:** 77, 85
- **T-802:** 63, 67, 76
- **T-804:** 50, 74

## Consequences
- Five tasks are new or reshaped with prompts: T-007, T-110, T-111, T-213 and T-308.
- T-307 and T-214 have prompts, but they touch `src/Game.Server/Net/**` and need the owner's explicit go-ahead before they start.
- The remaining new tasks (T-216, T-503, T-807) are backlog rows. Their prompts are written when their phase opens.

## Addendum — T-302 (2026-11-02)
| # | Question (source) | Decision |
|---|---|---|
| 79 | `StatSheet.WriteTo(asTarget)` allocates ~96 B per hit building `t_` names (T-302) | **→ T-308:** cache the target-side symbol slots once; the allocation test asserts 0. |
| 80 | A `StopMove` while standing still produces no movement event, so combat can't see it (T-302) | **→ T-307:** the gateway turns client `stop` into **both** `StopMove` and `StopAttack`. |
| 81 | The T-301 AI uses `long.MinValue` as "no move yet", so `tick - long.MinValue` overflows and `MovePending` is wrong until the first move (T-302) | **→ T-308:** a real bug; fix it with an explicit flag and add a regression test. |
| 82 | The death penalty edits `ProgressionState.BaseExp` directly (T-302) | **→ T-308:** add `ProgressionRules.LoseBaseExp` (never below 0 within the level, never loses a level), with golden tests; combat calls it. |
| 83 | Respawn point is the new-character start cell (T-302) | **→ T-308:** optional per-map `respawn` cell on `MapDef` (validated walkable), falling back to the map's first entry warp, then to `new_character`. Content sets it for both sample maps. |
| 84 | Ground items are skipped by the AOI until they have a wire kind (T-302) | **→ T-307:** add the `item` kind term and remove the skip. |
| 85 | Pickup and the progression store (T-302) | **→ T-401** (pickup uses `LootRules.MayPickUp` and despawns the item) and **→ T-702** (persist the in-memory progression store). |
| 86 | Rare drop chances 0.5–2% (T-302 drop audit) | **Decided: keep.** They are plausible for v1; the balance pass revisits them with real play data. |

## Addendum — T-110 (2026-11-02)
| # | Question (source) | Decision |
|---|---|---|
| 87 | Commit `content/client-content.reference.json` now so CI compares bundles (T-110) | **Decided: not yet.** No app packs a bundle yet, and a committed reference would make every content change in a parallel task conflict. **→ T-206** commits it in the change that first packs the bundle into the client; until then CI's `--allow-missing` step only prints the hash. |
| 88 | Does the CI NuGet cache key work on a real run (T-110) | **Decided:** checked on the first push to GitHub, which is the owner's call; nothing to build. |
| 89 | `BannedSymbolsTests` needs the analyzer package in the NuGet cache, so it can't run fully offline (T-110) | **Decided: acceptable.** `dotnet restore` runs before tests everywhere, CI included. |
| 90 | Schema-check `*.grid.json` against `map-grid.schema.json` (T-110) | **→ T-213:** `validate` checks grid files too, with the same error format. |
| 91 | Three major package bumps (YamlDotNet 18, Test.Sdk 18, xunit runner 4) (T-110) | **Decided: keep.** The suite passes on them and only two one-line YamlDotNet overrides were needed. |

## Addendum — T-308 and T-213 (2026-11-02)
| # | Question (source) | Decision |
|---|---|---|
| 92 | `MonsterReturned` has no wire mapping (T-308) | **Decided: none needed.** Clients see the monster walk home and its HP fraction refill; the event stays server-side (sound and quests may use it later). |
| 93 | `CombatSystem` chase uses a `-1_000_000` "never" sentinel (T-308) | **→ T-501:** replace it with an explicit flag when the effect runner reworks combat timing. It can't overflow today. |
| 94 | ARCHITECTURE §5.3 doesn't list `MapDef.respawn` (T-308) | **Done:** added. |
| 95 | Tick start lateness p99 is 12–32 ms after the high-resolution timer (T-213) | **Decided:** p50 dropped from ~8 ms to ~0 ms; the tail came from the other sessions building on the same machine. Re-measured by the lead after merging T-308 + T-213 (70 s run, two maps): **max start lateness 2–3 ms per 10 s window**, steady-state tick 34–55 µs avg. T-803 owns the production measurement. |
| 96 | Ctrl+C shutdown test uses Windows console APIs; Linux path (`kill -INT`) not run (T-213) | **Decided:** the first CI run on `ubuntu-latest` verifies it. If it is flaky there, mark the Ctrl+C test Windows-only and keep the `--duration` test on both. |

## Addendum — T-307 (2026-11-02)
| # | Question (source) | Decision |
|---|---|---|
| 97 | A character in its grace period stops auto-attacking (T-307) | **Decided: correct.** It can't keep farming while disconnected (no AFK botting through dropped sockets), and it can still be attacked (row 36). |
| 98 | `respawn` while alive is silently ignored (T-307) | **Decided: keep.** The request is idempotent and harmless, so no new rejection term is needed. |
| 99 | A new character (all stats 1, points unspent, no weapon) loses one-on-one to a Gel Slime (T-307 smoke test) | **→ new T-309:** the first fight must be winnable without spending points. Tune monster content first; formula changes are proposed, not made. Starting equipment **→ T-401** (`new_character.start_items`). |
| 100 | 62 ms combat tick on the first kill (first-run JIT in the reward path) (T-307) | **→ T-214:** the load test reports p99 after warm-up. If it is still over 50 ms, warm the hot paths at boot with a scratch instance tick. |
| 101 | Hits are lost while a client catches up after falling behind; the following snapshot restores state (T-307) | **Decided: acceptable.** Hits are cosmetic; HP, death and drops are state and arrive in the snapshot. T-214 documents it in §10. |
| 102 | The client receives a snapshot where its own character is already dead after a resume (T-307) | **→ T-206:** show the death screen with the respawn button straight from the snapshot. |

## Addendum — T-111 (2026-11-02)
| # | Question (source) | Decision |
|---|---|---|
| 103 | The starting class id `job.novice` is Ragnarok's class name (display name is already "Wanderer") (T-111) | **→ new T-112:** rename it to `job.wanderer`, with its EXP table and asset keys. Golden values must not change. |
| 104 | `job.swordsman` appears as an example id in comments, a schema description and VOCABULARY.md (T-111) | **→ T-112:** use the real sample id `job.warden`. |
| 105 | `BannedSymbolsTests` failed once in a full parallel run (nested `dotnet build`) (T-111) | **→ T-112:** find the root cause and fix it; prove it with five full runs. |
| 106 | Show `currency_name` (crowns) in the client bundle and HUD (T-111) | **→ T-401:** with the first currency UI, plus a bundle format bump. |
| 107 | Twin Strike level 1 is now exactly 1.0× an auto-attack; 80 golden keys moved by the +0.3× step (T-111) | **Decided: accepted.** T-309 re-checks early-game matchups on top of it. |

## Addendum — T-214 (2026-11-02)
| # | Question (source) | Decision |
|---|---|---|
| 108 | 200 bots in one view → ~15 KB/s per client, over the 10 KB/s budget (T-214) | **→ new T-215:** cap the visible set per observer (`aoi.max_visible`, nearest first, with hysteresis). The observer itself, its target and its attackers are always visible. This is the standard MMO answer and keeps every crowd within budget at no CPU cost. |
| 109 | permessage-deflate cut bytes by 85% but cost ~5 ms of CPU per tick at 50 bots (T-214) | **Decided: off in v1.** It stays a setting (`--net-compression`). The visible-set cap (row 108) keeps bandwidth in budget without it, so whether Godot's WebSocket client supports it doesn't matter for v1. Revisit only if T-803 shows mobile data use is a problem. |
| 110 | First tick of each map takes 64–176 ms at boot (T-214) | **Won't do (v1):** it happens once, before any player can join, and the stated rule (p99 after warm-up < 50 ms) holds. |
| 111 | Intents sent while a character is between maps can still be lost (T-214) | **Decided: acceptable** (same as row 46); the client re-sends after the new snapshot. |

## Addendum — starting the client before the phone test (2026-11-07)
| # | Question (source) | Decision |
|---|---|---|
| 112 | T-206 builds on the engine choice, but T-005's phone go/no-go is still open (no device has run it; iOS needs a Mac) | **Decided by the owner: start T-206 now.** Risk accepted: client work may need rework if device results are poor. T-005 stays In Progress, and T-206 reports all device behaviour as unverified. |

## Addendum — T-215, T-309, T-206 (2026-11-07)
| # | Question (source) | Decision |
|---|---|---|
| 113 | Ranking by distance alone lets nearby players crowd out monsters and NPCs; bots killed less and died more under the cap (T-215) | **→ new T-217:** reserve part of the cap by kind, so a quest NPC, the player's own loot and nearby monsters stay visible in a crowd. A hidden monster that can hit you is a gameplay bug, not just a display choice. |
| 114 | `aoi.max_visible: 50` leaves 9.95 KB/s at worst, against the 10 KB/s budget (T-215) | **Decided: keep 50** until T-803 has device data. |
| 115 | The cap is reached at 50 bots too, contrary to the T-215 prompt, because those views hold 61–78 entities (T-215) | **Decided: accepted.** The budget comes first. |
| 116 | T-215 added an `AttackStarted` event in `CombatSystem`, outside its listed scope (T-215) | **Decided: accepted.** Rule 7 requires an event; it is one publish line. |
| 117 | "Meadow's toughest monster" read as the Sting Wasp; an unarmed level 5–6 character has 0% wins against the Rock Beetle and Thornback Boar (T-309) | **Decided: accepted.** Those two are levels 7 and 10 and expect a weapon. **→ T-401** retunes them once weapons exist. |
| 118 | `stats --duel` models auto-attacks only, so the wasp's venom sting isn't counted (T-309) | **→ T-501:** model monster skills in `--duel` with the effect runner. |
| 119 | `welcome` doesn't carry the grace period, so the client keeps its own copy (T-206) | **Decided: keep for v1.** The client stops retrying on its own limit, and the server has the final say with `bye resume_failed`. |
| 120 | The client's own UI words live in `client/Core/UiText.cs` (T-206) | **→ T-211:** decide UI text storage with the UI shell. |
| 121 | The `aoi.*` comment in `balance.yaml` says 7 cells each way; measured depth is about 7 ahead and 4 behind (T-206) | **→ T-217:** fix the comment, no value change. |
| 122 | Live tests of `replaced`, `resume_failed`, `shutdown` on the Godot socket; `tools/loadtest` option for extra sessions (T-206) | **→ T-216.** |

## Addendum — T-216 (2026-11-08)
| # | Question (source) | Decision |
|---|---|---|
| 123 | No Android network-security config, unlike the T-216 prompt (T-216) | **Decided: accepted.** It doesn't cover Godot's native sockets and would need the Gradle export path. The client's own release rule is the real lock, proven on a Windows release export. **→ T-803:** confirm on a device that a release APK refuses `ws://`. |
| 124 | Godot downloads in CI are checked against the release's own `SHA512-SUMS.txt`, not pinned hashes (T-216) | **→ T-802:** pin the hashes in the repo when CI builds store releases, so a replaced release can't pass. Acceptable for a debug export job until then. |
| 125 | The APK fell from 104.2 MB to 44.3 MB by removing Mono static archives (T-216) | **Decided: keep, unverified on a device.** The phone test must install and start the stripped APK. If it fails to start, this is the first thing to revert. |
| 126 | Godot 4.7.2 does not support .NET 10 for Android (T-216) | **Decided:** the client stays on net8.0 / net9.0 (Android); `Game.Shared` already targets `netstandard2.1`. Recheck on a Godot upgrade. |

## Addendum — T-217 (2026-11-08)
| # | Question (source) | Decision |
|---|---|---|
| 127 | Monsters already hitting a player have no bound and can push a view past the cap, since the server has no entity collision (T-217) | **Decided: accept for v1.** Showing what is hitting you beats the cap; it takes a deliberate pile-up to matter. **→ T-803:** measure whether it occurs in real play. |
| 128 | The reservation numbers (15 monsters, 4 NPCs, 6 items, margin 2, 10 chasers) are reasoned estimates, not measured (T-217) | **Decided: keep.** They met the budget (max 8.3 KB/s at 200 bots) and moved kills back toward no cap. The comments in `balance.yaml` now say which are estimates. **→ T-803** revisits them with real data. |
| 129 | AOI phase cost rose about 40–70% (about 6 ms per 100 ms tick at 200 observers, every kind) (T-217) | **Decided: accept.** The tick has room and the p50 didn't rise. **→ T-803** if server data shows it matters. |
| 130 | At 200 bots T-215 had fewer deaths than no cap, so row 113's "died more" only shows, weakly, at 50 bots (T-217) | **Noted:** the kills finding stands, and the fix is still right. Deaths follow how much fighting happens. |
| 131 | A monster with 10 or more nearer chasers can land a first hit from out of view, appearing in the same delta as its hit (T-217) | **Decided: accept.** It's the cost of bounding a train. |

## Addendum — T-207 (2026-11-08)
| # | Question (source) | Decision |
|---|---|---|
| 132 | The export preset left out `themes/*/asset-manifest.json`, so a device build would draw every model as the missing marker (T-207) | **Done at merge:** added to every preset. `export-android.ps1` now mounts a copy of the themes first and re-imports; the APK was checked and holds the manifest, 80 imported assets and the bundle. |
| 133 | Headless Godot exits with -1073741819 after PASS in about 1 of 4 runs with model preload on; preload is now skipped in headless, root cause not found (T-207) | **→ new T-218:** find the root cause. A crash on exit may also point to a real shutdown bug on devices. |
| 134 | The exported APK also contains `themes/test` (T-207) | **→ T-802:** exclude test themes from store builds. It is under 1 MB, so harmless until then. |
| 135 | Validator check that manifest `fallbacks` values exist (T-207) | **→ T-218.** Only a client test checks this today. |

## Addendum — T-211 (2026-11-08)
| # | Question (source) | Decision |
|---|---|---|
| 136 | Where the client's own UI words live (row 120) (T-211) | **Decided: `client/Core/UiText.cs`.** The refusal and update screens must work when the bundle or data is broken, and a missing word is a compile error. Translation later replaces member bodies with table lookups; a test forbids text literals in `client/Scene`. |
| 137 | Hotbar default 6 slots, 1 row, 56 dp; UI scale 0.75–1.5 in 0.25 steps (T-211) | **Decided: keep.** Both are client config. **→ T-803** checks them on devices. |
| 138 | The HUD adds about 25 draw calls; quiet frames allocate 8–9 bytes (T-211) | **→ T-803:** measure on devices first. The scene stays well under the 100 draw-call budget, so no batching until a device shows a need. |
| 139 | The target frame shows the name only, because entity HP has no change counter (T-211) | **→ T-303:** add one with the combat presentation work. |
| 140 | Run `-Run ui` in CI (T-211) | **Done at merge** if `ci.yml` runs `-Run all`, which now includes `ui`. |

## Addendum — T-208, T-218 (2026-11-08)
| # | Question (source) | Decision |
|---|---|---|
| 141 | The headless crash is a Godot 4.7.2 engine bug: the dummy renderer's mesh allocator isn't thread-safe. A 20-line script with no game code reproduces it 10/10 (T-218) | **Decided:** in headless, models load on the main thread; with a real renderer they keep using threads. Crash rate went from 31% to 0 of 20. Any future threaded loading must do the same in headless (§7.7). Reporting it upstream to Godot is the owner's call, since it posts publicly. |
| 142 | The fallback check, like the asset-key check, only reads the active theme's own keys, so a partial theme pointing at a `default`-only key would be wrongly flagged (T-218) | **→ T-219:** resolve keys through the same theme chain the client uses. |
| 143 | Taps and monster picking still use the y = 0 plane, so taps on hills land slightly off (T-208) | **→ T-219:** pick against `GroundHeights`. |
| 144 | Keep `grid from-ascii` (T-208) | **Decided: keep** for test grids, documented as not for real maps. |
| 145 | `MapGridFile`'s doc comment still says the scene hash is "stored, not checked" (T-208) | **→ T-219:** fix the comment and regenerate the schema. |
| 146 | Run `client/export-maps.ps1 -Check` in CI's Godot job (T-208) | **→ T-219.** |
| 147 | Map scenes ship separately from their `.authoring.tscn` (spawns and markers); every export preset excludes the authoring scenes and the plugin (T-208) | **Decided: accepted.** That keeps spawn data out of the client (hard rule 11), and a test checks the presets. |

## Addendum — T-219, T-303 (2026-11-10)
| # | Question (source) | Decision |
|---|---|---|
| 148 | Terrain picking: march down the ray between the terrain's lowest and highest points, then bisect, using `GroundHeights` only (no physics query). On the sample hill the error goes from 0.44 cells to under 0.01, measured in unit tests (T-219) | **Decided: accepted.** A scripted tap on the hill that asserts the cell the server receives → **T-220**. |
| 149 | Theme-chain key checks merge the active theme's keys with `default`'s; the theme's entry wins (T-219) | **Decided: accepted.** The audio-key check (`AudioRule`) may have the same gap → **T-220** checks it. |
| 150 | NuGet's vulnerability feed timed out during restore (T-219) | **Decided: no change.** It was a network problem on one machine; CI keeps the audit on. |
| 151 | Hit timing: the swing starts at arrival plus the render delay; the blow lands after the attacker's `impact_delay` (model entry → animation library → client default 0.2 s, clamped to 1 s). `CombatTimeline.Impact` is the one timing source; audio uses it too (T-303, T-305) | **Decided: accepted.** |
| 152 | No replay: hits more than 0.5 s late are dropped, a snapshot clears pending hits, and old delta ticks are ignored (T-303) | **Decided: accepted**, closes row 101. |
| 153 | Damage numbers: cap 16, the oldest is dropped (not merged); numbers on one target take sideways lanes (T-303) | **Decided: accepted.** Dropping keeps every number readable; merging would hide crits. |
| 154 | Effects are named in the manifest by an `effect` field (`effect.hit`, `effect.crit`, `effect.death`, `effect.level_up`) with a fixed fallback level_up → crit → hit and death → hit, because client code may not name `vfx.*` keys (T-303) | **Decided: accepted.** A shared effect-name vocabulary waits until skills need one (T-501/T-502). |
| 155 | In a crowd of about 37 characters on screen, draw calls peak at 117, over the §9 limit of 100. The characters alone are about 80; effects add about 12 (T-303) | **→ T-220:** bring the crowd peak under 100 (for example simpler distant characters, or merged meshes), measured with `loadtest --spare-slots`. |
| 156 | The target HP bar updates when the server's `hp` arrives, up to about 0.4 s before the blow lands on screen (T-303) | **Decided: accept for v1.** Revisit if players notice; it only shows a fraction. |
| 157 | Corpse fade uses transparency plus sinking; transparency on the Mobile renderer is unconfirmed (T-303) | **Decided:** the sink alone is a visible fade. Transparency stays on the phone checklist (row 112). |

## Addendum — T-305 (2026-11-10)
| # | Question (source) | Decision |
|---|---|---|
| 158 | Client audio: 24 voices made at start; a per-key cooldown of 0.08 s; stealing by priority then age, never for a less important sound; skip beyond 30 world units; 2 s equal-power crossfade; a 48 MB LRU for decoded effects (an upper-bound estimate). All are client config `audio_*` (T-305) | **Decided: accepted.** |
| 159 | Swing sounds play when the attacker's `AttackCount` goes up, which is when the attack clip starts; impact, hit and aggro sounds play on `CombatTimeline.Impact` (T-305, row 151) | **Decided: accepted.** One timing source for visuals and audio. |
| 160 | The wire doesn't say which weapon a character holds, so everyone swings as `unarmed`; item use, equip and acquire have hooks for T-401 (T-305) | **→ T-401** sends or derives the weapon type and calls the hooks. |
| 161 | Godot 4.7.2 can't read the iOS silent switch or request Android audio focus from C#. `PlatformAudio` reads an optional `GameAudioPlatform` singleton; no plugin exists yet (T-305) | **→ Backlog:** small native plugins for iOS and Android, done together with the phone test (T-005). Until then the log says plainly that both are not handled. |
| 162 | `BannedSymbolsTests` failed once after 7 minutes while other sessions were building (T-305) | **Decided:** load-related. It passed 6 of 6 runs at merge. Watch it; it needs its own fix if it fails on an idle machine. |
| 163 | The headless release export hung at exit once at merge, with the file already written. `run-e2e.ps1` then ran that file even though Godot never exited, and a stale exe from an earlier run would also be accepted (T-305 merge) | **→ T-220:** time out the export at exit, check its exit code, and delete the old exe before exporting. |

## Addendum — T-220 (2026-11-15)
| # | Question (source) | Decision |
|---|---|---|
| 164 | Crowd draw calls: characters were already one call each. The excess came from about 18 loot-bag rings (one call each) and damage numbers (about 2 calls each). Rings are now one multimesh and numbers one canvas item. Peak 116–126 → 89–94 with 40 characters fighting, on an integrated desktop GPU (T-220) | **Decided: accepted**, closes row 155. Device numbers wait for the phone test (row 112). |
| 165 | The HUD costs a steady 25–31 draw calls, a quarter of the budget (T-220) | **→ Backlog:** measure on a phone first, as for row 138. Batch the HUD only if a device shows a need. |
| 166 | The export hang at exit was not reproduced (12 of 12 exited 0). `run-e2e.ps1` now deletes the old export, fails on a non-zero exit code, and fails with `EXPORT HUNG AT EXIT` after 60 s. Stopping Godot's console wrapper left `game-client.exe` running and locking the file; the script now stops it too (T-220) | **Decided: accepted**, closes row 163. |
| 167 | Scripted hill tap: `-Run hill` taps a point on cell (23, 13) that a flat-ground pick reads as (23, 12), and asserts the server walks there (T-220) | **Decided: accepted**, in `-Run all`. Closes row 148's follow-up. |
| 168 | The audio rule already resolved keys through the theme chain; four partial-theme tests now lock that in (T-220) | **Decided: no gap**, closes row 149. |

## Addendum — T-401 (2026-11-16)
| # | Question (source) | Decision |
|---|---|---|
| 169 | Weight: at 50% of `weight_limit` natural regeneration stops (`burdened`); at 90% the character can't attack (`overweight`); a pick-up past 100% is refused (`too_heavy`). Worn items count (T-401) | **Decided: accepted.** The numbers are in `balance.yaml` `inventory`. |
| 170 | Two-handed weapons: `inventory.two_handed_weapon_types` in `balance.yaml` (`sword_2h`, `staff`, `bow`); such a weapon also takes the shield slot, and either one takes the other off in the same step (T-401) | **Decided: accepted.** Data, not code. |
| 171 | `item.antidote` (`remove_status`) and `item.swift_elixir` (`apply_status`) are refused with `unsupported_effect`, nothing used up; only `heal` and `restore_sp` run, behind the effect seam (T-401) | **→ T-501** owns the seam and the other effect types. |
| 172 | `Net/**` changes: the five intents and two inventory messages in `ProtocolJson`, routing in `ClientSession` (a pick-up must target a ground item the client sees), and delivery in `Gateway` and `ViewMessageWriter` (`weapon`, `worn`) (T-401) | **Decided: accepted** as within the user's approval (2026-11-15): delivery is part of adding the messages. |
| 173 | Equipped items stay in the inventory, so equipping never needs a free entry. New characters start with the Bone Dirk worn and 3 Healing Draughts (`new_character.start_items`). Player drops vanish after 2 minutes. Other players see the weapon type and worn item ids only (T-401) | **Decided: accepted.** Closes row 160. |
| 174 | A tap goes to the monster or ground item nearest the finger; a monster wins a tie. The Bag button sits beside the hotbar (T-401) | **Decided: accepted.** |
| 175 | Row 117: retune the Rock Beetle and Thornback Boar now that weapons exist (T-401 left it out of scope) | **→ T-404:** an early-balance pass with weapons, measured like T-309. |
| 176 | Godot's headless `--import` hangs intermittently (3 in about 9 imports, not a stale cache), and the release export hung at exit twice; each passed on retry (T-401) | **→ T-221:** a bounded, retried import in `godot-path.ps1`, and a minimal reproduction if it's an engine bug. |
| 177 | Drop drops the whole stack (T-401) | **→ Backlog:** a quantity picker, with the trade and storage UI. |

## Addendum — T-403 (2026-11-17)
| # | Question (source) | Decision |
|---|---|---|
| 178 | Worn pieces are merged into the body's skinned mesh (attached pieces weighted fully to their bone), so a dressed character stays one draw call; a merged mesh is cached by body, level of detail and set of pieces. Separate bone-attached nodes peaked at 117 in a crowd; merged, full gear peaks at 79 (92 with `--fight`) for 33–39 players on an integrated desktop GPU (T-403) | **Decided: accepted.** A change re-merges the character but loads only the changed pieces; that meets "swap only the changed pieces". The CPU cost of a merge on a phone goes on the phone checklist (row 112). |
| 179 | Untextured materials that differ only in colour are baked into vertex colours to share one surface; a textured material keeps its own surface (T-403) | **Decided:** real equipment art uses one texture atlas per rig, so characters stay at one draw call. This is a rule for T-707 and any art pack. |
| 180 | A missing or wrong equipment key draws the theme's missing model at 0.3 scale on bone 0 (the hips), with one warning per key. Weapon-type clips are manifest entries `<animation id>.<weapon type>`, falling back to the generic clip (T-403) | **Decided: accepted.** |
| 181 | The Bone Dirk placeholder looks like a staff, because `themegen` picks the hand-weapon shape by a hash of the key (T-403) | **→ Backlog:** choose the shape by weapon type, with the next `themegen` change. |
| 182 | Validate that each equipment manifest entry has exactly one of `attach` or `skinned`, and that weapon-type clip suffixes are known weapon types (T-403) | **→ T-405:** a small validation task, with preloading worn visuals (a `DisplayBundle` list of worn visual keys). |

## Addendum — T-402 (2026-11-17)
| # | Question (source) | Decision |
|---|---|---|
| 183 | Refining is data in `content/refine.yaml`: max +7; +1 and +2 always succeed, then 90/75/60/45/30%; material Temper Shard ×1–3; a failure keeps the level up to +4, drops one at +5 and +6, and breaks the item at +7. One `Next(100)` from the instance random per try (T-402) | **Decided: accepted** as the starting table. It's balance data; tune it with T-404. |
| 184 | Refine affects armour only through the item's own modifiers using the existing `refine` variable (Padded Vest `def: 4 + refine * 2`, Wooden Buckler `def: 2 + refine`); the weapon through `refine * 2` in `physical_damage`. No formula change (T-402) | **Decided: accepted.** |
| 185 | One refine table for all gear (T-402) | **Decided: enough for v1.** A per-item table id waits until there's a reason for different tables. |
| 186 | Cards are permanent, count only while the item is worn (source `card:<entry>:<socket>`), and their weight goes away when socketed (T-402) | **Decided: accepted.** Card removal → T-601 or later, as an NPC service. |
| 187 | `insert_card` and `refine` work anywhere, cost no currency, and don't need an NPC (T-402) | **→ T-601** decides whether refining moves behind an NPC service; a currency cost waits for currency. |
| 188 | In the bag, tapping a worn slot now selects the item (Take off, Refine) instead of taking it off at once, reversing T-401 (T-402) | **Decided: accepted.** It avoids taking gear off by accident on a phone. |
| 189 | Bundle format 4 adds item `card_slots`, `refinable` and the refine table (chance, material, failure; no drop rates). The rate limit covers the new intents, because it applies to every inbound message (T-402) | **Decided: accepted.** |
| 190 | `run-e2e.ps1` once kept waiting after the `replaced` client had passed and exited, until its server was stopped (T-402) | **→ T-221**, with the import and export hangs. |

## Addendum — T-221 (2026-11-17)
| # | Question (source) | Decision |
|---|---|---|
| 191 | Cause of the Windows `--import` and export-exit hangs: the editor's Android plugin polls `adb devices` (our debug preset is `runnable=true`), adb forks a server into the console wrapper's job, and headless Godot reads `shutdown_adb_on_exit` too late to stop it, so the wrapper waits forever after Godot exited 0. Minimal repro hung 4/4, 0/4 with `runnable=false`. Not related to row 141 (T-221) | **Decided: accepted** as the explanation. It's an engine bug; the workaround stays. |
| 192 | One helper, `Invoke-GodotStep` in `godot-path.ps1`, runs every headless step with a per-step limit, an output-complete check and a shorter exit wait, stops the process tree by id, and retries a bounded number of times. A wrapper held after Godot exited is judged by Godot's own exit code (T-221) | **Decided: accepted.** Limits: import 180 s ×3, map export 300 s ×3, Android export 900 s ×2 (600 s on CI), release export 600 s ×3 (×2 on CI). |
| 193 | Proof: import 29 clean + 1 wrapper held (recovered) of 30, release export 19 + 1 of 20, no retries needed, no failures (T-221) | **Decided: accepted.** |
| 194 | Row 190 was not reproduced (20/20 passes). Mechanism shown: a child started with redirected output inherits the script's output handle, so a server left running holds the caller. `run-e2e.ps1` now stops every process it started, and a client that printed its result gets 30 s to exit (T-221) | **Decided: accepted.** |
| 195 | CI Godot job timeouts raised to each script's worst case plus margin (job 200 min). The job has never run on GitHub (T-221) | **Decided: accepted.** First real run → T-005 or the first push. |
| 196 | Set `runnable=false` on the Android debug preset to stop adb polling at the source (T-221 follow-up) | **Open — user.** It disables one-click deploy from the editor; the helper already handles the hang. |
| 197 | Report the `shutdown_adb_on_exit` headless bug to Godot (T-221 follow-up) | **Open — user.** It's a public post. |

## Addendum — T-501 (2026-11-18)
| # | Question (source) | Decision |
|---|---|---|
| 198 | One pure effect runner in `Game.Shared/Effects` (`EffectRunner`, `StatusSet`, `EffectLibrary`), hosted on the server by combat so effect damage shares the HP, death and EXP-share path; `stats --duel` runs the same code. An effect that can't run where it stands is a load error; `IItemEffects` and `unsupported_effect` are gone (T-501) | **Decided: accepted.** |
| 199 | Stacking: below `max_stacks` add a stack and run `on_apply` again; at the cap only refresh; the end time never shortens; the newest applier gets tick credit. `on_tick` runs per stack per interval, the end tick included; tick damage always lands (hit result `status`) (T-501) | **Decided: accepted.** |
| 200 | Statuses end on death and on arriving at another map (each instance has its own clock); a same-map warp keeps them (T-501) | **Decided: accepted** for v1. |
| 201 | Area: targets sorted by distance then id, every child on one target before the next, line of sight from the center, a hostile area never hits a player in a safe zone. Summon cap = the effect's `count` per owner and monster (T-501) | **Decided: accepted.** |
| 202 | A player's save point is the respawn point of its map, for now (T-501) | **→ T-601/T-702:** a real save point set at an NPC and stored with the character. |
| 203 | Consumable effects apply one tick after use; leaving the map in that tick uses the item and loses the effect (T-501) | **Decided: accepted** as a rare edge case. |
| 204 | Monster casts are server events only; sending casts to clients (cast bars) needs `Net/**` beyond the T-501 approval (T-501) | **→ T-502**, with player cast bars. |
| 205 | A player's summon is an ordinary monster to everyone, hostile to its owner (T-501) | **→ T-502:** allied summons. |
| 206 | Wasp with Venom Sting: wanderer L5 win rate 92.6% → 87.05%, L7 98.2% → 97.9%; T-309 golden values 0.428 → 0.433 (L5), 0.676 → 0.643 (L6), inside the 40–70% target (T-501) | **Decided: accepted.** Further tuning → T-404. |
| 207 | Crowd peak with statuses 85 draw calls (all markers one call) (T-501) | **Decided: accepted** (row 164 limit 100). |
| 208 | A fresh worktree can hold Git LFS pointers instead of theme models until `git lfs pull`; ThemePack tests then fail. The client measure tool once threw "Cannot access a disposed object" after sampling (T-501) | **→ T-405** (worktree LFS note and the measure tool fix go with the equipment preload work). |

## Addendum — T-502 (2026-11-18)
| # | Question (source) | Decision |
|---|---|---|
| 209 | Where do player skills live? (T-502) | **Decided:** in combat (`CombatSystem.PlayerSkills.cs`): a cast reuses the auto-attack's chase machinery (walking into range, the movement events that cancel it) and ends in the same `SkillRequested` monster casts use, so every cast shares one resolution path. `learn_skill`, `change_job` and `use_skill` are combat's intents. |
| 210 | When is SP spent and the cooldown started? (T-502) | **Decided:** when the cast time is up (a 0 ms skill: at once), before resolution. If the resolution tick then refuses the skill (target left, out of range) the SP is gone and the cooldown runs, as for monster casts. A cancelled or interrupted cast costs nothing. |
| 211 | Job change: what carries over? (T-502) | **Decided:** base level/EXP, stats and stat points, **unspent skill points and all learned skills** carry over (parents' skills stay usable); job level resets to 1 and job EXP to 0; the old job's stat bonuses are swapped for the new job's at job level 1; the player's `DefinitionId` becomes the job id. A weapon the new job cannot use stays worn until the inventory is asked to change it. |
| 212 | Jobs with `quest_ids` / `requires_unlock` before quests exist (T-502) | **Decided:** refused with `quest_not_completed` / `not_unlocked`; no faked completion. The scripted run reaches the Spellbinder (base 5, job 5, no quest). **→ T-602** sets the completed quests and unlocks. |
| 213 | Allied summons (row 205) (T-502) | **Decided:** a summon of a player is on the players' side for every side check (`Entity.OnPlayerSide`). It never targets a player; it attacks the nearest wild monster within its `chase_range` of itself and of its owner, or whoever hit its owner or it; otherwise it follows its owner to within `ai.wander_radius`. No home, no leash, no EXP or loot for anyone. A player cannot auto-attack it. |
| 214 | Cooldowns across maps (T-502) | **Decided:** kept as time left (`SkillCooldowns.Suspend`/`Resume`) on the entity, so a warp neither resets nor stretches them. Not persisted (T-702 decides whether they should survive a logout). |
| 215 | Wire for skills (T-502) | **Decided** (Net/** approval 2026-11-18): `use_skill`, `learn_skill`, `change_job` (C→S); the owner's `skills` (after every snapshot, a learn, a job change, a level) and `cooldown`; a delta's `casts` and `cast_ends` for any caster in view (never costs or numbers); skill refusals as `rejected` with `skill`. No bundle change (skills and job trees were already in it). Protocol version stays 1. |
| 216 | Friendly skills on other players (T-502) | **Decided: not in v1:** a friendly entity skill is cast on the caster (the server accepts a friendly ally target; the client has no way to aim at one yet). **→ T-6xx** with party or player targeting. |
| 217 | Skill bar cooldown redraw (T-502) | **Decided:** the skill book's version moves every frame while any cooldown runs, so the bar redraws its text each frame during a cooldown (a few short strings); nothing moves while idle. |

## Addendum — T-405 and lead review of T-502 (2026-11-18)
| # | Question (source) | Decision |
|---|---|---|
| 218 | Rows 209–217 were written by the T-502 session (T-502) | **Accepted by the lead** on review. The skills message goes with full snapshots, learns, job changes and level-ups, never every tick. |
| 219 | A weapon the new job can't use stays worn after a job change (T-502) | **→ T-404:** decide (unequip on job change, or keep it worn but unusable for skills) with the balance pass. |
| 220 | Skill names wrap badly in a 56 dp skill slot, and the bar redraws text every frame while a cooldown runs (T-502) | **→ T-222:** fix with the HUD's look in the toon pass if it fits; otherwise a UI art task. |
| 221 | Theme checks (`ThemeCheck`) run inside `validate`: `attach` xor `skinned`, the attach bone in every body rig from `appearance.yaml`, weapon-type clip suffixes, item visual keys with no model file (a warning naming the fallback), and LFS pointer `.glb` files (an error saying `git lfs pull`) (T-405) | **Decided: accepted.** Not run at server boot, since the server never loads models. |
| 222 | Preload up to `gear_preload_limit` (16) worn and bag pieces, in the piece cache, now LRU-bounded by `model_cache_limit` (48). Model work on the equip frame went from 1.6–3.6 ms to 0 ms (T-405) | **Decided: accepted.** |
| 223 | Equipping still has one 60–105 ms frame: the body re-merge in `GearMesher.Dress` (T-405) | **→ T-222** if the toon material work touches `GearMesher`; otherwise **→ Backlog:** build the merge ahead of time or spread it over frames. |
| 224 | `themegen` reads each weapon's type from `content/items/*.yaml` (`--content` to override) to pick its placeholder shape (T-405) | **Decided: accepted.** Placeholder generation may read content; the game client still reads only the bundle. |
| 225 | `ModelStore.HasPiece` is true for a piece that failed to load, so a failed preload isn't retried (T-405) | **→ T-222** (same files). |

## Addendum — T-222 (2026-11-18)
| # | Question (source) | Decision |
|---|---|---|
| 226 | Toon look as theme data: `themes/default/render/` (toon light include, toon and outline shaders and materials, environment scene), found by a `"render"` tag on manifest entries (`mat.toon`, `mat.outline`, `scene.environment`); a theme without them falls back to the old look with one warning; `toon_shading` switches it off (T-222) | **Decided: accepted.** Follows ADR-003. |
| 227 | Outlines: inverted hull as a `material_overlay`, closed by an outline direction themegen writes into glTF `TANGENT`; on living characters, monsters, NPCs and ground items within `outline_distance_cells` = 10, at most `outline_max_count` = 6 nearest (T-222) | **Decided: accepted.** |
| 228 | Godot's Mobile renderer counter doesn't count the overlay pass. Measured crowd peak 97 at 37 on screen; if each outline is one more GPU draw, the worst case is 103 (over §9's 100) (T-222) | **→ T-803:** measure the real overlay cost on a phone; until then the cap of 6 stays, and lowering it to 3 is the fallback. |
| 229 | Placeholder triangles rose (crowd peak 12.9k → 57k, dressed 83k), under the 150k budget; one 1024² hard shadow map, 24-unit range (T-222) | **Decided: accepted.** Shadow cost goes on the T-803 checklist. |
| 230 | Body re-merge (`GearMesher`) runs on a worker and preloaded pieces are read ahead: worst equip frame 58.9 → 35 ms (closes row 223). Failed pieces retry up to `piece_load_attempts` = 3 (closes row 225). Skill slot text redraws only per whole second and shrinks to fit (closes row 220) (T-222) | **Decided: accepted.** |
| 231 | Godot's glTF importer ignores `COLOR_0`, so `GearMesher` and `ToonLook` apply vertex colours themselves; the job palette lives in `themes/default/palette.json`, read only by themegen (T-222) | **Decided: accepted.** |
| 232 | Leftovers: ARCHITECTURE §8 still says placeholders are coloured from the key; map scenes include the toon light by an absolute `res://themes/default/...` path; the `--measure` client sometimes stays in Reconnecting on a loaded machine (3 of 9); one run showed the HUD at 61 canvas draws (T-222) | **→ T-406** (new backlog row: client and theme leftovers after the toon pass). |

## Addendum — T-503 (2026-11-18)
| # | Question (source) | Decision |
|---|---|---|
| 233 | `raise_stat` intent and the own `stats` message (points, each stat's value and next cost, the panel's derived stats), posted after the snapshot, a raise, a reset, a level, a job change, a learned skill and gear changes. `Net/**` approved under the user's standing approval of 2026-11-18 (T-503) | **Decided: accepted** as within that approval. Derived stats changed by a status (buff or debuff) aren't re-posted yet; **→ T-406**. |
| 234 | `reset_stats` and `reset_skills` effects, allowed only on consumables (a service site comes with T-601); rules in `balance.yaml` `progression.reset`: free below base level 10 (item kept), refused in combat (`reset_in_combat`) and when dead (`reset_dead`), `nothing_to_reset`; the inventory checks them before anything is used up (T-503) | **Decided: accepted.** A reset costs the item until currency exists. |
| 235 | Reset items Realign Tonic (stats) and Blank Page (skills), one of each in `new_character.start_items`; no icons yet (T-503) | **Decided: accepted.** Icons → **T-406**; whether they also drop → **T-404**. |
| 236 | The Stats panel opens from a button in the skill panel header; the `-Run ui` layout check doesn't cover it yet (T-503) | **→ T-406:** add the stats panel to the layout and touch-target check. |
