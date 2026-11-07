# Making a map

A map is built as a 3D scene in the Godot editor and exported to a logic grid the server reads. What each part is: [ARCHITECTURE §5.3](ARCHITECTURE.md). Height is for looking at only (§4).

## Two scene files

| File | Holds | In the app? |
|------|-------|-------------|
| `themes/<theme>/maps/<name>.tscn` | What you see, and the collision shapes | Yes |
| `themes/<theme>/maps/<name>.authoring.tscn` | The size, the slope limit, painted cells and marker nodes, with the first file instanced inside it | **Never** (the export presets exclude it): it names spawns, and the client must not get those (hard rule 11) |

## From nothing to a playable map

1. **Run the setup once per checkout:** `.\client\mount-themes.ps1`, then `dotnet build client/GameClient.csproj`. Open `client/project.godot`. The **Map** dock is on the right.
2. **Build the look and the ground.** Make a new 3D scene, save it as `res://themes/default/maps/<name>.tscn`. 1 world unit is 1 cell; cell `(x, y)` covers `x..x+1`, `z = y..y+1`, and `(0, 0)` is the north-west corner, so the map lies on the positive X and Z side of the origin.
   - The ground needs collision: a `StaticBody3D` with `CollisionShape3D` children. A cell with nothing under it is not walkable.
   - A body with metadata `map_walkable = false` is not walkable (water). Add `map_blocks_los = true` if it also blocks line of sight (walls, rocks). The flat top of a wall is otherwise walkable.
   - Meshes without collision (decoration) do not affect the grid at all, and changing them never needs a new export.
3. **Create the authoring scene.** With that scene open, set the size in the Map dock and press **Create authoring scene from this scene**. It opens `<name>.authoring.tscn`; the slope limit starts at the plugin's `default_slope_limit_degrees` (`client/addons/map_authoring/config.json`) and stays in the scene as `map_slope_limit_degrees`.
4. **Press Bake.** The overlay shows each cell: green walkable, red blocked, purple blocks line of sight. A yellow dot marks a painted cell.
5. **Paint what the bake gets wrong.** Pick a brush and drag with the left mouse in the 3D view: force walkable, force blocked, blocks line of sight, line of sight open, or clear. Set the brush back to **Off** to select nodes again. Paint is saved in the scene (`map_overrides`), so a new bake keeps it. Undo works.
6. **Add markers.** Add a `Marker3D` under `Markers` and put it on a cell. Set its metadata (Inspector, bottom, **Add Metadata**). An area's marker stands on its north-west cell.

   | `map_marker` | Other metadata |
   |--------------|----------------|
   | `spawn` | `map_monster_id`, `map_w`, `map_h`, `map_count`, `map_respawn_seconds` |
   | `warp` | `map_target_map_id`, `map_target_x`, `map_target_y` |
   | `npc` | `map_npc_id`, `map_facing` (`n`, `ne`, `e`, ...) |
   | `zone` | `map_zone_kind` (`safe`, `pvp`), `map_w`, `map_h` |

7. **Press Export.** It saves the scene and writes `content/maps/<name>.grid.json`. Problems show in the dock (a marker on a blocked cell, an unknown id); click one to select its node. Nothing is written while there is a problem.
8. **Connect it to the content.** Add the scene key to `themes/default/asset-manifest.json` (`"scene.map.<name>": { "file": "maps/<name>.tscn" }`) and write `content/maps/<name>.yaml` (`MapDef`: `scene_asset_key`, music, `grid_file: <name>.grid.json`). Do this after the first export: the content does not load with a `grid_file` that does not exist yet. Build the client again, so marker ids and warps to this map are known to the export, and export once more.
9. **Check it:** `dotnet run --project src/Game.Tools -- validate`. Commit both scenes, the grid and the YAML.

## After that

- **Changed collision, paint or a marker?** Export again. `validate` fails with `grid.scene_stale` until you do, and it needs no Godot. A change to meshes, materials or lights does not count.
- **Headless:** `.\client\export-maps.ps1` exports every map, `-Map <name>` one, and `-Check` writes nothing and fails if a fresh export differs from a committed grid by a byte. The dock's buttons run this same process, so a grid never depends on the editor's camera or selection.
- **Instanced art:** an instanced `.tscn` in a theme is read too. A `.glb` or `.res` counts by its file content, so replacing one calls for a new export even if only its look changed. Map scenes themselves must be `.tscn`, because `validate` reads them as text.
- **A theme that re-skins a map** keeps the layout: use the same collision and markers, and the hash stays the same (§5.3).
