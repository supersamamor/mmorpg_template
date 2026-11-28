# Content editing (T-801)

## Hot reload
Run a development server with `--hot-reload`:

```powershell
dotnet run --project src/Game.Server -- --hot-reload
```

Save a file under `content/`; within about a second the server validates the whole set and swaps it in between ticks. Errors are logged and the running content stays.
What reloads live and what needs a restart: ARCHITECTURE §5.5. In short: numbers, items, monsters, NPCs, dialogue, shops, drops, statuses and formulas reload;
maps, removals of items/skills/jobs/quests/outfits, `appearance.yaml` and some `balance.yaml` sections need a restart (logged as `NEEDS A RESTART`). Jobs, skills,
quests, EXP tables and outfits reload too, as long as nothing is removed.
Every monster respawns at a swap; players stay and are re-entered on their cell. A display change shows "Content changed on the server. Restart the game to see it." in the client.

## The editor

```powershell
dotnet run --project src/Game.Tools -- editor
```

Open http://127.0.0.1:7880/. Pick a kind and a document, change fields, press Save. Every save checks the whole set first and writes nothing on an error.
Value-only saves keep comments and layout; adding or removing keys or list items rewrites the file and loses comments. Details: ARCHITECTURE §5.6.
