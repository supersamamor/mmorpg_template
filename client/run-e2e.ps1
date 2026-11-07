# Scripted client runs against a real local server (docs/MOBILE_BUILD.md §8). Exit code 0 means the run passed.
#   .\client\run-e2e.ps1                                  login, map, walk, kill, drop, dropped socket, resume, warp, logout (headless)
#   .\client\run-e2e.ps1 -Run death -Monster monster.x    gets killed, drops the socket while dead, sees the death screen again, respawns
#   .\client\run-e2e.ps1 -Windowed -Screenshot shot.png   the same with the real renderer, saving a picture at the drop
#   .\client\run-e2e.ps1 -EasyMonsters                    the server plays a temporary content copy with weak, passive monsters
param(
    [ValidateSet("play", "death")] [string] $Run = "play",
    [string] $Monster = "",          # the monster to hunt (a content id, so it is an argument); empty means the nearest
    [switch] $Windowed,              # real renderer instead of --headless
    [string] $Screenshot = "",
    [int] $Port = 7790,
    [int] $Seed = 206,
    [switch] $EasyMonsters,          # a temporary content copy where a new character wins every fight (monster stats do not change the bundle hash)
    [string] $Resolution = "1280x720",
    [string[]] $ClientArgs = @()     # more arguments for the client, e.g. --set grace_seconds=30
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root "client"
$godot = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "Godot_v4.7.2-stable_mono_win64_console.exe" | Select-Object -First 1).FullName
if (-not $godot) { throw "Godot 4.7.2 .NET is not installed (docs/MOBILE_BUILD.md §1)." }

dotnet build (Join-Path $root "src/Game.Server") -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The server does not build." }
dotnet build (Join-Path $client "GameClient.csproj") -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The client does not build." }
& (Join-Path $client "mount-themes.ps1") | Out-Null   # res://themes (ARCHITECTURE §7.7)
$ErrorActionPreference = "Continue"   # the editor logs a harmless 'EditorSettings not instantiated' error on every import
& $godot --headless --path $client --import *>&1 | Out-Null   # picks up new or changed theme files; quick when nothing changed
$ErrorActionPreference = "Stop"

$log = New-TemporaryFile
$serverArgs = @("run", "--no-build", "--project", "src/Game.Server", "--", "--seed", "$Seed", "--net-port", "$Port")
$temp = $null
if ($EasyMonsters) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("t206-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $temp | Out-Null
    Copy-Item (Join-Path $root "content") (Join-Path $temp "content") -Recurse
    Copy-Item (Join-Path $root "themes") (Join-Path $temp "themes") -Recurse   # the validator looks for it next to the content
    foreach ($file in Get-ChildItem (Join-Path $temp "content/monsters") -Filter *.yaml) {
        $text = [IO.File]::ReadAllText($file.FullName)
        $text = $text -replace 'max_hp: \d+', 'max_hp: 12' -replace '\batk: \d+', 'atk: 1' -replace 'aggressive: true', 'aggressive: false'
        [IO.File]::WriteAllText($file.FullName, $text)
    }
    $serverArgs = @("run", "--no-build", "--project", "src/Game.Server", "--", (Join-Path $temp "content"), "--seed", "$Seed", "--net-port", "$Port")
}
$server = Start-Process dotnet -PassThru -NoNewWindow -WorkingDirectory $root -RedirectStandardOutput $log -ArgumentList $serverArgs
try {
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Select-String -Path $log -Pattern "Gateway listening" -Quiet)) {
        if ((Get-Date) -gt $deadline -or $server.HasExited) { Get-Content $log; throw "The server did not start." }
        Start-Sleep -Milliseconds 200
    }
    (Select-String -Path $log -Pattern "Content hash:").Line

    $arguments = @("--path", $client, "--resolution", $Resolution)
    if (-not $Windowed) { $arguments += "--headless" }
    $arguments += "--"
    $arguments += $(if ($Run -eq "death") { "--e2e-death" } else { "--e2e" })
    $arguments += @("--server", "ws://127.0.0.1:$Port/ws", "--name", "e2e")
    if ($Monster) { $arguments += @("--e2e-monster", $Monster) }
    if ($Screenshot) { $arguments += @("--screenshot", $Screenshot) }
    $arguments += $ClientArgs

    $ErrorActionPreference = "Continue"   # engine ERROR lines are printed below, not turned into a script failure; the exit code decides
    & $godot @arguments 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match "^\[(e2e|client)\]|^\[theme\]|WARNING: \[theme\]|ERROR|Unhandled" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    "client exit code: $code"
    exit $code
}
finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "ParentProcessId = $($server.Id)" -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item $log -ErrorAction SilentlyContinue
    if ($temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
