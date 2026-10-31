# ADR-001 — 3D presentation on a 2D grid, shipped on Android and iOS

| Field | Value |
|-------|-------|
| Status | Accepted — engine choice pending spike T-005 |
| Date | 2026-10-31 |
| Replaces | Top-down 2D, 32px tiles, 4-direction sprites, desktop client |

## Context
The template was planned as a top-down 2D game with a desktop client. The target is now a 3D
game that is playable on Android and iOS phones. Nothing was built yet, so changing now is cheap.

## Decision

1. **3D presentation, 2D grid simulation.** The server simulates on a cell grid on the ground
   plane (1 cell = 1 world unit, 8 directions). The client renders 3D models on terrain.
   - Pathfinding, AOI, line of sight and range checks stay simple, cheap and deterministic.
   - Every Phase 0–1 task and most server tasks are unaffected.
   - Cost: no multi-floor maps, bridges, jumping or flying in v1.
2. **Fixed-pitch 3/4 camera.** It reads well on a small screen, makes tapping a cell
   reliable, and limits how much of the world is drawn (helps the mobile budget).
3. **Engine: Godot 4 .NET, Mobile renderer** — kept from the original plan. It is free,
   open source, and uses C#, so the client can reference `Game.Shared` directly.
4. **`Game.Shared` stays `netstandard2.1` + C# 9.** Godot's .NET runtime is older than .NET 10,
   and it keeps a fallback engine possible without rewriting the shared domain.
5. **Modular rigged glTF characters** with skinned and bone-attached equipment replace
   paper-doll sprites. The equipment-changes-visuals-through-data rule is unchanged.
6. **Maps are authored in the Godot editor** and a plugin exports the logic grid. Tiled is dropped:
   with 3D scenes, two separate editors would let the layout and the visuals drift apart.
7. **Mobile is a first-class target from Phase 2 on:** budgets in ARCHITECTURE §9, `wss`,
   reconnect/resume, touch input, safe-area UI.

## Risk and fallback
Godot's C# export to Android and iOS has historically been marked experimental. **T-005**
must prove on physical devices that a Godot .NET build can:
- load `Game.Shared` (including under iOS AOT compilation and trimming),
- render a scene at the §9 budget at 30 fps,
- hold a `wss` connection and survive backgrounding.

If any of these fails and has no workaround, the fallback is **Unity 6** (C#, mature
mobile export). `Game.Shared` and every server/tools task carry over unchanged; the client tasks
(T-206 onward) and the map authoring plugin are redone. Unity's license and runtime fee terms
must be checked at that point.

## Consequences
- An iOS build needs a Mac with Xcode (or a macOS CI runner) and an Apple Developer account.
- Prerequisites change: Blender, Android SDK + JDK, Git LFS added; Tiled and Pixelorama removed.
- Art cost is much higher than for 2D sprites; the default theme starts with placeholders.
- The client needs a display-only content bundle (ARCHITECTURE §5.4), because it cannot run
  the YAML loader and must not hold server-only data.

## Spike results
_Filled in by T-005._
