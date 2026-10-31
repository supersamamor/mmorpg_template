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
T-005, 2026-11-01. Project: `spikes/mobile-export/`. How to reproduce: [MOBILE_BUILD.md](../MOBILE_BUILD.md).

### Verdict

| Platform | Result | Why |
|----------|--------|-----|
| Android | **Pending device run** | Build exported; waiting for measurements on a physical phone. |
| iOS | **Blocked** | No Mac available. A C# Godot project can only be exported to iOS from macOS (NativeAOT + Xcode toolchain). Nothing was measured on iOS; the AOT/trimming risk is still open. |

### Versions
- **Godot 4.7.2-stable, .NET build.** `Godot.NET.Sdk/4.7.2`. GodotSharp 4.7.2 ships for net8.0, so desktop and
  iOS target **net8.0**; the prebuilt **Android template only accepts net9.0**, so Android targets **net9.0**
  (conditional `TargetFramework` in the csproj). Built with the .NET 10.0.401 SDK.
- Runtime on device: Android uses the **Mono** runtime (`UseMonoRuntime`); iOS uses **NativeAOT**
  (`PublishAot`, trimmed) — set by the Godot SDK, not by us.
- `Game.Shared` is referenced as its `netstandard2.1` build (`SetTargetFramework`), and rooted for the
  trimmer (`TrimmerRootAssembly`), because the probe reaches it by reflection.

### Android build (measured on the PC)
- arm64-v8a export works from Windows with JDK 17, build-tools 35.0.1, platform android-35, no Gradle build.
- The APK contains `Game.Shared.dll` (netstandard2.1), the Mono runtime and the pinned dev certificate.
- **APK size: 103.9 MB debug, 102.1 MB release** (release signed with the debug key, only to measure size).
  About 60 MB uncompressed of it is static Mono archives (`libmonosgen-2.0.a` alone is 44 MB) that the app
  cannot use at runtime. Under the 200 MB install budget, but stripping them is the first size win.

### Desktop sanity run (not a budget measurement)
Windows 11, AMD Ryzen 7 7735U with integrated "AMD Radeon(TM) Graphics", 1280×720 window, cap 30, 40 characters (High, 4,968 tris each),
shadows PlayerOnly, after `--autoreport` (12 s):

| Renderer | avg fps (10 s) | worst frame | draw calls | triangles |
|----------|----------------|-------------|------------|-----------|
| Mobile (Vulkan) | 29.5 | 144.5 ms (start-up shader compile) | 98 | 216.9k |
| Compatibility (OpenGL 3.3) | 29.1 | 150.0 ms (start-up) | 98 | 217.2k |

`wss://127.0.0.1:8443` connected with certificate verification (pinned dev cert); `ws://` RTT on localhost
was 26–33 ms because the socket is polled once per frame at 30 fps. `Game.Shared` loaded (`.NETStandard,Version=v2.1`).

### Findings so far
- **§9 is internally inconsistent:** 40 characters × the 5k-triangle character cap = 200k triangles, above the
  150k scene cap before any terrain or props. The spike measures both 40 × ~5.0k ("High") and 40 × ~3.2k ("Low").
  §9 needs either a ~3k character cap or a higher scene cap — decided from the device numbers.
- **Draw calls sit right at the cap:** the test scene (40 skinned characters, 10 trees of 2 meshes, 2 batched prop
  MultiMeshes, ground, one shadow-casting character) measured 98 draw calls. Godot does not batch separate skinned
  meshes, so every on-screen character costs at least one draw call; map scenery must be MultiMesh-batched.
- **`Game.Shared` has no public types yet** (T-002 in progress), so on device the probe can only prove that the
  assembly is packaged, loads and exposes metadata. The probe already constructs a record and reads `IdPrefix`
  once T-002 lands; re-run on device after T-002 merges.
- **.NET 8 and .NET 9 both reach end of support on 2026-11-10**, and Godot 4.7.2 builds the client against them
  (net8.0 desktop/iOS, net9.0 Android). Moving the client to net10.0 depends on a Godot release whose templates
  accept it.
- **Cleartext `ws://` is policed by the client, not the OS** (expected; confirm on device): Godot uses native
  sockets, which Android's network-security config and iOS ATS do not cover.
- **Command-line export hangs after writing the APK** (Godot 4.7.2, `--headless --export-*`): it must be killed.
  CI will need a timeout + file check until this is fixed upstream.
