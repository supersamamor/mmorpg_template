# Themes

`themes/<theme>/asset-manifest.json` maps asset keys to files (ARCHITECTURE §8). The client mounts the folder at `res://themes` (§7.7, `client/mount-themes.ps1`).

- `default` is the **generated placeholder pack** (ADR-002 row 31): one real file per manifest key, written by `tools/themegen`.
  Rebuild it with `dotnet run --project tools/themegen`; check it with `-- --check`. Do not edit the generated files by hand. Keys and their metadata
  (`rig`, `anims`, `attach`, `skinned`, `clips`, `fallbacks`, `audio`) live in the manifest, which is the input; the generator only rewrites each entry's file path.
  Binary files are Git LFS (`.gitattributes`). Real art replaces the files and their manifest paths, theme by theme, with no code change.
- `test` is a partial theme (three models in other colours). It exists so tests and `--set theme=test` can show that a swap changes visuals; whatever it
  leaves out comes from `default`.
- Godot writes `*.import` files beside the assets it reads; they are gitignored.
