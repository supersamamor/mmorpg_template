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

## World units
Distances, ranges and AoE sizes are in **cells** (1 cell = 1 world unit, Chebyshev distance).
`move_speed` is in **cells per second**.

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
`EntityEnteredMap`, `EntityLeftMap`, `WarpRejected`

## Currency
`zeny` (rename freely in data; engine id stays `currency`)

## Formula variables (whitelist)
Caster: `base_level`, `job_level`, all primary & derived stat ids, `weapon_atk`, `refine`
Target: `t_` prefix (e.g. `t_def`, `t_vit`, `t_level`)
Skill: `level` (skill level)
Balance-only: `stat_value` (current value of the stat being raised) is valid only in `balance.yaml` `progression.stat_point_cost`; `progression.stat_points_per_base_level` may use only `base_level`, `progression.skill_points_per_job_level` only `job_level`.
Functions: `min`, `max`, `floor`, `ceil`, `round`, `clamp`, `pow`, `sqrt`
