# Mounts the repository's themes/ into the Godot project at res://themes (ARCHITECTURE §7.7, docs/MOBILE_BUILD.md §8.4).
#   .\client\mount-themes.ps1          directory junction client\themes -> themes (development: edits show at once)
#   .\client\mount-themes.ps1 -Copy    a real copy instead (what an export packs; use it when junctions are not available)
# client\themes is gitignored. Run it once per checkout; run-e2e.ps1 runs it for you.
param(
    [switch] $Copy
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "themes"
$mount = Join-Path $PSScriptRoot "themes"
if (-not (Test-Path $source)) { throw "No themes folder at $source." }

$item = Get-Item $mount -ErrorAction SilentlyContinue
if ($item -and -not $Copy -and $item.LinkType -eq "Junction" -and (@($item.Target) -contains $source)) {
    "themes already mounted (junction)"
    return
}
if ($item) {
    if ($item.LinkType) { $item.Delete() } else { Remove-Item $mount -Recurse -Force }
}
# A real copy leaves out themes generated for a test or a measurement (T-711's textured one): they ignore themselves in Git (a .gitignore of their own) and are never shipped.
function Copy-Themes {
    New-Item -ItemType Directory -Path $mount | Out-Null
    foreach ($theme in Get-ChildItem $source) {
        if ($theme.PSIsContainer -and (Test-Path (Join-Path $theme.FullName ".gitignore"))) { continue }
        Copy-Item $theme.FullName (Join-Path $mount $theme.Name) -Recurse
    }
}
if ($Copy) {
    Copy-Themes
    "themes copied to $mount"
    return
}
try {
    New-Item -ItemType Junction -Path $mount -Target $source | Out-Null
    "themes mounted: $mount -> $source"
}
catch {
    Write-Warning "Could not make a junction ($($_.Exception.Message)); copying instead."
    Copy-Themes
    "themes copied to $mount"
}
