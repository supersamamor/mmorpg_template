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
  An observer whose window of buckets had no change this tick costs a few array reads; otherwise its visible set is gathered
  and merged with the last one (O(visible)). It gets a **snapshot** when it starts observing, arrives on a map (warps included)
  or sends `RequestSnapshot`, and otherwise at most one **delta** per tick, none when nothing in view changed. The sink
  (`IViewSink`, implemented by the gateway) is called on the tick thread: it copies or serializes and queues, never does I/O;
  one that throws is logged and detached.
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
- **Movement system** (`Game.Server/World/MovementSystem`): `MoveTo` and `StopMove` intents; the last intent per entity in a tick wins. The path is searched on the tick from the entity's current cell and a rejected `MoveTo` (reason term in `MoveRejected`) leaves a move in progress running. Each tick a walking entity advances by 100 ms at its `move_speed` (read from its stat sheet when the move starts and after every tick in which a cell was reached; an entity with no speed cannot move), in entity id order. **Entities do not block each other in v1**: they may share a cell. Reaching a cell that has a warp (only by arriving, never by spawning or standing there) stops the entity and requests the warp. Other systems see movement only through `Entity.Cell` / `Facing` and the events `MovementStarted`, `CellReached`, `MovementStopped`, `MoveRejected`.
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
Singleton root files: `FormulaSetDef` (`formulas.yaml`), `BalanceDef` (`balance.yaml`: stat/skill point rules, stat range, damage and chance clamps, path search limits). `AppearanceDef` (`appearance.yaml`: character-creation options, cosmetic only, §7.5.1).

### 5.3 Maps
A map has three parts that share its id:

| Part | Where | Used by |
|------|-------|---------|
| Visual scene | Godot 3D scene in the theme, by asset key (`scene.map.starter_town`) | client |
| Logic grid | `content/maps/<name>.grid.json`, generated, committed | server, validator |
| Metadata | `content/maps/<name>.yaml` (`MapDef`: name, scene key, music key, grid file) | all |

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
  has `count` slots, so it never holds more. On `EntityDied` (combat) the spawner despawns the monster and refills the slot
  `respawn_seconds` of simulated time after the tick of the death, anywhere in the area. Respawn timers always run.
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
- **Per tick:** intents (`AttackTarget`, `StopAttack`, `Respawn`) → vitals clamp → the monster `AttackRequested` events raised last tick,
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
  is despawned and respawned by the spawner. A player stays dead on its cell until `Respawn`, then comes back with
  `death.respawn_hp_percent` of max HP at the respawn point (the `new_character` start cell for now); on death it loses
  `death.exp_penalty_percent` of its level's requirement, never more than its EXP (no level loss).
- **EXP.** Per monster, the HP each player took since its last full-HP moment. On death the players still on the map share `base_exp` /
  `job_exp` in proportion (largest remainder; ties to the first hitter; the shares add up exactly) through `ProgressionRules`, which raise
  `ExpGained` and `PlayerProgressed` (`LevelUp` / `JobLevelUp`); the sheet takes the new levels and job bonuses. Progression is kept per
  character in an `IPlayerProgressionStore` shared by all instances (in memory until persistence).
- **Drops.** Each `drop_table_ids` table rolls every entry on its own (`chance`, then a quantity in `min..max`) with the instance random.
  Each drop is an `EntityKind.Item` entity on a random walkable cell within `loot.spread_radius` of the death cell (`ItemDropped`); for
  `loot.priority_ms` only the top damager may take it (`LootRules.MayPickUp`), and after `loot.lifetime_ms` it is despawned
  (`ItemExpired`). Ground items are not shown to clients yet (the AOI skips them).

## 7. Client

### 7.1 Platforms & rendering
- Android 8.0+ (API 26), arm64-v8a. iOS 15+, arm64. Landscape only.
- Godot **Mobile** renderer (Vulkan / Metal). The **Compatibility** renderer (OpenGL ES 3) is
  the fallback for Android devices with poor Vulkan drivers — T-005 decides when it is used.
- Desktop builds use the same renderer and input code (mouse acts as touch).

### 7.2 Camera
Fixed-pitch 3/4 perspective that follows the player. Pinch (or scroll wheel) zooms within a
clamped range; optional rotation in 45° steps. Camera numbers are client config, not content.

### 7.3 Input
- Tap ground → raycast → cell → `MoveTo` intent. A tap marker shows at once; the character
  moves when the server confirms (≤ 1 tick).
- Tap an entity → target it (attack / talk / pick up, by entity type).
- Hotbar buttons → `UseSkill`. Ground-target skills: pick the skill, then tap a cell.
- Long-press → info / context menu. A virtual joystick is backlog.

### 7.4 UI
Godot Control nodes anchored to the device **safe area** (notches, home indicator). Minimum
touch target 48 dp (Android) / 44 pt (iOS). A UI-scale setting. Text is never baked into textures.

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
`themes/<theme>` is mounted into the Godot project at `res://themes/<theme>` (a directory
junction/symlink in dev; copied and packed at export). Downloading themes after install is a non-goal.

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
| Character | ≤ 5k triangles, 1 material, ≤ 1024² texture |
| Animated characters on screen | 40 at full rate; further ones at reduced animation rate |
| Real-time shadows | Player only by default; off on low settings |
| App memory | ≤ 1 GB |
| Initial install | ≤ 200 MB |
| Network | ≤ 10 KB/s average per client with 50 visible entities |
| Battery | No per-frame work while nothing changes; 30 fps cap in menus |
| Audio | ≤ 24 simultaneous voices; ≤ 48 MB decoded sound effects in memory; music and ambience streamed (proposed — T-803 confirms) |

## 10. Networking on mobile

- **TLS required:** `wss://` everywhere except local dev. Cleartext `ws://` is allowed only in
  debug builds (Android network-security config, iOS ATS exception).
- **Disconnects are normal.** When a socket drops or the app is backgrounded, the server keeps
  the character in the world for a grace period (`grace-period-ms`, server config, default 60 s), then saves (T-702) and logs it out.
- **Resume** (T-210): the client reconnects with a resume token and receives a full snapshot, then deltas (lifecycle step 6 below).
- **Snapshot / delta shape** (T-204): a view record is `id, kind, display_key, cell, facing, next, speed` (`next` and
  `speed` only while walking); a delta is `appeared` (records), `changed` (id + changed fields only), `disappeared` (ids), each
  ordered by entity id. A walker produces one change per cell reached, not per tick; the client interpolates from `cell` to `next`.
- **Latency:** the client renders ~2 ticks behind the newest snapshot and interpolates.
  It tolerates cellular jitter and does not predict combat.
- **Bandwidth:** JSON first. Switch to MessagePack if measured traffic exceeds the §9 budget. Measured (T-205): 50 loopback
  clients walking in each other's view receive 8.1 KB/s each on the wire (payload + WebSocket header).
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
  | `stop` | C→S | — |
  | `ping` | C→S | `seq` |
  | `logout` | C→S | — (leave now: `bye logout`, the character is removed on the next tick, no grace period) |
  | `welcome` | S→C | `version`, `id` (your entity), `map`, `tick_rate`, `content` (the server's content hash, §5.4), `token` (a new resume token) |
  | `snapshot` | S→C | `tick`, `map`, `id`, `entities` (view records). Replaces the view; a new `map` means you changed maps |
  | `delta` | S→C | `tick`, `appeared` (records), `changed` (id + changed fields; `"next":null` = stopped), `disappeared` (ids); empty lists left out |
  | `rejected` | S→C | `intent` (`move`), `reason` (a `MoveRejected` term), `cell` |
  | `pong` | S→C | `seq` |
  | `bye` | S→C (last) | `reason` (VOCABULARY "Bye reasons"), followed by the close frame |

  A view record is `{"id":7,"kind":"monster","display_key":"monster.x","cell":[3,4],"facing":"e","next":[4,4],"speed":3.51}`.
- **Session lifecycle:**
  1. The client connects and must send `hello` within `hello-timeout-ms`.
  2. The server checks the protocol version, then the content hash (T-209): a `content` that differs from the server's ends in `bye update_required`; a missing one is accepted in development (logged as a warning) and refused elsewhere. Then it checks the dev login. The dev login is development only; any other environment refuses it. A `hello` with `resume`
     instead of `name` goes to step 6.
  3. The server queues the character with `GameWorld.Spawn(..., initialize)` at `balance.yaml` `new_character`. On the start
     instance's tick, the initializer sets its stats (from the stats pipeline), its display key (the job id) and its view sink, and
     queues `welcome`. The AOI then sends the first `snapshot` in phase 5 of that tick, and deltas follow.
  4. `move`/`stop` are parsed and checked (shape, cell inside the current map) on the socket thread and queued as `MoveTo`/`StopMove`
     wherever the character is now. Warps are followed through the world's entity directory. Each session has a token-bucket rate
     limit: messages over it are dropped and counted, and sustained flooding ends in `bye rate_limit`.
  5. Malformed JSON, an unknown type, an oversize message, a wrong version, a message before `hello`, or silence longer than
     `idle-timeout-ms` all end in `bye <reason>` and the close frame.
  6. **Grace period and resume** (T-210). Every `welcome` carries a **resume token** for the character.
     - **The connection ends without `logout`** (the client closed or vanished, idle `timeout`, `too_slow`, a protocol error such as
       `malformed` or `rate_limit`, `server_error`): the character is **not** removed. It is stopped (`StopMove`), its view is detached
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
       rotated** (every successful resume returns a new one and the old one stops working). The server keeps only its SHA-256 and
       compares in constant time; tokens never appear in logs (at most a 6-hex-digit fingerprint of the hash). Tokens live in server
       memory only: **a server restart invalidates every token** (durable sessions are T-701).
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
- **Back-pressure:** the sink serializes on the tick thread into a bounded per-session queue, and the socket drains that queue. When
  the queue is full, queued views are dropped and later deltas are skipped. Once the queue has drained, the session asks for a fresh
  snapshot (`RequestSnapshot`). A client that stays behind for `slow-client-timeout-ms` gets `bye too_slow`.

## 11. Security baseline
- Server validates every intent (range, cooldown, SP, line of sight, ownership).
- Rate-limit intents per session.
- Passwords hashed (Argon2/BCrypt). Session tokens, not credentials, on the socket.
- Item operations in DB transactions; item instances have unique ids (dupe auditing).
- The client is untrusted (CLAUDE.md rule 11); the client bundle holds display data only.
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
