# The check of local play (docs/LOCAL_PLAY.md, T-807), headless, run-e2e.ps1 style. Exit code 0 means everything passed. Needs what play-local.ps1 needs.
#   .\client\check-local-play.ps1
# It starts a play stack of its OWN (compose project "mmo-play-check", port 7871, its own database volume) with play-local.ps1, builds the Windows release game
# into client/exports/play-check/, and then:
#   1. the port answers nothing to plain HTTP (the server in the container speaks TLS only);
#   2. the game without the certificate beside it is refused by the TLS handshake (a release build trusts nothing it was not told to);
#   3. with it, the scripted client registers, makes a character, enters the world, resumes after a dropped socket and signs in again (run-e2e's "account" run);
#   4. two clients at once: the first makes a character on the creation screen, enters and waits; a second (the "account" run again) plays beside it, and the first
#      must see that other player's character drawn. (run-e2e's watcher client signs in with the development login, which this server rightly refuses.)
#   5. -Stop keeps the data, and the next start has the same schema.
# At the end it takes the stack down and removes everything it made: containers, network, volume, server image, certificate and build. -Keep leaves them.
param(
    [string] $Project = "mmo-play-check",
    [int] $Port = 7871,
    [string] $Godot = "",
    [int] $ClientTimeoutSeconds = 180,
    [string] $KeepLogs = "",
    [switch] $Keep
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "godot-path.ps1")
$play = Join-Path $root "play-local.ps1"
$state = Join-Path $root ".play/$Project"
$buildFolder = Join-Path $PSScriptRoot "exports/play-check"
$exe = Join-Path $buildFolder "game-client.exe"
$console = Join-Path $buildFolder "game-client.console.exe"
$url = "wss://127.0.0.1:$Port/ws"

# This script wipes its stack, so it must be its own.
if ($Project -eq "mmo-play") { throw "-Project mmo-play is the real local game; this check wipes its stack. Use another name." }
$ErrorActionPreference = "Continue"
$existing = @(& docker volume ls -q --filter "name=${Project}_mmo_play_pgdata" 2>$null)
$ErrorActionPreference = "Stop"
if ($existing -contains "${Project}_mmo_play_pgdata") {
    throw "The volume ${Project}_mmo_play_pgdata already exists. If it is left from an earlier check: .\play-local.ps1 -Reset -Project $Project"
}

$script:started = New-Object Collections.ArrayList
function Start-Game([string] $Name, [string[]] $UserArgs) {
    $log = [IO.Path]::GetTempFileName()
    $arguments = @("--headless", "--") + $UserArgs + @("--server", $url, "--name", $Name)
    $process = Start-Process $console -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err" -ArgumentList $arguments
    $null = $process.Handle
    [void]$script:started.Add($process)
    $engine = Get-GodotEngineProcess $console $process
    if ($engine) { [void]$script:started.Add($engine) }
    return [pscustomobject]@{ Name = $Name; Process = $process; Log = $log; Printed = 0 }
}

# The client's [e2e] and error lines that have not been printed yet.
function Read-GameLines($Run) {
    $stream = [IO.File]::Open($Run.Log, "Open", "Read", "ReadWrite")
    try { $all = (New-Object IO.StreamReader($stream)).ReadToEnd() } finally { $stream.Dispose() }
    $end = $all.LastIndexOf("`n") + 1
    if ($end -le $Run.Printed) { return @() }
    $new = $all.Substring($Run.Printed, $end - $Run.Printed)
    $Run.Printed = $end
    $lines = @($new -split "`r?`n" | Where-Object { $_ -match "^\[(e2e|client)\]|ERROR|Unhandled" })
    foreach ($line in $lines) { Write-Host "  $($Run.Name): $line" }
    return $lines
}

# Waits for the client to exit and returns its exit code and printed text. A client that does not finish in time is stopped: code 124.
# $OnLine is called with every line as it arrives.
function Wait-Game($Run, [scriptblock] $OnLine = $null) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $text = New-Object Text.StringBuilder
    $code = $null
    while ($null -eq $code) {
        $exited = $Run.Process.HasExited
        foreach ($line in @(Read-GameLines $Run)) {
            [void]$text.AppendLine($line)
            if ($OnLine) { & $OnLine $line }
        }
        if ($exited) { $code = $Run.Process.ExitCode }
        elseif ($clock.Elapsed.TotalSeconds -gt $ClientTimeoutSeconds) {
            Write-Host "[check] the client did not finish in $ClientTimeoutSeconds s"
            Stop-ProcessTree $Run.Process
            $null = $Run.Process.WaitForExit(10000)
            $code = 124
        }
        else { Start-Sleep -Milliseconds 200 }
    }
    if ($KeepLogs) {
        New-Item -ItemType Directory -Force -Path $KeepLogs | Out-Null
        Copy-Item $Run.Log (Join-Path $KeepLogs ("{0}-{1:HHmmss-fff}-code{2}.out" -f $Run.Name, (Get-Date), $code))
    }
    Remove-Item $Run.Log, "$($Run.Log).err" -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Code = $code; Text = $text.ToString() }
}

# Runs play-local.ps1 in a PowerShell of its own, as a player would (its `exit` ends only that one).
function Invoke-Play([string[]] $Arguments) {
    $shell = (Get-Process -Id $PID).Path
    $ErrorActionPreference = "Continue"
    & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $play -Project $Project -Port $Port @Arguments | Out-Host
    return $LASTEXITCODE
}

$script:failures = New-Object Collections.ArrayList
function Assert-Step([bool] $Passed, [string] $What) {
    Write-Host "[check] $(if ($Passed) { 'ok  ' } else { 'FAIL' }) $What"
    if (-not $Passed) { [void]$script:failures.Add($What) }
}

try {
    if ((Invoke-Play @("-NoGame")) -ne 0) { throw "The play stack did not start." }
    Assert-Step $true "the stack is up and healthy"

    $pem = Join-Path $state "certs/server.pem"
    & (Join-Path $PSScriptRoot "export-windows.ps1") -Output $exe -Certificate $pem -Godot $Godot -Force

    # 1. plain HTTP, what ws:// starts with, gets no answer.
    $tcp = New-Object Net.Sockets.TcpClient
    try {
        $tcp.Connect("127.0.0.1", $Port)
        $stream = $tcp.GetStream()
        $stream.ReadTimeout = 15000
        $request = [Text.Encoding]::ASCII.GetBytes("GET /ws HTTP/1.1`r`nHost: 127.0.0.1`r`nUpgrade: websocket`r`nConnection: Upgrade`r`n`r`n")
        $stream.Write($request, 0, $request.Length)
        $buffer = New-Object byte[] 64
        $answer = ""
        try { $read = $stream.Read($buffer, 0, $buffer.Length); $answer = [Text.Encoding]::ASCII.GetString($buffer, 0, $read) } catch [IO.IOException] { }
    }
    finally { $tcp.Close() }
    Assert-Step (-not $answer.StartsWith("HTTP/")) "plain HTTP (ws://) is not answered by the server in the container"

    # 2. without override.cfg the release build does not trust the self-signed certificate.
    $override = Join-Path $buildFolder "override.cfg"
    $trust = [IO.File]::ReadAllText($override)
    Remove-Item $override
    $untrusting = Wait-Game (Start-Game "untrusting" @("--e2e-account"))
    [IO.File]::WriteAllText($override, $trust)
    Assert-Step ($untrusting.Code -eq 1 -and $untrusting.Text -match "Unreachable") "without the certificate beside it the game does not connect (exit code $($untrusting.Code))"

    # 3. register, make a character, play.
    $account = Wait-Game (Start-Game "account" @("--e2e-account"))
    Assert-Step ($account.Code -eq 0 -and $account.Text -match "release build, cleartext refused" -and $account.Text -match "account: in the world") `
        "a release build registers, makes a character and enters the world over wss:// (exit code $($account.Code))"

    # 4. two clients at once. The first asks for the second with its marker, when it is in the world. The second's lines are printed after it has ended, but both
    #    run at the same time: the first client waits in the world until it has seen the second's character and seen it leave.
    $pair = @{ Second = $null }
    $first = Wait-Game (Start-Game "first" @("--e2e-appearance")) -OnLine {
        param($line)
        if (-not $pair.Second -and $line -match "^\[e2e\] @ watch:") { $pair.Second = Start-Game "second" @("--e2e-account") }
    }
    $second = if ($pair.Second) { Wait-Game $pair.Second } else { [pscustomobject]@{ Code = 1; Text = "" } }
    Assert-Step ($first.Code -eq 0 -and $second.Code -eq 0 -and $first.Text -match "the second client's character is drawn") `
        "with two clients at once, one sees the other player's character (exit codes $($first.Code) and $($second.Code))"

    # 5. stop keeps the data.
    Assert-Step ((Invoke-Play @("-Stop")) -eq 0) "-Stop stops the stack"
    Assert-Step ((Invoke-Play @("-NoGame")) -eq 0) "it starts again on the kept data"
    $ErrorActionPreference = "Continue"
    $server = @(& docker ps -q --filter "label=com.docker.compose.project=$Project" --filter "label=com.docker.compose.service=play-server" 2>$null) | Select-Object -First 1
    $log = @(& docker logs $server 2>$null) -join "`n"
    $ErrorActionPreference = "Stop"
    Assert-Step ($log -match "the schema is up to date" -and $log -notmatch "the schema is missing") "the second start found the first one's database"
}
finally {
    foreach ($process in $script:started) {
        if (-not $process.HasExited) {
            Write-Host "[check] stopping a leftover process: $($process.ProcessName) (pid $($process.Id))"
            Stop-ProcessTree $process
            $null = $process.WaitForExit(10000)
        }
    }
    if ($Keep) { Write-Host "[check] -Keep: the stack '$Project', its data, certificate and build are left. Remove: .\play-local.ps1 -Reset -Project $Project" }
    else {
        $null = Invoke-Play @("-Reset", "-Force")
        $ErrorActionPreference = "Continue"
        $null = & docker image rm "${Project}-play-server" 2>$null
        $ErrorActionPreference = "Stop"
        Remove-Item $state, $buildFolder -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if ($script:failures.Count -gt 0) { Write-Host "[check] FAILED: $($script:failures -join '; ')"; exit 1 }
Write-Host "[check] local play: all passed"
exit 0
