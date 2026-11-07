# Finds the Godot 4.7.2 .NET console binary for the scripts in this folder (dot-source it): the argument, then $env:GODOT, then the winget install.
function Resolve-Godot([string] $Given) {
    if ($Given) { return $Given }
    if ($env:GODOT) { return $env:GODOT }
    $found = $null
    if ($env:LOCALAPPDATA) {
        $found = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "Godot_v4.7.2-stable_mono_win64_console.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $found) { throw "Godot 4.7.2 .NET is not installed: pass -Godot, set `$env:GODOT, or follow docs/MOBILE_BUILD.md §1." }
    return $found.FullName
}

# Imports the project's resources the first time (no .godot cache yet). Godot writes to stderr even on success, so it runs apart from the script's error handling.
function Import-GodotProject([string] $Godot, [string] $Client) {
    if (Test-Path (Join-Path $Client ".godot/uid_cache.bin")) { return }
    $null = Start-Process $Godot -Wait -NoNewWindow -RedirectStandardOutput ([IO.Path]::GetTempFileName()) -RedirectStandardError ([IO.Path]::GetTempFileName()) -ArgumentList @("--headless", "--path", $Client, "--import")
}
