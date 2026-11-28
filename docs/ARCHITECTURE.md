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
                   JSON Schema generation, client bundle export. Hot-reload in dev (§5.5).
  Game.Server      Host: tick loop, world/map instances, AOI, entity systems,
                   WebSocket gateway, persistence (PostgreSQL), event bus,
                   internal admin API (§12).
  Game.Admin       Internal web panel for live operations (§12). Staff only, never shipped.
  Game.Tools       CLI: `validate`, `schema`, `stats`, `export-client`, `editor` (§5.6).
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
  admin API's `GET /admin/v1/status` reads it (T-804, §12.6).
- **One content boot.** `ContentBoot` (`Game.Content/Loading`) loads the content and runs every validation rule, including the theme
  manifest's asset keys and audio data. The server calls it at start and refuses to run on an error, printing the same lines
  `Game.Tools validate` prints; `validate` adds only the JSON Schema check (YAML documents and `maps/*.grid.json`) and the map scene check (§5.3).
- **Map instances:** each map runs in its own instance; warps move a character between instances.
- **Persistence:** player state is saved on an interval, on map change and when a character leaves ("Saving characters" below, T-702). Content is never in the DB.
  **Base** (T-701, `Game.Server/Persistence`): EF Core with Npgsql. The connection string is the environment variable `GAME_DB_CONNECTION` (not an option: those
  show up in process listings); it is never committed or logged, and database error details (which quote row values) are off. Without it a development server runs with
  the dev login only; any other server refuses to start. At start the applied migrations are compared with the build's: a missing or older schema is migrated in
  development and refused elsewhere (`Game.Server --migrate` applies it and exits), and a newer one is always refused. A statement may take 5 s while playing and 10 minutes in a migration (T-710: making a database on a busy machine took longer than 5 s). Every operation opens its own `GameDb` through
  `GameDatabase.Open`, which throws on a tick thread (`TickThread`), so database work cannot run there. Tables: `accounts` (`id`, `username`, `password_hash`,
  `created_at`, `password_changed_at`), `account_sessions` (`token_hash`, `account_id`, `created_at`, `expires_at`), `characters` (`id`, `account_id`, `slot`, `name`,
  `name_key`, `job_id`, `created_at`, `deleted_at`). Unique indexes decide: `username`; `name_key` over **every** row (a deleted character keeps its name); and
  `account_id, slot` over characters that are not deleted. A character's `id` is a GUID, separate from its runtime `EntityId`; the saved state (T-702) and T-705's data hang off it.
  A new table is one row class, its block in `GameDb.OnModelCreating` and `dotnet ef migrations add <Name> --project src/Game.Server --output-dir Persistence/Migrations`.
- **Saving characters** (T-702; `Game.Server/World/Saving`, `PostgresCharacterStore`). An account's character is loaded before it enters the world and saved as it plays and leaves. Dev-login
  characters, and every character of a server without a database, live in memory only. The in-memory stores (`IPlayerProgressionStore`, `IInventoryStore`, `ISavePointStore`) stay what the systems
  use: a loaded character is put into them in its spawn's initializer, before any system sees it, and copied out of them whole.
  - **What is saved.** Ids and numbers only:

    | state | saved | why |
    |-------|-------|-----|
    | map, cell, facing; HP and SP | yes | HP 0 (dead) comes back as if it had pressed respawn: at its save point, else the map's respawn point, with `death.respawn_hp_percent`; the EXP penalty was paid at death |
    | save point; levels, EXP, job, points, stats, skills; unlocked jobs | yes | |
    | active quests (step, counters, turn-in number), completed quests, the turn-in counter, rewards owed but not paid yet | yes | see "Nothing is split" |
    | inventory entries (id, item, quantity, slot, cards, refine), currency, the turn-ins the bag has paid | yes | |
    | skill cooldowns (row 214), item cooldowns | yes, as time left; time away counts | signing in again must not reset them |
    | the autopilot's daily usage and when its day began (row 293) | yes | likewise |
    | what the content has no place for ("Loading") | yes | nothing disappears |
    | an autopilot run; statuses; an open dialogue, service or shop; a cast, auto-attack, walk or pending pick-up; "in combat" | no | they belong to a moment and a map; statuses end on every map change too. An out-of-combat reset is possible right after signing in: accepted, the character is out of the fight |
    | owned outfits (each once), what each outfit slot shows, the hide switch (T-708) | yes | an id the content has lost is kept and ignored, and comes back with it |
    | stat sheet, weapon, look, weight, load, NPC markers, views, the inventory `rev`, a held quest's reason | no | worked out again from the rest |
    | items on the ground | no | a crash loses them (see "Between characters") |

  - **Tables.** `server_runs` (`id` from an identity sequence, `started_at`, `seen_at`, `ended_at`), `character_states` (`character_id`, `claim`, `held_by` → run, `version`, `saved_at`, and the state: one
    column per number or id, `jsonb` for stats, skills, quests, cooldowns and what is set aside; `version` 0 = never saved), `character_items` (`id` primary key over **all** characters,
    `character_id`, `item_id`, `quantity`, `slot`, `cards`, `refine`, `aside`; one item per worn slot), and (T-708) `character_outfits` (`character_id`, `outfit_id`; the primary key keeps each outfit once) with `outfit_slots` (`jsonb`) and `outfits_hidden` on
    `character_states`. A check keeps the currency from going negative. T-705 and T-905 add tables or columns beside these.
  - **Item ids** are permanent: the run's number, which the database gives to no other run, shifted left 40 bits, plus a counter from 1 (`CharacterSaves.NextItemId`). No restart repeats one, and a
    tick thread gets one without the database. An entry keeps its id for life; equipment also keeps it from hand to hand.
  - **A copy** is made on the tick thread that holds the character, in phase 5 (`SaveSystem`), after every system has run and every event of the tick is out, so it is always consistent; the
    entries in it are immutable. It costs 5 µs and 3.4 KB for a character with 40 entries, 8 skills and 35 quests; 100 due in one tick cost 0.5 ms (max 2 ms) with the database answering
    nothing; with none due, 2 to 5 µs. The database work runs on up to 4 tasks of its own (100 saves of 40 items: 0.5 s; `GameDatabase.Open` refuses a tick thread).
  - **When:** every `--save-interval` seconds (60; each character has its own phase), after arriving on a map (entering the world included), in the tick it puts an item down, and last when it leaves:
    logout, end of the grace period, an account switch, shutdown. A leaving character stays one to three ticks longer, until nothing it began is half done.
  - **One transaction per save**, with a number: `version` only goes up, so an older copy never overwrites a newer one and the same save twice is harmless (an answer lost after the commit). **Only the
    newest claim saves:** entering the world counts `claim` up and sets `held_by`; a save whose claim is not the stored one is refused and that server has lost the character. Another server may not claim
    it while the holder's run is alive (not ended, heard of within 20 s by the database's clock). So two servers, or a late save, never both win. After a crash the next server can enter its characters once those 20 s are over.
  - **Nothing is split.** Some things span ticks. A quest turn-in keeps its number (`QuestProgress.Token`, per character, never reused) until the step has advanced, and the bag keeps the numbers it has
    paid (`InventoryState.SettledTurnIns`), set in the step that pays; on every arrival (a warp, a load) a paid turn-in advances and is never asked again (row 264). A completed quest's other rewards are owed
    (`ProgressionState.OwedRewards`) until combat pays them on whatever map the character is. So a copy at **any** tick of a turn-in comes out right. An item's or a dialogue's effects follow a tick or
    two after its price: copies, and leaving, wait for them (at most 10 ticks).
  - **Between characters: the giver's save lands first, or together.** In the tick a saved character puts an item down, `CharacterTicket.PutDown` gives the ground item a link to its next copy (the first one
    without the item); whoever picks it up takes the link (`DependOn`). `CharacterSaves` writes a copy only when every copy it links to has landed, or in one transaction with those copies (and theirs). A crash at
    any point therefore finds the item with the giver, with the receiver, or (put down and saved, not yet landed with the receiver) nowhere: **it may be lost, never doubled.** It holds for stacks too,
    which the primary key alone would not catch. T-903 and T-905 use the same two calls, on both sides for a trade: in the swap's tick each side's `PutDown`, then each `DependOn` the other's link,
    so each copy waits for the other's and `CharacterSaves` writes the two in one transaction (§6.10; no `Persistence/**` change was needed). The primary key is the second line: a save with an item that is another character's is
    refused whole and reported (`DUPLICATED ITEMS`).
  - **A guild founding rides the save** (T-904, §6.11): the inventory system takes the cost on the tick and puts a `StaffNote.ForGuild` on the ticket (the road T-806's staff changes use); the first save
    without the cost founds the reserved guild in its transaction (`UPDATE guilds SET founded_at`), so the cost and the guild land together or not at all, and a save that lands twice founds nothing twice.
  - **A guild storage move rides the save too** (T-905, §6.11): the storage's side is first a **hold** in the database (`guild_storage_holds`); the inventory system moves the bag's side on the tick
    and puts a `StaffNote.ForStorage` on the ticket; the first save with the bag's side settles the hold in its transaction (`PostgresGuildStorageStore.LandAsync`: a deposit's entry and currency
    go into the storage, a withdrawal's hold goes, a log line is written). A hold whose save never lands is undone by the server when the note settles as not landed, else in the transaction of
    the character's **next claim** (`ClaimAsync`), when no older stay of it can save any more. No new dependency between saves: the storage's side is not another character's.
  - **A lost character** (claimed elsewhere, deleted, or with a duplicated item) is not saved any more: its session gets `bye save_lost` and it leaves. Whoever holds something it gave away after its last landed
    save is lost with it (for them it is a crash), and what it put down can no longer be picked up.
  - **Loading.** Claimed and read in one transaction, which also re-checks that the character is not deleted (row 289; a held character cannot be deleted: both take the same row lock). Then `CharacterLoader`
    checks it against the content; every change is logged. A removed **item** is set aside with its id, cards and refine, and returns to the bag when the content has it again; a removed **card** stays
    in its socket and does nothing; a stack over `max_stack` splits; a worn item that may not be worn any more goes to the bag. A removed **skill**, or levels above a lower `max_level`, give their points back. A removed
    **job** leaves the start job at job level 1, with a record of what it was. A removed **quest** (active or completed) or unlocked job is set aside and comes back with it; a quest whose step changed starts
    that step over. A removed **map** moves the character to its save point, else the start; a **blocked cell**, to the map's respawn point; a save point that is gone is cleared.
  - **Failure.** A load that fails refuses the login (`bye load_failed`) and changes nothing; a failure is never read as a new character. A failed save is tried again for as long as the server runs (0.5 s doubling
    to 15 s) with whatever is newest by then, and the character plays on: the player sees nothing. After 30 s it is **at risk** (logged once as an error, counted in `CharacterSaves.Stats`). A character whose last save
    has not landed cannot be entered again: the login waits up to 3 s, then `bye save_pending`.
  - **Shutdown.** Every character is asked to leave, the instances go on ticking, and the server waits up to `--save-shutdown-wait` seconds (20) for the saves, without backoff. Any that failed are listed
    (`NOT SAVED`) and the exit code is 3.
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
Singleton root files: `FormulaSetDef` (`formulas.yaml`), `BalanceDef` (`balance.yaml`: stat/skill point rules, stat range, damage and chance clamps, movement and path search limits, AOI bucket size and view radius, new-character start, monster AI timings). `AppearanceDef` (`appearance.yaml`: character-creation options, cosmetic only, §7.5.1). `OutfitDef` (`content/outfits/`, T-708: looks only, §7.5.2).

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
- **Format:** `format_version` (now `8`; 2 added the sound keys, 3 the appearance options, 4 item `card_slots` / `refinable` and the `refine` steps, 5 dropped the dialogue graphs and an NPC's dialogue id: the server runs dialogue, T-601; 6 added `currency_name`, T-602; 7 added the looks of jobs and the start job, T-706; 8 added the outfits, T-708) is the first field. The client refuses a bundle with a version it does not know. Bump it
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
| Jobs | id, name, description, parent job, usable weapon types, model, icon, skill tree (skill, max level, required skills), look: cloth colour and pieces (model, gender, slot; T-706) | stat bonuses, change requirements, EXP table id |
| Statuses | id, name, description, buff or debuff, icon, vfx | effects, tick interval, stacks, tags, `prevents_*` flags |
| Monsters | id, name, model, sound keys (aggro, hit, die) | level, race, size, element, stats, attack range, EXP, drop table links, AI |
| NPCs | id, name, title, model, portrait | dialogue id |
| Dialogue | — | the whole graph: nodes, texts, conditions, actions and quest ids (the server runs it and sends one node at a time, T-601) |
| Quests | id, name, description, giver NPC, display text of each step | prerequisites, objectives and counts, rewards, repeatable flag |
| Maps | id, name, scene, music, ambience key, width, height, cell flags (`walkable`, `blocks_los`; base64, as in the grid file), warps (cell, target map, target cell), NPC placements, zones | `spawns`, `source_scene_hash` |
| Outfits (T-708) | id, name, description, icon, the colour roles it takes, pieces (slot, model, gender) | — (an outfit has nothing else: no stats, effects, requirements, prices or drops) |
| Appearance | genders (body model, faces, hairstyles, defaults), skin tones and hair colours: ids, names, asset keys, colours; the job a new character starts in (the creation preview wears its look, T-706) | — (cosmetic only) |
| Elements, stats | element ids and stat ids, for labels | element multipliers, derived-stat formulas |
| Balance | `currency_name` (the bag shows the balance with it, T-602) | every other number of `balance.yaml` |
| — | — | drop tables and chances, `formulas.yaml`, `balance.yaml`, EXP tables |

The leak tests (`tests/Game.Tests/ClientBundle`) fail if a key names server-only data (`drop`, `chance`, `spawn`, `formula`, `ai`, `exp`, `respawn`, `hp`, `atk`, ...),
if a server-only value (formula, scene hash, `drop.*` / `exp.*` id) appears, or if server-only fields set to unique values change a byte of the output.

### 5.5 Hot reload in development (T-801)

`Game.Server --hot-reload` (off by default) watches `content/` and the theme's `asset-manifest.json` and swaps a valid change into the running server
without a restart. How to use it: [CONTENT_EDITING.md](CONTENT_EDITING.md).

- **Who may.** A debug build (`dotnet run`), `--environment Development`, and a gateway on a loopback address. Anything else (a release build, another
  environment, `--net-bind-address 0.0.0.0`) refuses the option at start, with the reason, and the server does not start (exit code 1; `HotReloadGate`).
  The option is never on in production, and the server never writes a content file.
- **Loading.** 300 ms after the files go quiet (`ContentReloader`; an editor's `*.tmp` files are ignored), the whole set is loaded and validated off the tick
  threads by `ContentBoot`, exactly as at boot: every rule, every cross-reference (hard rule 5), the theme's asset keys. Then the systems' content is built
  (`ServerContent.Build`, which runs the boot checks of the movement, AI, AOI, combat and inventory numbers). A set with any error is **refused**: the errors are
  logged as `validate` prints them and the running content stays. A set whose content is the same (a comment, white space) does nothing.
- **What cannot reload (v1)** refuses the whole reload with a plain `NEEDS A RESTART` line naming it, so what runs is always one validated set, never a mix
  (`HotReloadRules`): map grids and map definitions (instances are built on them at start); removing an item, skill, job, quest or outfit (a playing character may
  hold it, and only a load sets such things aside, §3 "Loading"); `appearance.yaml` and the `balance.yaml` sections `aoi`, `characters`, `guild`, `inventory`,
  `new_character` and `party` (read once at start outside the tick: the AOI grids, the account, guild and party services, the gateway's own inventory messages,
  and the new characters' start, which every login must see the same).
- **The gateway reads the live set** (`LiveContent`; scope widened by the lead on 2026-11-28): its own-player `exp`, `skills`, `stats`, `quest_log` and `wardrobe`
  messages (`OwnProgress`) and the stat sheets of logins (`NewCharacters`) take one snapshot of the content in effect per message or login, so jobs, skills,
  quests, EXP tables and outfits reload live and no answer mixes two sets.
- **The swap** (`MapInstance.ApplyReload`): the new set is handed to every instance, and each swaps it in at the start of its next tick, before that tick's intents,
  all at once for the instance (instances may be up to one tick apart). Per kind:

  | What | On a swap |
  |------|-----------|
  | numbers in definitions, formulas, the other `balance.yaml` sections, drop tables, statuses, dialogue, shops, refine, elements | apply from that tick |
  | monsters | **every** monster is removed (not killed: no EXP, no drop) and the map's spawns are filled again in the same tick with the new definitions; summons and corpses go. Every monster, not only changed ones: a monster is built from many definitions (skills, statuses, drops, formulas), and respawning all is the one rule that is never half stale. A monster whose definition vanished (only one that no map spawns can) is gone and not spawned again |
  | NPCs | removed and placed again from the grid with the new definitions; open dialogues end (`warped`) |
  | players | stay connected and in the world. Each is taken off the map and put back on its cell as a same-map warp (`EntityLeftMap` / `EntityEnteredMap`, reason `warp`), with a stat sheet built from the new content; worn items and passives go back on it from the new definitions, HP and SP stand (a dead player stays dead), statuses end. What belongs to a moment ends as on a map change: a cast or auto-attack, a pending pick-up, an open trade, an autopilot run |
  | ground items | stay (an item cannot vanish: removing one needs a restart) |
  | the database | nothing is written by a reload. Saves go on as usual; the character loader checks every later load against the new set |

  Events still waiting from the last tick's handlers are dispatched at the swap, a tick early. On the sample content a swap took 5 ms on the hollow and
  30 ms on the meadow (40 entities removed and spawned again, one player); the first one in a process also pays the JIT. Development only, so no budget is set.
- **Clients.** When the display bundle's hash changed, the gateway wants the new hash from then on (a client packed with the old one gets `update_required` at its
  next sign-in, as at boot) and sends every playing session one `content_changed` with the new hash (§10; the one `Net/**` change of T-801). v1 does not take a
  bundle while it plays: the client's HUD says "Content changed on the server. Restart the game to see it." on the announcement line until the game is restarted.
  A change to server-only data (monster stats, drops, formulas) keeps the hash and sends nothing. The bundle is still built by the same whitelist (§5.4, hard rule 11).
- **Not reloaded:** what outside the tick read content at start (the admin API's checks in `Admin/**`, the account service, the gateway's own inventory messages);
  the rules above refuse what would make them disagree with the tick.

### 5.6 The content editor (T-801)

`dotnet run --project src/Game.Tools -- editor` serves a small local web page for the common kinds: items, monsters, skills, NPCs, dialogue, shops and quests
(`ContentEditor.FirstKinds`: the kinds a designer touches most, with mostly plain fields; jobs, statuses, drops and outfits are next; maps and the root files are not
offered). How to use it: [CONTENT_EDITING.md](CONTENT_EDITING.md).

- **Loopback only, no sign-in.** It listens on 127.0.0.1 (or `::1`) and refuses any other `--host` (exit code 2). It answers only requests whose `Host` is a loopback
  name, and a save must come from its own page (a JSON body with an `X-Editor` header, which a form or another site's page cannot send).
- **Driven by the schemas.** A kind is a content folder whose generated `schemas/*.schema.json` (matched by title to `ContentLayout.Folders`) exists; the page
  builds each form from the schema at run time (types, enums, required, descriptions). A string whose pattern names a namespace (`^item\.`) is a reference and offers
  the ids that exist with that prefix, from the content and from the theme's asset keys. No C# or page code lists a kind's fields (hard rule 2). A document's
  `id` is shown, not editable (a new id is a removal and an addition), and the editor changes existing documents only.
- **Every save validates the whole set first**, exactly as `validate` does (every rule, the JSON Schemas, the map scenes), on a copy of `content/` in a temporary
  folder with the change, and writes nothing when there is an error: the page lists the errors with file, line and path. A file changed on disk since it was opened
  is not overwritten. A save takes a few seconds on the sample content (the whole check).
- **Writes.** One file per save, atomically: a temporary file beside it, flushed, then a replace. A save that only changes values edits each value where it stands
  (YamlDotNet's marks), so comments, key order, quoting and layout are kept byte for byte. A save that changes the shape (a key or list item added or removed) writes
  the document again: key order is kept and new keys follow, but **comments are lost** (YamlDotNet's model has none), flow lists become block lists, and only the
  `yaml-language-server` line at the top is kept. The page says which happened.
- With a server running `--hot-reload` on the same `content/`, a save is live within about a second.

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
  active (step and one counter per objective of the step), completed. Saved with the character (§3).
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
- **Paid exactly once** (T-702, row 264): a turn-in has a number (`QuestProgress.Token`) that stays until its step has advanced, and the bag that paid it keeps that number, so a player who warps between the two
  advances on arrival, and one whose turn-in was not paid yet has it asked again there. The rewards combat pays are kept in `ProgressionState.OwedRewards` until paid, on whichever map. `abandon_quest` waits while a turn-in is
  being paid (it used to be able to keep the rewards and the quest).
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
  - **Save point.** `ISavePointStore` (shared by every instance, saved with the character) holds a character's save point. **The rule:** `teleport` with `to_save_point` and the respawn after death both go
    to it; a character without one uses the respawn point of the map it is on (`CombatContent.RespawnPointFor`). It is forgotten when the character leaves the world.
  - **Services.** `open_service` sets `Entity.OpenService` (and, for `shop`, `Entity.OpenShopId` and `Entity.OpenServiceNpc`) and sends `service_opened`; it stays open until the player walks beyond the talk range, warps, dies, closes or talks to someone else (a
    finished graph leaves it open). **Refine:** with `dialogue.refine_needs_service` (on in `balance.yaml`) the `refine` intent is refused `service_required` unless a `refine` service is open;
    the starter meadow's smith opens it. `insert_card` stays free anywhere. **Resets:** Odalys offers a free stat reset and skill reset to characters below base level 10 (`base_level` condition and
    `run_effects`), the same line as `progression.reset.free_below_base_level`; the reset items stay. Since T-602 the choices also need `out_of_combat`, the window of `progression.reset.refuse_in_combat`.
    **Shop** (T-603): a service that needs data takes it from a field of the action, not from a generic parameter bag: `open_service` with `service: shop` also names `shop_id: shop.*`
    (required for `shop`, refused for any other service; checked at load). The shop is data (`content/shops/*.yaml`, `ShopDef`): its stock, each row an item id and an optional own `price`
    (else the item's `buy_price`); §6.8 has the trading rules.

#### 6.4.2 Quest autopilot (T-604)
`AutopilotSystem` (`Game.Server/World/Autopilot`) plays a player's ticked quests, one after the other: it travels, hunts, gathers, walks, talks and turns in. It runs after the quest system. Numbers:
`balance.yaml` `autopilot`; terms: VOCABULARY "Autopilot ...". The rules that need no world are pure, in `Game.Shared/Autopilot` (what may start, the next objective, skills, dialogue) and
`Game.Shared/World` (`DistanceField`, `TravelMap`, `Pathfinder.FindNearest`).
- **It only decides.** It queues the intents a player would (`MoveTo`, `StopMove`, `AttackTarget`, `StopAttack`, `UseSkill`, `PickUp`, `TalkTo`, `DialogueChoose`, `DialogueClose`) on its instance's
  queue, and the systems that own them check them as ever: range, cooldown, SP, loot priority, weight, safe zones. It grants, teleports and bypasses nothing, calls no other system (rule 7: events and
  intents only), draws no randomness, and tells the client nothing of monsters, spawns or routes (rule 11). It aims only at what the player is sure to see (`aoi.view_radius` x `aoi.bucket_size`
  cells) and walks up to anything farther first.
- **The run** is on `Entity.Autopilot`, so it follows the character through warps; it is not stored. Each thought (every `decide_interval_ms`, or at once on the event it waits for) starts from
  what is true now, so a warp, a stolen kill or a vanished item needs no handling of its own. A finished quest moves on to the next of the list, skipping what is no longer active; the list's end is `finished`.
- **Objectives.** The current step's next unmet one: first what it can play (`kill`, `collect`, `reach`) in list order, then what only the player may do (`level`, `job`, `equip`, `use_item`:
  it pauses), then `talk`, which is the turn-in.
- **Hunting.** The nearest living wanted monster by walking distance (one bounded search outwards; not in a safe zone, not one that proved unreachable within `avoid_ms`). `collect` hunts every monster whose drop
  table has the item. Between fights it fetches ground items an unmet `collect` objective of the step wants, and no others, within `gather_range` and when the loot priority allows. From a safe zone it
  steps out first. With nothing alive in reach it goes to the nearest spawn area and waits there. **Retaliation:** on a journey, to an item or to an NPC it walks on when attacked; at the
  hunting ground, between fights, an attacker blocks the plan (it keeps hitting a character that waits or looks for a target), so whatever hit it within `retaliate_ms` is fought first.
- **Skills** (`AutopilotSkill`): the shortcut slots in slot order; the first one that is learned, allowed with the weapon, off cooldown and affordable (the same rules combat checks, so nothing is sent to be
  refused) and that has a use; else the auto-attack. Hostile skills with a range go on the target; hostile areas around the user when the target is within the smallest of them; friendly skills on the player
  only, and only when they would do something (`heal` / `restore_sp` below `support_below_percent`, a status not on yet, something to cleanse). **Ground skills** only when aimed at enemies and every effect is
  a hostile `area` with `damage` in it, on the target's cell. Never: passive, `teleport`, `summon`, `reset_*`. No consumables at all.
- **Travel.** "Nearest" is the fewest warps, then the shortest walk. `TravelMap` is built at boot: a distance field (one integer and one byte per cell) for every warp and every place a quest objective needs
  (NPC, `reach` cell, spawn area), and what is left of each journey after each warp. A route is then array reads, with no search. Fields go around warp cells that are not meant; a journey is
  walked in legs of at most `leg_cells`, shortened while a shortest walk could cross such a warp, halved when a leg is refused.
- **Dialogue.** A `talk` counts when it starts, so the dialogue is closed as soon as the quest moved. Otherwise `AutopilotRules.ChooseDialogue` picks, among what is shown, a choice whose actions are all
  `start_quest` for ticked quests, or one without any action that is about a step of a ticked quest (`quest_step`, or `quest_state` `active`); else a choice (or continue) without actions that leads there over such ones.
  Any other action (service, effects, save point, items, another quest) is never picked. Nothing safe: it closes the dialogue and pauses.
- **Ending.** The player's own order ends the run: the gateway queues `ManualInput` just before it. `ManualInput` and `StopAutopilot` are `ICancelsAutomation`: every `Automated` intent of the character drained in
  the same tick is dropped, whichever came first, so an order queued a moment ago never overrules the player; one that arrives while the character is between maps is kept until it arrived. `stop_autopilot`,
  death, leaving the world, the end of the connection (or its takeover), a bag without room for what is wanted, the daily limit and dropping the quest end it too; the character stops. A resume or a
  respawn does not restart it. **The player's order also ends a paused run.** A pause ends by itself when the quest's step or counters move (an `unreachable` one also on another map).
- **Daily limit.** `daily_limit_minutes` of simulated running time per 24 hours of the server's clock, counted from the first run of that day (the clock has no calendar); pauses are free. Saved with the character (T-702), so signing in again does not reset it.
- **Cost** (Release, sample meadow, the system's own share of a tick): one hunting run 5 us on average; 100 hunting runs 0.27 ms; 100 waiting runs 0.02 ms; 100 runs whose every search fails 0.5 ms. All runs of an
  instance share `searches_per_tick` searches, each within `movement`'s limits; a run whose search failed waits 3 s before the next; what no walk leads to is ruled out without a search.
- **Limits.** Monsters never block and are never avoided on the way; a monster that moves around all the time is only re-chosen when a fight ends; a `collect` hunts the nearest source, however dangerous; parties are not considered.

### 6.5 Progression
Base level + job level, separate EXP tables. Stat points per base level and skill points
per job level, stat point cost, the stat range and combat clamps come from `balance.yaml`
(`BalanceDef`); the max base level is the base EXP table length + 1. Job change requirements from `JobDef`.

**Player skills** (T-502; combat hosts them, `CombatSystem.PlayerSkills.cs`, rules in `Game.Shared/Skills` and `Progression`). State per character is the `ProgressionState`
in the progression store (saved with the character, §3): skill points, learned levels, job, completed quests and unlocked jobs.
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
  same players who share its EXP. Quests count `kill` objectives from it; party members near the kill get credit through `PartyKillCredit` (T-902, §6.9).
- **EXP.** Per monster, the HP each player took since its last full-HP moment. On death the players still on the map share `base_exp` /
  `job_exp` in proportion (largest remainder; ties to the first hitter; the shares add up exactly) through `ProgressionRules`, which raise
  `ExpGained` and `PlayerProgressed` (`LevelUp` / `JobLevelUp`); the sheet takes the new levels and job bonuses. Progression is kept per
  character in an `IPlayerProgressionStore` shared by all instances (saved with the character, §3).
- **Drops.** Each `drop_table_ids` table rolls every entry on its own (`chance`, then a quantity in `min..max`) with the instance random.
  Each drop is an `EntityKind.Item` entity on a random walkable cell within `loot.spread_radius` of the death cell (`ItemDropped`); for
  `loot.priority_ms` only the top damager may take it (`LootRules.MayPickUp`), and after `loot.lifetime_ms` it is despawned
  (`ItemExpired`). Clients see ground items as kind `item` (§10) with their quantity and whether they may take them now, never the owner or the timers.
  Picking them up is the inventory's (§6.8).

### 6.8 Inventory
One system, `Game.Server/World/Inventory` (`inventory`), after combat and before the AOI; the rules are pure functions in `Game.Shared/Items`
(`InventoryRules`, `InventoryState`), shared with the tests. Numbers from `balance.yaml` `inventory` and `new_character.start_items`; the weight limit is the
derived stat `weight_limit` (`formulas.yaml`).

- **Model.** Per character, an `InventoryState` in an `IInventoryStore` shared by every instance (saved with the character, §3). An **entry** is a
  stack of one item up to its `max_stack`, or one equipment item with its own instance id, its socketed cards (in socket order) and its refine level. Entry ids come from the store,
  permanent and unique over all characters and server runs (§3 "Item ids"; without a database only for the run); an equipment item dropped and picked up again keeps its id, cards and refine level (they ride on the ground item, which clients never see them on). Worn entries stay in the inventory (their slot is set), so they count
  toward `inventory.max_entries` and the weight, and putting something on never needs room. **Weight** = Σ `weight × quantity`.
- **Weight states** (`WeightStates`, from the weight as a share of `weight_limit`): `normal`; `burdened` from `inventory.burdened_percent` (50%): no natural
  HP and SP regeneration; `overweight` from `inventory.overweight_percent` (90%): no regeneration and no attacks (§6.7). A pick-up that would pass 100% is
  refused (`too_heavy`). The state is on `Entity.Load`; the limit is re-read whenever the stat sheet changes (a level, STR from equipment), and the owner is told.
- **Atomic operations.** Every change (pick up, use, equip, unequip, drop) checks everything first and returns an "Item reasons" term with nothing changed;
  only then does it change the state, in steps that cannot fail, inside one tick. So the state is whole at the end of every tick, which is when it is copied for a save
  (§3). A refusal is an `ItemRefused` event and a `rejected` message.
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
- **Currency** (T-602): one balance per character, `InventoryState.Currency`, kept with the bag so one transaction can take items and pay or spend currency;
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

### 6.9 Party (T-902)
`Game.Server/Party` (`PartyService`, off the tick) and `Game.Server/World/Party` (`PartyRoster`, `PartySystem`, `PartyKillCredit`); the client's `PartyBook` and `PartyPanel`. Numbers from
`balance.yaml` `party` (game rules) and `formulas.yaml` `party_exp`; the invite rate and the state interval are server configuration (`PartyOptions`, `--party-<name>` or `GAME_PARTY_<NAME>`:
`invites-per-minute` 6, `invite-burst` 4, `status-interval-ms` 1000).
- **Where a party lives.** In the memory of one server process, never in the database: nothing about a person is stored (STORE_COMPLIANCE unchanged), and a restart ends every party. Its members are
  characters this process's gateway holds, so all are on this process: v1 runs one game process per world (§3), so a member on another process cannot happen; an invite finds only characters playing
  here, and a member who leaves the world (logout, end of the grace period, a staff kick) leaves the party. Cross-process parties are out of scope (a later task would move the roster behind a shared store).
- **Rules.** One party per character; `party.max_members` (5) including the leader. Only the leader invites, kicks and hands over. **Invite** by name (as names compare, T-701) or by tapping a player on
  the same map (`target`): rate-limited per account (a dev login per entity), then refused `not_online` (also when the invited **blocks** the inviter through T-901's blocks: the same answer), `invalid_target`,
  `not_leader`, `full`, `already_in_party`, `invite_pending`. The invited gets `party_invited` and answers with `party_answer`; it expires after `party.invite_timeout_ms` (30 s) and both are told
  (`expired`). An accept joins the inviter's party, or makes one the inviter leads; it is checked again then (`full`, `already_in_party`, `no_invite`).
  **Leader leaving**: the earliest-joined member who plays now leads, else the earliest-joined (the order is the join order, shown in the panel). **A party of one ends** (`disbanded`).
- **Disconnects.** A member in its grace period (§10) stays a member, shown `away` to the others, and gets its party and any invite again with its next `welcome`: a phone that drops on a lift comes
  back to its party. When the grace period ends it leaves like a `party_leave`. The party service looks at who the gateway still holds on each pulse (`status-interval-ms`), so a member leaves at most
  a second after the world let it go. It cannot be made leader while away.
- **EXP share** (combat, §6.7, reading the roster). With nobody of the kill's damage dealers in a party, the split is exactly the old one (golden tests). Otherwise the monster's EXP is first split
  by damage between **shares**, by the same `ExpSplit`: each player in no party is one share, each party one share with its members' damage added up, in first-hit order. A player's share is its
  own. A party's share goes to the members who **share the kill**: alive, on the kill's map instance and within `party.exp_range` (12, Chebyshev) of the death cell, in party order. They split
  `party_exp` of it evenly (the odd points to the first in party order): `floor(exp * (100 + (share_count - 1) * 10) / 100)`, so one member alone gets exactly its part, two 110%, a full party
  140%. **Level gap**: when the base levels of those members differ by more than `party.exp_level_gap` (15), or none of them may share (all dead or far), the party's damage dealers split its
  share by damage as without a party. Base and job EXP the same way, through the same expression (one rule to learn; the job tables already scale per job). Kill credit for loot priority is unchanged.
- **Quest kill credit** (hard rule 7): the instance's `PartySystem` turns `MonsterKilled` into `PartyKillCredit` for the party members of credited players who are alive, on the instance and within
  `party.kill_credit_range` (12) of the death cell and had no credit of their own; the quest system counts their `kill` objectives from it (a tick later). Quests never call the party, the party never calls quests.
- **Members' state.** Each instance's `PartySystem` reports its party members every `status-interval-ms` (map, cell, HP and SP as whole percent, rounded up so a living member never shows 0, dead) to the
  roster; the party service sends each member the others' (`party_members`), only when they changed, and only to members. Whole percent is all a bar needs and costs a byte or two each: a party
  of five moving all the time is about 1 KB a second in all, nothing when it stands still. The area of interest is untouched: members out of view stay out of the world view; the client shows
  them by these cells only. (The AOI's `VisibleCap.AlwaysVisible` hook for party members is not used yet: ADR-002 row 427.)
- **Party chat**: the party service is the `party` channel's `IChannelMembers` (T-901's seam): the members who play now hear a line, with the mute, rate, text, blocks and reports of every channel.
- **No client authority**: the client sends intents (`party_*`, a tap's entity id) and shows what the server sends; who is in range, who gets EXP and who killed what are the server's.

### 6.10 Trade (T-903)
`Game.Server/World/Trade` (`TradeSystem`, the intents and events, on the tick), `Game.Server/Trade` (`TradeGate`, `TradeOptions`, off the tick), the swap in `InventorySystem` with the pure
`Game.Shared/Items/TradeRules`; the client's `TradeBook`, `TradePanel` and the party panel's Trade buttons. Numbers from `balance.yaml` `trade` (`range` 4 cells, `ask_timeout_ms` 20 s,
`max_entries` 10 per side); the ask rate is server configuration (`--trade-asks-per-minute` 6, `--trade-ask-burst` 3, or `GAME_TRADE_<NAME>`). Items have `tradeable` (default true;
`item.warden_token` is false), validated with the item schema; the bundle marks `untradeable` ones so the window does not list them (a hint: the server decides).
- **Asking.** A tap on a player opens the party panel with **Trade** beside its invite; each party member's row has Trade too. The gate takes the per-account rate limit and answers
  `not_online` for a player who does not play now or whose account blocks the asker (the same answer, as T-902), `invalid_target` for oneself; the rest is the tick's: both on one map instance
  (else `not_online`), alive, within `trade.range` (Chebyshev), neither in a trade nor with an ask of its own (`busy`), the asker not already waiting (`ask_pending`), and both saved or both
  development logins (`cannot_trade`: nothing may move between a saved character and one that is never saved). An ask expires after `trade.ask_timeout_ms`; a decline tells the asker.
- **Offers.** Each side offers carried entries, whole or part of a stack (`trade_offer` with a quantity, 0 takes it out; the same entry again replaces its line), at most `trade.max_entries`, and
  currency up to what it carries. Worn (`item_equipped`) and not-tradeable items are refused. The server keeps each offered entry **exactly as it was** (id, quantity, cards, refine) and sends
  both sides the two offers as it holds them (`trade`, with a `version`). When the inventory changes under an offer (the entry dropped, sold, used, equipped, refined) the offer follows what is
  carried now and both sides unlock.
- **Two steps.** Each side **locks**; any change to either offer, and `trade_unlock`, unlocks both and clears both confirms; every change raises the version. With both locked each side **confirms
  the version it saw** (`stale` for another, `not_locked` before), so a last-moment change voids a confirm. When both confirmed the same version the trade is frozen (`swapping`) and the swap is
  asked for (`TradeSwapRequested`).
- **Ends.** A cancel, a dropped connection or a takeover by another session (the gateway queues `TradeCancel disconnected`; a resume never finds a trade under way), death, a warp
  (`left_map`), leaving the world (logout, server stop, staff: `left`), and being farther apart than `trade.range` end the trade; nothing moves. Being attacked does not end it (dying does): a
  trade is two clicks from done, and a monster hitting a trader should not lose the trade it was confirming. Once both confirmed only the swap's answer ends it.
- **The swap**, on the inventory's next tick, before that tick's intents, in one step for both characters (`TradeRules.TrySwap`): it checks again that each side carries exactly what it offered
  (else `offer_changed`), that the receiver stays within `inventory.max_entries` (`inventory_full`) and its weight limit unless the swap makes it lighter (`too_heavy`), and that no currency
  passes what a bag holds (`currency_full`); then it moves everything or nothing. A whole entry moves with its permanent id, cards and refine; part of a stack becomes a new entry with a new id on
  the receiving side, never merged into a stack there (so the log says where each unit went). Each side gets one `inventory_changed` `traded`; `ItemsTraded` says what moved (for quests and
  logs); the trade closes `done`. A side that left, or whose save was lost, refuses the swap (`left`).
- **Saving** (§3): in the swap's tick both `PutDown` and both `DependOn` the other's link, so the two copies always land in **one transaction** or not at all: a crash before the commit finds both
  characters before the swap, after it both after (tested at every step of the save against PostgreSQL, with a save already on its way, and with a stop in the middle of a trade).
- **Logs.** One line per swap, `[trade] {"trade":…,"at":"<UTC>","a":"<character id>","b":"<character id>","a_gave":[{"from":<entry>,"to":<entry>,"item":…,"quantity":…}],"a_currency":…,
  "b_gave":[…],"b_currency":…}`: permanent ids only, never names or anything else about the people. How long logs are kept is the fork's (STORE_COMPLIANCE §2: the template keeps no log files).
- **No client authority**: the client sends intents only; what is in an offer, who is in range and whether a swap happened come from the server.

### 6.11 Guilds (T-904)
`Game.Server/Guild` (`GuildService`, `GuildOptions`, `IGuildStore`; off the tick), `Game.Server/Persistence/PostgresGuildStore` (`guilds`, `guild_ranks`, `guild_members`, migration
`20261127125839_Guilds`), `Game.Server/World/Guild/GuildFoundingPayment` (the cost, on the tick, taken by the inventory system), the pure `Game.Shared/Guild/GuildRules`; the client's `GuildBook`
and `GuildPanel`. Rules from `balance.yaml` `guild` (name lengths and separators, `found_cost`, `max_members` 30, `max_ranks` 6, `rank_name_max_length` 16, `invite_timeout_ms` 30 s,
`default_ranks`, `emblems`); server configuration `--guild-notice-length` 120, `--guild-intents-per-minute` 20, `--guild-intent-burst` 6, `--guild-pulse-ms` 1000, `--guild-refresh-ms` 5000,
`--guild-payment-wait-ms` 10000, `--guild-storage-lease-ms` 15000 (T-810; at least 6000) (or `GAME_GUILD_<NAME>`). A server without a database (development's dev login) has no guilds.
- **The database decides.** Every change is one transaction that locks the guild's row (`FOR UPDATE`), reads its ranks and members, and runs `GuildRules` on what it read: two officers on two
  server processes acting at once are run one after the other, and the second sees the first's change (tested on two connection pools). Whatever adds a member row takes the character's row
  first (`FOR KEY SHARE`), as erasing and deleting take the character and then the guild, so the two never wait on each other in a circle. Every change raises the guild's `version`.
- **Founding** (`guild_found`): the name by the character naming rules (their letters and separators, plus `guild.name_separators`, with the guild's own lengths), unique by its lower-case key;
  an emblem of `guild.emblems`; one guild per character (a unique index on `guild_members.character_id`). The guild is first **reserved** (`founded_at` null: the name is held, nobody can join);
  then the inventory system takes `guild.found_cost` on the tick (currency, carried units of an item, or nothing) and puts a note on the founder's save (§3, T-806's `StaffNote` road), so the
  save that first lacks the cost also founds the guild, in **one transaction**. A cost refused, a tick that never starts it (`--guild-payment-wait-ms`, then nothing is taken) or a stay that ends
  before the save lands drops the reservation; a reservation a crash left is dropped at the founder's next sign-in or founding.
- **Invites** (`guild_invite` by name or by tapping a player on the same map, `guild_answer`): as for a party (§6.9): per-account rate limit, a block reads as `not_online`, one pending invite per
  character, `guild.invite_timeout_ms`, held in this process's memory. The inviter needs `invite`; the accept checks again in the database that the inviter is still a member with `invite` and that
  there is room. A new member joins at the last rank.
- **Ranks.** Place 0 is the leader's; the leader names and orders the places below (`guild_ranks`, 1 to `max_ranks` - 1 of them, names cleaned like chat, permissions from "Guild permissions");
  members keep their place, and those on a place the new list no longer has go to its last. `guild_rank` (`set_rank`) moves a member of a lower rank to a rank also below the actor's;
  `guild_kick` (`kick`) takes out a member of a lower rank; nobody acts on the same or a higher rank. `guild_lead` (leader only) gives the leader rank to a member, and the old leader takes that
  member's old rank. The leader edits the list in the client's rank editor (T-810, §7.4), which sends all of it as one `guild_ranks`.
- **Leaving and the end.** `guild_leave`; a leader who leaves hands over to the **successor** (the highest rank, the earliest to join among equals); the last member leaving disbands the guild
  (its rows go and its name is free). Erasing an account (T-703) and deleting a character take the character out by the same rule, in their own transactions.
- **Notice** (`guild_notice`, `edit_notice`): cleaned by chat's text rules (format characters out, spaces folded; over `--guild-notice-length` refused `too_long`; empty clears it). It is in the
  `guild` message; the client shows it once on the flash line at sign-in and when it changed.
- **What members get.** `guild` after every change, at every welcome (sign-in, resume, takeover; read in the background) and when a member's online state changes here: the name, emblem,
  notice, ranks, and members highest rank first with their **member number**, name, rank, base level as last saved and whether they play on this process. **No map or cell**: a guild can be
  thirty people a member never met, and where someone stands is for a party (§6.9), which they choose to join for it.
- **Other server processes** (v1). Guilds, ranks and every rule hold across processes (the database). What one process changes, another sees within `--guild-refresh-ms` (it reads the versions of
  the guilds its players are in and reloads a changed one). **Online** means playing on this process: members on another one show offline here. **Guild chat** reaches the members on the
  speaker's process only, as T-901's whispers do. A follow-up would relay both through the database (PostgreSQL `LISTEN`/`NOTIFY` on a `guild_events` channel: a line or a presence change is
  published once and each process delivers it to its members) or a message bus once there is one.
- **Guild chat**: the service is the `guild` channel's `IChannelMembers` (`ChatService.Register`): chat does the mute, rate limit, text rules, blocks and reports as on every channel.
- **Storage** (T-905; `Game.Server/Guild/GuildStorage`, `IGuildStorageStore`, `Persistence/PostgresGuildStorageStore`, `World/Guild/GuildStorageTransfer`, pure `Game.Shared/Guild/GuildStorageRules`;
  migration `20261127224051_GuildStorage`: `guild_storage_items`, `guild_storage_holds`, `guild_storage_log`, `guilds.storage_currency`). Per guild: item entries under their permanent ids (cards
  and refine kept) and currency, at most `guild.storage.max_entries` entries (50) and `guild.storage.max_currency`. Any member may look (`guild_storage_open`); `storage_deposit` puts in,
  `storage_items` and `storage_currency` take out, `storage_log` reads the log. **No daily withdrawal limit** in v1: taking out is a rank's permission the leader gives, and every move is in the
  log. **No storage NPC**: it opens from the guild panel, because nothing the server checks depends on where the character stands and a phone player reaches the panel anywhere.
  - **A move, in three steps** (rule 10): (1) a **hold**, one transaction that locks the guild's row (`FOR UPDATE`, as every rank change does) and then reads the member's rank: a rank change
    racing a withdrawal commits before it (and refuses it) or after it, never in between; two members withdrawing the last unit on two server processes are run one after the other and the
    second gets `item_gone`. A deposit's hold keeps a place (holds count toward `max_entries`) and its currency toward `max_currency`; a withdrawal's hold takes the entry (or part of a stack, as a
    new entry with a new id from `CharacterSaves.NextItemId`) or the currency out of the storage at once. (2) The **bag's side** on the tick (`GuildStorageRules`): a deposit checks again that
    the bag carries the entry in that quantity, not worn and tradeable (content's `tradeable`), and the currency; a withdrawal that the bag stays within `inventory.max_entries`, its weight
    limit and the most currency a bag holds; a whole entry keeps its id, part of a stack becomes a new entry, never merged. (3) The character's **save** settles the hold (§3). A refusal on the
    tick, a tick that never starts it (`--guild-payment-wait-ms`), or a stay that ends before the save undoes the hold (a withdrawal's units go back onto a stack of the same item if one is
    there); a crash leaves it to the next claim. The same entry twice: the second finds it gone on the tick; a drop or use first: the same.
  - **Log**: every landed move (who by character id, the move, item and entry id, units, currency, when: the save's time), newest first in `guild_storage`, at most `guild.storage.log_shown`,
    kept `guild.storage.log_days` (older lines are deleted when the storage is read). Names are read at that time from `characters`; an erased account's moves stay without the id (§13).
  - **Disbanding**: the last member cannot leave (`storage_not_empty`) nor its character be deleted (`guild_storage_not_empty`) while the storage holds an entry, currency or a move under way:
    nothing in it is ever deleted unasked; they take it out first. Erasing an account (T-703) cannot be refused: a guild its last member's erasure ends goes with its storage, as the rest of that
    account's things do.
  - **Live** (T-810): `guild_storage_open` also gives the member a **lease** of `--guild-storage-lease-ms` (15 s) on this process; the client opens again every 5 s while its window is open,
    which renews it and reads the database anew. A lease, not an open and a close intent: a close can be lost (a dropped connection, an app put in the background or killed) or never sent (a
    modified client), so an end by time is needed anyway, and it needs no new message. After a move lands, or a withdrawal is undone (its hold had taken from the storage), every member with a
    running lease on that guild gets the new `guild_storage`, and so does the mover, whose answer this is. One flush per tick (100 ms) sends them, so **at most one per member per tick** however
    many moves landed, and none while nothing changes; each is read from the database for that member at that moment: still in that guild, and the log only with `storage_log`. A member who closed
    the window may still get changes until its lease ends (data it may read anyway). Opening has a rate bucket of its own, with the guild intents' numbers, so renewals do not spend what the
    other guild intents may. **Other server processes** (v1, as row 466): no push; their moves show when the window is opened again, which the renewal also does, so within 5 s while it is open.
    Claim-time undos and erasures (made in the database, not by this service) show the same way.
- **Emblems** (rule 3): asset keys (`icon.emblem.*`) the theme maps to files and `guild.emblems` lists, validated at load (each must be a key of the theme; at least one, none twice); the bundle
  has them as `guild_emblems`; never uploaded. The placeholder theme has four plain ones from `tools/themegen`. Every pulse (`--guild-pulse-ms`), each player gets the emblems of the guild
  members on its map instance when that list changed for it (`guild_tags`); the client shows the emblem beside a member's name in the target frame, and on the guild panel and its invite prompt.
  (Characters have no name plates over their heads: the target frame is where a name shows in the world.)
- **No client authority**: the client hides the buttons a rank cannot use; the server checks every act in its database.

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
  stored in the engine's per-user storage (`user://preferences.json`, `UserPreferences`; a scripted run uses a file of its own that starts empty, T-710), never in `client_settings.json` and never on the server, and is clamped to the
  range when read. It scales sizes and the font together; it is applied by the HUD itself and does not use the window's content scale, so the camera and picking see no change.
- **Text is never baked into textures.** Every size is dp times the scale; labels wrap rather than cut. A scripted check lays the HUD out for six screen shapes (small
  16:9, 19.5:9, a large phone, an iPhone, two desktop windows) with and without the notch at every UI scale, filled with the longest words, and fails on anything outside
  the safe area, smaller than its text needs, or under the touch minimum.
- **Layout.** Top left: level and EXP, HP and SP with bars. Top centre: connection notice and the target frame (the target's name and an HP bar: only the fraction the server sends, redrawn when the entity's `HpVersion` counter moves; never a number). A level-up pulses the top-left block for `level_up_flash_seconds`. Top right: connection state, the UI size
  button, log out. Bottom: the one-line flash, then the hotbar (slot count, rows and size are client config; a narrow screen shrinks slots to the minimum and then adds rows).
  The login, death, stop and refusal screens are one full-screen panel whose content stays inside the safe area.
- **Character creation** (T-706, `client/Scene/CreationPanel.cs`, view model `client/Core/Accounts/CharacterCreator.cs`). "New character" in the character list opens it (`AccountScreen.Creation`, a
  screen of the client's own: the server is asked nothing until Create). Left: the turning 3D preview (`LookView`, §7.5.1) and Random; right: the name, then Body, Face, Hairstyle, Skin tone and Hair
  colour in a scroll container, and under it, always in reach, the line about the server's answer, Back and Create. Every option is a button of at least the touch target with its **name**, the colours
  too (a chip of the colour beside "Deep"), and the chosen one is framed and marked `[ Deep ]`, so nothing depends on telling colours apart. An option is as wide as its name and wraps when that is wider than the
  column. The options are the bundle's only, and the server decides: every refusal is said on the line and keeps the choices; Back keeps them too, as does a session that ended by itself; a created character or
  signing out starts over from the defaults. **Switching gender** keeps the colours, and a face or hairstyle the other gender has as well; else it takes the one last chosen for that gender, else its default.
  **Random** draws gender, face, hairstyle and both colours from the injected RNG and keeps the name. The screen has a solid background (the HUD's empty hotbar would show through the panel's veil).
  **Character select** shows a picture of each character (the same `LookView`, still) beside its name and job. Both are in the `-Run ui` matrix with the longest words: 0 problems in 48 combinations each.
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
- **Left column on a small phone** (T-408). On a 360 dp phone at UI scale 1.25 and 1.5 the bars' text lines (level and EXP, HP, SP) stop growing with the scale at 1.0: they are read-outs, not touch targets, and at
  the larger scales their wrapped longest text pushed the tracker into the Skills button. The bars, the tracker and every button still scale. The Skills button is as tall as a button, anchored to the bottom of its
  spacer (it used to fill the spacer, as tall as a wrapped hotbar). `-Run ui` fails when the tracker, long or one-line, lies over the Skills button, the hotbar slots, the bag, size, sound or log out buttons or the target
  frame in any of the 48 combinations of a matrix (panels opened over the tracker are meant to cover it). Device behaviour stays unverified (ADR-002 row 112).
- **Quests** (T-602, `client/Scene/QuestPanel.cs`, view model `client/Core/Quests/QuestBook.cs`). **Tracker:** a compact button under the bars on the top left (13 dp text, the left column's
  width, at least the touch target): the tracked quest's name and, in the corner's two-line cap (`QuestBook.MaxTrackerLines`, T-406), its first objective or held reason with the server's counter (`Defeat Gel Slime 3/5`),
  a longer line trimmed with an ellipsis by the label; the log shows everything; or "Quests" when none is active; the quest
  last accepted or advanced is tracked, and the log's Track button picks another. A tap opens the **quest log**, a panel like the stats panel: active quests with their step text
  (from the bundle), objectives and a held reason, each with Track and Abandon (asks once more before sending `abandon_quest`), then the completed quests. **Markers:** a "!" drawn
  by a billboard shader above every NPC the server marks, gold to offer and green to turn in, one multimesh (one draw call, none when no NPC is marked), rewritten only when the
  markers or the NPCs in view change. The client decides no count, step or completion; quest changes go on the flash line. The bag's first line shows the currency.
- **Wardrobe** (T-708, `client/Scene/WardrobePanel.cs`, view model `client/Core/Outfits/WardrobeBook.cs`). Opened with the Outfits button in the bag's header (the full phone HUD has no room for
  another button; it has a Bag button back), as the stats are opened from the skills. Left: a turning preview (`LookView`) of the own character drawn from **its own view record**, the one everyone else gets (its
  looks, worn gear and shown outfits), so it shows exactly what others see, and the switch that hides every outfit ("Hide outfits" / "Show outfits"). Right, in a scroll container: one row per outfit slot that an
  owned outfit has a piece for on this character's gender, each with "My gear" and one button per such outfit (icon and name); the chosen one is framed and marked `[ Name ]`. Under them a line that says outfits are
  looks only. It sends `set_outfit` only; what is chosen is the server's `wardrobe`, and a refusal goes on the flash line. A new outfit is said on the flash line ("New outfit: ..."). It redraws only when the
  wardrobe or the own character's gender changes; the preview renders every frame only while it is open (it is freed when it closes). It is in the `-Run ui` matrix with the longest words (48 combinations).
- **Quest autopilot** (T-605, view model `client/Core/Autopilot/AutopilotModel.cs`, controls in `QuestPanel`, indicator in `Hud`). **Ticks:** each active quest in the log has a tick ("Add to
  autopilot", then "Autopilot: #n" with its place in the run); a tick is this player's own wish, kept only in memory, sent nowhere until Start, and dropped when the quest is completed or abandoned
  or the login ends. **Order:** a ticked quest gets Earlier and Later buttons that swap it one place; not dragging, because a drag inside a scrolling list fights the scroll and needs a precise
  finger on a phone, while a button is a plain 48 dp target and the "#n" stays readable. **Start and Stop** are pinned under the log's title, outside the scroll: Start sends `start_autopilot` (the ticked
  quests in run order, the skill bar's skills in slot order, level 0) and is disabled with nothing ticked, while dead or offline; Stop sends `stop_autopilot` while a run is on. The server still decides;
  every refusal (`rejected` with `intent` `start_autopilot`) goes on the flash line with its reason, naming the quest or skill. **Indicator:** while a run is running or paused, a 48 dp high, at most 260 dp wide button in the
  centre column of the top row, under the target frame (so it lies over nothing and nothing lies over it): "Stop autopilot: hunting" and the quest's name, or "Stop autopilot: paused" and what the player can
  do ("Reach level 10 to go on."), trimmed with an ellipsis; a tap sends `stop_autopilot`. Off shows nothing. A run that ends says why on the flash line (finished, stopped, "you took over", died, bag full ...).
  **Only the server's word:** the state is the last `autopilot` message, reset at a new login and told again after a resume; the client never ends, pauses or restarts a run. It sends no message of its own that
  counts as manual input (the dialogue and shop panels send `dialogue_close` only from their Close buttons); the dialogue a turn-in opens stays down while a run plays, so it cannot cover the indicator or be
  pressed by mistake (a paused run shows it).
- **Where the client's own words live (ADR-002 row 120).** In `client/Core/UiText.cs`: one static class of constants and small formatting functions in English, and nowhere
  else (a test fails on a literal assigned to a text property in `client/Scene`). Why: the refusal and update screens must read even when the bundle, theme or any data
  file is missing or broken, which rules out loading them; the pure core and its tests can use them; a typo or a missing word is a compile error; and the number of
  words is small. Names of game things never come from there; they come from the bundle by `display_key`. Translation later replaces the bodies of these members with
  lookups into a per-language table (the members are the keys, and their parameters the placeholders) without touching any caller. Localisation itself is not built.
- **Budgets (§9).** The HUD is about 40 nodes and adds about 24 draw calls (windowed measurement, T-211 report); the frame cap is 30 fps and the HUD does work only when
  a version counter changes, plus one size comparison per frame and one comparison while no finger is down.

- **Chat** (T-901, §10 "Chat"): the Chat button sits beside the quest tracker in the left column (the two share the column's width, so it adds no row), and the last two lines take the
  flash line's place at the bottom while no flash or announcement shows (as T-806's announcements share it; they take no touches). A 640x360 dp phone at UI scale 1.25 and 1.5 has no row to
  spare: the layout check found a strip above the flash line covering the left column's read-outs, and one at the top of the centre column pushing it off the screen. The panel opens at the
  top of the safe area so the on-screen keyboard, which comes up from the bottom, never covers the field being typed in: each frame the keyboard's height changes, the panel is cut to what
  is left (`DisplayServer.VirtualKeyboardGetHeight`). The layout check runs the button in every "playing" combination, the last lines in the flash line's place ("chat lines shown"), and the
  open panel with its longest lines with and without a keyboard taking 60% of the screen's height (the field, Send, the switch and Close must stay above it); nothing of it may lie over
  another part of the HUD.
- **Party** (T-902, §6.9; `PartyPanel`): the panel opens from the chat panel's **Party** button (the HUD's columns have no button to spare on a 640x360 dp phone at 1.5, as the wardrobe opens
  from the bag): the members in party order with the leader's mark, where each is (here, a map's name, fallen, away) and HP and SP bars, Lead and Kick for the leader, a name field with Invite, the
  invite of a player tapped in the world beside it (a tap on another player opens the panel and sends nothing), and Leave in the heading. The **invite prompt** (who asks, seconds left, Join and No) comes up by itself at
  the top of the safe area. Members on this map who are off the screen get a **mark at the screen's edge**, an arrow and the name, pushed in from the edge toward them: no minimap, as no corner of
  the phone HUD is free at the largest scale and a minimap would draw the map a second time every frame; an arrow says which way, which is what a party needs. The layout check runs a full party of
  the longest names with the leader's buttons on every row, the prompt over it and four marks, at every screen, notch and scale.
- **Trade** (T-903, §6.10; `TradePanel`): the **ask prompt** (who asks, seconds left, Trade and No) at the top of the safe area; while a trade is open the **window** fills the safe area: the
  heading (whom with, the state line, Cancel), two columns (what this side gives, each line a button that takes it out, with **Add** opening the bag's tradeable carried entries in the columns'
  place, with a quantity picker for stacks; what the other side gives), and a footer (currency field, Set, Lock or Unlock, Confirm). Both columns are the server's last `trade`; Confirm sends the
  version on the screen. How a trade ended goes on the flash line. The layout check runs both columns full of the longest lines with the longest name and the prompt, and the picker, at every
  screen, notch and scale. It redraws only when the trade's or the bag's version moves.
- **Guild** (T-904, §6.11; `GuildPanel`): opened from the chat panel's **Guild** button beside Party. A heading (the emblem, the name, who is online, Leave, Close); a scroll list with the
  notice (a field and Save for a rank with `edit_notice`) and the members highest rank first (online dot, name, rank, level; **Up**, **Down**, **Kick** and **Lead** only where this character's
  rank may act on that member); a name field with Invite and the tapped player's invite for a rank with `invite`. In no guild: the name field with **Found** and the theme's emblems as toggle
  buttons. The **invite prompt** (who, which guild with its emblem, seconds left, Join and No) at the top of the safe area. The chat switch goes map, whisper, party, guild while in them. The
  target frame shows a guild member's emblem before its name (one texture, no extra draw call beyond the frame's). The leader also has **Ranks** at the top of the list, which opens the rank
  editor (below). The layout check runs the panel with the longest names, rank names and notice,
  every button on every row and the prompt, and the founding form with the picker, at every screen, notch and scale.
- **Guild storage** (T-905, §6.11; `GuildStoragePanel`): the guild panel's **Storage** button (beside Leave). A heading (entries out of the size, the currency); tabs **Storage** (what it holds;
  a tap takes out, disabled without `storage_items`), **Bag** (carried entries that are not worn and are tradeable, by the bundle; a tap puts in) and **Log** (only with `storage_log`); Close;
  the scroll list; the quantity picker (−, units, +, Take or Put) once a stack is tapped, in the coins row's place (one row under the list at a time); the coins row (an amount, Put, Take).
  The layout check runs it full (fifty of the longest lines, the largest currency) with the picker, and with fifty of the longest log lines. While it is open it opens the storage again every
  5 s and redraws when a `guild_storage` arrives (T-810, §6.11); a picker open on a stack that changed takes the new quantity, and one whose stack went closes.
- **Guild ranks** (T-810, §6.11; `GuildRanksPanel`, draft `Core/Guild/RankDraft`): the leader's. A heading (**Add**, **Save**, Close); the server's answer to Save in words under it ("Ranks
  saved.", or the refusal); a scroll list: a text saying that members stay at their **place** (moving or removing a rank changes what the members at that place are called and may do, those past
  the end go to the last rank, where new members join), then per rank below the leader's its name field with **Up**, **Down** and **Remove** (disabled for the only one), "Place n: m members"
  (who the server would put there on Save), and a box per "Guild permissions" term, wrapping to the width. Nothing is sent before Save; closing drops the draft; a saved answer reads the names
  back as the server cleaned them. The client does not know `guild.max_ranks` or the name length: the server refuses (`invalid_ranks`) and the editor shows it. The layout check runs it with five
  ranks of the longest names, every box ticked and the longest refusal.
  **§9** (T-810): no 3D draw, and both windows redraw only when the guild, storage or bag version moves (no per-frame work while nothing changes; a renewal redraws the storage once per 5 s). The
  `ui` run prints the main-thread time of a redraw of the full storage log and the full editor (ADR-002 row 516). Network: only while the storage window is open, one `guild_storage` per 5 s
  plus one per tick with a change; the largest (50 entries with four cards, 50 log lines, long ids) stays under 5 KB/s at that rate (a test), under half of the 10 KB/s budget.

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
  materials differ only in colour (opaque `StandardMaterial3D`, the same shading, and the same image or none) join one surface with the colour baked into the vertex colours. Since T-711 the image is
  told by **content**, not by file: every `.glb` embeds its own copy of a texture, so `ImageIdentity` (pure, `client/Core/Theme`) keys each texture once by a hash of its imported bytes (the
  `.ctex` file, read without touching the GPU; else its pixels), and parts that embed the same atlas merge although each file has its own texture object; the shared material keeps the first copy.
  Vertex colours keep multiplying the texture, so the character's colours still show on textured parts. A material that is see-through, has another texture than the base colour, or whose image
  cannot be read (one warning per texture) keeps a surface of its own, as does every other image: the theme's artist rule is one character atlas (THEMES.md §1.9, checked by `validate --theme`),
  not an atlas built on the phone (why: §1.9); `merge_by_image` off groups by texture object as before, for measuring. A merged character (both levels of detail together) is
  cached by body file, the character's colours and the set of parts in any order (`WornSet`, T-706: face, hairstyle and job pieces are parts too, §7.5.1), so a crowd with the same looks and gear shares one mesh
  and one skin per level. The cache keeps `look_cache_limit` (64) characters, the least recently used go first. A piece without a LOD1 mesh uses its LOD0 at both levels.
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
| Skin tone | A palette of colours | Baked into the vertex colours where the body and face are painted skin (T-706; no extra material) |
| Face | Face variants per gender | A part merged into the body's mesh |
| Hairstyle | Styles per gender, by asset key | Attached to the `head` attach point, merged into the body's mesh |
| Hair colour | A palette of colours | Baked into the hairstyle's vertex colours |

- **Data, not code:** options live in `content/appearance.yaml` (ids and asset keys). The validator checks every asset key and
  that each face and hairstyle fits its gender's rig. A face or hairstyle fits when its model has the body's rig, or has no rig and is attached at `head` in the asset manifest. The options are in the client bundle (§5.4). Headgear can declare that it hides the hair (`hides_hair` in its manifest entry, §8).
- **Server authoritative:** the server checks creation choices against `appearance.yaml` and stores them with the character.
  A modified client cannot pick an option that does not exist.
- **Network:** appearance is a few ids in the view record of a **snapshot / appeared** entry; deltas never repeat it.
- **Client:** the creation screen shows a rotating 3D preview of the choices.

**On the server** (T-705). The ids are `AppearanceRecord { gender, skin, face, hair, hair_color }` (`Game.Shared`).
- **Creation:** `POST /account/characters` takes an optional `"appearance":{"gender":..,"skin":..,"face":..,"hair":..,"hair_color":..}`; every part is optional. `AppearanceRules.Choose` checks each id against
  `appearance.yaml` (the face and hairstyle must belong to the chosen gender; an id of the wrong palette is unknown) and answers a bad one with `invalid_appearance`; nothing is created then, and no name is taken.
  A part left out is the default of the gender chosen. **A request without a gender gets the first term of `Genders` (`male`)**: the vocabulary is code, its order is fixed, and every `appearance.yaml` must offer every
  term, so that gender's own defaults always exist; a content id would make the default depend on the file's key order. An appearance of the wrong type, a repeated or unknown field in it is `invalid_request`.
- **Storage:** five nullable id columns on `characters` (`gender`, `skin_tone_id`, `face_id`, `hairstyle_id`, `hair_color_id`), written in the creation's own transaction. Ids only: no content in the migration.
  A NULL part means "the content's default": characters that existed before T-705 have all five NULL and show the defaults of the content of the day; a load never writes anything back.
- **Content changes:** at login and in the list, `AppearanceRules.Resolve` shows the default for any stored id the file no longer offers (a removed gender: the default gender and its defaults; a face or hairstyle the
  gender lacks: the gender's default), and logs one warning per character and server run. The row keeps the old id, so putting the option back restores the look.
- **In the world:** `Entity.Appearance` is set once in the spawn initializer (`Gateway.SpawnCharacter`) from the login (`LoginResult.Appearance`; the dev login and a login without a character get
  `NewCharacters.Appearance`, the defaults), is never reassigned, follows warps (the entity object moves) and is read by nothing but the AOI: cosmetic only. A resume keeps the entity, so the looks too.
- **Wire:** an `EntityRecord` of a snapshot or an `appeared` entry for a player has `"appearance":{"gender":"female","skin":"skin.deep","face":"face.female_sly","hair":"hair.female_curls","hair_color":"haircolor.ember"}`
  (about 135 bytes of JSON a player; about 5 after deflate, as the ids repeat). No `ViewFields` flag exists for it and `changed` records never carry it, so a delta cannot repeat it. At the AOI cap of 50 visible
  entities a snapshot of all players grows by at most about 6.7 KB uncompressed, once. If it ever matters, an index into the bundle's lists (5 small numbers, about 20 bytes) is possible because the client checks
  the bundle's content hash in `hello`; the ids stay until measured on a phone. `GET /account/characters` has the same object per character, so a select screen can draw it.
- **Client** (`client/Core`): `EntityView.Appearance` (set at `Reset`), `CharacterRecord.Appearance`, and `AccountApi.Write(CreateCharacterRequest)` write and read the ids as they are; an id the
  bundle does not have is kept as a string and drawn as the default ("Drawing it" below).
- **Outfits** (T-708) are worn cosmetic state that changes, so they do not share fields with the fixed appearance: §7.5.2.

**Drawing it** (T-706; pure rules in `client/Core/Theme/Appearance.cs` and `Recolor.cs`, tested; drawing in `client/Scene/Theme`).
- *Ids to a look:* `AppearanceCatalog` turns the ids and the job (the display key) into a `CharacterLook` through the bundle and the theme chain: the gender's body, the face, the hairstyle, the colours and what the job
  adds. Looks are cached, so equal ones share one object. The visual is pooled by its **body**, and stays hidden until its look is on (a pooled one would show the last entity's face for some frames).
- *Fallbacks* (§7.7; each logged once, never a crash): an id the bundle lacks is drawn as the gender's default (an unknown gender as the first one); a face or hairstyle no theme has, or on another rig, as the gender's
  default one, and when that is missing too as the missing model, small, at the default point; a body no theme has means the character is drawn by its job's model, as before T-706. So is a player without ids
  (an older server) and a bundle without options. `draw_appearance` off draws everyone that way, for measuring.
- *Jobs.* Before T-706 a player was its job's model. Now the body, face and hair are the character's own, so a job shows as **what it adds**, data in `content/jobs/*.yaml`: `look.cloth_color` (the body's cloth takes it) and
  `look.pieces` (worn-piece models merged like equipment, each with an optional `gender` and an optional equipment `slot`; equipment worn in that slot is drawn instead of the piece). Why: a job model is a whole
  body with a head, so it cannot keep the chosen face, skin and hair, and a model per job, gender, face and hairstyle is not affordable. A colour needs no art and no draw call and works on every body at once, so it is what
  the template uses (its three jobs have the identity colours of `palette.json`); colour alone is weak for people who cannot tell colours apart and gives no silhouette (ADR-003 point 3), so a fork adds pieces (a hat, a
  tabard, pauldrons). The template ships no such art and lists no pieces. `model_asset_key` stays: it is the fallback body, and since T-711 (ADR-002 row 314) it is not preloaded while looks are drawn and every gender's body is in the theme (`ModelPreload`, with the
  players' kind fallback): the first character drawn by one loads it then, on a worker like any model and hidden until it is in. A job change puts the new job's look on the same body at once. Monsters and NPCs are drawn as before.
- *What is merged* (`DressPlan`), over the body: the face; the hairstyle, unless a worn piece or a drawn job piece has `hides_hair`; the job's pieces whose slot no equipment takes; the worn equipment (§7.5). All in one
  `GearMesher` merge, so a character stays one surface and one draw call (two with the outline).
- *Colours* (`Recolor`): baked into the vertex colours during the merge. A model says what it is painted with per colour role (VOCABULARY "Appearance colour roles") in its manifest entry's `recolor`, else the contract's
  colours (`AppearancePaint`, THEMES.md 1.7): a vertex painted with the first colour of a role's list takes the character's colour, the others in the list keep their relation to the first (a cheek on deep skin is a deep
  cheek). `skin` and `hair` apply to the body and the face, `cloth` to the body. A hairstyle takes the hair colour as a whole, keeping each vertex's relation to its most common colour, unless its entry names its `hair`
  paint (then the rest, a ribbon, stays). Glowing vertices (alpha 0) never change. Equipment and job pieces keep their own colours. A textured surface keeps its texture and has only its vertex colours replaced, so skin
  in an atlas is painted white there and by vertex colour; parts on the same atlas stay one surface (T-711, §7.5).
- *Cache.* The template offers 864 looks (2 x 3 faces x 4 hairstyles x 6 skin tones x 6 hair colours), times the jobs' looks, times gear; a crowd is bounded, the product is not. So the mesher keeps at most
  `look_cache_limit` (64) merged characters, least recently used out first, above the 50 entities a client can see: a crowd of all different characters merges each one once and never thrashes. Characters on screen hold their
  own meshes, and idle pooled ones too (`pool_idle_limit` per body). Placeholder cost about 0.14 MB of video memory a look, so 9 MB at the limit; art at the §9 limits (5k + 2k triangles) is about 0.4 MB a look, 26 MB.
  At most two merges run on workers at once, and making the results shares `instantiate_per_frame` (6), so a crowd of 40 new looks comes in over about half a second with no long frame. Measured: §9 "Looks".
- *Preview and pictures* (`LookView`): `ThemedEntities.Preview` makes a visual of its own from the same models, merges, materials and outline as the world's, in a small world lit by the theme's `scene.environment`, inside a
  `SubViewport`. The creation preview renders while it is open and turns by itself (`preview_turn_degrees`, 30 a second) or by drag (`Turntable`, pure); a picture in the character list renders once, when the character is
  dressed. Hidden, both free the character and shrink their render target. The preview wears the start job's look (the bundle's `start_job`) and no equipment.
- *Outfits (T-708)* join `DressPlan` as parts with a role of their own (§7.5.2).

### 7.5.2 Outfits (T-708)
Something a character owns and shows in place of its gear. **An outfit only ever changes how a character looks**: it has no modifiers, effects, requirements, price or drop, and cannot have them.
- **Data, not code** (`content/outfits/*.yaml`, `OutfitDef`): `id` (`outfit.*`), `name`, `description`, `icon_asset_key`, `takes_colors` (colour roles, VOCABULARY "Appearance colour roles") and `pieces`
  (`slot`, an "Outfit slots" term; `model_asset_key`; optional `gender`). The slots it covers are the ones that have a piece; a slot has one piece for everyone or at most one per gender. The loader knows no
  other field, so `modifiers`, `effects`, `buy_price` or a drop table in an outfit file is a load error, and no item, shop or drop table can name an `outfit.*` id (the reference rule says it is not an item).
  `validate` checks the terms, that pieces are `model.*` keys of the manifest, and (`validate --theme`) that they are worn pieces (`attach` or `skinned`), and warns when an outfit takes a colour none of its
  pieces is painted with. The template has two placeholder outfits from `themegen`, the Harvest Garland (a wreath and a sash) and the Rain Cloak (a hood that hides the hair, a coat cut per gender that takes
  the job's cloth colour, a cape). The first quest (First Hunt) gives both, so every character has a wardrobe early; a character that completed it before T-708 has none.
- **Slots** (VOCABULARY "Outfit slots") are a set of their own and vocabulary, not data: which equipment slots one covers is part of drawing a character, the client and the server must agree on the words,
  and the set is small and fixed. `head` covers `head_top`; `face` `head_mid` and `head_low`; `body`; `back` the garment; `feet` the footgear. **Nothing covers the weapon and the shield**: what a character
  fights with stays readable (and its weapon-type clips stay right), and an accessory has no look.
- **Owning** (`ProgressionState.Wardrobe`, `WardrobeState`): a set of ids, each once, saved with the character in T-702's transaction (`character_outfits`, §3). **The one primitive** is
  `WardrobeRules.Grant`: the reward type `outfit` uses it when a quest's rewards are paid (owed like its other rewards until then, so nothing is lost or given twice), and the server intent `GrantOutfit` uses it
  for whatever grants outfits elsewhere (T-709's store). Granting what is owned does nothing. Outfits belong to the **character**, not the account, as everything else that is saved: a deleted character's
  outfits go with it (soft-deleted with it, erased with the account). T-709 must therefore keep its purchases per account and deliver each to a character (and again, to another, after a deletion, as its
  restore rule says); the grant is idempotent, so delivering twice is harmless.
- **Content changes:** an owned or chosen id the content has lost is kept and ignored (not told, not drawn), like a look (T-705), and comes back with the content; the load notes it.
- **Choosing** (`OutfitSystem`, `set_outfit`): one intent sets what shows in a slot (an outfit, or nothing), or turns the switch that hides every outfit (the choices stay). The server checks, in order:
  the slot, that the outfit exists, that it is owned, that it has a piece for the slot, and one for the character's gender, and answers a refusal with an "Outfit reasons" term. Looks only, so it works in a
  fight and while dead; the session's rate limit is the only limit. A choice survives a save and a reconnect (it is part of the saved state, and a resume keeps the entity).
- **Others see it:** `Entity.Outfit` (an immutable `OutfitLook`, set by the outfit system and read only by the AOI) is the record field `outfit` (`{"head":"outfit.x"}`, left out when none shows) of a
  snapshot and an `appeared` entry, and a change has its own `ViewFields.Outfit` flag: the change record carries the whole field once (`{}` when it goes). Not the job change's way (the display key), because
  outfits are several ids and change apart from the job. Measured on the wire (`GatewayOutfitTests`): a player's record grows by 38 bytes with one outfit and 97 with three slots (from 313 for a snapshot of one
  record with looks and a weapon); a change is an 85-byte delta, once, to each observer. Hidden outfits send nothing.
- **Drawing** (`DressPlan`, `AppearanceCatalog.ResolveOutfit`): an outfit's pieces are parts of the one merged mesh (`PartRole.Outfit`), the gender's piece for each shown slot. In the drawing only they take
  the place of the worn equipment whose slots they cover (an item that fits several slots gives way when any of them is covered: the record does not say which it is worn in) and of the job's pieces of those
  slots; `hides_hair` counts for what is drawn (a hood hides the hair; a wreath over a cap that hides it lets it show). A piece takes the colours its outfit names through its `recolor` or the contract's paint,
  and is part of the `WornSet` key with them. Same merge, same cache, still one surface and one draw call. An outfit the bundle lacks, or with no piece for the slot and gender, is left out (one warning) and the
  gear shows. Measured: §9 "Outfits".

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

**Entity visuals** (T-207; a player with looks is drawn on its own body instead of its job's model, §7.5.1): `display_key` -> the bundle's model key (a job's `model`, a monster's or NPC's `model`, an item's `ground`) -> the theme's file -> a pooled scene
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

**Appearance fields** (T-706, §7.5.1; THEMES.md 1.7): `"hides_hair": true` on a worn piece hides the hairstyle while it is worn (`default` has one example, `model.equip.leather_cap`); `"recolor": { "skin": ["#f1c7a1",
"#e9a08a"], "cloth": "#efe3c8" }` on a body, face or hairstyle names what it is painted with, per colour role (an empty list: nothing takes that colour). `validate` checks both, warns about a body without its skin
paint (or without cloth paint while a job has a cloth colour) and about a face or hairstyle with a texture of its own, and fails a body that with its largest face and hairstyle is over the §9 character budgets.

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
| Character | LOD0 ≤ 5k triangles (the nearest 8 characters), LOD1 ≤ 2k triangles (everyone else); 1 material, ≤ 1024² texture, one atlas image shared by all of a character's parts (THEMES.md §1.9, T-711). Worst case 8 × 5k + 32 × 2k = 104k, leaving ≥ 45k of the 150k for map, props and VFX (ADR-002) |
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

**Looks (T-706, §7.5.1), measured** on the same machine (AMD Radeon integrated, 1280x720 in a window off the screen, 30 fps cap), `tools/loadtest --bots 70 --fighters 0.7 --spare-slots 3` and the client's
`--measure 36 --measure-looks --measure-dress <dagger, cap, vest, buckler>`: one run on one crowd, 10 s a phase. Bots move between phases, so each peak names how many entities were on screen.

| Phase | Players in view, different looks | Draw calls avg / peak (on screen) | Triangles peak | Worst frame | Putting the looks on | Static / video memory |
|---|---|---|---|---|---|---|
| Before: job models (`draw_appearance=0`) | 34.0, 0 | 88.7 / 104 (42) | 56.1k | 36.1 ms | 0.41 s | 57.1 / 36.4 MB |
| Own looks, all the default | 35.2, 1 | 69.1 / 91 (31) | 66.1k | 35.9 ms | 0.29 s | 57.3 / 36.6 MB |
| All different | 39.4, 38 | 74.3 / 84 (41) | 65.2k | 35.0 ms | 0.46 s, worst frame 34.3 ms; 67 merges, 88 ms of main thread in all | 58.5 / 45.3 MB |
| All different, dressed | 38.5, 38 | 80.7 / 90 (36) | 84.7k | 35.1 ms | 0.77 s, worst frame 73.7 ms | 59.7 / 52.7 MB |

Every player is one surface in every phase, so looks add no draw call (the differences are monsters, drops and effects on screen); 30.0 fps throughout; a face and a hairstyle add about 350 triangles at each level (the
placeholder parts have no LOD1); 64 merged characters cached. The single 73.7 ms frame is 38 characters changing gear at once. Not run on a phone (ADR-002 row 112).

**Outfits (T-708, §7.5.2), measured** on the same machine and the same way as "Looks" (one windowed run off the screen, `--measure 36 --measure-looks --measure-dress <dagger, cap, vest, buckler>`), with a last
phase that also puts every player in the template's outfits in every slot they have for its gender (head, body and back: they replace the cap and the vest; the dagger and buckler stay):

| Phase | Players in view, different looks | Draw calls avg / peak (on screen) | Triangles peak | Worst frame | Putting it on | Static / video memory |
|---|---|---|---|---|---|---|
| Before: job models | 37.9, 0 | 75.0 / 82 (32) | 53.3k | 38.1 ms | 0.49 s | 56.0 / 35.8 MB |
| All different, dressed (no outfits) | 39.7, 34 | 73.9 / 77 (37) | 73.6k | 43.6 ms | 0.65 s, worst frame 112.6 ms | 59.3 / 49.7 MB |
| All different, dressed, in outfits | 36.7, 26 | 72.4 / 79 (35) | 96.9k | 48.7 ms | 0.61 s, worst frame 41.8 ms; 43 merges, 90 ms of main thread in all | 58.8 / 52.0 MB |

Every player is one surface in every phase, so outfits add no draw call; 30.0 fps throughout. The placeholder outfits are rounder than the gear they replace (a hood, a long coat, a cape), hence the triangles,
still well inside 150k. Not run on a phone (ADR-002 row 112).

**Textured characters (T-711, §7.5), measured** on the same machine and the same way as "Outfits", on the textured test theme (`tools/themegen --textured`: every character part embeds one 64² atlas in its own
file; two non-default hairstyles, Curls and Topknot, another image; job models stay `default`'s, untextured), one windowed run moved off the screen by the client (`--offscreen`: Godot 4.7.2 put a
window given `--position` off every screen back on it, ADR-002 row 346; a watcher saw the window only off screen for the whole run). The last phase is the same crowd merged by file as before T-711
(`merge_by_image=0`):

| Phase | Players in view, different looks | Surfaces per player | Draw calls avg / peak (on screen) | Triangles peak | Worst frame | Putting it on | Static / video memory |
|---|---|---|---|---|---|---|---|
| Job models (loaded now on demand) | 37.3, 0 | 2 (job model untextured, dagger textured) | 71.2 / 74 (29) | 48.8k | 83.0 ms | 0.52 s, worst frame 34.5 ms | 57.0 / 35.8 MB |
| Own looks, all the default | 38.2, 1 | 1 | 73.7 / 76 (37) | 60.9k | 80.5 ms | 0.28 s | 57.0 / 36.1 MB |
| All different | 37.9, 43 | 1 (33), 2 (10: the other image) | 76.9 / 80 (36) | 62.6k | 66.4 ms | 0.89 s, worst frame 35.1 ms | 59.8 / 45.4 MB |
| All different, dressed | 35.5, 42 | 1 (the cap hides the hair) | 79.5 / 84 (42) | 83.4k | 41.0 ms | 0.83 s, worst frame 33.4 ms | 61.2 / 55.9 MB |
| All different, dressed, in outfits | 38.7, 42 | 1 (33), 2 (10) | 80.9 / 87 (39) | 107.4k | 54.8 ms | 0.92 s, worst frame 36.1 ms; 65 merges, 73 ms of main thread | 63.2 / 71.5 MB |
| The same, merged by file (before T-711) | 38.6, 38 | **8** (39) | 85.2 / 95 (42) | 110.1k | 89.5 ms | 2.07 s, worst frame **559.9 ms**; 62 merges, 1,332 ms of main thread | 66.4 / 51.6 MB |

By image every player is one surface, two with a part on another image; by file a character in outfits was eight (body, face, hair and five pieces, each file's own copy of the atlas). The engine's
draw-call monitor rose by far less than the surfaces did (peak 87 → 95 for about 7 more surfaces per character on screen): like the outline pass (T-222), Godot's Mobile renderer on this desktop does
not count what a phone pays for each surface, so the surfaces are the budget's measure and a phone run (T-803) settles the count. Telling the images apart read 23 textures as 2 images in 32 ms of main
thread over the run (once per texture). The job models are no longer preloaded: none is loaded before the first phase; the first character drawn by one waited 0.52 s for it, hidden, with no frame over 35 ms
(`run-e2e.ps1 -Run textured`, headless, where loading runs on the main thread: 0.06 s). The 80 ms worst frames are outside the changes (the crowd moving in, as in earlier runs); 30 fps throughout.
A headless run of the same (no draw calls or triangles without a renderer) gave the same surfaces and 64.7 MB static memory dressed in outfits against 75.7 MB merged by file. Not run on a phone (ADR-002 row 112).

## 10. Networking on mobile

- **TLS required:** `wss://` everywhere except local dev. Cleartext `ws://` is allowed only in
  debug builds, and **the client enforces that itself** (T-216): Godot's WebSocket uses native sockets, which Android's network-security
  config and iOS ATS do not police. A release build shows a refusal screen for a `ws://` or invalid server address and opens no socket;
  the allowance is set once at start-up from the engine's debug-build flag and cannot be changed by `client_settings.json` or the command line
  (`TransportPolicy`, MOBILE_BUILD §8.4). The iOS preset still states ATS explicitly; no Android network-security config is shipped (it would need the Gradle export and would not cover these sockets).
- **Disconnects are normal.** When a socket drops or the app is backgrounded, the server keeps
  the character in the world for a grace period (`grace-period-ms`, server config, default 60 s), then saves it and logs it out.
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
  weapon type, and `worn`, the item ids it wears that have a worn visual, left out when empty: the client finds the visuals in its bundle for T-403, and never gets stats; and, in a snapshot or an `appeared` entry only,
  `appearance`, its looks as ids, T-705; and `outfit`, the outfits it shows by slot, in every record and, when they change, in a change, T-708); a delta is
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
  | `hello` | C→S (first) | `version`, `content` (the hash of the client bundle, §5.4; optional in development), and exactly one of: `session` and `character` (an account's session token and its character's id, T-701), `resume` (a resume token), `name` (the dev login, development only) |
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
  | `start_autopilot` | C→S | `quests` (active quest ids, in the order to play), `skills` (`[["skill.x",level],...]`: the shortcut slots in order; level 0 = the learned one; may be left out). Replaces a run in progress; may come back `rejected` with an "Autopilot reasons" term and the `quest` or `skill` (T-604) |
  | `stop_autopilot` | C→S | none: switch it off; ignored when it is off (T-604) |
  | `set_outfit` | C→S | either `slot` (an "Outfit slots" term) and `outfit` (left out: nothing), or `hidden` (true hides every outfit, false shows the choices again); a request, it may come back `rejected` (`intent` `set_outfit`, an "Outfit reasons" term, `slot`, `item` the outfit) (T-708) |
  | `chat` | C→S | `channel` (a "Chat channels" term a player may send on), `to` (a whisper's character name, any letter case), `text`: an intent; the server answers with `chat_line` to everyone it reaches (the sender included) or `chat_refused` (T-901, "Chat" below) |
  | `block` | C→S | `name`: that character's lines no longer reach this account; answered by `chat_state` or `chat_refused` (T-901) |
  | `unblock` | C→S | `name` (T-901) |
  | `report` | C→S | `line` (a `chat_line`'s number this client received): the server stores its own copy of the line; answered by `chat_reported` or `chat_refused` (T-901) |
  | `party_invite` | C→S | `name` (a character's name) or `target` (a player's entity id on this map), exactly one: answered by `party_notice` `invite_sent` or `party_refused` (T-902, §6.9) |
  | `party_answer` | C→S | `accept` (true or false): the invite this character has |
  | `party_leave` | C→S | (none): leave the party |
  | `party_kick` | C→S | `target` (a member's entity id), leader only |
  | `party_lead` | C→S | `target` (a member who plays now), leader only |
  | `trade_ask` | C→S | `target` (a player's entity id on this map instance): answered by `trade_notice` `ask_sent` or `trade_refused` (T-903, §6.10) |
  | `trade_answer` | C→S | `accept` (true or false): the ask this character has |
  | `trade_offer` | C→S | `entry` (a carried entry's id), `quantity` (0 takes it out): replaces that entry's line in this side's offer |
  | `trade_currency` | C→S | `currency` (0 to what it carries): the currency in this side's offer |
  | `trade_lock` / `trade_unlock` | C→S | (none): lock this side's offer / unlock both |
  | `trade_confirm` | C→S | `version` (of the `trade` message whose offers the player saw); `stale` when they changed since |
  | `trade_cancel` | C→S | (none): end this character's ask or trade |
  | `guild_found` | C→S | `name`, `emblem` (a `guild_emblems` key): answered by `guild` and `guild_news` `founded`, or `guild_refused` (T-904, §6.11) |
  | `guild_invite` | C→S | `name` or `target` (a player's entity id on this map), exactly one: answered by `guild_news` `invite_sent` or `guild_refused` |
  | `guild_answer` | C→S | `accept`: the invite this character has |
  | `guild_leave` | C→S | (none) |
  | `guild_kick` | C→S | `member` (a member number) |
  | `guild_rank` | C→S | `member`, `rank` (a rank place, 1 or more) |
  | `guild_ranks` | C→S | `ranks` (`[{"name":"Officer","permissions":["invite"]},...]`, the places below the leader's), leader only |
  | `guild_lead` | C→S | `member`, leader only |
  | `guild_notice` | C→S | `text` (empty clears it) |
  | `guild_storage_open` | C→S | none: answered by `guild_storage` (T-905, §6.11); also starts or renews the lease under which others' moves are sent (T-810) |
  | `guild_deposit` | C→S | `entry` and `quantity`, or `currency` (exactly one of the two; `storage_deposit`): answered by `guild_storage` once it landed, or `guild_refused` (T-905) |
  | `guild_withdraw` | C→S | `entry` and `quantity` (`storage_items`), or `currency` (`storage_currency`): as `guild_deposit` (T-905) |
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
  | `wardrobe` | S→C (owner) | `owned` (outfit ids the content has, by id), `chosen` (`{"<slot>":"outfit.x"}`, also while hidden), `hidden` (only when true). After every snapshot and every change; it replaces what the client had (T-708) |
  | `autopilot` | S→C (owner) | `mode` (an "Autopilot modes" term), `quest` (while running or paused), `doing` (an "Autopilot activities" term, while running), `reason` (why it paused or ended). On change only, and once after the first snapshot of a connection that took over a character that has had a run. Never anything about monsters, spawns or routes (T-604) |
  | `skills` | S→C (owner) | `job`, `points` (unspent), `levels` (`[["skill.x",2],...]`), `cooldowns` (`[["skill.x",ms left],...]`), `jobs` (the jobs `change_job` would accept now). After every snapshot, a learn, a job change and a level (T-502) |
  | `stats` | S→C (owner) | `points` (unspent stat points), `stats` (`[["str",3,2],...]`: stat id, base value, the cost of its next +1, 0 at the maximum), `derived` (`[["atk",12],...]`: `DerivedStats.Panel`, floored from the sheet). After every snapshot, a raise, a stat reset, a level, a job change, a learned skill, a skill reset and a gear change (T-503). The client never computes a cost or a derived value |
  | `cooldown` | S→C (owner) | `skill`, `ms`: the skill went on cooldown when its cast ended (T-502) |
  | `chat_line` | S→C | `line` (its number on this server), `channel`, `from` (the sender's entity id), `name` (its character name), `to` (a whisper's receiver), `text` (the server's cleaned copy). The same bytes to everyone it reaches; never before the server took it (T-901) |
  | `chat_refused` | S→C (own) | `intent` (`chat`, `block`, `unblock`, `report`), `reason` (a "Chat reasons" term), `retry_after` (seconds: `muted`, `rate_limited`), `channel`, `to`, `line` (T-901) |
  | `chat_state` | S→C (own) | `max_length` (the longest line, UTF-16 code units), `blocked` (the names this account blocks). After every `welcome` and every block change; it replaces what the client had (T-901) |
  | `chat_reported` | S→C (own) | `line`: the report is stored (T-901) |
  | `party` | S→C (members) | `party` (its number), `leader` (entity id), `members` (`[{"id":42,"name":"Ada"},...]`, party order); in no party `{"type":"party","members":[]}`. After every change and every `welcome` (T-902) |
  | `party_members` | S→C (members) | `status` (`[{"id":43,"hp":80,"sp":100,"map":"map.x","cell":[3,4]},...]`: whole percent; `dead` and `away` only when true, `away` alone in its grace period): the other members, at most every `status-interval-ms` and only on change |
  | `party_invited` | S→C (the invited) | `from` (entity id), `name`, `expires_in` (seconds) |
  | `party_notice` | S→C | `notice` (a "Party notices" term), `name` (whom it is about) |
  | `party_refused` | S→C (own) | `intent` (a `party_*` type), `reason` (a "Party reasons" term), `retry_after` (`rate_limited`), `name` (the name an invite named) |
  | `trade_asked` | S→C (the asked) | `from` (entity id), `name`, `expires_in` (seconds) |
  | `trade` | S→C (the two) | `partner` (entity id), `name`, `version`, `mine` and `theirs` (`{"items":[{"entry":1207,"item":"item.x","quantity":3,"cards":[...],"refine":4}],"currency":0,"locked":true,"confirmed":true}`, flags and cards only when set): after every change |
  | `trade_notice` | S→C (own) | `notice` (a "Trade notices" term), `name` (the other character) |
  | `trade_closed` | S→C (the two) | `reason` (a "Trade ends" term), `name` (the other side), `own` (true when this side caused it); after the swap's `inventory_changed` |
  | `trade_refused` | S→C (own) | `intent` (a `trade_*` type), `reason` (a "Trade reasons" term), `retry_after` (`rate_limited`) |
  | `guild` | S→C (members) | `guild`: `{"id","name","emblem","notice","own" (this character's member number),"ranks":[{"name","permissions"}],"members":[{"member","name","rank","level","online"}]}` (`online` only when true); no `guild` field when in none. After every change, welcome and online change (T-904) |
  | `guild_invited` | S→C (the invited) | `name` (the inviter), `title` (the guild), `emblem`, `expires_in` (seconds) |
  | `guild_news` | S→C | `news` (a "Guild news" term), `name` (whom it is about) |
  | `guild_refused` | S→C (own) | `intent` (a `guild_*` type), `reason` (a "Guild reasons" term), `retry_after` (`rate_limited`), `name` (an invited character, or the name taken) |
  | `guild_tags` | S→C | `tags` (`[{"entity":42,"emblem":"icon.emblem.oak"}]`: the guild members on this map instance), when the list changes for this player |
  | `guild_storage` | S→C (own) | `size`, `currency`, `entries` (inventory entry records, oldest first), `log` (only for `storage_log`: `[{"at" (unix s),"name" (empty once erased),"move","item","entry","quantity","currency"}]`, newest first) (T-905) |
  | `pong` | S→C | `seq` |
  | `bye` | S→C (last) | `reason` (VOCABULARY "Bye reasons"), followed by the close frame |

  A view record is `{"id":7,"kind":"monster","display_key":"monster.x","cell":[3,4],"facing":"e","next":[4,4],"speed":3.51,"hp":0.37}`;
  a ground item is `{"id":9,"kind":"item","display_key":"item.x","cell":[4,5],"facing":"s","quantity":2,"mine":true}`; a player is
  `{"id":42,"kind":"player","display_key":"job.wanderer","cell":[12,12],"facing":"n","hp":1,"weapon":"dagger","worn":["item.bone_dirk"]}`; a monster with a status adds
  `"statuses":[{"id":"status.poison","stacks":1,"left_ms":7400,"debuff":true}]`.
- **Session lifecycle:**
  1. The client connects and must send `hello` within `hello-timeout-ms`.
  2. The server checks the protocol version, then the content hash (T-209): a `content` that differs from the server's ends in `bye update_required`; a missing one is accepted in development (logged as a warning) and refused elsewhere. Then it checks the login: a live session and one of its account's characters (T-701, "Accounts" below), or the dev
     login (`name`), which any environment but development refuses. The check may read the database; it runs on the session's receive loop, never on a tick. Messages that arrive meanwhile wait for it, and a
     connection that drops, times out or is shut down meanwhile leaves no slot and no character behind. A `hello` with `resume` goes to step 6.
     An account's character that the gateway does not hold is then claimed and loaded (T-702, §3): `bye load_failed`, `save_pending` or (deleted meanwhile) `login_refused` when that fails, with nothing changed.
  3. The server queues the character with `GameWorld.Spawn(..., initialize)` at `balance.yaml` `new_character`, or where a loaded one was. On the start
     instance's tick, the initializer sets its stats (from the stats pipeline), its display key (the job id) and its view sink, and
     queues `welcome`. The AOI then sends the first `snapshot` in phase 5 of that tick, and deltas follow.
  4. `move`/`stop`/`attack`/`stop_attack`/`respawn` and the item intents are parsed and checked on the socket thread (shape; a cell inside the current map; an
     attack target the client currently sees, not itself, a monster) and queued as `MoveTo`/`StopMove`+`StopAttack`/`AttackTarget`/
     `StopAttack`/`Respawn` wherever the character is now. When the connection drops, the grace period stops walking, attacking and the autopilot.
     Before a move, stop, attack, skill, item, dialogue, shop or respawn message's own intent goes `ManualInput`, which ends an autopilot run (§6.4.2); `ping`, `learn_skill`, `raise_stat`, `change_job` and `abandon_quest` do not. Warps are followed through the world's entity directory. Each session has a token-bucket rate
     limit: messages over it are dropped and counted, and sustained flooding ends in `bye rate_limit`.
  5. Malformed JSON, an unknown type, an oversize message, a wrong version, a message before `hello`, or silence longer than
     `idle-timeout-ms` all end in `bye <reason>` and the close frame.
  6. **Grace period and resume** (T-210). Every `welcome` carries a **resume token** for the character.
     - **The connection ends without `logout`** (the client closed or vanished, idle `timeout`, `too_slow`, a protocol error such as
       `malformed` or `rate_limit`, `server_error`): the character is **not** removed. It is stopped (`StopMove`, `StopAttack`), its view is detached
       (`AttachView` with no sink), and it stays where it is, visible to others, for `grace-period-ms`. When that ends it is saved and leaves
       the world (§3). A buggy or hostile message must not delete a character, so protocol errors are no exception.
       A character whose session ended before its `welcome` was queued (no token yet) is removed at once.
     - **`logout`**: `bye logout`, the close frame, and the character leaves on its instance's next tick (a saved one within a few ticks, §3); its token stops working.
     - **Resume:** `hello` with `resume` (the protocol version and content hash are still checked first). If the token is live, the
       new session takes the character over wherever it is now (it may have warped since the drop): `welcome` (same `id`, current
       `map`, a **new** token), then a full `snapshot`, then deltas. A session still playing that character (the server had not
       noticed the drop yet) gets `bye replaced`. A character never has two sessions. An unknown, used or expired token gets
       `bye resume_failed`. Resume works in every environment; the token check sits in `ILoginService` (T-701 plugs in there).
     - **Abuse:** failed resumes are counted per remote address in a token bucket (`resume-failure-burst`,
       `resume-failures-per-minute`); an address that runs out gets `bye rate_limit` and may not resume for `resume-lockout-ms`.
     - **Shutdown:** every session gets `bye shutdown`; characters in their grace period leave too. Each is saved as it leaves, and the server waits for the saves (§3).
     - **The token:** 256 bits from the OS CSPRNG, base64url without padding (43 characters), bound to one character, **single use and
       rotated** (every successful resume returns a new one). The server keeps only its SHA-256 and
       compares in constant time; tokens never appear in logs (at most a 6-hex-digit fingerprint of the hash). Tokens live in server
       memory only: **a server restart invalidates every token**. T-701 decided to keep it so: a resume token stands for a place in a running world, and after
       a restart there is none. What is durable is the account's session: after a restart the client says `hello` with the same `session` and `character`, without asking the player.
     - **Acknowledgement** (T-214): the `welcome` of a resume can be lost if the connection drops again before it arrives. So the token
       the resume used keeps working until the client's **first message on the new session** (any message: that is the implicit
       acknowledgement), then it stops. Resuming with it again before that rotates once more and kills the token issued in between. It
       never outlives the character's grace period: both tokens die when the character leaves.
     - **Capacity** (T-214): `max-sessions` counts characters, not sockets: every character the server holds, played or in its grace
       period, plus logins still joining. A new login on a full server gets `bye server_full` at its `hello`; nobody is evicted to make
       room. A resume is always let in, since it reuses its character's slot. Connections that have not said `hello` are refused with
       `server_full` only when more than `max-sessions` of them are open at once.
- **Accounts** (T-701, `Game.Server/Accounts`). HTTPS on the gateway's own host and port, so the transport rule is the socket's: TLS everywhere, plain HTTP on loopback in
  development only. Bodies are JSON objects of at most 2 KB that must arrive within 5 s; answers are JSON with `Cache-Control: no-store`. The session token travels as
  `Authorization: Bearer <token>`, never in a URL. A refusal is `{"error": <an "Account errors" term>}`, with `retry_after` (seconds) when waiting helps.

  | endpoint | request | answer |
  |----------|---------|--------|
  | `POST /account/register` | `username`, `password` | 201 `token`, `expires_in` (seconds): registered and signed in |
  | `POST /account/login` | `username`, `password` | 200 `token`, `expires_in` |
  | `POST /account/logout` | token | 204; the session has ended |
  | `POST /account/password` | token, `current`, `new` | 200 `token`, `expires_in`: the password is changed, every session of the account has ended, and this is the caller's new one (T-710) |
  | `POST /account/delete` | token, `password` | 200 `erased_in` (seconds): every session of the account has ended, nobody can sign in, and it will be erased (T-703) |
  | `POST /account/keep` | `username`, `password` | 200 `token`, `expires_in`: the deletion is taken back and the player signed in (T-703) |
  | `GET /account/characters` | token | 200 `slots`, `name_min`, `name_max`, `characters` (`[{id, slot, name, job, appearance}]`, by slot) |
  | `POST /account/characters` | token, `name`, optional `appearance` (§7.5.1) | 201 `id`, `slot`, `name`, `job`, `appearance` (the lowest free slot); 400 `invalid_appearance` |
  | `DELETE /account/characters/{id}` | token | 204 (a soft delete: the row and its name stay); `character_in_use` while it is in the world, on this server or another (T-710) |

  Status: 400 invalid, 401 `invalid_credentials` / `invalid_session`, 404 `unknown_character`, 409 taken / full / in use / `account_deleting`, 429 `rate_limited`, 503 `unavailable`.
  - **Slots and names** come from `balance.yaml` `characters` (slots, name lengths, letters, separators). Names that differ in letter case are the same name.
  - **An account plays one character at a time.** A login with the character the gateway already holds (played elsewhere, or in its grace period) takes it over where it
    stands, as a resume would: the old session gets `bye replaced` and its resume token stops working. A login with another character ends the first one (`bye replaced`,
    and it leaves the world). A login of the same account that is still joining is replaced too, so there is never a second one.
  - **A session that ends takes its game along** (T-710). Signing out, being pushed out by a newer sign-in, and a password change end the connection that signed in with the session
    (`bye session_ended`). Its character, also one in its grace period, leaves the world at once, saved like on any other leave, and its resume token stops working. A resume keeps the session
    the character was entered with; an account login that takes the character over brings its own. A login still pending with that session ends too. The gateway is told after the database has
    the session ended (`Gateway.EndSessions`); nothing is looked up per tick or per message. Two limits: a session whose 24 hours merely run out does not end a game in progress (nobody decided
    to end it, and being put out in the middle of a fight helps nobody; the next login needs a new one, and a password change ends that game too), and a session ended through another server
    process on the same database does not reach the game played on this one.
  - **Password change** (T-710). The caller proves the current password although it is signed in, so a stolen session cannot take the account. It gets a new session at once: it has just shown
    the password, and everyone else is out. If that last step fails the answer is `invalid_session` (sign in with the new password). The account's lock for wrong current passwords is kept under
    its id, apart from the sign-in's lock under its username: a stolen session must not be able to lock the owner out. The address's lock is the same one.
  - **Account deletion** (T-703; what a fork owes the stores: docs/STORE_COMPLIANCE.md). A request proves the password again, under the rules of a password change, ends every session with its game and sets `accounts.erase_after`
    (`--account-erase-days`, default 14). While it is set a sign-in answers `account_deleting`, a session cannot be added and a character cannot be made, whatever raced the request: all of them lock the account's row. `keep`
    takes it back. `AccountEraser` then erases what is due in one transaction per account, a pass a minute on a task of its own: the account's row first (one that another server is at is skipped), then its characters in the
    order a save locks them. A character held by a live run puts it off; entering the world takes the same lock, so a character is entered or erased, never both; a save that comes late finds nothing and is refused as lost.
    Usernames and character names become free. `account_deletions` keeps the account's id and the two times, for support and for cleaning a restored backup. The cascade rules stay: nothing but this removes an account.
  - **The client** (`client/Core/Accounts`, `Scene/AccountPanel`) keeps the session token in memory only: Godot offers no secure storage (Keychain, Keystore) without
    native code. The password is never kept. A restarted app signs in again. `TransportPolicy.AccountBase` gives the endpoints' address: a release build has none for `ws://`.
- **Chat** (T-901, `Game.Server/Chat`, `ChatService`; the client's `ChatBook` and `ChatPanel`). The server decides every line; the client sends an intent and shows a line only when the server sends it back.
  - **Order of the checks** (each refusal is a `chat_refused`, and the line goes nowhere): the **mute** (`ModerationChecks.SpeakAsync`, T-806: cached 5 s per account, so a burst costs one read, and it
    fails closed: `unavailable` when the database is away; a muted sender gets `muted` with `retry_after`), the **rate** (`ChatLimits`), the **text** (`ChatText`), the **channel**, then **blocks**.
    Sending a line reads nothing else: block lists are read when an account signs in (on the receive loop, before its character enters) and kept in memory while it plays; a sweep a minute
    forgets those of accounts that left. A session's chat messages are handled in order, off its receive loop (the mute check may read the database), never on a tick.
  - **Channels** (a "Chat channels" term each). `map`: every playing character on the sender's **map instance**, not its area of interest: the AOI window is a few buckets and the crowd
    cap cuts players out of it unpredictably, so a conversation beside someone could miss them; and the AOI's state is the tick thread's. A map is what players see as "here", the line
    costs one pass over the gateway's characters (bounded by the rate limit), and a fork that wants a local channel can register one (below). `whisper`: one playing character by name
    (a character in its grace period is not playing); nobody of that name, and one who blocks the sender, both answer `not_online`, so a block does not show; oneself is `invalid_target`.
    `system`: the server's; no player sends on it. Staff's announcements (T-806) stay their own `announcement` message (they also reach players not looking at chat); the client also
    writes them into the chat log under "Server". One server process: a whisper does not cross to another process on the same database.
  - **The seam** for party (T-902) and guild (T-904): `ChatService.Register(kind, IChannelMembers)`, with a new "Chat channels" term. Chat asks the members for the characters who hear a
    line (the speaker included; empty when the speaker is in none, `invalid_channel`) and routes it like the map, with every rule above. Chat knows nothing of parties or guilds; a test
    double shows it works.
  - **Text** (`ChatText`): NFC; format characters (zero-width ones, the bidirectional controls such as U+202E, the BOM, tags, the soft hyphen), private use, unassigned code points and the
    letters that draw nothing are **removed**, not refused; line breaks, tabs, other controls and every space become one space; combining marks are kept up to `marks-per-letter` after a
    letter; trimmed; then `empty` or `too_long` (`max-length`, UTF-16 code units, never cut). Right-to-left letters stay. **No word filter**: a list is language- and community-specific, easy
    to get round and wrong about innocent words; moderation is reports, mutes and blocks. A fork that wants one adds it after the text rules, with its list in content or theme data.
  - **Rate**: a token bucket per **account** (`burst` lines, `lines-per-minute` back): a connection is cheap to replace (a resume or a new sign-in brings a fresh one), an account is not. Blocks,
    unblocks and reports take from it too. A dev login is counted by its entity.
  - **Blocks** (`chat_blocks`): on the server and saved, per account and blocked character, at most `block-limit`; so they hold on every device, survive reinstalling the app, and let a
    whisper be refused for the sender. They go when either account is erased.
  - **Reports** (`chat_reports`): `report` names a line by number. The server keeps every line in memory for `report-window-seconds` (600) and, per account, the numbers of the last
    `reportable-lines-per-player` (200) lines that reached it, at most `recent-lines-max` (20,000) lines in all; a report must name one of those, not one's own, once. It stores the server's
    copy with who said it, who reported it (account and character ids) and both times; staff read and close it through the admin API (§12.9). The client never sends a text.
  - **Nothing a player wrote is logged**: chat logs ids and numbers only (a failure's exception type, never its message). A report is the only place a line is kept. The client keeps the
    last 50 lines of this session in memory, and empties them when the player signs out.
  - **Settings**: `ChatOptions`, server configuration (`--chat-<name>` or `GAME_CHAT_<NAME>`): `max-length` 120, `marks-per-letter` 3, `lines-per-minute` 20, `burst` 5, `block-limit` 100,
    `report-window-seconds` 600, `reportable-lines-per-player` 200, `recent-lines-max` 20,000, `closed-report-days` 30. The server refuses to start when `max-length` cannot fit the gateway's
    `max-message-bytes` (a code unit may take six bytes escaped).
  - **The panel** (§7.4): the Chat button beside the quest tracker, the last two lines in the flash line's place while no flash or announcement shows (they take no touches), and, opened by the button, a panel at the top of the safe area with the input
    first and the log below, cut to what the on-screen keyboard leaves. The log is one `RichTextLabel` without BBCode: names (the server's, a colour per channel) and texts go in with
    `AddText`, so nothing a player typed can colour, style or move a name. A tap on a name gives Whisper, Report and Block (or Unblock).
- **Client resume flow** (for T-206):
  1. Keep the token from the latest `welcome` **in memory only** (never on disk or in preferences); replace it on every `welcome`.
  2. On a socket drop, or when the app comes back to the foreground and the socket is gone, reconnect with backoff (e.g. 0.5 s,
     1 s, 2 s, 4 s, capped, with jitter) and send `hello` with `resume`, `version` and `content`. Stop trying once the grace period
     must be over.
  3. On `welcome`: keep the new token, keep the `id`, load `map` if it changed, then replace the view with the `snapshot` that follows.
  4. On `bye resume_failed` (or no token, e.g. the app was killed): go to the login screen. On `bye rate_limit` during a resume: same,
     and do not retry automatically.
  5. On `bye logout` or `bye replaced`: do not reconnect (`replaced` means the same player is on another connection).
     On `bye update_required`: go to the store; `shutdown`: show a message and retry later with a normal login. On `bye session_ended`: go to the sign-in screen.
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
    A `bye` with `retry_after` writes it after the reason, separated by a space (`banned 3600`; `ProtocolJson.CloseReasonOf`), so a ban's time left survives the same loss (T-806's live run found it).
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
- **Passwords** (T-701): Argon2id through NSec (libsodium): 19 MiB, 2 passes, 1 lane, 16-byte salt, 32-byte hash, stored in PHC format with its parameters and rehashed at
  the next login when they change. At most a quarter of the cores hash at once, with a bounded line behind them, so a flood of logins does not take the tick's cores.
  Usernames are stored and compared in one normalised form (NFKC, lower case, `a-z 0-9 . _ -`). No e-mail or other personal data.
- **Session tokens, not credentials, on the socket** (T-701): 256 random bits, only the SHA-256 stored, 24 hours, at most 10 per account; logout and a password change end them, with the game played on them (T-710, §10).
- **Guessing** (T-701): an unknown username and a wrong password get the same answer after the same work (a dummy hash). Failed logins lock the username (whether or not it
  exists) after 5 and the address (an IPv6 /64 counts as one) after 20, for 5 s doubling to 5 min; attempts count when they start, so a burst gains nothing. A wrong current password at a
  password change counts the same way (T-710). Requests and registrations
  per address, creations per account and failed `hello`s per address are limited too. Every table of the limiter is bounded.
- **Nothing secret in logs:** no password, token, connection string or username (accounts appear by id); records that hold a secret do not print it.
- Item operations are atomic in the tick and saved whole, in one transaction, giver before receiver (§3 "Between characters"); every item has a permanent id, unique in the database (dupe auditing:
  a save with another character's item is refused and logged as `DUPLICATED ITEMS`).
- The client is untrusted (CLAUDE.md rule 11); the client bundle holds display data only.
- No plaintext outside debug builds (§10): release clients refuse `ws://`. The Android APK carries only what runs on a device (arm64-v8a; the Mono static
  archives are stripped, MOBILE_BUILD §8.3), and CI exports it on every push (MOBILE_BUILD §9).
- Store rules (docs/STORE_COMPLIANCE.md): in-app account deletion (built: T-703); Sign in with Apple only if another
  third-party login is offered (the template has none); iOS privacy manifest and Play data-safety form kept current.

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

### 12.6 The admin API as built (T-804)
Endpoints, headers and local use: **docs/ADMIN_API.md**. Code: `src/Game.Server/Admin`; contracts and terms in `Game.Shared` (`AdminPaths`, `AdminHeaders`, "Staff roles", "Admin actions", "Admin errors").
- **Listener.** A second Kestrel listener (`AdminHost`), off unless `--admin-port` is set, never on the game's port, serving `/admin/v1` only. Loopback by default; development allows only loopback and
  plain HTTP; outside development it needs a certificate, and a non-loopback address needs `--admin-private-network true` (the deployer's word that only a VPN or an allowlist reaches it). Headers 8 KB,
  bodies 8 KB, 10 s a request, 64 connections; every answer `no-store`. It needs the database (the audit log): without one the server refuses to start.
- **Who calls.** The **service credential** (an environment variable, 32+ characters, never logged) proves the panel: kept as a SHA-256 digest and compared in constant time. Each request names the
  **staff member** (`X-Staff-Id`, `-Name`, `-Role`). The server checks the credential, the shape, that the role is a role, the command's lowest role, the reason and the rate limits (per credential, and
  failed credentials per address); it **takes from the panel** that the person is who it says and holds that role, because the panel did the OIDC sign-in and only the credential's holder can say it. A
  forwarded, signed ID token checked here is the way to close that gap (not built).
- **Commands** (`IAdminCommand`) go through `AdminCommands`: the role table (`AdminCommandTable`, one place, `kick` → `support`), a required reason (1-500 characters), then the command. A command
  that touches the world runs a **step on the tick** (`AdminRoad.OnTickAsync` → `AdminIntent` → `AdminSystem`, last before the AOI) and waits for it (3 s, else `timeout` and nothing is changed). The
  answer is an outcome: `done`, `accepted` (the world applies it on a coming tick) or `refused` with an "Admin errors" term.
- **Audit log.** `admin_audit` (+ `admin_staff` for the names). A change in the database is written with its row in one transaction (rule 10); one that is not gets its row before the answer. Before and
  after are JSON of ids and numbers only. **Triggers** refuse `UPDATE`, `DELETE` and `TRUNCATE` for every role, the owner included (the server owns its tables, so grants alone could not); only DDL gets past,
  which only migrations run (docs/ADMIN_API.md says how a deployment closes that with a second role). Erasing an account leaves its rows: they name only ids (docs/STORE_COMPLIANCE.md §6).
- **Events.** After the change and its row, `AdminActionApplied` is raised on the **admin event bus** (`AdminEvents`, an `EventBus` dispatched at once on the request's thread; a subscriber that must
  touch the world queues an intent). Admin commands do not belong to one instance, so they have a bus of their own.
- **Reads.** Status (players online, sessions, every instance's tick p50/p99/max and counters, saving, errors logged, the content hash `ClientBundleExporter.Hash`), accounts and characters by name prefix
  or id (paged), one character's saved state, the audit log by target, staff, action and time. No answer holds a password hash or a token.
- **Kick** (`support`): every session of the account ends with its row in one transaction; the gateway says `bye kicked` and the character (in its grace period too) leaves, saved, its resume token dead,
  exactly as T-710's session end. The client says so and goes to the sign-in screen.

### 12.7 Moderation and support as built (T-806)
Every endpoint and field: **docs/ADMIN_API.md**. Code: `src/Game.Server/Admin` (`ModerationCommands`, `ModerationChecks`, `IModerationStore`), `PostgresModerationStore`, the `Moderation` migration
(`account_sanctions`, `support_requests`, `admin_items`). Roles (`AdminCommandTable`): `support` mutes and runs the support queue; `gm` bans, gives and removes items and currency, unsticks and announces;
`admin` runs maintenance.
- **Bans** are the database's: a ban, its sessions' end and its audit row are one transaction; sign-in and every `hello` ask the database (after the password or the session is proved, so only
  the owner hears of it), so a ban holds on every server process and through restarts. Sign-in answers `account_banned` with `retry_after` (seconds; none for a ban without an end), `hello` and
  a game it ends `bye banned` with the same. Staff's reason is never shown: it is written for staff and may name others. The game the ban's own server holds ends at once; a game on **another
  process** ends at that process's **ban watch**, which asks every 10 s which accounts playing there are banned: such a player keeps playing at most about 10 s (row 320's limit, for bans).
- **Mutes** are stored the same way (always timed). There is no chat yet: `ModerationChecks.MaySpeakAsync(account)` is the check T-901 calls before it sends a line; it caches an answer 5 s per
  account (a mute made here is seen at once, one made through another process within 5 s) and fails closed when the database is away.
- **Support fixes to a character** go one of two roads, never both (`CharacterFix`): in this server's world, the system that owns the thing changes it on the tick (`StaffItemChange` in the
  inventory system, `StaffUnstick` in combat, which knows the save point), a step that either runs or is given up for good, so `timeout` means nothing changed. An item or currency change of a
  saved character carries its audit row as a **staff note** on the character's ticket into the next save's transaction (§3): the change and its row land together, a retried or later save never
  writes it twice (only notes above the stored save number are written), and a character the server loses takes its note with it. The command waits for that save (10 s) and answers `done` with
  the row, else `accepted`. Not in the world, the change is made in the database under the claim rules: the character and its state are locked in a save's order, a live holder is refused
  (`character_busy`), and the claim number moves on, so a late save of an earlier stay is refused (`Lost`) instead of undoing the fix.
- **Items staff make** are new entries with new permanent ids, never merged into a stack (`InventoryRules.TryAdd(separate: true)`), and each id is put in `admin_items` with its audit row:
  that table is where `source = admin` lives (§12.3). It names no character, because the item may change hands. A worn entry staff remove comes off first. Weight is not checked.
- **Unstick**: in the world as a respawn moves (a dead one respawns); offline to the stored save point, or the content's start when there is none or it is gone.
- **Server**: `announce` sends an `announcement` (at most 80 characters: two lines on the smallest phone at the largest UI scale) to every session playing here, which the HUD shows for 12 s on the flash line's place, in the tracker's font; `start_maintenance` refuses new logins (`bye maintenance` at `hello`; a resume still gets back in), announces
  the warning, and after it ends every game here (`bye maintenance`, characters leave saved) with the server still running; `end_maintenance` opens logins and calls off a pending end. Both are
  this process's only, and end with it.
- **Support queue** (§12.4 "Store compliance"): `open_request` (deletion or export, after support checked who asked), `complete_request`, `cancel_request`. A deletion takes T-703's own path: the
  erasure wait, `account_deletions`, every session and game ended; the owner can still keep the account by signing in. An export answers with everything stored about the account
  (docs/STORE_COMPLIANCE.md §2) as JSON for staff to send, read in one snapshot; never a hash or a token, not who of staff did what or why. The server sends no e-mail.
- **Erasing** an account takes its sanctions, requests and the `admin_items` marks of the items erased with it; its audit rows stay (§12.6).

### 12.8 The panel as built (T-805)
Setup, local use and a fork's security notes: **docs/ADMIN_PANEL.md**. Code: `src/Game.Admin` (references `Game.Shared` and `Game.Content` only).
- **Shape.** Blazor Server: pages are rendered on the server per request, so each one checks the sign-in again; only the status page holds a live connection, and asks again on a timer
  (10 s, never under 5). Commands are plain posts with antiforgery.
- **Sign-in.** OIDC authorization code with PKCE; configured groups give the role (the highest); MFA is the provider's. The cookie holds the staff id, name, role and end only; it is never extended
  and signing out ends it. A development sign-in works in `Development` on loopback only; switched on anywhere else the panel does not start.
- **Calls.** One typed client: the five headers, no retry, redirect, proxy or request logging, a timeout. A write with no usable answer is shown as "may have happened" with its request id.
- **Commands.** The server's list, greyed out below each lowest role (the server decides). Reason, then a confirmation showing the exact JSON, sent once (a one-time ticket held by the panel). A command
  without a page gets a general form of named, typed fields.
- **Exposure.** Loopback unless told it is on a private network, HTTPS outside development, only named hosts, a content security policy of this origin only.
- **Not built:** the login and item history of §12.4 (the admin API has no read for them), an IP allowlist in the panel (a VPN or firewall does it), a real provider's test.

### 12.9 Chat reports as built (T-901)
Endpoints: **docs/ADMIN_API.md**. Code: `Admin/ChatReportCommands.cs`, the `/chat-reports` read in `AdminApi`, `Persistence/PostgresChatStore`, the `Chat` migration (`chat_blocks`, `chat_reports`).
- **Read** (`GET /admin/v1/chat-reports`, any staff role): reports newest first by status and account (as sender or reporter), with what was said (the server's copy), the channel, both
  times, and the account and character ids of the sender and the reporter. Staff look the names up by id (`/characters/{id}`).
- **Close** (`close_report`, `support`): the report's status and its audit row in one transaction; before and after hold the status and ids, never the line. What staff do about the sender
  (a mute, a ban) is its own command and row. A closed report is erased `--chat-closed-report-days` (30) after it was closed (the chat sweep, hourly); an open one stays until it is closed.
- **Erasing** either account takes the reports that name it (as sender or reporter) and the blocks it made or that name its characters (docs/STORE_COMPLIANCE.md §2, §6). The data export
  holds its blocks, the reports it made and the reports of its own lines (without who reported them).

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
