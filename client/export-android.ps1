# Exports the client as an Android APK from the command line (docs/MOBILE_BUILD.md §4, §8.3), working around Godot 4.7.2's export, which writes the
# APK and then never exits: it waits for the file to be complete, stops Godot, and checks the result. Exit code 0 means a complete APK exists.
#   .\client\export-android.ps1                                   the debug preset to client/exports/android/game-client.apk
#   .\client\export-android.ps1 -Release                          the release preset (signed with the debug key unless GODOT_ANDROID_KEYSTORE_RELEASE_* are set)
param(
    [string] $Output = "",            # relative to client/ unless absolute; default: the preset's own export_path
    [switch] $Release,
    [string] $Godot = "",             # the Godot .NET console binary; default: $env:GODOT, then the winget install
    [int] $TimeoutSeconds = 900,      # hard stop for the whole export
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
Import-GodotProject $Godot $client

$flag = if ($Release) { "--export-release" } else { "--export-debug" }
$log = Join-Path ([IO.Path]::GetTempPath()) ("t216-export-" + [guid]::NewGuid().ToString("N") + ".log")
$process = Start-Process $Godot -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err" `
    -ArgumentList @("--headless", "--path", "`"$client`"", $flag, "`"$preset`"", "`"$apk`"")
$null = $process.Handle   # keeps the exit code readable after the process ends
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastSize = -1
    $stableSince = $null
    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($process.HasExited) { break }
        if ((Test-Path $apk) -and (Test-Path "$apk.idsig")) {
            $size = (Get-Item $apk).Length
            if ($size -eq $lastSize) {
                if (-not $stableSince) { $stableSince = Get-Date }
                elseif (((Get-Date) - $stableSince).TotalSeconds -ge 3) { break }
            } else { $lastSize = $size; $stableSince = $null }
        }
        Start-Sleep -Milliseconds 500
    }
    $finished = (Test-Path $apk) -and (Test-Path "$apk.idsig")
    if (-not $finished) {
        Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
        if ($process.HasExited) { throw "Godot exited with code $($process.ExitCode) without writing a complete APK." }
        throw "No complete APK after $TimeoutSeconds s."
    }
    Write-Output ("[export] {0}: {1:N1} MB in {2:N0} s" -f $apk, ((Get-Item $apk).Length / 1MB), $clock.Elapsed.TotalSeconds)
    Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Where-Object { $_ -match "ERROR|WARNING" } | Select-Object -First 20
}
finally {
    if (-not $process.HasExited) {
        if ($IsLinux -or $IsMacOS) { & kill -9 $process.Id 2>$null } else { & taskkill /PID $process.Id /T /F *>$null }
    }
    Remove-Item $log, "$log.err" -ErrorAction SilentlyContinue
}
