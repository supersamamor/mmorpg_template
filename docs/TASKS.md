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
| T-004 | Sample content set (YAML) | Claude Code | Sonnet 5.5 | [T-004](../tasks/prompts/T-004.md) | T-003 | Ready | Original names only |
| T-005 | Mobile export spike (Godot .NET → Android + iOS) | Claude Code | Opus 5.5 | [T-005](../tasks/prompts/T-005.md) | T-001 | In Progress | Engine go/no-go (ADR-001). iOS half needs a Mac |
| T-006 | Update prereqs script for 3D mobile (Blender, Android SDK/JDK, Git LFS + `.gitattributes`) | Claude Code | Sonnet 5.5 | — | — | Backlog | Drop Tiled/Pixelorama |

## Phase 1 — Headless simulation

| ID | Task | Agent | Model | Prompt | Depends on | Status | Notes |
|----|------|-------|-------|--------|------------|--------|-------|
| T-101 | YAML loader + typed registries | Claude Code | Sonnet 5.5 | [T-101](../tasks/prompts/T-101.md) | T-004 | Ready | |
| T-102 | Cross-reference validator + `validate` CLI | Claude Code | Sonnet 5.5 | [T-102](../tasks/prompts/T-102.md) | T-101 | Ready | |
| T-103 | Formula evaluator (sandboxed, whitelisted vars) | Claude Code | Opus 5.5 | [T-103](../tasks/prompts/T-103.md) | T-101 | Ready | |
| T-104 | Stat / modifier pipeline | Claude Code | Opus 5.5 | [T-104](../tasks/prompts/T-104.md) | T-103 | Ready | |
| T-105 | Leveling, EXP tables, stat & skill points | Claude Code | Sonnet 5.5 | [T-105](../tasks/prompts/T-105.md) | T-104 | Ready | |
| T-106 | Combat resolution (hit/crit/element/damage) | Claude Code | Opus 5.5 | [T-106](../tasks/prompts/T-106.md) | T-104 | Ready | |
| T-107 | Golden-value tests + `stats` balance report | Claude Code | Sonnet 5.5 | [T-107](../tasks/prompts/T-107.md) | T-105, T-106 | Ready | |
| T-108 | Independent review of Phase 0–1 | Codex | default | [T-108](../tasks/prompts/T-108.md) | T-107 | Ready | Findings → new tasks |

## Backlog (prompts written when the previous phase is Done)

| ID | Phase | Task | Agent | Model | Status |
|----|-------|------|-------|-------|--------|
| T-201 | 2 | Map grid loader (`.grid.json` + MapDef: collision, spawns, warps) | Claude Code | Sonnet 5.5 | Backlog |
| T-202 | 2 | Tick loop + map instances | Claude Code | Opus 5.5 | Backlog |
| T-203 | 2 | A* pathfinding + 8-direction grid movement, LOS | Claude Code | Sonnet 5.5 | Backlog |
| T-204 | 2 | AOI grid + delta snapshots | Claude Code | Opus 5.5 | Backlog |
| T-205 | 2 | WebSocket gateway (`wss`) + message contracts | Claude Code | Opus 5.5 | Backlog |
| T-206 | 2 | Godot 3D client: map scene, camera, tap-to-move, interpolation | Claude Code | Sonnet 5.5 | Backlog |
| T-207 | 2 | Theme mounting + asset manifest loader (glTF models, rigs, anims) | Claude Code | Sonnet 5.5 | Backlog |
| T-208 | 2 | Map authoring plugin: bake walkability, markers, grid export | Claude Code | Opus 5.5 | Backlog |
| T-209 | 2 | Client content bundle (`export-client`) + content hash check | Claude Code | Sonnet 5.5 | Backlog |
| T-210 | 2 | Mobile session lifecycle: background, grace period, resume token | Claude Code | Opus 5.5 | Backlog |
| T-211 | 2 | Touch input + mobile UI shell (safe area, scaling, hotbar) | Claude Code | Sonnet 5.5 | Backlog |
| T-301 | 3 | Monster spawns, respawn, data-driven AI | Claude Code | Opus 5.5 | Backlog |
| T-302 | 3 | Auto-attack, death, EXP distribution, drops | Claude Code | Sonnet 5.5 | Backlog |
| T-303 | 3 | Combat presentation: animations, damage numbers, hit VFX | Claude Code | Sonnet 5.5 | Backlog |
| T-401 | 4 | Inventory, equip slots, weight | Claude Code | Sonnet 5.5 | Backlog |
| T-402 | 4 | Cards, refine | Claude Code | Sonnet 5.5 | Backlog |
| T-403 | 4 | Modular 3D equipment visuals (skinned meshes + attach points) | Claude Code | Sonnet 5.5 | Backlog |
| T-501 | 5 | Effect runner + statuses | Claude Code | Opus 5.5 | Backlog |
| T-502 | 5 | Skill trees, job change, cast/cooldown | Claude Code | Sonnet 5.5 | Backlog |
| T-601 | 6 | Dialogue graphs + NPC interaction | Claude Code | Sonnet 5.5 | Backlog |
| T-602 | 6 | Quest state machine on event bus | Claude Code | Opus 5.5 | Backlog |
| T-701 | 7 | Auth, accounts, character slots | Claude Code | Opus 5.5 | Backlog |
| T-702 | 7 | Persistence (EF Core + Postgres), transactional inventory | Claude Code | Opus 5.5 | Backlog |
| T-703 | 7 | Store compliance: account deletion, Sign in with Apple, privacy disclosures | Claude Code | Sonnet 5.5 | Backlog |
| T-801 | 8 | Content hot-reload + simple editor | Claude Code | Sonnet 5.5 | Backlog |
| T-802 | 8 | Mobile build pipeline (Android AAB, iOS on macOS CI, TestFlight / Play internal) | Claude Code | Sonnet 5.5 | Backlog |
| T-803 | 8 | On-device performance capture vs ARCHITECTURE §9 budgets | Claude Code | Sonnet 5.5 | Backlog |
| T-901 | 9 | Chat, party, trade, guilds | Claude Code | Opus 5.5 | Backlog |

## Workflow
1. Pick the lowest-ID `Ready` task whose dependencies are `Done`. Set it `In Progress`.
2. Paste the prompt file into the assigned agent with the assigned model.
3. Agent finishes → status `Review`. Check the acceptance criteria yourself.
4. Green build + tests + `validate` → `Done`, commit as `T-xxx: <task>`.
5. Anything discovered mid-task becomes a **new row**, not scope creep in the current one.
6. Changing an agent/model for a task? Update the row and note why.
