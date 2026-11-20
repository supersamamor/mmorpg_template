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
  `Game.Tools validate` prints; `validate` adds only the JSON Schema check (YAML documents and `maps/*.grid.json`) and the map scene check (§5.3).
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

- **Authoring** (T-208; how-to: [MAP_AUTHORING.md](MAP_AUTHORING.md)): in the Godot editor. A map has two scene files in the theme. `maps/<name>.tscn` (the asset key's file) holds the look and the
  collision and ships in the app. `maps/<name>.authoring.tscn` instances it and adds the root's `map_*` metadata (size, slope limit, painted cells) and the marker nodes. It holds spawn data, so hard rule 11 keeps it
  out of the app: every export preset excludes it and the plugin (a test checks the presets).
- **Bake:** one downward ray per cell centre against the scene's collision shapes, in its own physics world, in a separate headless process (`ExportRunner`), so the editor's camera and selection cannot reach it. A cell
  is walkable when the ray hits a surface no steeper than `map_slope_limit_degrees` on a body not marked `map_walkable = false`; it blocks line of sight when that body has `map_blocks_los = true`. No hit is not walkable.
  The limit lives in the scene so the hash covers it; the plugin's `config.json` only gives new maps their first value. Paint (`map_overrides`, sorted lines like `3,4:block+los`) goes on top after every bake.
- **Export:** `client/export-maps.ps1 [-Map name] [-Check]` or the editor's Map dock (the same process). Markers are `Marker3D` nodes with `map_marker` (a `MapObjectTypes` term) and metadata that reference content ids
  (VOCABULARY "Map authoring metadata"). A marker on a blocked cell or naming an unknown id (checked against the display bundle) is an error shown in the dock, and nothing is written. Output is canonical: lists sorted, the
  same bytes as `GridFileJson.Write` (a test holds them together), so an unchanged scene exports to identical bytes.
- **Staleness:** `validate` fails with `grid.scene_stale` (file and `source_scene_hash`) when a grid's hash differs from its scene's. `MapSceneHash` reads the `.tscn` text with no Godot, and the exporter uses the same code. It covers the
  root, physics and unknown nodes, instanced scenes, `map_*` metadata, and the transforms of their ancestors, with resource references replaced by their content. It leaves out purely visual nodes, `uid`, `unique_id`, resource ids, section order, white space and line endings.
  A non-text theme file counts by its SHA-256 (an LFS pointer's `oid` is the same value, so CI needs no LFS); its own dependencies are not followed. A map scene must be `.tscn`. The check is in `validate`, not in `ContentBoot`: the server reads grids, never scenes.
- **Client** (`ThemeMapView`): shows the scene key's file through the fallback chain; a missing scene is `PlaceholderMap` with one warning. A few frames after a map loads, one ray per walkable cell fills `GroundHeights`, and entities read that, blended between cell centres, so nothing queries physics per frame.
- **Grid file format** (`MapGridFile`, schema `schemas/map-grid.schema.json`, `format_version` 1; the loader rejects other versions).
  Cell `(0, 0)` is the north-west corner, `+x` is east, `+y` is south, so cell `(x, y)` maps to world `(x + 0.5, y + 0.5)` as in §4.
  Anything outside the grid is not walkable and blocks line of sight.

  | Key | Type | Meaning |
  |-----|------|---------|
  | `format_version` | int | `1` |
  | `map_id` | string | must equal the `MapDef` that names the file |
  | `width`, `height` | int | 1 to 4096 |
  | `cells` | string | base64 of `width * height` bytes, row-major (`index = y * width + x`). Bit `i` is the `i`-th term of `CellFlags.All`: bit 0 `walkable`, bit 1 `blocks_los`; other bits must be 0 |
  | `source_scene_hash` | string | `MapSceneHash` of the authoring scene at export; `validate` compares it (the loader allows empty, `validate` does not) |
  | `spawns[]` | object | `monster_id`, `area {x, y, w, h}`, `count` (≥ 1), `respawn_seconds` (≥ 0) |
  | `warps[]` | object | `cell {x, y}`, `target_map_id` (may be this map), `target_cell {x, y}` |
  | `npcs[]` | object | `npc_id`, `cell {x, y}`, `facing` (a `Directions` term) |
  | `zones[]` | object | `kind` (a `ZoneKinds` term), `area {x, y, w, h}` |

  Runtime access is the immutable `MapGrid` (`Game.Shared/World`), exposed by `ContentDatabase.GetGrid(mapId)`.
- **Theme caveat:** a theme re-skins maps, it does not re-lay them out. A replacement scene must
  match the existing grid, or the grid must be re-exported (that is a content change). The same collision and markers give the same hash, so a pure re-skin stays valid.

### 5.4 Server content vs client content
The server loads everything. The client gets only what it needs to display: names,
descriptions, asset keys, skill targeting info, UI text. That is the **client bundle**.

- `dotnet run --project src/Game.Tools -- export-client [out.json] [--content dir] [--check file]` writes the bundle from the
  loaded registries (default `build/client-content.json`, gitignored; never commit a generated bundle) and prints its content hash.
  `--check <file>` writes nothing and exits 1 when the file differs from what the current content produces. The bundle is packed into the app.
- **Whitelist, not blacklist.** `Game.Shared/ClientContent` holds one plain record per definition type, and
  `Game.Content/ClientBundle/ClientBundleBuilder` copies each field by hand. A field added to a server definition reaches the client
  only when someone maps it there; a test fails if a client record gains a field without the table below changing.
- **Format:** `format_version` (now `6`; 2 added the sound keys, 3 the appearance options, 4 item `card_slots` / `refinable` and the `refine` steps, 5 dropped the dialogue graphs and an NPC's dialogue id: the server runs dialogue, T-601; 6 added `currency_name`, T-602) is the first field. The client refuses a bundle with a version it does not know. Bump it
  on any change to the bundle's shape.
- **Canonical output:** compact JSON, UTF-8 without BOM, one final `\n`, fixed property order, lists ordered by id,
  integers and booleans only, non-ASCII (and `<>&'"+`) escaped as `\uXXXX`, null values left out. Two exports of the same content are byte-identical on any OS.
- **Content hash:** SHA-256, lowercase hex, over the bundle JSON **without** the `content_hash` property and without the final `\n`.
  The file is the same JSON with `"content_hash"` inserted right after `format_version`. The server computes the same hash with the same builder at
  boot and logs it. Only displayed data is in the bundle, so a change to drops, monster stats, formulas, balance, EXP or spawns does **not**
  change the hash (a balance hotfix needs no app update); a change to any displayed field does.
- **Login check** (§10): a client whose hash differs gets `bye update_required`. A missing hash is allowed in development (logged) and refused elsewhere.

| Definition | In the bundle | Never in the bundle |
|------------|---------------|---------------------|
| Items | id, name, description, kind, icon, ground visual, equip slots (card slots for cards), weapon type, worn visual, use and equip sound keys, card slot count and `refinable` (T-402) | prices, weight, stack size, consumable effects and cooldown, weapon attack, element, level and job limits, modifiers (card modifiers too) |
| Refine (`refine.yaml`) | `max_level`, per level: `success_percent`, material item and quantity, failure outcome (all of it: it is what a player decides on) | — |
| Skills | id, name, description, max level, passive, target range / ground / friendly, area shape (`square`) and radius, cast time **only if a plain number**, animation id, icon, vfx, sfx | SP cost, cooldown and every other formula, effects and their numbers, passive modifiers, weapon requirement |
| Jobs | id, name, description, parent job, usable weapon types, model, icon, skill tree (skill, max level, required skills) | stat bonuses, change requirements, EXP table id |
| Statuses | id, name, description, buff or debuff, icon, vfx | effects, tick interval, stacks, tags, `prevents_*` flags |
| Monsters | id, name, model, sound keys (aggro, hit, die) | level, race, size, element, stats, attack range, EXP, drop table links, AI |
| NPCs | id, name, title, model, portrait | dialogue id |
| Dialogue | — | the whole graph: nodes, texts, conditions, actions and quest ids (the server runs it and sends one node at a time, T-601) |
| Quests | id, name, description, giver NPC, display text of each step | prerequisites, objectives and counts, rewards, repeatable flag |
| Maps | id, name, scene, music, ambience key, width, height, cell flags (`walkable`, `blocks_los`; base64, as in the grid file), warps (cell, target map, target cell), NPC placements, zones | `spawns`, `source_scene_hash` |
| Appearance | genders (body model, faces, hairstyles, defaults), skin tones and hair colours: ids, names, asset keys, colours | — (cosmetic only) |
| Elements, stats | element ids and stat ids, for labels | element multipliers, derived-stat formulas |
| Balance | `currency_name` (the bag shows the balance with it, T-602) | every other number of `balance.yaml` |
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

- **One runner** (T-501, `Game.Shared/Effects/EffectRunner`): pure and deterministic, so the server and `stats --duel` run the same code. It runs a compiled
  effect list (`EffectLibrary`: every skill, consumable and status compiled once at boot) for a caster, a primary target (an entity, a ground cell, or both)
  and a skill level, with the instance's random source and tick clock. It decides every number; an `IEffectWorld` applies the outcome and raises the
  events. On the server that world is combat's (§6.7), because `damage` must take the auto-attack's HP, death and EXP-share path.
- **Numbers** come from the effect's own formulas and `formulas.yaml`, with the caster as caster variables, the target as `t_` variables and `level`.
  `damage`: `CombatResolver` with the effect's multiplier and element (a status tick always lands and never crits: no rolls). `heal` / `restore_sp`: the
  amount, floored, never below 0, never above max. `apply_status`: `chance` (a roll only strictly between 0 and 1) then `duration_ms` (0 or less applies
  nothing). `remove_status`: every status with a listed id or tag. `modify_stat`: in a status's `on_apply`, a modifier with the source `status:<id>` (§6.1).
- **Order.** Effects run in list order on the primary target; one on a dead or vanished target is skipped (a killing `damage` ends the list there).
  `area` collects its targets first, then runs all its children on one before the next: living entities within `radius` (Chebyshev) of its center (the
  ground cell, else the target's cell, else the caster's), on the caster's side (`friendly`, the caster included) or the other side (players and monsters
  are the two sides in v1), in line of sight of the center, never a player in a safe zone for a hostile area; ordered by distance from the center, then
  entity id. The area's `friendly` must match its skill's `target.friendly` (load error otherwise).
- **Moves.** `knockback` pushes the target away from the caster (in an area, from its center) along the nearest of the 8 directions, one cell at a time,
  stopping before a cell that is not walkable or a diagonal that would cut a corner; `teleport` sends the caster to its save point (a player's: the respawn
  point of its map, until save points exist), a cell (of another map with `map_id`) or a random walkable cell of its map. Both are warps (phase 3);
  movement ends an interrupted walk with `warped`, and observers see a cell change (the client glides or snaps, §7). Forced moves ignore `prevents_move`.
- **Summon** asks the spawner (`SummonRequested`): the summons are ordinary monsters with `Entity.Owner` set, created on a random walkable cell within one
  cell of the target cell. **Cap:** an owner has at most the effect's `count` live summons of that monster (casting again tops them up). They are never
  respawned and are despawned after `duration_ms` (or as a corpse when killed). **Allied summons** (T-502): a summon of a player has `Entity.SummonedByPlayer`, which
  puts it on the players' side (`Entity.OnPlayerSide`) for every side check (skill targets, `area`, auto-attack). It is described in §6.6.
- **Statuses** (`StatusSet` on `Entity.Statuses`). **Stacking:** applying a status the bearer lacks starts it (1 stack); again below `max_stacks` adds a stack
  and runs `on_apply` once more (a stacked `modify_stat` counts per stack); at `max_stacks` it only refreshes. Either way its end becomes the later of the
  current end and now + the new duration, its tick schedule stays, and the newest applier becomes its source. `on_tick` runs once per stack every
  `tick_interval_ms` of simulated time, with ticks at or before the end included (8 s of 1 s poison = 8 ticks), then the status expires (`StatusExpired`),
  all on the instance's tick clock. Tick damage is credited to the applier (kill credit; the EXP share while it is on the map). Every status ends on death
  (`died`) and when its bearer arrives on another map (`warped`; each instance has its own tick clock and the applier stays behind); a warp within the map
  keeps them. Removal always takes the `status:<id>` modifiers off. `prevents_move`, `prevents_attack`, `prevents_skills` are read through
  `Entity.CannotMove` / `CannotAttack` / `CannotUseSkills` by movement (`cannot_move`), combat (`cannot_attack`; a running auto-attack pauses) and skill
  resolution (`cannot_use_skills`).
- **Load errors, not runtime refusals.** `EffectSupport` (validation code `effect.unsupported`) refuses an effect the runner cannot run where it stands
  (VOCABULARY "Effect types"); a status applying itself is refused, and nesting of statuses' `on_apply` stops at `EffectRunner.MaxDepth`.
- **Reset effects** (T-503). `reset_stats` and `reset_skills` run through the same runner as every effect, so a consumable (and, with T-601, an NPC service) uses them as data.
  They are load-validated: only directly in a consumable, never in a skill, a status or an `area`, and they act on the user. The world applies them through
  `IEffectWorld.ResetStats` / `ResetSkills` (combat hosts them) and announces `StatsReset` / `SkillsReset`. See §6.5 for what they change.
- **Events** (hard rule 7): `DamageDealt` (result `status` for ticks), `HpRestored`, `SpRestored`, `StatusApplied`, `StatusExpired`, `StatusRemoved`,
  `KnockedBack`, `Teleported`, `SummonRequested` → `MonsterSummoned`, `AreaApplied`; skills: `SkillCastStarted`, `SkillRequested`, `SkillUsed`, `SkillRefused`.

### 6.4 Quests
State machine per quest: `prerequisites → steps[objective] → rewards` (T-602). `QuestSystem` (`Game.Server/World/Quests`, after the inventory in the tick) drives it from the
event bus only (hard rule 7): it never calls combat, the inventory or dialogue. The rules are pure, `Game.Shared/Quests/QuestRules`.
- **State** per character, in the progression store (`ProgressionState.ActiveQuests`: `{questId, stepIndex, counters}`, beside `CompletedQuests` and `UnlockedJobs`): not started,
  active (step and one counter per objective of the step), completed. **In memory until T-702** (with the currency): a server restart forgets them.
- **Accepting.** A dialogue's `start_quest` raises `QuestStartRequested`; the quest starts when it is known, not active, not completed (unless `repeatable`), the base level, job and
  prerequisite quests are met and fewer than `balance.yaml` `quests.max_active` are active. Each refusal is a "Quest reasons" term the player gets as `rejected` (`intent`
  `dialogue_choice`, `quest`). **Abandoning** (`abandon_quest`) drops the progress and gives nothing back; the items a step would consume stay.
- **Objectives** count only while their step is current (VOCABULARY "Quest objective types"): `kill` on `MonsterKilled` for every player with **kill credit** (§6.7: everyone with a
  damage share who is still on the map), `collect` the bag's carried quantity (live), `talk` on `NpcTalked`, `reach` a cell and radius on a map (`CellReached`, arrival; latched),
  `level` the base level and `job` the current job (both already met when the step starts), `use_item` on `ItemUsed`, `equip` on `ItemEquipped` or worn at the step's start.
  A step completes when every objective is met.
- **Turning in.** A `talk` objective counts only when every other objective of its step is met, so talking to that NPC is the step's turn-in; a step with only a `talk` completes when
  the player talks to the NPC. The step's `collect` objectives with `consume: true` are taken then, and for the last step the `item` and `currency` rewards are given, in **one**
  inventory transaction (`QuestItemsRequested` → `InventoryRules.TryExchange` plus the currency → `QuestItemsResolved`; rule 10). Then the next step starts (and is checked at once)
  or the quest completes: `CompletedQuests`, `QuestCompleted`, and the other rewards go to the progression host, combat (`QuestRewardsRequested`: EXP through `ProgressionRules`,
  stat and skill points, `unlock_job` into `UnlockedJobs`, `status` through the effect runner), none of which can fail.
- **Full bag: the quest waits.** When the turn-in's items do not fit (`inventory_full`, `too_heavy`) nothing changed: the step stays met and *held* (the reason is in the owner's
  `quest` message) and is tried again whenever the player's bag changes or it arrives on a map. Nothing is dropped on the ground, so no reward can be lost or taken by someone else.
- **Dialogue follows the log.** The quest conditions `quest_state` (`not_started`, `available`, `active`, `completed`) and `quest_step` are on T-601's seam; the guide's and the trainer's
  graphs use them to offer, track and thank. A turn-in by talking lands a tick or two after the node was shown, so the dialogue system shows the open node again when a quest
  event (or a level or job) changes which of its choices hold.
- **Markers** (`QuestRules.Markers`, sent in `quest_log`): `offer` over the giver of every quest that is `available`, `turn_in` over the NPC of an unmet `talk` whose other objectives
  are met. The quest system announces a change (`QuestMarkersChanged`) and the gateway sends the whole log again.
- **Load checks** (`QuestRule`): steps and objectives exist, counts, levels, radii and reward amounts are in range, prerequisites do not loop; the giver is placed on a map and its
  dialogue has a reachable `start_quest` for the quest (and no other NPC's does); every `talk` NPC is placed and has a dialogue. Ids are `ReferenceRule`'s, reach cells `MapGridRule`'s.
- **The Warden path** (rows 211, 212): Captain Brannoc offers the Warden's Trial to a Wanderer of base level 5; base level 10, three Sting Wasps, then reporting to him completes it
  and unlocks the Warden; `change_job` is then accepted (§6.5); the Warden's Oath (offered once the trial is done) completes when the character is a Warden.
- **Warp edge case:** a turn-in waits for the inventory of the map it was asked on; a player who warps in that tick has it asked again on arrival (open question in ADR-002).
Dialogue graphs, NPCs and their services are T-601; quests are T-602. The sections below are the dialogue half of this chapter.

#### 6.4.1 NPCs and dialogue (T-601)
`DialogueSystem` (`Game.Server/World/Dialogue`) owns the NPC entities and the dialogues players have with them. It runs after the monster AI and before combat.
- **NPC entities.** On an instance's first tick every `npcs` placement of its map grid becomes an `EntityKind.Npc` entity on its cell and facing, `DefinitionId` the NPC id. A placement of an
  NPC the content does not have throws at that first tick (the validator reports it earlier: `ref.missing`). An NPC has no stats and no vitals: it never moves, is not a combatant (an attack
  is refused `invalid_target`, a skill `target_gone`, areas skip it) and the AOI treats it like any entity (view radius and the crowd cap's `cap_reserved_npcs` apply).
  **Blocking:** an NPC does **not** block its cell. Entities do not block each other in v1 (`MovementSystem`), the pathfinder works on the immutable grid, and a tap on an NPC is a `talk`
  that stops the player beside it; making NPC cells unwalkable per instance is a later change if players standing on NPCs turns out to matter.
- **Talking.** `talk` (`target`: an NPC entity in view; the gateway answers `target_gone` / `invalid_target` itself, like `attack`) is refused with a "Dialogue reasons" term when the player is dead,
  the target is gone or not an NPC, or the NPC has no dialogue. From beyond `dialogue.talk_range` (Chebyshev, `balance.yaml`) the player is walked in with a `MoveTo` like an auto-attack or a pick-up
  (another order cancels it, `unreachable` when there is no way); then the dialogue starts and `NpcTalked` is raised. One dialogue per player: a new talk ends the old one (`replaced`). It ends
  (`dialogue_ended`) when the player walks beyond the range, warps, dies or leaves the world, or the NPC is gone.
- **The graph.** The server keeps the player's node and sends it (`dialogue`: the speaker's NPC id, an optional portrait key, the text and the *shown* choices with their original indices).
  `dialogue_choice` (`index`) moves on; the client never decides where a choice leads. A choice is *shown* when every one of its `conditions` holds, and is checked again when picked
  (`invalid_choice`). A node without shown choices is a "continue": index 0 follows its `next`, and a null `next` ends the dialogue (`finished`) after the player has read the node.
  The client gets no graph, condition or action in its display bundle (format 5): only the NPCs' names, titles and portraits.
- **Conditions** (`Game.Shared/Dialogue/DialogueRules`, pure over `IDialoguePlayer`): `base_level` / `job_level` (`min`, `max`), `job_is`, `job_is_not`, `has_item` (`item_id`, `quantity`), `has_status`,
  and since T-602 `quest_state` (`quest_id`, `state`), `quest_step` (`quest_id`, `step` from 1) and `out_of_combat` (row 253: no hit dealt or taken within `regen.out_of_combat_delay_ms`).
  **The seam:** a condition is a record in `Discriminators.DialogueConditions`, a term in `DialogueConditionTypes`, a member on `IDialoguePlayer` and a case in `DialogueRules.Matches`. An unknown condition type hides its choice.
- **Actions**, in order when a choice is picked: `start_quest` raises `QuestStartRequested` (the quest system's, T-602; it replaces the old `start_quest_id`), `set_save_point` (a cell of the NPC's map),
  `run_effects` (an effect list, `EffectSite.Service`: the player is caster and target, resets allowed), `take_item` / `give_item`, `open_service`. Systems talk by events (rule 7):
  - **Items.** A choice's `take_item` / `give_item` actions are one transaction: the dialogue system publishes `DialogueItemsRequested`, the inventory applies all takes then all gives
    (`InventoryRules.TryExchange`, all or nothing, rule 10) and answers `DialogueItemsResolved`. Meanwhile the dialogue is `busy`. On a refusal (`missing_item`, `inventory_full`, `too_heavy`) nothing
    changed and the player stays at the node (`rejected` on the wire); on success the other actions run and the dialogue moves on. The inventory change goes out as `inventory_changed` (`exchanged`).
  - **Effects.** `DialogueEffectsRequested` is run by combat's effect runner on its next tick, like a consumable's effects (so they apply one tick later; row 203's edge case).
  - **Save point.** `ISavePointStore` (in memory until T-702, shared by every instance) holds a character's save point. **The rule:** `teleport` with `to_save_point` and the respawn after death both go
    to it; a character without one uses the respawn point of the map it is on (`CombatContent.RespawnPointFor`). It is forgotten when the character leaves the world.
  - **Services.** `open_service` sets `Entity.OpenService` (and, for `shop`, `Entity.OpenShopId` and `Entity.OpenServiceNpc`) and sends `service_opened`; it stays open until the player walks beyond the talk range, warps, dies, closes or talks to someone else (a
    finished graph leaves it open). **Refine:** with `dialogue.refine_needs_service` (on in `balance.yaml`) the `refine` intent is refused `service_required` unless a `refine` service is open;
    the starter meadow's smith opens it. `insert_card` stays free anywhere. **Resets:** Odalys offers a free stat reset and skill reset to characters below base level 10 (`base_level` condition and
    `run_effects`), the same line as `progression.reset.free_below_base_level`; the reset items stay. Since T-602 the choices also need `out_of_combat`, the window of `progression.reset.refuse_in_combat`.
    **Shop** (T-603): a service that needs data takes it from a field of the action, not from a generic parameter bag: `open_service` with `service: shop` also names `shop_id: shop.*`
    (required for `shop`, refused for any other service; checked at load). The shop is data (`content/shops/*.yaml`, `ShopDef`): its stock, each row an item id and an optional own `price`
    (else the item's `buy_price`); §6.8 has the trading rules.

### 6.5 Progression
Base level + job level, separate EXP tables. Stat points per base level and skill points
per job level, stat point cost, the stat range and combat clamps come from `balance.yaml`
(`BalanceDef`); the max base level is the base EXP table length + 1. Job change requirements from `JobDef`.

**Player skills** (T-502; combat hosts them, `CombatSystem.PlayerSkills.cs`, rules in `Game.Shared/Skills` and `Progression`). State per character is the `ProgressionState`
in the progression store (in memory until T-702): skill points, learned levels, job, completed quests and unlocked jobs.
- **Learning.** `learn_skill` spends one point on a skill of the current job's tree or a parent job's (`ProgressionRules.LearnSkill`): up to the tree entry's `max_level`
  (else the skill's), only when every `requires` entry is met. Refusals are "Skill reasons" terms (`SkillLearnRefused`).
- **Job change.** `change_job` to a job whose `parent_job_id` is the current job and whose `requirements` are met (base level, job level, `quest_ids` all in
  `CompletedQuests`, and `requires_unlock` in `UnlockedJobs`; refused with the first failure's term). **Carried over:** base level and EXP, stats and stat points, **unspent skill
  points and every learned skill** (parent jobs' skills stay usable and passive). **Reset:** job level to 1 and job EXP to 0 (the new job has its own EXP table). The old
  job's stat bonuses (`job:<id>` modifiers) are removed and the new job's added at job level 1; the player's `DefinitionId` becomes the job id, which is what usable
  weapon types and the look follow. `JobChanged` goes out as `PlayerProgressed`. `quest_ids` are read from `CompletedQuests` and `requires_unlock` from `UnlockedJobs`, both filled by
  completed quests (T-602, §6.4): the sample's Warden needs the Warden's Trial.
- **Raising stats** (T-503). `raise_stat` (`stat`, `amount`) runs `ProgressionRules.RaiseStat` on the progression store: each +1 step costs `stat_point_cost` of the value before it, up to
  `max_stat_value`, all or nothing. The new base value goes on the stat sheet in the same tick (`sheet.Base[stat]`), so every derived stat and the HP and SP pools follow at once.
  Refusals are "Stat reasons" terms (`StatRaiseRefused`, on the wire `rejected` with `stat`). `stats` tells the client the points, each stat's next cost and the panel's derived stats.
- **Resets** (T-503). `ProgressionRules.ResetStats` puts every stat back to `starting_stat_value` and refunds exactly the cost of the steps taken (`StatPointsSpent`); `ResetSkills` drops every
  skill to 0 and refunds one skill point per level learned; the host then takes the passive modifiers off the sheet and cancels a running cast; cooldowns are kept (they are timed on
  the clock). Levels, EXP, the job and unspent points never change. **Rules for the item** are data in `balance.yaml` `progression.reset`, checked by the inventory *before* anything is
  used up (`InventorySystem.CheckReset`, which reads the shared progression store and `Entity.InCombatUntilMs`, both plain data): `refuse_when_dead` → `reset_dead`, `refuse_in_combat`
  (a hit dealt or taken within `regen.out_of_combat_delay_ms`) → `reset_in_combat`, nothing allocated or learned → `nothing_to_reset`; below `free_below_base_level` the reset runs and the
  item is **kept** (its cooldown still runs). A reset costs the item itself: there is no currency yet (T-601 adds NPC services that reuse the same effects).
- **Passives.** A learned `passive` skill adds its `passive_modifiers` to the stat sheet with the source `skill:<id>` (formulas of `level` and the sheet's own variables),
  removed and added again on every level change and when the character enters a map; it cannot be cast (`passive_skill`).
- **Casting.** See §6.7 (player skills). Cooldowns live on `Entity.Cooldowns` (`SkillCooldowns`), counted on the map's tick clock and carried across a map change as time
  left, so a warp neither resets nor stretches a cooldown.

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
- **Skills** (T-501). When an attack is due, the AI first looks at the monster's `ai.skills` in order and casts the first one that is off cooldown, whose target
  is within the skill's `target.range` (a self skill always is; a ground skill aims at the target's cell) and whose `chance` roll wins; the cast takes that
  attack's place. A cast stands the monster still for `cast_time_ms` × its `cast_time_mod` (`SkillCastStarted`), then raises `SkillRequested`, which combat
  resolves on its next tick like an attack request (caster alive and free to cast, target alive and on the right side, range, line of sight above range 1,
  no hostile skill on a player in a safe zone; else `SkillRefused`), running the skill's effects through the runner (§6.3). The cooldown (`cooldown_ms`)
  starts when the cast ends. A lost target or `prevents_skills` drops a cast. A monster held by `prevents_move` sends no moves and one held by
  `prevents_attack` requests no attacks. `stats --duel` models the same choice (ADR-002 row 118).
- **A player's summon** (T-502, ADR-002 row 205). The rule: it is on its owner's side. `Ally` replaces the wild state machine for a monster with
  `SummonedByPlayer`: it never scans for or targets a player; it attacks the nearest living monster that is not a player's summon within its `chase_range` of itself **and**
  of its owner (a scan every `think_interval_ms`), or whoever just hit its owner or itself; with nothing to fight it follows its owner to within `ai.wander_radius` cells
  (re-pathing every `repath_interval_ms`). It has no home and no leash and stays idle while its owner is gone. It earns nobody EXP or loot (credit goes to players only).
  Combat refuses a player's `attack` on it (`invalid_target`), and areas and skills treat it as an ally.
- **Cast events** (T-502). A monster's cast with a cast time also raises `SkillCastEnded` (`done`, or `interrupted` when it loses its target or is stunned), for the cast bars.
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
- **Weight.** A player at `burdened` (§6.8) does not regenerate; one at `overweight` cannot attack: `AttackTarget` is refused and a running auto-attack
  ends, both with `overweight`.
- **Regeneration.** Each tick every living player and monster regenerates `hp_regen` / `sp_regen` points per second of simulated time (the derived
  stats; fractions of a point are carried, so the total follows the rate whatever the tick length), never above max. Dealing or taking an
  attack (a miss included) pauses it for `regen.out_of_combat_delay_ms`. The dead never regenerate. A monster healed back to full by it
  forgets its damage shares, as when a hit leaves it at full HP.
- **Effects and statuses** (T-501): combat hosts the instance's effect runner (§6.3): each tick, after regeneration, every living entity's statuses tick and
  expire (entity id order); after the attack requests, last tick's `SkillRequested` events resolve (above, §6.6) and last tick's `ItemUsed` events run their
  consumable's effects on the user (a user that left the map or died in between is not affected; the item stays used). A status's damage lands through the
  same path as a hit, with the applier as source and killer. Every entity's statuses go on `Entity.StatusLook` at the end of the tick for the AOI.
- **Per tick:** intents (`AttackTarget`, `StopAttack`, `Respawn`) → vitals clamp and regeneration → status ticks → the monster `AttackRequested` events raised last tick,
  in order, each re-checked (both alive on this map, a player target not in a `safe` zone, Chebyshev distance within the request's range,
  and for a ranged attack (range above 1) line of sight between the two cells, and an attacker free of `prevents_attack`; otherwise `AttackRefused` with a reason) →
  skill requests → consumables used → player auto-attacks in entity id order → ground item expiry → status looks.
- **Player skills** (T-502). `UseSkill` is an intent combat owns (with `LearnSkill` and `ChangeJob`). It is checked on arrival, in this order: caster alive, skill known,
  learned (`not_learned`), not passive, level up to the learned level (`level_too_high`), not held by `prevents_skills`, weapon type in `weapon_types` (`unarmed` without a
  weapon: `wrong_weapon`), off cooldown, SP for the cost at that level (`not_enough_sp`), a hostile skill not from a safe zone, then the aim: a ground skill needs an
  in-bounds cell, an entity skill a living target on the right side (`friendly` against the players' side; none given: a friendly skill is cast on the caster).
  An accepted order replaces any auto-attack or cast and **walks into range** like an auto-attack (`target.range` Chebyshev, and line of sight above range 1), then
  **casts standing still** for `cast_time_ms` × the caster's `cast_time_mod` (floored; `SkillCastStarted` when above 0). A move, `stop`, a new order, death,
  `prevents_skills` or a lost target ends the cast without cost (`SkillCastEnded` `cancelled` or `interrupted`). **When the cast time is up** the SP is spent and the cooldown
  starts (`SkillCooldownStarted`, `SkillCastEnded` `done`) and a `SkillRequested` goes out, which the effect runner's host resolves on the next tick with all the checks of
  §6.6 (range and sight again; a refusal there still costs the SP). Cost, cast time and cooldown are formulas of the skill `level`.
- **Player auto-attack.** `AttackTarget` (monsters only; refused in a `safe` zone) keeps attacking until the target dies or leaves, the player
  sends `StopAttack`, a new move order (seen as a `MovementStarted` / `MovementStopped` combat did not cause), or no path leads into range.
  The player walks in with `MoveTo` (re-path at most every `combat.player_repath_interval_ms`) and attacks at its `aspd` within
  `combat.player_attack_range`; a ranged attack also needs line of sight, and without it the player walks closer as when out of range.
  Standing in a safe zone it neither hits nor can be hit.
- **Disconnected characters fight on.** A character in its disconnect grace period (§10) is an ordinary player entity: it stops
  walking but can still be attacked and killed (disconnecting is no escape), stays dead, and a resumed session sees it dead and sends
  `Respawn`. Combat has no special case for it.
- **Hits.** Every attack goes through `CombatResolver` with one reused `AttackContext` (monster element, race and size from `MonsterDef`;
  a player's `weapon_atk`, `refine` and attack element from its equipped weapon, `Entity.Weapon`, set by the inventory, §6.8; 0 and neutral unarmed).
  A weapon's `attack_range` replaces `combat.player_attack_range`. HP changes by the damage, clamped to 0..max; `DamageDealt` carries amount, `hit`/`crit`/`miss` and HP left.
- **Death** at 0 HP, in the same step: `Dead = true` (movement stops it, the router drops its intents, combat ignores it), `EntityDied`. A monster
  stays as a corpse for `death.corpse_ms`, is then despawned, and is respawned by the spawner. A player stays dead on its cell until `Respawn`, then comes back with
  `death.respawn_hp_percent` of max HP at the respawn point of the map it died on (the map's `respawn` cell, else the first warp arriving on it, else the `new_character` start); on death it loses
  `death.exp_penalty_percent` of its level's requirement, never more than its EXP (`ProgressionRules.LoseBaseExp`: no level loss).
- **Kill credit** (T-602): when a monster dies combat raises `MonsterKilled` with every player that has a damage share in it and is still on the map, in first-hit order: the
  same players who share its EXP. Quests count `kill` objectives from it; a party rule is T-902.
- **EXP.** Per monster, the HP each player took since its last full-HP moment. On death the players still on the map share `base_exp` /
  `job_exp` in proportion (largest remainder; ties to the first hitter; the shares add up exactly) through `ProgressionRules`, which raise
  `ExpGained` and `PlayerProgressed` (`LevelUp` / `JobLevelUp`); the sheet takes the new levels and job bonuses. Progression is kept per
  character in an `IPlayerProgressionStore` shared by all instances (in memory until persistence).
- **Drops.** Each `drop_table_ids` table rolls every entry on its own (`chance`, then a quantity in `min..max`) with the instance random.
  Each drop is an `EntityKind.Item` entity on a random walkable cell within `loot.spread_radius` of the death cell (`ItemDropped`); for
  `loot.priority_ms` only the top damager may take it (`LootRules.MayPickUp`), and after `loot.lifetime_ms` it is despawned
  (`ItemExpired`). Clients see ground items as kind `item` (§10) with their quantity and whether they may take them now, never the owner or the timers.
  Picking them up is the inventory's (§6.8).

### 6.8 Inventory
One system, `Game.Server/World/Inventory` (`inventory`), after combat and before the AOI; the rules are pure functions in `Game.Shared/Items`
(`InventoryRules`, `InventoryState`), shared with the tests. Numbers from `balance.yaml` `inventory` and `new_character.start_items`; the weight limit is the
derived stat `weight_limit` (`formulas.yaml`).

- **Model.** Per character, an `InventoryState` in an `IInventoryStore` shared by every instance (in memory until T-702, like progression). An **entry** is a
  stack of one item up to its `max_stack`, or one equipment item with its own instance id, its socketed cards (in socket order) and its refine level. Entry ids come from the store,
  unique for the server run; an equipment item dropped and picked up again keeps its id, cards and refine level (they ride on the ground item, which clients never see them on). Worn entries stay in the inventory (their slot is set), so they count
  toward `inventory.max_entries` and the weight, and putting something on never needs room. **Weight** = Σ `weight × quantity`.
- **Weight states** (`WeightStates`, from the weight as a share of `weight_limit`): `normal`; `burdened` from `inventory.burdened_percent` (50%): no natural
  HP and SP regeneration; `overweight` from `inventory.overweight_percent` (90%): no regeneration and no attacks (§6.7). A pick-up that would pass 100% is
  refused (`too_heavy`). The state is on `Entity.Load`; the limit is re-read whenever the stat sheet changes (a level, STR from equipment), and the owner is told.
- **Atomic operations.** Every change (pick up, use, equip, unequip, drop) checks everything first and returns an "Item reasons" term with nothing changed;
  only then does it change the state, in steps that cannot fail, inside one tick. So each one maps onto one database transaction for T-702 without changing
  the rules. A refusal is an `ItemRefused` event and a `rejected` message.
- **Intents** (`PickUp`, `UseItem`, `Equip`, `Unequip`, `DropItem`, `InsertCard`, `RefineItem`; rate-limited like every client message).
  - *Pick up:* the gateway checks the target is a ground item the client sees and stops the auto-attack; the inventory walks the player with `MoveTo`
    until it is within `inventory.pickup_range` (1, Chebyshev) of the item, then checks `LootRules.MayPickUp`, the weight and the free entries, and takes
    it (`ItemAcquired`; the entity is despawned). A new move order or a stop cancels the walk (`cancelled`), no path ends it (`unreachable`), and an item taken
    or expired meanwhile is `item_gone`. Two players on one item in the same tick: the intent that arrived first wins.
  - *Use:* a consumable that is not worn, off its cooldown (`cooldown_ms` per item id, on the server clock so it holds across maps). Its effects go through
    the effect runner (T-501, §6.3): the inventory uses the item up and raises `ItemUsed`, and combat runs its effects on the user on its next tick, at level 1.
    Every effect type runs (an effect that could not is a load error), so the antidote cures poison and the Swift Elixir applies Swift. `consumed: false` items stay.
  - *Equip:* kind `equip`; `slot` (any one of the item's `slots`; without one, the first of them that is free, else the first); `min_base_level`;
    `job_ids`, and for a weapon its type in the job's `weapon_types`. A filled slot swaps in the same operation. **Two-handed rule (data):** a weapon whose
    `weapon_type` is in `inventory.two_handed_weapon_types` (`sword_2h`, `staff`, `bow`) placed in `weapon` also takes `shield`: putting it on takes a
    shield off, and putting a shield on takes it off. Modifiers are evaluated once, with `refine` = the entry's refine level, and go onto the stat sheet with the source
    `equip:<entry id>`; each socketed card's modifiers go on with their own source `card:<entry id>:<socket>` (§6.1); taking the entry off removes exactly those sources.
  - *Unequip:* a worn slot (`not_equipped` otherwise). *Drop:* a carried entry (worn ones are `item_equipped`), 1..its quantity, onto the player's cell
    as a ground item with no owner (`ItemDiscarded`), gone after `inventory.dropped_lifetime_ms`.
  - *Insert card* (T-402, `InsertCard`): a card entry into an equipment entry, carried or worn, when one of the card's `slots` is one of the item's
    `slots` and it has a free one of its `card_slots`; refusals `item_gone`, `not_a_card`, `not_equipment`, `card_does_not_fit`, `no_free_socket`. For good
    (removing cards is a later NPC service). One card unit is used up (its weight goes; the equipment's own weight stays). Worn, the card counts at once (`CardInserted`).
  - *Refine* (T-402, `RefineItem`): a `refinable` equipment entry, carried or worn, one level up using `content/refine.yaml` (`RefineDef`, validated at load:
    one step per level 1..`max_level`, `success_percent` 1–100, `material_item_id` a `material` item, `material_quantity` ≥ 1, `failure` a "Refine failures" term).
    Refusals `item_gone`, `not_equipment`, `not_refinable`, `at_max_refine`, `missing_material` (carried units only). Then the material is used up, one
    `Next(100)` of the instance's seeded random decides: below `success_percent` the level goes up, else the step's failure: `keep`, `down` (not below 0) or
    `break` (the entry is gone with its cards). A worn entry's modifiers are re-evaluated (or removed when it broke) and the weapon's `refine` reaches combat
    (`Entity.Weapon`). `ItemRefined` → the owner's `refine_result`. **Armour rule:** refine affects armour only through the item's own modifiers, which may use
    the formula variable `refine` (Padded Vest `def: 4 + refine * 2`, Wooden Buckler `def: 2 + refine`); no formula change. Refining costs no currency until currency exists.
    Sample data: max +7; +1 and +2 always succeed, then 90/75/60/45/30 %, Temper Shards ×1–3, failures keep up to +4, down at +5 and +6, break at +7.
- **What other systems read** (hard rule 7, no calls): the sheet's modifiers; `Entity.Weapon` (type, `weapon_atk`, refine, range, element) for combat;
  `Entity.Look` (weapon type and the worn item ids that have a worn visual, in slot order) for the AOI; `Entity.Load` for combat.
- **Currency** (T-602): one balance per character, `InventoryState.Currency` (in memory until T-702), kept with the bag so one transaction can take items and pay or spend currency;
  sent as `currency` in `inventory` and `inventory_changed` and shown in the bag with the bundle's `currency_name`. Quest rewards and shop sales add to it; shop purchases spend it.
- **Shops** (T-603, `ShopContent`, `InventorySystem`, `Game.Shared/Shop/ShopRules.cs`). A shop is a `ShopDef` (`shop.*`, `content/shops/`): a stock list of item ids with an optional own price; load
  validation needs at least one row, no item twice, every price from 1 to `ShopRules.MaxUnitPrice` (1,000,000,000; the item's `buy_price` counts when a row sets none) and every item to exist.
  `shop.max_buy_quantity` (`balance.yaml`, 99) caps one purchase. A dialogue opens it with `open_service` (`service: shop`, `shop_id`). The **inventory** system (not the dialogue system) owns the two
  intents, because the items and the currency are one transaction (rule 10): `shop_buy` (item, quantity) and `shop_sell` (entry, quantity). **The client never sends a price**; every price is the
  server's: a buy costs the stock row's price, a sale pays the item's own `sell_price`. Both need a living player with a `shop` service open and the NPC within `dialogue.talk_range`
  (`service_required`, `out_of_range`, `dead`). **Buy** is refused unless the item is in the open shop's stock (`not_in_stock`), the quantity is from 1 to the limit (`invalid_quantity`,
  `quantity_limit`), the total is affordable (`cannot_afford`; a price × quantity beyond the `long` range counts as unaffordable: `ShopRules.TryTotal` never wraps) and the bag takes it
  (`inventory_full`, `too_heavy`). **Sell** works on a carried entry and is refused for a missing one (`item_gone`), a worn one (`item_equipped`), a bad quantity (`invalid_quantity`), a quest item or an
  item with `sell_price` 0 (`not_sellable`: the two reset items are priced to buy, not to sell back) and a balance that could not hold the proceeds (`currency_full`). **Decisions:** any carried item with a
  sell price sells to any shop, not only the shop's own stock; starter items sell like any other once they are in the bag (a worn one must come off first); quest-kind items never sell; an
  item that an active quest still needs can be sold, and the quest's counter simply drops (nothing is lost for good: it can be gathered again). Every check runs before anything changes and the last
  one that can fail is the add or the remove itself, which changes nothing when it refuses; so items and currency move together or not at all, in one `inventory_changed` (`shop_buy` / `shop_sell`,
  with the new `currency`). A refusal is `ItemRefused` → `rejected` (`intent` `shop_buy` / `shop_sell`, `entry` or `item`). When the shop opens the inventory answers `ServiceOpened` with `ShopOpened`
  (the owner's `shop` message: the priced stock, the sell prices of every item a shop buys, the buy limit). The display bundle has no prices (§5.4): the sell prices ride in `shop`, which only a
  player at a shop is sent.
- **Quest turn-ins** (T-602): `QuestItemsRequested` takes the consumed items and gives the item rewards (`TryExchange`, all or nothing) and adds the currency, then `inventory_changed`
  (`quest`) and `QuestItemsResolved`; a refusal changes nothing and the quest waits (§6.4).
- **Starting items** are data: `new_character.start_items` (`item_id`, `quantity`, `equipped`), validated at load. A new character gets them when the
  inventory first sees it; today the Bone Dirk (worn) and three Healing Draughts.

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
  of its centre, or as wide as the shape looks if that is more. The client checks no range, path or walkability. A tap that is not on a monster but on a ground
  item (the same pick radius, at the bag's height) sends `pick_up`; the loot ring shows what this client may take, the server decides. **Tap an NPC** (T-601) → `talk`: the same pick radius; the nearest of monster, item and NPC within reach wins (a monster wins a tie, then an item), so an NPC tapped directly beats the ground under it. The server walks the character up and starts the dialogue. A skill waiting for its target ignores NPCs.
- **Long-press** (still for 0.5 s) on any entity shows a small info panel with what the display bundle says (name, stack count, kind) and nothing else; on the ground it
  does nothing. It fires once, while the finger is down, and the lift that ends it is never a tap. Any later touch hides the panel.
- **Drag** (past the threshold) is neither tap nor long-press. **Pinch** (two fingers, past an 8 dp dead zone) and the scroll wheel zoom the camera inside its clamp;
  a pinch never taps, and neither does the finger left after it.
- **UI first.** A touch that begins on a button, field or panel belongs to the UI until it ends and never reaches the world. A full-screen panel blocks everything.
- **Hotbar** buttons call `HotbarModel.Press(slot)`. T-502 binds them to the skill bar: `SkillBook` is the `IHotbarContent` (the learned active skills in tree order, a slot's cooldown fraction, and a
  version that moves every frame a cooldown runs) and `ClientSession.PressSkill` the activator. A skill on the caster is sent at once; one that needs an enemy is sent on the
  current target if it is alive, else it **waits for a tap** on one (the slot is lit and the flash says so; a second press cancels); a ground skill waits for a tap on the
  ground (a tap on an enemy aims at its cell). A tap on the ground while an enemy skill waits is a walk and cancels the wait. The slot shows a dark sweep over the
  cooldown and its whole seconds; a press during the cooldown is refused by the bar and sends nothing. Friendly skills that need a target are cast on the caster in v1.
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
- **Layout.** Top left: level and EXP, HP and SP with bars. Top centre: connection notice and the target frame (the target's name and an HP bar: only the fraction the server sends, redrawn when the entity's `HpVersion` counter moves; never a number). A level-up pulses the top-left block for `level_up_flash_seconds`. Top right: connection state, the UI size
  button, log out. Bottom: the one-line flash, then the hotbar (slot count, rows and size are client config; a narrow screen shrinks slots to the minimum and then adds rows).
  The login, death, stop and refusal screens are one full-screen panel whose content stays inside the safe area.
- **Bag** (T-401, `client/Scene/InventoryPanel.cs`, view model `client/Core/Items/InventoryView.cs`). The Bag button (top right, while online) opens a panel
  over the safe area: the entry count, the weight line and bar (coloured when `burdened` or `overweight`), the ten equipment slots, and the carried entries as
  buttons with icon (theme `icon.*` key from the bundle), name and count, in a scroll container (a phone at 1.5x scrolls), then the selected entry's
  actions: Use, Equip, Take off, Drop (from the bundle's item kind and whether it is worn). A tap on a worn slot takes it off. It only sends intents; the
  server's `inventory_changed` redraws it. It redraws only when the view's `Version` moves and costs nothing while closed. The layout check covers it open
  and filled with the longest words at every screen shape and scale (controls inside a scroll container may be scrolled out of sight; only their sizes count).
- **Skills** (T-502, `client/Scene/SkillPanel.cs`, view model `client/Core/Skills/SkillBook.cs`). The Skills button (bottom left, beside the hotbar, while online) opens a panel
  like the bag: the job and unspent points, one row for every skill of the job's tree and its parents' trees (name, `Lv x/max`, and while locked the requirements it
  lacks; locked rows are dimmed, learnable ones tinted), the selected skill's description and a Learn button, and one button per job the server says the player may change
  to. It only sends `learn_skill` and `change_job`; the new `skills` message redraws it. **Cast bars:** one amber bar above any caster in view (the player's own and monsters'), a
  second multimesh next to the HP bars (one draw call, none while nobody casts), filled by the cast time the server sent and closed by `cast_ends`, death or leaving view.
- **Stats** (T-503, `client/Scene/StatsPanel.cs`, view model `client/Core/Stats/StatBook.cs`). Opened with the Stats button in the skill panel's header (the Skills button on the bottom
  edge is the one entry, so the full phone HUD gets no new button; the stats panel has a Skills button back). It shows the unspent stat points, the six stats as label and `+ (cost)`
  button pairs in a two-column grid (the cost is the server's `next cost`; the button is greyed when the points do not cover it, and the server decides anyway) and the derived stats
  (ATK, MATK, DEF, HIT, FLEE, ASPD, max HP and SP) as one wrapping line, in a scroll container, every button at the touch minimum. It only sends `raise_stat`.
  **Reset items** (the bundle marks them `confirm_use`): Use in the bag first shows what the item does and a "Use it" button; only that sends `use_item`. The server's refusal
  (`reset_in_combat` and so on) appears on the flash line and the item stays. The skill panel redraws on the `skills` message the reset sends.
- **Dialogue** (T-601, `client/Scene/DialoguePanel.cs`, view model `client/Core/Dialogue/DialogueView.cs`). A panel at the bottom of the safe area, so the world stays in view: the speaker's
  portrait (the theme's `portrait.*` texture, else the first letter of the name, §7.7), name and title (from the display bundle), the line, and the choices as buttons of at least the touch
  target (48 dp), in a scroll container; a Close button. It only sends `dialogue_choice` and `dialogue_close`; it closes when the server says `dialogue_ended`. A `service_opened` for `refine`
  opens the bag, where the refine button is; `rejected` `service_required` is said on the flash line.
- **Shop** (T-603, `client/Scene/ShopPanel.cs`, view model `client/Core/Shop/ShopBook.cs`). A panel the size of the bag's, opened by the `shop` message and closed when the server ends the dialogue or the service
  (the panel follows `Dialogue.Service`; a `rejected` `service_required` / `out_of_range` closes it too). Header with the currency balance and a Close button (which sends `dialogue_close`); a Buy tab (the stock with
  its prices) and a Sell tab (the carried bag with what the shop pays, "not bought" and disabled where it will not take the item); a picker (minus, the count with the total, plus, Max) and the Buy / Sell button.
  It sends `shop_buy` (item, count) and `shop_sell` (entry, count) only; the total shown is a preview from `ShopRules`, and the balance and bag come back in `inventory_changed`. Rows, tabs and picker buttons
  are at least the touch target (48 dp); the panel is in the `-Run ui` layout matrix (the longest words, six screen shapes, every scale) and its touch targets are checked with the rest of the HUD.
- **Quests** (T-602, `client/Scene/QuestPanel.cs`, view model `client/Core/Quests/QuestBook.cs`). **Tracker:** a compact button under the bars on the top left (13 dp text, the left column's
  width, at least the touch target): the tracked quest's name and one line per objective with the server's counter (`Defeat Gel Slime 3/5`), or "Quests" when none is active; the quest
  last accepted or advanced is tracked, and the log's Track button picks another. A tap opens the **quest log**, a panel like the stats panel: active quests with their step text
  (from the bundle), objectives and a held reason, each with Track and Abandon (asks once more before sending `abandon_quest`), then the completed quests. **Markers:** a "!" drawn
  by a billboard shader above every NPC the server marks, gold to offer and green to turn in, one multimesh (one draw call, none when no NPC is marked), rewritten only when the
  markers or the NPCs in view change. The client decides no count, step or completion; quest changes go on the flash line. The bag's first line shows the currency.
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

**Client** (T-403; pure rules in `client/Core/Theme/Equipment.cs`, tested; drawing in `client/Scene/Theme`). A player's view record carries its weapon type and worn item
ids (T-401); `EntityView.LookVersion` counts their changes.
- *Resolution:* item id → the bundle's `visual` key → the theme chain (§7.7) → an entry with exactly one of `attach` (a bone name) or `skinned` (a rig). A key no
  theme has, an entry with neither or both, or one skinned to another rig than the character's falls back to the theme's missing model (`fallbacks.missing`, else
  the built-in marker), drawn at 0.3 scale on the default point, with one warning per key; an item the bundle gives no visual draws nothing (one warning). Never a crash.
- *Default attach point:* the skeleton's first bone (the rig's root: `hips` on the humanoid). An attached piece whose bone the rig lacks goes there, with one warning
  per model and bone; a character model without a skin takes its pieces as plain meshes on its root.
- *Re-dressing:* the presenter hands the look to the visual when the entity's look changed; the visual's `Wardrobe` does nothing unless the counter moved, and then
  reports only the added and removed items (a multiset: the same accessory twice is two pieces). Only added pieces are loaded. A pooled visual keeps its pieces for
  the next entity and swaps only what differs. A two-handed weapon arrives with the shield already gone in the same change (ADR-002 row 170), so the off hand is empty.
- *One draw call per character (§9):* the body and its pieces are merged into the body's own skinned mesh, per level of detail (`GearMesher`). An attached piece becomes
  part of the skin, weighted fully to its bone, so it follows the bone exactly; a skinned piece keeps its weights, its skin's binds mapped onto the body's. Surfaces whose
  materials differ only in colour (untextured, opaque `StandardMaterial3D`, the same shading) join one surface with the colour baked into the vertex colours; a textured
  material keeps its own surface, so real art merges into one call only with a shared atlas (§9's one material per character). A merged mesh is cached by body file,
  level of detail and the set of pieces in any order (`WornSet`), so a crowd in the same gear shares one mesh and one skin. A piece without a LOD1 mesh uses its LOD0 at both levels.
- *Loading:* a piece's file loads through the same `ModelStore` as characters (threaded with a renderer, on the main thread headless: ADR-002 row 141); its meshes are
  taken from the scene once and shared by every character wearing it, and the scene is let go. Making merges shares the per-frame instance budget (`instantiate_per_frame`). Since T-222 the merge runs on a worker over arrays read once per source mesh (§7.9); the character keeps its old look until it is done.
- *Preloading* (T-405, ADR-002 row 182): `GearPreload` (pure, `client/Core/Theme`) names the worn-visual models to load ahead of need: the pieces worn by characters on screen first,
  then the equipment in the player's own bag, each path once, at most `gear_preload_limit` (default 16; 0 turns it off). `ThemedEntities.WatchGear` re-plans when a character's look or the
  inventory changes; the models start loading through the same `ModelStore` (the row 141 rule) and, once loaded, their meshes are taken one per frame into the same piece cache that
  `TryDress` reads, so equipping a bag item finds them ready. Memory: preloaded pieces are the same entries worn pieces use, held at most `model_cache_limit` (48) per store (least
  recently used go first; a dressed character keeps its own references), so the preload adds at most 16 small meshes (the placeholder gear is 1–2 KB each; §9's 200 MB install and
  memory budgets are not touched). *Measured* (T-405, `run-e2e.ps1 -Run items -Windowed`, integrated desktop GPU, placeholder pack, 3 runs each): main-thread model work from the press of
  Equip until the weapon is on, 1.6, 1.6 and 3.6 ms without the preload and 0.00 ms with it. The frame that merges the piece into the body mesh is unchanged (about 60–105 ms in both
  modes, at 30 fps), which is the next thing to look at (see the T-405 follow-ups). Not run on a phone (ADR-002 row 112).
- *Clips by weapon type (§7.6):* an animation library may map `<animation id>.<weapon type>` (e.g. `attack.staff`, `idle.staff`) besides the plain id; the weapon's
  clip wins where the library has it, else the generic one. Hit timing is unchanged: T-303's `impact_delay`.
- *Measured* (T-403, integrated desktop GPU, 1280x720, `tools/loadtest --bots 70 --fighters 0.7 --spare-slots 3` and `--measure 40`, about 40 players on screen; the
  client's debug-only `--measure-dress <item ids>` draws every player in those items): peak draw calls 75 with no gear, 77 with the real gear (every bot wears its
  starting dagger), 79 with dagger, cap, vest and buckler on everyone (92 with `--fight`, where the player also hunts), so worn pieces cost no draw call; triangles peak 11.8k, 15.4k, 29.5k. Drawn as separate
  nodes (the first version), one weapon each took the crowd peak to 117. None of it has run on a phone (ADR-002 row 112).

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

**Hit timing (T-303).** A delta's `hits` go to `WorldView.Combat` (`CombatTimeline`, pure and tested). A hit received at view time `t` swings the attacker at
`t + render delay` (the moment the attacker's movement reaches the same cell on screen, so a swing never shows before it is in range) and **lands** at the swing
plus the attacker's impact delay: `impact_delay` on its model's manifest entry, else its animation library's (per rig), else the client default (`impact_delay_seconds`,
0.2 s), clamped to `max_impact_delay_seconds`. At the landing the target plays its hit clip (not on a miss) and `CombatTimeline.Impact` fires once with a `HitImpact`
(attacker, target, amount, hit/crit/miss, dealt/taken/other, world position, time). **That event is the one timing source**: damage numbers, effects and audio (T-305)
subscribe to it. A hit still pending when the clock is more than `stale_hit_seconds` past its moment (a stall, a resume) is dropped, a snapshot (the end of a catch-up,
row 101) clears everything pending, and a delta tick that is not newer than the last is ignored, so old hits are never replayed. A death waits for the last pending
hit on the entity (`EntityView.DeadShown`), then plays the die clip once, holds the last frame, and starts the death effect; an entity that appears already dead lies
down at once. A dead body that leaves view fades out and sinks over `corpse_fade_seconds` before its visual returns to the pool. The player's own death screen is
unchanged (row 102).

**Damage numbers.** `DamageNumberPool` (pure) holds up to `max_damage_numbers` (16) numbers; at the cap the **oldest is dropped** (not merged) and counted. Numbers on
one target within `damage_number_lane_seconds` take different sideways lanes (0, +1, -1, +2, -2, ...). Style = who (dealt by you, taken by you, others) x hit/crit/miss;
a crit is larger with a pop and an accent colour, a miss shows `UiText.MissWord`. `client/Scene/CombatFx.cs` draws them all from one canvas item (outlines, then fills, grouped by font size, so they batch into a few draw calls; T-220) on a canvas layer under the
HUD's, anchored to the world through the camera, sized in dp x UI scale like the HUD, and moves nothing when no number is showing. Digit strings are cached, so a hit
allocates nothing after warm-up.

**Effects.** `effect.hit`, `effect.crit`, `effect.death` and `effect.level_up` (VOCABULARY "Effect names") resolve through `ThemeCatalog.ResolveEffect` (the manifest entry tagged with that name in
each theme layer, then the name chain) to a tiny scene; instances are pooled per scene, at most `max_effects` (10) play at once (the oldest ends early; hits between others use only half of them), and the client pops and
shrinks them (no animation player, no transparency), so each costs one draw call and a few dozen triangles.

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
per frame, so a warp spreads over a few frames. With no renderer (the headless display server, which uses Godot's dummy renderer) everything loads on the main thread instead: that renderer's mesh allocator is not
thread-safe, and creating meshes from several threads there corrupts memory and crashes the process (T-218, `ModelLoading`). Any new threaded resource loading must follow the same
rule. Memory is bounded: `model_cache_limit` (48) loaded scenes (least recently used, never one in use) and `pool_idle_limit` (12) idle
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
- **Client implementation** (T-305): the pure rules are in `client/Core/Audio` (tested without the engine), the players and buses in `client/Scene/Audio`.
  - *Resolution:* every request goes through `AudioResolver` and the theme chain (`SoundCatalog`: active theme, then `default`; a key counts as found when an entry lists it
    and a file exists). The order is the content key, then the category default, then silence with one warning per key. A variation is picked at random from `files`
    with the injected `IRandom`, with the entry's `volume_db`, a random pitch from its range, and its bus. Loading is always on the main thread (§7.7: the headless dummy renderer).
  - *Voices (§9, client config `audio_*`):* 24 players made at start, no allocation per sound after warm-up. A per-key cooldown (0.08 s) comes first, then a free voice,
    then stealing: lowest priority, then oldest; a sound is never cut for a less important one (priorities: interface and events > own actions > ordinary > other
    characters' combat). Sounds further than `audio_cull_distance` (30 world units) from the camera focus are skipped; interface sounds and events have no place and are not.
  - *Music and ambience:* one `CrossfadeChannel` each with two players, an equal-power fade of `audio_crossfade_seconds` (2 s), started on every map load, a warp included.
    A map without music fades the old one out. OGG is decoded as it plays, from the compressed bytes in memory (Godot does not read OGG from disk while playing); only the
    streams in use are kept.
  - *Effects memory:* each loaded effect is charged its decoded size (length × 44.1 kHz × 2 channels × 2 bytes, an upper bound) to a `SoundBudget`; over `audio_effects_budget_mb`
    (48) the least recently used are let go.
  - *Buses and settings:* `music`, `sfx`, `ui`, `ambient` (`default_bus_layout.tres`). The player's volume per bus and the mute switch live in `UserPreferences` and are set in
    the HUD's Sound panel; the engine's bus volume is their decibel value.
  - *What plays when:* map music and ambience on load; a monster's aggro sound the first time it hits anyone (the wire has no aggro message); hit and die sounds from the bundle's
    `sounds` or the defaults; a weapon's swing and impact from `weapon.<type>.swing|impact`, on the same moments as the combat visuals (`IImpactSource`: swing = the attack clip
    starts, impact = the hit lands, from T-303's `CombatTimeline.Impact` through `CombatImpactSource`); level up from `audio.events`; the interface tap on HUD
    buttons. A player swings with the weapon type its view record says (`weapon`, T-401). Own `inventory_changed` messages call the hooks: `picked_up` ->
    `OnItemAcquired` (the `ItemAcquired` event sound), `used` -> `OnItemUsed`, `equipped` -> `OnItemEquipped` (the item's sound key, else the category default).
  - *Debug trace:* client setting `audio_trace` (`--set audio_trace=1`, or `run-e2e.ps1 -AudioTrace`) logs one `[audio]` line per play, skip (with the reason), steal, crossfade, bus change and pause.
  - *Phone:* the pause in the background is the engine's pause notification and is pure state (`AppAudioState`, which also freezes the audio clock). **Godot 4.7.2 cannot do from C#:**
    the iOS silent switch (it has no API for reading it; making effects follow it while music does not needs native code that sets the audio session category and reads the switch)
    and Android audio focus (the engine never requests it; it needs a small Android plugin calling `AudioManager`). `PlatformAudio` reads an engine singleton `GameAudioPlatform`
    (`is_silent_switch_on()`, `has_audio_focus()`) if a plugin provides one; none exists yet. None of it has run on a device (ADR-002 row 112).
- **Validated at load** (`validate`, the `audio` rule): every sound key used by content, defaults or events exists in the manifest with the right
  prefix (`sfx.` for monster, item, skill, default and event sounds; `music.`, `ambience.` for maps); categories, weapon types, events and buses
  are known terms; `volume_db` is within -60 to 12; `pitch` is `[min, max]` with 0 < min ≤ max; `files` is not empty and every file ends in `.ogg`.
  The manifest is theme data and is **not** in the client bundle; the bundle carries only the content's sound keys.

### 7.9 Look: storybook toon (ADR-003)
T-222 draws ADR-003's storybook toon in the Mobile renderer. Pure parts in `client/Core/Theme/RenderLook.cs` (tested), drawing in `client/Scene/Theme/ToonLook.cs`.
- **From the theme.** Three render roles (VOCABULARY "Render roles"): a manifest entry tagged `"render": "toon"` (`mat.toon`, a `ShaderMaterial`), `"outline"` (`mat.outline`) and
  `"environment"` (`scene.environment`), resolved through the theme chain (`RenderPlan`). A chain without a role draws that part as before T-222, with **one** warning naming
  what is missing. The setting `toon_shading` (default on) switches the whole look off, for measuring and as a fallback for weak phones.
- **Toon material.** Three cel steps (shadow, mid, lit) from one directional light, in `light()` (`toon_light.gdshaderinc`, shared with the map ground so terrain uses the same
  ramp); the shadow map folds into the steps; the flat ambient lights the shadow step; no specular. `ToonLook` gives every surface of a model, worn piece and map mesh whose
  material is an untextured or textured opaque `BaseMaterial3D` a toon material with the same colour and texture (a surface override; identical looks share one material),
  so a dressed character is still one surface. Unshaded, transparent and shader materials are left alone. Vertex colours always count: glTF's `COLOR_0` multiplies the
  base colour, which Godot's importer leaves off, and the placeholders keep their palette colours there. A vertex with alpha 0 glows: drawn unlit at its full colour.
- **Outlines.** An inverted hull: the theme's outline material as the `material_overlay` of a character, monster, NPC or ground item that is alive, within
  `outline_distance_cells` (10) of the camera target, and among the nearest `outline_max_count` (6) of those (`OutlinePlanner`, planned with the levels of detail).
  Never on terrain or props. Each vertex moves out along its outline direction (the `TANGENT` the generated models carry: the normal averaged over the vertices at the
  same place, so the hull stays closed at hard edges; `GearMesher` carries it through the merge), by `width_px` pixels of a 720-pixel-high view. A dressed character
  with its outline is one surface and one overlay: at most 2 draw calls.
- **World.** `scene.environment` holds a sky shader (a soft gradient; at the 50° camera mostly its lower half shows past the map's edge), a flat ambient (colour, not sky),
  depth haze from 17 to 60 units, no tone mapping, and the warm key light with the one shadow (§9: only the player casts). The shadow map is 1024² (project
  setting, also on mobile), orthogonal, 24 units, hard edges.
- **Placeholders** (`tools/themegen`): the humanoid's head is about a third of its height and its face sits above the head's equator, tipped toward the camera;
  silhouettes differ by headgear (hood, crested helm, pointed hat) and shoulders. Colours come from `themes/<theme>/palette.json` (ADR-003 point 4): each job's two
  colours, monsters two colours from the shared creature list. Rigs, bones, attach points and clip names are unchanged.
- **Skill bar text** (ADR-002 rows 217, 220): a slot shows its number (and the whole seconds of a running cooldown) and the name below; the font is the largest step
  from 13 dp down to 9 dp at which every word fits the slot, so a name wraps only between words (`HotbarText`). The text is set again only when the skill or a whole
  second changes; the sweep moves every frame.
- **Equip frame** (ADR-002 row 223): `GearMesher` merges on a worker; the main thread reads each source mesh's arrays once (worn pieces when they are preloaded) and
  only turns the result into a mesh and a skin. **Retry** (row 225): a worn piece that failed to load is not "present"; the preload tries it again, `piece_load_attempts`
  (3) tries in all, then it is drawn as the missing model for good.
- *Measured:* see §9 "Toon look". None of it has run on a phone (ADR-002 row 112); the shader's cost there is on the T-803 checklist.

## 8. Theming (asset swap)

**The theme contract** (T-224) is [THEMES.md](THEMES.md): what a theme must provide per asset kind (rig and bone names, required clips, scale and facing,
attach points, the §9 budgets per model, how the toon look applies), which of it `validate` checks, manifest **variants**, and `tools/themeimport`, which turns an
art pack into a theme from a mapping file. Forks apply a theme with [FORKING.md](FORKING.md); this template keeps only the generated placeholders.

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
Models are low-poly rigs (five shared rigs) with LOD0 and LOD1 and one material; icons are 64x64 PNG, sounds one silent OGG, effects an empty `.tscn`. Map scenes are not generated: they are authored (§5.3).
**Colours** (T-222, ADR-003 point 4): with a `palette.json` in the theme, the model's colours are **vertex colours** taken from the palette (a job's or a creature's identity colours;
gear and other creatures pick two palette colours by a hash of the key) and the material's base colour is white, so the toon material (§7.9) multiplies them and a dressed
character's pieces bake into one surface. Icons and portraits are still a flat colour taken from the key's hash (`KeyColor`). Without a `palette.json` the old behaviour stands: one material
tinted with the colour of the key. The generator also writes the second theme with every hue turned, so a swap is visible. A theme's own art follows its palette file; the game never reads it.
`themes/test` is a partial theme (three models in other colours) used by tests.

Rig names are theme-internal; the validator checks that a model, its animation library and its
skinned equipment use the same rig. Active theme is a config setting.

**Variants** (T-224, THEMES.md §2): an entry with `"variant_of": "<key>"` draws that key's model (looked up through the chain) with an optional `tint` (`#rrggbb`,
multiplied into its materials: one more material per variant), `scale` (0.1–4) and `outline` (`false` or a colour), so one model serves a recolour, an elite or an
alias. It takes the base's file, rig, animations and clips. Only whole models (monsters, NPCs, job models, props) can be variants. The client resolves it in
`ThemeCatalog.TryFind` (pure, tested); `ModelVisual` scales the instance, `ToonLook` makes the tinted material and the outline copy. A tinted variant pools apart from
its base. `default` has one example, `model.monster.gel_slime_elite`.

**Equipment entries** (T-403, §7.5): `"attach": "<bone>"` for a rigid piece, modelled in its bone's space (origin at the bone, facing -Z), or `"skinned": "<rig>"` for a
mesh skinned to that rig in the body's own mesh space; exactly one of the two. An animation library's `clips` may add `<animation id>.<weapon type>` entries (VOCABULARY
"Weapon types"), which a character holding that weapon type plays instead of the generic clip. Equipment should use an untextured or atlas-shared material so it merges
into the character's single draw call. The placeholder pack shows both kinds: weapons, the buckler and the cap are attached, the vest is skinned; the humanoid library
has `attack.dagger`, `idle.staff` and `attack.staff`.

**Checked at build time** (T-405, `validate`, `Game.Content/Validation/ThemeCheck.cs`): each `model.equip.*` entry has exactly one of `attach` or `skinned`; an `attach` bone exists in the
rig of every body model of `appearance.yaml` (the bones are read from the glTF skin); a `skinned` rig is one a model of the theme chain declares; every clip suffix is a weapon type;
a theme `.glb` that is a Git LFS pointer is an error ("run `git lfs pull`"); an item's worn visual key with no model file in the theme chain is a warning that names the missing
model the client draws instead. Since T-224 it also checks the theme contract (THEMES.md §1): variants; every model's `Body_LOD0`, one material, textures ≤ 1024², the
triangle budgets (5k/2k for living models, 1k for worn pieces, 2k for props); a rigged model's skin under `Armature`, the shared humanoid bones and its facing (right
hand on +X); a living model's height (0.1–8 units); a library's rig, required clips (idle, walk, attack, hit, die), clip names and tracks; warnings for see-through
materials, missing outline directions and an origin off the ground. The facts are read from the glTF (`Game.Content/Themes`: `GltfFile`, `ModelFacts`), without Godot. Each finding names the manifest file, the key and the reason. **Placeholder weapons** take their shape from the item's `weapon_type`
(`tools/themegen/Weapons.cs`; themegen reads `content/items`, or `--content <dir>`): a dagger is a short blade, a staff stands up, and so on.

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
| Real-time shadows | Player only by default; off on low settings. One 1024² directional shadow map (4 MB of 32-bit depth, hard edges, 24 units; T-222): one extra draw call (the player) in the shadow pass |
| App memory | ≤ 1 GB |
| Initial install | ≤ 200 MB |
| Network | ≤ 10 KB/s average per client with 50 visible entities |
| Battery | No per-frame work while nothing changes; 30 fps cap in menus |
| Audio | ≤ 24 simultaneous voices; ≤ 48 MB decoded sound effects in memory; music and ambience streamed (proposed — T-803 confirms) |

**Toon look (T-222, §7.9), measured** on an integrated desktop GPU (AMD Radeon), 1280x720 windowed, 30 fps cap, `tools/loadtest --bots 70 --fighters 0.7 --spare-slots 3`
and the client's `--measure 40 --fight` (crowd numbers vary with how many bots stand on screen, so each figure names it):
- *Before* (the plain look and the T-405 placeholders): peak 88 draw calls (29 on screen), triangles peak 12.9k; fully dressed (`--measure-dress` dagger, cap, vest,
  buckler) peak 82, triangles 23.1k; 30.0 fps, worst frame 98 ms.
- *After* (toon, outlines within 10 cells on the nearest 6): peak **97** (37 on screen), triangles peak 57k; fully dressed peak **95** (37 on screen), triangles 83k;
  29.9 fps, worst frame 73–75 ms. The same build with `toon_shading=0`: peak 93 (34 on screen). The new placeholders are rounder (cylinders, domes, eyes), hence
  the triangles; still well inside 150k and the per-model budgets (the pack tests check 8 × LOD0 + 32 × LOD1).
- *Outlines and the counter:* the engine's draw-call monitor did not rise when the scripted run turned three outlines off and on (59 both ways) nor per outlined
  character in a crowd, so Godot's Mobile renderer does not count the overlay pass there. Counting it as one draw each, the worst case is the peak plus
  `outline_max_count` (6): 97 + 6 = 103 at 37 on screen (over §9), 88 + 6 = 94 at the 29 on screen of the before run. That is why the default count is 6 and not 12; a phone measurement
  (T-803) settles it.
- *Equip frame* (`run-e2e.ps1 -Run items -Windowed`): worst frame from the press of Equip until dressed 58.9 ms before (T-405 build), 84.2 ms with the toon models
  and the merge still on the main thread (the re-dress alone 81.9 ms), **35.1 ms** with the merge on a worker (frames 33 ms at the cap; no frame stands out).
- None of it has run on a phone (ADR-002 row 112). The toon shader's cost there, the overlay draw count and the shadow map are on the T-803 checklist.

## 10. Networking on mobile

- **TLS required:** `wss://` everywhere except local dev. Cleartext `ws://` is allowed only in
  debug builds, and **the client enforces that itself** (T-216): Godot's WebSocket uses native sockets, which Android's network-security
  config and iOS ATS do not police. A release build shows a refusal screen for a `ws://` or invalid server address and opens no socket;
  the allowance is set once at start-up from the engine's debug-build flag and cannot be changed by `client_settings.json` or the command line
  (`TransportPolicy`, MOBILE_BUILD §8.4). The iOS preset still states ATS explicitly; no Android network-security config is shipped (it would need the Gradle export and would not cover these sockets).
- **Disconnects are normal.** When a socket drops or the app is backgrounded, the server keeps
  the character in the world for a grace period (`grace-period-ms`, server config, default 60 s), then saves (T-702) and logs it out.
- **Resume** (T-210): the client reconnects with a resume token and receives a full snapshot, then deltas (lifecycle step 6 below).
- **Statuses on the wire** (T-501). Every view record carries the entity's active statuses, `statuses: [{id, stacks, left_ms, debuff}]` (left out when none;
  `debuff` only when true; `left_ms` = simulated ms left at the message's tick), and a delta's `changed` carries the whole list when one starts, stacks, is
  refreshed or ends (empty when none is left). Never per tick: the client counts down itself. Never a chance, a number, an interval, tags, `prevents_*` or
  the applier (the bundle already says buff or debuff; it is repeated so a marker needs no lookup). The client shows the own character's statuses as icons
  under its bars (the bundle's `icon`, else the first letter of the name, §7.7), a dot above every other character with any (one multimesh: one draw call),
  and plays the status `vfx` key once when one starts. Status damage arrives in `hits` with the result `status`: a number, no swing. A monster's cast is an
  event on the server only (`SkillCastStarted`); cast bars are T-502.
- **Snapshot / delta shape** (T-204, T-307, T-401): a view record is `id, kind, display_key, cell, facing, next, speed, hp, dead` (`next` and
  `speed` only while walking, `hp` only for entities that fight, `dead` only when true; a ground item adds `quantity` and `mine`; a player adds `weapon`, its
  weapon type, and `worn`, the item ids it wears that have a worn visual, left out when empty: the client finds the visuals in its bundle for T-403, and never gets stats); a delta is
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
  | `pick_up` | C→S | `target` (a ground item the client sees; it walks there first; may come back `rejected` with an "Item reasons" term) |
  | `use_item` | C→S | `entry` (an inventory entry id) |
  | `equip` | C→S | `entry`, optional `slot` (an "Equipment slots" term; without it the server picks) |
  | `unequip` | C→S | `slot` |
  | `drop_item` | C→S | `entry`, `quantity` (onto the character's cell, for anyone) |
  | `insert_card` | C→S | `entry` (the equipment entry, carried or worn), `card` (the card entry); for good (T-402) |
  | `refine` | C→S | `entry` (an equipment entry, carried or worn); the outcome comes back as `inventory_changed` (`refined`) then `refine_result` (T-402) |
  | `use_skill` | C→S | `skill`, optional `level` (up to the learned level), `target` (an entity id in view) or `cell` (ground); a request, it may come back `rejected` (T-502) |
  | `learn_skill` | C→S | `skill` (spend one skill point) (T-502) |
  | `change_job` | C→S | `job` (T-502) |
  | `raise_stat` | C→S | `stat` (a primary stat id), `amount` (steps of +1, default 1, at most 99) (T-503) |
  | `talk` | C→S | `target` (an NPC entity in view): the server walks the character into `dialogue.talk_range` and starts its dialogue; a request, it may come back `rejected` with a "Dialogue reasons" term (T-601) |
  | `dialogue_choice` | C→S | `index` (a shown choice's index from `dialogue`; 0 at a node without choices) (T-601) |
  | `dialogue_close` | C→S | none: end the dialogue and any service it opened (T-601) |
  | `shop_buy` | C→S | `item` (a stock item id), `quantity`: buy from the open shop; the price is the server's. Comes back `inventory_changed` (`shop_buy`, with the new `currency`) or `rejected` (`intent` `shop_buy`, `reason` an "Item reasons" term, `item`) (T-603) |
  | `shop_sell` | C→S | `entry` (a carried inventory entry), `quantity`: sell to the open shop at the item's sell price. Comes back `inventory_changed` (`shop_sell`) or `rejected` (`intent` `shop_sell`, `entry`) (T-603) |
  | `abandon_quest` | C→S | `quest`: drop an active quest, its progress is lost; may come back `rejected` (`not_active`, with `quest`) (T-602) |
  | `welcome` | S→C | `version`, `id` (your entity), `map`, `tick_rate`, `content` (the server's content hash, §5.4), `token` (a new resume token) |
  | `snapshot` | S→C | `tick`, `map`, `id`, `entities` (view records), `vitals` (own `hp`, `max_hp`, `sp`, `max_sp`). Replaces the view; a new `map` means you changed maps |
  | `delta` | S→C | `tick`, `appeared` (records), `changed` (id + changed fields; `"next":null` = stopped), `disappeared` (ids), `hits` (`[attacker,target,amount,"hit"|"crit"|"miss"]`), `casts` (`[caster,"skill",ms]`: a cast with a cast time began in view; never costs or numbers), `cast_ends` (`[caster,"skill","done"|"cancelled"|"interrupted"]`), `vitals` (when changed); empty ones left out |
  | `rejected` | S→C | `intent` (`move`, `attack`, an item intent, or `use_skill`, `learn_skill`, `change_job`, `raise_stat`, `talk`, `dialogue_choice`), `reason` (a `MoveRejected`, "Attack reasons", "Item reasons", "Skill reasons" or "Dialogue reasons" term), `cell` (move), `target` (attack, `pick_up`, `use_skill`, `talk`), `entry` (`use_item`, `equip`, `drop_item`; the equipment entry for `insert_card`, `refine`), `slot` (`equip`, `unequip`), `skill` (the skill, or for `change_job` the job), `quest` (a refused quest start, `intent` `dialogue_choice`, or `abandon_quest`; a "Quest reasons" term, T-602), `item` (a refused `shop_buy`, T-603) |
  | `attack_stopped` | S→C (owner) | `target`, `reason` (an "Attack reasons" term) |
  | `exp` | S→C (owner) | `base_level`, `base_exp`, `base_next`, `job_level`, `job_exp`, `job_next` (`*_next` = EXP the level needs, 0 at the cap) |
  | `level_up` | S→C (owner) | `base_level` or `job_level` (one message per level reached) |
  | `you_died` | S→C (owner) | `killer` (entity id, if any); send `respawn` to come back |
  | `respawned` | S→C (owner) | `map`, `cell`, `hp`; a snapshot of the respawn point follows |
  | `inventory` | S→C (owner) | `rev`, `slots` (most entries), `weight`, `weight_limit`, `load` (a "Weight states" term), `currency` (T-602), `entries` (`[{id, item, quantity, slot?, cards?, refine?}]`, by id; `cards` and `refine` only when the equipment has any, and only ever to the owner). After every snapshot (join, resume, warp, resync); it replaces what the client had |
  | `inventory_changed` | S→C (owner) | `rev` (one more than the last), `cause` (an "Inventory changes" term), `item`, `weight`, `weight_limit`, `load`, `currency` (T-602), `entries` (added or changed, as they are now), `removed` (ids); empty lists left out. A client that sees a gap in `rev` keeps what it has until the next `inventory` |
  | `refine_result` | S→C (owner) | `entry`, `item`, `result` (a "Refine results" term: `success`, `keep`, `down`, `break`), `refine` (the level now; what it had when it broke) |
  | `dialogue` | S→C (owner) | `npc` (entity id), `speaker` (an `npc.*` id: name, title and portrait are in the display bundle), `portrait` (a key overriding the speaker's, if any), `text`, `choices` (`[{index,text}]`, the shown ones), `more` (only with no choices: the dialogue goes on after this node) (T-601) |
  | `dialogue_ended` | S→C (owner) | `reason` (a "Dialogue end reasons" term) (T-601) |
  | `service_opened` | S→C (owner) | `service` (a "Service ids" term), `npc` (T-601) |
  | `shop` | S→C (owner) | `npc`, `shop` (the `shop.*` id), `max_quantity`, `stock` (`[{item, price}]`, in display order), `sell` (`[{item, price}]`: what a shop pays per unit for every item it buys). Sent right after `service_opened` for the `shop` service (T-603) |
  | `quest_log` | S→C (owner) | `active` (`[{quest, step, objectives:[{type, target?, have, need}], held?}]`, by id; `step` indexes the bundle's step texts), `completed` (quest ids), `offer`, `turn_in` (NPC ids for the markers). After every snapshot and whenever the markers change; it replaces what the client had (T-602) |
  | `quest` | S→C (owner) | `change` (a "Quest changes" term), `quest`, `state` (the quest as it is now, the shape of an `active` entry; absent for `completed` and `abandoned`) (T-602) |
  | `skills` | S→C (owner) | `job`, `points` (unspent), `levels` (`[["skill.x",2],...]`), `cooldowns` (`[["skill.x",ms left],...]`), `jobs` (the jobs `change_job` would accept now). After every snapshot, a learn, a job change and a level (T-502) |
  | `stats` | S→C (owner) | `points` (unspent stat points), `stats` (`[["str",3,2],...]`: stat id, base value, the cost of its next +1, 0 at the maximum), `derived` (`[["atk",12],...]`: `DerivedStats.Panel`, floored from the sheet). After every snapshot, a raise, a stat reset, a level, a job change, a learned skill, a skill reset and a gear change (T-503). The client never computes a cost or a derived value |
  | `cooldown` | S→C (owner) | `skill`, `ms`: the skill went on cooldown when its cast ended (T-502) |
  | `pong` | S→C | `seq` |
  | `bye` | S→C (last) | `reason` (VOCABULARY "Bye reasons"), followed by the close frame |

  A view record is `{"id":7,"kind":"monster","display_key":"monster.x","cell":[3,4],"facing":"e","next":[4,4],"speed":3.51,"hp":0.37}`;
  a ground item is `{"id":9,"kind":"item","display_key":"item.x","cell":[4,5],"facing":"s","quantity":2,"mine":true}`; a player is
  `{"id":42,"kind":"player","display_key":"job.wanderer","cell":[12,12],"facing":"n","hp":1,"weapon":"dagger","worn":["item.bone_dirk"]}`; a monster with a status adds
  `"statuses":[{"id":"status.poison","stacks":1,"left_ms":7400,"debuff":true}]`.
- **Session lifecycle:**
  1. The client connects and must send `hello` within `hello-timeout-ms`.
  2. The server checks the protocol version, then the content hash (T-209): a `content` that differs from the server's ends in `bye update_required`; a missing one is accepted in development (logged as a warning) and refused elsewhere. Then it checks the dev login. The dev login is development only; any other environment refuses it. A `hello` with `resume`
     instead of `name` goes to step 6.
  3. The server queues the character with `GameWorld.Spawn(..., initialize)` at `balance.yaml` `new_character`. On the start
     instance's tick, the initializer sets its stats (from the stats pipeline), its display key (the job id) and its view sink, and
     queues `welcome`. The AOI then sends the first `snapshot` in phase 5 of that tick, and deltas follow.
  4. `move`/`stop`/`attack`/`stop_attack`/`respawn` and the item intents are parsed and checked on the socket thread (shape; a cell inside the current map; an
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
