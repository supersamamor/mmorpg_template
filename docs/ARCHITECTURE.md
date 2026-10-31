# Architecture

## 1. Goal

A reusable MMORPG **template**: the engine is generic, the game is data.
Replacing `/content` and `/themes/<name>` should produce a different game with zero code changes.

## 2. Solution layout

```
/src
  Game.Shared      Pure domain: definition records, stat/modifier pipeline,
                   formula evaluator, effect model, combat & leveling math,
                   message contracts. No I/O. Referenced by server, tools, client.
  Game.Content     YAML loading, registries, cross-reference validation,
                   JSON Schema generation. Hot-reload in dev.
  Game.Server      Host: tick loop, world/map instances, AOI, entity systems,
                   WebSocket gateway, persistence (PostgreSQL), event bus.
  Game.Tools       CLI: `validate`, `schema`, `stats` (balance reports).
/tests
  Game.Tests       Unit + golden-value tests (xUnit).
/client            Godot 4 C# project. Renders state, plays effects, sends intents.
/content           Game definitions (YAML) + maps (Tiled .tmj).
/themes/default    Art/audio/fonts + asset-manifest.json.
/docs              This folder.
```

Dependency direction: `Shared ← Content ← Server/Tools`, `Shared ← Client`.
`Game.Shared` must never reference anything else in the solution.

## 3. Runtime model

```
 Godot client                     .NET server (authoritative, 10 Hz)
 ───────────                      ──────────────────────────────────
 input → Intent ──WebSocket──►    Gateway → session → intent queue
                                  Tick: drain intents → validate → systems
                                        (movement, AI, combat, status, regen,
                                         spawn, quest) → events → AOI diff
 render ◄──── Snapshot/Delta ───  Outbound per-client deltas (nearby only)
```

- **Intents in, state out.** Clients never send results.
- **Area of Interest:** maps are split into cells; a client receives entities within N cells.
- **Map instances:** each map runs in its own instance; warps move a character between instances.
- **Persistence:** player state saved on interval, on map change, and on logout. Content is never in the DB.

## 4. Content system

### 4.1 Registry
On startup, `Game.Content` loads every YAML file under `/content` into typed registries
(`Registry<ItemDef>`, `Registry<SkillDef>`, …) keyed by string id, then runs validation:

- schema validity (shape, required fields, enums)
- id uniqueness and namespace prefix matches type
- every referenced id exists (job → skills, quest → items/monsters/npcs, map → monsters/warps)
- every asset key exists in the active theme's manifest
- every formula parses and only uses known variables

Any failure prevents startup and is reported with file + path.

### 4.2 Definition types (initial)
`JobDef`, `SkillDef`, `ItemDef` (consumable | equip | card | etc), `MonsterDef`,
`StatusDef` (buffs/debuffs), `QuestDef`, `NpcDef`, `DialogueDef`, `DropTableDef`,
`MapDef` (metadata; geometry from Tiled), `ExpTableDef`, `ElementTableDef`.

### 4.3 Maps
Authored in Tiled. Engine reads:
- tile layers → render only (client)
- `collision` layer → walkability grid (server)
- object layers: `spawns`, `warps`, `npcs`, `zones` (safe/pvp), using custom properties
  that reference content ids (e.g. `monster: monster.slime`, `count: 20`, `respawn: 10`).

## 5. Core mechanics model

### 5.1 Stats & modifiers
`FinalStat = (Base + ΣFlat) × (1 + ΣPercent)` evaluated in priority order.
Modifier sources: base allocation, job bonus, equipment, cards, refine, statuses.
Each modifier carries its source so it can be removed cleanly (unequip, buff expiry).
Derived stats (ATK, MATK, DEF, HIT, FLEE, CRIT, ASPD, MaxHP, MaxSP) come from formulas.

### 5.2 Formulas
Expression strings in `content/formulas.yaml`, evaluated with a sandboxed expression
library. Variables are whitelisted (stats, levels, skill level, target stats). Compiled once at load.

### 5.3 Effects
Skills, consumables, cards and statuses are lists of **effects**. Effect types are the
only place gameplay behavior lives in code (see VOCABULARY.md). New skill = new YAML.
New *kind* of behavior = new effect type + tests.

### 5.4 Quests
State machine per quest: `prerequisites → steps[objective] → rewards`.
Objectives listen to the event bus (`MonsterKilled`, `ItemAcquired`, `NpcTalked`, …).
Quest state per character is persisted as `{questId, stepIndex, counters}`.

### 5.5 Progression
Base level + job level, separate EXP tables. Stat points per base level and skill points
per job level come from data. Job change requirements from `JobDef`.

## 6. Theming (asset swap)

`themes/<theme>/asset-manifest.json` maps asset keys to files plus metadata:

```json
{
  "sprite.monster.slime": { "file": "sprites/monsters/slime.png", "frame": [32, 32], "anims": { "idle": [0,3], "walk": [4,7] } },
  "icon.item.knife":      { "file": "icons/knife.png" },
  "sfx.hit.light":        { "file": "audio/hit_light.ogg" }
}
```

Active theme is a config setting. Characters use **layered paper-doll sprites**
(body, head, headgear, weapon, shield) each by asset key, so equipment changes visuals via data.

## 7. Security baseline
- Server validates every intent (range, cooldown, SP, line of sight, ownership).
- Rate-limit intents per session.
- Passwords hashed (Argon2/BCrypt). Session tokens, not credentials, on the socket.
- Item operations in DB transactions; item instances have unique ids (dupe auditing).

## 8. Non-goals for v1
Sharding/multi-server, client prediction beyond movement interpolation,
PvP balance, guild wars, cash shop, mobile client.
