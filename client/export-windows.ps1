# Builds the game for a Windows PC (docs/LOCAL_PLAY.md, T-807): a RELEASE export through Invoke-GodotStep (godot-path.ps1, bounded and retried), preset
# "Windows Desktop (local play)", to client/exports/play/ (gitignored). Works in Windows PowerShell 5.1 and PowerShell 7. Exit code 0 means a complete build exists.
#   .\client\export-windows.ps1                                       build if anything it is made from has changed
#   .\client\export-windows.ps1 -Force                                build again
#   .\client\export-windows.ps1 -Certificate .play\mmo-play\certs\server.pem
#       also writes override.cfg beside the exe: this copy of the game then trusts exactly that certificate for TLS (the local server's self-signed one) and
#       nothing else. Without -Certificate an override.cfg is removed, and the build trusts the PC's certificate authorities, as for a real server.
# It is a release build: it refuses ws:// (ARCHITECTURE section 10) and checks the server's certificate like any release build.
param(
    [string] $Output = "exports/play/game-client.exe",   # relative to client/ unless absolute
    [string] $Certificate = "",       # a PEM certificate the build is told to trust (see above)
    [switch] $Force,
    [string] $Godot = "",             # the Godot .NET console binary; default: $env:GODOT, then the winget install
    [int] $TimeoutSeconds = 600,      # one export attempt (it builds the C# project first; a clean one takes about 1 minute)
    [int] $ExitSeconds = 60,          # from every file written (and no longer growing) to Godot's exit
    [int] $Attempts = 3,
    [int] $ImportTimeoutSeconds = 180
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root "client"
. (Join-Path $PSScriptRoot "godot-path.ps1")

$exe = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $client $Output }
$exe = [IO.Path]::GetFullPath($exe)
$folder = Split-Path -Parent $exe
$expected = @($exe, [IO.Path]::ChangeExtension($exe, ".console.exe"), [IO.Path]::ChangeExtension($exe, ".pck"))
$stampFile = Join-Path $folder "build.stamp"
if ($Certificate -and -not (Test-Path $Certificate)) { throw "No certificate file at $Certificate." }

# Art kept in Git LFS can be a small pointer file in a fresh clone; an export of those would be a game without models.
foreach ($file in Get-ChildItem (Join-Path $root "themes") -Recurse -File -Include *.glb, *.png, *.ogg, *.wav, *.ttf, *.otf, *.bin) {
    if ($file.Length -lt 1024 -and [IO.File]::ReadAllText($file.FullName).StartsWith("version https://git-lfs")) {
        throw "$($file.FullName) is a Git LFS pointer, not the file: run `git lfs pull` first."
    }
}

# A fingerprint of everything the build is made from (names, sizes and times): the game's content hash must match the server's, so a change rebuilds it.
function Get-BuildStamp {
    $lines = New-Object Collections.Generic.List[string]
    foreach ($part in @("client", "content", "themes", "src/Game.Shared", "src/Game.Content", "src/Game.Tools", "src/Game.Server/Net/ProtocolJson.cs",
            "Directory.Build.props", "Directory.Packages.props", "global.json")) {
        $path = Join-Path $root $part
        $files = if ($part -eq "client") {
            # Not what building and importing write (.godot, bundle, the mounted themes, exports) and not the scripts.
            @(Get-ChildItem $path -File | Where-Object { $_.Extension -ne ".ps1" -or $_.Name -eq "export-windows.ps1" }) +
            @(Get-ChildItem $path -Directory | Where-Object { @(".godot", "exports", "bundle", "themes", "bin", "obj", "build") -notcontains $_.Name } |
                    ForEach-Object { Get-ChildItem $_.FullName -Recurse -File })
        } else { @(Get-ChildItem $path -Recurse -File) }
        foreach ($file in $files) {
            if ($file.Extension -eq ".import" -or $file.FullName -match '[\\/](bin|obj)[\\/]') { continue }
            $lines.Add(("{0}|{1}|{2}" -f $file.FullName.Substring($root.Length), $file.Length, $file.LastWriteTimeUtc.Ticks))
        }
    }
    $lines.Sort([StringComparer]::Ordinal)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($lines -join "`n"))) -replace "-", "" }
    finally { $sha.Dispose() }
}

$complete = @($expected | Where-Object { -not (Test-Path $_) }).Count -eq 0
if (-not $Force -and $complete -and (Test-Path $stampFile) -and ([IO.File]::ReadAllText($stampFile) -eq (Get-BuildStamp))) {
    Write-Host "[export-windows] the game is up to date: $exe"
}
else {
    $godotPath = Resolve-Godot $Godot
    Write-Host "[export-windows] building the game (the first time takes a few minutes)"
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    # The old build goes first: a stale one must never pass for this one (ADR-002 row 163). A game still running from it holds the files.
    $old = @($expected) + $stampFile + (Join-Path $folder "override.cfg")
    foreach ($file in $old) {
        if (Test-Path $file) {
            try { Remove-Item $file -Force }
            catch { throw "Cannot replace $file. Close the game windows (or run .\play-local.ps1 -Stop) and try again." }
        }
    }

    dotnet build (Join-Path $client "GameClient.csproj") -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "The client does not build." }

    # An export packs a real copy of themes/ (ARCHITECTURE 7.7). Afterwards the folder is put back as it was.
    $mount = Join-Path $client "themes"
    $before = Get-Item $mount -ErrorAction SilentlyContinue
    $wasCopy = $before -and -not $before.LinkType
    & (Join-Path $client "mount-themes.ps1") -Copy | Out-Null
    try {
        Import-GodotProject $godotPath $client -Force -TimeoutSeconds $ImportTimeoutSeconds
        $state = @{ Since = [datetime]::UtcNow }
        $written = {
            $sizes = foreach ($file in $expected) {
                $item = Get-Item $file -ErrorAction SilentlyContinue
                if (-not $item -or $item.LastWriteTimeUtc -lt $state.Since) { return $null }
                $item.Length
            }
            return ($sizes -join ",")
        }
        $export = Invoke-GodotStep -Godot $godotPath -Step "windows export" -TimeoutSeconds $TimeoutSeconds -ExitSeconds $ExitSeconds -Attempts $Attempts -Output $written `
            -Arguments @("--headless", "--path", "`"$client`"", "--export-release", "`"Windows Desktop (local play)`"", "`"$exe`"") `
            -BeforeAttempt { Remove-Item $expected -Force -ErrorAction SilentlyContinue; $state.Since = [datetime]::UtcNow.AddSeconds(-1) }
    }
    finally {
        if (-not $wasCopy) {
            if ($before) { & (Join-Path $client "mount-themes.ps1") | Out-Null }
            else { Remove-Item $mount -Recurse -Force }
        }
    }
    if (-not $export.Ok) {
        $export.Output | Where-Object { $_ -match "ERROR|error" } | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
        throw "The Windows export failed: $($export.Outcome). Are the Godot 4.7.2 .NET export templates installed (docs/MOBILE_BUILD.md section 1)?"
    }
    [IO.File]::WriteAllText($stampFile, (Get-BuildStamp))
    Write-Host ("[export-windows] built in {0:N0} s: {1}" -f $export.Seconds, $exe)
}

# Godot reads override.cfg beside an exported game at its start. This one setting replaces the list of certificate authorities with the one certificate.
$override = Join-Path $folder "override.cfg"
if ($Certificate) {
    $pem = (Resolve-Path $Certificate).Path -replace '\\', '/'
    [IO.File]::WriteAllText($override, "[network]`n`ntls/certificate_bundle_override=`"$pem`"`n")
    Write-Host "[export-windows] this copy trusts only the certificate $pem"
}
elseif (Test-Path $override) { Remove-Item $override -Force }
