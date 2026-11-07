# The headless map export (ARCHITECTURE §5.3, docs/MAP_AUTHORING.md): bakes authoring scenes and writes content/maps/<name>.grid.json. Exit code 0 means every map exported with no problem.
#   .\client\export-maps.ps1                       every map
#   .\client\export-maps.ps1 -Map starter_meadow   one map
#   .\client\export-maps.ps1 -Check                writes nothing: fails if a fresh export differs from a byte of the committed grids (needs Godot, unlike `validate`)
#   .\client\export-maps.ps1 -Content C:\temp\c    writes to <dir>/maps instead of content/maps
param(
    [string] $Map = "",
    [switch] $Check,
    [string] $Content = "",
    [string] $Godot = ""             # the Godot .NET console binary; default: $env:GODOT, then the winget install
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root "client"
. (Join-Path $PSScriptRoot "godot-path.ps1")
$godotPath = Resolve-Godot $Godot

dotnet build (Join-Path $client "GameClient.csproj") -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The client does not build." }
& (Join-Path $client "mount-themes.ps1") | Out-Null
Import-GodotProject $godotPath $client -Force

$temp = $null
if ($Check) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("mapcheck-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    $Content = $temp
}

$arguments = @("--headless", "--path", "`"$client`"", "res://addons/map_authoring/ExportRunner.tscn", "--")
$arguments += $(if ($Map) { @("--map", $Map) } else { @("--all") })
if ($Content) { $arguments += @("--content", "`"$Content`"") }

# Godot writes to stderr even when all is well, so its output goes to files and success is judged by the exit code.
$log = [IO.Path]::GetTempFileName()
$process = Start-Process $godotPath -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err" -ArgumentList $arguments
$null = $process.Handle
$process.WaitForExit()
$code = $process.ExitCode
Get-Content $log | Where-Object { $_ -match "^\[map-export\]|Unhandled" } | ForEach-Object { Write-Host $_ }
Remove-Item $log, "$log.err" -ErrorAction SilentlyContinue

if ($Check -and $code -eq 0) {
    foreach ($file in Get-ChildItem (Join-Path $temp "maps") -Filter *.grid.json) {
        $committed = Join-Path $root ("content/maps/" + $file.Name)
        $same = (Test-Path $committed) -and ((Get-FileHash $committed).Hash -eq (Get-FileHash $file.FullName).Hash)
        if (-not $same) { Write-Host "[map-export] differs from the committed grid: $($file.Name)"; $code = 1 }
    }
    if ($code -eq 0) { Write-Host "[map-export] every committed grid matches a fresh export, byte for byte" }
}
if ($temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
exit $code
