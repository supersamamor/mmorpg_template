# Forking: making a game from this template

This repository is a **neutral template**. It keeps the generated placeholder theme (`themes/default`) and placeholder names. A game is a **fork** that adds its
own theme (art, sounds, icons) and, if it wants, its own display names and content. Real art never comes back into the template. The theme contract and the
import tool are in [THEMES.md](THEMES.md).

## 1. Fork and apply a theme

1. **Fork** the repository (or copy it and keep `main` of the template as an upstream remote, so engine fixes can be merged later).
2. **Get a pack** and put it in `packs/<pack-name>/` at the repository root. `packs/` is git-ignored: pack files are never committed (§4).
3. **Write a mapping** in `theme-import/<theme>.yaml` (this file is yours and is committed; it holds no art). Start from the example in THEMES.md §3.1 and the
   role table below: for each key, pick the pack file that fits the **role**.
4. **Trial run** with a throwaway theme name `eval-<name>` (git-ignored):

   ```powershell
   dotnet run --project tools/themeimport -- --mapping theme-import/eval-meadow.yaml
   ```

   Read the report: fix each failure in the mapping (scale, facing, bone and clip names, mesh names) or in Blender (too many triangles, several materials, an
   origin off the ground), and run it again until it ends with `OK`. Running it again changes only what you changed.
5. **Look at it** in the game: `.\client\run-e2e.ps1 -Run ui -Windowed -Screenshot shot.png -ClientArgs "--set","theme=eval-meadow"` (or set `theme` in
   `client/client_settings.json`). Check what the tool cannot: facing of non-humanoids, proportions, the palette, readability at the default zoom on a
   phone-sized view (ADR-003 point 7).
6. **Make it the fork's theme**: rename `theme` in the mapping to the fork's real theme name (no `eval-` prefix), run the import once more, set the client
   setting `theme` to it, and commit the theme folder (Git LFS keeps the binaries, `.gitattributes`) and the mapping. The server needs nothing: the theme is
   client-side, and `validate --theme <theme>` checks it in CI.
7. A theme may stay **partial**: whatever it leaves out comes from `default`. The report's "missing" list is what is still a placeholder.

## 2. Monsters are matched by role, not by name

A pack's "Goblin" is not a template monster. Map by what the monster **does** and how it reads on screen: its rig (body plan), size, level band and whether it
attacks first. A pack with fewer models than the game has monsters can share one model between several keys with **variants** (THEMES.md §2): a recolour, a
bigger elite, or a plain alias.

### Role table (the template's content today)

**Player characters** — one shared `humanoid` rig (THEMES.md §1.3); bodies, faces and hair come from `content/appearance.yaml`, and a player is drawn on its own body (T-706). What a fork must supply for appearance:

1. a **body** per gender and every **face** and **hairstyle** key of `appearance.yaml` (add or remove options there; keep the ids players may have saved: a removed id is drawn as the default);
2. the **paint**: skin, cloth and scalp vertex colours by the contract, or named in `recolor` (THEMES.md §1.7);
3. each **job's look** in `content/jobs/*.yaml`: `look.cloth_color`, and `look.pieces` for a silhouette (hat, tabard, pauldrons; worn-piece models of the theme). The template ships colours only;
4. `hides_hair` on headgear that swallows the hair.

`validate --theme <theme>` checks all of it. The job models in the table are drawn only when a body is missing (§7.5.1); a fork may point them at a body or leave them to `default`.

| Model key | Job id | Role | Needs |
|-----------|--------|------|-------|
| `model.char.wanderer` | `job.wanderer` | Starting job: light melee, everyone begins here | Humanoid; light clothing; reads as a beginner |
| `model.char.warden` | `job.warden` | Second job: armoured melee, sword and shield | Humanoid; heavy shoulders or a helm |
| `model.char.spellbinder` | `job.spellbinder` | Second job: caster with a staff | Humanoid; a hat or hood; staff clips (`idle.staff`, `attack.staff`) |
| `model.char.body_male`, `model.char.body_female` | (appearance) | What every player is drawn on: gear, face, hair and the job's pieces are merged into it | Humanoid; skin, cloth and scalp are painted vertex colours (THEMES.md §1.7), no baked skin texture |
| `model.char.face_*`, `model.char.hair_*` | (appearance) | Face and hairstyle options per gender | Faces skinned to `head`; hair attached to `head`; a hairstyle takes the hair colour as a whole |

**NPCs** — humanoid, stand still, talk.

| Model key | Used by (NPC id) | Role |
|-----------|------------------|------|
| `model.npc.guide` | `npc.guide` (quests, the first dialogue), `npc.merchant` (shop) | A friendly town figure; one model serves both today |
| `model.npc.trainer` | `npc.trainer` (the Warden trainer, dialogue), `npc.smith` (refining) | An authority figure; one model serves both today |

To give the merchant or the smith a model of its own, add a key (a model or a variant of the guide or trainer) to the theme and point the NPC's
`model_asset_key` at it: that is a content change (§3).

**Monsters** — by map, in level order.

| Model key | Monster id | Map | Level | Rig (body plan) | Size | Attacks first | Role |
|-----------|------------|-----|-------|-----------------|------|---------------|------|
| `model.monster.gel_slime` | `monster.gel_slime` | Starter Meadow | 1 | `blob` (no limbs) | small | no | The first, harmless target: soft, round, slow |
| `model.monster.mossling` | `monster.mossling` | Starter Meadow | 3 | `sprout` (a stem and two leaves) | small | no | Walking plant, the second step |
| `model.monster.sting_wasp` | `monster.sting_wasp` | Starter Meadow | 5 | `insect` (body and wings) | small | yes | Small fast flyer with a poison sting |
| `model.monster.rock_beetle` | `monster.rock_beetle` | Starter Meadow | 7 | `insect` | medium | no | Armoured, slow, hard to hurt |
| `model.monster.thornback_boar` | `monster.thornback_boar` | Starter Meadow | 10 | `quadruped` (four legs, a head) | large | yes | The meadow's big threat: tough and hard-hitting |
| `model.monster.thistle_burr` | `monster.thistle_burr` | Thistle Hollow | 8 | `sprout` | medium | no | Spiky plant, the Hollow's first step |
| `model.monster.dewhare` | `monster.dewhare` | Thistle Hollow | 10 | `quadruped` | small | yes | Quick small beast |
| `model.monster.duskwing_moth` | `monster.duskwing_moth` | Thistle Hollow | 12 | `insect` | medium | yes | Large flyer, the Hollow's hardest |
| `model.monster.gel_slime_elite` | (none yet) | — | — | variant of `gel_slime` | — | — | The variant example: a bigger, warmer slime for a fork's elite |

Rig names are the placeholders' and are theme-internal: a pack's quadruped may have any bones, as long as its models and its library agree. The required clips
are the same for every rig (THEMES.md §1.5).

**Props**: `model.prop.loot_bag` (an item on the ground), `model.missing` (the "missing asset" marker; keep it loud).

## 3. Keep the ids, change the display names

- **Ids are the game's contract** (`monster.gel_slime`, `item.iron_blade`, `job.warden`): saves, quests, drops, maps, the golden tests and the server all use
  them. A fork keeps the ids and changes only the **display names** (`name`, `title`, descriptions) in `content/**/*.yaml`. Renaming a display name changes the
  client bundle: run `dotnet run --project src/Game.Tools -- validate` and `export-client`, and rebuild the client. Nothing else changes.
- **Adding or removing a monster is data**: a YAML file in `content/monsters/`, its drops in `content/drops/`, a spawn marker in the map's authoring scene
  (docs/MAP_AUTHORING.md), and a model key in the theme. No code changes. `validate` catches every broken reference.
- **The balance golden tests to update** when monsters, items, jobs or numbers change (docs/BALANCING.md says how; `UPDATE_GOLDEN=1` rewrites the snapshot files,
  the hand-computed ones are redone by hand):

  | Test | What it pins | Update |
  |------|--------------|--------|
  | `tests/Game.Tests/Golden/BalanceGoldenTests.cs` (`damage.json`, `derived_stats.json`, `monster_ttk.json`) | Derived stats per job and damage and time-to-kill against every monster | `UPDATE_GOLDEN=1`, then review the diff |
  | `tests/Game.Tests/Golden/EarlyBalanceTests.cs` | The first fights of a new character on the meadow (T-309, T-404) | Redo the targets |
  | `tests/Game.Tests/Golden/WeaponBalanceTests.cs` | The meadow with weapons, skills and stat points (T-404) | Redo the targets |
  | `tests/Game.Tests/Golden/HollowBalanceTests.cs` | Thistle Hollow's monsters and their reward (T-407) | Redo the targets |
  | `tests/Game.Tests/Combat/GoldenDamageTests.cs` | Hand-computed damage (a Warden on a gel slime) | Redo the arithmetic in the comments |
  | `tests/Game.Tests/Stats/GoldenCharacterTests.cs` | Hand-computed final stats of sample characters | Redo the arithmetic |

  Other tests name sample content to drive a scenario (the scripted client runs hunt the nearest monster; some server tests pick `monster.gel_slime`); a fork
  that removes a monster fixes those the same way. `content/formulas.yaml` is balance: change it on purpose, with the golden tests.

## 4. Re-theming the content

A fork can change more than the art: the same engine makes a pirate game or a ninja game. Everything below is **data** in `content/` (and the theme), except where a
section says code is needed. Every area follows the same rules:

- **Keep the ids** (`job.warden`, `item.iron_blade`). Change the display names, descriptions and numbers instead. An id is a contract: references in other
  files, the tests, the scripted client runs (`client/run-e2e.ps1` names a few items, skills and `monster.gel_slime`) and, once players exist, their saved
  characters all hold ids. **Renaming an id safely** (only before launch, or with a data migration): rename the file and its `id`, replace every reference
  (`git grep -n "item.iron_blade"` over `content/`, `themes/`, `tests/`, `client/`), run `validate` (a reference you missed is a load error that names the file,
  line and path) and the tests. Asset keys follow the same rule: keep `model.monster.gel_slime` even if it now draws a crab.
- **Keep the generic types**: effect types, quest objective types, reward types, dialogue condition and action types and service ids (VOCABULARY). They are the
  engine's primitives; a re-theme recombines them. A new type is code (§4.2 says what that takes).
- **Balance is checked by tests.** `validate` catches broken references, unknown terms and impossible values; the golden and balance tests (§3) catch changed
  balance, and `stats --duel` (docs/BALANCING.md) re-tunes it, for example
  `dotnet run --project src/Game.Tools -- stats --job job.warden --level 10 --duel monster.thornback_boar`.
- **Display text is in the client bundle.** A changed name, description or dialogue line changes the bundle: re-export the reference bundle
  (`export-client content/client-content.reference.json`) in the same change, or `ClientRulesTests` and CI's `export-client --check` fail.

The running example turns the template into **"Saltwind"**, a pirate game. Each example shows only what changes.

### 4.1 Jobs

| | |
|---|---|
| **Files** | `content/jobs/*.yaml`; the starting job is `new_character.start_job_id` in `content/balance.yaml`; the job's look on a character's own body is `look` (`cloth_color`, `pieces`; §1); `model_asset_key` is the fallback body, and the placeholder one takes its two identity colours from `themes/default/palette.json` (`models` → `model.char.<job>`: `colors`, `trim`, `look`) |
| **Rename** | `name`, `description`. The job's colour on a character is `look.cloth_color`; a changed look changes the client bundle (re-export the reference bundle) |
| **Stat bonuses and skill tree** | `stat_bonuses` (modifiers whose `value` is an expression over `job_level`), `skills` (each `skill_id`, `max_level`, and `requires` on other skills of the tree), and the `weapon_types` the job may use |
| **Add or remove a job; the change path** | A new `jobs/<name>.yaml` with `parent_job_id` (the job it changes from) and `requirements` (`min_base_level`, `min_job_level`, `quest_ids`, and `requires_unlock: true` when a quest's `unlock_job` reward must open it, §4.3). Players change job in the skill panel once the requirements hold. Removing a job: delete it, and every reference to it (items' `job_ids`, quests' `job_ids`, dialogue `job_is`) fails `validate` until fixed |
| **Keep** | The ids, `job_exp_table_id`, the starting job (or change `start_job_id` with it) |
| **Caught by** | `validate` (unknown skill, weapon type, stat or parent; a bad expression); `ProgressionRulesTests`, `PlayerSkillTests`, `ValidatorTests`; `run-e2e -Run skills` changes job through the skill panel |
| **Re-tune** | `BalanceGoldenTests` (`derived_stats.json` per job), `GoldenCharacterTests` (hand-computed), `EarlyBalanceTests` and `WeaponBalanceTests` (Wanderer and Warden fights); then `stats --job <job> --level <n> --duel <monster>` |

*Pirate:* `job.wanderer` → "Deckhand"; `job.warden` → "Corsair" (a cutlass is a `sword_1h`, already in its `weapon_types`); `job.spellbinder` → "Tidecaller".
The ids stay, so saves, quests and tests still match.

### 4.2 Skills

| | |
|---|---|
| **Files** | `content/skills/*.yaml` (player and monster skills alike), `content/statuses/*.yaml` for what skills apply |
| **Rename** | `name`, `description`; the icon, effect and sound by asset key (`icon_asset_key`, `vfx_asset_key`, `sfx_asset_key`) |
| **Numbers and effects** | `sp_cost`, `cast_time_ms`, `cooldown_ms`, `target` and `effects`, built from the existing effect types (VOCABULARY "Effect types": `damage`, `heal`, `restore_sp`, `apply_status`, `remove_status`, `knockback`, `teleport`, `summon`, `area`, ...). Expressions use the whitelisted variables (VOCABULARY "Formula variables") |
| **A new skill** | A new `skills/<name>.yaml`, added to a job's `skills` tree (or a monster's `ai.skills`), with its asset keys in the theme |
| **A new effect type** | Code, not data (CLAUDE.md rule 2), as its own task: the definition (`src/Game.Shared/Definitions/Effects.cs` and its discriminator in `Discriminators.cs`), the term in `src/Game.Shared/Vocabulary/EffectTypes.cs` and VOCABULARY "Effect types", where it may stand (`EffectSupport`), the runner (`src/Game.Server/World/Combat/CombatSystem.Effects.cs`), the regenerated schemas (`schema`), and tests (rule 9) |
| **Keep** | The ids, the effect types |
| **Caught by** | `validate` (unknown status, a bad target, an effect where it may not stand: `effect.unsupported`, bad expressions); `EffectTests`, `PlayerSkillTests`, `CombatResolverTests`; `run-e2e -Run skills` and `-Run statuses` cast `skill.mana_font` and `skill.iron_guard` |
| **Re-tune** | `WeaponBalanceTests` (the duel rotation casts skills), `HollowBalanceTests`, `BalanceGoldenTests`; `stats --duel <monster> --skills skill.twin_strike=3` |

*Pirate:* `skill.twin_strike` → "Cutlass Flurry" (the same `damage` effect with `hits: 2`); `skill.ember_bolt` → "Powder Shot"; a "Boarding Hook" that pulls
the target in is a `damage` plus a pull: if `knockback` cannot pull, that is a new effect type task.

### 4.3 Quests

| | |
|---|---|
| **Files** | `content/quests/*.yaml`; who offers a quest is the dialogue (`start_quest` actions, §4.5) and its `giver_npc_id` |
| **Text and objectives** | `name`, `description`, each step's `description`; objectives use the objective types (VOCABULARY: `kill`, `collect`, `talk`, `reach`, `level`, `job`, `use_item`, `equip`) |
| **Step order** | Steps run in list order and only the current step's objectives count. A `talk` objective is the turn-in: it counts once the step's other objectives are met |
| **Rewards** | `rewards`: `exp`, `item`, `currency`, `skill_point`, `stat_point`, `unlock_job`, `status`. Item and currency rewards wait while the bag is full |
| **Job-unlock quests** | A job with `requires_unlock: true` opens only through an `unlock_job` reward. In the template `quest.warden_trial` pays `unlock_job: job.warden`, `job.warden` lists the quest in `requirements.quest_ids`, and `quest.warden_oath` follows it (`prerequisites.quest_ids`) with a `job` objective. Keep that chain intact when rewriting: the reward, the job's `quest_ids` and the follow-up's prerequisite name each other |
| **Keep** | The ids, the objective and reward types |
| **Caught by** | `validate` (unknown monster, item, NPC, map or job); `QuestTests`, `GatewayQuestTests`, `DialogueTests`; `run-e2e -Run quests` plays First Hunt (5 `monster.gel_slime`, 3 `item.gel_glob`) |
| **Re-tune** | EXP and currency rewards move leveling: `stats --leveling <level>` and `EarlyBalanceTests` |

*Pirate:* `quest.first_hunt` → "Clear the Shallows": kill 5 `monster.gel_slime` (now drawn as crabs) and bring 3 `item.gel_glob` (renamed "Crab Shell");
`quest.warden_trial` → "The Corsair's Mark": the same steps and the same `unlock_job` reward, the captain renamed in his NPC file.

### 4.4 Maps

| | |
|---|---|
| **Edit an existing map** | Open its authoring scene (`themes/default/maps/<name>.authoring.tscn`), change the look and the markers (spawns, warps, NPCs, zones), Bake, paint, **Export** (docs/MAP_AUTHORING.md). `.\client\export-maps.ps1 -Check` then proves the committed grid matches the scene |
| **Add a map** | (1) the visual scene `themes/default/maps/<name>.tscn` and its authoring scene; (2) Export, which writes `content/maps/<name>.grid.json`; (3) the scene key in the manifest (`"scene.map.<name>": { "file": "maps/<name>.tscn" }`); (4) `content/maps/<name>.yaml` (`id`, `name`, `scene_asset_key`, `music_asset_key`, `ambience_asset_key`, `grid_file`, `respawn`); (5) the music and ambience keys in the manifest; (6) **warps** both ways: a warp marker on each side (`map_target_map_id`, `map_target_x`, `map_target_y`) onto a walkable cell; (7) **spawns**: spawn markers (`map_monster_id`, `map_w`, `map_h`, `map_count`, `map_respawn_seconds`); (8) the **save point**: the map's `respawn` cell, and, if players should bind there, an NPC whose dialogue has a `set_save_point` action (§4.5); (9) build the client and export again, so the new ids are known to the map dock |
| **Keep** | Map ids; `new_character.start_map_id` and its cell in `balance.yaml` stay on a walkable cell |
| **Caught by** | The map dock's export (a marker on a blocked cell, an unknown id), `validate` (`MapSceneCheck`: the grid matches its scene; warps, spawns, NPCs and save points point at real maps, monsters and walkable cells), `GridContentTests`, `MapSceneHashTests`; `run-e2e -Run warp` and `-Run hill` |
| **Re-tune** | New spawns change who players meet: `HollowBalanceTests` is the model for a new area's targets |

*Pirate:* Starter Meadow becomes "Gull's Rest", a beach (a new look in the same scenes, the id `map.starter_meadow` unchanged); a new `map.sunken_reef` is
reached by a warp at the end of the pier, with spawns of crabs and eels and its respawn cell on the jetty.

### 4.5 NPCs and dialogue

| | |
|---|---|
| **Files** | `content/npcs/*.yaml` (`name`, `title`, `dialogue_id`, `model_asset_key`, `portrait_asset_key`), `content/dialogue/*.yaml` (node graphs), `content/shops/*.yaml` |
| **Rename** | `name`, `title`, and the dialogue lines (`text` of nodes and choices) |
| **A new NPC and its place** | A new `npcs/<name>.yaml`, a dialogue for it, and an NPC marker in the map's authoring scene (`map_marker: npc`, `map_npc_id`, `map_facing`), then Export (§4.4) |
| **Dialogue graphs** | `start` and `nodes`: each node has a `speaker_npc_id`, `text` and `choices` (`text`; `next`: a node, or `null` to end). A choice shows only when all its `conditions` hold (`base_level`, `job_level`, `job_is`, `job_is_not`, `has_item`, `has_status`, `quest_state`, `quest_step`, `out_of_combat`) and runs its `actions` in order (`start_quest`, `set_save_point`, `run_effects`, `take_item`, `give_item`, `open_service`) |
| **Services** | The shop: `open_service` with `service: shop` and a `shop_id` (`content/shops/<name>.yaml`: a `stock` list); refining: `service: refine` (the rules in `content/refine.yaml`); the save point: a `set_save_point` action with a cell of the NPC's map; resets: `run_effects` with `reset_stats` or `reset_skills` |
| **Keep** | NPC, dialogue and shop ids; the condition, action and service terms |
| **Caught by** | `validate` (a `next` node that does not exist, an unknown quest, item, shop or status, a save point off the walkable grid); `DialogueTests`, `GatewayDialogueTests`, `ShopTests`, `GatewayShopTests`; `run-e2e -Run dialogue` taps the guide and walks two nodes to the save point |

*Pirate:* `npc.guide` → "Old Mags, Harbour-mistress", her lines rewritten, and "Make the meadow my home." becomes "Drop anchor here." (the same
`set_save_point`); `npc.merchant` → "Quartermaster Pell", with `shop.meadow_goods` renamed "Ship's Stores"; `npc.smith` → "the Shipwright" (`service: refine`).

### 4.6 Monsters and drops

| | |
|---|---|
| **Re-skin by role** | Keep the monster; change its `name` and its model by role (§2: a crab for the passive blob, a gull for the fast flyer). The stats and AI stay, so the balance stays |
| **New monsters** | A new `monsters/<name>.yaml` (`level`, `race`, `size`, `element`, `stats`, `ai`, `drop_table_ids`, `model_asset_key`, `sounds`), its model key in the theme, and spawn markers (§4.4) |
| **Drop tables** | `content/drops/*.yaml`: `entries` of `item_id`, `chance` (0 to 1), `min`, `max`; the shared tables (`tier1_supplies` ...) can be listed by several monsters |
| **Keep** | Monster and drop ids; races, sizes and elements are vocabulary |
| **Caught by** | `validate` (an unknown drop table, item, skill or element; a chance out of range); `CombatLoopTests`, `GatewayCombatTests`; `run-e2e` hunts `monster.gel_slime` in `-Run quests` and the nearest monster elsewhere |
| **Balance targets** | docs/BALANCING.md §5 and §6: a win rate of 50–80 % against each monster for its level band, and EXP per minute that rises from one area to the next. Re-tune with `stats --duel`, then update `BalanceGoldenTests` (`UPDATE_GOLDEN=1`), `EarlyBalanceTests`, `WeaponBalanceTests`, `HollowBalanceTests` and `GoldenDamageTests` |

*Pirate:* `monster.gel_slime` → "Shore Crab" (a `blob`-rig model is fine for a crab that only scuttles), `monster.sting_wasp` → "Needle Gull",
`monster.thornback_boar` → "Reef Brute"; a new `monster.bilge_eel` for the Sunken Reef, with its own drop table.

### 4.7 Items

| | |
|---|---|
| **Files** | `content/items/*.yaml`; the start kit is `new_character.start_items` in `balance.yaml` |
| **Rename** | `name`, `description`; the icon (`icon_asset_key`), worn look (`equip.visual_asset_key`) and sounds by key |
| **New weapons and armour** | A new item with `kind: equip` and `equip` (`slots`, `weapon_type`, `weapon_atk`, `job_ids`, `modifiers`, `card_slots`, `refinable`, `visual_asset_key`). Add its `icon.item.*` and `model.equip.*` keys to the manifest (`attach` for a weapon or shield, `skinned` for armour). In `default`, `dotnet run --project tools/themegen` then draws a **placeholder shape from the item's `weapon_type`** (a dagger is a short blade, a staff stands up: `tools/themegen/Weapons.cs`) |
| **Shop prices** | `buy_price` and `sell_price` on the item; a shop's `stock` row may set its own price |
| **Keep** | Item ids, the weapon types and equipment slots |
| **Caught by** | `validate` (an unknown job, slot, weapon type or asset key; a worn visual without a model is a warning), ThemeCheck (the attach bone exists), `InventoryTests`, `CardRefineTests`, `ShopTests`; `run-e2e -Run items` equips `item.bone_dirk` and `item.padded_vest` |
| **Re-tune** | `WeaponBalanceTests`, `HollowBalanceTests`, `BalanceGoldenTests`, `GoldenDamageTests` (the Iron Blade +3) |

*Pirate:* `item.iron_blade` → "Cutlass" (still `sword_1h`), `item.oak_staff` → "Driftwood Staff", `item.padded_vest` → "Sailcloth Vest",
`item.healing_draught` → "Grog"; a flintlock needs a ranged weapon type the vocabulary has (`bow`, with an `attack_range` above 1) or a new one (code).

### 4.8 Words on screen

| | |
|---|---|
| **The currency** | `currency_name` in `content/balance.yaml` (it travels in the client bundle) |
| **The client's own words** | `client/Core/UiText.cs`: every button, title and message the client writes itself rather than reading from content. Edit the strings, keep the member names. Long words must still fit: `run-e2e -Run ui` checks the layouts at every screen shape with the longest texts |
| **The game title** | `config/name` in `client/project.godot`; `package/name`, and before the first store upload `package/unique_name` (Android) and `application/bundle_identifier` (iOS), in `client/export_presets.cfg` |
| **Caught by** | `UiShellTests`, `ClientRulesTests`, `ShopClientTests`; `run-e2e -Run ui` and `-Run release` |

*Pirate:* `currency_name: doubloons`; `UiText.BagTitle` "Sea chest", `UiText.Respawn` "Back aboard"; the title "Saltwind" and the package
`com.example.saltwind`.

## 5. Licences

- **CC0 packs** (public domain, for example many low-poly packs from community artists): may be committed to the fork's theme and shipped. Keep a
  `themes/<theme>/CREDITS.md` with the pack's name, author, source and licence anyway; it costs nothing and helps the next person.
- **Paid or restricted packs**: the licence usually allows shipping the game but **not** redistributing the source files. The fork's repository is a
  redistribution if it is public or shared. So:
  - never commit the pack's source files (`packs/` is git-ignored for this reason), and keep the mapping, which holds no art;
  - the converted files in `themes/<theme>/` are derived from the pack; commit them only to a private repository, and only if the licence allows it.
    When in doubt, keep the theme folder out of git and rebuild it from the mapping on each machine.
- **AI-generated models** (the later T-225 pipeline) go into a fork the same way, with the generator's terms in `CREDITS.md`.
- **This template** keeps only its own generated placeholders. No pack, real or trial, is ever committed here; trial themes are `eval-*` and git-ignored.
- Original names and designs only: the template's systems are inspired by classic MMORPGs, but a fork must not copy another game's names, data tables or art.

## 6. Checklist: a full re-theme, in order

Run the commands after each step, in PowerShell from the repository root. Short names used below: **validate** is
`dotnet run --project src/Game.Tools -- validate`; **bundle** is `dotnet run --project src/Game.Tools -- export-client content/client-content.reference.json`;
**tests** is `dotnet test`.

1. **Plan the roles.** Fill in §2's role table for the new setting: what each job, monster, NPC, item and map becomes, and which ids stay. No commands.
2. **Display names and descriptions** of jobs, skills, items, monsters, NPCs, maps and quests, the dialogue lines, and `currency_name` (§4.1–§4.8).
   Run validate, bundle, tests.
3. **The client's words and the title** (`UiText`, `project.godot`, `export_presets.cfg`; §4.8). Run `dotnet build client/GameClient.csproj`, then
   `.\client\run-e2e.ps1 -Run ui`.
4. **Skills and statuses** (§4.2). Run validate, bundle, tests. After a new effect type: `dotnet run --project src/Game.Tools -- schema`, then
   `git diff --exit-code schemas/` shows only the intended change; run `dotnet run --project src/Game.Tools -- schema` again and expect no diff; then tests.
5. **Jobs** (§4.1). Run validate, bundle, tests; re-tune with `dotnet run --project src/Game.Tools -- stats --job <job> --level <n> --duel <monster>`.
6. **Items, prices and shops** (§4.7). Run validate, bundle; after new keys in `default`, `dotnet run --project tools/themegen`, then
   `dotnet run --project tools/themegen -- --check`; then tests.
7. **Monsters and drops** (§4.6). Run validate, bundle, `stats --duel` per monster, then the golden snapshots:
   `$env:UPDATE_GOLDEN = "1"; dotnet test --filter "FullyQualifiedName~BalanceGoldenTests"; Remove-Item Env:UPDATE_GOLDEN`. Review the diff, redo the
   hand-computed and target tests (§3), then tests.
8. **Maps** (§4.4): edit in the authoring scenes, Export, add the new maps. Run `.\client\export-maps.ps1 -Check`, validate, bundle, tests,
   `.\client\run-e2e.ps1 -Run warp`.
9. **NPCs, dialogue and services** (§4.5), placed on the maps. Run validate, bundle, tests, `.\client\run-e2e.ps1 -Run dialogue`.
10. **Quests** (§4.3), keeping the job-unlock chain. Run validate, bundle, tests, `.\client\run-e2e.ps1 -Run quests`.
11. **The art** (§1): `dotnet run --project tools/themeimport -- --mapping theme-import/<theme>.yaml` until it ends with `OK`, then
    `dotnet run --project src/Game.Tools -- validate --theme <theme>`.
12. **Everything together.** `dotnet build` (0 warnings); tests; validate; `dotnet run --project src/Game.Tools -- schema` (no diff);
    `dotnet run --project tools/themegen -- --check`; `dotnet run --project src/Game.Tools -- export-client --check content/client-content.reference.json`;
    `.\client\export-maps.ps1 -Check`; `.\client\run-e2e.ps1 -Run all`. Then play it once with `-Windowed`, at the default zoom on a phone-sized view.
