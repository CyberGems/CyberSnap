<#
.SYNOPSIS
Runs the verify-capture-tools check steps locally, writing one log per step.

.DESCRIPTION
Mirrors the eight dotnet-run steps of .github/workflows/verify-capture-tools.yml,
building the smoke project once and then running each step into its own log file. A
summary.txt records the outcome, duration and check count of every step.

.PARAMETER Configuration
Build configuration for the smoke project. Defaults to Release, which keeps the run
independent of a Debug build the desktop app may have locked.

.PARAMETER Only
Restricts the run to the named steps, for example -Only preview-margin,preview-outline.

.PARAMETER LogDirectory
Where the per-step logs and summary.txt go. Defaults to a timestamped folder in the
temporary directory.

.PARAMETER Background
Starts the same run in a hidden process and returns immediately, so a long run does
not block the terminal. Follow the summary file to watch progress.

.EXAMPLE
./scripts/Invoke-CiSteps.ps1 -Background

.EXAMPLE
./scripts/Invoke-CiSteps.ps1 -Only preview-margin,preview-outline
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string[]]$Only,
    [string]$LogDirectory,
    [switch]$Background
)

$ErrorActionPreference = 'Stop'
# Native commands report failures through $LASTEXITCODE; they must not throw here.
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = 'tests/CyberSnap.ResultTabs.Smoke'

# Keep these in the workflow's order so a local run reports in the same sequence as CI.
$allSteps = @(
    @{ Name = 'capture-tool-regressions'; Flag = '--capture-tool-regressions-only' },
    @{ Name = 'preview-outline';          Flag = '--preview-outline-only' },
    @{ Name = 'preview-margin';           Flag = '--preview-margin-only' },
    @{ Name = 'preview-actions';          Flag = '--preview-actions-only' },
    @{ Name = 'widget-cursor-following';  Flag = '--widget-follow-cursor-only' },
    @{ Name = 'widget-preferences';       Flag = '--widget-preferences-only' },
    @{ Name = 'widget-monitors';          Flag = '--widget-monitors-only' },
    @{ Name = 'widget-active';            Flag = '--widget-active-only' }
)

$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
# @() keeps a single match a one-element list instead of the hashtable itself.
$steps = @(if ($Only) { $allSteps | Where-Object { $Only -contains $_.Name } } else { $allSteps })
if (-not $steps) {
    throw "No step matches -Only '$($Only -join ', ')'. Known steps: $(($allSteps | ForEach-Object { $_.Name }) -join ', ')."
}

if (-not $LogDirectory) {
    $LogDirectory = Join-Path ([IO.Path]::GetTempPath()) ('cybersnap-ci-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null

if ($Background) {
    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { (Get-Command pwsh).Source }
        else { (Get-Command powershell).Source }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-Configuration', $Configuration, '-LogDirectory', $LogDirectory)
    if ($Only) { $arguments += @('-Only', ($Only -join ',')) }
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Set-Content -Path (Join-Path $LogDirectory 'pid.txt') -Value $process.Id
    Write-Host "Running $($steps.Count) check step(s) in the background (PID $($process.Id))."
    Write-Host "Logs: $LogDirectory"
    Write-Host "Watch: Get-Content '$LogDirectory\summary.txt' -Wait -Tail 20"
    return
}

function Format-Seconds([TimeSpan]$elapsed) {
    # Invariant so the summary columns keep their alignment under a comma decimal separator.
    return [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0,7:0.0}s', $elapsed.TotalSeconds)
}

$summaryPath = Join-Path $LogDirectory 'summary.txt'
Set-Content -Path $summaryPath -Value @(
    "CyberSnap verify-capture-tools, $Configuration"
    "Started:    $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "Repository: $repoRoot"
    "Logs:       $LogDirectory"
    "Steps:      $(($steps | ForEach-Object Name) -join ', ')"
    ''
)

Push-Location $repoRoot
try {
    $buildLog = Join-Path $LogDirectory 'build.log'
    $buildWatch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet build $project --configuration $Configuration *> $buildLog
    $buildExit = $LASTEXITCODE
    $buildWatch.Stop()
    if ($buildExit -ne 0) {
        Add-Content $summaryPath @("FAIL  build ($(Format-Seconds $buildWatch.Elapsed)), see build.log", '')
        Get-Content $buildLog -Tail 20 | Add-Content $summaryPath
        Write-Host "Build failed. See $buildLog"
        exit 1
    }
    Add-Content $summaryPath "PASS  build ($(Format-Seconds $buildWatch.Elapsed))"
    Add-Content $summaryPath ''

    $failures = @()
    foreach ($step in $steps) {
        $logPath = Join-Path $LogDirectory ($step.Name + '.log')
        Write-Host "Running $($step.Name)..."
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & dotnet run --project $project --configuration $Configuration --no-build -- $step.Flag *> $logPath
        $exitCode = $LASTEXITCODE
        $watch.Stop()

        $text = Get-Content $logPath -Raw -ErrorAction SilentlyContinue
        $checks = if ($text -match 'PASS: (\d+) [^\r\n]*check') { [int]$Matches[1] } else { 0 }
        $seconds = Format-Seconds $watch.Elapsed
        if ($exitCode -eq 0) {
            Add-Content $summaryPath ("PASS  {0,-24} {1}  checks={2}" -f $step.Name, $seconds, $checks)
        }
        else {
            $failures += $step.Name
            Add-Content $summaryPath ("FAIL  {0,-24} {1}  exit={2}  log={3}.log" -f $step.Name, $seconds, $exitCode, $step.Name)
            # Keep the first failure line in the summary so the reason is visible without opening the log.
            $reason = ($text -split "`r?`n" | Where-Object { $_ -match 'Exception|FAIL:' } | Select-Object -First 1)
            if ($reason) { Add-Content $summaryPath ('      ' + $reason.Trim()) }
        }
    }

    Add-Content $summaryPath ''
    $finished = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    if ($failures.Count -eq 0) {
        Add-Content $summaryPath "Result: PASS, all $($steps.Count) step(s) passed. Finished: $finished"
        Write-Host "All $($steps.Count) step(s) passed. Summary: $summaryPath"
        exit 0
    }
    Add-Content $summaryPath "Result: FAIL, $($failures.Count) of $($steps.Count) step(s) failed ($($failures -join ', ')). Finished: $finished"
    Write-Host "Failed: $($failures -join ', '). Summary: $summaryPath"
    exit 1
}
finally {
    Pop-Location
}
