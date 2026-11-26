# MMORPG Template

A data-driven, server-authoritative 3D MMORPG template for **Android and iOS**
(Ragnarok-style progression). The engine knows nothing about the game — maps, jobs, skills,
items, monsters, quests and art are all data that can be swapped without code changes.

## Play it on your PC

Windows, with Docker Desktop **running** (the script never starts it), the .NET 10 SDK, Godot 4.7.2 .NET **with its export templates**
([`docs/MOBILE_BUILD.md`](docs/MOBILE_BUILD.md) §1) and the art checked out (`git lfs pull`). From the repo root:

1. Start the server, its database and two game windows (the first time takes several minutes):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\play-local.ps1 -Players 2
   ```
2. In each window: enter a username and a password (not one you use elsewhere), **Register**, create a character, **Play**. With a second account in the second window, the two see each other.
3. Stop (accounts and characters are kept; `-Reset` instead wipes them, after asking):
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\play-local.ps1 -Stop
   ```

The server runs in Docker, reachable from this PC only, over `wss://` with a self-signed certificate made on your PC. It does not touch the development database below.
Details, ports, dev passwords and Linux/macOS: [`docs/LOCAL_PLAY.md`](docs/LOCAL_PLAY.md).

## Prerequisites (Windows)

1. Start **Docker Desktop** (not installed by the script).
2. From the repo root, in PowerShell:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\setup-prereqs.ps1
   ```
3. Open a **new** terminal so PATH updates apply.

The script installs these via winget (already-installed ones are skipped), runs `git lfs install`,
starts PostgreSQL + pgAdmin from `docker-compose.yml`, installs the pinned Android SDK packages and
accepts the SDK licences (when the SDK command-line tools exist; skip with `-SkipAndroid`), and prints the
manual steps left:

| Tool | Why |
|------|-----|
| .NET 10 SDK | server, tools, tests; builds the client's C# |
| Godot 4 (.NET build) | the 3D mobile client |
| Git, **Git LFS** | source control; binary art (glTF, textures, audio, fonts) is stored in LFS, see `.gitattributes` |
| VS Code + extensions | editor (C# Dev Kit, Godot tools, YAML, Docker) |
| **Blender** | authoring and exporting rigged glTF models |
| **JDK 17** (Microsoft OpenJDK) | Godot's Android export; the version pinned in `docs/MOBILE_BUILD.md` |
| **Android Studio** | provides the SDK command-line tools; the script then runs `sdkmanager` for android-35, build-tools 35.0.1, platform-tools and the licences |

Android first run: open Android Studio once, finish the wizard, then SDK Manager > SDK Tools > tick
"Android SDK Command-line Tools (latest)" and re-run the script; it installs the pinned packages and accepts
the licences. What stays manual: pointing Godot at the SDK and JDK and the debug keystore:
[`docs/MOBILE_BUILD.md`](docs/MOBILE_BUILD.md) §1–2.
**iOS builds need a Mac with Xcode** and an Apple ID or Developer account (`docs/MOBILE_BUILD.md` §5).

> **Git LFS:** the remote you push to must support LFS (GitHub does, with quota limits). Run
> `git lfs install` once per machine (the script does). Binaries committed before LFS was enabled stay
> as ordinary git objects; they are not converted.

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
- [`docs/LOCAL_PLAY.md`](docs/LOCAL_PLAY.md) — play on one PC: the server in Docker, a Windows game build
- [`docs/VOCABULARY.md`](docs/VOCABULARY.md)
- [`docs/ROADMAP.md`](docs/ROADMAP.md)
- [`docs/TASKS.md`](docs/TASKS.md) — task tracker: agent, model, prompt, status per task
- [`docs/decisions/ADR-001-3d-mobile.md`](docs/decisions/ADR-001-3d-mobile.md) — why 3D on a grid, why Godot, fallback plan
- [`docs/decisions/ADR-002-follow-up-decisions.md`](docs/decisions/ADR-002-follow-up-decisions.md) — every open question and follow-up from T-001 … T-704, decided

## Status

- [x] Prerequisites (desktop and mobile tooling)
- [~] Phase 0 — Contracts & docs (docs done; solution skeleton, then mobile export spike)
- [ ] Phase 1 — Headless simulation
