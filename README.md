# MMORPG Template

A data-driven, server-authoritative 2D MMORPG template (Ragnarok-style progression).
The engine knows nothing about the game — maps, jobs, skills, items, monsters, quests
and art are all data that can be swapped without code changes.

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
  Game.Shared     definitions, formulas, modifier pipeline, packets
  Game.Content    loader, registry, cross-reference validator
  Game.Server     authoritative tick loop, networking, persistence
  Game.Tools      content CLI (validate, schema gen)
/tests            Game.Tests
/client           Godot 4 C# project (renderer/input only — no game rules)
/content          THE GAME: maps, jobs, skills, items, monsters, quests, npcs
/themes/default   art, audio, fonts + asset-manifest.json
/docs             ARCHITECTURE, CLAUDE, VOCABULARY, ROADMAP
```

## Locked decisions

Top-down 2D (32px grid) · RO-style stats (STR/AGI/VIT/INT/DEX/LUK) · WebSockets · YAML content · 10 Hz server tick

## Docs

- [`CLAUDE.md`](CLAUDE.md) — rules for AI agents (read first)
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)
- [`docs/VOCABULARY.md`](docs/VOCABULARY.md)
- [`docs/ROADMAP.md`](docs/ROADMAP.md)
- [`docs/TASKS.md`](docs/TASKS.md) — task tracker: agent, model, prompt, status per task

## Status

- [x] Prerequisites
- [~] Phase 0 — Contracts & docs (docs done; solution skeleton next)
- [ ] Phase 1 — Headless simulation
