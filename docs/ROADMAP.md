# Roadmap

Each phase must be green (build + tests + content validate) before the next starts.
From Phase 2 on, "green" also means: runs on an Android and an iOS reference device
within the ARCHITECTURE §9 budgets.

## Phase 0 — Contracts ✅ in progress
- [x] Prerequisites script + Postgres via Docker
- [x] CLAUDE.md, ARCHITECTURE.md, VOCABULARY.md, ROADMAP.md
- [x] ADR-001: 3D on a 2D grid, Android + iOS
- [ ] Solution skeleton (`Game.Shared`, `Game.Content`, `Game.Server`, `Game.Tools`, `Game.Tests`)
- [ ] Mobile export spike: Godot .NET on Android + iOS devices (go / no-go for the engine)
- [ ] Definition records + JSON Schema generation
**Done when:** `dotnet build` passes, `Game.Tools schema` emits schemas, and ADR-001 has spike results.

## Phase 1 — Headless simulation
- Registry loader + cross-reference validator
- Stat/modifier pipeline, formula evaluator (formulas.yaml)
- Base/job leveling, stat & skill point allocation
- Combat resolution (hit/miss/crit/element/damage)
- Sample content: 3 jobs, 5 skills, 10 items, 5 monsters, 1 quest
**Done when:** golden tests pass; a broken reference fails `validate` with a clear message.

## Phase 2 — World slice (3D, mobile)
- Map authoring plugin: bake + export logic grids from Godot scenes
- Map grid loading (collision, spawns, warps)
- 10 Hz tick loop, A* pathing (8 directions), AOI grid
- WebSocket gateway (`wss`), login stub, enter map, move, reconnect/resume
- Client content bundle + content hash check
- Godot 3D client: map scene, camera, tap-to-move, interpolation, safe-area UI shell
**Done when:** an Android phone and an iPhone see each other walk on the same 3D map at 30 fps,
and one of them survives being backgrounded for 30 s.

## Phase 3 — Combat loop
- Monster spawns/respawn, data-driven AI states, auto-attack, death, EXP, drops
- Attack / hit / death animations and damage numbers
**Done when:** you can level from 1 to 10 killing monsters on a phone.

## Phase 4 — Items & equipment
- Inventory, equip slots, cards, refine, modular 3D equipment visuals, weight
**Done when:** equipping a weapon changes damage *and* the 3D model.

## Phase 5 — Jobs & skills
- Job tree + job change, skill tree, effect runner, statuses, cast/cooldown, skill VFX
**Done when:** Novice → first job via quest, using 3+ skills from the touch hotbar.

## Phase 6 — NPCs, dialogue, quests
- Dialogue graphs, quest state machine on event bus, quest log UI

## Phase 7 — Persistence & accounts
- Auth, character slots, save/load, transactional inventory
- Store compliance: in-app account deletion, Sign in with Apple (if social login), privacy disclosures

## Phase 8 — Tooling & release
- Content hot-reload, balance report CLI, simple content editor
- Mobile build pipeline (Android AAB, iOS via macOS CI → TestFlight / Play internal track)
- On-device performance capture against the §9 budgets

## Phase 9 — Social
- Chat, party (EXP share), trade, guilds
