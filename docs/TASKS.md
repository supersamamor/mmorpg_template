# Task Tracker

Single source of truth for all AI-assisted work. **No vibecoding without a task row here.**
Each task has a prompt file in `tasks/prompts/` — paste it into the assigned agent as-is.

## Status legend
| Status        | Meaning                                                        |
|---------------|----------------------------------------------------------------|
| `Backlog`     | Identified, prompt not written yet                             |
| `Ready`       | Prompt written — can start once every dependency is `Done`     |
| `In Progress` | An agent is working on it (one task per agent at a time)       |
| `Review`      | Agent finished; human/reviewer agent checking                  |
| `Done`        | Merged; build + tests + `validate` green                       |
| `Blocked`     | Waiting on a decision or another task (say why in Notes)       |

## Model selection guide
| Tier | Agent / Model | Use for |
|------|---------------|---------|
| **A — Architect** | Claude Code · Claude Opus 5.5 | Core abstractions, math-heavy systems, netcode, persistence, anything in CLAUDE.md "restricted areas" |
| **B — Builder** | Claude Code · Claude Sonnet 5.5 | Well-specified systems, CLI tools, loaders, tests, refactors |
| **C — Content** | Claude Code · Claude Haiku 5.5 | Bulk YAML content against existing schemas, boilerplate, docs touch-ups |
| **R — Reviewer** | Codex (default model) | Independent review of Tier A work — a different vendor isn't anchored on the author's reasoning |

Rule of thumb: if a bug in it would be silent (balance, concurrency, dupes) → Tier A + Tier R review.

## Phase 0 — Contracts

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-001 | Solution skeleton & build config | Claude Code | Sonnet 5.5 | [T-001](../tasks/prompts/T-001.md) | — | Review | |
| T-002 | Definition records (Game.Shared) | Claude Code | Opus 5.5 | [T-002](../tasks/prompts/T-002.md) | T-001 | Review | Core contract — review carefully |
| T-003 | JSON Schema generation (`schema` CLI) | Claude Code | Sonnet 5.5 | [T-003](../tasks/prompts/T-003.md) | T-002 | Review | |
| T-004 | Sample content set (YAML) | Claude Code | Sonnet 5.5 | [T-004](../tasks/prompts/T-004.md) | T-003 | Review | Original names only |
| T-005 | Mobile export spike (Godot .NET → Android + iOS) | Claude Code | Opus 5.5 | [T-005](../tasks/prompts/T-005.md) | T-001 | In Progress | Engine go/no-go (ADR-001). iOS half needs a Mac |
| T-006 | Update prereqs script for 3D mobile (Blender, Android SDK/JDK, Git LFS + `.gitattributes`) | Claude Code | Sonnet 5.5 | [T-006](../tasks/prompts/T-006.md) | — | Review | Drop Tiled/Pixelorama; edits the script only, never runs it |
| T-007 | Prereqs script: Android SDK licences + pinned packages via `sdkmanager` | Claude Code | Sonnet 5.5 | [T-007](../tasks/prompts/T-007.md) | T-006 | Review | ADR-002 #75 |

## Phase 1 — Headless simulation

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-101 | YAML loader + typed registries | Claude Code | Sonnet 5.5 | [T-101](../tasks/prompts/T-101.md) | T-004 | Review | |
| T-102 | Cross-reference validator + `validate` CLI | Claude Code | Sonnet 5.5 | [T-102](../tasks/prompts/T-102.md) | T-101 | Review | |
| T-103 | Formula evaluator (sandboxed, whitelisted vars) | Claude Code | Opus 5.5 | [T-103](../tasks/prompts/T-103.md) | T-101 | Review | |
| T-104 | Stat / modifier pipeline | Claude Code | Opus 5.5 | [T-104](../tasks/prompts/T-104.md) | T-103 | Review | |
| T-105 | Leveling, EXP tables, stat & skill points | Claude Code | Sonnet 5.5 | [T-105](../tasks/prompts/T-105.md) | T-104, T-109 | Review | |
| T-106 | Combat resolution (hit/crit/element/damage) | Claude Code | Opus 5.5 | [T-106](../tasks/prompts/T-106.md) | T-104, T-109 | Review | |
| T-107 | Golden-value tests + `stats` balance report | Claude Code | Sonnet 5.5 | [T-107](../tasks/prompts/T-107.md) | T-105, T-106 | Review | |
| T-108 | Independent review of Phase 0–1 | Codex | default | [T-108](../tasks/prompts/T-108.md) | T-107 | Ready | Findings → new tasks |
| T-109 | Balance constants (`BalanceDef` + `balance.yaml`) | Claude Code | Sonnet 5.5 | [T-109](../tasks/prompts/T-109.md) | T-103 | Review | Added: T-105/T-106 need it |
| T-110 | Tooling guardrails: CI workflow, banned-API + AOT analyzers on `Game.Shared`, schema-checked `validate`, `[Formula]` marker | Claude Code | Sonnet 5.5 | [T-110](../tasks/prompts/T-110.md) | T-102, T-103, T-107, T-209 | Review | ADR-002 #2–9, 17, 19, 23 |
| T-111 | Vocabulary + content hygiene: `zeny` → `currency` (original name), drop `MapEntered`, start-job and reach-cell checks, Twin Strike level 1 | Claude Code | Sonnet 5.5 | [T-111](../tasks/prompts/T-111.md) | T-302, T-110 | Review | Starts after T-302 and T-110 merge; ADR-002 #11–22, 29 |
| T-112 | Naming cleanup: `job.novice` → `job.wanderer`, neutral `job.swordsman` examples; robust `BannedSymbolsTests` | Claude Code | Sonnet 5.5 | [T-112](../tasks/prompts/T-112.md) | T-111 | Review | ADR-002 #103–105 |

## Phase 2 — World slice

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-201 | Map grid loader (`.grid.json` + MapDef: collision, spawns, warps) | Claude Code | Sonnet 5.5 | [T-201](../tasks/prompts/T-201.md) | T-101, T-102 | Review | Defines the grid format T-208 must export |
| T-202 | Tick loop + map instances + event bus | Claude Code | Opus 5.5 | [T-202](../tasks/prompts/T-202.md) | T-201 | Review | Tier R review recommended (concurrency) |
| T-203 | A* pathfinding + 8-direction grid movement, LOS | Claude Code | Sonnet 5.5 | [T-203](../tasks/prompts/T-203.md) | T-201 | Review | Pure `Game.Shared`; wired in by T-212 |
| T-212 | Movement system: `MoveTo` intent → path → grid mover in the tick loop | Claude Code | Sonnet 5.5 | [T-212](../tasks/prompts/T-212.md) | T-202, T-203 | Review | |
| T-204 | AOI grid + delta snapshots | Claude Code | Opus 5.5 | [T-204](../tasks/prompts/T-204.md) | T-202, T-212 | Review | Adds tick phase 5; Tier R review recommended |
| T-205 | WebSocket gateway (`wss`) + message contracts | Claude Code | Opus 5.5 | [T-205](../tasks/prompts/T-205.md) | T-204 | Review | Restricted `Net/**` — approved by owner; Tier R review required |
| T-209 | Client content bundle (`export-client`) + content hash check | Claude Code | Sonnet 5.5 | [T-209](../tasks/prompts/T-209.md) | T-205 | Review | `Net/**` only for the hash check — approved by owner |
| T-210 | Mobile session lifecycle: background, grace period, resume token | Claude Code | Opus 5.5 | [T-210](../tasks/prompts/T-210.md) | T-205, T-209 | Review | Restricted `Net/**` — approved by owner; Tier R review required |
| T-213 | Server boot + tick hygiene: shared content boot (with audio checks), Windows high-res tick timer, tick p50/p99 stats, AOI crowd + Ctrl+C tests | Claude Code | Sonnet 5.5 | [T-213](../tasks/prompts/T-213.md) | T-302, T-110 | Review | ADR-002 #47–52 |
| T-214 | Gateway hardening: resume-token ack, grace cap, load test, permessage-deflate if it pays | Claude Code | Opus 5.5 | [T-214](../tasks/prompts/T-214.md) | T-210, T-307 | Review | Restricted `Net/**` — **approved by owner 2026-11-02**; ADR-002 #55–59 |
| T-215 | AOI crowd cap: `aoi.max_visible` per observer, nearest first, always-visible target/attackers, hysteresis | Claude Code | Opus 5.5 | [T-215](../tasks/prompts/T-215.md) | T-204, T-214 | Review | ADR-002 #108 |
| T-206 | Godot 3D client: bundle, connect + resume, map from grid, camera, tap to move/attack, interpolation, minimum combat display | Claude Code | Opus 5.5 | [T-206](../tasks/prompts/T-206.md) | T-005, T-209, T-210, T-307 | Review | Started before the T-005 phone test (ADR-002 #112) |
| T-207 | Theme mounting + asset manifest loader, generated placeholder pack | Claude Code | Sonnet 5.5 | [T-207](../tasks/prompts/T-207.md) | T-206 | Review | ADR-002 #31 |
| T-216 | Client build hardening: refuse `ws://` outside debug, strip the APK, CI export job, live `bye` tests | Claude Code | Sonnet 5.5 | [T-216](../tasks/prompts/T-216.md) | T-206 | Review | ADR-002 #64, #122 |
| T-217 | AOI cap fairness: reserved slots by kind, own loot first, chasing monsters visible | Claude Code | Opus 5.5 | [T-217](../tasks/prompts/T-217.md) | T-215 | Review | ADR-002 #113, #121 |
| T-208 | Map authoring plugin: bake walkability, markers, grid export, staleness check, authored sample maps | Claude Code | Opus 5.5 | [T-208](../tasks/prompts/T-208.md) | T-201, T-207 | Review | |
| T-211 | Touch input + mobile UI shell: safe area, scaling, gestures, hotbar seam | Claude Code | Sonnet 5.5 | [T-211](../tasks/prompts/T-211.md) | T-206, T-207 | Review | ADR-002 #120 |
| T-218 | Client robustness: root-cause the headless exit crash with model preload; validator check for manifest `fallbacks` | Claude Code | Opus 5.5 | [T-218](../tasks/prompts/T-218.md) | T-207 | Review | ADR-002 #133, #135 |
| T-219 | Phase 2 leftovers: terrain-aware tap picking, theme-chain key checks, `MapGridFile` comment, `export-maps -Check` in CI | Claude Code | Sonnet 5.5 | [T-219](../tasks/prompts/T-219.md) | T-208, T-218 | Review | ADR-002 #142, #143, #145, #146 |
| T-220 | Phase 3 leftovers: crowd draw calls under 100, release-export guard in `run-e2e.ps1`, scripted hill tap, audio key chain | Claude Code | Opus 5.5 | [T-220](../tasks/prompts/T-220.md) | T-219, T-303, T-305 | Review | ADR-002 #148, #149, #155, #163 |
| T-221 | Godot tooling hangs: one bounded, retried helper for import and export, bounded harness waits, root cause of the import and export-exit hangs | Claude Code | Opus 5.5 | [T-221](../tasks/prompts/T-221.md) | T-218, T-220 | Review | ADR-002 #163, #176, #190 |
| T-222 | Storybook toon rendering (ADR-003): toon material, near-only outlines, lighting and haze, toon placeholders | Claude Code | Opus 5.5 | [T-222](../tasks/prompts/T-222.md) | T-403, T-405, T-501, T-502 | Review | Starts when T-502 and T-405 are merged (shared client and themegen files); ADR-003 |
| T-223 | Quiet test runs: `BannedSymbolsTests` starts `dotnet build` with `CreateNoWindow`, so no Windows Terminal window pops up on the desktop | Lead | Opus 5.5 | — | T-104 | Review | Lead fix on the user's request; ADR-002 #270 |
| T-224 | Theme kit for forks: theme contract (`docs/THEMES.md`), manifest variants (tint, scale), import tool from a mapping file, fork guide (`docs/FORKING.md`); tested on a generated synthetic pack | Claude Code | Opus 5.5 | [T-224](../tasks/prompts/T-224.md) | T-405, T-222, T-407 | Review | Template keeps the placeholder theme; no downloads |

## Phase 3 — Combat loop

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-301 | Monster spawns, respawn, data-driven AI | Claude Code | Opus 5.5 | [T-301](../tasks/prompts/T-301.md) | T-201, T-204, T-212 | Review | Server-side; started before Phase 2 client work because that waits on T-005 |
| T-304 | Audio data: sound keys, theme `audio.defaults` / `audio.events`, validation | Claude Code | Sonnet 5.5 | [T-304](../tasks/prompts/T-304.md) | T-102, T-209 | Review | Data only; client audio system is T-305 |
| T-302 | Auto-attack, death, EXP distribution, drops | Claude Code | Opus 5.5 | [T-302](../tasks/prompts/T-302.md) | T-105, T-106, T-301 | Review | Server world only; wire side (attack messages, HP and ground items in views) is a follow-up in `Net/**` |
| T-307 | Combat over the wire: attack/respawn messages, HP fraction + dead flag + ground items in views, hits batch, own EXP/HP | Claude Code | Opus 5.5 | [T-307](../tasks/prompts/T-307.md) | T-302, T-205, T-204 | Review | Restricted `Net/**` — **approved by owner 2026-11-02**; ADR-002 #60 |
| T-308 | Combat + AI polish: HP/SP regen, heal on return, corpse time, only players trigger warps | Claude Code | Sonnet 5.5 | [T-308](../tasks/prompts/T-308.md) | T-302 | Review | Starts after T-302 merges; ADR-002 #37–39 |
| T-303 | Combat presentation: attack and hit timing, damage numbers, hit and death effects, target HP bar, level-up flash | Claude Code | Sonnet 5.5 | [T-303](../tasks/prompts/T-303.md) | T-207, T-211, T-307 | Review | ADR-002 #139 |
| T-305 | Client audio: theme resolution and fallback, music crossfade, voice pool and cooldowns, buses and volume settings, mobile background / silent switch / audio focus | Claude Code | Sonnet 5.5 | [T-305](../tasks/prompts/T-305.md) | T-207, T-304 | Review | Proven by a debug trace until T-306 sounds exist |
| T-309 | Early-game balance: a new character beats a Gel Slime without spending points; pinned matchups + seeded duel sim | Claude Code | Sonnet 5.5 | [T-309](../tasks/prompts/T-309.md) | T-307, T-112 | Review | Found by the T-307 smoke test; ADR-002 #99 |

## Phase 4 — Items

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-401 | Inventory, equip slots, weight: pick up, use, equip, unequip, drop; own-inventory messages; HUD inventory panel | Claude Code | Opus 5.5 | [T-401](../tasks/prompts/T-401.md) | T-302, T-307, T-211, T-305 | Review | Touches `Net/**` for the item messages only (approved by the user 2026-11-15); ADR-002 #160 |
| T-402 | Cards and refining: socket cards, refine with data-driven chances and failure outcomes, bag panel | Claude Code | Sonnet 5.5 | [T-402](../tasks/prompts/T-402.md) | T-401 | Review | Touches `Net/**` for the two item messages only (approved by the user 2026-11-16) |
| T-403 | Worn equipment on 3D characters: attach points, skinned meshes, weapon-type clips | Claude Code | Sonnet 5.5 | [T-403](../tasks/prompts/T-403.md) | T-401, T-207 | Review | ADR-002 #164, #170, #173 |
| T-404 | Early balance with weapons, skills and stat points: Rock Beetle, Thornback Boar, Thistle Hollow, time to level 10; job-change weapon rule; reset item drops | Claude Code | Sonnet 5.5 | [T-404](../tasks/prompts/T-404.md) | T-309, T-401, T-501, T-502, T-503 | Review | ADR-002 #117, #175, #219, #235 |
| T-405 | Equipment manifest validation, preloading worn visuals, placeholder weapon shapes by type, LFS pointer message | Claude Code | Sonnet 5.5 | [T-405](../tasks/prompts/T-405.md) | T-403, T-501 | Review | ADR-002 #181, #182, #208 |
| T-406 | Load-proof tests (gateway reader first), stats re-post on status changes, reset item icons, stats and quest panels in the layout check, toon leftovers | Claude Code | Sonnet 5.5 | [T-406](../tasks/prompts/T-406.md) | T-222, T-503, T-602 | Review | Restricted `Net/**`: test seam and stats re-post only (standing approval 2026-11-18); ADR-002 #232–236, #252, #266, #267, #269 |
| T-407 | Thistle Hollow monsters: 2–3 original monsters with toon placeholders, spawns, tuned to 50–80% with `stats --duel` | Claude Code | Sonnet 5.5 | [T-407](../tasks/prompts/T-407.md) | T-404, T-222 | Review | ADR-002 #249 |
| T-408 | Small-phone HUD layout (left column vs the Skills hotbar), tracker cap test, the last load-sensitive gateway tests | Claude Code | Sonnet 5.5 | [T-408](../tasks/prompts/T-408.md) | T-406 | Review | ADR-002 #280, #282 |

## Phase 5 — Skills & statuses

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-501 | Effect runner + statuses: every effect type, status apply/stack/tick/expire, consumables, monster skills, statuses on the wire and client | Claude Code | Opus 5.5 | [T-501](../tasks/prompts/T-501.md) | T-401, T-402, T-303, T-309 | Review | Touches `Net/**` for status fields only (approved by the user 2026-11-17); ADR-002 #118, #171 |
| T-502 | Player skills: learning, skill trees, job change, casting, cooldowns, passives, allied summons, cast bars | Claude Code | Sonnet 5.5 | [T-502](../tasks/prompts/T-502.md) | T-501 | Review | Touches `Net/**` for skill, job and cast messages only (approved by the user 2026-11-18); ADR-002 #204, #205 |
| T-503 | Stat allocation (raise_stat, stats panel) and respec items: reset_stats / reset_skills effects, data-driven rules | Claude Code | Sonnet 5.5 | [T-503](../tasks/prompts/T-503.md) | T-501, T-502 | Review | Touches `Net/**` for raise_stat and own stat fields only (standing approval 2026-11-18) |

## Phase 6 — NPCs & quests

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-601 | NPCs in the world, dialogue graphs, conditions and actions, NPC services (save point, refine decision, resets) | Claude Code | Sonnet 5.5 | [T-601](../tasks/prompts/T-601.md) | T-501, T-502, T-503, T-222 | Review | Touches `Net/**` for talk and dialogue messages only (standing approval 2026-11-18); ADR-002 #187, #202, #234 |
| T-602 | Quest state machine on the event bus: objectives, turn-in, atomic rewards, currency balance, quest conditions in dialogue, quest log and tracker, the Warden path | Claude Code | Opus 5.5 | [T-602](../tasks/prompts/T-602.md) | T-601, T-404 | Review | Touches `Net/**` for quest messages and the currency field only (standing approval 2026-11-18); ADR-002 #211, #212, #253 |
| T-603 | Shop NPC service: buy and sell for currency in one inventory transaction, shops as data, merchant NPC, shop panel | Claude Code | Sonnet 5.5 | [T-603](../tasks/prompts/T-603.md) | T-601, T-602 | Review | Touches `Net/**` for shop intents and the shop message only (standing approval 2026-11-18); ADR-002 #251, #268 |
| T-604 | Quest autopilot, server: plays a player's ticked quests in sequence (travel, hunt, gather, walk, talk, turn in) through the same intents and rules as manual play | Claude Code | Opus 5.5 | [T-604](../tasks/prompts/T-604.md) | T-502, T-601, T-602, T-701 | Review | Restricted `Net/**`, limited scope approved by owner 2026-11-25 |

## Phase 7 — Persistence & accounts

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-701 | Accounts, login and character slots: EF Core + PostgreSQL base, account endpoints, sessions, sign-in and character-select screens | Claude Code | Opus 5.5 | [T-701](../tasks/prompts/T-701.md) | T-210, T-214, T-704 | Review | Restricted `Net/**` and `Persistence/**`, limited scope approved by owner 2026-11-25 |
| T-702 | Saving characters: whole state in one versioned transaction, loaded before entering the world, crash-safe items between characters | Claude Code | Opus 5.5 | [T-702](../tasks/prompts/T-702.md) | T-701, T-604 | Review | Restricted `Persistence/**` and `Net/**`, limited scope; owner's go-ahead 2026-11-25 |
| T-704 | Appearance definitions: `appearance.yaml` (gender male/female, skin, faces, hair) + validation | Claude Code | Sonnet 5.5 | [T-704](../tasks/prompts/T-704.md) | T-102, T-209 | Review | Data only, started early; server flow is T-705 |

## Backlog (prompts written when the previous phase is Done)

| ID | Phase | Task | Agent | Model | Status |
|----|-------|------|-------|-------|--------|
| T-306 | 3 | Default theme sound pack: music, ambience, combat, UI and event sounds (CC0 libraries, AI audio or a sound designer) | Human / sound designer | — | Backlog |
| T-225 | 2 | AI asset pipeline for forks (after T-224): scripts drive a 3D generation API (Meshy, Tripo or Rodin; key from an environment variable, never committed) from an approved brief, then cleanup in headless Blender, rigging or scripted motion, import through the T-224 tool, and an in-game review loop (scripted runs, screenshots at the game camera next to existing art, ThemeCheck and §9 budgets) that adjusts scale, tint, outline and pose for consistency, retrying within a set credit limit. Art is generated in a fork only | Claude Code | Opus 5.5 | Backlog |
| T-605 | 6 | Quest autopilot, client: an autopilot toggle per quest in the log (tick several, shown in run order), an on/off indicator and the current action on the HUD, the reason when it pauses, in the `-Run ui` check (needs T-604) | Claude Code | Sonnet 5.5 | Backlog |
| T-703 | 7 | Store compliance: account deletion, Sign in with Apple, privacy disclosures | Claude Code | Sonnet 5.5 | Backlog |
| T-705 | 7 | Appearance on the server: checked at creation, stored with the character, sent in snapshots (needs T-701, T-702, T-704) | Claude Code | Sonnet 5.5 | Backlog |
| T-706 | 7 | Character creation screen with rotating 3D preview (needs T-206, T-207, T-705) | Claude Code | Sonnet 5.5 | Backlog |
| T-707 | 7 | Appearance art: 2 genders, skin tones, faces, hairstyles in the chosen art style (art direction: ADR-003, storybook toon) | Human / artist | — | Backlog |
| T-708 | 7 | Cosmetic wardrobe: outfit slots that change only the look (never stats), outfits as data on the shared `humanoid` rig and T-403 attach points, owned outfits saved with the character, a wardrobe panel to pick what shows, others see it in snapshots; the template ships 1–2 placeholder outfits, forks add their own (needs T-702, T-705) | Claude Code | Opus 5.5 | Backlog |
| T-709 | 7 | Cosmetics store: Apple and Google in-app purchases with receipts checked on the server (never trusted from the client), grants outfits from T-708 atomically, refunds revoke, store compliance; the template ships the hook and a sandbox setup, no real products or keys (needs T-701, T-702, T-703, T-708; restricted `Net/**` and `Persistence/**`) | Claude Code | Opus 5.5 | Backlog |
| T-710 | 7 | Account leftovers: password-change endpoint and screen, end the live connection when its session is revoked, a failed `ui` run restores stored preferences, the Ctrl+C test (ADR-002 #289; restricted `Net/**`) | Claude Code | Sonnet 5.5 | Backlog |
| T-801 | 8 | Content hot-reload + simple editor | Claude Code | Sonnet 5.5 | Backlog |
| T-802 | 8 | Mobile build pipeline (Android AAB, iOS on macOS CI, TestFlight / Play internal) | Claude Code | Sonnet 5.5 | Backlog |
| T-803 | 8 | On-device performance capture vs ARCHITECTURE §9 budgets | Claude Code | Sonnet 5.5 | Backlog |
| T-804 | 8 | Admin API in server: admin commands via tick loop, role checks, audit log (ARCH §12) | Claude Code | Opus 5.5 | Backlog |
| T-805 | 8 | `Game.Admin` web panel: SSO + roles, player lookup, server status, audit search | Claude Code | Sonnet 5.5 | Backlog |
| T-806 | 8 | Moderation & support: kick/mute/ban, item/currency restore, announcements, maintenance, deletion/export queue | Claude Code | Sonnet 5.5 | Backlog |
| T-807 | 8 | Local Docker stack (server + Postgres, compose) and a Windows desktop client build for end-to-end play on one PC | Claude Code | Sonnet 5.5 | Backlog |
| T-901 | 9 | Chat: map, whisper, party and guild channels; rate limit + moderation hooks (mute from T-806) | Claude Code | Sonnet 5.5 | Backlog |
| T-902 | 9 | Party: invite/leave/leader, member HP list + minimap, EXP share formula in `formulas.yaml`, shared quest kill credit via event bus | Claude Code | Opus 5.5 | Backlog |
| T-903 | 9 | Player trade: two-step confirm, atomic transactional swap, dupe-safe (rule 10) | Claude Code | Opus 5.5 | Backlog |
| T-904 | 9 | Guild: create/join/leave, ranks + permissions, notice, guild chat, emblem chosen from a theme set (no uploads) | Claude Code | Opus 5.5 | Backlog |
| T-905 | 9 | Guild storage: shared items with rank permissions and audit log, transactional | Claude Code | Opus 5.5 | Backlog |

## Workflow
1. Pick the lowest-ID `Ready` task whose dependencies are `Done`. Set it `In Progress`.
2. Paste the prompt file into the assigned agent with the assigned model.
3. Agent finishes → status `Review`. Check the acceptance criteria yourself.
4. Green build + tests + `validate` → `Done`, commit as `T-xxx: <task>`.
5. Anything discovered mid-task becomes a **new row**, not scope creep in the current one.
6. Changing an agent/model for a task? Update the row and note why.
