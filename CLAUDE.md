# CLAUDE.md — Rules for AI agents working in this repo

Read `docs/ARCHITECTURE.md` and `docs/VOCABULARY.md` before changing code.
If a request conflicts with these rules, stop and say so instead of working around them.

## Locked decisions (do not change without explicit instruction)
- Perspective: **top-down 2D**, grid-based movement, 32px tiles, 4-direction sprites.
- Stats: **STR, AGI, VIT, INT, DEX, LUK** (ids in VOCABULARY.md).
- Transport: **WebSockets**, JSON messages (MessagePack later if needed).
- Content format: **YAML**, validated against JSON Schemas generated from C# definitions.
- Stack: .NET 10 (server/shared/tools), Godot 4 C# (client), PostgreSQL (player state only).
- Server tick: **10 Hz**.

## Hard rules
1. **Server is authoritative.** The client sends *intents* (move to X, use skill Y on Z).
   The client never computes damage, drops, EXP, cooldowns, or validates anything that matters.
2. **No game content in code.** Never hardcode item/skill/monster/job/map/quest ids, names,
   numbers, or formulas in C#. If you need a new behavior, add a generic primitive
   (effect type, objective type, condition type) and expose it to data.
3. **No asset paths in content or code.** Reference art/audio by **asset key**
   (`sprite.monster.slime`); only `themes/<theme>/asset-manifest.json` maps keys to files.
4. **All ids are namespaced strings** (`item.knife`, `skill.bash`). See VOCABULARY.md.
5. **Every cross-reference is validated at load.** A missing id is a load error, not a runtime null.
6. **Formulas live in `content/formulas.yaml`** as expressions. Don't inline balance math in C#.
7. **Systems talk via the event bus**, not direct calls into each other (quests don't call combat).
8. **Deterministic, testable core.** `Game.Shared` has no I/O, no networking, no Godot, no DB.
   Inject time and RNG (`IClock`, `IRandom`) — never `DateTime.Now` or `new Random()` there.
9. **Every new system ships with tests** in `tests/Game.Tests`. Balance-relevant changes
   update or add golden-value tests.
10. **Persistence mutations are transactional.** Item moves, trades, and drops are atomic.

## Restricted areas — only touch when explicitly asked
- `src/Game.Server/Net/**` (networking)
- `src/Game.Server/Persistence/**` (database, migrations)
- `content/formulas.yaml` (balance)

## Task workflow
- Every piece of work has a row in `docs/TASKS.md` and a prompt in `tasks/prompts/`.
- Work only on the task ID you were given. Set it `In Progress` when you start and `Review` when done.
- Never mark a task `Done` — a human does that after review.
- Discovered work becomes a new suggested task in your reply, not extra changes.
- Commit message format: `T-xxx: <short description>`.

## Working style
- One system per task. Keep diffs focused.
- Run `dotnet build` and `dotnet test` before declaring done.
- Run `dotnet run --project src/Game.Tools -- validate` after any content change.
- If you add a vocabulary term (stat, effect type, event, element), add it to VOCABULARY.md in the same change.
- Use original names and art only. Ragnarok is inspiration for systems; never copy its names, data tables, or assets.
