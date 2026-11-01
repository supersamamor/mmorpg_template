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
Singleton root files: `FormulaSetDef` (`formulas.yaml`), `BalanceDef` (`balance.yaml`: stat/skill point rules, stat range, damage and chance clamps, path search limits).

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

- `Game.Tools export-client` writes `client-content.json` from the loaded registries, plus a
  content hash. The bundle is packed into the app.
- Drop tables, AI, spawn data, formulas and quest internals are never in the bundle.
- On login the server sends its content hash; a mismatch means "update required".

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
  that each face and hairstyle fits its gender's rig. Headgear can declare that it hides the hair.
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

## 10. Networking on mobile

- **TLS required:** `wss://` everywhere except local dev. Cleartext `ws://` is allowed only in
  debug builds (Android network-security config, iOS ATS exception).
- **Disconnects are normal.** When a socket drops or the app is backgrounded, the server keeps
  the character in the world for a grace period (server config), then saves and logs it out.
- **Resume:** the client reconnects with a resume token and receives a full snapshot, then deltas.
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
  | `hello` | C→S (first) | `version`, `name` (dev login stub; T-701 replaces it) |
  | `move` | C→S | `cell` (a request; it may come back `rejected`) |
  | `stop` | C→S | — |
  | `ping` | C→S | `seq` |
  | `welcome` | S→C | `version`, `id` (your entity), `map`, `tick_rate` |
  | `snapshot` | S→C | `tick`, `map`, `id`, `entities` (view records). Replaces the view; a new `map` means you changed maps |
  | `delta` | S→C | `tick`, `appeared` (records), `changed` (id + changed fields; `"next":null` = stopped), `disappeared` (ids); empty lists left out |
  | `rejected` | S→C | `intent` (`move`), `reason` (a `MoveRejected` term), `cell` |
  | `pong` | S→C | `seq` |
  | `bye` | S→C (last) | `reason` (VOCABULARY "Bye reasons"), followed by the close frame |

  A view record is `{"id":7,"kind":"monster","display_key":"monster.x","cell":[3,4],"facing":"e","next":[4,4],"speed":3.51}`.
- **Session lifecycle:**
  1. The client connects and must send `hello` within `hello-timeout-ms`.
  2. The server checks the protocol version and the dev login. The dev login is development only; any other environment refuses it.
  3. The server queues the character with `GameWorld.Spawn(..., initialize)` at `balance.yaml` `new_character`. On the start
     instance's tick, the initializer sets its stats (from the stats pipeline), its display key (the job id) and its view sink, and
     queues `welcome`. The AOI then sends the first `snapshot` in phase 5 of that tick, and deltas follow.
  4. `move`/`stop` are parsed and checked (shape, cell inside the current map) on the socket thread and queued as `MoveTo`/`StopMove`
     wherever the character is now. Warps are followed through the world's entity directory. Each session has a token-bucket rate
     limit: messages over it are dropped and counted, and sustained flooding ends in `bye rate_limit`.
  5. Malformed JSON, an unknown type, an oversize message, a wrong version, a message before `hello`, or silence longer than
     `idle-timeout-ms` all end in `bye <reason>` and the close frame.
  6. When the connection ends for any reason, the character is despawned on its instance's next tick. T-210 adds the grace period
     and resume here.
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
