# Roadmap

Each phase must be green (build + tests + content validate) before the next starts.

## Phase 0 — Contracts ✅ in progress
- [x] Prerequisites script + Postgres via Docker
- [x] CLAUDE.md, ARCHITECTURE.md, VOCABULARY.md, ROADMAP.md
- [ ] Solution skeleton (`Game.Shared`, `Game.Content`, `Game.Server`, `Game.Tools`, `Game.Tests`)
- [ ] Definition records + JSON Schema generation
**Done when:** `dotnet build` passes and `Game.Tools schema` emits schemas.

## Phase 1 — Headless simulation
- Registry loader + cross-reference validator
- Stat/modifier pipeline, formula evaluator (formulas.yaml)
- Base/job leveling, stat & skill point allocation
- Combat resolution (hit/miss/crit/element/damage)
- Sample content: 3 jobs, 5 skills, 10 items, 5 monsters, 1 quest
**Done when:** golden tests pass; a broken reference fails `validate` with a clear message.

## Phase 2 — World slice
- Tiled map loading (collision, spawns, warps)
- 10 Hz tick loop, A* pathing, AOI grid
- WebSocket gateway, login stub, enter map, move
- Godot client: render map + entities, click-to-move, interpolation
**Done when:** two clients see each other walk on the same map.

## Phase 3 — Combat loop
- Monster spawns/respawn, data-driven AI states, auto-attack, death, EXP, drops
**Done when:** you can level from 1 to 10 killing monsters.

## Phase 4 — Items & equipment
- Inventory, equip slots, cards, refine, paper-doll sprites, weight
**Done when:** equipping a weapon changes damage *and* visuals.

## Phase 5 — Jobs & skills
- Job tree + job change, skill tree, effect runner, statuses, cast/cooldown
**Done when:** Novice → first job via quest, using 3+ skills.

## Phase 6 — NPCs, dialogue, quests
- Dialogue graphs, quest state machine on event bus, quest log UI

## Phase 7 — Persistence & accounts
- Auth, character slots, save/load, transactional inventory

## Phase 8 — Tooling
- Content hot-reload, balance report CLI, simple content editor

## Phase 9 — Social
- Chat, party (EXP share), trade, guilds
