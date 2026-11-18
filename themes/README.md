# Themes

`themes/<theme>/asset-manifest.json` maps asset keys to files (ARCHITECTURE §8). The client mounts the folder at `res://themes` (§7.7, `client/mount-themes.ps1`).

- `default` is the **generated placeholder pack** (ADR-002 row 31): one real file per manifest key, written by `tools/themegen`. The exception is `maps/`: map scenes are authored in the editor ([docs/MAP_AUTHORING.md](../docs/MAP_AUTHORING.md)), and `tools/themegen` leaves them alone.
  Rebuild it with `dotnet run --project tools/themegen`; check it with `-- --check`. Do not edit the generated files by hand. Keys and their metadata
  (`rig`, `anims`, `attach`, `skinned`, `clips`, `fallbacks`, `audio`) live in the manifest, which is the input; the generator only rewrites each entry's file path.
  Binary files are Git LFS (`.gitattributes`). Real art replaces the files and their manifest paths, theme by theme, with no code change.
- `default/palette.json` is the theme's shared palette (ADR-003 point 4, T-222): named colours, the lists creatures, gear and hair pick from, and each job's two
  identity colours, trim and look. `tools/themegen` colours the placeholders from it (as vertex colours on a white material); the game never reads it.
- `default/render/` holds the storybook toon look (ADR-003, T-222), authored by hand and tagged in the manifest with `"render"`: the toon material (`mat.toon`,
  three cel steps from `toon_light.gdshaderinc`, which the map scenes' ground includes too), the outline (`mat.outline`) and the environment scene
  (`scene.environment`: sky gradient, flat ambient, haze, the warm key light). `tools/themegen` leaves them alone.
- `test` is a partial theme (three models in other colours). It exists so tests and `--set theme=test` can show that a swap changes visuals; whatever it
  leaves out comes from `default`.
- Godot writes `*.import` files beside the assets it reads; they are gitignored.
