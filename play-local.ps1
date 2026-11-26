# Play the game on this PC (docs/LOCAL_PLAY.md, T-807). Works in Windows PowerShell 5.1 and PowerShell 7. Needs Docker Desktop running, the .NET 10 SDK,
# Godot 4.7.2 .NET with its export templates, and Git LFS files checked out.
#   .\play-local.ps1                  start the server and its database in Docker, build the game if needed, open one game window
#   .\play-local.ps1 -Players 2       the same with two windows side by side (register one account in each to see each other)
#   .\play-local.ps1 -NoGame          the server only
#   .\play-local.ps1 -Stop            close the game windows this script opened and stop the play stack; accounts and characters are kept
#   .\play-local.ps1 -Reset           -Stop, then wipe the play stack's database after asking (-Force: without asking)
# It only ever touches the compose project -Project (default mmo-play) and, of it, the two play services: the development database of `docker compose up`
# and its data are never stopped or changed. It never starts Docker Desktop.
[CmdletBinding()]
param(
    [ValidateRange(1, 2)] [int] $Players = 1,
    [switch] $Stop,
    [switch] $Reset,
    [switch] $Force,                  # -Reset: do not ask
    [switch] $NoGame,                 # start the server only: no game build, no windows
    [switch] $Rebuild,                # build the game again even if nothing changed
    [ValidateRange(1, 65535)] [int] $Port = 7780,   # on this PC's loopback
    [string] $Project = "mmo-play",   # the compose project: its containers, network and database volume
    [string] $Resolution = "",        # each window; default 1280x720, with two windows 960x540 or half the screen
    [string] $At = "",                # "x,y": where the first window opens (the second one to its right)
    [string] $Godot = "",             # the Godot .NET console binary; default: $env:GODOT, then the winget install
    [int] $StartTimeoutSeconds = 300  # for the containers to be healthy, after the image is built
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$compose = Join-Path $root "docker-compose.yml"

function Stop-WithMessage([string] $Message) {
    Write-Host "[play] $Message" -ForegroundColor Red
    exit 1
}

if ($Project -cnotmatch '^[a-z0-9][a-z0-9_-]*$') { Stop-WithMessage "-Project takes lower case letters, digits, - and _." }
if ($Stop -and $Reset) { Stop-WithMessage "-Reset stops the stack too: use one of -Stop and -Reset." }

$state = Join-Path $root ".play/$Project"       # gitignored
$certificates = Join-Path $state "certs"        # the only part the server container sees
$passwordFile = Join-Path $state "certificate-password.txt"
$windowsFile = Join-Path $state "windows.txt"
$volume = "${Project}_mmo_play_pgdata"

# Runs docker and returns what it printed to stdout; $script:dockerCode is its exit code. Windows PowerShell turns redirected stderr into errors, so errors do not
# stop the script while docker runs.
function Get-DockerLines([string[]] $Arguments) {
    $ErrorActionPreference = "Continue"
    $lines = @(& docker @Arguments 2>$null)
    $script:dockerCode = $LASTEXITCODE
    return $lines
}

# The same with everything docker prints shown as it comes (image builds take minutes).
function Invoke-Docker([string[]] $Arguments) {
    $ErrorActionPreference = "Continue"
    & docker @Arguments | Out-Host
    $script:dockerCode = $LASTEXITCODE
}

function Get-ComposeArguments([string[]] $Rest) { return @("compose", "-f", $compose, "-p", $Project, "--profile", "play") + $Rest }

# $null when Docker answers, else what to tell the user. Never starts anything.
function Get-DockerProblem {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return "Docker is not installed (no docker command). Install Docker Desktop, start it, and run this again." }
    $log = [IO.Path]::GetTempFileName()
    $notRunning = "Docker is not running. Start Docker Desktop yourself, wait until it says it is running, then run this again. (This script never starts it.)"
    try {
        $process = Start-Process docker -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err" -ArgumentList @("info", "--format", "{{.ServerVersion}}")
        $null = $process.Handle
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            return "Docker did not answer within 30 s. " + $notRunning
        }
        if ($process.ExitCode -ne 0 -or -not (Get-Content $log)) { return $notRunning }
    }
    finally { Remove-Item $log, "$log.err" -ErrorAction SilentlyContinue }
    $null = Get-DockerLines @("compose", "version")
    if ($script:dockerCode -ne 0) { return "Docker Compose v2 (the 'docker compose' command) is needed; it comes with Docker Desktop." }
    return $null
}

# ---- the game windows this script opened: one line each, "process id|start time|exe". A line only ever matches the very process that was started. ----
function Get-GameWindows {
    if (-not (Test-Path $windowsFile)) { return @() }
    $found = @()
    foreach ($line in @(Get-Content $windowsFile)) {
        $parts = $line -split '\|', 3
        if ($parts.Count -ne 3) { continue }
        $process = Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue
        if (-not $process) { continue }
        try { $same = ($process.StartTime.ToUniversalTime().Ticks -eq [long]$parts[1]) -and ($process.Path -eq $parts[2]) } catch { $same = $false }
        if ($same) { $found += [pscustomobject]@{ Line = $line; Process = $process } }
    }
    return $found
}

function Stop-GameWindows {
    foreach ($window in @(Get-GameWindows)) {
        $process = $window.Process
        $null = $process.Handle
        if (-not ($process.CloseMainWindow() -and $process.WaitForExit(5000))) {
            if (-not $process.HasExited) { $process.Kill() }
            $null = $process.WaitForExit(5000)
        }
        Write-Host "[play] closed the game window (process $($process.Id))"
    }
    Remove-Item $windowsFile -ErrorAction SilentlyContinue
}

function Get-PlayContainer([string] $Service) {
    return @(Get-DockerLines @("ps", "-a", "-q", "--filter", "label=com.docker.compose.project=$Project", "--filter", "label=com.docker.compose.service=$Service")) | Select-Object -First 1
}

# Stops and removes the two play containers, by name of service: nothing else in the project (or on the PC) is touched.
function Stop-PlayStack {
    $server = Get-PlayContainer "play-server"
    if (-not $server -and -not (Get-PlayContainer "play-db")) {
        Write-Host "[play] the play stack of project '$Project' is not running"
    }
    else {
        if ($server) {
            Invoke-Docker (Get-ComposeArguments @("stop", "play-server"))   # SIGTERM: the server saves every character before it exits
            foreach ($line in @(Get-DockerLines @("logs", "--tail", "40", $server))) {
                if ($line -match "Every character is saved|NOT SAVED") { Write-Host "[play] server: $line" }
            }
            $code = @(Get-DockerLines @("inspect", "--format", "{{.State.ExitCode}}", $server)) | Select-Object -First 1
            if ("$code" -eq "3") { Write-Host "[play] WARNING: the server stopped with exit code 3: some characters were not saved (docker logs, before the next start)." -ForegroundColor Yellow }
        }
        Invoke-Docker (Get-ComposeArguments @("rm", "--stop", "--force", "play-server", "play-db"))
        if ($script:dockerCode -ne 0) { Stop-WithMessage "docker compose could not remove the play containers." }
    }
    # The project's network, once no container of the project is left.
    if (-not @(Get-DockerLines @("ps", "-a", "-q", "--filter", "label=com.docker.compose.project=$Project"))) {
        $null = Get-DockerLines @("network", "rm", "${Project}_default")
    }
}

if ($Stop -or $Reset) {
    if ($Reset -and -not $Force) {
        Write-Host "[play] -Reset deletes every account and character of the local game (Docker volume $volume)."
        $answer = ""
        try { $answer = Read-Host "Type wipe to go on" }
        catch { Stop-WithMessage "Cannot ask here. Nothing was changed. Use -Reset -Force to wipe without asking." }
        if ($answer -cne "wipe") { Stop-WithMessage "Nothing was changed." }
    }
    Stop-GameWindows
    $problem = Get-DockerProblem
    if ($problem) { Stop-WithMessage $problem }
    Stop-PlayStack
    if ($Reset) {
        $null = Get-DockerLines @("volume", "inspect", $volume)
        if ($script:dockerCode -ne 0) { Write-Host "[play] there is no data to wipe (no volume $volume)" }
        else {
            Invoke-Docker @("volume", "rm", $volume)
            if ($script:dockerCode -ne 0) { Stop-WithMessage "The volume $volume could not be removed." }
            Write-Host "[play] the local game's data is wiped"
        }
    }
    else { Write-Host "[play] stopped. Accounts and characters are kept (Docker volume $volume); -Reset wipes them." }
    exit 0
}

# ---- start ----
$problem = Get-DockerProblem
if ($problem) { Stop-WithMessage $problem }
. (Join-Path $root "client/godot-path.ps1")
if (-not $NoGame) {
    # Fail now, not after the server image has been built.
    try { $null = Resolve-Godot $Godot } catch { Stop-WithMessage $_.Exception.Message }
    if ($At -and $At -notmatch '^-?\d+,-?\d+$') { Stop-WithMessage "-At takes x,y." }
    if ($Resolution -and $Resolution -notmatch '^\d+x\d+$') { Stop-WithMessage "-Resolution takes WIDTHxHEIGHT." }
}

# The certificate: made once, again when a file is missing or it is about to run out. A new one has a new password, so compose restarts the server with it.
$pem = Join-Path $certificates "server.pem"
$fresh = -not ((Test-Path $pem) -and (Test-Path (Join-Path $certificates "server.pfx")) -and (Test-Path $passwordFile))
if (-not $fresh) {
    $current = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $pem
    $fresh = $current.NotAfter -lt (Get-Date).AddDays(7)
    $current.Dispose()
}
if ($fresh) {
    Write-Host "[play] making a self-signed certificate for 127.0.0.1 in $certificates"
    $bytes = New-Object byte[] 24
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $random.GetBytes($bytes)
    $random.Dispose()
    $secret = [BitConverter]::ToString($bytes) -replace "-", ""
    & (Join-Path $root "tools/new-play-cert.ps1") -Folder $certificates -Password $secret
    [IO.File]::WriteAllText($passwordFile, $secret)
}

# The port must be free, unless it is this stack's own server that has it.
$ours = @(Get-DockerLines @("ps", "--filter", "label=com.docker.compose.project=$Project", "--filter", "label=com.docker.compose.service=play-server", "--format", "{{.Ports}}")) -join " "
if (-not $ours.Contains("127.0.0.1:$Port->")) {
    $probe = New-Object Net.Sockets.TcpClient
    $taken = $false
    try { $probe.Connect("127.0.0.1", $Port); $taken = $true } catch { } finally { $probe.Close() }
    if ($taken) { Stop-WithMessage "Something else already listens on 127.0.0.1:$Port (a server started with 'dotnet run'?). Stop it, or use -Port with another number." }
}

Write-Host "[play] building and starting the server (the first time downloads the .NET and PostgreSQL images: a few minutes)"
$settings = @{
    MMO_PLAY_CERT_DIR      = ($certificates -replace '\\', '/')
    MMO_PLAY_CERT_PASSWORD = [IO.File]::ReadAllText($passwordFile)
    MMO_PLAY_PORT          = "$Port"
}
# For this docker command only: the caller's PowerShell session keeps its own environment.
$saved = @{}
foreach ($name in $settings.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $settings[$name])
}
try {
    Invoke-Docker (Get-ComposeArguments @("up", "--detach", "--build", "--wait", "--wait-timeout", "$StartTimeoutSeconds", "play-server"))
    $started = $script:dockerCode -eq 0
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
if (-not $started) {
    $server = Get-PlayContainer "play-server"
    if ($server) {
        Write-Host "[play] the server's last lines:"
        Invoke-Docker @("logs", "--tail", "30", $server)
    }
    Stop-WithMessage "The play stack did not start. '.\play-local.ps1 -Stop -Project $Project' removes what is left."
}
Write-Host "[play] the server is up: wss://127.0.0.1:$Port/ws (project '$Project')" -ForegroundColor Green

if ($NoGame) { exit 0 }

$buildArguments = @{ Certificate = $pem; Godot = $Godot }
if ($Rebuild) { $buildArguments.Force = $true }
try { & (Join-Path $root "client/export-windows.ps1") @buildArguments }
catch { Stop-WithMessage "$($_.Exception.Message) (The server stays up; -Stop stops it.)" }
$exe = Join-Path $root "client\exports\play\game-client.exe"
$exe = [IO.Path]::GetFullPath($exe)

$origin = $null
$size = $Resolution
if ($At) { $origin = @($At -split ',' | ForEach-Object { [int]$_ }) }
if ($Players -eq 2) {
    $area = $null
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    }
    catch { }
    if (-not $size) {
        $width = if ($area) { [Math]::Min(960, [int][Math]::Floor($area.Width / 2)) } else { 960 }
        $size = "{0}x{1}" -f $width, [int]($width * 9 / 16)
    }
    if (-not $origin) { $origin = if ($area) { @($area.X, ($area.Y + 60)) } else { @(0, 60) } }
}
if (-not $size) { $size = "1280x720" }

New-Item -ItemType Directory -Force -Path $state | Out-Null
$lines = @(Get-GameWindows | ForEach-Object { $_.Line })
for ($i = 0; $i -lt $Players; $i++) {
    $arguments = @("--resolution", $size)
    if ($origin) { $arguments += @("--position", ("{0},{1}" -f ($origin[0] + $i * [int]($size -split 'x')[0]), $origin[1])) }
    $arguments += @("--", "--server", "wss://127.0.0.1:$Port/ws")
    $game = Start-Process $exe -PassThru -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList $arguments
    $null = $game.Handle
    $lines += "{0}|{1}|{2}" -f $game.Id, $game.StartTime.ToUniversalTime().Ticks, $exe
    [IO.File]::WriteAllLines($windowsFile, [string[]]$lines)
    Write-Host "[play] opened game window $($i + 1) of $Players (process $($game.Id))"
}
Write-Host "[play] Register in the window (any username; a password of your own for this local game), make a character and press Play."
if ($Players -eq 2) { Write-Host "[play] In the second window register a second account: the two characters see each other." }
Write-Host "[play] When you are done: .\play-local.ps1 -Stop"
exit 0
