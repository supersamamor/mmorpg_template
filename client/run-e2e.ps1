# Scripted client runs against a real local server (docs/MOBILE_BUILD.md §8). Exit code 0 means every run passed. Works in Windows PowerShell 5.1 and PowerShell 7 (Linux in CI).
#   .\client\run-e2e.ps1                                  play: login, map, walk, kill, drop, dropped socket, resume, background, logout (real content, headless)
#   .\client\run-e2e.ps1 -Run warp                        the warp to the second map, on a temporary content copy with weak monsters (real content kills a new character on the way)
#   .\client\run-e2e.ps1 -Run hill                        taps a raised cell on the sample hill; the server must walk the character to exactly that cell
#   .\client\run-e2e.ps1 -Run ui                        the UI shell: layouts for every screen shape and UI scale, gestures, hotbar, cost (more: -Windowed -Screenshot x.png -Resolution 1560x720 -ClientArgs "--set","debug_notch=1")
#   .\client\run-e2e.ps1 -Run bye                      the four live bye runs: resume_failed, replaced, shutdown, grace_expiry (each can be named alone)
#   .\client\run-e2e.ps1 -Run release                     exports a desktop RELEASE build and checks that it refuses ws:// and does not refuse wss://
#   .\client\run-e2e.ps1 -Run items                       kill, pick up the drop, equip it through the bag, see the hits get stronger, use a potion (content copy: weak monsters
#                                                         that always drop a dagger, a potion, a vest, a card and a temper shard; new characters start with nothing) (T-401),
#                                                         then wear the vest, socket the card (max HP up), refine the dagger once (hits up) (T-402)
#   .\client\run-e2e.ps1 -Run statuses                    use a Swift Elixir (Swift in the HUD), fight a slime until it casts Iron Guard (Iron Skin marker above it) (T-501;
#                                                         content copy: weak but tough slimes that cast Iron Guard, new characters start with a Swift Elixir)
#   .\client\run-e2e.ps1 -Run skills                      fight monsters that cast Mana Font (a cast bar over a monster) until the EXP brings skill points and a job to change to, then through the
#                                                         skill panel: learn a skill, change job, learn a ranged attack; press its skill bar slot, tap a monster, see the own cast bar, the hit and
#                                                         the cooldown sweep (T-502; content copy: weak monsters worth a lot of EXP, 3 skill points a job level)
#   .\client\run-e2e.ps1 -Run stats                       spend the new character's stat points through the stats panel (the server's costs, derived stats following), use the stat reset
#                                                         from the bag after its confirmation and see the points and stats come back; the skill reset has nothing to take back and is refused
#                                                         (T-503; real content: new characters start with 10 stat points, a Realign Tonic and a Blank Page; below level 10 a reset keeps the item)
#   .\client\run-e2e.ps1 -Run dialogue                    tap the guide (the server walks the character up), see the dialogue panel, pick choices through two nodes to the save point,
#                                                         finish, talk again and close it (T-601; real content)
#   .\client\run-e2e.ps1 -Run all                         play, warp, hill, ui, items, statuses, skills, stats, dialogue, bye, release
#   .\client\run-e2e.ps1 -Run death -Monster monster.x    gets killed, drops the socket while dead, sees the death screen again, respawns
#   .\client\run-e2e.ps1 -EasyMonsters                    play with the whole warp leg on the weak-monster copy, as T-206 did
#   .\client\run-e2e.ps1 -Windowed -Screenshot shot.png   the same with the real renderer, saving a picture at the drop
param(
    [ValidateSet("play", "death", "warp", "hill", "ui", "items", "statuses", "skills", "stats", "dialogue", "resume_failed", "replaced", "shutdown", "grace_expiry", "bye", "release", "all")] [string] $Run = "play",
    [string] $Monster = "",          # the monster to hunt (a content id, so it is an argument); empty means the nearest
    [switch] $Windowed,              # real renderer instead of --headless
    [switch] $AudioTrace,           # log every sound played, skipped, stolen and crossfaded (audio_trace=1)
    [string] $Screenshot = "",
    [int] $Port = 7790,
    [int] $Seed = 206,
    [switch] $EasyMonsters,          # play: run the warp leg too, on a temporary content copy where a new character wins every fight
    [string] $Resolution = "1280x720",
    [string[]] $ClientArgs = @(),    # more arguments for the client, e.g. --set grace_seconds=30
    [string] $Godot = "",            # the Godot .NET console binary; default: $env:GODOT, then the winget install
    [int] $Repeat = 1,               # run each scenario this many times and print the count of each exit code (T-218)
    [string] $KeepLogs = "",         # keep each client run's stdout and stderr in this folder
    [int] $ExportTimeoutSeconds = 600,   # release: one export attempt (it builds the C# project first; a clean one takes about 1 minute here)
    [int] $ExportExitSeconds = 60,       # release: from every exported file written (and no longer growing) to Godot's exit (ADR-002 row 163)
    [int] $ExportAttempts = 3,           # release: an export that times out or hangs at exit is retried this many times in all (T-221)
    [int] $ImportTimeoutSeconds = 180,   # the import before the runs, per attempt (a clean one takes about 7 s); retried 3 times in all
    [int] $ClientExitSeconds = 30        # from the client's verdict line ([e2e] PASS/FAIL) to its exit (T-221)
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
Import-GodotProject $godot $client -Force -TimeoutSeconds $ImportTimeoutSeconds

$script:temp = $null
$script:itemsTemp = $null
$script:statusesTemp = $null
$script:skillsTemp = $null
# Every process this script starts (servers, the server exe under `dotnet run`, Godot and its game exe). Start-Process with redirection makes children
# inherit this script's handles, so anything left running keeps the caller's output pipe open and the caller waiting after the script has ended
# (ADR-002 row 190). The final cleanup stops whatever is still running and says so (T-221).
$script:started = New-Object Collections.ArrayList
function Register-Started($process) { if ($process) { [void]$script:started.Add($process) } }

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

# The items run's copy (T-401): the weak monsters, every kill drops a Bone Dirk and a Healing Draught (and for T-402 a Padded Vest, a Gelcap Card and a
# Temper Shard), and a new character starts with nothing, so the dagger it equips is the one it picked up. Drops and balance are server-only data: the
# bundle hash does not change.
function Get-ItemsContent {
    if ($script:itemsTemp) { return (Join-Path $script:itemsTemp "content") }
    $script:itemsTemp = Join-Path ([IO.Path]::GetTempPath()) ("t401-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:itemsTemp | Out-Null
    Copy-Item (Get-EasyContent) (Join-Path $script:itemsTemp "content") -Recurse
    Copy-Item (Join-Path $root "themes") (Join-Path $script:itemsTemp "themes") -Recurse
    foreach ($tier in @("tier1_supplies", "tier2_supplies", "tier3_supplies")) {
        # T-402 adds armour with a card slot, a card that fits it and the refine material, so the run can socket a card and refine.
        $text = "id: drop.$tier`nname: Items run drops`nentries:`n  - { item_id: item.bone_dirk, chance: 1 }`n  - { item_id: item.healing_draught, chance: 1 }`n" +
            "  - { item_id: item.padded_vest, chance: 1 }`n  - { item_id: item.gelcap_card, chance: 1 }`n  - { item_id: item.temper_shard, chance: 1 }`n"
        [IO.File]::WriteAllText((Join-Path $script:itemsTemp "content/drops/$tier.yaml"), $text)
    }
    $balance = Join-Path $script:itemsTemp "content/balance.yaml"
    $text = [IO.File]::ReadAllText($balance) -replace '(?ms)^  start_items:\r?\n(    - .*?\r?\n)+', "  start_items: []`n"
    $text = $text -replace "refine_needs_service: true", "refine_needs_service: false"   # this run is about the bag, not the smith (T-601); server-only, the bundle hash is unchanged
    [IO.File]::WriteAllText($balance, $text)
    return (Join-Path $script:itemsTemp "content")
}

# The statuses run's copy (T-501): the weak monsters, but slimes with 400 HP that cast Iron Guard on themselves whenever their attack is due and it is ready
# (a status on a monster), and new characters start with one Swift Elixir only (a status on the own character, through the bag). Monster AI and
# start items are server-only data: the bundle hash does not change.
function Get-StatusesContent {
    if ($script:statusesTemp) { return (Join-Path $script:statusesTemp "content") }
    $script:statusesTemp = Join-Path ([IO.Path]::GetTempPath()) ("t501-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:statusesTemp | Out-Null
    Copy-Item (Get-EasyContent) (Join-Path $script:statusesTemp "content") -Recurse
    Copy-Item (Join-Path $root "themes") (Join-Path $script:statusesTemp "themes") -Recurse
    $slime = Join-Path $script:statusesTemp "content/monsters/gel_slime.yaml"
    $text = [IO.File]::ReadAllText($slime) -replace 'max_hp: \d+', 'max_hp: 400' -replace '(?m)^  chase_range: (\d+)', "  chase_range: `$1`n  skills:`n    - { skill_id: skill.iron_guard, level: 1, chance: 1 }"
    [IO.File]::WriteAllText($slime, $text)
    $balance = Join-Path $script:statusesTemp "content/balance.yaml"
    $text = [IO.File]::ReadAllText($balance) -replace '(?ms)^  start_items:\r?\n(    - .*?\r?\n)+', "  start_items:`n    - { item_id: item.swift_elixir, quantity: 1 }`n"
    [IO.File]::WriteAllText($balance, $text)
    return (Join-Path $script:statusesTemp "content")
}

# The skills run's copy (T-502): the weak monsters, but with 60 HP so a cast has time to show, worth 3000 base and job EXP (one kill brings a few levels and the
# job's requirements), casting Mana Font whenever their attack is due (a cast bar over a monster), and 3 skill points a job level. Monster AI, EXP and the
# progression numbers are server-only data: the bundle hash does not change.
function Get-SkillsContent {
    if ($script:skillsTemp) { return (Join-Path $script:skillsTemp "content") }
    $script:skillsTemp = Join-Path ([IO.Path]::GetTempPath()) ("t502-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:skillsTemp | Out-Null
    Copy-Item (Get-EasyContent) (Join-Path $script:skillsTemp "content") -Recurse
    Copy-Item (Join-Path $root "themes") (Join-Path $script:skillsTemp "themes") -Recurse
    foreach ($file in Get-ChildItem (Join-Path $script:skillsTemp "content/monsters") -Filter *.yaml) {
        $text = [IO.File]::ReadAllText($file.FullName)
        $text = $text -replace 'max_hp: \d+', 'max_hp: 60' -replace '(?m)^base_exp: \d+', 'base_exp: 3000' -replace '(?m)^job_exp: \d+', 'job_exp: 3000'
        $text = $text -replace '(?m)^  skills:\r?\n(    .*\r?\n)+', ''
        $text = $text -replace '(?m)^  chase_range: (\d+)', "  chase_range: `$1`n  skills:`n    - { skill_id: skill.mana_font, level: 1, chance: 1 }"
        [IO.File]::WriteAllText($file.FullName, $text)
    }
    $balance = Join-Path $script:skillsTemp "content/balance.yaml"
    $text = [IO.File]::ReadAllText($balance) -replace 'skill_points_per_job_level: "1"', 'skill_points_per_job_level: "3"'
    [IO.File]::WriteAllText($balance, $text)
    return (Join-Path $script:skillsTemp "content")
}

function Start-GameServer([switch] $Easy, [switch] $Items, [switch] $Statuses, [switch] $Skills, [string[]] $Extra = @()) {
    $log = [IO.Path]::GetTempFileName()
    $arguments = @("run", "--no-build", "--project", "src/Game.Server", "--")
    if ($Items) { $arguments += (Get-ItemsContent) }
    elseif ($Statuses) { $arguments += (Get-StatusesContent) }
    elseif ($Skills) { $arguments += (Get-SkillsContent) }
    elseif ($Easy) { $arguments += (Get-EasyContent) }
    $arguments += @("--seed", "$Seed", "--net-port", "$Port") + $Extra
    $process = Start-Process dotnet -PassThru -NoNewWindow -WorkingDirectory $root -RedirectStandardOutput $log -RedirectStandardError "$log.err" -ArgumentList $arguments
    $null = $process.Handle
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Select-String -Path $log -Pattern "Gateway listening" -Quiet)) {
        if ((Get-Date) -gt $deadline -or $process.HasExited) { Get-Content $log, "$log.err"; throw "The server did not start." }
        Start-Sleep -Milliseconds 200
    }
    Write-Host (Select-String -Path $log -Pattern "Content hash:").Line
    Register-Started $process
    $child = $null
    if (Get-Command Get-CimInstance -ErrorAction SilentlyContinue) {   # Windows; on Linux Stop-ProcessTree finds it with ps
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '^conhost' } | Select-Object -First 1
    }
    $child = $(if ($child) { Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue } else { $null })
    if ($child) { $null = $child.Handle; Register-Started $child }
    return [pscustomobject]@{ Process = $process; Child = $child; Log = $log }
}

# Stop-ProcessTree (godot-path.ps1) stops the whole tree by process id: `dotnet run` starts the server as a child, and Godot's console wrapper the game exe.
function Stop-GameServer($server) {
    if (-not $server) { return }
    Stop-ProcessTree $server.Process.Id
    if ($server.Child -and -not $server.Child.HasExited) { Stop-ProcessTree $server.Child.Id }   # in case the tree lookup missed it
    $null = $server.Process.WaitForExit(10000)
    if ($server.Child) { $null = $server.Child.WaitForExit(10000) }
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
    $engine = Get-GodotEngineProcess $Binary $process
    Register-Started $process
    Register-Started $engine
    return [pscustomobject]@{ Process = $process; Engine = $engine; Log = $log; Position = 0L }
}

# Prints the client's new [e2e]/[client] lines until it exits (or $TimeoutSeconds passes, which stops it and fails with 124). $OnTick runs about every 0.3 s with
# the lines that arrived; a line "[e2e] @ name" is a request to the script and is passed on as $OnMarker. Once the client has printed its verdict
# ("[e2e] PASS" or "[e2e] FAIL"), it has $ClientExitSeconds to exit; if it has not, it is stopped and the run fails with 125 (CLIENT HUNG AT EXIT, T-221).
function Wait-GodotClient($run, [int] $TimeoutSeconds, [scriptblock] $OnMarker = $null, [scriptblock] $OnTick = $null) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $text = New-Object Text.StringBuilder
    $verdictAt = $null
    $engineExitAt = $null
    $stuck = $null
    $held = $null
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
            if ($line -match "^\[(e2e|client|audio)\]|ERROR|Unhandled") {
                if ($line -match "EditorSettings not instantiated") { continue }   # Godot 4.7.2's harmless line at exit
                Write-Host $line
                [void]$text.AppendLine($line)
                if ($OnMarker -and $line -match "^\[e2e\] @ (\S+)") { & $OnMarker $Matches[1] }
                if (-not $verdictAt -and $line -match "^\[e2e\] (PASS|FAIL)") { $verdictAt = $clock.Elapsed.TotalSeconds }
            }
        }
        if ($OnTick) { & $OnTick }
        if ($exited) { break }
        # The game exe has exited but Godot's console wrapper has not: it waits for every process in its job (docs/MOBILE_BUILD.md §8.5). The game's
        # own exit code is the result; the wrapper is stopped.
        if ($run.Engine -and $run.Engine.HasExited) {
            if (-not $engineExitAt) { $engineExitAt = $clock.Elapsed.TotalSeconds }
            elseif ($clock.Elapsed.TotalSeconds - $engineExitAt -ge 2) {
                Write-Host "[run-e2e] the client exited (exit code $($run.Engine.ExitCode)) but its console wrapper was held by a leftover process; stopped it"
                if ($env:GODOT_STUCK_HOOK) { & $env:GODOT_STUCK_HOOK $run.Process.Id | Out-Null }
                Stop-ProcessTree $run.Process.Id
                $null = $run.Process.WaitForExit(10000)
                $held = $run.Engine.ExitCode
                break
            }
        }
        $stuck = $null
        if ($verdictAt -and ($clock.Elapsed.TotalSeconds - $verdictAt) -gt $ClientExitSeconds) {
            $stuck = 125
            Write-Host ("[run-e2e] CLIENT HUNG AT EXIT: it printed its verdict at {0:0.0} s but had not exited {1} s later; stopped it" -f $verdictAt, $ClientExitSeconds)
        } elseif ($clock.Elapsed.TotalSeconds -gt $TimeoutSeconds) {
            $stuck = 124
            Write-Host "[run-e2e] the client did not finish in $TimeoutSeconds s"
        }
        if ($stuck) {
            if ($env:GODOT_STUCK_HOOK) { & $env:GODOT_STUCK_HOOK $run.Process.Id | Out-Null }
            Stop-ProcessTree $run.Process.Id
            $null = $run.Process.WaitForExit(10000)
            break
        }
        Start-Sleep -Milliseconds 300
    }
    if ($stuck) { $code = $stuck }
    elseif ($null -ne $held) { $code = $held }
    else {
        $null = $run.Process.WaitForExit(10000)   # it has exited; bounded anyway
        $code = $run.Process.ExitCode
    }
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
    if ($AudioTrace) { $list += @("--set", "audio_trace=1") }
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
            "ui" {
                $server = Start-GameServer -Easy
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-ui" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 300).Code
            }
            "items" {
                $server = Start-GameServer -Items
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-items" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 600).Code
            }
            "statuses" {
                $server = Start-GameServer -Statuses
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-statuses" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 300).Code
            }
            "skills" {
                $server = Start-GameServer -Skills
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-skills" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 400).Code
            }
            "stats" {
                $server = Start-GameServer
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-stats" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 180).Code
            }
            "dialogue" {
                $server = Start-GameServer
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-dialogue" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 180).Code
            }
            "hill" {
                # Weak monsters, so nothing on the way to the hill gets in the way (T-220).
                $server = Start-GameServer -Easy
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-hill" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 180).Code
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

# The desktop release export through Invoke-GodotStep. Before each attempt every file of the last export goes (a stale build must never pass for this
# one, ADR-002 row 163); the output is complete when every expected file exists, was written by this attempt and has stopped growing. An attempt that
# times out, or hangs for $ExportExitSeconds after writing every file, is stopped and retried (the build it wrote is not trusted).
function Invoke-ReleaseExport([string] $Preset, [string] $Exe, [string[]] $Expected) {
    $release = @{ Folder = (Split-Path -Parent $Exe); Stem = [IO.Path]::GetFileNameWithoutExtension($Exe); Name = [IO.Path]::GetFileName($Exe); Since = [datetime]::UtcNow }
    $before = {
        $old = { @(Get-ChildItem $release.Folder -File | Where-Object { $_.Name -eq $release.Name -or $_.Name -like "$($release.Stem).*" }) }
        # A client stopped a moment ago may still hold the exe for a second or two, so the delete is retried for up to 10 s.
        for ($try = 0; $try -lt 20 -and (& $old).Count -gt 0; $try++) {
            & $old | Remove-Item -Force -ErrorAction SilentlyContinue
            if ((& $old).Count -gt 0) { Start-Sleep -Milliseconds 500 }
        }
        if ((& $old).Count -gt 0) { throw "could not delete the old export: $((& $old | ForEach-Object Name) -join ', ')" }
        $release.Since = [datetime]::UtcNow.AddSeconds(-1)
    }
    $written = {
        $sizes = foreach ($file in $Expected) {
            $item = Get-Item $file -ErrorAction SilentlyContinue
            if (-not $item -or $item.LastWriteTimeUtc -lt $release.Since) { return $null }
            $item.Length
        }
        return ($sizes -join ",")
    }
    return Invoke-GodotStep -Godot $godot -Step "release export" -Arguments @("--headless", "--path", "`"$client`"", "--export-release", "`"$Preset`"", "`"$Exe`"") `
        -TimeoutSeconds $ExportTimeoutSeconds -ExitSeconds $ExportExitSeconds -Attempts $ExportAttempts -BeforeAttempt $before -Output $written -KeepLogs $KeepLogs
}

# A desktop release export must refuse ws:// (a release template: OS.IsDebugBuild() is false) and must not refuse wss://.
function Invoke-Release {
    Write-Host "== release =="
    $linux = ($PSVersionTable.PSVersion.Major -ge 6) -and $IsLinux
    $preset = if ($linux) { "Linux (release check)" } else { "Windows Desktop (release check)" }
    $output = if ($linux) { "exports/linux/game-client.x86_64" } else { "exports/windows/game-client.exe" }
    $exe = Join-Path $client $output
    $console = if ($linux) { $exe } else { [IO.Path]::ChangeExtension($exe, ".console.exe") }
    $folder = Split-Path -Parent $exe
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    # A stale build from an earlier run must never pass for this one (ADR-002 row 163): Invoke-ReleaseExport deletes every file of the last export first.
    $expected = @($exe, $console) + $(if ($linux) { @() } else { @([IO.Path]::ChangeExtension($exe, ".pck")) }) | Select-Object -Unique
    try { $export = Invoke-ReleaseExport -Preset $preset -Exe $exe -Expected $expected }
    catch { Write-Host "[run-e2e] the release export did not run: $($_.Exception.Message)"; return 1 }
    if (-not $export.Ok) {
        $export.Output | Where-Object { $_ -match "ERROR|error" } | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
        Write-Host "[run-e2e] the release export failed: $($export.Outcome)"
        return 1
    }
    Write-Host ("[run-e2e] release export: exit code 0 after {0:0} s (attempt {1})" -f $export.Seconds, $export.Attempts)
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
    "all" { @("play", "warp", "hill", "ui", "items", "statuses", "skills", "stats", "dialogue", "resume_failed", "replaced", "shutdown", "grace_expiry", "release") }
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
    foreach ($process in $script:started) {
        if (-not $process.HasExited) {
            Write-Host "[run-e2e] stopping a leftover process: $($process.ProcessName) (pid $($process.Id))"
            Stop-ProcessTree $process.Id
            $null = $process.WaitForExit(10000)
        }
    }
    if ($script:temp) { Remove-Item $script:temp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:itemsTemp) { Remove-Item $script:itemsTemp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:skillsTemp) { Remove-Item $script:skillsTemp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:statusesTemp) { Remove-Item $script:statusesTemp -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($failed.Count -gt 0) { Write-Host "[run-e2e] FAILED: $($failed -join ', ')"; exit 1 }
Write-Host "[run-e2e] all passed: $($runs -join ', ')"
exit 0
