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
#   .\client\run-e2e.ps1 -Run quests                      First Hunt end to end: the guide's marker, accept in the dialogue, kill slimes and pick up their globs until the tracker
#                                                         counts 5/5 and 3/3, open the quest log, talk to turn it in, see the globs go and the draughts and currency arrive
#                                                         (T-602; content copy: weak monsters, every slime drops a glob; quests and drops are server-only, the bundle hash is unchanged)
#   .\client\run-e2e.ps1 -Run autopilot                 tick First Hunt in the quest log, Start, watch the HUD indicator while the server plays it to the turn-in with no input from the client and says
#                                                         finished; then start another quest, stop it with a tap on the indicator, start again and end it by walking (T-605; the quests content copy)
#   .\client\run-e2e.ps1 -Run account                     register, create a character, enter the world, drop and resume, log out, sign out, sign in again and enter the world with
#                                                         the same character (T-701); another device changes the password: put out, signed in with the new one, changed back (T-710); delete the account, keep it, delete it again (T-703). Needs PostgreSQL: $env:GAME_TEST_DB_CONNECTION; it uses its own database, mmo_e2e, on that server
#   .\client\run-e2e.ps1 -Run saving                      a new account's character changes stats, quest, items, currency, gear, EXP and place; its server stops (and saves) and the next one has it
#                                                         all; that one is killed outright and the third has its interval saves (T-702). Needs PostgreSQL like account; about 3 minutes
#   .\client\run-e2e.ps1 -Run appearance                  make a character with looks other than the defaults on the creation screen, enter and see it drawn with them; a second client (always
#                                                         headless) logs in beside it and sees them too (T-706). Needs PostgreSQL like account
#   .\client\run-e2e.ps1 -Run wardrobe                    earn the first quest's outfits (content copy: its step is only the talk), put one on through the bag's wardrobe, see it drawn; a second
#                                                         client (headless) sees it too; log out and in, it is still on; take it off (T-708). Needs PostgreSQL like account
#   .\client\run-e2e.ps1 -Run kick                        a new account's character plays; the script kicks the account through the admin API (its own port, a credential made for
#                                                         the run); the client says so and goes to the sign-in screen, signs in again; the audit row is there (T-804). Needs PostgreSQL like account
#   .\client\run-e2e.ps1 -Run moderation                  a new account's character plays; the script gives it an item through the admin API (it sees it), bans the account (bye banned,
#                                                         refused at sign-in), lifts the ban, and it signs in and plays with the item; the audit rows are there (T-806). Needs PostgreSQL like account
#   .\client\run-e2e.ps1 -Run textured                  the textured test theme (tools/themegen --textured, generated before the import, never committed): characters whose parts share
#                                                         one atlas are one textured surface, dressed and in outfits too; a hairstyle with another image is a second one; job models
#                                                         load only when a character needs one (T-711)
#   .\client\run-e2e.ps1 -Run chat                        two accounts' characters on one map talk and whisper through the chat panel; the second reports a line and blocks the first;
#                                                         the script reads and closes the report and mutes the first through the admin API, whose next line is refused (T-901). Needs PostgreSQL like account
#   .\client\run-e2e.ps1 -Run party                       two dev-login characters form a party by name, talk on the party channel, share a slime's EXP by party_exp, and one leaves
#   .\client\run-e2e.ps1 -Run trade                       two new accounts' characters trade a draught and currency for a tonic through the trade window; the server stops itself, the next
#                                                         one has the swap; a second trade ends when one walks away, and nothing moved (T-903; needs PostgreSQL like account)
#   .\client\run-e2e.ps1 -Run guild                       a new account's character founds a guild through the guild panel, invites a second (it accepts), they talk on the guild channel,
#                                                         the leader writes the notice and promotes the second, which leaves; the last member disbands it (T-904; needs PostgreSQL like account)
#   .\client\run-e2e.ps1 -Run all                         play, warp, hill, ui, items, statuses, skills, stats, dialogue, quests, autopilot, account, saving, appearance, wardrobe, kick, moderation, textured, chat, bye, release (account,
#                                                         saving, appearance, wardrobe, kick and moderation are skipped, and said so, when no database is configured)
#   .\client\run-e2e.ps1 -Run death -Monster monster.x    gets killed, drops the socket while dead, sees the death screen again, respawns
#   .\client\run-e2e.ps1 -EasyMonsters                    play with the whole warp leg on the weak-monster copy, as T-206 did
#   .\client\run-e2e.ps1 -Windowed -Screenshot shot.png   the same with the real renderer, saving a picture at the drop
param(
    [ValidateSet("play", "death", "warp", "hill", "ui", "items", "statuses", "skills", "stats", "dialogue", "quests", "autopilot", "account", "saving", "appearance", "wardrobe", "kick", "moderation", "textured", "chat", "party", "trade", "guild", "resume_failed", "replaced", "shutdown", "grace_expiry", "bye", "release", "all")] [string] $Run = "play",
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
# T-711: the textured test theme, written before the import (only files that changed), so the textured run and a textured measurement find it imported.
$texturedTheme = "textured-test"
$texturedOther = "model.char.hair_female_curls,model.char.hair_male_topknot"   # parts given another image than the atlas (not the default hairstyles) (asset keys: data for the run)
dotnet run --project (Join-Path $root "tools/themegen") -v q --nologo -- --textured $texturedTheme --other $texturedOther | Out-Host
if ($LASTEXITCODE -ne 0) { throw "The textured test theme could not be written." }
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

# The quests run's copy (T-602): the weak monsters, and every Gel Slime drops one or two Gel Globs, so First Hunt's kill-and-collect takes a few minutes at
# most. Drops are server-only data: the bundle hash does not change.
$script:questsTemp = $null
function Get-QuestsContent {
    if ($script:questsTemp) { return (Join-Path $script:questsTemp "content") }
    $script:questsTemp = Join-Path ([IO.Path]::GetTempPath()) ("t602-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:questsTemp | Out-Null
    Copy-Item (Get-EasyContent) (Join-Path $script:questsTemp "content") -Recurse
    Copy-Item (Join-Path $root "themes") (Join-Path $script:questsTemp "themes") -Recurse
    $drops = Join-Path $script:questsTemp "content/drops/gel_slime.yaml"
    $text = [IO.File]::ReadAllText($drops) -replace '\{ item_id: item.gel_glob, chance: [0-9.]+', '{ item_id: item.gel_glob, chance: 1'
    [IO.File]::WriteAllText($drops, $text)
    return (Join-Path $script:questsTemp "content")
}

# The wardrobe run's copy (T-708): the weak monsters, and the first quest's step is only its talk, so a new character earns the outfits by accepting it and talking again.
# Objectives are server-only data: the bundle hash does not change.
$script:wardrobeTemp = $null
function Get-WardrobeContent {
    if ($script:wardrobeTemp) { return (Join-Path $script:wardrobeTemp "content") }
    $script:wardrobeTemp = Join-Path ([IO.Path]::GetTempPath()) ("t708-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $script:wardrobeTemp | Out-Null
    Copy-Item (Get-EasyContent) (Join-Path $script:wardrobeTemp "content") -Recurse
    Copy-Item (Join-Path $root "themes") (Join-Path $script:wardrobeTemp "themes") -Recurse
    $quest = Join-Path $script:wardrobeTemp "content/quests/first_hunt.yaml"
    $text = [IO.File]::ReadAllText($quest) -replace '(?m)^      - \{ type: (kill|collect),.*\r?\n', ''
    [IO.File]::WriteAllText($quest, $text)
    return (Join-Path $script:wardrobeTemp "content")
}

# The account run's connection string (T-701): $env:GAME_TEST_DB_CONNECTION with the database mmo_e2e.
function Get-E2EConnection {
    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder.set_ConnectionString($env:GAME_TEST_DB_CONNECTION)
    $builder["Database"] = "mmo_e2e"
    return $builder.get_ConnectionString()
}

function Start-GameServer([switch] $Easy, [switch] $Items, [switch] $Statuses, [switch] $Skills, [switch] $Quests, [switch] $Wardrobe, [string[]] $Extra = @()) {
    $log = [IO.Path]::GetTempFileName()
    $arguments = @("run", "--no-build", "--project", "src/Game.Server", "--")
    if ($Wardrobe) { $arguments += (Get-WardrobeContent) }
    elseif ($Quests) { $arguments += (Get-QuestsContent) }
    elseif ($Items) { $arguments += (Get-ItemsContent) }
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
    # Windows; on Linux Stop-ProcessTree finds it with ps
    $child = Get-StartedProcesses $process | Where-Object { $_.Name -notmatch '^conhost' } | Select-Object -First 1
    $child = $(if ($child) { Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue } else { $null })
    if ($child) { $null = $child.Handle; Register-Started $child }
    return [pscustomobject]@{ Process = $process; Child = $child; Log = $log }
}

# Stop-ProcessTree (godot-path.ps1) stops the whole tree by process id and creation time: `dotnet run` starts the server as a child, and Godot's console wrapper the game exe.
function Stop-GameServer($server) {
    if (-not $server) { return }
    Stop-ProcessTree $server.Process
    if ($server.Child -and -not $server.Child.HasExited) { Stop-ProcessTree $server.Child }   # in case the tree lookup missed it
    $null = $server.Process.WaitForExit(10000)
    if ($server.Child) { $null = $server.Child.WaitForExit(10000) }
    Remove-Item $server.Log, "$($server.Log).err" -ErrorAction SilentlyContinue
}

# Starts Godot with the client's arguments (after `--`) and returns the process and its log file. Godot writes to stderr even when all is well,
# so its output goes to files and success is judged by the exit code, never by stderr.
function Start-GodotClient([string[]] $UserArgs, [string] $Binary = $godot, [string[]] $EngineArgs = @(), [switch] $Headless) {
    $log = [IO.Path]::GetTempFileName()
    $arguments = @()
    if ($Binary -eq $godot) { $arguments += @("--path", "`"$client`"") }
    $arguments += $EngineArgs
    if ($Headless -or -not $Windowed) { $arguments += "--headless" }
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
                Stop-ProcessTree $run.Process
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
            Stop-ProcessTree $run.Process
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
                return (Wait-GodotClient $run 480).Code   # about 300 s of layout matrix since the rank editor (T-810)
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
            "textured" {
                $server = Start-GameServer
                $extra = @("--e2e-textured", $texturedOther, "--e2e-textured-dress", "item.bone_dirk,item.leather_cap,item.padded_vest,item.wooden_buckler", "--set", "theme=$texturedTheme")
                $run = Start-GodotClient (Get-RunClientArgs @() $extra) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 180).Code
            }
            "dialogue" {
                $server = Start-GameServer
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-dialogue" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 180).Code
            }
            "quests" {
                $server = Start-GameServer -Quests
                $extra = @(); if (-not $Monster) { $extra += @("--e2e-monster", "monster.gel_slime") }   # the hunt counts gel slimes only
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-quests" $extra) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 600).Code
            }
            "autopilot" {
                # T-605: the quests content copy (weak monsters, every slime drops globs); the server plays the ticked quest, the client only shows it.
                $server = Start-GameServer -Quests
                $extra = @(); if (-not $Monster) { $extra += @("--e2e-monster", "monster.gel_slime") }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-autopilot" $extra) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 900).Code
            }
            "account" {
                # T-701: needs PostgreSQL. The server gets a database of its own (mmo_e2e), which it creates and migrates, so the developer's data is untouched.
                # Its connection string goes through the environment, never the command line or the output.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                try { $server = Start-GameServer } finally { $env:GAME_DB_CONNECTION = $null }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-account" @()) -EngineArgs $engineArgs
                return (Wait-GodotClient $run 180).Code
            }
            "saving" {
                # T-702: three servers on one database, one after the other. The first stops itself (the client is done changing things well before), the second is killed.
                # Weak monsters, so the new character wins the fight that earns it EXP.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                $holder = @{ Servers = @(); Next = 0; Killed = $false }
                try {
                    $holder.Servers += Start-GameServer -Easy -Extra @("--duration", "80")
                    $run = Start-GodotClient (Get-RunClientArgs "--e2e-saving" @("--set", "shutdown_retry_seconds=3")) -EngineArgs $engineArgs
                    $result = Wait-GodotClient $run 420 -OnMarker {
                        param($marker)
                        if ($marker -eq "kill-server") {
                            Write-Host "[run-e2e] killing the second server outright, starting a third"
                            Stop-GameServer $holder.Servers[1]
                            $holder.Servers += Start-GameServer -Easy -Extra @("--save-interval", "1")
                        }
                    } -OnTick {
                        if ($holder.Servers.Count -eq 1 -and $holder.Servers[0].Process.HasExited) {
                            $code = $holder.Servers[0].Process.ExitCode
                            Select-String -Path $holder.Servers[0].Log -Pattern "Every character is saved|NOT SAVED" | ForEach-Object { Write-Host "[run-e2e] first server: $($_.Line)" }
                            Write-Host "[run-e2e] the first server has stopped (exit code $code); starting a second one"
                            $holder.Servers += Start-GameServer -Easy -Extra @("--save-interval", "1")
                        }
                    }
                    if ($holder.Servers.Count -ne 3) { Write-Host "[run-e2e] expected three servers, had $($holder.Servers.Count)"; return 1 }
                    if ($holder.Servers[0].Process.ExitCode -ne 0) { Write-Host "[run-e2e] the first server did not stop cleanly"; return 1 }
                    return $result.Code
                }
                finally {
                    $env:GAME_DB_CONNECTION = $null
                    foreach ($one in $holder.Servers) { if (Test-Path $one.Log) { Stop-GameServer $one } }
                }
            }
            "appearance" {
                # T-706: a database like account. The first client asks for the second (the marker names the looks it must see) and waits for it to come and go.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                try { $server = Start-GameServer } finally { $env:GAME_DB_CONNECTION = $null }
                $holder = @{ Second = $null }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-appearance" @()) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 240 -OnMarker {
                    param($marker)
                    if ($marker -like "watch:*") {
                        Write-Host "[run-e2e] starting the second client"
                        $holder.Second = Start-GodotClient @("--e2e-appearance-watch", $marker.Substring(6), "--server", "ws://127.0.0.1:$Port/ws", "--name", "e2e-watcher") -EngineArgs $engineArgs -Headless
                    }
                }
                if (-not $holder.Second) { Write-Host "[run-e2e] the first client never asked for the second"; return 1 }
                $second = Wait-GodotClient $holder.Second 60
                if ($second.Code -ne 0) { Write-Host "[run-e2e] the second client failed (exit code $($second.Code))"; return 1 }
                return $result.Code
            }
            "wardrobe" {
                # T-708: a database like account, and the wardrobe content copy. The first client asks for the second (the marker names the outfit it must see) and waits for it.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                try { $server = Start-GameServer -Wardrobe } finally { $env:GAME_DB_CONNECTION = $null }
                $holder = @{ Second = $null }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-wardrobe" @()) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 300 -OnMarker {
                    param($marker)
                    if ($marker -like "watch-outfit:*") {
                        Write-Host "[run-e2e] starting the second client"
                        $holder.Second = Start-GodotClient @("--e2e-wardrobe-watch", $marker.Substring(13), "--server", "ws://127.0.0.1:$Port/ws", "--name", "e2e-watcher") -EngineArgs $engineArgs -Headless
                    }
                }
                if (-not $holder.Second) { Write-Host "[run-e2e] the first client never asked for the second"; return 1 }
                $second = Wait-GodotClient $holder.Second 60
                if ($second.Code -ne 0) { Write-Host "[run-e2e] the second client failed (exit code $($second.Code))"; return 1 }
                return $result.Code
            }
            "kick" {
                # T-804: a database like account, and the admin API on the next port with a service credential made for this run. The credential goes through the
                # environment and the request headers only: never the command line or the output. The client asks for the kick with a marker naming its username.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                $adminPort = $Port + 1
                $bytes = New-Object byte[] 32
                [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
                $credential = [Convert]::ToBase64String($bytes)
                $env:GAME_ADMIN_CREDENTIAL = $credential
                try { $server = Start-GameServer -Extra @("--admin-port", "$adminPort") } finally { $env:GAME_DB_CONNECTION = $null; $env:GAME_ADMIN_CREDENTIAL = $null }
                $deadline = (Get-Date).AddSeconds(10)
                while (-not (Select-String -Path $server.Log -Pattern "Admin API listening" -Quiet)) {
                    if ((Get-Date) -gt $deadline) { Get-Content $server.Log | Select-Object -Last 20; throw "The admin API did not start." }
                    Start-Sleep -Milliseconds 100
                }
                $admin = "http://127.0.0.1:$adminPort/admin/v1"
                $headers = @{ "Authorization" = "Bearer $credential"; "X-Staff-Id" = "e2e-staff"; "X-Staff-Name" = "E2E%20Staff"; "X-Staff-Role" = "support"; "X-Request-Id" = "e2e-kick" }
                $reason = "e2e: the admin API kick run"
                $holder = @{ Account = $null; Kick = $null }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-kick" @()) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 180 -OnMarker {
                    param($marker)
                    if ($marker -like "kick:*") {
                        try {
                            $found = Invoke-RestMethod -Uri "$admin/accounts?name=$($marker.Substring(5))" -Headers $headers
                            $holder.Account = @($found.items)[0].id
                            $body = @{ account_id = $holder.Account; reason = $reason } | ConvertTo-Json
                            $holder.Kick = Invoke-RestMethod -Method Post -Uri "$admin/commands/kick" -Headers $headers -ContentType "application/json" -Body $body
                            Write-Host "[run-e2e] kicked the account through the admin API: outcome $($holder.Kick.outcome), audit row $($holder.Kick.audit_id), $($holder.Kick.result.sessions_ended) session(s) ended"
                        } catch { Write-Host "[run-e2e] the admin API call failed: $($_.Exception.Message)" }
                    }
                }
                if (-not $holder.Kick) { Write-Host "[run-e2e] the account was never kicked"; return 1 }
                $audit = Invoke-RestMethod -Uri "$admin/audit?target=$($holder.Account)" -Headers $headers
                $row = @($audit.items) | Where-Object { $_.id -eq $holder.Kick.audit_id } | Select-Object -First 1
                if (-not $row -or $row.action -ne "kick" -or $row.reason -ne $reason -or $row.staff_id -ne "e2e-staff" -or $row.request_id -ne "e2e-kick" -or -not $row.before.online) {
                    Write-Host "[run-e2e] the audit row is missing or wrong"; return 1
                }
                Write-Host "[run-e2e] audit row $($row.id): $($row.action) of account $($row.target_id) by $($row.staff_id) ($($row.staff_role)), before online=$($row.before.online) on $($row.before.map), after sessions_ended=$($row.after.sessions_ended)"
                if (Select-String -Path $server.Log -Pattern ([regex]::Escape($credential)) -Quiet) { Write-Host "[run-e2e] THE CREDENTIAL IS IN THE SERVER LOG"; return 1 }
                return $result.Code
            }
            "moderation" {
                # T-806: as kick, with a gm. The client asks by markers: give it an item, ban it, lift the ban; it checks what it sees, the script checks the audit rows.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                $adminPort = $Port + 1
                $bytes = New-Object byte[] 32
                [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
                $credential = [Convert]::ToBase64String($bytes)
                $env:GAME_ADMIN_CREDENTIAL = $credential
                try { $server = Start-GameServer -Extra @("--admin-port", "$adminPort") } finally { $env:GAME_DB_CONNECTION = $null; $env:GAME_ADMIN_CREDENTIAL = $null }
                $deadline = (Get-Date).AddSeconds(10)
                while (-not (Select-String -Path $server.Log -Pattern "Admin API listening" -Quiet)) {
                    if ((Get-Date) -gt $deadline) { Get-Content $server.Log | Select-Object -Last 20; throw "The admin API did not start." }
                    Start-Sleep -Milliseconds 100
                }
                $admin = "http://127.0.0.1:$adminPort/admin/v1"
                $headers = @{ "Authorization" = "Bearer $credential"; "X-Staff-Id" = "e2e-gm"; "X-Staff-Name" = "E2E%20GM"; "X-Staff-Role" = "gm" }
                $holder = @{ Account = $null; Character = $null; Done = @() }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-moderation" @()) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 240 -OnMarker {
                    param($marker)
                    try {
                        if ($marker -like "moderation-give:*") {
                            $found = Invoke-RestMethod -Uri "$admin/accounts?name=$($marker.Substring(16))" -Headers $headers
                            $holder.Account = @($found.items)[0].id
                            $holder.Character = @((Invoke-RestMethod -Uri "$admin/characters?account=$($holder.Account)" -Headers $headers).items)[0].id
                            $item = (Get-ChildItem (Join-Path $root "content/items") -Filter *.yaml | Where-Object { Select-String -Path $_.FullName -Pattern '^kind:\s*material' -Quiet } | Select-Object -First 1 | Get-Content | Select-String '^id:\s*(\S+)').Matches[0].Groups[1].Value   # any material the content has
                            $body = @{ character_id = $holder.Character; item_id = $item; quantity = 2; reason = "e2e: a lost reward restored" } | ConvertTo-Json
                            $given = Invoke-RestMethod -Method Post -Uri "$admin/commands/give_item" -Headers $headers -ContentType "application/json" -Body $body
                            Write-Host "[run-e2e] gave $item x2 through the admin API: outcome $($given.outcome), audit row $($given.audit_id), entries $($given.result.entry_ids)"
                            $holder.Done += "give_item"
                        }
                        elseif ($marker -like "moderation-ban:*") {
                            $body = @{ account_id = $holder.Account; minutes = 60; reason = "e2e: the ban run" } | ConvertTo-Json
                            $banned = Invoke-RestMethod -Method Post -Uri "$admin/commands/ban" -Headers $headers -ContentType "application/json" -Body $body
                            Write-Host "[run-e2e] banned the account for 60 minutes: outcome $($banned.outcome), audit row $($banned.audit_id), until $($banned.result.until)"
                            $holder.Done += "ban"
                        }
                        elseif ($marker -like "moderation-unban:*") {
                            $body = @{ account_id = $holder.Account; reason = "e2e: lifted" } | ConvertTo-Json
                            $lifted = Invoke-RestMethod -Method Post -Uri "$admin/commands/unban" -Headers $headers -ContentType "application/json" -Body $body
                            Write-Host "[run-e2e] lifted the ban: outcome $($lifted.outcome), audit row $($lifted.audit_id)"
                            $holder.Done += "unban"
                        }
                    } catch { Write-Host "[run-e2e] the admin API call failed: $($_.Exception.Message)" }
                }
                if (($holder.Done -join ",") -ne "give_item,ban,unban") { Write-Host "[run-e2e] the script did not get every step done: $($holder.Done -join ', ')"; return 1 }
                $rows = @((Invoke-RestMethod -Uri "$admin/audit?target=$($holder.Account)" -Headers $headers).items) + @((Invoke-RestMethod -Uri "$admin/audit?target=$($holder.Character)" -Headers $headers).items)
                $actions = @($rows | ForEach-Object { $_.action } | Sort-Object)
                if (($actions -join ",") -ne "ban,give_item,unban") { Write-Host "[run-e2e] the audit rows are wrong: $($actions -join ', ')"; return 1 }
                Write-Host "[run-e2e] audit rows: $(@($rows | Sort-Object id | ForEach-Object { "$($_.id) $($_.action) by $($_.staff_id)" }) -join '; ')"
                if (Select-String -Path $server.Log -Pattern ([regex]::Escape($credential)) -Quiet) { Write-Host "[run-e2e] THE CREDENTIAL IS IN THE SERVER LOG"; return 1 }
                return $result.Code
            }
            "chat" {
                # T-901: a database like account and the admin API like moderation. The speaker asks for the listener by a marker naming its character; once both have talked,
                # whispered, reported and blocked, the speaker asks the script to announce (the listener's signal), read and close the report, and mute the speaker.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                $adminPort = $Port + 1
                $bytes = New-Object byte[] 32
                [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
                $credential = [Convert]::ToBase64String($bytes)
                $env:GAME_ADMIN_CREDENTIAL = $credential
                try { $server = Start-GameServer -Extra @("--admin-port", "$adminPort") } finally { $env:GAME_DB_CONNECTION = $null; $env:GAME_ADMIN_CREDENTIAL = $null }
                $deadline = (Get-Date).AddSeconds(10)
                while (-not (Select-String -Path $server.Log -Pattern "Admin API listening" -Quiet)) {
                    if ((Get-Date) -gt $deadline) { Get-Content $server.Log | Select-Object -Last 20; throw "The admin API did not start." }
                    Start-Sleep -Milliseconds 100
                }
                $admin = "http://127.0.0.1:$adminPort/admin/v1"
                $support = @{ "Authorization" = "Bearer $credential"; "X-Staff-Id" = "e2e-support"; "X-Staff-Name" = "E2E%20Support"; "X-Staff-Role" = "support" }
                $gm = $support.Clone(); $gm["X-Staff-Id"] = "e2e-gm"; $gm["X-Staff-Role"] = "gm"
                $holder = @{ Second = $null; Account = $null; Report = $null; Closed = $null; Muted = $null }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-chat" @()) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 360 -OnMarker {
                    param($marker)
                    try {
                        if ($marker -like "chat-second:*") {
                            Write-Host "[run-e2e] starting the listener"
                            $holder.Second = Start-GodotClient @("--e2e-chat-listen", $marker.Substring(12), "--server", "ws://127.0.0.1:$Port/ws", "--name", "e2e-listener") -EngineArgs $engineArgs -Headless
                        }
                        elseif ($marker -like "chat-sync:*") {
                            $body = @{ text = "E2E chat check"; reason = "e2e: the chat run's signal" } | ConvertTo-Json
                            $null = Invoke-RestMethod -Method Post -Uri "$admin/commands/announce" -Headers $gm -ContentType "application/json" -Body $body
                            $holder.Account = @((Invoke-RestMethod -Uri "$admin/accounts?name=$($marker.Substring(10))" -Headers $support).items)[0].id
                            $reports = @((Invoke-RestMethod -Uri "$admin/chat-reports?status=open&account=$($holder.Account)" -Headers $support).items)
                            $holder.Report = $reports | Where-Object { $_.sender_account_id -eq $holder.Account } | Select-Object -First 1
                            if ($holder.Report) {
                                Write-Host "[run-e2e] report $($holder.Report.id): channel $($holder.Report.channel), sender $($holder.Report.sender_character_id), reporter $($holder.Report.reporter_character_id)"
                                $body = @{ report_id = $holder.Report.id; reason = "e2e: looked at, nothing more to do" } | ConvertTo-Json
                                $holder.Closed = Invoke-RestMethod -Method Post -Uri "$admin/commands/close_report" -Headers $support -ContentType "application/json" -Body $body
                                Write-Host "[run-e2e] closed the report: outcome $($holder.Closed.outcome), audit row $($holder.Closed.audit_id)"
                            }
                            $body = @{ account_id = $holder.Account; minutes = 5; reason = "e2e: the chat run" } | ConvertTo-Json
                            $holder.Muted = Invoke-RestMethod -Method Post -Uri "$admin/commands/mute" -Headers $support -ContentType "application/json" -Body $body
                            Write-Host "[run-e2e] muted the speaker for 5 minutes: outcome $($holder.Muted.outcome), audit row $($holder.Muted.audit_id)"
                        }
                    } catch { Write-Host "[run-e2e] the admin API call failed: $($_.Exception.Message)" }
                }
                if (-not $holder.Second) { Write-Host "[run-e2e] the speaker never asked for the listener"; return 1 }
                $second = Wait-GodotClient $holder.Second 120
                if ($second.Code -ne 0) { Write-Host "[run-e2e] the listener failed (exit code $($second.Code))"; return 1 }
                if (-not $holder.Report) { Write-Host "[run-e2e] no open report of the speaker's line"; return 1 }
                if ($holder.Report.text -ne "e2esaid hi there" -or $holder.Report.channel -ne "map") { Write-Host "[run-e2e] the report holds the wrong line"; return 1 }
                if (-not $holder.Closed -or $holder.Closed.outcome -ne "done" -or -not $holder.Muted -or $holder.Muted.outcome -ne "done") { Write-Host "[run-e2e] closing the report or muting failed"; return 1 }
                $open = @((Invoke-RestMethod -Uri "$admin/chat-reports?status=open&account=$($holder.Account)" -Headers $support).items)
                if ($open.Count -ne 0) { Write-Host "[run-e2e] the report is still open"; return 1 }
                $rows = @((Invoke-RestMethod -Uri "$admin/audit?target=$($holder.Report.id)" -Headers $support).items) + @((Invoke-RestMethod -Uri "$admin/audit?target=$($holder.Account)" -Headers $support).items)
                $actions = @($rows | ForEach-Object { $_.action } | Sort-Object)
                if (($actions -join ",") -ne "close_report,mute") { Write-Host "[run-e2e] the audit rows are wrong: $($actions -join ', ')"; return 1 }
                if (@($rows | Where-Object { ($_.before | ConvertTo-Json -Compress) + ($_.after | ConvertTo-Json -Compress) -match "e2esaid" }).Count -gt 0) { Write-Host "[run-e2e] a line is in an audit row"; return 1 }
                Write-Host "[run-e2e] audit rows: $(@($rows | Sort-Object id | ForEach-Object { "$($_.id) $($_.action) by $($_.staff_id)" }) -join '; ')"
                if (Select-String -Path $server.Log -Pattern "e2esaid" -Quiet) { Write-Host "[run-e2e] A PLAYER'S LINE IS IN THE SERVER LOG"; return 1 }
                if (Select-String -Path $server.Log -Pattern ([regex]::Escape($credential)) -Quiet) { Write-Host "[run-e2e] THE CREDENTIAL IS IN THE SERVER LOG"; return 1 }
                Write-Host "[run-e2e] the server log holds no line a player wrote"
                return $result.Code
            }
            "guild" {
                # T-904: a database like account. The founder asks for the member by a marker naming its character; the member says map lines until the invite comes.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                try { $server = Start-GameServer } finally { $env:GAME_DB_CONNECTION = $null }
                $holder = @{ Second = $null }
                $run = Start-GodotClient (Get-RunClientArgs "--e2e-guild" @()) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 300 -OnMarker {
                    param($marker)
                    if ($marker -like "guild-second:*") {
                        Write-Host "[run-e2e] starting the member"
                        # T-905: the member sells at the meadow's merchant (content/npcs/) for the coins it deposits.
                        $holder.Second = Start-GodotClient @("--e2e-guild-join", $marker.Substring(13), "--e2e-guild-ids", "npc.merchant", "--server", "ws://127.0.0.1:$Port/ws", "--name", "e2e-member") -EngineArgs $engineArgs -Headless
                    }
                }
                if (-not $holder.Second) { Write-Host "[run-e2e] the founder never asked for the member"; return 1 }
                $second = Wait-GodotClient $holder.Second 120
                if ($second.Code -ne 0) { Write-Host "[run-e2e] the member failed (exit code $($second.Code))"; return 1 }
                $lines = @(Select-String -Path $server.Log -Pattern "\[guild\] guild \d+ (founded|disbanded)")
                Write-Host "[run-e2e] guild log lines: $(@($lines | ForEach-Object { ($_.Line -split '\[guild\] ')[1] }) -join '; ')"
                if ($lines.Count -ne 2) { Write-Host "[run-e2e] expected a founding and a disbanding on the server log, found $($lines.Count)"; return 1 }
                $moves = @(Select-String -Path $server.Log -Pattern "\[guild\] storage (deposit|withdraw) by .* landed")
                Write-Host "[run-e2e] guild storage moves on the server log: $($moves.Count)"
                if ($moves.Count -ne 6) { Write-Host "[run-e2e] expected three deposits and three withdrawals to land, found $($moves.Count)"; return 1 }
                if (Select-String -Path $server.Log -Pattern "e2esaid" -Quiet) { Write-Host "[run-e2e] A PLAYER'S LINE IS IN THE SERVER LOG"; return 1 }
                if (Select-String -Path $server.Log -Pattern "\[guild\].*(failed|could not)" -Quiet) { Write-Host "[run-e2e] the guild service logged a failure"; return 1 }
                return $result.Code
            }
            "party" {
                # T-902: two dev-login clients, no database. The expected EXP comes from content/: the Gel Slime's base and job EXP through formulas.yaml party_exp for a party of two,
                # split evenly with the odd point to the leader (listed first). The script knows the formula's shape, so a changed party_exp stops it here instead of passing wrongly.
                $slime = Get-Content (Join-Path $root "content/monsters/gel_slime.yaml") -Raw
                $baseExp = [long]([regex]::Match($slime, '(?m)^base_exp:\s*(\d+)').Groups[1].Value)
                $jobExp = [long]([regex]::Match($slime, '(?m)^job_exp:\s*(\d+)').Groups[1].Value)
                $formula = ([regex]::Match((Get-Content (Join-Path $root "content/formulas.yaml") -Raw), '(?m)^party_exp:\s*(.+)$').Groups[1].Value).Trim()
                if ($formula -ne "floor(exp * (100 + (share_count - 1) * 10) / 100)") { Write-Host "[run-e2e] party_exp changed ($formula): teach the party run its new shape"; return 1 }
                $basePool = [long][math]::Floor($baseExp * (100 + (2 - 1) * 10) / 100)
                $jobPool = [long][math]::Floor($jobExp * (100 + (2 - 1) * 10) / 100)
                $leader = @(([long][math]::Ceiling($basePool / 2)), ([long][math]::Ceiling($jobPool / 2)))
                $ally = @(($basePool - $leader[0]), ($jobPool - $leader[1]))
                Write-Host "[run-e2e] party: a Gel Slime ($baseExp base, $jobExp job) for two: $basePool and $jobPool, leader $($leader -join '/'), ally $($ally -join '/')"
                $server = Start-GameServer
                $holder = @{ Second = $null }
                $run = Start-GodotClient (Get-RunClientArgs @("--e2e-party", "e2e-ally", "$($leader[0])", "$($leader[1])", "--e2e-monster", "monster.gel_slime") @()) -EngineArgs $engineArgs
                $result = Wait-GodotClient $run 360 -OnMarker {
                    param($marker)
                    if ($marker -like "party-second:*") {
                        Write-Host "[run-e2e] starting the ally"
                        $holder.Second = Start-GodotClient @("--e2e-party-join", $marker.Substring(13), "$($ally[0])", "$($ally[1])", "--server", "ws://127.0.0.1:$Port/ws", "--name", "e2e-ally") -EngineArgs $engineArgs -Headless
                    }
                }
                if (-not $holder.Second) { Write-Host "[run-e2e] the leader never asked for the ally"; return 1 }
                $second = Wait-GodotClient $holder.Second 120
                if ($second.Code -ne 0) { Write-Host "[run-e2e] the ally failed (exit code $($second.Code))"; return 1 }
                if (Select-String -Path $server.Log -Pattern "e2esaid" -Quiet) { Write-Host "[run-e2e] A PLAYER'S LINE IS IN THE SERVER LOG"; return 1 }
                return $result.Code
            }
            "trade" {
                # T-903: a database like account; two servers one after the other, as saving does. The first stops itself (a clean stop: every character is saved) once the trade is
                # long done; the clients sign in again by themselves on the second, which must have the swap. The trader asks for the partner by a marker naming its character.
                $env:GAME_DB_CONNECTION = Get-E2EConnection
                $holder = @{ Servers = @(); Second = $null }
                try {
                    $holder.Servers += Start-GameServer -Extra @("--duration", "150")
                    $ids = @("--e2e-trade-ids", "item.healing_draught", "item.realign_tonic", "npc.merchant")   # a start stack, a start single, the meadow's merchant (content/balance.yaml, npcs/)
                    $run = Start-GodotClient (Get-RunClientArgs @("--e2e-trade") (@("--set", "shutdown_retry_seconds=3") + $ids)) -EngineArgs $engineArgs
                    $result = Wait-GodotClient $run 480 -OnMarker {
                        param($marker)
                        if ($marker -like "trade-second:*") {
                            Write-Host "[run-e2e] starting the partner"
                            $holder.Second = Start-GodotClient (@("--e2e-trade-join", $marker.Substring(13), "--server", "ws://127.0.0.1:$Port/ws", "--set", "shutdown_retry_seconds=3") + $ids) -EngineArgs $engineArgs -Headless
                        }
                    } -OnTick {
                        if ($holder.Servers.Count -eq 1 -and $holder.Servers[0].Process.HasExited) {
                            Select-String -Path $holder.Servers[0].Log -Pattern "Every character is saved|NOT SAVED|\[trade\]" | ForEach-Object { Write-Host "[run-e2e] first server: $($_.Line)" }
                            Write-Host "[run-e2e] the first server has stopped (exit code $($holder.Servers[0].Process.ExitCode)); starting a second one"
                            $holder.Servers += Start-GameServer
                        }
                    }
                    if (-not $holder.Second) { Write-Host "[run-e2e] the trader never asked for the partner"; return 1 }
                    $second = Wait-GodotClient $holder.Second 240
                    if ($second.Code -ne 0) { Write-Host "[run-e2e] the partner failed (exit code $($second.Code))"; return 1 }
                    if ($holder.Servers.Count -ne 2) { Write-Host "[run-e2e] expected two servers, had $($holder.Servers.Count)"; return 1 }
                    if ($holder.Servers[0].Process.ExitCode -ne 0) { Write-Host "[run-e2e] the first server did not stop cleanly"; return 1 }
                    $lines = @(Select-String -Path $holder.Servers[0].Log -Pattern "\[trade\] \{")
                    if ($lines.Count -ne 1) { Write-Host "[run-e2e] expected one trade log line on the first server, found $($lines.Count)"; return 1 }
                    if (@(Select-String -Path $holder.Servers[1].Log -Pattern "\[trade\] \{").Count -ne 0) { Write-Host "[run-e2e] the walked-away trade wrote a trade line"; return 1 }
                    if ($lines[0].Line -match "e2e-trade-|Ta[a-z]{10}|Tb[a-z]{10}") { Write-Host "[run-e2e] A NAME IS IN THE TRADE LOG LINE"; return 1 }
                    Write-Host "[run-e2e] trade log line: $(($lines[0].Line -split '\[trade\] ')[1])"
                    return $result.Code
                }
                finally {
                    $env:GAME_DB_CONNECTION = $null
                    foreach ($one in $holder.Servers) { if (Test-Path $one.Log) { Stop-GameServer $one } }
                }
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
    "all" { @("play", "warp", "hill", "ui", "items", "statuses", "skills", "stats", "dialogue", "quests", "autopilot", "account", "saving", "appearance", "wardrobe", "kick", "moderation", "textured", "chat", "party", "trade", "guild", "resume_failed", "replaced", "shutdown", "grace_expiry", "release") }
    default { @($Run) }
}
# The account, saving and appearance runs need a database. Named alone they fail without one; in `all` they are skipped, and the last line says so: skipped is not passed.
$skipped = @()
$databaseRuns = @("account", "saving", "appearance", "wardrobe", "kick", "moderation", "chat", "trade", "guild")
if (@($runs | Where-Object { $databaseRuns -contains $_ }).Count -gt 0 -and -not $env:GAME_TEST_DB_CONNECTION) {
    if ($Run -ne "all") { throw "The $Run run needs a PostgreSQL server: set `$env:GAME_TEST_DB_CONNECTION." }
    $skipped = $databaseRuns
    $runs = @($runs | Where-Object { $databaseRuns -notcontains $_ })
    Write-Host "== $($databaseRuns -join ', ') == SKIPPED: no database (`$env:GAME_TEST_DB_CONNECTION is not set)"
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
            Stop-ProcessTree $process
            $null = $process.WaitForExit(10000)
        }
    }
    if ($script:temp) { Remove-Item $script:temp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:itemsTemp) { Remove-Item $script:itemsTemp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:skillsTemp) { Remove-Item $script:skillsTemp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:statusesTemp) { Remove-Item $script:statusesTemp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:wardrobeTemp) { Remove-Item $script:wardrobeTemp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($script:questsTemp) { Remove-Item $script:questsTemp -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($failed.Count -gt 0) { Write-Host "[run-e2e] FAILED: $($failed -join ', ')"; exit 1 }
Write-Host "[run-e2e] all passed: $($runs -join ', ')$(if ($skipped.Count -gt 0) { "; SKIPPED: $($skipped -join ', ')" })"
exit 0
