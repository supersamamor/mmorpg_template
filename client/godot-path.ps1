# Finds the Godot 4.7.2 .NET console binary for the scripts in this folder (dot-source it): the argument, then $env:GODOT, then the winget install.
# Also holds Invoke-GodotStep, the one way these scripts run a headless Godot step (import, export, map export): bounded, retried, logged (T-221).
function Resolve-Godot([string] $Given) {
    if ($Given) { return $Given }
    if ($env:GODOT) { return $env:GODOT }
    $found = $null
    if ($env:LOCALAPPDATA) {
        $found = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "Godot_v4.7.2-stable_mono_win64_console.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $found) { throw "Godot 4.7.2 .NET is not installed: pass -Godot, set `$env:GODOT, or follow docs/MOBILE_BUILD.md §1." }
    return $found.FullName
}

# The processes $Process started that still run (CIM records with ProcessId, ParentProcessId, CreationDate, Name). Windows keeps a dead parent's id on its children and reuses ids, so
# `ParentProcessId` alone also finds strangers (csrss, once: T-226). A child is never older than its parent, so only processes created after $Process are its children. $Process must
# come from Start-Process or Get-Process with its Handle read: an open handle keeps the id from being reused and the start time readable after the process has ended.
function Get-StartedProcesses([Diagnostics.Process] $Process) {
    $since = $null
    try { $since = $Process.StartTime } catch { }
    if (-not $since -or -not (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) { return @() }
    return @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Process.Id)" -ErrorAction SilentlyContinue | Where-Object { Test-StartedAfter $_.CreationDate $since })
}

# Whether a process was created no earlier than its parent. An unknown time (a protected process) is never a child.
function Test-StartedAfter($Created, $ParentCreated) { return [bool]($Created -and $ParentCreated -and $Created -ge $ParentCreated) }

# Stops a process and every process it started, children first. Only by process id and creation time, never by name: other worktrees run Godot too, and ids are reused (T-226).
# Godot's Windows console wrapper (_console.exe) starts the real Godot exe as its child, and `dotnet run` starts the server as its child (ADR-002 row 166).
function Stop-ProcessTree([Diagnostics.Process] $Process) {
    $since = $null
    try { $since = $Process.StartTime } catch { }
    $children = @{}
    $created = @{}
    $windows = [bool](Get-Command Get-CimInstance -ErrorAction SilentlyContinue)
    if ($windows) {
        foreach ($p in @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate -ErrorAction SilentlyContinue)) {
            if ($p.ProcessId -ne $p.ParentProcessId) { $children["$($p.ParentProcessId)"] += @([int]$p.ProcessId) }
            $created["$($p.ProcessId)"] = $p.CreationDate
        }
        $created["$($Process.Id)"] = $since   # it may have ended already
    } else {   # Linux: the children of a process that has ended are given to another parent at once, so a parent id is never stale
        foreach ($line in @(& ps -e -o pid=,ppid= 2>$null)) {
            $f = "$line".Trim() -split '\s+'
            if ($f.Count -eq 2) { $children[$f[1]] += @([int]$f[0]) }
        }
    }
    $order = New-Object Collections.Generic.List[int]
    $seen = @{}
    $visit = {
        param([int] $Node)
        if ($seen["$Node"]) { return }
        $seen["$Node"] = $true
        foreach ($child in @($children["$Node"])) {
            if ($child -and (-not $windows -or (Test-StartedAfter $created["$child"] $created["$Node"]))) { & $visit $child }
        }
        $order.Add($Node)
    }
    & $visit $Process.Id
    foreach ($node in $order) {
        $target = $(if ($node -eq $Process.Id) { $Process } else { Get-Process -Id $node -ErrorAction SilentlyContinue })
        if (-not $target) { continue }
        try {
            if ($target.HasExited) { continue }
            # The id may have gone to another process since the list was taken (CIM's time is the same one, cut to microseconds).
            if ($windows -and $node -ne $Process.Id -and [Math]::Abs(($target.StartTime - $created["$node"]).TotalMilliseconds) -gt 1) { continue }
            $target.Kill()
        } catch { }   # it ended meanwhile, or it is not ours to stop
    }
}

# On Windows the binary the scripts start is Godot's console wrapper (*_console.exe for the editor, *.console.exe for an export), which starts the real Godot exe as its child, puts it in a job
# and exits only when every process in that job has exited. This returns that child (with its exit code kept readable), or $null for any other binary.
function Get-GodotEngineProcess([string] $Godot, [Diagnostics.Process] $Wrapper) {
    if ($Godot -notmatch '[._]console\.exe$' -or -not (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) { return $null }
    for ($i = 0; $i -lt 50 -and -not $Wrapper.HasExited; $i++) {
        $child = Get-StartedProcesses $Wrapper | Where-Object { $_.Name -notmatch '^conhost' } | Select-Object -First 1
        if ($child) {
            $process = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
            if ($process) { $null = $process.Handle; return $process }
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

# Runs one headless Godot step and never waits without a limit. Each attempt:
#   - stops the step after -TimeoutSeconds (outcome "timeout");
#   - if -Output is given, polls it every 0.5 s: it returns $null while the expected output is incomplete, or a fingerprint (file sizes, say) once it is
#     complete. When the fingerprint has not changed for -StableSeconds, Godot gets -ExitSeconds more to exit; if it has not, the tree is stopped:
#     outcome "ok, stopped after output" with -StopWhenComplete (Godot's Android export never exits, ADR-001), else "hung at exit" (ADR-002 row 163);
#   - stops the whole process tree by id and creation time (Stop-ProcessTree), never by name.
# Hangs are retried up to -Attempts in all; a non-zero exit code only with -RetryOnExitCode (a real error, such as a bad map, repeats, so it is not
# retried by default). -BeforeAttempt runs before every attempt (delete the old output). -OnStuck gets the process id just before a stuck process is
# stopped (to collect evidence); without it, $env:GODOT_STUCK_HOOK (a program taking the process id) is run if set. The blocks run inside this
# function, whose own variables all start with "step", so they can use the caller's variables by any other name. One line per attempt: "[godot] <step> attempt 1/3: <outcome> after 8.1 s".
# Returns { Ok, Code, Outcome, Attempts, Seconds, Output } where Output is the last attempt's stdout and stderr lines and Code is 0 only if Ok.
function Invoke-GodotStep {
    param(
        [Parameter(Mandatory = $true)] [string] $Godot,
        [Parameter(Mandatory = $true)] [string] $Step,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [int] $TimeoutSeconds = 300,
        [scriptblock] $Output = $null,
        [int] $StableSeconds = 3,
        [int] $ExitSeconds = 30,
        [switch] $StopWhenComplete,
        [int] $Attempts = 3,
        [switch] $RetryOnExitCode,
        [scriptblock] $BeforeAttempt = $null,
        [scriptblock] $OnStuck = $null,
        [string] $KeepLogs = ""
    )
    $stepResult = $null
    for ($stepAttempt = 1; $stepAttempt -le $Attempts; $stepAttempt++) {
        if ($BeforeAttempt) { & $BeforeAttempt }
        $stepLog = Join-Path ([IO.Path]::GetTempPath()) ("godot-step-" + [guid]::NewGuid().ToString("N") + ".log")
        # Godot writes to stderr even when all is well, so its output goes to files and success is judged by the exit code and the output check.
        $stepProcess = Start-Process $Godot -PassThru -NoNewWindow -RedirectStandardOutput $stepLog -RedirectStandardError "$stepLog.err" -ArgumentList $Arguments
        $null = $stepProcess.Handle   # keeps the exit code readable after the process ends
        $stepClock = [Diagnostics.Stopwatch]::StartNew()
        $stepOutcome = $null; $stepOk = $false; $stepCode = $null
        $stepFingerprint = $null; $stepStableAt = $null; $stepCompleteAt = $null
        $stepEngine = Get-GodotEngineProcess $Godot $stepProcess
        $stepEngineExitAt = $null
        while ($true) {
            $stepHeld = $false
            if ($stepEngine -and -not $stepProcess.HasExited -and $stepEngine.HasExited) {
                # Godot itself has exited but its console wrapper has not (T-221, docs/MOBILE_BUILD.md §8.5): the wrapper waits for every process in its job, and
                # the adb server that Godot's Android export plugin starts never exits. Godot's own exit code is the step's result.
                # Godot can restart itself (a new Godot process in the same job); then that one is followed instead.
                $stepNext = Get-StartedProcesses $stepEngine | Where-Object { $_.Name -eq "$($stepEngine.ProcessName).exe" } | Select-Object -First 1
                $stepNext = $(if ($stepNext) { Get-Process -Id $stepNext.ProcessId -ErrorAction SilentlyContinue } else { $null })
                if ($stepNext) { $null = $stepNext.Handle; $stepEngine = $stepNext; $stepEngineExitAt = $null }
                elseif (-not $stepEngineExitAt) { $stepEngineExitAt = $stepClock.Elapsed.TotalSeconds }
                elseif ($stepClock.Elapsed.TotalSeconds - $stepEngineExitAt -ge 2) { $stepHeld = $true }
            }
            if ($stepHeld -or $stepProcess.WaitForExit(500)) {
                if ($stepHeld) {
                    try { if ($OnStuck) { & $OnStuck $stepProcess.Id } elseif ($env:GODOT_STUCK_HOOK) { & $env:GODOT_STUCK_HOOK $stepProcess.Id | Out-Null } }
                    catch { Write-Host "[godot] $Step evidence capture failed: $($_.Exception.Message)" }
                    Stop-ProcessTree $stepProcess   # closing the wrapper's job also ends the adb server in it
                    $null = $stepProcess.WaitForExit(10000)
                    $stepCode = $stepEngine.ExitCode
                } else {
                    $null = $stepProcess.WaitForExit(5000)   # bounded: lets .NET finish its exit bookkeeping
                    $stepCode = $stepProcess.ExitCode
                }
                $stepNote = $(if ($stepHeld) { " (Godot exited; its console wrapper was held by a leftover process and was stopped)" } else { "" })
                $stepComplete = (-not $Output) -or ($null -ne (& $Output))
                if ($stepCode -eq 0 -and $stepComplete) { $stepOutcome = "ok, exit 0$stepNote"; $stepOk = $true }
                elseif ($stepCode -eq 0) { $stepOutcome = "exit 0 without the expected output$stepNote" }
                else { $stepOutcome = "exit $stepCode$stepNote" }
                break
            }
            $stepElapsed = $stepClock.Elapsed.TotalSeconds
            if ($Output -and -not $stepCompleteAt) {
                $stepNow = & $Output
                if ($null -eq $stepNow) { $stepFingerprint = $null; $stepStableAt = $null }
                elseif ($stepNow -ne $stepFingerprint) { $stepFingerprint = $stepNow; $stepStableAt = $stepElapsed }
                elseif ($stepElapsed - $stepStableAt -ge $StableSeconds) { $stepCompleteAt = $stepElapsed }
            }
            $stepStuck = $null
            if ($stepCompleteAt -and ($stepElapsed - $stepCompleteAt -ge $ExitSeconds)) {
                $stepStuck = $(if ($StopWhenComplete) { "ok, stopped after output" } else { "hung at exit (output complete at {0:0.0} s, no exit {1} s later)" -f $stepCompleteAt, $ExitSeconds })
            } elseif ($stepElapsed -ge $TimeoutSeconds) {
                $stepStuck = "timeout ({0} s, output {1})" -f $TimeoutSeconds, $(if ($Output) { if ($stepCompleteAt) { "complete" } elseif ($stepFingerprint) { "growing" } else { "missing" } } else { "not checked" })
            }
            if ($stepStuck) {
                if ($stepStuck -notlike "ok*") {
                    try {
                        if ($OnStuck) { & $OnStuck $stepProcess.Id } elseif ($env:GODOT_STUCK_HOOK) { & $env:GODOT_STUCK_HOOK $stepProcess.Id | Out-Null }
                    } catch { Write-Host "[godot] $Step evidence capture failed: $($_.Exception.Message)" }
                }
                Stop-ProcessTree $stepProcess
                $null = $stepProcess.WaitForExit(10000)
                $stepOutcome = $stepStuck; $stepOk = $StopWhenComplete -and ($stepStuck -like "ok*"); $stepCode = 124
                break
            }
        }
        $stepSeconds = $stepClock.Elapsed.TotalSeconds
        Write-Host ("[godot] {0} attempt {1}/{2}: {3} after {4:0.0} s" -f $Step, $stepAttempt, $Attempts, $stepOutcome, $stepSeconds)
        $stepLines = @(Get-Content $stepLog, "$stepLog.err" -ErrorAction SilentlyContinue)
        if ($KeepLogs) {
            New-Item -ItemType Directory -Force -Path $KeepLogs | Out-Null
            $stepStem = Join-Path $KeepLogs ("{0}-{1:yyyyMMdd-HHmmss-fff}-attempt{2}" -f ($Step -replace '[^\w.-]', '_'), (Get-Date), $stepAttempt)
            Copy-Item $stepLog "$stepStem.out" -ErrorAction SilentlyContinue
            Copy-Item "$stepLog.err" "$stepStem.err" -ErrorAction SilentlyContinue
        }
        Remove-Item $stepLog, "$stepLog.err" -ErrorAction SilentlyContinue
        $stepResult = [pscustomobject]@{ Ok = $stepOk; Code = $(if ($stepOk) { 0 } elseif ($stepCode) { $stepCode } else { 1 }); Outcome = $stepOutcome; Attempts = $stepAttempt; Seconds = $stepSeconds; Output = $stepLines }
        if ($stepOk) { return $stepResult }
        $stepHang = $stepCode -eq 124
        if (-not $stepHang -and -not $RetryOnExitCode) { break }
    }
    Write-Host ("[godot] {0} FAILED after {1} attempt(s): {2}" -f $Step, $stepResult.Attempts, $stepResult.Outcome)
    return $stepResult
}

# The import is complete when Godot has rewritten its filesystem cache and UID cache in .godot (both are saved at the end of the scan and reimport).
function Get-GodotImportStamp([string] $Client, [datetime] $Since) {
    $files = @(Join-Path $Client ".godot/editor/filesystem_cache10"; Join-Path $Client ".godot/uid_cache.bin")
    foreach ($file in $files) { if (-not (Test-Path $file) -or (Get-Item $file).LastWriteTimeUtc -lt $Since) { return $null } }
    return (($files | ForEach-Object { "{0}:{1}" -f (Get-Item $_).Length, (Get-Item $_).LastWriteTimeUtc.Ticks }) -join ",")
}

# Imports the project's resources (the first time, or with -Force to pick up new or changed theme files). Throws if every attempt fails.
function Import-GodotProject([string] $Godot, [string] $Client, [switch] $Force, [int] $TimeoutSeconds = 180, [int] $ExitSeconds = 30, [int] $Attempts = 3,
    [string] $KeepLogs = "", [scriptblock] $OnStuck = $null) {
    if (-not $Force -and (Test-Path (Join-Path $Client ".godot/uid_cache.bin"))) { return }
    $state = @{ Since = [datetime]::UtcNow }
    $result = Invoke-GodotStep -Godot $Godot -Step "import" -Arguments @("--headless", "--path", "`"$Client`"", "--import") -TimeoutSeconds $TimeoutSeconds `
        -ExitSeconds $ExitSeconds -Attempts $Attempts -RetryOnExitCode -KeepLogs $KeepLogs -OnStuck $OnStuck `
        -BeforeAttempt { $state.Since = [datetime]::UtcNow.AddSeconds(-1) } -Output { Get-GodotImportStamp $Client $state.Since }
    if (-not $result.Ok) { throw "The Godot import failed: $($result.Outcome) (after $($result.Attempts) attempts)." }
}
