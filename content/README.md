# Content

Game definitions as YAML. The engine is generic; replacing this folder (and `themes/<name>`) makes a
different game. This starter set ("Lumen Vale") is small on purpose: it is the fixture for the loader,
validator, formula and combat tests, and the reference for new content. All names are original.

Read [`docs/VOCABULARY.md`](../docs/VOCABULARY.md) for ids, enums and formula variables, and
[`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) §5 for how content is loaded and validated.

## Layout

| Path | Schema | Holds |
|------|--------|-------|
| `jobs/` | `job.schema.json` | `job.*` — skill tree, bonuses, change requirements |
| `skills/` | `skill.schema.json` | `skill.*` — active and passive skills (including monster skills) |
| `items/` | `item.schema.json` | `item.*` — consumables, equipment, cards, materials, quest items |
| `monsters/` | `monster.schema.json` | `monster.*` — stats, AI, drop table links |
| `statuses/` | `status.schema.json` | `status.*` — buffs and debuffs |
| `quests/` | `quest.schema.json` | `quest.*` — prerequisites, steps, rewards |
| `npcs/` | `npc.schema.json` | `npc.*` |
| `dialogue/` | `dialogue.schema.json` | `dialogue.*` — node graphs |
| `drops/` | `drop_table.schema.json` | `drop.*` |
| `maps/` | `map.schema.json` | `map.*` metadata; the logic grid `<name>.grid.json` is exported from the Godot scene (T-208) |
| `formulas.yaml` | `formula_set.schema.json` | derived stats, damage, hit and crit formulas |
| `balance.yaml` | `balance.schema.json` | stat point awards and cost, stat range, damage and chance clamps |
| `elements.yaml` | `element_table.schema.json` | attacker element × defender element multipliers |
| `exp_tables.yaml` | `exp_table.schema.json` | `exp.*` — base and job level curves |

## Conventions

- **One definition per file**, named after the id without its prefix: `item.iron_blade` is
  `items/iron_blade.yaml`. The exception is `exp_tables.yaml`, a multi-document file (`---`) because the
  schema describes a single table.
- Every file starts with `# yaml-language-server: $schema=...` pointing at the schema in `/schemas`
  (`../../schemas/...` from a subfolder, `../schemas/...` from the content root). Schemas are generated
  from the C# records; never edit them by hand.
- Ids are `prefix.lowercase_name`. Every id you reference must exist, and every asset key must exist in
  `themes/<theme>/asset-manifest.json`. Content never contains file paths.
- Anything that is a formula in the schema (`sp_cost`, `multiplier`, `amount`, modifier `value`, ...) is a
  string expression over whitelisted variables, so write numbers there as quoted strings (`"0"`) or as an
  expression (`6 + level * 2`).
- No game content in code and no balance math outside `formulas.yaml` and the data files.
- `exp_tables.yaml` is generated from the expression in the comment above each table; change the
  expression, regenerate, and do not hand-edit the numbers.
- Run `dotnet run --project src/Game.Tools -- validate` after any change.
