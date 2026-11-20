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

**Player characters** — one shared `humanoid` rig (THEMES.md §1.3); bodies, faces and hair come from `content/appearance.yaml`.

| Model key | Job id | Role | Needs |
|-----------|--------|------|-------|
| `model.char.wanderer` | `job.wanderer` | Starting job: light melee, everyone begins here | Humanoid; light clothing; reads as a beginner |
| `model.char.warden` | `job.warden` | Second job: armoured melee, sword and shield | Humanoid; heavy shoulders or a helm |
| `model.char.spellbinder` | `job.spellbinder` | Second job: caster with a staff | Humanoid; a hat or hood; staff clips (`idle.staff`, `attack.staff`) |
| `model.char.body_male`, `model.char.body_female` | (appearance) | The bare bodies gear and hair go on | Humanoid; skin is a colour parameter, no baked skin texture |
| `model.char.face_*`, `model.char.hair_*` | (appearance) | Face and hairstyle options per gender | Faces skinned to `head`; hair attached to `head` |

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

## 4. Licences

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
