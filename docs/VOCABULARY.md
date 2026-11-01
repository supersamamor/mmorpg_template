# Vocabulary

The single source of truth for names used across code, content, and formulas.
Add new terms here in the same change that introduces them.

## Id namespaces
| Prefix       | Type                  | Example                 |
|--------------|-----------------------|-------------------------|
| `job.`       | Job / class           | `job.swordsman`         |
| `skill.`     | Skill                 | `skill.bash`            |
| `item.`      | Item (any kind)       | `item.red_potion`       |
| `monster.`   | Monster               | `monster.slime`         |
| `status.`    | Buff / debuff         | `status.stun`           |
| `quest.`     | Quest                 | `quest.first_hunt`      |
| `npc.`       | NPC                   | `npc.guide`             |
| `dialogue.`  | Dialogue graph        | `dialogue.guide_intro`  |
| `map.`       | Map                   | `map.starter_town`      |
| `drop.`      | Drop table            | `drop.slime`            |
| `exp.`       | EXP table             | `exp.base`              |

Ids: lowercase, `a-z0-9_`, one prefix. Display names are separate fields.

## Appearance option namespaces
Options defined inside `content/appearance.yaml` (ARCHITECTURE §7.5.1), not registry definitions. Unique in the file; cosmetic only.

| Prefix       | Option       | Example              |
|--------------|--------------|----------------------|
| `face.`      | Face variant | `face.male_calm`     |
| `hair.`      | Hairstyle    | `hair.male_crop`     |
| `skin.`      | Skin tone    | `skin.fair`          |
| `haircolor.` | Hair colour  | `haircolor.chestnut` |

## Genders
`male`, `female` (character creation; one body type and rig each; cosmetic only)

## Asset key prefixes
| Prefix      | Asset                                              |
|-------------|----------------------------------------------------|
| `model.`    | 3D model (`.glb`): character, monster, equipment, prop |
| `anim.`     | Animation library for one rig                      |
| `scene.`    | Godot scene, e.g. a map's visual scene             |
| `tex.`      | Standalone texture                                 |
| `mat.`      | Material                                           |
| `vfx.`      | Visual effect (particles, decals)                  |
| `sprite.`   | 2D image used in the 3D world (billboard, ground marker) |
| `icon.`     | UI icon                                            |
| `ui.`       | Other UI art                                       |
| `portrait.` | Dialogue portrait                                  |
| `sfx.`      | Sound effect                                       |
| `music.`    | Music track                                        |
| `font.`     | Font                                               |
| `ambience.` | Ambience loop (a map's background sound)           |

## World units
Distances, ranges and AoE sizes are in **cells** (1 cell = 1 world unit, Chebyshev distance).
`move_speed` is in **cells per second**.

## Audio buses
`music`, `sfx`, `ui`, `ambient` — the bus a sound plays on (ARCHITECTURE §7.8). Without a `bus`, a manifest sound follows its key prefix: `music.` → `music`, `ambience.` → `ambient`, `sfx.` → `sfx`.

## Audio default categories
Keys of the manifest's `audio.defaults` (category → `sfx.` key): `monster.aggro`, `monster.hit`, `monster.die`, `item.use`, `item.equip`, `ui.tap`, `ui.open`, `ui.close`, `ui.error`,
and for every weapon type: `weapon.<weapon_type>.swing`, `weapon.<weapon_type>.impact` (e.g. `weapon.dagger.swing`).
Keys of `audio.events` are "Events (event bus)" names.

## Directions (facing)
`n`, `ne`, `e`, `se`, `s`, `sw`, `w`, `nw`

## Cell flags
`walkable`, `blocks_los`

## Map object types
`spawn`, `warp`, `npc`, `zone` — zone kinds: `safe`, `pvp`

## Attach points
`head`, `face`, `mouth`, `back`, `hand_r`, `hand_l`

## Animation ids
`idle`, `walk`, `attack`, `cast`, `hit`, `die`, `sit`, `pick_up`

## Primary stats
| Id    | Name      | Primary influence                      |
|-------|-----------|----------------------------------------|
| `str` | Strength  | Melee ATK, carry weight                |
| `agi` | Agility   | ASPD, FLEE                             |
| `vit` | Vitality  | MaxHP, DEF, HP regen, status resist    |
| `int` | Intellect | MATK, MaxSP, SP regen, MDEF            |
| `dex` | Dexterity | HIT, ranged ATK, cast time             |
| `luk` | Luck      | CRIT, perfect dodge, status chance     |

## Derived stats
`max_hp`, `max_sp`, `atk`, `matk`, `def`, `mdef`, `hit`, `flee`, `crit`,
`aspd`, `move_speed`, `cast_time_mod`, `hp_regen`, `sp_regen`, `weight_limit`

## Modifier ops
`flat`, `percent`, `override`

## Elements
`neutral`, `fire`, `water`, `earth`, `wind`, `holy`, `shadow`, `poison`, `ghost`, `undead`
(multipliers in `content/elements.yaml`)

## Monster races / sizes
Races: `formless`, `beast`, `plant`, `insect`, `fish`, `demon`, `undead`, `humanoid`, `dragon`, `angel`
Sizes: `small`, `medium`, `large`

## Effect types (v1)
| Type             | Purpose                                    |
|------------------|--------------------------------------------|
| `damage`         | Physical/magic damage via formula + element |
| `heal`           | Restore HP                                  |
| `restore_sp`     | Restore SP                                  |
| `apply_status`   | Apply a `status.*` with chance/duration     |
| `remove_status`  | Cleanse statuses by id or tag               |
| `modify_stat`    | Temporary stat modifier (used by statuses)  |
| `knockback`      | Push target N cells                         |
| `teleport`       | Move caster (random/save point/map)         |
| `summon`         | Spawn a monster                             |
| `area`           | Wrap child effects over an AoE shape        |

## Equipment slots
`head_top`, `head_mid`, `head_low`, `body`, `weapon`, `shield`, `garment`, `footgear`, `accessory_1`, `accessory_2`

## Weapon types
`unarmed`, `dagger`, `sword_1h`, `sword_2h`, `spear`, `axe`, `mace`, `staff`, `bow`, `knuckle`

## Item kinds
`consumable`, `equip`, `card`, `material`, `quest`, `ammo`

## Quest objective types
`kill`, `collect`, `talk`, `reach`, `level`, `job`, `use_item`, `equip`

## Reward types
`exp`, `item`, `zeny`, `skill_point`, `stat_point`, `unlock_job`, `status`

## Events (event bus)
`EntitySpawned`, `EntityDied`, `MonsterKilled`, `DamageDealt`, `ItemAcquired`,
`ItemUsed`, `ItemEquipped`, `NpcTalked`, `MapEntered`, `ZoneEntered`,
`LevelUp`, `JobLevelUp`, `JobChanged`, `SkillLearned`, `QuestStarted`,
`QuestStepCompleted`, `QuestCompleted`, `StatusApplied`, `StatusExpired`,
`EntityEnteredMap`, `EntityLeftMap`, `WarpRejected`, `MovementStarted`, `CellReached`,
`MovementStopped`, `MoveRejected`, `AggroGained`, `AggroLost`, `AttackRequested`

## Area of interest
**Observer**: an entity with a view sink (`Entity.View`); it is sent what it can see. **AOI bucket**: a square of
`aoi.bucket_size` cells; an observer sees the buckets within `aoi.view_radius` of its own. **Snapshot**: an observer's full view.
**Delta**: one tick's `appeared` / `changed` / `disappeared`.

## View fields
In a view record and in a delta's `changed` field set: `cell`, `facing`, `next`, `speed`, `display_key`

## Movement reasons
In `MoveRejected`: `out_of_bounds`, `not_walkable`, `start_not_walkable`, `no_path`, `path_too_long`, `search_limit`, `no_speed`
In `MovementStopped`: `arrived`, `cancelled`, `warped`, `died`, `no_speed`

## Monster AI states
`idle`, `wander`, `chase`, `attack`, `return` (ARCHITECTURE §6.6)

## Aggro reasons
In `AggroGained`: `sighted`, `attacked`
In `AggroLost`: `target_gone`, `leash`, `unreachable`

## Currency
`zeny` (rename freely in data; engine id stays `currency`)

## Formula variables (whitelist)
Caster: `base_level`, `job_level`, all primary & derived stat ids, `weapon_atk`, `refine`
Target: `t_` prefix (e.g. `t_def`, `t_vit`, `t_level`)
Skill: `level` (skill level)
Balance-only: `stat_value` (current value of the stat being raised) is valid only in `balance.yaml` `progression.stat_point_cost`; `progression.stat_points_per_base_level` may use only `base_level`, `progression.skill_points_per_job_level` only `job_level`.
Functions: `min`, `max`, `floor`, `ceil`, `round`, `clamp`, `pow`, `sqrt`

## Message types (wire protocol)
Every message is a JSON object whose `type` field is one of these (shapes in ARCHITECTURE §10; constants in `Game.Shared/Protocol`).
Client → server: `hello`, `move`, `stop`, `ping`, `logout`
Server → client: `welcome`, `snapshot`, `delta`, `rejected`, `pong`, `bye`

## Bye reasons
In `bye` (the server's last message before it closes the connection): `timeout`, `bad_version`, `malformed`, `unknown_type`,
`oversize`, `before_hello`, `duplicate_hello`, `rate_limit`, `login_refused`, `server_full`, `too_slow`, `shutdown`, `server_error`, `update_required`,
`logout`, `replaced`, `resume_failed`.
`update_required`: the client's content hash differs from the server's, or it sent none outside development (ARCHITECTURE §5.4).
`logout`: the client sent `logout`; its character left the world at once. `replaced`: another connection resumed this character with its token.
The client reconnects after neither. `resume_failed`: the token in `hello` `resume` is unknown, already used, or its grace period is over
(the client goes to the login screen). `rate_limit` also ends a resume from an address that failed too many resumes (ARCHITECTURE §10).
A `rejected` message carries a "Movement reasons" (`MoveRejected`) term instead; the session goes on.

## Entity kinds
In a view record's `kind`: `player`, `monster`, `npc`
