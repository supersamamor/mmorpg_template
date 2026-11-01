# Mobile build guide (Android + iOS)

How to build the Godot .NET client for phones. Written during spike T-005 against the
throwaway project in `spikes/mobile-export/`; the same steps apply to `/client` from T-206 on.
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
| Char detail | High (~5.0k tris each, the §9 cap) / Low (~3.2k) |
| Renderer (restart) | Switches Mobile ↔ Compatibility **and quits**. Start the app again; the stats line shows which renderer is active |
| host field + ws / wss / close | Connect to the echo server at that LAN IP |
| Auto-reconnect | Off: record raw socket behaviour. On: reconnect after a drop that follows a resume, and log how long it took |
| Copy report | Copies the full report (device, GPU, stats, events) to the clipboard and prints it to logcat |

Tap the ground: a yellow marker appears on the tapped cell and the stats show `cell (x, z)`.
Pinch to zoom.

### 6.3 What to record (per device, per renderer)

1. Let the scene settle 15 s (first frames compile shaders). With **40 chars / High / PlayerOnly / cap 30**,
   note `avg10s`, `worst`, `>34ms` count, draw calls, triangles, then **Copy report**.
2. Repeat with Shadows **Off** and **All**, and with Char detail **Low**.
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
  Security do not police (they cover the Java / `NSURLSession` HTTP stacks). "`ws://` only in debug builds" (ARCHITECTURE §10) must therefore be enforced by
  the client itself, e.g. refuse non-`wss` URLs unless `OS.IsDebugBuild()`.
- **Trusting a dev certificate:** `TlsOptions.Client(cert, "echo.spike.test")` pins one certificate
  and fixes the expected name. Production uses a public CA certificate and `TlsOptions.Client()`.
- **Renderer choice** is a project setting read at startup; the spike switches it through
  `application/config/project_settings_override = "user://override.cfg"`.
