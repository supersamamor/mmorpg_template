# The headless map export (ARCHITECTURE §5.3, docs/MAP_AUTHORING.md): bakes authoring scenes and writes content/maps/<name>.grid.json and the minimap picture
# themes/default/maps/<name>.minimap.png (T-812). Exit code 0 means every map exported with no problem.
#   .\client\export-maps.ps1                       every map
#   .\client\export-maps.ps1 -Map starter_meadow   one map
#   .\client\export-maps.ps1 -Check                writes nothing: fails if a fresh export differs from a byte of the committed grids or minimap pictures, or a picture is missing (needs Godot, unlike `validate`)
#   .\client\export-maps.ps1 -Content C:\temp\c    writes to <dir>/maps instead of content/maps
param(
    [string] $Map = "",
    [switch] $Check,
    [string] $Content = "",
    [string] $Godot = "",            # the Godot .NET console binary; default: $env:GODOT, then the winget install
    [int] $TimeoutSeconds = 300,     # one map export attempt (every map takes about 10 s here); a hang is retried, 3 attempts in all (T-221)
    [int] $ImportTimeoutSeconds = 180
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root "client"
. (Join-Path $PSScriptRoot "godot-path.ps1")
$godotPath = Resolve-Godot $Godot

dotnet build (Join-Path $client "GameClient.csproj") -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The client does not build." }
& (Join-Path $client "mount-themes.ps1") | Out-Null
Import-GodotProject $godotPath $client -Force -TimeoutSeconds $ImportTimeoutSeconds

$temp = $null
if ($Check) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("mapcheck-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    $Content = $temp
}

$arguments = @("--headless", "--path", "`"$client`"", "res://addons/map_authoring/ExportRunner.tscn", "--")
$arguments += $(if ($Map) { @("--map", $Map) } else { @("--all") })
if ($Content) { $arguments += @("--content", "`"$Content`"") }
if ($Check) { $arguments += @("--pictures", "`"$(Join-Path $temp "pictures")`"") }

# Bounded and retried (godot-path.ps1): a real map problem exits 1 and is not retried; a hang is stopped and retried.
$export = Invoke-GodotStep -Godot $godotPath -Step "map export" -Arguments $arguments -TimeoutSeconds $TimeoutSeconds
$code = $export.Code
$export.Output | Where-Object { $_ -match "^\[map-export\]|Unhandled" } | ForEach-Object { Write-Host $_ }

if ($Check -and $code -eq 0) {
    foreach ($file in Get-ChildItem (Join-Path $temp "maps") -Filter *.grid.json) {
        $committed = Join-Path $root ("content/maps/" + $file.Name)
        $same = (Test-Path $committed) -and ((Get-FileHash $committed).Hash -eq (Get-FileHash $file.FullName).Hash)
        if (-not $same) { Write-Host "[map-export] differs from the committed grid: $($file.Name)"; $code = 1 }
        # T-812: the minimap picture made from the same grid must be committed, with the same bytes.
        $stem = $file.Name.Substring(0, $file.Name.Length - ".grid.json".Length)
        $picture = Join-Path $temp ("pictures/" + $stem + ".minimap.png")
        $committedPicture = Join-Path $root ("themes/default/maps/" + $stem + ".minimap.png")
        if (-not (Test-Path $committedPicture)) { Write-Host "[map-export] the minimap picture is missing: themes/default/maps/$stem.minimap.png (export the map again)"; $code = 1 }
        elseif (-not (Test-Path $picture) -or (Get-FileHash $committedPicture).Hash -ne (Get-FileHash $picture).Hash) { Write-Host "[map-export] the minimap picture is stale: themes/default/maps/$stem.minimap.png (export the map again)"; $code = 1 }
    }
    if ($code -eq 0) { Write-Host "[map-export] every committed grid and minimap picture matches a fresh export, byte for byte" }
}
if ($temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
exit $code
