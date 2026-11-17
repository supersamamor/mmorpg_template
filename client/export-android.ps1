# Exports the client as an Android APK from the command line (docs/MOBILE_BUILD.md §4, §8.3, §8.5) through Invoke-GodotStep (godot-path.ps1): bounded and
# retried. It waits for the APK and its .idsig to be complete, then gives Godot -ExitSeconds to exit and otherwise stops it (Godot 4.7.2's console
# wrapper does not exit while the adb server Godot started runs, T-221). Exit code 0 means a complete APK exists.
#   .\client\export-android.ps1                                   the debug preset to client/exports/android/game-client.apk
#   .\client\export-android.ps1 -Release                          the release preset (signed with the debug key unless GODOT_ANDROID_KEYSTORE_RELEASE_* are set)
param(
    [string] $Output = "",            # relative to client/ unless absolute; default: the preset's own export_path
    [switch] $Release,
    [string] $Godot = "",             # the Godot .NET console binary; default: $env:GODOT, then the winget install
    [int] $TimeoutSeconds = 900,      # one export attempt; 2 attempts in all
    [int] $ExitSeconds = 30,          # from the APK and .idsig written (and no longer growing) to Godot's exit; after that Godot is stopped, the APK kept
    [int] $ImportTimeoutSeconds = 180,
    [switch] $SkipBuild               # keep client/bundle/client-content.json and the C# build as they are
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root "client"
. (Join-Path $PSScriptRoot "godot-path.ps1")
$Godot = Resolve-Godot $Godot

$preset = if ($Release) { "Android arm64 (release)" } else { "Android arm64 (debug)" }
if (-not $Output) { $Output = if ($Release) { "exports/android/game-client-release.apk" } else { "exports/android/game-client.apk" } }
$apk = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $client $Output }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $apk) | Out-Null
Remove-Item $apk, "$apk.idsig" -ErrorAction SilentlyContinue

if (-not $SkipBuild) {
    dotnet build (Join-Path $client "GameClient.csproj") -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The client does not build." }
}
& (Join-Path $client "mount-themes.ps1") -Copy | Out-Null   # a real copy: what an export packs (ARCHITECTURE §7.7)
Import-GodotProject $Godot $client -Force -TimeoutSeconds $ImportTimeoutSeconds

$flag = if ($Release) { "--export-release" } else { "--export-debug" }
$state = @{ Since = [datetime]::UtcNow }
$complete = {
    $files = @(Get-Item $apk, "$apk.idsig" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTimeUtc -ge $state.Since })
    if ($files.Count -lt 2) { return $null }
    return (($files | ForEach-Object Length) -join ",")
}
$export = Invoke-GodotStep -Godot $Godot -Step "android export" -Arguments @("--headless", "--path", "`"$client`"", $flag, "`"$preset`"", "`"$apk`"") `
    -TimeoutSeconds $TimeoutSeconds -ExitSeconds $ExitSeconds -StopWhenComplete -Attempts 2 -Output $complete `
    -BeforeAttempt { Remove-Item $apk, "$apk.idsig" -ErrorAction SilentlyContinue; $state.Since = [datetime]::UtcNow.AddSeconds(-1) }
if (-not $export.Ok) {
    $export.Output | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
    throw "No complete APK: $($export.Outcome)."
}
Write-Output ("[export] {0}: {1:N1} MB in {2:N0} s" -f $apk, ((Get-Item $apk).Length / 1MB), $export.Seconds)
$export.Output | Where-Object { $_ -match "ERROR|WARNING" } | Select-Object -First 20
