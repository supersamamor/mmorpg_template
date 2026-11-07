# Scripted client runs against a real local server (docs/MOBILE_BUILD.md §8). Exit code 0 means every run passed. Works in Windows PowerShell 5.1 and PowerShell 7 (Linux in CI).
#   .\client\run-e2e.ps1                                  play: login, map, walk, kill, drop, dropped socket, resume, background, logout (real content, headless)
#   .\client\run-e2e.ps1 -Run warp                        the warp to the second map, on a temporary content copy with weak monsters (real content kills a new character on the way)
#   .\client\run-e2e.ps1 -Run bye                         the four live bye runs: resume_failed, replaced, shutdown, grace_expiry (each can be named alone)
#   .\client\run-e2e.ps1 -Run release                     exports a desktop RELEASE build and checks that it refuses ws:// and does not refuse wss://
#   .\client\run-e2e.ps1 -Run all                         play, warp, bye, release
#   .\client\run-e2e.ps1 -Run death -Monster monster.x    gets killed, drops the socket while dead, sees the death screen again, respawns
#   .\client\run-e2e.ps1 -EasyMonsters                    play with the whole warp leg on the weak-monster copy, as T-206 did
#   .\client\run-e2e.ps1 -Windowed -Screenshot shot.png   the same with the real renderer, saving a picture at the drop
param(
    [ValidateSet("play", "death", "warp", "resume_failed", "replaced", "shutdown", "grace_expiry", "bye", "release", "all")] [string] $Run = "play",
    [string] $Monster = "",          # the monster to hunt (a content id, so it is an argument); empty means the nearest
    [switch] $Windowed,              # real renderer instead of --headless
    [string] $Screenshot = "",
    [int] $Port = 7790,
    [int] $Seed = 206,
    [switch] $EasyMonsters,          # play: run the warp leg too, on a temporary content copy where a new character wins every fight
    [string] $Resolution = "1280x720",
    [string[]] $ClientArgs = @(),    # more arguments for the client, e.g. --set grace_seconds=30
    [string] $Godot = "",            # the Godot .NET console binary; default: $env:GODOT, then the winget install
    [int] $Repeat = 1,               # run each scenario this many times and print the count of each exit code (T-218)
    [string] $KeepLogs = ""          # keep each client run's stdout and stderr in this folder
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root "client"
. (Join-Path $PSScriptRoot "godot-path.ps1")
$godot = Resolve-Godot $Godot

dotnet build (Join-Path $root "src/Game.Server") -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The server does not build." }
dotnet build (Join-Path $client "GameClient.csproj") -v q --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The client does not build." }
& (Join-Path $client "mount-themes.ps1") | Out-Null   # res://themes (ARCHITECTURE §7.7)
Import-GodotProject $godot $client -Force

$script:temp = $null

# A temporary content copy where a new character wins every fight (monster stats do not change the bundle hash).
function Get-EasyContent {
    if ($script:temp) { return (Join-Path $script:temp "content") }
    $script:temp = Join-Path ([IO.Path]::GetTempPath()) ("t206-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:temp | Out-Null
    Copy-Item (Join-Path $root "content") (Join-Path $script:temp "content") -Recurse
    Copy-Item (Join-Path $root "themes") (Join-Path $script:temp "themes") -Recurse   # the validator looks for it next to the content
    foreach ($file in Get-ChildItem (Join-Path $script:temp "content/monsters") -Filter *.yaml) {
        $text = [IO.File]::ReadAllText($file.FullName)
        $text = $text -replace 'max_hp: \d+', 'max_hp: 12' -replace '\batk: \d+', 'atk: 1' -replace 'aggressive: true', 'aggressive: false'
        [IO.File]::WriteAllText($file.FullName, $text)
    }
    return (Join-Path $script:temp "content")
}

function Start-GameServer([switch] $Easy, [string[]] $Extra = @()) {
    $log = [IO.Path]::GetTempFileName()
    $arguments = @("run", "--no-build", "--project", "src/Game.Server", "--")
    if ($Easy) { $arguments += (Get-EasyContent) }
    $arguments += @("--seed", "$Seed", "--net-port", "$Port") + $Extra
    $process = Start-Process dotnet -PassThru -NoNewWindow -WorkingDirectory $root -RedirectStandardOutput $log -RedirectStandardError "$log.err" -ArgumentList $arguments
    $null = $process.Handle
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Select-String -Path $log -Pattern "Gateway listening" -Quiet)) {
        if ((Get-Date) -gt $deadline -or $process.HasExited) { Get-Content $log, "$log.err"; throw "The server did not start." }
        Start-Sleep -Milliseconds 200
    }
    Write-Host (Select-String -Path $log -Pattern "Content hash:").Line
    return [pscustomobject]@{ Process = $process; Log = $log }
}

function Stop-GameServer($server) {
    if (-not $server) { return }
    # `dotnet run` starts the server as a child; stop it first.
    if ((Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
        Get-CimInstance Win32_Process -Filter "ParentProcessId = $($server.Process.Id)" -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } else {
        & pkill -KILL -P $server.Process.Id 2>$null
    }
    Stop-Process -Id $server.Process.Id -Force -ErrorAction SilentlyContinue
    Remove-Item $server.Log, "$($server.Log).err" -ErrorAction SilentlyContinue
}

# Starts Godot with the client's arguments (after `--`) and returns the process and its log file. Godot writes to stderr even when all is well,
# so its output goes to files and success is judged by the exit code, never by stderr.
function Start-GodotClient([string[]] $UserArgs, [string] $Binary = $godot, [string[]] $EngineArgs = @()) {
    $log = [IO.Path]::GetTempFileName()
    $arguments = @()
    if ($Binary -eq $godot) { $arguments += @("--path", "`"$client`"") }
    $arguments += $EngineArgs
    if (-not $Windowed) { $arguments += "--headless" }
    $arguments += "--"
    $arguments += $UserArgs
    $process = Start-Process $Binary -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err" -ArgumentList $arguments
    $null = $process.Handle
    return [pscustomobject]@{ Process = $process; Log = $log; Position = 0L }
}

# Prints the client's new [e2e]/[client] lines until it exits (or $TimeoutSeconds passes, which stops it and fails). $OnTick runs about every 0.3 s with
# the lines that arrived; a line "[e2e] @ name" is a request to the script and is passed on as $OnMarker.
function Wait-GodotClient($run, [int] $TimeoutSeconds, [scriptblock] $OnMarker = $null, [scriptblock] $OnTick = $null) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $text = New-Object Text.StringBuilder
    while ($true) {
        $exited = $run.Process.HasExited
        $stream = [IO.File]::Open($run.Log, "Open", "Read", "ReadWrite")
        try {
            $null = $stream.Seek($run.Position, "Begin")
            $reader = New-Object IO.StreamReader($stream)
            $chunk = $reader.ReadToEnd()
            $run.Position = $stream.Position
        } finally { $stream.Dispose() }
        foreach ($line in ($chunk -split "`r?`n")) {
            if ($line -match "^\[(e2e|client)\]|ERROR|Unhandled") {
                if ($line -match "EditorSettings not instantiated") { continue }   # Godot 4.7.2's harmless line at exit
                Write-Host $line
                [void]$text.AppendLine($line)
                if ($OnMarker -and $line -match "^\[e2e\] @ (\S+)") { & $OnMarker $Matches[1] }
            }
        }
        if ($OnTick) { & $OnTick }
        if ($exited) { break }
        if ($clock.Elapsed.TotalSeconds -gt $TimeoutSeconds) {
            Stop-Process -Id $run.Process.Id -Force -ErrorAction SilentlyContinue
            Write-Host "[run-e2e] the client did not finish in $TimeoutSeconds s"
            return [pscustomobject]@{ Code = 124; Text = $text.ToString() }
        }
        Start-Sleep -Milliseconds 300
    }
    $run.Process.WaitForExit()
    $code = $run.Process.ExitCode
    if ($KeepLogs) {
        New-Item -ItemType Directory -Force -Path $KeepLogs | Out-Null
        $stem = Join-Path $KeepLogs ("run-{0:yyyyMMdd-HHmmss-fff}-code{1}" -f (Get-Date), $code)
        Copy-Item $run.Log "$stem.out"
        Copy-Item "$($run.Log).err" "$stem.err"
    }
    Remove-Item $run.Log, "$($run.Log).err" -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Code = $code; Text = $text.ToString() }
}

function Get-RunClientArgs([string[]] $Mode, [string[]] $Extra) {
    $list = @($Mode) + @("--server", "ws://127.0.0.1:$Port/ws", "--name", "e2e")
    if ($Monster) { $list += @("--e2e-monster", $Monster) }
    if ($Screenshot) { $list += @("--screenshot", $Screenshot) }
    return $list + $Extra + $ClientArgs
}

$engineArgs = @("--resolution", $Resolution)

# One run with one server. Returns the client's exit code.
function Invoke-Scenario([string] $Name) {
    Write-Host "== $Name =="
    $server = $null
    try {
        switch ($Name) {
            "play" {
                $server = Start-GameServer -Easy:$EasyMonsters
                $extra = @(); if (-not $EasyMonsters) { $extra += "--e2e-skip-warp" }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e" $extra) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 600).Code
            }
            "death" {
                $server = Start-GameServer -Easy:$EasyMonsters
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-death" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 600).Code
            }
            "warp" {
                $server = Start-GameServer -Easy
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-warp" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 300).Code
            }
            "resume_failed" {
                # The server forgets a dropped character after 2 s; the client's first retry comes after 5 s.
                $server = Start-GameServer -Extra @("--net-grace-period-ms", "2000")
                $run = Start-GodotClient (Get-RunClientArgs @("--e2e-bye", "resume_failed") @("--set", "backoff_initial_seconds=5", "--set", "backoff_cap_seconds=6", "--set", "backoff_jitter=0")) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 120).Code
            }
            "replaced" {
                $server = Start-GameServer
                $run = Start-GodotClient (Get-RunClientArgs @("--e2e-bye", "replaced") @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 120).Code
            }
            "grace_expiry" {
                # pings every second keep "last heard" fresh, so the deadline (5 s grace + 1 s margin) is 5 s or more after the kill.
                $server = Start-GameServer
                $holder = @{ Server = $server }
                $run = Start-GodotClient (Get-RunClientArgs @("--e2e-bye", "grace_expiry") @("--set", "grace_seconds=5", "--set", "grace_margin_seconds=1", "--set", "ping_interval_seconds=1",
                        "--set", "backoff_initial_seconds=0.5", "--set", "backoff_cap_seconds=1", "--set", "connect_timeout_seconds=2")) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 120 -OnMarker {
                    param($marker)
                    if ($marker -eq "kill-server") { Write-Host "[run-e2e] killing the server"; Stop-GameServer $holder.Server; $holder.Server = $null }
                }
                $server = $holder.Server
                return $result.Code
            }
            "shutdown" {
                # The first server stops itself after 20 s (bye shutdown); the script starts a second one on the same port for the client's retry.
                $holder = @{ Server = (Start-GameServer -Extra @("--duration", "20")); Second = $null }
                $server = $holder.Server
                $run = Start-GodotClient (Get-RunClientArgs @("--e2e-bye", "shutdown") @("--set", "shutdown_retry_seconds=3")) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 150 -OnTick {
                    if (-not $holder.Second -and $holder.Server.Process.HasExited) {
                        Write-Host "[run-e2e] the first server has stopped (exit code $($holder.Server.Process.ExitCode)); starting a second one"
                        $holder.Second = Start-GameServer
                    }
                }
                $server = $holder.Second
                Stop-GameServer $holder.Server
                if (-not $holder.Second) { Write-Host "[run-e2e] the server never stopped by itself"; return 1 }
                if ($holder.Server.Process.ExitCode -ne 0) { Write-Host "[run-e2e] the first server did not stop cleanly"; return 1 }
                return $result.Code
            }
        }
    }
    finally {
        Stop-GameServer $server
    }
}

# A desktop release export must refuse ws:// (a release template: OS.IsDebugBuild() is false) and must not refuse wss://.
function Invoke-Release {
    Write-Host "== release =="
    $linux = ($PSVersionTable.PSVersion.Major -ge 6) -and $IsLinux
    $preset = if ($linux) { "Linux (release check)" } else { "Windows Desktop (release check)" }
    $output = if ($linux) { "exports/linux/game-client.x86_64" } else { "exports/windows/game-client.exe" }
    $exe = Join-Path $client $output
    $console = if ($linux) { $exe } else { [IO.Path]::ChangeExtension($exe, ".console.exe") }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $exe) | Out-Null
    $export = Start-Process $godot -PassThru -NoNewWindow -RedirectStandardOutput ([IO.Path]::GetTempFileName()) -RedirectStandardError ([IO.Path]::GetTempFileName()) `
        -ArgumentList @("--headless", "--path", "`"$client`"", "--export-release", "`"$preset`"", "`"$exe`"")
    $null = $export.Handle
    if (-not $export.WaitForExit(900000)) { Stop-Process -Id $export.Id -Force; Write-Host "[run-e2e] the release export did not finish"; return 1 }
    if (-not (Test-Path $console)) { Write-Host "[run-e2e] the release export wrote no $console (exit code $($export.ExitCode))"; return 1 }
    Write-Host "[run-e2e] exported $preset to $output"

    # 1. ws:// is refused, and --set cannot turn it back on.
    $refuse = Start-GodotClient @("--exit-on-refusal", "--server", "ws://127.0.0.1:$Port/ws", "--set", "allow_cleartext=true") -Binary $console
    $a = Wait-GodotClient $refuse 60
    $refused = ($a.Code -eq 0) -and ($a.Text -match "release build") -and ($a.Text -match "CleartextRefused") -and ($a.Text -match "refusal screen for the server address")
    Write-Host "[run-e2e] release build, ws://: $(if ($refused) { 'refused, as it must (exit code 0, refusal screen shown)' } else { 'NOT REFUSED' })"

    # 2. wss:// is not refused: the client gets past the check and starts (it has nowhere to connect, so it is stopped after a few seconds).
    $secure = Start-GodotClient @("--autologin", "--server", "wss://127.0.0.1:9/ws") -Binary $console
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $seen = $false
    $b = Wait-GodotClient $secure 8 -OnTick { }
    if ($b.Text -match "release build, cleartext refused; renderer") { $seen = $true }
    Write-Host "[run-e2e] release build, wss://: $(if ($seen -and $b.Text -notmatch 'address refused') { 'passes the address check, as it must' } else { 'WRONGLY REFUSED' })"
    return $(if ($refused -and $seen) { 0 } else { 1 })
}

$runs = switch ($Run) {
    "bye" { @("resume_failed", "replaced", "shutdown", "grace_expiry") }
    "all" { @("play", "warp", "resume_failed", "replaced", "shutdown", "grace_expiry", "release") }
    default { @($Run) }
}
$failed = @()
try {
    foreach ($name in $runs) {
        $codes = @{}
        for ($i = 1; $i -le $Repeat; $i++) {
            $code = if ($name -eq "release") { Invoke-Release } else { Invoke-Scenario $name }
            Write-Host "[run-e2e] $name exit code: $code$(if ($Repeat -gt 1) { " (run $i of $Repeat)" })"
            $codes["$code"] = 1 + [int]$codes["$code"]
            if ($code -ne 0 -and $failed -notcontains $name) { $failed += $name }
        }
        if ($Repeat -gt 1) { Write-Host "[run-e2e] $name exit codes: $(($codes.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) x$($_.Value)" }) -join ', ')" }
    }
}
finally {
    if ($script:temp) { Remove-Item $script:temp -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($failed.Count -gt 0) { Write-Host "[run-e2e] FAILED: $($failed -join ', ')"; exit 1 }
Write-Host "[run-e2e] all passed: $($runs -join ', ')"
exit 0
