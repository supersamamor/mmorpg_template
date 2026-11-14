# Mobile build guide (Android + iOS)

How to build the Godot .NET client for phones. Written during spike T-005 against the
throwaway project in `spikes/mobile-export/`; the same steps apply to `/client` from T-206 on (§8 covers running it).
Why Godot, and what the spike measured: [ADR-001](decisions/ADR-001-3d-mobile.md).

| Pinned | Version |
|--------|---------|
| Godot | **4.7.2-stable, .NET build** (`GodotEngine.GodotEngine.Mono` on winget) |
| Godot C# SDK | `Godot.NET.Sdk/4.7.2`. `TargetFramework` **net8.0** (desktop, iOS); **net9.0 for Android** — the 4.7.2 Android template requires it (§4) |
| .NET SDK used to build | 10.0.x (repo `global.json`); it builds the client and `Game.Shared`'s netstandard2.1 target |
| Android | JDK 17 (Microsoft OpenJDK), SDK platform-tools, build-tools 35.0.1, platform android-35 |
| iOS | macOS with Xcode 16+, Apple ID (free) or Apple Developer account |

All paths below are for Windows 11 unless marked macOS.

---

## 1. Install Godot 4.7.2 .NET and the export templates

```powershell
winget install --id GodotEngine.GodotEngine.Mono -e --version 4.7.2
```

The editor ends up in
`%LOCALAPPDATA%\Microsoft\WinGet\Packages\GodotEngine.GodotEngine.Mono_*\Godot_v4.7.2-stable_mono_win64\`.
The `_console.exe` next to it prints logs to the terminal; use it for command-line exports.

Export templates (~1 GB, Android + iOS + desktop, **.NET flavour**). Either:
- Editor → **Editor ▸ Manage Export Templates ▸ Download and Install**, or
- by hand: download `Godot_v4.7.2-stable_mono_export_templates.tpz` and `SHA512-SUMS.txt` from
  <https://github.com/godotengine/godot/releases/tag/4.7.2-stable>, check the SHA-512, then unzip the
  `.tpz` (it is a zip) and move the contents of its `templates/` folder to
  `%APPDATA%\Godot\export_templates\4.7.2.stable.mono\`.

Non-.NET templates do not work for a C# project; the folder name must end in `.mono`.

## 2. Android toolchain

### 2.1 JDK 17

```powershell
winget install --id Microsoft.OpenJDK.17 -e
```

### 2.2 Android SDK packages and licences

`setup-prereqs.ps1` does this step for you. It installs Android Studio, and once the SDK command-line tools exist at
`%LOCALAPPDATA%\Android\Sdk\cmdline-tools\latest\bin\sdkmanager.bat` it accepts the licences and installs the pinned
packages (android-35, build-tools 35.0.1, platform-tools) with the JDK 17 from §2.1. Re-running is safe: installed
packages are skipped. `-SkipAndroid` leaves this step out.

First time on a machine:
1. Open Android Studio once and finish the setup wizard.
2. SDK Manager > SDK Tools > tick **Android SDK Command-line Tools (latest)** > Apply.
3. Re-run `setup-prereqs.ps1`.

Without Android Studio you can unzip Google's "command line tools only" package
(<https://developer.android.com/studio#command-line-tools-only>, check its SHA-1 against
`https://dl.google.com/android/repository/repository2-3.xml`) into `...\Sdk\cmdline-tools\latest\` and re-run the script.

The script's commands, if you want them by hand (newer `sdkmanager` versions use `/` in package names; older ones use `;`):

```powershell
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-17.0.21.21-hotspot"   # adjust to your JDK folder
$sm = "$env:LOCALAPPDATA\Android\Sdk\cmdline-tools\latest\bin\sdkmanager.bat"
& $sm --licenses          # read and accept the Android SDK licenses
& $sm platform-tools build-tools/35.0.1 platforms/android-35
```

### 2.3 Point Godot at the SDK and JDK

Editor → **Editor Settings ▸ Export ▸ Android**:
- *Java SDK Path*: the JDK folder above.
- *Android SDK Path*: `%LOCALAPPDATA%\Android\Sdk` (Godot finds this one by itself).
- *Debug Keystore*: Godot 4.7 generates one on first export if the field points at a missing file.
  To make your own: `keytool -genkeypair -keyalg RSA -keysize 2048 -validity 10000 -alias androiddebugkey -keypass android -storepass android -keystore debug.keystore -dname "CN=Android Debug,O=Android,C=US"`.
  These are the well-known Android debug credentials, not secrets. **Release keystores never go into the repo**
  (`*.keystore`/`*.jks` belong to whoever publishes; pass them via `GODOT_ANDROID_KEYSTORE_RELEASE_*` env vars in CI).

These settings live in `%APPDATA%\Godot\editor_settings-4.7.tres`, not in the project.

## 3. Prepare the phone (Android)

1. Settings ▸ About phone ▸ tap **Build number** 7 times → Developer options are enabled.
2. Settings ▸ System ▸ Developer options ▸ **USB debugging** on.
3. Connect by USB, accept the "Allow USB debugging?" prompt on the phone.
4. Check: `"$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" devices` lists the phone as `device`
   (not `unauthorized`).
5. The phone and the PC must be on the same Wi-Fi/LAN for the echo-server test.

## 4. Export and install on Android

The project has two presets in `export_presets.cfg`: **Android arm64 (debug)** and **iOS (Xcode project)**.

From the command line (from `spikes/mobile-export/godot`):

```powershell
$godot = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "Godot_v4.7.2-stable_mono_win64_console.exe").FullName
& $godot --headless --path . --import
& $godot --headless --path . --export-debug "Android arm64 (debug)" ../builds/android/mobile-spike.apk
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
& $adb install -r ../builds/android/mobile-spike.apk
& $adb shell monkey -p dev.mmorpgtemplate.spike 1     # launch it
```

Or in the editor: **Project ▸ Export ▸ Android arm64 (debug) ▸ Export Project**, or the
one-click **Remote Debug / Deploy** button (top right) once the phone is connected.

Gotchas found while exporting (Godot 4.7.2):
- **Android needs `net9.0`.** The prebuilt Android template refuses a `net8.0` project
  ("export template only supports 'net9.0'"). The csproj sets
  `<TargetFramework Condition=" '$(GodotTargetPlatform)' == 'android' ">net9.0</TargetFramework>`;
  desktop and iOS stay on net8.0.
- **A `.sln` must sit next to the `.csproj`** (`MobileSpike.sln`), or the C# part is silently left out
  and the export only "completes with warnings". Create it with `dotnet new sln --format sln`
  (the .NET 10 default `.slnx` is not picked up).
- **The command-line export does not exit.** It writes the `.apk` within ~2 minutes (the first run
  also downloads the .NET Android runtime pack), logs `EditorSettings not instantiated yet …
  shutdown_adb_on_exit`, and then hangs. Wait until the `.apk` and `.apk.idsig` exist, then close it.
  (Only the command-line export was tried.)

Logs: `& $adb logcat -s godot:V` (the spike prefixes its lines with `[spike]`).

## 5. Build and install on iOS (macOS only)

Godot exports C# projects to iOS **only from macOS**: the C# code is compiled with .NET NativeAOT
(`PublishAot`, trimmed) and linked with Xcode's toolchain. There is no Windows route.

1. On the Mac: install Xcode from the App Store, open it once, accept its license, and install the
   iOS platform it offers. Then `xcode-select -p` must print `/Applications/Xcode.app/Contents/Developer`.
2. Install the .NET 10 SDK (<https://dotnet.microsoft.com/download>) and Godot 4.7.2 .NET for macOS
   (`Godot_v4.7.2-stable_mono_macos.universal.zip` from the GitHub release), then the export templates as in §1.
3. Clone the repo on the Mac and open `spikes/mobile-export/godot` in Godot.
4. Xcode ▸ Settings ▸ Accounts: add your Apple ID. Note the **Team ID** (10 characters).
5. Godot ▸ Project ▸ Export ▸ **iOS (Xcode project)**: set *App Store Team ID*; change
   *Bundle Identifier* if `dev.mmorpgtemplate.spike` is taken for a free account.
   Export to `../builds/ios/MobileSpike.ipa` — with *Export Project Only* on, this writes
   `MobileSpike.xcodeproj` next to it.
6. Open `MobileSpike.xcodeproj` in Xcode, select the target ▸ **Signing & Capabilities** ▸ check
   *Automatically manage signing*, pick your team.
7. Connect the iPhone by USB, trust the Mac, enable **Developer Mode** on the phone
   (Settings ▸ Privacy & Security ▸ Developer Mode, iOS 16+). Select it as the run destination and press **Run**.
8. Free Apple IDs: the first launch fails until you trust the developer under
   Settings ▸ General ▸ VPN & Device Management. The build expires after 7 days.

Logs: Xcode's debug console, or Console.app filtered on the device.

## 6. Running the spike measurements

### 6.1 Echo server (on the PC)

```powershell
cd spikes/mobile-export/echo-server
dotnet run
```

It listens on **8080** (`ws://`) and **8443** (`wss://`) on all interfaces and prints connection
open/close lines with timestamps. Windows asks to allow it through the firewall the first time:
allow **Private networks**. Find the PC's LAN IP with `ipconfig` (IPv4 of the Wi-Fi adapter).

**TLS for `wss://`:** on first start the server creates a self-signed certificate for the name
`echo.spike.test` (private key in `echo-server/certs/`, gitignored) and writes its *public* certificate to
`godot/certs/echo-dev.crt`. The export includes that file and the client trusts exactly it, with
the expected name overridden to `echo.spike.test` — so the phone can connect by IP and verification
still happens. **Start the echo server once before exporting**, otherwise the app falls back to an
unverified TLS connection and shows `TLS UNVERIFIED` on screen. If you delete `echo-server/certs/`,
re-export the app.

### 6.2 On the phone

The app opens straight into the test scene. Left: stats and an event log. Right: buttons.

| Control | What it does |
|---------|--------------|
| Shadows | Off → PlayerOnly (§9 default: one character casts) → All (every character and prop) |
| FPS cap | 30 → 60 → off (vsync off, shows headroom) |
| Characters | 40 → 60 → 20 → 0 |
| LOD | Near8 (§9 worst case: the 8 nearest at Full, 4,968 tris; the rest at Lod1, 1,932 tris) → AllFull → AllLod1 (comparison runs) |
| Renderer (restart) | Switches Mobile ↔ Compatibility **and quits**. Start the app again; the stats line shows which renderer is active |
| host field + ws / wss / close | Connect to the echo server at that LAN IP |
| Auto-reconnect | Off: record raw socket behaviour. On: reconnect after a drop that follows a resume, and log how long it took |
| Copy report | Copies the full report (device, GPU, stats, events) to the clipboard and prints it to logcat |

Tap the ground: a yellow marker appears on the tapped cell and the stats show `cell (x, z)`.
Pinch to zoom.

### 6.3 What to record (per device, per renderer)

1. Let the scene settle 15 s (first frames compile shaders). With **40 chars / LOD Near8 / PlayerOnly / cap 30**,
   note `avg10s`, `worst`, `>34ms` count, draw calls, triangles, then **Copy report**.
2. Repeat with Shadows **Off** and **All**, and with LOD **AllFull** and **AllLod1**. Check the `Game.Shared`
   lines of the report: formula `OK` and pcg32 `OK` (real Shared code ran on the device).
3. Set the FPS cap to **off** and note `avg10s` (headroom).
4. **Memory (the §9 number):** while the scene runs,
   `& $adb shell dumpsys meminfo dev.mmorpgtemplate.spike` → **TOTAL PSS** line. The on-screen
   `engine/VRAM/managed` figures are Godot's own counters and are not the app's real footprint.
5. **Install size:** `& $adb shell pm path dev.mmorpgtemplate.spike`, then the APK size on disk, and
   Settings ▸ Apps ▸ T-005 Spike ▸ Storage.
6. **Network:** connect `ws`, wait 30 s, Copy report (RTT p50/p95). Then `wss`, same.
   Note: RTT is measured from the frame loop, so it includes up to one frame (33 ms at 30 fps) of
   polling delay — the same delay the real client will have.
7. **Backgrounding:** connected over `wss` with Auto-reconnect **off**: press Home, wait **30 s**
   (use a stopwatch), return. Copy report. The event log shows `PAUSED`, `RESUMED after N s`, the socket
   state before and after the first poll, and either `first echo after resume` or a close code.
   Also copy the echo server's console lines for that connection (`GAP`, `CLOSE`).
   Repeat with Auto-reconnect **on**, and once with a 2-minute background.
8. Switch the renderer and repeat 1–4 and 7.

Paste the reports and server lines into the T-005 task thread; they go into ADR-001 "Spike results".

### 6.4 Desktop smoke run (no phone)

```powershell
& $godot --path . -- --autoreport --connect wss://127.0.0.1:8443/echo
```

Runs the scene for 12 s, prints the report, saves a screenshot to `user://autoreport.png`, and quits.
Desktop numbers are only a sanity check; §9 budgets are judged on phones.

## 7. Platform notes found by the spike

- **Cleartext `ws://` is not expected to be blocked by the OS** (confirm on device in §6.3 step 6).
  Godot's WebSocket uses native sockets, which Android's network-security config and iOS App Transport
  Security do not police (they cover the Java / `NSURLSession` HTTP stacks). "`ws://` only in debug builds" (ARCHITECTURE §10) is therefore enforced by
  the client itself: it refuses non-`wss` URLs unless `OS.IsDebugBuild()` (T-216, §8.4).
- **Trusting a dev certificate:** `TlsOptions.Client(cert, "echo.spike.test")` pins one certificate
  and fixes the expected name. Production uses a public CA certificate and `TlsOptions.Client()`.
- **Renderer choice** is a project setting read at startup; the spike switches it through
  `application/config/project_settings_override = "user://override.cfg"`.

## 8. The real client (`/client`, T-206)

Not in `Game.sln`: a plain `dotnet build` at the root needs neither Godot nor this project. `client/Directory.Build.props` and `client/Directory.Packages.props` keep the
root settings away from the Godot SDK. Godot, SDK and framework versions are those in the table at the top. **No phone has run this client yet; §8.3 lists what is unverified.**

### 8.1 Run it on one PC

```powershell
dotnet run --project src/Game.Server                  # terminal 1; dev loopback on ws://127.0.0.1:7780/ws
dotnet build client/GameClient.csproj                 # terminal 2; also packs the bundle (below)
$godot = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "Godot_v4.7.2-stable_mono_win64_console.exe").FullName
& $godot --headless --path client --import            # first time only
& $godot --path client                                # add -- --autologin to skip the login screen
```

Left click or tap: walk, or attack a monster. Scroll wheel or pinch: zoom. On a device the server must be reachable, and the dev login needs the Development environment.

**Settings:** `client/client_settings.json` (server URL, dev name, frame cap, camera; every key is in `client/Core/ClientConfig.cs`), then the command line after `--`:
`--server ws://host:port/ws`, `--name Ada`, `--set key=value`. There are no secrets in it. **Release builds refuse `ws://`** (§8.4): the shipped `server_url` is the dev loopback, so a release build needs a `wss://` address first.

**Bundle:** the `PackClientBundle` target writes `client/bundle/client-content.json` from `export-client` when any file under `content/` is newer (gitignored; `-p:SkipClientBundle=true` keeps it as it is).
By hand: `dotnet run --project src/Game.Tools -- export-client client/bundle/client-content.json`. A displayed-content change that is not repacked ends in `bye update_required`, and a
`format_version` the build does not know ends at a refusal screen. Update `content/client-content.reference.json` in the same change as any displayed-content change, or CI's `--check` fails:
`dotnet run --project src/Game.Tools -- export-client content/client-content.reference.json`.

### 8.2 Scripted runs

`client/run-e2e.ps1` builds, starts a real server on its own port, runs the client and stops the server. Exit code 0 means it passed; every step prints an `[e2e]` line.

```powershell
.\client\run-e2e.ps1 -EasyMonsters                                     # login, map, walk, kill, drop, dropped socket, resume, warp, logout (headless)
.\client\run-e2e.ps1 -EasyMonsters -Windowed -Screenshot C:\temp\run.png  # the same with the real renderer, plus a real mouse click through the engine's input
.\client\run-e2e.ps1 -Run death -Monster monster.gel_slime              # real content: gets killed, drops the socket while dead, sees the death screen again, respawns
```

`-EasyMonsters` gives the server a temporary content copy with weak, passive monsters (monster stats do not change the bundle hash). It is needed until T-309 lands: on today's
content a new character cannot beat a slime. Taps go through `Main.HandleInput`, the same method the engine's input calls.

Measuring with extra entities: start the client with `-- --measure 40 --server ws://127.0.0.1:<port>/ws --screenshot shot.png` (add `--set fps_cap=0` for no cap, and `--resolution 1560x720` before the `--` for 19.5:9).
It waits for 40 entities on screen, samples 15 s and prints fps, draw calls, triangles, allocations and the visible cells. Bots: `tools/loadtest --bots 50 --fighters 0 --spare-slots 2 --port 7791` keeps two session slots free and a known port, so the client can join the bot-filled server
(`--server ws://127.0.0.1:7791/ws`; tools/loadtest/README.md). Without `--spare-slots` it sets `max-sessions` to exactly its bots.

**Runs** (T-216; every run starts its own server on `-Port`, default 7790, and stops it; each prints `[run-e2e] <name> exit code: N`, and the script's exit code is 0 only if all passed):

| `-Run` | What it proves | Server |
|--------|----------------|--------|
| `play` (default) | login, walk, kill, drop, dropped socket, resume, background, logout | real content |
| `warp` | walk to the warp and arrive on the second map | weak-monster copy: on real content, aggressive monsters between the start and the warp kill a new character, which is intended game danger |
| `hill` | tap a point on cell (23, 13) on the sample hill, which the old flat-ground pick read as (23, 12): the server walks the character to exactly (23, 13) (T-220) | weak-monster copy |
| `ui` | the HUD layout fits every screen, notch and UI scale; pinch and wheel zoom clamp; touches on UI never reach the world; long-press shows the info panel without a tap; the hotbar sends nothing; the UI-scale and sound settings are stored (T-211, T-305) | default |
| `resume_failed` | the socket drops, the client's first retry comes after the server's grace period (2 s vs 5 s): the resume gets `bye resume_failed`, the client goes to the login screen and does not retry | `--net-grace-period-ms 2000` |
| `replaced` | a second connection resumes the character with the client's token: the client gets `bye replaced`, stops, and does not reconnect | default |
| `shutdown` | the server stops itself (`--duration 20`): `bye shutdown`, the retry notice, token dropped; the script starts a second server and the client logs in again by itself | default, twice |
| `grace_expiry` | the script kills the server: the client resumes with backoff until its own deadline (last heard + 5 s + 1 s), then `SessionExpired` and the login screen | default, killed |
| `bye` | the four runs above | |
| `release` | exports a desktop **release** build (Windows, or Linux on CI), runs it with `ws://` and `--set allow_cleartext=true` (refused, refusal screen, exit 0), then with `wss://` (passes the address check). The old export is deleted first; a non-zero export exit code, or Godot not exiting 60 s after writing every file (`EXPORT HUNG AT EXIT`), fails the run | none |
| `all` | `play`, `warp`, `hill`, `ui`, the four bye runs and `release` | |
| `death` | killed on real content, drops the socket while dead, death screen again, respawn (`-Monster monster.gel_slime`; up to 5 minutes, not in `all`) | real content |

`-EasyMonsters` runs the whole `play` run, warp included, on the weak-monster copy, as T-206 did. The scripts find Godot with `-Godot`, then `$env:GODOT`, then the winget install, and run in Windows PowerShell 5.1 and PowerShell 7.
Godot's stderr is never treated as failure (it prints an error line even on a good exit); success is the exit code.

All four bye behaviours matched ARCHITECTURE §10; none differed. Timings of the first run (this PC, headless): `resume_failed` 5.2 s from the drop to the bye; `shutdown` 19.0 s to the bye and 22.2 s to being online again
(3 s retry setting); `grace_expiry` noticed the dead server after 0.7 s and gave up at 6.1 s. The client's deadline counts from the last message heard, so `grace_expiry` pings every second to keep "last heard" fresh under its short 5 s + 1 s settings (with the shipped 60 s + 30 s the deadline is far off).

Stale-hash and refusal screens: `--bundle <file>` (debug builds only) reads another bundle file, and `--exit-on-refusal` quits after the refusal screen.

### 8.4 Themes

`client/mount-themes.ps1` makes `client/themes` a junction to `themes/` (`-Copy` for a real copy; gitignored; `run-e2e.ps1` runs it and re-imports). Regenerate the placeholder pack with
`dotnet run --project tools/themegen`. Godot writes `*.import` files beside each asset (gitignored). Switch theme with `--set theme=test`. An export needs
`themes/*/asset-manifest.json` in the preset's `include_filter` (it is not a Godot resource); `export_presets.cfg` belongs to T-216. Every preset's `exclude_filter` keeps `addons/*` and `themes/*/maps/*.authoring.tscn` out: they hold spawn data and editor tools (T-208).

### 8.3 Export

`export_presets.cfg` is committed for `/client` (no credentials in it); its `include_filter` carries the bundle and the settings, which are not Godot resources.

```powershell
& $godot --headless --path client --export-debug "Android arm64 (debug)" exports/android/game-client.apk
```

The path is relative to `client`, and `client/exports/` is gitignored. §4's gotchas apply. The export does not exit by itself, so use the script, which waits for `.apk` and `.apk.idsig`, stops Godot and fails after `-TimeoutSeconds`:

```powershell
.\client\export-android.ps1            # debug preset -> client/exports/android/game-client.apk
.\client\export-android.ps1 -Release   # release preset; signed with GODOT_ANDROID_KEYSTORE_RELEASE_{PATH,USER,PASSWORD} (the debug keystore is fine to measure size)
```

**What the APK carries (T-216).** The presets are `Android arm64 (debug)`, `Android arm64 (release)`, `iOS (Xcode project)`, and two desktop presets used only by `-Run release` (`Windows Desktop (release check)`, `Linux (release check)`).
The Android ones export `arm64-v8a` only (§7.1) and no other architecture.

| | Before (T-206) | After (T-216) |
|---|---|---|
| Debug APK | 104.2 MB (178.5 MB uncompressed) | **44.3 MB (118.2 MB uncompressed)** |
| Release APK (debug key) | 102.1 MB (T-005) | 42.5 MB |

Removed: the 13 Mono static archives `lib/arm64-v8a/*.a` (58 MB uncompressed: `libmonosgen-2.0.a` 44 MB, `libSystem.IO.Compression.Native.a` 4.6 MB, `libmono-component-diagnostics_tracing-static.a` 4.2 MB, and the debugger, hot-reload,
marshal-ilgen, `System.Native`, `System.Security.Cryptography.Native.Android`, `System.Globalization.Native` archives and their `-stub-static` variants) and `Game.Shared.xml` (documentation, 0.4 MB). The `StripUnusedAndroidFiles` target in
`client/GameClient.csproj` does it at publish time. Android loads only the `.so` files from `lib/`, and only NativeAOT (iOS) links statically, so these were dead weight. **Kept:** every `.so` (`libgodot_android.so` 76 MB, `libmonosgen-2.0.so`, the
`System.*.Native.so`, `libc++_shared.so`, the mono components), all framework assemblies, `Game.Shared.dll`, `GameClient.dll`, the `.pdb` files, `assets/bundle/client-content.json` and `assets/client_settings.json`. Still in the 118 MB:
the engine library 76 MB, the framework assemblies about 30 MB uncompressed, `classes.dex` 5.7 MB. Not done, because it can only be judged on a device (ADR-002 row 112): trimming the framework assemblies and the mono component `.so` files
(debugger, diagnostics). A custom Godot export template without unused modules would cut the engine library, but is a build of Godot itself.

**Unverified until a phone runs it** (ADR-002 row 112): everything in T-005's §6.3, plus this client's touch input and pinch, the 48 dp picking at a real density, ARCHITECTURE §9's numbers (fps, draw calls, memory, thermal), the
safe area, `ws://` and `wss://` from the device, real pause and resume with the socket's behaviour across a suspension, and all of iOS (NativeAOT, trimming, `System.Text.Json`). T-216 adds: that the stripped APK installs and starts
(only the file contents were checked, on a PC), and the iOS ATS keys.

### 8.4 Release builds refuse `ws://`

ARCHITECTURE §10 allows cleartext `ws://` only in debug builds. The OS does not police Godot's WebSocket (§7), so the client does: `client/Core/Net/TransportPolicy.cs` is a pure function, `ClientConfig.AllowCleartext` is false unless
`Main._Ready` sets it from `OS.IsDebugBuild()` (the only place, checked by a test), and the setting has no `client_settings.json` key and no command-line switch. A release build with a `ws://` (or invalid) `server_url` shows a refusal screen and does not
open a socket; `ClientSession` checks again before opening, as a second lock. `-Run release` shows it on a real release export (§8.2). Debug builds are the opposite and every other run proves it (`debug build, cleartext allowed`).

**Android network-security config and iOS ATS.** Neither affects this client's sockets (§7), and Android's platform default (target SDK 35, no `usesCleartextTraffic`) already denies cleartext to the Java stacks. A custom
`network_security_config` needs the Gradle export path (`gradle_build/use_gradle_build`, a custom build template with its own manifest), which this project does not use; it would add Gradle and a template to every build for no effect on the WebSocket.
So none is shipped. The iOS preset sets `NSAppTransportSecurity` explicitly (`NSAllowsArbitraryLoads` and `NSAllowsLocalNetworking` false) through `application/additional_plist_content`; that is unverified until a Mac exports it.

## 9. CI export job (T-216)

`.github/workflows/ci.yml` has a second job, `client-export`, separate from the server checks (`build-test`) so that a slow export does not hold them up. On `ubuntu-latest`, with a 60 minute job limit and shorter limits per step, it:
installs the pinned JDK 17, Android SDK packages (platform android-35, build-tools 35.0.1) and Godot 4.7.2 .NET with the Android and Linux export templates (downloaded from the Godot release, checked against the release's `SHA512-SUMS.txt`, cached by version);
builds the client; exports the Android debug APK with `export-android.ps1`; fails if the APK is over 60 MB, lacks the bundle or settings, contains static archives or another architecture; keeps the APK as an artifact for 3 days; and runs `run-e2e.ps1 -Run all`.
The versions are in the job's `env`, next to a reminder to change them here and there together. **It has never run on GitHub** (the owner has not pushed), so the first run may need fixes; it was checked locally for YAML syntax only (`actionlint` is not installed).
What was run locally is the same scripts on Windows.

## 10. Framework: .NET 10

Checked with the pinned Godot 4.7.2: the client cannot move to net10.0. With `net10.0` for Android the export stops with `C# project targets 'net10.0' but the export template only supports 'net9.0'. Consider using gradle builds instead.`
Nothing was changed (ADR-002 row 63 stands: check at every Godot upgrade). The Gradle export path might accept it, but it is not used here and was not tried.
