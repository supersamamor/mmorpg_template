# ============================================================
#  MMORPG Template - Prerequisites Installer (Windows)
#  Run in PowerShell:  powershell -ExecutionPolicy Bypass -File .\setup-prereqs.ps1
#  Re-runnable: already-installed packages are skipped.
# ============================================================

$ErrorActionPreference = "Continue"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    [OK]   $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    [WARN] $msg" -ForegroundColor Yellow }

# --- 0. winget check ----------------------------------------
Write-Step "Checking winget"
if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Warn "winget not found. Install 'App Installer' from the Microsoft Store, then re-run."
    exit 1
}
Write-Ok "winget available"

# --- 1. Packages --------------------------------------------
# Docker is intentionally NOT included (already installed).
# Comment out anything you don't want.
$packages = @(
    @{ Id = "Microsoft.DotNet.SDK.10";        Name = ".NET 10 SDK" },
    @{ Id = "GodotEngine.GodotEngine.Mono";   Name = "Godot 4 (.NET / C# build)" },
    @{ Id = "Tiled.Tiled";                    Name = "Tiled map editor" },
    @{ Id = "Git.Git";                        Name = "Git" },
    @{ Id = "Microsoft.VisualStudioCode";     Name = "VS Code" },
    @{ Id = "OramaInteractive.Pixelorama";    Name = "Pixelorama (free pixel-art editor)" }
    # @{ Id = "JetBrains.Rider";              Name = "JetBrains Rider (optional IDE)" }
)

foreach ($p in $packages) {
    Write-Step "Installing $($p.Name)  [$($p.Id)]"
    $listed = winget list --id $p.Id -e --accept-source-agreements 2>$null | Out-String
    if ($listed -match [regex]::Escape($p.Id)) {
        Write-Ok "Already installed - skipping"
        continue
    }
    winget install --id $p.Id -e --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -eq 0) { Write-Ok "Installed" }
    else { Write-Warn "winget exit code $LASTEXITCODE - try: winget search `"$($p.Name)`"" }
}

# --- 2. VS Code extensions ----------------------------------
Write-Step "VS Code extensions (C# Dev Kit, Godot tools, YAML, Docker)"
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path","User")
if (Get-Command code -ErrorAction SilentlyContinue) {
    @(
        "ms-dotnettools.csdevkit",
        "geequlim.godot-tools",
        "redhat.vscode-yaml",
        "ms-azuretools.vscode-docker"
    ) | ForEach-Object { code --install-extension $_ --force | Out-Null; Write-Ok $_ }
} else {
    Write-Warn "'code' not on PATH yet - open a new terminal and re-run this script to add extensions."
}

# --- 3. Docker check + PostgreSQL ---------------------------
Write-Step "Checking Docker"
if (Get-Command docker -ErrorAction SilentlyContinue) {
    docker info *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Docker is running"
        if (Test-Path ".\docker-compose.yml") {
            Write-Step "Starting PostgreSQL (docker-compose.yml)"
            docker compose up -d
            if ($LASTEXITCODE -eq 0) { Write-Ok "Postgres up on localhost:5432" }
        } else {
            Write-Warn "docker-compose.yml not found next to this script - skipping Postgres."
        }
    } else {
        Write-Warn "Docker is installed but not running. Start Docker Desktop and re-run."
    }
} else {
    Write-Warn "docker command not found on PATH."
}

# --- 4. Verify ----------------------------------------------
Write-Step "Verification"
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path","User")

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdks = dotnet --list-sdks
    if ($sdks -match "^10\.") { Write-Ok ".NET 10 SDK present" } else { Write-Warn ".NET 10 SDK not listed. SDKs found:`n$sdks" }
} else { Write-Warn "dotnet not on PATH (open a new terminal)" }

if (Get-Command git -ErrorAction SilentlyContinue) { Write-Ok (git --version) } else { Write-Warn "git not on PATH (open a new terminal)" }

$godot = Get-Command godot* -ErrorAction SilentlyContinue | Select-Object -First 1
if ($godot) { Write-Ok "Godot found: $($godot.Name)" }
else { Write-Warn "Godot alias not on PATH - it's a portable zip; find it via: winget list GodotEngine" }

if (Get-Command docker -ErrorAction SilentlyContinue) {
    $pg = docker ps --filter "name=mmo-postgres" --format "{{.Status}}"
    if ($pg) { Write-Ok "mmo-postgres: $pg" } else { Write-Warn "mmo-postgres container not running" }
}

Write-Host "`nDone. Open a NEW terminal so PATH changes take effect." -ForegroundColor Cyan
