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
| `shop.`      | Shop (T-603)          | `shop.meadow_goods`     |
| `outfit.`    | Outfit, looks only (T-708) | `outfit.rain_cloak` |
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

## Appearance colour roles
`skin`, `hair`, `cloth` (T-706: the painted colours of a character's models that its own colours replace, in a manifest entry's `recolor`; `skin` takes the skin tone, `hair` the hair colour, `cloth` the job's `look.cloth_color`)

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
| `reset_stats`    | Allocated stats back to the start value, points refunded (T-503) |
| `reset_skills`   | Learned skills back to 0, points refunded, passives off (T-503) |

All twelve run in the one effect runner (T-501, ARCHITECTURE §6.3). Where each may stand is checked at load (`EffectSupport`, validation code `effect.unsupported`):
`modify_stat` only directly in a status's `on_apply` (its modifier has the source `status:<id>` and lasts as long as the status); `teleport` and `summon` only
directly in a skill or a consumable; no `area` inside an `area`; a ground skill's own effects aim at a cell (`area`, `summon`, `teleport`); `knockback` never on
the caster itself; a status never applies itself; `reset_stats` and `reset_skills` only directly in a consumable or a dialogue `run_effects` action (an NPC service, T-601), never in a skill, a status or an `area`, and they act on the
user only. **Save point**: until save points exist (T-601), a player's is the respawn point of the map it is on.

## Reset effects (T-503)
`reset_stats`: every primary stat goes back to `progression.starting_stat_value` and exactly the points those steps cost (the sum of `stat_point_cost` over each +1 step) come back
as unspent stat points. `reset_skills`: every learned skill goes to 0, exactly one skill point per level learned comes back, passive modifiers come off the sheet, a running cast is
cancelled and cooldowns are kept. Neither changes levels, EXP, job or unspent points. A reset that would change nothing does nothing. The rules for using a reset **item** are data,
`balance.yaml` `progression.reset`: `free_below_base_level` (below it the reset happens and the item is kept), `refuse_in_combat` (no reset within `regen.out_of_combat_delay_ms` of the
last damage dealt or taken) and `refuse_when_dead`. There is no currency yet, so a reset costs the item.

## Stat reasons
In `StatRaiseRefused` and on the wire as `rejected` (`intent` `raise_stat`, with `stat` naming the stat); nothing changed:
`unknown_stat` (not a primary stat), `invalid_amount` (below 1 or above 99 steps), `at_max_value` (would pass `progression.max_stat_value`), `not_enough_points`, `dead`.

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

## Outfit slots
`head`, `face`, `body`, `back`, `feet` (T-708, ARCHITECTURE §7.5.2): where an outfit's piece is drawn. A set of their own, because one look may stand for several equipment slots and nothing may
hide what a character fights with. Each covers equipment slots: `head` → `head_top`; `face` → `head_mid`, `head_low`; `body` → `body`; `back` → `garment`; `feet` → `footgear`. No outfit slot
covers `weapon`, `shield` or the accessories. While an outfit shows in a slot, the equipment and the job's pieces of the slots it covers are not drawn (only drawn: every rule still counts them).

## Outfit reasons
Why a `set_outfit` was refused (T-708), in `OutfitRefused` and on the wire as `rejected` (`intent` `set_outfit`, with `slot` and `item`, the outfit); nothing changed. In this order: `unknown_slot`
(not an "Outfit slots" term), `unknown_outfit` (the content has none), `not_owned`, `slot_not_covered` (the outfit has no piece for the slot), `wrong_gender` (its piece for the slot is for the other gender).

## Weapon types
`unarmed`, `dagger`, `sword_1h`, `sword_2h`, `spear`, `axe`, `mace`, `staff`, `bow`, `knuckle`
Two-handed (`balance.yaml` `inventory.two_handed_weapon_types`): `sword_2h`, `staff`, `bow`. Such a weapon in `weapon` also takes the `shield` slot (ARCHITECTURE §6.8).

## Item kinds
`consumable`, `equip`, `card`, `material`, `quest`, `ammo`

## Quest objective types
`kill`, `collect`, `talk`, `reach`, `level`, `job`, `use_item`, `equip`

How each counts (T-602, ARCHITECTURE §6.4), only while its step is current: `kill` (`MonsterKilled` for each player with **kill credit**: every player with a damage
share in the monster who is still on the map, the same players who share its EXP), `collect` (the bag's carried quantity, live; `consume: true` takes the items when the
step is turned in), `talk` (`NpcTalked` with the NPC, counted only when every other objective of its step is met: talking is the turn-in), `reach` (standing within
`radius` cells, Chebyshev, of the cell on the map; latched), `level` (the base level; met at once if already reached), `job` (the current job), `use_item` (`ItemUsed`),
`equip` (`ItemEquipped`, or worn when the step starts; latched).

## Reward types
`exp`, `item`, `currency`, `skill_point`, `stat_point`, `unlock_job`, `status`, `outfit`

Paid when the last step is turned in (T-602). `item` and `currency` go into the bag in the same inventory transaction as the step's consumed items (all or nothing);
when they do not fit (`inventory_full`, `too_heavy`) **the quest waits**: nothing changed, the step stays met and *held*, and it is tried again whenever the bag changes.
Then `exp` (through `ProgressionRules`), `skill_point`, `stat_point`, `unlock_job` (`UnlockedJobs`, a job change's `requires_unlock`) and `status` are paid by the
progression host; none of them can fail. `outfit` (T-708) is granted there too: the character owns it from then on, and owning it already changes nothing. It is never put on by itself.

## Quest states
The `state` of a `quest_state` dialogue condition (T-602): `not_started` (neither active nor completed; an abandoned quest is not started), `available` (a start would be
accepted now: prerequisites met, not active, not completed unless `repeatable`, below `quests.max_active`), `active`, `completed` (finished and not active again).

## Quest reasons
In `QuestRefused` and on the wire as `rejected` (`intent` `dialogue_choice` for a start, `abandon_quest` for an abandon, with `quest`); nothing changed. Checked in
this order for a start: `unknown_quest`, `already_active`, `already_completed`, `base_level_too_low`, `wrong_job`, `quest_not_completed` (a prerequisite quest),
`too_many_active`. An abandon: `not_active`. A met step whose turn-in waits says why in its `held` field: `inventory_full`, `too_heavy`, `missing_item`.

## Quest changes
The `change` of a `quest` message (T-602): `accepted`, `progress` (a counter moved, or the turn-in is held or no longer held), `step_done`, `completed`, `abandoned`.

## Quest markers
What the client shows over an NPC for the player (T-602; in `quest_log` `offer` and `turn_in`): `offer` (the NPC gives a quest that is `available`), `turn_in`
(talking to it would complete the current step of an active quest: every other objective of the step is met).

## Autopilot
The quest autopilot (T-604, ARCHITECTURE §6.4.2) plays a player's ticked quests on the server. **Run**: one list of quests, from its start until it ends.

## Autopilot modes
The `mode` of an `autopilot` message: `running`, `paused` (it waits; it goes on by itself when the quest moves), `off` (the run is over).

## Autopilot activities
The `doing` of an `autopilot` message while `running`: `travelling` (to a hunting ground, an NPC, a cell or a warp), `hunting` (after a monster, or fighting it), `gathering` (picking a
wanted item up), `talking` (with the NPC, or turning the step in), `waiting` (at the hunting ground, nothing left to hunt until it respawns).

## Autopilot reasons
Why a `start_autopilot` was refused, in `AutopilotRefused` and on the wire as `rejected` (`intent` `start_autopilot`, with the `quest` or `skill` it is about). Nothing changed: a run in
progress goes on. Checked in this order: `disabled` (`autopilot.enabled` is false), `dead`, `time_limit` (today's `autopilot.daily_limit_minutes` are used up), `empty_queue`, `too_many_quests`
(`autopilot.max_quests`), `unknown_quest`, `duplicate_quest`, `not_active` (the character does not have it), `too_many_skills` (`autopilot.max_skills`), `unknown_skill`, `not_learned`,
`passive_skill`, `level_too_high` (above the learned level).

## Autopilot pauses
The `reason` of an `autopilot` message with `mode` `paused`: `needs_level`, `needs_job`, `needs_equip`, `needs_item_use` (only the player may do what is left of the step), `no_source` (nothing
placed on any map gives what the objective wants), `unreachable` (no way there, warps included), `no_progress` (nothing got done for `autopilot.no_progress_ms`), `no_safe_choice` (the NPC's
dialogue offers nothing the autopilot may pick).

## Autopilot stops
The `reason` of an `autopilot` message with `mode` `off`: `finished` (every quest of the list is done), `stopped` (`stop_autopilot`), `manual_input` (the player's own order), `died`, `logout` (the
character left the world), `disconnected` (the connection ended, or another took the character over), `bag_full` (a wanted item or a turn-in does not fit, or the load is too heavy to fight),
`time_limit`, `quest_abandoned` (the player dropped the quest it was playing).

## Dialogue condition types
The `type` of a choice's `conditions` entry (T-601); a choice is shown only when every condition holds (ARCHITECTURE §6.4).
`base_level` (`min`, `max`: either may be left out), `job_level` (`min`, `max`), `job_is` (`job_id`), `job_is_not` (`job_id`), `has_item` (`item_id`, `quantity`, default 1; the bag, worn pieces excluded),
`has_status` (`status_id`), `quest_state` (`quest_id`, `state`: a "Quest states" term, T-602), `quest_step` (`quest_id`, `step`: the quest is active at this step, 1 being the first, T-602),
`out_of_combat` (no hit dealt or taken within `regen.out_of_combat_delay_ms`, T-602)

## Dialogue action types
The `type` of a choice's `actions` entry (T-601); they run in order when the choice is picked:
`start_quest` (`quest_id`: raises `QuestStartRequested`, which the quest system handles, T-602), `set_save_point` (`x`, `y`: a cell of the NPC's map), `run_effects` (`effects`: run on the player as a consumable would, resets allowed),
`take_item` / `give_item` (`item_id`, `quantity`, default 1: a choice's item actions are one all-or-nothing transaction), `open_service` (`service`: a service id; `shop_id`: the shop, required for `shop` and refused for any other service, T-603)

## Service ids
What an `open_service` action opens: `refine`, `shop` (T-603: buying and selling; the action's `shop_id` names the shop, and `shop_buy` / `shop_sell` work while it is open). A service stays open until the player walks beyond `dialogue.talk_range` of the NPC, warps, dies, closes or talks to someone else.

## Dialogue reasons
In `DialogueRefused` and on the wire as `rejected` (`intent` `talk` or `dialogue_choice`); nothing changed (ARCHITECTURE §6.4):
`target_gone`, `invalid_target` (not an NPC), `dead`, `no_dialogue` (the NPC has nothing to say, or the player is in no dialogue), `unreachable`, `cancelled` (another order while walking to the NPC),
`invalid_choice` (no such choice, or its conditions do not hold), `busy` (the previous choice's items are still being applied), `missing_item`, `inventory_full`, `too_heavy` (an item action failed: nothing changed).

## Dialogue end reasons
In `DialogueEnded` and on the wire as `dialogue_ended`: `finished` (the graph reached its end; a service it opened stays open), `closed`, `moved_away`, `warped`, `died`, `replaced` (talking to another NPC), `npc_gone`, `disconnected`.
Any reason other than `finished` ends the services the dialogue opened too.

## Events (event bus)
`EntitySpawned`, `EntityDied`, `MonsterKilled`, `DamageDealt`, `ItemAcquired`,
`ItemUsed`, `ItemEquipped`, `NpcTalked`, `ZoneEntered`,
`LevelUp`, `JobLevelUp`, `JobChanged`, `SkillLearned`, `QuestStarted`,
`QuestStepCompleted`, `QuestCompleted`, `StatusApplied`, `StatusExpired`,
`EntityEnteredMap`, `EntityLeftMap`, `WarpRejected`, `MovementStarted`, `CellReached`,
`MovementStopped`, `MoveRejected`, `AggroGained`, `AggroLost`, `MonsterReturned`, `AttackRequested`, `AttackRefused`, `AttackStarted`, `AttackStopped`,
`ExpGained`, `ExpLost`, `PlayerRespawned`, `ItemDropped`, `ItemExpired`, `ItemUnequipped`, `ItemDiscarded`, `ItemRefused`, `InventoryChanged`,
`CardInserted`, `ItemRefined`, `StatusRemoved`, `HpRestored`, `SpRestored`, `KnockedBack`, `Teleported`, `SummonRequested`, `MonsterSummoned`, `AreaApplied`,
`SkillCastStarted`, `SkillRequested`, `SkillUsed`, `SkillRefused`, `SkillCastEnded`, `SkillCooldownStarted`, `SkillLearnRefused`, `JobChangeRefused`,
`StatRaised`, `StatRaiseRefused`, `StatsReset`, `SkillsReset`,
`DialogueShown`, `DialogueEnded`, `DialogueRefused`, `ServiceOpened`, `ShopOpened`, `QuestStartRequested`, `SavePointSet`, `DialogueEffectsRequested`, `DialogueItemsRequested`, `DialogueItemsResolved`,
`QuestProgressed`, `QuestAbandoned`, `QuestRefused`, `QuestItemsRequested`, `QuestItemsResolved`, `QuestRewardsRequested`, `QuestRewarded`, `QuestMarkersChanged`,
`AutopilotChanged`, `AutopilotRefused`,
`OutfitGranted`, `WardrobeChanged`, `OutfitRefused`,
`AdminActionApplied` (on the admin event bus, not an instance's: T-804),
`PartyKillCredit` (T-902: party members alive, on the instance and within `party.kill_credit_range` of a kill, who had no kill credit of their own; the quest system counts their `kill` objectives),
`TradeAsked`, `TradeNoticed`, `TradeChanged`, `TradeRefused`, `TradeClosed` (T-903: the trade system's news for the two players), `TradeSwapRequested` (both confirmed the same offers; the
inventory swaps them on its next tick), `TradeSwapResolved` (the inventory's answer: done, or a "Trade ends" term and nothing moved), `ItemsTraded` (two players swapped their offers: each
entry id given and received, the units, the currency and both permanent character ids; for quests, logs and dupe audits)

## Area of interest
**Observer**: an entity with a view sink (`Entity.View`); it is sent what it can see. **AOI bucket**: a square of
`aoi.bucket_size` cells; an observer sees the buckets within `aoi.view_radius` of its own, up to the **crowd cap** (`aoi.max_visible`).
**Snapshot**: an observer's full view. **Delta**: one tick's `appeared` / `changed` / `disappeared`.

## View fields
In a view record and in a delta's `changed` field set: `cell`, `facing`, `next`, `speed`, `display_key`, `hp`, `dead`, `mine`, `weapon`, `worn`, `statuses`, `outfit`.
`hp`: HP as a fraction 0–1 in steps of 0.01 (never 0 while alive, never 1 while hurt), only for entities that fight. `dead`: true while dead.
`mine`: a ground item only, true when this client may pick it up now. `weapon` (a "Weapon types" term) and `worn` (item ids with a worn visual, in
"Equipment slots" order): players only, what they hold and wear; a change sends both.
`statuses` (T-501): every entity's active statuses, each `{id, stacks, left_ms, debuff}` (`left_ms`: simulated ms left at the message's tick; `debuff` only when
true), sent when one starts, stacks, is refreshed or ends (the whole list; empty when none is left), never per tick; never a chance, a number or the applier. A ground item's record also has `quantity` (it never changes).
A player's record in a snapshot or an `appeared` entry also has `appearance` (T-705): `{gender, skin, face, hair, hair_color}`, ids from `appearance.yaml`; it is not a view field, a `changed` record never carries it.
`outfit` (T-708): a player's shown outfits by slot, `{"head":"outfit.x"}` ("Outfit slots" terms to outfit ids), left out of a record when none shows; a change sends all of them (`{}`: none). Looks only.
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
`not_refinable`, `at_max_refine`, `missing_material`, `reset_in_combat`, `reset_dead`, `nothing_to_reset`, `service_required`,
`out_of_range`, `not_in_stock`, `quantity_limit`, `cannot_afford`, `not_sellable`, `currency_full`, `dead`.
Shops (T-603), for `intent` `shop_buy` and `shop_sell`: `service_required` (no `shop` service open), `out_of_range` (the shop's NPC is beyond `dialogue.talk_range` or gone), `dead`,
`not_in_stock` (the open shop does not sell the item), `invalid_quantity` (below 1, or more than the entry holds when selling), `quantity_limit` (more than `shop.max_buy_quantity`),
`cannot_afford` (the total is beyond the balance, or beyond the number range), `inventory_full` and `too_heavy` (the bag cannot take the purchase), `item_gone` (no such entry), `item_equipped`,
`not_sellable` (a quest item, or `sell_price` 0), `currency_full` (the proceeds would pass the number range). A refusal changes nothing: items and currency move together or not at all.
`service_required` (T-601): `refine` works only while an NPC's `refine` service is open and in reach (`dialogue.refine_needs_service`).
Reset items (T-503): `reset_in_combat` and `reset_dead` follow `progression.reset`; `nothing_to_reset` means no stat was raised (or no skill learned). All three leave the item in the bag.
`card_does_not_fit`: none of the card's `slots` is one of the equipment's. `no_free_socket`: its `card_slots` are full (or it has none).
`at_max_refine`: at `refine.yaml` `max_level`. `missing_material`: fewer carried units of the next step's material than its quantity.
`not_yours`: another player still has loot priority. (T-501 removed `unsupported_effect`: every effect type runs, and one that cannot run where it stands is a load error.)
`wrong_job`: the item's `job_ids` leave the job out, or the job's `weapon_types` leave the weapon's type out. `item_equipped`: worn items are not used or dropped.

## Weight states
`normal`, `burdened` (at `inventory.burdened_percent` of `weight_limit`: no natural regeneration), `overweight` (at `inventory.overweight_percent`: no attacks either).
In `inventory` and `inventory_changed` (`load`).

## Inventory changes
In `inventory_changed` `cause`: `picked_up`, `used`, `equipped`, `unequipped`, `dropped`, `weight` (only the weight limit or state changed),
`card_inserted`, `refined` (any refine attempt: the material is used up whatever the result), `exchanged` (a dialogue's `take_item` / `give_item` actions, T-601),
`quest` (a quest step's turn-in: its consumed items taken and its item and currency rewards given, one transaction, T-602),
`shop_buy` (items bought; the currency went down in the same change, T-603), `shop_sell` (items sold; the currency went up, T-603), `staff` (staff gave or took items or currency through the admin API, T-806), `traded` (a trade swapped both offers: items and currency in one change on each side, T-903), `guild_founded` (founding a guild took its cost, `guild.found_cost`; the guild and the change land in one transaction, T-904), `guild_storage` (items or currency went into or out of the guild's storage; the storage's side lands in the same transaction, T-905).

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

## Render roles (client)
`toon`, `outline`, `environment` — what a theme manifest entry is for in the storybook toon look (ADR-003, T-222), named with a `"render"` field: `mat.toon` -> `toon`
(the cel-shaded material), `mat.outline` -> `outline` (the near-only outline overlay), `scene.environment` -> `environment` (sky, ambient, haze and key light).
Theme data, not content and not asset keys. A theme chain that leaves a role out draws that part as before T-222, with one warning. A vertex with colour alpha 0
is a glowing part, drawn unlit by the toon material.

## Currency
Engine id `currency`; the display name is data (`currency_name` in `balance.yaml`, default "crowns"; in the display bundle since format 6).
The reward type is also `currency`. Per character, kept and saved with the bag (`InventoryState.Currency`) and sent as `currency` in `inventory` and
`inventory_changed` (T-602). Earned from quests; nothing spends it yet (shops are later).

## Formula variables (whitelist)
Caster: `base_level`, `job_level`, all primary & derived stat ids, `weapon_atk`, `refine`
Target: `t_` prefix (e.g. `t_def`, `t_vit`, `t_level`)
Skill: `level` (skill level)
Party EXP share only (T-902, `formulas.yaml` `party_exp`, and nothing else in it): `exp` (the party's part of a kill's base or job EXP), `share_count` (how many members share it),
`level_spread` (the highest base level of those members minus the lowest).
Balance-only: `stat_value` (current value of the stat being raised) is valid only in `balance.yaml` `progression.stat_point_cost`; `progression.stat_points_per_base_level` may use only `base_level`, `progression.skill_points_per_job_level` only `job_level`.
Functions: `min`, `max`, `floor`, `ceil`, `round`, `clamp`, `pow`, `sqrt`

## Message types (wire protocol)
Every message is a JSON object whose `type` field is one of these (shapes in ARCHITECTURE §10; constants in `Game.Shared/Protocol`).
Client → server: `hello`, `move`, `stop`, `attack`, `stop_attack`, `respawn`, `ping`, `logout`, `pick_up`, `use_item`, `equip`, `unequip`, `drop_item`, `insert_card`, `refine`, `use_skill`, `learn_skill`, `change_job`, `raise_stat`, `talk`, `dialogue_choice`, `dialogue_close`, `abandon_quest`, `shop_buy`, `shop_sell`, `start_autopilot`, `stop_autopilot`, `set_outfit`, `chat`, `block`, `unblock`, `report`, `party_invite`, `party_answer`, `party_leave`, `party_kick`, `party_lead`, `trade_ask`, `trade_answer`, `trade_offer`, `trade_currency`, `trade_lock`, `trade_unlock`, `trade_confirm`, `trade_cancel`, `guild_found`, `guild_invite`, `guild_answer`, `guild_leave`, `guild_kick`, `guild_rank`, `guild_ranks`, `guild_lead`, `guild_notice`, `guild_storage_open`, `guild_deposit`, `guild_withdraw`
Server → client: `welcome`, `snapshot`, `delta`, `rejected`, `attack_stopped`, `exp`, `level_up`, `you_died`, `respawned`, `inventory`, `inventory_changed`, `refine_result`, `skills`, `stats`, `cooldown`, `dialogue`, `dialogue_ended`, `service_opened`, `shop`, `quest_log`, `quest`, `autopilot`, `wardrobe`, `announcement`, `chat_line`, `chat_refused`, `chat_state`, `chat_reported`, `party`, `party_members`, `party_invited`, `party_notice`, `party_refused`, `trade_asked`, `trade`, `trade_notice`, `trade_closed`, `trade_refused`, `guild`, `guild_invited`, `guild_news`, `guild_refused`, `guild_tags`, `guild_storage`, `content_changed` (T-801, development servers with `--hot-reload` only), `pong`, `bye`
Party (T-902): client → server `party_invite`, `party_answer`, `party_leave`, `party_kick`, `party_lead`; server → client `party`, `party_members`, `party_invited`, `party_notice`,
`party_refused`. Only to the members: `party`, `party_members`; only to the invited: `party_invited`; own only: `party_refused`.
Trade (T-903): client → server `trade_ask`, `trade_answer`, `trade_offer`, `trade_currency`, `trade_lock`, `trade_unlock`, `trade_confirm`, `trade_cancel`; server → client `trade_asked`,
`trade`, `trade_notice`, `trade_closed`, `trade_refused`. Only to the two sides: `trade`, `trade_closed`; only to the asked: `trade_asked`; own only: `trade_notice`, `trade_refused`.
Guild (T-904): client → server `guild_found`, `guild_invite`, `guild_answer`, `guild_leave`, `guild_kick`, `guild_rank`, `guild_ranks`, `guild_lead`, `guild_notice`; server → client `guild`,
`guild_invited`, `guild_news`, `guild_refused`, `guild_tags`. Only to the members: `guild`; only to the invited: `guild_invited`; own only: `guild_refused`; to every player on the map:
`guild_tags`. Storage (T-905): client → server `guild_storage_open`, `guild_deposit`, `guild_withdraw`; server → client, own only: `guild_storage`.
Owner only (about the client's own character): `attack_stopped`, `exp`, `level_up`, `you_died`, `respawned`, `inventory`, `inventory_changed`, `refine_result`, `skills`, `stats`, `cooldown`, `dialogue`, `dialogue_ended`, `service_opened`, `shop`, `quest_log`, `quest`, `autopilot`, `wardrobe`; and (T-901) `chat_refused`, `chat_state`, `chat_reported`. A `chat_line` goes to everyone the line reaches, its sender included.

## Bye reasons
In `bye` (the server's last message before it closes the connection): `timeout`, `bad_version`, `malformed`, `unknown_type`,
`oversize`, `before_hello`, `duplicate_hello`, `rate_limit`, `login_refused`, `server_full`, `too_slow`, `shutdown`, `server_error`, `update_required`,
`logout`, `replaced`, `resume_failed`, `load_failed`, `save_pending`, `save_lost`, `session_ended`, `kicked`, `banned`, `maintenance`.
`banned` (T-806): staff banned the account; a timed ban carries `retry_after` (seconds left), one without an end none; sent at `hello` and to a game the ban ends. `maintenance` (T-806): the server is in maintenance: a new login is refused at `hello`, and every game ends (saved) when the warning time is up.
`login_refused`: the dev login outside development or with a bad name, a session token that is unknown, expired or revoked, or a character that is not the account's
(T-701: one answer for all of them). `server_error` also ends a `hello` whose login the database could not answer.
`update_required`: the client's content hash differs from the server's, or it sent none outside development (ARCHITECTURE §5.4).
`logout`: the client sent `logout`; its character left the world at once. `replaced`: another connection resumed this character with its token, or its account logged in again (an account plays one character at a time, T-701).
The client reconnects after neither. `resume_failed`: the token in `hello` `resume` is unknown, already used, or its grace period is over
(the client goes to the login screen). `rate_limit` also ends a resume from an address that failed too many resumes (ARCHITECTURE §10).
`load_failed`: the character's saved state could not be read; nothing was created or changed. `save_pending`: its last save has not landed yet (bounded wait), or another live server holds it.
`save_lost`: the server can no longer save the character it plays (it is held elsewhere, or deleted), so the session ends and the character goes back to its last save. After these three the
client goes to the login screen and does not retry by itself (T-702, ARCHITECTURE §3 "Saving characters").
`session_ended`: the account session the character is played with was ended (signed out, pushed out by a newer sign-in, or a password change), so the character left the world and is saved. The client goes to the login screen (T-710).
`kicked`: staff ended the account's game through the admin API (T-804): every session of the account ended with it, and the character left the world, saved. The client says so and goes to the sign-in screen.
A `rejected` message carries a "Movement reasons" (`MoveRejected`) term for `move`, an "Attack reasons" term for `attack`, or an "Item reasons" term for an
item intent (`shop_buy` and `shop_sell` included), or an "Outfit reasons" term for `set_outfit`, instead; the session goes on.

## Account errors
The `error` of a refused account request (T-701, ARCHITECTURE §10 "Accounts"): `invalid_request`, `invalid_username`, `invalid_password`, `same_password`, `username_taken`,
`invalid_credentials`, `account_deleting`, `invalid_session`, `rate_limited`, `invalid_name`, `invalid_appearance`, `name_taken`, `no_free_slot`, `unknown_character`, `character_in_use`, `unavailable`, `account_banned`, `guild_storage_not_empty` (T-905: the character is the last member of a guild whose storage holds something; it cannot be deleted until the storage is empty).
`account_banned` (T-806): staff banned the account; told only to someone who has its password, with `retry_after` (seconds until the ban ends; none when it has no end); never staff's reason.
Appearance refusals (T-705): an id is unknown, or a face or hairstyle is not the chosen gender's. `invalid_credentials` answers an unknown username and a wrong password alike, and a password change whose current password is wrong. `same_password` (T-710): a password change to the one the account has. `account_deleting` (T-703): the account waits to be erased, told only to someone who has its password; it comes with `erased_in` (seconds). `rate_limited` and `unavailable` come with `retry_after` (seconds).

## Account terms
**Account**: who signs in (a username and a password). **Session token**: proves a sign-in to the account endpoints and in `hello`; stored on the server as its SHA-256.
**Resume token**: takes a character back after a drop; server memory only (ARCHITECTURE §10). **Character id**: a character's permanent id, a GUID; not the runtime
entity id. **Slot**: a character's place in its account, from 0 (`characters.max_slots` in `balance.yaml`).

## Wardrobe terms
(T-708, ARCHITECTURE §7.5.2.) **Outfit**: an `outfit.*` definition, looks only. **Owned**: in the character's wardrobe, each outfit once, saved with it. **Chosen**: what a slot shows of the owned outfits. **Hidden**: the switch that shows every
outfit as nothing, keeping the choices. **Shown**: the choices that stand (owned, known to the content, with a piece for the slot and the character's gender), unless hidden; what others see.

## Saving terms
(T-702, ARCHITECTURE §3 "Saving characters".) **Copy**: a character's whole state, taken on the tick thread. **Save**: a copy in the database, with a number that only goes up. **Claim**: which stay of a character may
save it; the newest. **Run**: one server start, numbered by the database. **Link**: one save must not land before another. **Landed**: in the database. **At risk**: nothing has landed for 30 s.
**Lost**: this server may not save the character any more. **Set aside**: kept, but not in use, because the content has no place for it. Set-aside kinds: `active_quest`, `completed_quest`, `unlocked_job`, `job`, `skill`.

## Staff roles
(T-804, ARCHITECTURE §12.2.) In the `X-Staff-Role` header of an admin request, lowest first; each may do what the ones before it may: `support`, `gm`, `admin`.

## Admin actions
What an admin command does; the `action` of its audit row (T-804, T-806): `kick`, `ban`, `unban`, `mute`, `unmute`, `give_item`, `remove_item`, `change_currency`, `unstick`, `announce`,
`start_maintenance`, `end_maintenance`, `open_request`, `complete_request`, `cancel_request`, `close_report` (T-901: staff handled a player's report of a chat line).

## Admin target kinds
What an admin command or audit row is about: `account`, `character`, `request` (a support request's number, T-806), `server` (this server process; its id is `server`, T-806), `chat_report` (a chat report's number, T-901).

## Admin errors
The `error` of a refused admin request or command (T-804, docs/ADMIN_API.md): `invalid_request`, `unauthorized`, `invalid_staff`, `unknown_role`, `forbidden`, `reason_required`, `not_found`,
`unknown_target`, `rate_limited`, `timeout`, `unavailable`, and (T-806) `already_banned`, `not_banned`, `already_muted`, `not_muted`, `character_busy` (a server holds the character but this one's world
does not have it), `never_saved`, `unknown_item`, `item_gone`, `not_enough`, `inventory_full`, `currency_limit`, `maintenance_on`, `maintenance_off`, `request_closed`, `account_deleting`, and (T-901) `report_closed`. `rate_limited` comes with a `Retry-After` header. A refused command changed nothing and wrote no audit row.

## Admin outcomes
The `outcome` of an admin command: `done` (made and audited), `accepted` (audited; the world applies it on a coming tick), `refused` (with an "Admin errors" term).

## Sanction kinds
An account restriction (T-806, `account_sanctions`): `ban` (no sign-in and no game; timed or without an end), `mute` (no speaking; always timed: the check chat calls, T-901).

## Support request kinds
A request that came in through support (T-806, `support_requests`): `deletion` (completing it starts T-703's erasure wait), `export` (completing it answers with the account's data as JSON).

## Support request statuses
`open`, `done`, `cancelled`.

## Announcement kinds
The `kind` of an `announcement` (T-806): `staff` (staff's words in `text`, at most 80 characters), `maintenance` (every game here ends, saved, in `in` seconds).

## Chat channels
Where a chat line goes (T-901, ARCHITECTURE §10 "Chat"): `map` (every playing character on the sender's map instance), `whisper` (one playing character, named by the sender), `party` (T-902: the
members of the sender's party who play now; registered by the party service through the seam), `guild` (T-904: the members of the sender's guild who play on this server process; registered by
the guild service through the seam), `system` (the server's own
lines: no player sends on it; the client lists staff's announcements under it). A channel whose members something else supplies (a party, T-902; a guild, T-904) is one more term here and
one `IChannelMembers` registered with the chat service; chat does not change.

## Chat reasons
The `reason` of a `chat_refused` (T-901): `muted` (with `retry_after`, seconds left), `rate_limited` (with `retry_after`), `not_online` (nobody of that name plays now, or the one named blocks
the sender: the same answer, so a block does not show), `invalid_target` (oneself), `empty`, `too_long`, `invalid_channel`, `unavailable` (the database is away, or a dev login, which has no
account), `unknown_character`, `already_blocked`, `not_blocked`, `block_limit`, `unknown_line` (a report of a line that did not reach the reporter, is its own, or is no longer kept),
`already_reported`.

## Chat report statuses
`open`, `closed` (`chat_reports.status`, T-901).

## Chat terms
(T-901.) **Line**: what one player said once, cleaned by the server, with a number the server gives it (`line`). **Block**: an account no longer hears a character (`chat_blocks`). **Report**:
a player's report of a line it received; the server stores its own copy of the line (`chat_reports`). **Recent lines**: the lines kept in server memory for a while so they can be reported.

## Party reasons
The `reason` of a `party_refused` (T-902, ARCHITECTURE §6.9): `not_online` (nobody of that name plays now, a tapped player is not on the inviter's map, or the invited blocks the inviter: the
same answer, so a block does not show; for `party_lead`, the member is away), `invalid_target` (oneself), `full` (`party.max_members`), `already_in_party`, `invite_pending` (the invited has an
invite it has not answered), `not_leader` (only the leader invites, kicks and hands over), `not_in_party`, `no_invite` (nothing to answer: none came, it expired, or its inviter left),
`rate_limited` (with `retry_after`).

## Party notices
The `notice` of a `party_notice` (T-902): `invite_sent`, `declined`, `expired`, `joined`, `left` (by itself, or at the end of its grace period), `kicked`, `leader` (a new leader),
`disbanded` (one member was left, so the party ended).

## Party terms
(T-902.) **Party**: up to `party.max_members` characters that share kills and a chat channel; it lives in one server process's memory and is never stored. **Leader**: the member who invites,
kicks and hands over; when it leaves, the earliest-joined member who plays now. **Invite**: one pending question to one character, ending after `party.invite_timeout_ms`. **Away**: a member in
its disconnect grace period; still a member until the grace period ends. **Sharing a kill**: the members alive, on the kill's map instance and within `party.exp_range` of the death cell, whose
base levels differ by at most `party.exp_level_gap`. **Members' state**: the others' HP and SP (whole percent), map and cell, sent to members only.

## Trade reasons
The `reason` of a `trade_refused` (T-903, ARCHITECTURE §6.10); nothing changed: `not_online` (the asked character does not play now or is not on the asker's map instance, or its account
blocks the asker: the same answer, so a block does not show), `invalid_target` (oneself, or not a player), `busy` (in a trade, or with an ask under way), `too_far` (more than `trade.range`
cells apart), `dead`, `ask_pending` (the asker already waits for an answer), `no_ask` (nothing to answer), `rate_limited` (with `retry_after`), `not_trading`, `not_tradeable` (the item's
`tradeable: false`), `item_gone`, `item_equipped`, `invalid_quantity` (units or currency below 0 or above what is carried), `offer_full` (`trade.max_entries`), `not_locked` (a confirm before
both locked), `stale` (a confirm of a version the trade is no longer at), `swapping` (both confirmed: the swap is under way), `cannot_trade` (a saved character and a development login).

## Trade notices
The `notice` of a `trade_notice` (T-903): `ask_sent` (to the asker), `declined` (to the asker), `expired` (no answer in time, the asker withdrew or went away; to both).

## Trade ends
The `reason` of a `trade_closed` (T-903); only `done` moved anything: `done`, `cancelled`, `too_far`, `dead`, `left_map` (a warp), `disconnected` (the connection dropped, or another session
took the character over), `left` (left the world: logout, server stop, staff), and at the swap, about the side `own` names: `too_heavy`, `inventory_full`, `currency_full`, `offer_changed` (it
no longer carried exactly what it offered).

## Trade terms
(T-903.) **Ask**: one pending question from one player to another on the same map instance, ending after `trade.ask_timeout_ms`. **Offer**: one side's entries (whole, or part of a stack;
at most `trade.max_entries`) and currency, each entry kept exactly as it was offered. **Lock**: a side says its offer is final; any change to either offer unlocks both. **Version**: the
trade's change counter; a **confirm** names the version whose two locked offers it accepts. **Swap**: when both confirmed the same version, the inventory moves both offers in one step, all or
nothing; whole entries keep their ids, a part of a stack becomes a new entry. **Tradeable**: an item field (default true); `false` keeps it with its owner.

## Guild permissions
What a guild rank may do (T-904, ARCHITECTURE §6.11): `invite` (invite characters), `kick` (take out a member of a lower rank), `edit_notice` (write the notice), `set_rank` (give a member of
a lower rank another rank, also below one's own); T-905: `storage_deposit` (put items and currency into the guild's storage), `storage_items` (take items out), `storage_currency` (take
currency out), `storage_log` (read the storage's log). The leader's rank holds every one; naming and ordering the ranks (`guild_ranks`) and handing the leader rank over (`guild_lead`) are the
leader's alone. Nobody acts on a member of the same or a higher rank.

## Guild reasons
The `reason` of a `guild_refused` (T-904): `not_online` (nobody of that name plays on this server process, a tapped player is not on the inviter's map, or the invited blocks the inviter: the same
answer, as for a party), `invalid_target` (oneself, or not a member), `invalid_name` (the naming rules), `name_taken` (letter case does not count), `invalid_emblem` (not one of `guild.emblems`),
`already_in_guild` (one guild per character), `not_in_guild`, `invite_pending`, `no_invite` (nothing to answer: none came, it expired, or the inviter may no longer invite), `full`
(`guild.max_members`), `not_allowed` (the rank lacks the permission, or the act is the leader's), `rank_too_high` (the member's rank, or the rank asked for, is the actor's own or above),
`invalid_ranks` (a rank list the rules refuse), `too_long` (the notice, `--guild-notice-length`), `cannot_afford` (the founding cost), `busy` (a founding of this character is under way),
`rate_limited` (with `retry_after`), `unavailable` (a dev login, or a database that does not answer); T-905's storage: `item_gone` (the entry is not there, or not in that quantity; too little
currency), `item_equipped` (worn items stay in the bag), `not_tradeable` (content's `tradeable: false`), `storage_full` (`guild.storage.max_entries` places, or `max_currency`), `inventory_full`,
`too_heavy`, `currency_full` (the bag's side of a withdrawal), `storage_not_empty` (the last member cannot leave while the storage holds anything).

## Guild storage moves
What a guild storage log line records (T-905, ARCHITECTURE §6.11): `deposit` (into the storage), `withdraw` (out of it).

## Guild news
The `news` of a `guild_news` (T-904): `invite_sent`, `declined`, `expired`, `founded`, `joined`, `left`, `kicked`, `rank` (a member has another rank), `leader` (a new leader), `ranks` (the
leader renamed or reordered the ranks), `notice` (the notice changed), `disbanded` (the last member left).

## Guild terms
(T-904.) **Guild**: a named group of characters stored in the database (`guilds`), with an **emblem** (an asset key of the theme, `guild.emblems`; never uploaded), a **notice** (one text,
cleaned like a chat line, at most `--guild-notice-length`), ranks and members; it holds on every server process that shares the database. **Rank**: a place from 0 (the **leader**'s) down,
with a name and "Guild permissions"; a new member joins at the last. **Member number**: a member's id in the protocol (`member`), never the character's permanent id. **Founding**: the name is
reserved, the tick takes `guild.found_cost` and the founder's next save founds the guild in the same transaction (a **reservation** whose save never lands is dropped). **Successor**: when the
leader goes, the member of the highest rank, the earliest to join among equals; with nobody left the guild is **disbanded** and its name is free. **Tag**: a guild member's emblem shown where its
name shows (`guild_tags`). **Online**: playing on this server process (v1). **Storage** (T-905): the guild's shared entries and currency (`guild.storage`). **Storage lease** (T-810): the
time after `guild_storage_open` during which this server process sends that member the storage's changes. **Hold**: a move under way, made in
the database before the tick moves the bag's side and settled by the character's save that has it (undone when that save never lands). **Storage log**: the landed moves, by character id.

## Admin terms
(T-804, ARCHITECTURE §12.) **Admin API**: the server's private HTTP listener for staff tooling, on its own port. **Service credential**: the secret that proves a request comes from the panel.
**Staff member**: who the panel says makes the request (id, display name, role). **Admin command**: a write through the admin API, checked against the role table and the game like an intent.
**Admin step**: the part of a command that runs on an instance's tick (`AdminIntent`). **Audit row**: one append-only record of an admin change (staff, role, action, target, before, after,
reason, time, request id). **Reason**: the staff member's own words for a write; required.
(T-806.) **Ban** / **mute**: an account restriction the database holds, so every server process enforces it. **Ban watch**: each server's look, every 10 s, for banned accounts playing on it. **Staff note**: the audit row of a
staff change to a character in the world, written in the transaction of the save that first has the change. **`source = admin`**: an item entry whose permanent id is in `admin_items`. **Offline change**: a support fix
to a character no server holds, made in the database under T-702's claim rules. **Maintenance mode**: no new logins on this server, a warning, then every game ends. **Support queue**: deletion and data-export requests that came in through support.

## Entity kinds
In a view record's `kind`: `player`, `monster`, `npc`, `item` (a ground item a monster dropped, `EntityKind.Item`; its `display_key` is the item id).
