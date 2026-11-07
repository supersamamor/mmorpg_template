# Architecture

## 1. Goal

A reusable MMORPG **template**: the engine is generic, the game is data.
Replacing `/content` and `/themes/<name>` should produce a different game with zero code changes.

The game ships as a **3D** title on **Android and iOS** phones (landscape). Desktop builds
(Windows/macOS/Linux) exist only for development and testing. Why 3D on a 2D grid, and why
Godot: see [ADR-001](decisions/ADR-001-3d-mobile.md).

## 2. Solution layout

```
/src
  Game.Shared      Pure domain: definition records, stat/modifier pipeline,
                   formula evaluator, effect model, combat & leveling math,
                   grid math (cells, directions, LOS), message contracts.
                   No I/O. Referenced by server, tools, client.
  Game.Content     YAML loading, registries, cross-reference validation,
                   JSON Schema generation, client bundle export. Hot-reload in dev.
  Game.Server      Host: tick loop, world/map instances, AOI, entity systems,
                   WebSocket gateway, persistence (PostgreSQL), event bus,
                   internal admin API (§12).
  Game.Admin       Internal web panel for live operations (§12). Staff only, never shipped.
  Game.Tools       CLI: `validate`, `schema`, `stats`, `export-client`.
/tests
  Game.Tests       Unit + golden-value tests (xUnit).
/client            Godot 4 .NET project (3D). Renders state, plays effects, sends intents.
  addons/map_authoring   Editor plugin: bakes + exports map logic grids.
/content           Game definitions (YAML) + map logic grids (exported JSON).
/themes/default    Models, animations, textures, map scenes, VFX, audio, UI, fonts
                   + asset-manifest.json.
/docs              This folder. Decisions in /docs/decisions (ADRs).
```

Dependency direction: `Shared ← Content ← Server/Tools/Admin`, `Shared ← Client`.
`Game.Admin` never references `Game.Server`; it talks to it over the admin API (§12).
`Game.Shared` must never reference anything else in the solution.

`Game.Shared` targets `net10.0;netstandard2.1` with C# 9. Godot's .NET runtime is older than
.NET 10, and iOS builds are AOT-compiled and trimmed, so Shared must also avoid reflection-heavy
code and runtime code generation. `Game.Content` is server/tools only — the client never parses YAML.

**Machine-enforced rules (CI).** `.github/workflows/ci.yml` runs on every push and pull request, on Windows and Linux, with the SDK
from `global.json`: restore, `dotnet build` with warnings as errors, `dotnet test`, `validate`, `schema` followed by
`git diff --exit-code schemas/` (stale schemas fail), and `export-client --check content/client-content.reference.json --allow-missing`
(fails when the client bundle differs from the committed reference; before a reference is committed it prints the current hash and passes).
Rule 8 is enforced by the compiler: `Game.Shared` uses `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `src/Game.Shared/BannedSymbols.txt`
(clock, `Random`, `Guid.NewGuid`, `Stopwatch`, `File` and `Directory` are errors) and the AOT/trim analyzers on its `net10.0` target.
`validate` also checks every YAML document against its generated `schemas/*.schema.json`; the JSON Schema library is referenced by
`Game.Tools` only. Formula-string properties carry `[Formula]`, and a test fails when `FormulaSources` and the markers disagree.

## 3. Runtime model

```
 Godot client (Android/iOS)       .NET server (authoritative, 10 Hz)
 ──────────────────────────       ──────────────────────────────────
 tap → Intent ──── wss ──────►    Gateway → session → intent queue
                                  Tick: drain intents → validate → systems
                                        (movement, AI, combat, status, regen,
                                         spawn, quest) → events → AOI diff
 3D render ◄── Snapshot/Delta ──  Outbound per-client deltas (nearby only)
```

- **Tick phases.** Each map instance ticks on its own (one thread per instance, 10 Hz, scheduled against its start
  time). Every tick, in this order:
  1. **Drain** the intents (and queued spawns/despawns/warp arrivals) that arrived before the tick began, in arrival
     order; route each intent to the one system that owns its type. Intents for unknown or dead entities are dropped
     and counted.
  2. **Systems** run in their fixed registered order (`ITickSystem.Tick(TickContext)`), each seeing its own intents.
     A system that throws is logged (instance, tick, system) and the tick goes on.
  3. **Structural changes** (entity add/remove, warp departures) are applied, never mid-system. A warp leaves the source
     here and joins the target instance in the target's next tick; an unwalkable target cell rejects it.
  4. **Events** raised this tick are dispatched on the instance's event bus in raise order. Events published by
     handlers wait for the next tick.
  5. **Outbound:** systems that implement `IOutboundPhase` (the AOI) see the tick's final state and send each observer
     what changed near it. Events published here wait for the next tick.
- **Intents in, state out.** Clients never send results.
- **3D presentation, 2D simulation.** The server simulates on a cell grid on the ground plane
  (§4). The client turns cells into 3D positions, terrain height, animation and effects.
- **Area of Interest** (`Game.Server/World/Aoi`): each map is split into square AOI buckets of `balance.yaml` `aoi.bucket_size`
  cells. An **observer** (an entity with a view sink, `Entity.View`) sees every entity whose bucket is within `aoi.view_radius`
  buckets (Chebyshev) of its own, itself included. In phase 5 the AOI compares every entity with what observers were last told
  (one pass, O(entities)) and moves it between buckets when it crosses a boundary; walking state comes from the movement events.
  An observer whose window of buckets had no change this tick costs a few array reads; otherwise its window is gathered, cut to the
  crowd cap and merged with the last visible set (O(window)). It gets a **snapshot** when it starts observing, arrives on a map (warps
  included) or sends `RequestSnapshot`, and otherwise at most one **delta** per tick, none when nothing in view changed. The sink
  (`IViewSink`, implemented by the gateway) is called on the tick thread: it copies or serializes and queues, never does I/O;
  one that throws is logged and detached.
- **Crowd cap** (T-215, T-217, `VisibleCap`): an observer sees at most `aoi.max_visible` entities of its window. Always visible, and counted
  toward the cap: itself, its current attack target (from `AttackStarted` until `AttackStopped`), any living entity attacking it
  (from its first `DamageDealt` until `AttackStopped` or `AggroLost`), and the nearest `aoi.cap_pinned_chasers` living monsters that have
  taken it as their target (from `AggroGained` until `AggroLost`), as long as they are in the window. Party members join this list
  when parties exist (a documented hook in `VisibleCap.AlwaysVisible`). Attackers are never cut, so more of them than the cap go over it; a
  chaser only stays pinned within the limit plus `aoi.cap_reserved_margin`, so a long train cannot bypass the cap. Past that limit a chaser
  ranks like any monster and, if it does land a hit from out of view, arrives in the same delta as the hit.
  Next, monsters, NPCs and ground items each get a **reservation** (`aoi.cap_reserved_monsters`, `_npcs`, `_items`), filled with the nearest of
  the kind; what is pinned of a kind counts toward it, and a kind with fewer entities leaves the rest of its share to the others. The
  remaining slots go nearest first, whatever the kind. Rank is Chebyshev distance, then entity id, except that a ground item another player
  has loot priority on ranks behind everything else, in its reservation and in the remaining slots, until the priority ends.
  **No flicker:** a new entity enters within its reservation, or at rank `max_visible - cap_hysteresis` or better overall (the cutoff).
  Once shown, it stays up to `aoi.cap_reserved_margin` ranks past its reservation, or until it ranks past `max_visible`, or until it leaves the
  window. So the cap is never passed by ranked entities, and an entity at either edge does not flicker, also when its reservation shrinks
  because something of its kind was just pinned. A window that has never held more than the cap is not cut at
  all; once capped, a view stays capped until its window is back at the cutoff. Anything cut is simply not in view: no changes for it,
  and the client cannot target it. Being cut is not visibly different from walking out of the window: it arrives in `disappeared`.
- **Tick timing and stats.** A tick's start is scheduled against the instance's start time, never against the previous tick, so a late
  wake-up does not push later ticks back; simulated time still comes from the tick count. On Windows the wait uses a high-resolution
  waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) up to 1 ms before the due time and spins for the rest, because an ordinary
  `WaitOne(ms)` wakes on the ~15.6 ms system timer period; other operating systems keep the ordinary wait (`--legacy-timer` forces it
  on Windows, to measure the difference). Each runner logs start lateness (p50 / p99 / max) every 600 ticks. Each `MapInstance` records
  its tick durations in a fixed 600-sample ring (`TickTimings`, no allocation per tick); `GetStats()` returns an immutable
  `InstanceStats` (p50 / p99 / max of the window, lifetime max, tick number, counters) that is safe to read from another thread. The
  admin panel (T-804/T-805) reads it; there is no endpoint yet.
- **One content boot.** `ContentBoot` (`Game.Content/Loading`) loads the content and runs every validation rule, including the theme
  manifest's asset keys and audio data. The server calls it at start and refuses to run on an error, printing the same lines
  `Game.Tools validate` prints; `validate` adds only the JSON Schema check (YAML documents and `maps/*.grid.json`).
- **Map instances:** each map runs in its own instance; warps move a character between instances.
- **Persistence:** player state saved on interval, on map change, and on logout. Content is never in the DB.
- **Mobile sessions are flaky** — the network layer assumes drops and app suspension (§10).

## 4. World space & movement

- **Cell = 1 world unit.** The logic grid lies on the X/Z plane (Godot is Y-up). Cell `(x, y)`
  maps to world `(x + 0.5, ground_height, y + 0.5)`.
- **8-direction movement.** A diagonal step is allowed only if both orthogonal neighbours are
  walkable (no corner cutting) and costs √2 in pathing and in time.
- **Pathfinding** (`Game.Shared/World`): A* over the 8 neighbours, visited in the order n, ne, e, se, s, sw, w, nw. Costs are integers
  (10 orthogonal, 14 diagonal) so ties never depend on floating point; among equal-`f` nodes the lower heuristic wins, then the lower
  cell index (`y * width + x`). Paths exclude the start and include the goal. Limits (max path length, max nodes expanded) are
  `balance.yaml` `movement`; a search that exceeds either returns "no path" with a reason.
- **Facing** is one of 8 directions (VOCABULARY). The client rotates the model smoothly toward it.
- **Speed:** derived stat `move_speed`, in cells per second.
- **Movement system** (`Game.Server/World/MovementSystem`): `MoveTo` and `StopMove` intents; the last intent per entity in a tick wins. The path is searched on the tick from the entity's current cell and a rejected `MoveTo` (reason term in `MoveRejected`) leaves a move in progress running. Each tick a walking entity advances by 100 ms at its `move_speed` (read from its stat sheet when the move starts and after every tick in which a cell was reached; an entity with no speed cannot move), in entity id order. **Entities do not block each other in v1**: they may share a cell. Reaching a cell that has a warp (only by arriving, never by spawning or standing there) stops a **player** and requests the warp; other kinds (monsters) walk over warp cells as over any walkable cell. Other systems see movement only through `Entity.Cell` / `Facing` and the events `MovementStarted`, `CellReached`, `MovementStopped`, `MoveRejected`.
- **Ranges and AoE** are in cells, Chebyshev distance. **Line of sight** is a grid line test
  over cells flagged `blocks_los`.
  It is a supercover walk between cell centres, symmetric, and the two end cells never block. A line that passes exactly through a
  cell corner is blocked only if *both* cells beside that corner block; grazing a single blocking corner is allowed.
- **Height is visual only.** The client places entities on the terrain by raycast. The server
  never uses height in v1. Multi-floor maps, bridges, jumping and flying are non-goals.

## 5. Content system

### 5.1 Registry
On startup, `Game.Content` loads every YAML file under `/content` into typed registries
(`Registry<ItemDef>`, `Registry<SkillDef>`, …) keyed by string id, then runs validation:

- schema validity (shape, required fields, enums)
- id uniqueness and namespace prefix matches type
- every referenced id exists (job → skills, quest → items/monsters/npcs, map → monsters/warps)
- every asset key exists in the active theme's manifest
- every formula parses and only uses known variables
- every map has a logic grid; spawns, warps and NPCs sit on walkable cells; warp targets exist
- every map grid was exported from the current version of its scene (§5.3)

Any failure prevents startup and is reported with file + path.

### 5.2 Definition types (initial)
`JobDef`, `SkillDef`, `ItemDef` (consumable | equip | card | etc), `MonsterDef`,
`StatusDef` (buffs/debuffs), `QuestDef`, `NpcDef`, `DialogueDef`, `DropTableDef`,
`MapDef` (metadata + grid reference), `ExpTableDef`, `ElementTableDef`.
Singleton root files: `FormulaSetDef` (`formulas.yaml`), `BalanceDef` (`balance.yaml`: stat/skill point rules, stat range, damage and chance clamps, movement and path search limits, AOI bucket size and view radius, new-character start, monster AI timings). `AppearanceDef` (`appearance.yaml`: character-creation options, cosmetic only, §7.5.1).

### 5.3 Maps
A map has three parts that share its id:

| Part | Where | Used by |
|------|-------|---------|
| Visual scene | Godot 3D scene in the theme, by asset key (`scene.map.starter_town`) | client |
| Logic grid | `content/maps/<name>.grid.json`, generated, committed | server, validator |
| Metadata | `content/maps/<name>.yaml` (`MapDef`: name, scene key, music key, grid file, optional `respawn` cell for players who die here) | all |

- **Authoring:** in the Godot editor. The `map_authoring` plugin bakes walkability from the
  scene's collision shapes (one downward ray per cell, slope limit), then the designer paints
  overrides. Spawns, warps, NPCs and zones are marker nodes with metadata that reference content
  ids (e.g. `monster: monster.slime`, `count: 20`, `respawn: 10`).
- **Export:** the plugin writes the grid file: size, cell flags (bit-packed), object lists
  `spawns`, `warps`, `npcs`, `zones`, and a hash of the source scene. `validate` fails when the
  scene changed after the last export.
- **Grid file format** (`MapGridFile`, schema `schemas/map-grid.schema.json`, `format_version` 1; the loader rejects other versions).
  Cell `(0, 0)` is the north-west corner, `+x` is east, `+y` is south, so cell `(x, y)` maps to world `(x + 0.5, y + 0.5)` as in §4.
  Anything outside the grid is not walkable and blocks line of sight.

  | Key | Type | Meaning |
  |-----|------|---------|
  | `format_version` | int | `1` |
  | `map_id` | string | must equal the `MapDef` that names the file |
  | `width`, `height` | int | 1 to 4096 |
  | `cells` | string | base64 of `width * height` bytes, row-major (`index = y * width + x`). Bit `i` is the `i`-th term of `CellFlags.All`: bit 0 `walkable`, bit 1 `blocks_los`; other bits must be 0 |
  | `source_scene_hash` | string | hash of the exported Godot scene; may be empty; stored, not yet checked (T-208) |
  | `spawns[]` | object | `monster_id`, `area {x, y, w, h}`, `count` (≥ 1), `respawn_seconds` (≥ 0) |
  | `warps[]` | object | `cell {x, y}`, `target_map_id` (may be this map), `target_cell {x, y}` |
  | `npcs[]` | object | `npc_id`, `cell {x, y}`, `facing` (a `Directions` term) |
  | `zones[]` | object | `kind` (a `ZoneKinds` term), `area {x, y, w, h}` |

  Runtime access is the immutable `MapGrid` (`Game.Shared/World`), exposed by `ContentDatabase.GetGrid(mapId)`.
- **Theme caveat:** a theme re-skins maps, it does not re-lay them out. A replacement scene must
  match the existing grid, or the grid must be re-exported (that is a content change).

### 5.4 Server content vs client content
The server loads everything. The client gets only what it needs to display: names,
descriptions, asset keys, skill targeting info, UI text. That is the **client bundle**.

- `dotnet run --project src/Game.Tools -- export-client [out.json] [--content dir] [--check file]` writes the bundle from the
  loaded registries (default `build/client-content.json`, gitignored; never commit a generated bundle) and prints its content hash.
  `--check <file>` writes nothing and exits 1 when the file differs from what the current content produces. The bundle is packed into the app.
- **Whitelist, not blacklist.** `Game.Shared/ClientContent` holds one plain record per definition type, and
  `Game.Content/ClientBundle/ClientBundleBuilder` copies each field by hand. A field added to a server definition reaches the client
  only when someone maps it there; a test fails if a client record gains a field without the table below changing.
- **Format:** `format_version` (now `3`; 2 added the sound keys, 3 the appearance options) is the first field. The client refuses a bundle with a version it does not know. Bump it
  on any change to the bundle's shape.
- **Canonical output:** compact JSON, UTF-8 without BOM, one final `\n`, fixed property order, lists ordered by id (dialogue nodes by key),
  integers and booleans only, non-ASCII (and `<>&'"+`) escaped as `\uXXXX`, null values left out. Two exports of the same content are byte-identical on any OS.
- **Content hash:** SHA-256, lowercase hex, over the bundle JSON **without** the `content_hash` property and without the final `\n`.
  The file is the same JSON with `"content_hash"` inserted right after `format_version`. The server computes the same hash with the same builder at
  boot and logs it. Only displayed data is in the bundle, so a change to drops, monster stats, formulas, balance, EXP or spawns does **not**
  change the hash (a balance hotfix needs no app update); a change to any displayed field does.
- **Login check** (§10): a client whose hash differs gets `bye update_required`. A missing hash is allowed in development (logged) and refused elsewhere.

| Definition | In the bundle | Never in the bundle |
|------------|---------------|---------------------|
| Items | id, name, description, kind, icon, ground visual, equip slots (card slots for cards), weapon type, worn visual, use and equip sound keys | prices, weight, stack size, consumable effects and cooldown, weapon attack, element, level and job limits, modifiers, refine |
| Skills | id, name, description, max level, passive, target range / ground / friendly, area shape (`square`) and radius, cast time **only if a plain number**, animation id, icon, vfx, sfx | SP cost, cooldown and every other formula, effects and their numbers, passive modifiers, weapon requirement |
| Jobs | id, name, description, parent job, usable weapon types, model, icon, skill tree (skill, max level, required skills) | stat bonuses, change requirements, EXP table id |
| Statuses | id, name, description, buff or debuff, icon, vfx | effects, tick interval, stacks, tags, `prevents_*` flags |
| Monsters | id, name, model, sound keys (aggro, hit, die) | level, race, size, element, stats, attack range, EXP, drop table links, AI |
| NPCs | id, name, title, dialogue id, model, portrait | — |
| Dialogue | id, start node, nodes (speaker, portrait, text, next, choices with text / next / quest id) | — |
| Quests | id, name, description, giver NPC, display text of each step | prerequisites, objectives and counts, rewards, repeatable flag |
| Maps | id, name, scene, music, ambience key, width, height, cell flags (`walkable`, `blocks_los`; base64, as in the grid file), warps (cell, target map, target cell), NPC placements, zones | `spawns`, `source_scene_hash` |
| Appearance | genders (body model, faces, hairstyles, defaults), skin tones and hair colours: ids, names, asset keys, colours | — (cosmetic only) |
| Elements, stats | element ids and stat ids, for labels | element multipliers, derived-stat formulas |
| — | — | drop tables and chances, `formulas.yaml`, `balance.yaml`, EXP tables |

The leak tests (`tests/Game.Tests/ClientBundle`) fail if a key names server-only data (`drop`, `chance`, `spawn`, `formula`, `ai`, `exp`, `respawn`, `hp`, `atk`, ...),
if a server-only value (formula, scene hash, `drop.*` / `exp.*` id) appears, or if server-only fields set to unique values change a byte of the output.

## 6. Core mechanics model

### 6.1 Stats & modifiers
`FinalStat = (Base + ΣFlat) × (1 + ΣPercent)` evaluated in priority order.
Modifier sources: base allocation, job bonus, equipment, cards, refine, statuses.
Each modifier carries its source so it can be removed cleanly (unequip, buff expiry).
Derived stats (ATK, MATK, DEF, HIT, FLEE, CRIT, ASPD, MaxHP, MaxSP, move speed) come from formulas.

### 6.2 Formulas
Expression strings in `content/formulas.yaml`, evaluated with a sandboxed expression
library. Variables are whitelisted (stats, levels, skill level, target stats). Compiled once at load.

### 6.3 Effects
Skills, consumables, cards and statuses are lists of **effects**. Effect types are the
only place gameplay behavior lives in code (see VOCABULARY.md). New skill = new YAML.
New *kind* of behavior = new effect type + tests.

### 6.4 Quests
State machine per quest: `prerequisites → steps[objective] → rewards`.
Objectives listen to the event bus (`MonsterKilled`, `ItemAcquired`, `NpcTalked`, …).
Quest state per character is persisted as `{questId, stepIndex, counters}`.

### 6.5 Progression
Base level + job level, separate EXP tables. Stat points per base level and skill points
per job level, stat point cost, the stat range and combat clamps come from `balance.yaml`
(`BalanceDef`); the max base level is the base EXP table length + 1. Job change requirements from `JobDef`.

### 6.6 Monster AI
Two systems in `Game.Server/World/Monsters`, after movement in the tick (`ServerPipeline`: movement, `monster_spawner`, `monster_ai`, `combat`, AOI).

- **Spawner.** On an instance's first tick every `spawns` entry of the grid gets `count` monsters on random walkable cells of its
  area (the instance's seeded random). A monster is a `Monster` entity with `DefinitionId` = its monster id and a stat sheet from the
  stats pipeline (primary stats as base, listed derived stats as overrides); its spawn cell is its **home** (`EntitySpawned`). Each entry
  has `count` slots, so it never holds more. On `EntityDied` (combat) the slot is freed and refilled
  `respawn_seconds` of simulated time after the tick of the death, anywhere in the area. Respawn timers always run. The dead monster stays
  in the world as a corpse (`Dead`, not targetable) for `death.corpse_ms` so clients can play its death animation, then the spawner
  despawns it; the respawn timer does not wait for the corpse, so a short `respawn_seconds` may put the new monster beside the old corpse.
- **State machine** (`AiStates`), per monster, in entity id order; numbers from `MonsterDef` (`ai.*`, `attack_range`, `aspd`) and
  `balance.yaml` `ai` (think, wander, re-path, return timeout):

  ```
  idle ──pause (wander_pause_min..max)──► wander ──arrived──► idle
   │  aggressive: nearest player within aggro_range with line of sight (every think_interval)   ┐
   │  or hit (DamageDealt)                                                                        ├─► chase  (AggroGained)
  wander ─────────── same ─────────────────────────────────────────────────────────────────────── ┘
  chase ──Chebyshev ≤ attack_range──► attack ──out of range──► chase      (re-path ≤ every repath_interval)
  attack: one AttackRequested per 1000 / aspd ms
  chase / attack ──target gone or dead · farther than chase_range from home · no path──► return (AggroLost)
  return ──home──► idle   (ignores players and hits; after return_timeout it is put on its home cell)
  ```
  A monster back on its home cell raises `MonsterReturned`; combat then resets its damage shares and, if `ai.heal_on_return` (default true), heals it to full.
- **The AI only decides.** It walks with `MoveTo` / `StopMove` intents (applied on the next tick) and follows the movement events; it asks
  combat to attack with the `AttackRequested` event (attacker, target, both cells, range, time). Combat (§6.7) re-checks and resolves the hit and
  publishes `DamageDealt` / `EntityDied`, which the AI and the spawner listen to. Nobody calls another system.
- **Dormancy.** An `idle` or `wander` monster that no player could be seeing (no player's AOI window, `aoi.view_radius` buckets of
  `aoi.bucket_size`, covers its bucket) does nothing. Players come from the instance's player list (`MapInstance.Players`), bucketed once per
  AI tick, so the cost is O(players) plus a few bucket reads per awake monster, never O(entities). A dormant map allocates nothing per tick.

### 6.7 Combat loop
One system, `Game.Server/World/Combat` (`combat`), after the monster AI. Numbers from `MonsterDef` and `balance.yaml` `combat`, `death`, `loot`.

- **Vitals.** Players and monsters with a stat sheet get `Entity.Vitals` (current HP/SP) when they enter a map, full at `max_hp`; they travel
  with the entity across warps. Every tick they are clamped to a changed sheet's maxima (a higher max does not refill). HP never goes below 0.
- **Regeneration.** Each tick every living player and monster regenerates `hp_regen` / `sp_regen` points per second of simulated time (the derived
  stats; fractions of a point are carried, so the total follows the rate whatever the tick length), never above max. Dealing or taking an
  attack (a miss included) pauses it for `regen.out_of_combat_delay_ms`. The dead never regenerate. A monster healed back to full by it
  forgets its damage shares, as when a hit leaves it at full HP.
- **Per tick:** intents (`AttackTarget`, `StopAttack`, `Respawn`) → vitals clamp and regeneration → the monster `AttackRequested` events raised last tick,
  in order, each re-checked (both alive on this map, a player target not in a `safe` zone, Chebyshev distance within the request's range,
  and for a ranged attack (range above 1) line of sight between the two cells; otherwise `AttackRefused` with a reason) → player auto-attacks in entity id order → ground item expiry.
- **Player auto-attack.** `AttackTarget` (monsters only; refused in a `safe` zone) keeps attacking until the target dies or leaves, the player
  sends `StopAttack`, a new move order (seen as a `MovementStarted` / `MovementStopped` combat did not cause), or no path leads into range.
  The player walks in with `MoveTo` (re-path at most every `combat.player_repath_interval_ms`) and attacks at its `aspd` within
  `combat.player_attack_range`; a ranged attack also needs line of sight, and without it the player walks closer as when out of range.
  Standing in a safe zone it neither hits nor can be hit.
- **Disconnected characters fight on.** A character in its disconnect grace period (§10) is an ordinary player entity: it stops
  walking but can still be attacked and killed (disconnecting is no escape), stays dead, and a resumed session sees it dead and sends
  `Respawn`. Combat has no special case for it.
- **Hits.** Every attack goes through `CombatResolver` with one reused `AttackContext` (monster element, race and size from `MonsterDef`;
  weapon inputs 0 until equipment). HP changes by the damage, clamped to 0..max; `DamageDealt` carries amount, `hit`/`crit`/`miss` and HP left.
- **Death** at 0 HP, in the same step: `Dead = true` (movement stops it, the router drops its intents, combat ignores it), `EntityDied`. A monster
  stays as a corpse for `death.corpse_ms`, is then despawned, and is respawned by the spawner. A player stays dead on its cell until `Respawn`, then comes back with
  `death.respawn_hp_percent` of max HP at the respawn point of the map it died on (the map's `respawn` cell, else the first warp arriving on it, else the `new_character` start); on death it loses
  `death.exp_penalty_percent` of its level's requirement, never more than its EXP (`ProgressionRules.LoseBaseExp`: no level loss).
- **EXP.** Per monster, the HP each player took since its last full-HP moment. On death the players still on the map share `base_exp` /
  `job_exp` in proportion (largest remainder; ties to the first hitter; the shares add up exactly) through `ProgressionRules`, which raise
  `ExpGained` and `PlayerProgressed` (`LevelUp` / `JobLevelUp`); the sheet takes the new levels and job bonuses. Progression is kept per
  character in an `IPlayerProgressionStore` shared by all instances (in memory until persistence).
- **Drops.** Each `drop_table_ids` table rolls every entry on its own (`chance`, then a quantity in `min..max`) with the instance random.
  Each drop is an `EntityKind.Item` entity on a random walkable cell within `loot.spread_radius` of the death cell (`ItemDropped`); for
  `loot.priority_ms` only the top damager may take it (`LootRules.MayPickUp`), and after `loot.lifetime_ms` it is despawned
  (`ItemExpired`). Clients see ground items as kind `item` (§10) with their quantity and whether they may take them now, never the owner or the timers.

## 7. Client

**Shape** (T-206). `client/Core` is the pure part, with no Godot namespace, and is compiled into `Game.Tests` too: settings, bundle reader, protocol parsing, the
connection state machine, the view model, interpolation, gestures, camera geometry and UI state (`client/Core/UiText.cs` holds the client's own words). `client/Scene`
is the thin Godot layer: its nodes read the model and draw it. Time comes into the core as a parameter. The protocol mapping is `Game.Server/Net/ProtocolJson.cs`, linked
as a source file: it has no server dependencies. Seams: `IMapView` (T-208's scenes replace `PlaceholderMap`), `IEntityVisualSource` (T-207's theme loader replaces
`PlaceholderEntities`), `IClientSocket` (the socket). All placeholder art sits behind the first two.

**Threads.** One socket thread per connection polls Godot's `WebSocketPeer` and parses frames; the main thread drains parsed messages once a frame and applies them.
The client bundle is packed into the project by the `PackClientBundle` MSBuild target (generated, gitignored) and read at start. A `format_version` it does not know
stops at a refusal screen, before any connection. The client is not in `Game.sln`.

**Interpolation.** Each entity keeps a short queue of timed steps from `cell` to `next`, drawn `render_delay_ticks / tick_rate` behind arrival (200 ms at 10 Hz). A step
after a step follows on with no gap, at its length over `speed`, so a diagonal takes √2 as long. A late delta makes the walker wait at the cell and pick up from the render
time, never jump. A step behind schedule is shortened, down to half its time, so a burst catches up. A new order mid-step turns from where the entity is drawn; a correction
glides, and snaps beyond `snap_distance_cells`. Nothing is predicted. A frame with nothing new allocates nothing, and only what changed touches a node.

### 7.1 Platforms & rendering
- Android 8.0+ (API 26), arm64-v8a. iOS 15+, arm64. Landscape only.
- Godot **Mobile** renderer (Vulkan / Metal). The **Compatibility** renderer (OpenGL ES 3) is
  the fallback for Android devices with poor Vulkan drivers — T-005 decides when it is used.
- Desktop builds use the same renderer and input code (mouse acts as touch).

### 7.2 Camera
Fixed-pitch 3/4 perspective that follows the player. Pinch (or scroll wheel) zooms within a
clamped range; optional rotation in 45° steps (not built yet). Camera numbers are client config, not content (`client/client_settings.json`).

Measured (T-206, pitch 50°, vertical field of view 30°, default distance 15; the engine's own camera rays agree with `CameraMath`), in cells from the player:

| Screen | Top edge, each side | Through the player, each side | Bottom edge, each side | Ahead | Behind |
|--------|--------------------:|------------------------------:|-----------------------:|------:|-------:|
| 1280×720 (16:9) | 9.2 | 7.1 | 5.8 | 6.8 | 4.3 |
| 1560×720 (19.5:9) | 11.2 | 8.7 | 7.1 | 6.8 | 4.3 |

The AOI always sends 12 cells (`view_radius` × `bucket_size`), so nothing pops in on screen. The zoom clamp keeps that true: at the farthest zoom (15.5) the top corners of a 19.5:9 screen
are 11.6 cells away, and a test fails if the settings or `balance.yaml` break it. A phone wider than 19.5:9 needs this checked again.

### 7.3 Input
One path for touch and mouse (`InputRouter`; a mouse arrives as touch index 0, so a click, a press-and-hold and the wheel behave as fingers do). The pure part is
`client/Core/Input/TapGestures.cs`; thresholds are client config, in dp (times the screen's units per dp), never canvas pixels.
- **Tap** (a press that lifts within the drag threshold, 12 dp, before the long-press time, 0.5 s). Ground → raycast to the plane → cell → `move`. A tap marker shows
  at once; the character moves when the server confirms (≤ 1 tick). `rejected` removes the marker quietly.
- **Tap a monster** → `attack`, with a target ring. It is picked on screen, at least half the platform's minimum touch target (48 dp Android, 44 pt iOS) on each side
  of its centre, or as wide as the shape looks if that is more. The client checks no range, path or walkability. Talking and picking up come with their systems
  (the other kinds count as ground for now).
- **Long-press** (still for 0.5 s) on any entity shows a small info panel with what the display bundle says (name, stack count, kind) and nothing else; on the ground it
  does nothing. It fires once, while the finger is down, and the lift that ends it is never a tap. Any later touch hides the panel.
- **Drag** (past the threshold) is neither tap nor long-press. **Pinch** (two fingers, past an 8 dp dead zone) and the scroll wheel zoom the camera inside its clamp;
  a pinch never taps, and neither does the finger left after it.
- **UI first.** A touch that begins on a button, field or panel belongs to the UI until it ends and never reaches the world. A full-screen panel blocks everything.
- **Hotbar** buttons call `HotbarModel.Press(slot)`. The seam for T-502 is `IHotbarContent` (what the slots hold, with a version counter so a slot redraws only
  when it changes) and `IHotbarActivator` (what an activated slot does: T-502 sends the `UseSkill` intent from there). Until they are bound every slot is an empty
  placeholder, a press gives visual feedback only, and the client sends nothing. Ground-target skills: pick the skill, then tap a cell (T-502).
- A virtual joystick is backlog.

### 7.4 UI
The HUD is code-built Godot Control nodes (`client/Scene/Hud.cs`) under one `CanvasLayer`; its rules are pure (`client/Core/Ui/`, tested).
- **Safe area.** Everything sits inside one root that is placed and sized to the device's safe area (notch, rounded corners, home indicator), so nothing needs its own
  inset. It is recalculated when the viewport resizes and when the OS reports a different safe area (turning the phone over). On a desktop the window is wholly
  safe; `debug_notch` (debug builds only, ignored in a release build) simulates a landscape notch and home indicator, with insets in dp.
- **Touch targets.** Minimum 48 dp (Android and desktop) / 44 pt (iOS), as rules (`TouchTargetRules`) using the screen's density: Android's dp is 1/160 inch and iOS's
  point 1/163 inch, and the stretch from window pixels to canvas units is undone. A desktop monitor never counts below the base density. Every button, slot and field is
  at least the minimum at every UI scale (a small scale never shrinks a target below it); a scripted check measures them.
- **UI scale.** A step setting (`ui_scale_min`/`max`/`step`/`default` in client config: 0.75 to 1.5 in steps of 0.25) the player cycles with the size button. The choice is
  stored in the engine's per-user storage (`user://preferences.json`, `UserPreferences`), never in `client_settings.json` and never on the server, and is clamped to the
  range when read. It scales sizes and the font together; it is applied by the HUD itself and does not use the window's content scale, so the camera and picking see no change.
- **Text is never baked into textures.** Every size is dp times the scale; labels wrap rather than cut. A scripted check lays the HUD out for six screen shapes (small
  16:9, 19.5:9, a large phone, an iPhone, two desktop windows) with and without the notch at every UI scale, filled with the longest words, and fails on anything outside
  the safe area, smaller than its text needs, or under the touch minimum.
- **Layout.** Top left: level and EXP, HP and SP with bars. Top centre: connection notice and the target frame (the target's name). Top right: connection state, the UI size
  button, log out. Bottom: the one-line flash, then the hotbar (slot count, rows and size are client config; a narrow screen shrinks slots to the minimum and then adds rows).
  The login, death, stop and refusal screens are one full-screen panel whose content stays inside the safe area.
- **Where the client's own words live (ADR-002 row 120).** In `client/Core/UiText.cs`: one static class of constants and small formatting functions in English, and nowhere
  else (a test fails on a literal assigned to a text property in `client/Scene`). Why: the refusal and update screens must read even when the bundle, theme or any data
  file is missing or broken, which rules out loading them; the pure core and its tests can use them; a typo or a missing word is a compile error; and the number of
  words is small. Names of game things never come from there; they come from the bundle by `display_key`. Translation later replaces the bodies of these members with
  lookups into a per-language table (the members are the keys, and their parameters the placeholders) without touching any caller. Localisation itself is not built.
- **Budgets (§9).** The HUD is about 40 nodes and adds about 24 draw calls (windowed measurement, T-211 report); the frame cap is 30 fps and the HUD does work only when
  a version counter changes, plus one size comparison per frame and one comparison while no finger is down.

### 7.5 Characters & equipment
Modular 3D characters. Each body type has one shared rig. Equipment is either:
- **skinned** to that rig (body armor, garment, footgear) — swapped as meshes, or
- **attached** to a named attach point (weapon, shield, headgear) — a bone attachment.

The item's visual asset key points to a manifest entry that says which (§8), so changing
equipment visuals is data only.

### 7.5.1 Character appearance (creation)
Chosen once at character creation; **cosmetic only** (never changes stats or rules).

| Choice | Options (content) | Rendering |
|--------|-------------------|-----------|
| Gender | `male`, `female` — one body type and rig each | Selects the body mesh and rig; equipment visuals exist per gender where the mesh differs |
| Skin tone | A palette of colours | Colour parameter on the body's single material (no extra material) |
| Face | Face variants per gender | Head texture or shape variant |
| Hairstyle | Styles per gender, by asset key | Attached to the `head` attach point |
| Hair colour | A palette of colours | Colour parameter on the hair material |

- **Data, not code:** options live in `content/appearance.yaml` (ids and asset keys). The validator checks every asset key and
  that each face and hairstyle fits its gender's rig. A face or hairstyle fits when its model has the body's rig, or has no rig and is attached at `head` in the asset manifest. The options are in the client bundle (§5.4). Headgear can declare that it hides the hair.
- **Server authoritative:** the server checks creation choices against `appearance.yaml` and stores them with the character.
  A modified client cannot pick an option that does not exist.
- **Network:** appearance is a few ids in the view record of a **snapshot / appeared** entry; deltas never repeat it.
- **Client:** the creation screen shows a rotating 3D preview of the choices.

### 7.6 Animation
Animation ids (VOCABULARY) are shared by every model on the same rig. Server events
(`attack`, `cast`, `hit`, `die`) map to animation ids. The client may delay a damage number to
the impact frame, but that is visual only — outcomes are already decided by the server.

### 7.7 Theme mounting
`themes/<theme>` is mounted into the Godot project at `res://themes/<theme>` (a directory junction in dev, made by `client/mount-themes.ps1`; copied and packed at export).
Downloading themes after install is a non-goal. The active theme is the client setting `theme` (default `default`). A theme may be **partial**: lookups search the active
theme, then `default`, so a theme contains only what it changes.

**Entity visuals** (T-207): `display_key` -> the bundle's model key (a job's `model`, a monster's or NPC's `model`, an item's `ground`) -> the theme's file -> a pooled scene
instance. The pure part (`client/Core/Theme`: manifest parser, `ThemeCatalog` fallback chain) is tested; `client/Scene/Theme` loads and shows. Model contract: faces -Z (north at
yaw 0), origin on the ground, meshes named `Body_LOD0` and optionally `Body_LOD1` (one material); a rigged model has `Armature/Skeleton3D`, and its clips live in the `anim.*`
library file on the same skeleton. Animation follows the view model: idle, walk, dead, and one-shot attack and hit when a delta's `hits` name the entity (delayed by the render
delay so they line up with movement). The nearest 8 animated characters draw at LOD0, the rest at LOD1; the nearest 40 animate every frame, the rest every 4th (`lod0_count`,
`full_rate_animated_count`, `reduced_animation_divisor`). Loading is threaded and the bundle's models start loading at start; at most `instantiate_per_frame` (6) instances are made
per frame, so a warp spreads over a few frames. Memory is bounded: `model_cache_limit` (48) loaded scenes (least recently used, never one in use) and `pool_idle_limit` (12) idle
instances per model.

### 7.8 Audio
Every sound is an **asset key** resolved by the theme (§8), so replacing a sound never touches code or content.

- **Content names sounds by key, all optional:** skills (`sfx_asset_key`), monsters (`sounds.aggro|hit|die`), items (`use_sfx`, `equip_sfx`),
  maps (`music_asset_key`, `ambience_asset_key`). Weapon swing and impact sounds are not content fields: weapon types are vocabulary, so
  they are theme defaults (`weapon.<weapon_type>.swing|impact`). A missing key is not an error: the client falls back (below).
- **Fallback order** (`AudioResolver` in `Game.Shared/Audio`, shared by the client and the tests): the key content names → the
  theme's `audio.defaults` entry for the category → silence. Events have no content key: `audio.events` → silence.
- **Fallbacks live in the theme:** the manifest's `audio.defaults` gives a category default (e.g. any monster's death sound,
  any weapon impact), so a theme can sound complete with a handful of files and get more specific over time.
- **Game events without content** (level up, quest complete, item pickup, UI tap, warp) map to keys in the manifest's
  `audio.events` table, keyed by `EventNames` terms. A theme re-sounds them by editing one table.
- **Variations and mix are theme data:** a sound entry may list several files (one picked at random), a volume, a pitch range,
  and a bus (`music`, `sfx`, `ui`, `ambient`). Bus volumes are player settings.
- **Playback:** music crossfades on map change; sound effects play from a pool with a voice cap and per-key cooldown so 30 hits in one
  tick don't stack; sounds far outside the camera are skipped. On mobile the client pauses audio in the background, follows the iOS
  silent switch for effects, and gives up audio focus on Android when another app plays.
- **Validated at load** (`validate`, the `audio` rule): every sound key used by content, defaults or events exists in the manifest with the right
  prefix (`sfx.` for monster, item, skill, default and event sounds; `music.`, `ambience.` for maps); categories, weapon types, events and buses
  are known terms; `volume_db` is within -60 to 12; `pitch` is `[min, max]` with 0 < min ≤ max; `files` is not empty and every file ends in `.ogg`.
  The manifest is theme data and is **not** in the client bundle; the bundle carries only the content's sound keys.

## 8. Theming (asset swap)

`themes/<theme>/asset-manifest.json` maps asset keys to files plus metadata:

```json
{
  "model.monster.slime":    { "file": "models/monsters/slime.glb", "rig": "blob", "anims": "anim.blob" },
  "anim.humanoid":          { "file": "anims/humanoid.glb", "rig": "humanoid",
                              "clips": { "idle": "Idle", "walk": "Walk", "attack": "Slash", "die": "Death" } },
  "model.equip.short_blade":{ "file": "models/weapons/short_blade.glb", "attach": "hand_r" },
  "model.equip.leather_vest":{ "file": "models/armor/leather_vest.glb", "skinned": "humanoid" },
  "scene.map.starter_town": { "file": "maps/starter_town.tscn" },
  "icon.item.short_blade":  { "file": "icons/short_blade.png" },
  "sfx.hit.light":          { "file": "audio/hit_light.ogg" }
}
```

**Sound entries** (`sfx.*`, `ambience.*`, `music.*`) take the plain form above or a richer one; every field except the file is optional:

```json
{
  "sfx.weapon.impact.slash": { "files": ["audio/slash_1.ogg", "audio/slash_2.ogg"],
                               "volume_db": -3, "pitch": [0.95, 1.05], "bus": "sfx", "loop": false },
  "ambience.map.starter_meadow": { "file": "audio/meadow_loop.ogg", "loop": true, "bus": "ambient" },
  "audio": {
    "defaults": { "monster.die": "sfx.monster.die.generic", "weapon.dagger.swing": "sfx.weapon.swing.light", "ui.tap": "sfx.ui.tap" },
    "events":   { "LevelUp": "sfx.event.level_up", "QuestCompleted": "sfx.event.quest_complete" }
  }
}
```

`file` or `files` (one is picked at random; not both), `volume_db` (decibels), `pitch` (`[min, max]` scale, picked at random per play), `bus`
(VOCABULARY "Audio buses"; default follows the key prefix), `loop`. `audio.defaults` maps a category (VOCABULARY "Audio default categories") and
`audio.events` maps an `EventNames` term to an `sfx.` key. The `audio` object is not an asset key. Replacing a sound means editing its entry or file.

**Fallback chain** (T-207, `ThemeCatalog`). A key is found when a theme lists it and its file exists. A model that is not found goes to the manifest's `fallbacks` entry for the
entity kind (`player`, `monster`, `npc`, `item`), then `fallbacks.missing` (a visible magenta marker), then a built-in marker: one logged warning per key, never a crash. An entity
whose content names no model uses the kind stand-in silently. `fallbacks` is a table like `audio`, not an asset key.

**Generated placeholder pack** (ADR-002 row 31): `themes/default` holds one real file per manifest key, written by `dotnet run --project tools/themegen` (`-- --check` verifies).
Models are low-poly rigs (five shared rigs) with LOD0 and LOD1 and one material coloured from the key; icons are 64x64 PNG, sounds one silent OGG, scenes empty `.tscn`.
`themes/test` is a partial theme (three models in other colours) used by tests.

Rig names are theme-internal; the validator checks that a model, its animation library and its
skinned equipment use the same rig. Active theme is a config setting.

| Asset | Format |
|-------|--------|
| Models, rigs, animations | glTF 2.0 binary (`.glb`), authored in Blender |
| Textures | PNG source; imported as VRAM-compressed (ASTC, ETC2 fallback) |
| Map scenes | Godot scenes (`.tscn`) |
| Audio | OGG Vorbis |
| Fonts | TTF / OTF |

Binary assets are stored with Git LFS.

## 9. Mobile budgets

These are requirements, not goals. **Reference devices:** a 2020-era mid-range Android phone
(4 GB RAM, Adreno 610 / Mali-G52 class) and an iPhone 11. T-005 confirms or adjusts these numbers.

| Budget | Value |
|--------|-------|
| Frame rate | 30 fps locked on reference devices; 60 fps option on high-end |
| Draw calls | ≤ 100 in a typical scene |
| Visible triangles | ≤ 150k |
| Character | LOD0 ≤ 5k triangles (the nearest 8 characters), LOD1 ≤ 2k triangles (everyone else); 1 material, ≤ 1024² texture. Worst case 8 × 5k + 32 × 2k = 104k, leaving ≥ 45k of the 150k for map, props and VFX (ADR-002) |
| Animated characters on screen | 40 at full rate; further ones at reduced animation rate |
| Real-time shadows | Player only by default; off on low settings |
| App memory | ≤ 1 GB |
| Initial install | ≤ 200 MB |
| Network | ≤ 10 KB/s average per client with 50 visible entities |
| Battery | No per-frame work while nothing changes; 30 fps cap in menus |
| Audio | ≤ 24 simultaneous voices; ≤ 48 MB decoded sound effects in memory; music and ambience streamed (proposed — T-803 confirms) |

## 10. Networking on mobile

- **TLS required:** `wss://` everywhere except local dev. Cleartext `ws://` is allowed only in
  debug builds, and **the client enforces that itself** (T-216): Godot's WebSocket uses native sockets, which Android's network-security
  config and iOS ATS do not police. A release build shows a refusal screen for a `ws://` or invalid server address and opens no socket;
  the allowance is set once at start-up from the engine's debug-build flag and cannot be changed by `client_settings.json` or the command line
  (`TransportPolicy`, MOBILE_BUILD §8.4). The iOS preset still states ATS explicitly; no Android network-security config is shipped (it would need the Gradle export and would not cover these sockets).
- **Disconnects are normal.** When a socket drops or the app is backgrounded, the server keeps
  the character in the world for a grace period (`grace-period-ms`, server config, default 60 s), then saves (T-702) and logs it out.
- **Resume** (T-210): the client reconnects with a resume token and receives a full snapshot, then deltas (lifecycle step 6 below).
- **Snapshot / delta shape** (T-204, T-307): a view record is `id, kind, display_key, cell, facing, next, speed, hp, dead` (`next` and
  `speed` only while walking, `hp` only for entities that fight, `dead` only when true; a ground item adds `quantity` and `mine`); a delta is
  `appeared` (records), `changed` (id + changed fields only), `disappeared` (ids), each ordered by entity id, plus `hits` and `vitals`.
  A walker produces one change per cell reached, not per tick; the client interpolates from `cell` to `next`.
- **Combat on the wire** (T-307). Others' HP is a fraction rounded to 0.01 (`hp`), sent only when that rounded value changes, so exact HP
  never leaks; the owner gets its exact `hp/max_hp/sp/max_sp` as `vitals` in every snapshot and in a delta when they changed. `hits` is one
  batch per tick per observer: every attack whose attacker or target is in view (after the crowd cap, §3), `[attacker, target, amount, result]`. A death is the `dead`
  flag (a dead monster stays in view until the spawner removes it). A ground item says only whether this client may pick it up now
  (`mine`): the priority holder and the loot timers stay on the server. The owner alone gets `exp` (after every snapshot and on every change),
  `level_up`, `you_died`, `respawned` and `attack_stopped`; a refused attack comes back as `rejected` with an "Attack reasons" term.
- **Latency:** the client renders ~2 ticks behind the newest snapshot and interpolates.
  It tolerates cellular jitter and does not predict combat.
- **Bandwidth:** JSON first. Switch to MessagePack if measured traffic exceeds the §9 budget. Measured (T-205): 50 loopback
  clients walking in each other's view receive 8.1 KB/s each on the wire (payload + WebSocket header). With combat (T-307: 20 of
  the 50 auto-attacking gel slimes nonstop, 30 walking; about 52 entities and 15 hits per second in every view) it is 6.9–7.2 KB/s, so
  full keys and JSON stay; no key shortening or distance rate limit on `hits` was needed.
  **Load test (T-214, `tools/loadtest`):** a real server process with bots on the same machine (Ryzen 7 7735U, 8 cores, Release
  build), half walking and half fighting on the start map, 60 s measured after a 30 s warm-up. Per-client payload: 5.2 KB/s at 50
  bots, 9.3 KB/s at 100, 15.3 KB/s at 200 (everyone in everyone's view, so 200 is over the §9 budget, which assumes 50 visible).
  **permessage-deflate** (`--net-compression`, off by default): at 50 bots it cut the wire from 5.16 to 0.79 KB/s per client (−85%),
  but server CPU went from 6.6 to 12.0 ms per 100 ms tick (+5.5 ms, mostly on the send threads, not the tick) and memory by about
  0.3 MB per connection. The rule was ≥ 30% for < 1 ms/tick, so it stays off; turn it on per server if bandwidth matters more than
  CPU. Shared serialized delta pieces were not built: the view sinks cost 1.5 ms/tick at 100 observers (rule: build above 2 ms);
  4.4 ms at 200.
  **Crowd cap (T-215, §3):** same load test and machine. At 200 bots the per-client payload (mean / p50 / max) went from 13.4 / 16.7 / 18.2
  KB/s to 7.2 / 9.1 / 9.8 and 7.3 / 9.2 / 9.95 (two runs) at `aoi.max_visible` 50. A cap of 60 gave 8.5 / 10.6 / 11.3 and 80 gave 10.7 / 12.6 / 13.4. Hits are why it does
  not fall in step with the view: a hit is sent when either side is in view, so crowds around shown monsters still arrive. At 50 bots, whose
  views hold 61–78 entities (bots, monsters, ground items) before the cap, it went from 5.4 to 4.4 KB/s. So the cap applies there too.
  **Reservations (T-217, §3):** same load test and machine, after the T-309 rebalance, 2–4 runs each. Max per-client KB/s and mean kills / deaths per
  run: at 200 bots, no cap 18.2–20.4 and 2062 / 189, distance only 10.1–10.2 and 1510 / 70, reservations 8.1–8.3 and 1935 / 160. At 50 bots, no cap
  6.1 and 665 / 37, distance only 4.9–5.2 and 437 / 50, reservations 4.9–5.3 and 650 / 29. The runs are noisy (at 50 bots with reservations, 387–797 kills
  and 11–46 deaths), but both kills and deaths move back toward no cap, and the payload fell.
- **Gateway** (`Game.Server/Net`, T-205): Kestrel WebSockets on one path (`/ws`). In development it listens on loopback with plain
  `ws://`; otherwise it listens on every interface with TLS only (a `.pfx` set in config; the server refuses to start without one).
  All ports, limits and timeouts are in `GatewayOptions`, which has defaults and can be overridden with `--net-<name>` or
  `GAME_NET_<NAME>`. They are server config, not content.
- **Messages** (contracts in `Game.Shared/Protocol`, JSON mapping in `Game.Server/Net/ProtocolJson`; the mapping is written by hand,
  with no reflection, so the client can compile the same file). Every message is one JSON text frame with a `type`. Cells are
  `[x,y]`, speeds are rounded to 0.01, and unknown fields are ignored. The protocol version is sent in `hello`.

  | type | direction | fields |
  |------|-----------|--------|
  | `hello` | C→S (first) | `version`, `content` (the hash of the client bundle, §5.4; optional in development), and exactly one of `name` (dev login stub; T-701 replaces it) and `resume` (a resume token) |
  | `move` | C→S | `cell` (a request; it may come back `rejected`) |
  | `stop` | C→S | — (stops walking **and** auto-attacking: a stop while standing still has no movement event, so combat could not see it) |
  | `attack` | C→S | `target` (an entity id the client sees; a request, it may come back `rejected`) |
  | `stop_attack` | C→S | — |
  | `respawn` | C→S | — (ignored while alive) |
  | `ping` | C→S | `seq` |
  | `logout` | C→S | — (leave now: `bye logout`, the character is removed on the next tick, no grace period) |
  | `welcome` | S→C | `version`, `id` (your entity), `map`, `tick_rate`, `content` (the server's content hash, §5.4), `token` (a new resume token) |
  | `snapshot` | S→C | `tick`, `map`, `id`, `entities` (view records), `vitals` (own `hp`, `max_hp`, `sp`, `max_sp`). Replaces the view; a new `map` means you changed maps |
  | `delta` | S→C | `tick`, `appeared` (records), `changed` (id + changed fields; `"next":null` = stopped), `disappeared` (ids), `hits` (`[attacker,target,amount,"hit"|"crit"|"miss"]`), `vitals` (when changed); empty ones left out |
  | `rejected` | S→C | `intent` (`move` or `attack`), `reason` (a `MoveRejected` or "Attack reasons" term), `cell` (move) or `target` (attack) |
  | `attack_stopped` | S→C (owner) | `target`, `reason` (an "Attack reasons" term) |
  | `exp` | S→C (owner) | `base_level`, `base_exp`, `base_next`, `job_level`, `job_exp`, `job_next` (`*_next` = EXP the level needs, 0 at the cap) |
  | `level_up` | S→C (owner) | `base_level` or `job_level` (one message per level reached) |
  | `you_died` | S→C (owner) | `killer` (entity id, if any); send `respawn` to come back |
  | `respawned` | S→C (owner) | `map`, `cell`, `hp`; a snapshot of the respawn point follows |
  | `pong` | S→C | `seq` |
  | `bye` | S→C (last) | `reason` (VOCABULARY "Bye reasons"), followed by the close frame |

  A view record is `{"id":7,"kind":"monster","display_key":"monster.x","cell":[3,4],"facing":"e","next":[4,4],"speed":3.51,"hp":0.37}`;
  a ground item is `{"id":9,"kind":"item","display_key":"item.x","cell":[4,5],"facing":"s","quantity":2,"mine":true}`.
- **Session lifecycle:**
  1. The client connects and must send `hello` within `hello-timeout-ms`.
  2. The server checks the protocol version, then the content hash (T-209): a `content` that differs from the server's ends in `bye update_required`; a missing one is accepted in development (logged as a warning) and refused elsewhere. Then it checks the dev login. The dev login is development only; any other environment refuses it. A `hello` with `resume`
     instead of `name` goes to step 6.
  3. The server queues the character with `GameWorld.Spawn(..., initialize)` at `balance.yaml` `new_character`. On the start
     instance's tick, the initializer sets its stats (from the stats pipeline), its display key (the job id) and its view sink, and
     queues `welcome`. The AOI then sends the first `snapshot` in phase 5 of that tick, and deltas follow.
  4. `move`/`stop`/`attack`/`stop_attack`/`respawn` are parsed and checked on the socket thread (shape; a cell inside the current map; an
     attack target the client currently sees, not itself, a monster) and queued as `MoveTo`/`StopMove`+`StopAttack`/`AttackTarget`/
     `StopAttack`/`Respawn` wherever the character is now. When the connection drops, the grace period stops both walking and attacking. Warps are followed through the world's entity directory. Each session has a token-bucket rate
     limit: messages over it are dropped and counted, and sustained flooding ends in `bye rate_limit`.
  5. Malformed JSON, an unknown type, an oversize message, a wrong version, a message before `hello`, or silence longer than
     `idle-timeout-ms` all end in `bye <reason>` and the close frame.
  6. **Grace period and resume** (T-210). Every `welcome` carries a **resume token** for the character.
     - **The connection ends without `logout`** (the client closed or vanished, idle `timeout`, `too_slow`, a protocol error such as
       `malformed` or `rate_limit`, `server_error`): the character is **not** removed. It is stopped (`StopMove`, `StopAttack`), its view is detached
       (`AttachView` with no sink), and it stays where it is, visible to others, for `grace-period-ms`. When that ends it leaves the
       world (T-702 saves it first). A buggy or hostile message must not delete a character, so protocol errors are no exception.
       A character whose session ended before its `welcome` was queued (no token yet) is removed at once.
     - **`logout`**: `bye logout`, the close frame, and the character leaves on its instance's next tick; its token stops working.
     - **Resume:** `hello` with `resume` (the protocol version and content hash are still checked first). If the token is live, the
       new session takes the character over wherever it is now (it may have warped since the drop): `welcome` (same `id`, current
       `map`, a **new** token), then a full `snapshot`, then deltas. A session still playing that character (the server had not
       noticed the drop yet) gets `bye replaced`. A character never has two sessions. An unknown, used or expired token gets
       `bye resume_failed`. Resume works in every environment; the token check sits in `ILoginService` (T-701 plugs in there).
     - **Abuse:** failed resumes are counted per remote address in a token bucket (`resume-failure-burst`,
       `resume-failures-per-minute`); an address that runs out gets `bye rate_limit` and may not resume for `resume-lockout-ms`.
     - **Shutdown:** every session gets `bye shutdown`; characters in their grace period leave too (T-702 saves them).
     - **The token:** 256 bits from the OS CSPRNG, base64url without padding (43 characters), bound to one character, **single use and
       rotated** (every successful resume returns a new one). The server keeps only its SHA-256 and
       compares in constant time; tokens never appear in logs (at most a 6-hex-digit fingerprint of the hash). Tokens live in server
       memory only: **a server restart invalidates every token** (durable sessions are T-701).
     - **Acknowledgement** (T-214): the `welcome` of a resume can be lost if the connection drops again before it arrives. So the token
       the resume used keeps working until the client's **first message on the new session** (any message: that is the implicit
       acknowledgement), then it stops. Resuming with it again before that rotates once more and kills the token issued in between. It
       never outlives the character's grace period: both tokens die when the character leaves.
     - **Capacity** (T-214): `max-sessions` counts characters, not sockets: every character the server holds, played or in its grace
       period, plus logins still joining. A new login on a full server gets `bye server_full` at its `hello`; nobody is evicted to make
       room. A resume is always let in, since it reuses its character's slot. Connections that have not said `hello` are refused with
       `server_full` only when more than `max-sessions` of them are open at once.
- **Client resume flow** (for T-206):
  1. Keep the token from the latest `welcome` **in memory only** (never on disk or in preferences); replace it on every `welcome`.
  2. On a socket drop, or when the app comes back to the foreground and the socket is gone, reconnect with backoff (e.g. 0.5 s,
     1 s, 2 s, 4 s, capped, with jitter) and send `hello` with `resume`, `version` and `content`. Stop trying once the grace period
     must be over.
  3. On `welcome`: keep the new token, keep the `id`, load `map` if it changed, then replace the view with the `snapshot` that follows.
  4. On `bye resume_failed` (or no token, e.g. the app was killed): go to the login screen. On `bye rate_limit` during a resume: same,
     and do not retry automatically.
  5. On `bye logout` or `bye replaced`: do not reconnect (`replaced` means the same player is on another connection).
     On `bye update_required`: go to the store; `shutdown`: show a message and retry later with a normal login.
  6. To leave on purpose, send `logout` and wait for `bye logout`.

  **What the client does** (`client/Core/Net/ConnectionMachine.cs`; the numbers are in `client_settings`):
  - Backoff 0.5, 1, 2, 4, 4... s, ±25%. It gives up at the last message heard + `grace_seconds` (60, a copy of the server's `grace-period-ms`, which the protocol does not send)
    + `grace_margin_seconds` (30, since the server may notice a silent client late). Those timers run on the wall clock, which keeps going while the app is suspended.
  - The other reasons (`timeout`, `too_slow`, `server_error`, `malformed`, `unknown_type`, `oversize`, `before_hello`, `duplicate_hello`, and `rate_limit` outside a resume) leave the character
    in its grace period, so they count as a drop. `bad_version` is treated as `update_required`. `login_refused` and, on a new login, `server_full` go to the login screen.
    During a resume `server_full` keeps trying.
  - It sends a `ping` right after every `welcome` (the acknowledgement of T-214), then every `ping_interval_seconds`. `silence_timeout_seconds` without any message counts as a drop.
    A server message it cannot read counts as a drop too, since the view can no longer be trusted, and an unknown `type` is ignored.
  - In the background it keeps the socket, sends nothing and starts no reconnect. On return it retries at once if the socket is gone, and otherwise a ping must be answered
    within `foreground_probe_seconds`, because a socket can look open after a suspension.
  - **`bye` also comes from the close frame.** The server writes the reason into the close frame as well, and that is needed: when `bye` and the close frame arrive in one poll,
    `WebSocketPeer` clears its packets before they can be read. Found by T-206's live run with a stale hash, where the `bye update_required` was lost.
- **Back-pressure:** the sink serializes on the tick thread into a bounded per-session queue, and the socket drains that queue. When
  the queue is full, queued views are dropped and later deltas are skipped. Once the queue has drained, the session asks for a fresh
  snapshot (`RequestSnapshot`). A client that stays behind for `slow-client-timeout-ms` gets `bye too_slow`.
  **`hits` are dropped while a client catches up** (ADR-002 #101): they ride in the deltas that are skipped, and the snapshot that
  ends the catch-up carries state, not history. That is accepted. Hits are cosmetic (numbers and swing animations), while HP (`hp`,
  `vitals`), deaths (`dead`, `you_died`) and drops (ground items) are state, which the snapshot restores in full.
- **Warps and removal** (T-214): a character removed while it is between maps (a drop or `logout` during a cross-map warp, or while it
  is still joining) stays removed. The world marks the removal, the target instance drops the arrival, and a removal that reaches a map
  the character has just left follows it. The gateway needs no workaround for this.

## 11. Security baseline
- Server validates every intent (range, cooldown, SP, line of sight, ownership).
- Rate-limit intents per session.
- Passwords hashed (Argon2/BCrypt). Session tokens, not credentials, on the socket.
- Item operations in DB transactions; item instances have unique ids (dupe auditing).
- The client is untrusted (CLAUDE.md rule 11); the client bundle holds display data only.
- No plaintext outside debug builds (§10): release clients refuse `ws://`. The Android APK carries only what runs on a device (arm64-v8a; the Mono static
  archives are stripped, MOBILE_BUILD §8.3), and CI exports it on every push (MOBILE_BUILD §9).
- Store rules: in-app account deletion (Apple and Google require it); Sign in with Apple if any
  other third-party login is offered; iOS privacy manifest and Play data-safety form kept current.

## 12. Admin panel & live operations
A live game needs staff tooling: player support, moderation, fixing mistakes, and watching
server health. Account deletion and data-export requests are also store requirements (§11).

### 12.1 Shape
```
Staff browser ──HTTPS + SSO──► Game.Admin (ASP.NET Core, Blazor Server)
                                   │  service credential, private network only
                                   ▼
                        Game.Server admin API (src/Game.Server/Admin)
                                   │  admin commands queued into the tick loop
                                   ▼
                        game systems + event bus + PostgreSQL (transactions)
```
- **`Game.Admin`** is a separate ASP.NET Core app (Blazor Server, so logic stays server-side).
  It references `Game.Shared` (admin command contracts) and `Game.Content` (to show item and
  monster names), never `Game.Server`. It has no game rules of its own.
- **The admin API** is a separate HTTP listener inside `Game.Server`, bound to a private
  interface/port and authenticated with a service credential. It is never reachable from the
  public internet or from the game client's `wss` endpoint.
- **Writes go through the game, not around it.** Every change (ban, item restore, currency fix)
  is an *admin command* that the server queues into the tick loop and applies like a player
  intent: validated, transactional (rule 10), and published on the event bus. The panel never
  writes game tables directly. Reads (player lookup, history) may use a read-only database role.

### 12.2 Access control
- Staff sign in through an OIDC identity provider with MFA. There are no local panel passwords.
- Roles: `support` (read, kick, mute, handle deletion/export requests), `gm` (+ ban, item and
  currency restore, announcements), `admin` (+ maintenance mode, role management).
- The server re-checks the role on every admin command. The panel's UI is not trusted for this.
- Reachable only from a VPN or IP allowlist in production; `localhost` in development.

### 12.3 Audit log
- Every admin command writes an append-only audit row in the same transaction: staff id, role,
  action, target, before/after values, a required free-text reason, time, request id.
- No role can edit or delete audit rows. The panel can search them.
- Items created by staff get new instance ids and `source = admin`, so dupe audits (§11) can tell
  them apart.

### 12.4 Features (v1)
- **Players:** search accounts and characters; view inventory, equipment, level, location,
  login history and item history.
- **Moderation:** kick, mute (timed), ban (timed or permanent, account level), with reasons.
  Bans are enforced by the server at login and on the live session.
- **Support fixes:** restore or remove items and currency; move a stuck character to its save
  point.
- **Server:** players online, map instances, tick duration (p50/p99), error rate, loaded
  content hash; server-wide announcements; maintenance mode (blocks new logins, then a timed
  kick).
- **Store compliance:** a queue for account deletion and data-export requests. In-app deletion
  still runs without staff; the queue covers requests that come in through support.

### 12.5 Not in the panel
- **Content editing.** Items, monsters, drop rates and formulas stay in YAML in git, reviewed
  and validated (`validate`) before they ship. The panel can show content, not change it.
- Running arbitrary SQL or scripts against the live database.
- Anything a player can see. The panel is internal; it is not the "web client" excluded in §13.

## 13. Non-goals for v1
Sharding/multi-server, client prediction beyond movement interpolation, PvP balance,
guild wars, cash shop / in-app purchases, multi-floor maps / jumping / flying,
downloading content or themes after install, web client, tablets-first layout, controller support.

**Planned for v2: guild war.** Not built in v1, but v1 must not block it:
- The combat resolver stays target-agnostic: a player can be a target exactly like a monster (no monster-only shortcuts).
- The `pvp` zone kind stays in the grid format; PvP rules switch on by zone, never by hardcoded map ids.
- Guild membership (T-904) is a server-side relation other systems can query through events, so a war system can add teams later.
- A load test (T-803 or later) measures crowds above the §9 budget of 50 visible characters, to size the v2 work (LOD, cheaper
  animation for distant characters, MessagePack).
