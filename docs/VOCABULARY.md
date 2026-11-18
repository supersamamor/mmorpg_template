# Vocabulary

The single source of truth for names used across code, content, and formulas.
Add new terms here in the same change that introduces them.

## Id namespaces
| Prefix       | Type                  | Example                 |
|--------------|-----------------------|-------------------------|
| `job.`       | Job / class           | `job.warden`         |
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

## Map authoring metadata
Names on nodes of a map's authoring scene (ARCHITECTURE §5.3, `MapMeta`).
Root: `map_id`, `map_width`, `map_height`, `map_slope_limit_degrees`, `map_overrides`.
Collision bodies: `map_walkable`, `map_blocks_los`.
Markers: `map_marker` (a map object type); spawn `map_monster_id`, `map_w`, `map_h`, `map_count`, `map_respawn_seconds`; warp `map_target_map_id`, `map_target_x`, `map_target_y`; NPC `map_npc_id`, `map_facing`; zone `map_zone_kind`, `map_w`, `map_h`.
Paint terms in `map_overrides`: `walk`, `block`, `los`, `open`.

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

All ten run in the one effect runner (T-501, ARCHITECTURE §6.3). Where each may stand is checked at load (`EffectSupport`, validation code `effect.unsupported`):
`modify_stat` only directly in a status's `on_apply` (its modifier has the source `status:<id>` and lasts as long as the status); `teleport` and `summon` only
directly in a skill or a consumable; no `area` inside an `area`; a ground skill's own effects aim at a cell (`area`, `summon`, `teleport`); `knockback` never on
the caster itself; a status never applies itself. **Save point**: until save points exist (T-601), a player's is the respawn point of the map it is on.

## Status end reasons
In `StatusRemoved` (a status that runs out raises `StatusExpired` instead): `removed` (a `remove_status` by id or tag), `died` (every status ends on death),
`warped` (its bearer moved to another map: statuses are timed by one map instance's tick clock; a warp within the map keeps them).

## Skill reasons
In `SkillRefused` (a `SkillRequested` dropped before its effects ran): `caster_dead`, `target_dead`, `target_gone`, `out_of_range`, `no_line_of_sight`, `safe_zone`,
`invalid_target`, `cannot_use_skills` (a status with `prevents_skills`).
Player skills (T-502), in `SkillRefused` for a `use_skill`, in `SkillLearnRefused` for a `learn_skill` and in `JobChangeRefused` for a `change_job`, and on the wire as
`rejected` (`intent` `use_skill`, `learn_skill` or `change_job`, with `skill` naming the skill or job):
- use: `unknown_skill`, `not_learned` (level 0), `passive_skill` (cannot be cast), `level_too_high` (above the learned level), `not_enough_sp`, `on_cooldown`,
  `wrong_weapon` (not in `weapon_types`; no weapon counts as `unarmed`). Range, sight, side and safe zone use the terms above.
- learn: `unknown_skill`, `not_in_tree` (not in the job's tree or a parent job's), `at_max_level`, `prerequisite_not_met`, `not_enough_points`.
- job change: `unknown_job`, `same_job`, `wrong_parent_job`, `base_level_too_low`, `job_level_too_low`, `quest_not_completed`, `not_unlocked`
  (the first failed requirement, in this order).

## Cast results
In `SkillCastEnded` and a delta's `cast_ends`: `done` (the cast time ran out), `cancelled` (the caster moved, gave a new order or stopped), `interrupted` (a stun,
death, lost target, or SP or cooldown that no longer allow it).

## Inventory
**Entry**: one stack of an item, or one equipment item with its own instance id; worn entries stay in the inventory and count toward `inventory.max_entries`
and the weight. **Weight**: the sum of `weight × quantity` over every entry; the limit is the derived stat `weight_limit`. **Source** of an equipped entry's
stat modifiers: `equip:<entry id>`. **Rev**: the change counter of the own inventory messages.

## Equipment slots
`head_top`, `head_mid`, `head_low`, `body`, `weapon`, `shield`, `garment`, `footgear`, `accessory_1`, `accessory_2`
An item's `equip.slots` lists the slots it fits: it fits **any one** of them (an accessory listing `accessory_1` and `accessory_2` goes in either).

## Weapon types
`unarmed`, `dagger`, `sword_1h`, `sword_2h`, `spear`, `axe`, `mace`, `staff`, `bow`, `knuckle`
Two-handed (`balance.yaml` `inventory.two_handed_weapon_types`): `sword_2h`, `staff`, `bow`. Such a weapon in `weapon` also takes the `shield` slot (ARCHITECTURE §6.8).

## Item kinds
`consumable`, `equip`, `card`, `material`, `quest`, `ammo`

## Quest objective types
`kill`, `collect`, `talk`, `reach`, `level`, `job`, `use_item`, `equip`

## Reward types
`exp`, `item`, `currency`, `skill_point`, `stat_point`, `unlock_job`, `status`

## Events (event bus)
`EntitySpawned`, `EntityDied`, `MonsterKilled`, `DamageDealt`, `ItemAcquired`,
`ItemUsed`, `ItemEquipped`, `NpcTalked`, `ZoneEntered`,
`LevelUp`, `JobLevelUp`, `JobChanged`, `SkillLearned`, `QuestStarted`,
`QuestStepCompleted`, `QuestCompleted`, `StatusApplied`, `StatusExpired`,
`EntityEnteredMap`, `EntityLeftMap`, `WarpRejected`, `MovementStarted`, `CellReached`,
`MovementStopped`, `MoveRejected`, `AggroGained`, `AggroLost`, `MonsterReturned`, `AttackRequested`, `AttackRefused`, `AttackStarted`, `AttackStopped`,
`ExpGained`, `ExpLost`, `PlayerRespawned`, `ItemDropped`, `ItemExpired`, `ItemUnequipped`, `ItemDiscarded`, `ItemRefused`, `InventoryChanged`,
`CardInserted`, `ItemRefined`, `StatusRemoved`, `HpRestored`, `SpRestored`, `KnockedBack`, `Teleported`, `SummonRequested`, `MonsterSummoned`, `AreaApplied`,
`SkillCastStarted`, `SkillRequested`, `SkillUsed`, `SkillRefused`, `SkillCastEnded`, `SkillCooldownStarted`, `SkillLearnRefused`, `JobChangeRefused`

## Area of interest
**Observer**: an entity with a view sink (`Entity.View`); it is sent what it can see. **AOI bucket**: a square of
`aoi.bucket_size` cells; an observer sees the buckets within `aoi.view_radius` of its own, up to the **crowd cap** (`aoi.max_visible`).
**Snapshot**: an observer's full view. **Delta**: one tick's `appeared` / `changed` / `disappeared`.

## View fields
In a view record and in a delta's `changed` field set: `cell`, `facing`, `next`, `speed`, `display_key`, `hp`, `dead`, `mine`, `weapon`, `worn`, `statuses`.
`hp`: HP as a fraction 0–1 in steps of 0.01 (never 0 while alive, never 1 while hurt), only for entities that fight. `dead`: true while dead.
`mine`: a ground item only, true when this client may pick it up now. `weapon` (a "Weapon types" term) and `worn` (item ids with a worn visual, in
"Equipment slots" order): players only, what they hold and wear; a change sends both.
`statuses` (T-501): every entity's active statuses, each `{id, stacks, left_ms, debuff}` (`left_ms`: simulated ms left at the message's tick; `debuff` only when
true), sent when one starts, stacks, is refreshed or ends (the whole list; empty when none is left), never per tick; never a chance, a number or the applier. A ground item's record also has `quantity` (it never changes).
A delta may also carry `hits` (this tick's attacks the client can see: `[attacker, target, amount, result]` with a "Hit results" term) and
`vitals` (the client's own `hp`, `max_hp`, `sp`, `max_sp`, when they changed; also in every snapshot).

## Movement reasons
In `MoveRejected`: `out_of_bounds`, `not_walkable`, `start_not_walkable`, `no_path`, `path_too_long`, `search_limit`, `no_speed`, `cannot_move`
In `MovementStopped`: `arrived`, `cancelled`, `warped`, `died`, `no_speed`, `cannot_move`
`cannot_move`: a status with `prevents_move` holds the entity (refused, not queued; a walker stops on the last cell it reached). `warped` also ends a walk that a
same-map warp (a knockback, a teleport) interrupted.

## Monster AI states
`idle`, `wander`, `chase`, `attack`, `return` (ARCHITECTURE §6.6)

## Aggro reasons
In `AggroGained`: `sighted`, `attacked`
In `AggroLost`: `target_gone`, `leash`, `unreachable`

## Attack reasons
In `AttackRefused` and `AttackStopped`: `attacker_dead`, `target_dead`, `target_gone`, `out_of_range`, `no_line_of_sight`, `safe_zone`, `invalid_target`,
`unreachable`, `cancelled`, `overweight`, `cannot_attack` (ARCHITECTURE §6.7; `overweight`: the player carries at least `inventory.overweight_percent` of its
weight limit; `cannot_attack`: a status with `prevents_attack` holds it: a new attack is refused, a running auto-attack pauses)
On the wire in `rejected` (`intent` `attack`) and `attack_stopped`. The gateway also refuses an `attack` itself: `target_gone` for an id the client
does not see, `invalid_target` for itself or anything but a monster.

## Item reasons
In `ItemRefused` and on the wire in `rejected` (`intent` `pick_up`, `use_item`, `equip`, `unequip`, `drop_item`, `insert_card` or `refine`); nothing changed (ARCHITECTURE §6.8):
`item_gone`, `not_yours`, `unreachable`, `cancelled`, `too_heavy`, `inventory_full`, `not_usable`, `cooldown`, `not_equipment`,
`wrong_slot`, `level_too_low`, `wrong_job`, `not_equipped`, `item_equipped`, `invalid_quantity`, `not_a_card`, `card_does_not_fit`, `no_free_socket`,
`not_refinable`, `at_max_refine`, `missing_material`.
`card_does_not_fit`: none of the card's `slots` is one of the equipment's. `no_free_socket`: its `card_slots` are full (or it has none).
`at_max_refine`: at `refine.yaml` `max_level`. `missing_material`: fewer carried units of the next step's material than its quantity.
`not_yours`: another player still has loot priority. (T-501 removed `unsupported_effect`: every effect type runs, and one that cannot run where it stands is a load error.)
`wrong_job`: the item's `job_ids` leave the job out, or the job's `weapon_types` leave the weapon's type out. `item_equipped`: worn items are not used or dropped.

## Weight states
`normal`, `burdened` (at `inventory.burdened_percent` of `weight_limit`: no natural regeneration), `overweight` (at `inventory.overweight_percent`: no attacks either).
In `inventory` and `inventory_changed` (`load`).

## Inventory changes
In `inventory_changed` `cause`: `picked_up`, `used`, `equipped`, `unequipped`, `dropped`, `weight` (only the weight limit or state changed),
`card_inserted`, `refined` (any refine attempt: the material is used up whatever the result).

## Refine failures
What a failed refine does, per step of `refine.yaml` (`failure`): `keep` (the level stays), `down` (one level down, not below 0), `break` (the item is gone, with its cards).

## Refine results
In `ItemRefined` and the owner's `refine_result` (`result`): `success` (one level up), then the failure terms `keep`, `down`, `break`.

## Hit results
In `DamageDealt` (and the wire's `hits`): `hit`, `crit`, `miss`, `status` (damage from a status's `on_tick`, e.g. poison: not an attack, so it always lands, never
crits and shows no swing; its source is the status's applier)

## Effect names (client)
Presentation hooks the client plays on combat events; theme data, not content. A manifest entry names the effect it plays with an `"effect"` field (`vfx.hit.normal` -> `effect.hit`, `vfx.hit.crit` -> `effect.crit`,
`vfx.death.generic` -> `effect.death`, `vfx.level_up` -> `effect.level_up`), so a theme re-skins an effect by tagging another key. A theme that leaves a name out, or
whose file is missing, falls down the chain `effect.level_up` -> `effect.crit` -> `effect.hit`, `effect.death` -> `effect.hit`, then shows nothing.
A model or animation-library entry may carry `impact_delay` (seconds from the start of the attack clip to the blow landing; the client default applies when absent).

## Currency
Engine id `currency`; the display name is data (`currency_name` in `balance.yaml`, default "crowns").
The reward type is also `currency`.

## Formula variables (whitelist)
Caster: `base_level`, `job_level`, all primary & derived stat ids, `weapon_atk`, `refine`
Target: `t_` prefix (e.g. `t_def`, `t_vit`, `t_level`)
Skill: `level` (skill level)
Balance-only: `stat_value` (current value of the stat being raised) is valid only in `balance.yaml` `progression.stat_point_cost`; `progression.stat_points_per_base_level` may use only `base_level`, `progression.skill_points_per_job_level` only `job_level`.
Functions: `min`, `max`, `floor`, `ceil`, `round`, `clamp`, `pow`, `sqrt`

## Message types (wire protocol)
Every message is a JSON object whose `type` field is one of these (shapes in ARCHITECTURE §10; constants in `Game.Shared/Protocol`).
Client → server: `hello`, `move`, `stop`, `attack`, `stop_attack`, `respawn`, `ping`, `logout`, `pick_up`, `use_item`, `equip`, `unequip`, `drop_item`, `insert_card`, `refine`, `use_skill`, `learn_skill`, `change_job`
Server → client: `welcome`, `snapshot`, `delta`, `rejected`, `attack_stopped`, `exp`, `level_up`, `you_died`, `respawned`, `inventory`, `inventory_changed`, `refine_result`, `skills`, `cooldown`, `pong`, `bye`
Owner only (about the client's own character): `attack_stopped`, `exp`, `level_up`, `you_died`, `respawned`, `inventory`, `inventory_changed`, `refine_result`, `skills`, `cooldown`.

## Bye reasons
In `bye` (the server's last message before it closes the connection): `timeout`, `bad_version`, `malformed`, `unknown_type`,
`oversize`, `before_hello`, `duplicate_hello`, `rate_limit`, `login_refused`, `server_full`, `too_slow`, `shutdown`, `server_error`, `update_required`,
`logout`, `replaced`, `resume_failed`.
`update_required`: the client's content hash differs from the server's, or it sent none outside development (ARCHITECTURE §5.4).
`logout`: the client sent `logout`; its character left the world at once. `replaced`: another connection resumed this character with its token.
The client reconnects after neither. `resume_failed`: the token in `hello` `resume` is unknown, already used, or its grace period is over
(the client goes to the login screen). `rate_limit` also ends a resume from an address that failed too many resumes (ARCHITECTURE §10).
A `rejected` message carries a "Movement reasons" (`MoveRejected`) term for `move`, an "Attack reasons" term for `attack`, or an "Item reasons" term for an
item intent, instead; the session goes on.

## Entity kinds
In a view record's `kind`: `player`, `monster`, `npc`, `item` (a ground item a monster dropped, `EntityKind.Item`; its `display_key` is the item id).
