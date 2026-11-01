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
- **T-206:** 65, 66, 67, 87
- **T-207:** 31
- **T-305:** 73
- **T-401:** 15, 68, 85
- **T-403:** 72
- **T-501:** 14
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
