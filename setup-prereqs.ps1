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
    @{ Id = "Git.Git";                        Name = "Git" },
    @{ Id = "GitHub.GitLFS";                  Name = "Git LFS (binary art storage)" },
    @{ Id = "Microsoft.VisualStudioCode";     Name = "VS Code" },
    @{ Id = "BlenderFoundation.Blender";      Name = "Blender (glTF models and animation)" },
    # JDK 17 is the version docs/MOBILE_BUILD.md pins for Godot 4.7's Android export.
    @{ Id = "Microsoft.OpenJDK.17";           Name = "JDK 17 (Android export)" },
    # Android Studio is used instead of the command-line tools because winget has no
    # package for the latter. Its SDK Manager installs the SDK into %LOCALAPPDATA%\Android\Sdk.
    @{ Id = "Google.AndroidStudio";           Name = "Android Studio (Android SDK Manager)" }
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

# --- 2b. Git LFS (user-level hooks, idempotent) -------------
Write-Step "Git LFS"
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path","User")
if (Get-Command git -ErrorAction SilentlyContinue) {
    git lfs install
    if ($LASTEXITCODE -eq 0) { Write-Ok "git lfs install done" }
    else { Write-Warn "git lfs not available yet - open a new terminal and re-run." }
} else {
    Write-Warn "git not on PATH yet - open a new terminal and re-run to enable Git LFS."
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

if (Get-Command git -ErrorAction SilentlyContinue) {
    $lfs = git lfs version 2>$null
    if ($LASTEXITCODE -eq 0) { Write-Ok $lfs } else { Write-Warn "git lfs not available (open a new terminal)" }
}
if (Get-Command blender -ErrorAction SilentlyContinue) { Write-Ok "Blender on PATH" }
else { Write-Warn "blender not on PATH - normal on Windows; start it from the Start menu" }

$javaHome = Get-ChildItem "$env:ProgramFiles\Microsoft" -Directory -Filter "jdk-17*" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($javaHome) { Write-Ok "JDK 17: $($javaHome.FullName)" } else { Write-Warn "JDK 17 not found under $env:ProgramFiles\Microsoft" }

$sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
if (Test-Path $sdk) { Write-Ok "Android SDK: $sdk" } else { Write-Warn "Android SDK not installed yet - see manual step 1 below" }

$godot = Get-Command godot* -ErrorAction SilentlyContinue | Select-Object -First 1
if ($godot) { Write-Ok "Godot found: $($godot.Name)" }
else { Write-Warn "Godot alias not on PATH - it's a portable zip; find it via: winget list GodotEngine" }

if (Get-Command docker -ErrorAction SilentlyContinue) {
    $pg = docker ps --filter "name=mmo-postgres" --format "{{.Status}}"
    if ($pg) { Write-Ok "mmo-postgres: $pg" } else { Write-Warn "mmo-postgres container not running" }
}

Write-Step "Manual steps this script cannot do (details: docs/MOBILE_BUILD.md)"
Write-Host @"
  1. Android Studio: start it once and finish the setup wizard, then SDK Manager:
     install platform android-35, build-tools 35.0.1, platform-tools and
     'Android SDK Command-line Tools'. SDK folder: $env:LOCALAPPDATA\Android\Sdk
     (docs/MOBILE_BUILD.md 2.2)
  2. Accept the Android SDK licences: sdkmanager --licenses (docs/MOBILE_BUILD.md 2.2)
  3. Godot: install the .NET export templates (docs/MOBILE_BUILD.md 1) and set
     Editor Settings > Export > Android > Java SDK Path (the JDK 17 folder) and
     Android SDK Path (docs/MOBILE_BUILD.md 2.3)
  4. Create or let Godot generate the debug keystore (docs/MOBILE_BUILD.md 2.3)
  5. iOS builds need a Mac with Xcode (docs/MOBILE_BUILD.md 5)
"@

Write-Host "`nDone. Open a NEW terminal so PATH changes take effect." -ForegroundColor Cyan
