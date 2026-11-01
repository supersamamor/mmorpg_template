# MMORPG Template

A data-driven, server-authoritative 3D MMORPG template for **Android and iOS**
(Ragnarok-style progression). The engine knows nothing about the game — maps, jobs, skills,
items, monsters, quests and art are all data that can be swapped without code changes.

## Prerequisites (Windows)

1. Start **Docker Desktop**.
2. From the repo root, in PowerShell:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\setup-prereqs.ps1
   ```
3. Open a **new** terminal so PATH updates apply.

Installs (via winget, skips what's already present): .NET 10 SDK, Godot 4 (.NET build),
Tiled, Git, VS Code + extensions, Pixelorama. Starts PostgreSQL + pgAdmin from
`docker-compose.yml`.

> **The script predates the switch to 3D mobile** ([ADR-001](docs/decisions/ADR-001-3d-mobile.md)).
> Until T-006 updates it, also install by hand: **Blender**, **Android Studio** (Android SDK +
> JDK 17), and **Git LFS**. Tiled and Pixelorama are no longer needed.
> **iOS builds need a Mac with Xcode** and an Apple Developer account; T-005 documents the steps
> in `docs/MOBILE_BUILD.md`.

| Service    | Address                | Credentials (dev only)          |
|------------|------------------------|---------------------------------|
| PostgreSQL | `localhost:5432`       | db `mmo_game`, user `mmo` / `mmo_dev_password` |
| pgAdmin    | http://localhost:8081  | `dev@local.test` / `admin`      |

### Database commands
```powershell
docker compose up -d      # start
docker compose down       # stop (data kept)
docker compose down -v    # stop and WIPE all data
```

## Repository layout (planned)

```
/src
  Game.Shared     definitions, formulas, modifier pipeline, grid math, packets
  Game.Content    loader, registry, cross-reference validator, client bundle export
  Game.Server     authoritative tick loop, networking, persistence, internal admin API
  Game.Admin      internal web panel for live ops (staff only, never shipped)
  Game.Tools      content CLI (validate, schema gen, stats, export-client)
/tests            Game.Tests
/client           Godot 4 .NET 3D project for Android/iOS (renderer/input only — no game rules)
/content          THE GAME: maps, jobs, skills, items, monsters, quests, npcs
/themes/default   3D models, animations, map scenes, audio, UI, fonts + asset-manifest.json
/docs             ARCHITECTURE, VOCABULARY, ROADMAP, TASKS, decisions/ (ADRs)
```

## Locked decisions

3D on a 2D grid (fixed 3/4 camera, 8 directions) · Android + iOS · Godot 4 .NET · RO-style stats (STR/AGI/VIT/INT/DEX/LUK) · WebSockets (`wss`) · YAML content · 10 Hz server tick

## Docs

- [`CLAUDE.md`](CLAUDE.md) — rules for AI agents (read first)
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- [`docs/VOCABULARY.md`](docs/VOCABULARY.md)
- [`docs/ROADMAP.md`](docs/ROADMAP.md)
- [`docs/TASKS.md`](docs/TASKS.md) — task tracker: agent, model, prompt, status per task
- [`docs/decisions/ADR-001-3d-mobile.md`](docs/decisions/ADR-001-3d-mobile.md) — why 3D on a grid, why Godot, fallback plan

## Status

- [x] Prerequisites (desktop; mobile tooling pending T-006)
- [~] Phase 0 — Contracts & docs (docs done; solution skeleton, then mobile export spike)
- [ ] Phase 1 — Headless simulation
