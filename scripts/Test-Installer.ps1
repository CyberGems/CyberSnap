[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# This script creates local accounts and installs software. Never run it on a workstation.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer integration tests require a disposable GitHub-hosted Windows VM.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole('Administrators')) {
    throw 'Provisioning disposable standard users requires an administrator on the test VM.'
}
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$testRoot = 'C:\CyberSnapInstallerTest'
if (Test-Path -LiteralPath $testRoot) { throw "Test directory already exists: $testRoot" }
New-Item -ItemType Directory -Path $testRoot | Out-Null
Copy-Item -LiteralPath $installerPath -Destination "$testRoot\Setup.exe"
Copy-Item -Path "$PSScriptRoot\installer-tests\*" -Destination $testRoot
Add-Type -Path "$testRoot\WindowsSession.cs"
$policy = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$environmentReport = [ordered]@{
    OperatingSystem = (Get-CimInstance Win32_OperatingSystem).Caption
    RunnerImage = $env:ImageVersion
    SourceCommit = $env:GITHUB_SHA
    InstallerSHA256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    EnableLUA = $policy.EnableLUA
    ConsentPromptBehaviorAdmin = $policy.ConsentPromptBehaviorAdmin
    StartupTest = 'Launch the exact HKCU Run command in the standard-user profile, without a Windows logoff/logon.'
    UacTest = 'Assert non-elevated standard-user tokens and successful non-administrative install/uninstall. Hosted runner UAC policy is recorded separately.'
}
$environmentReport | ConvertTo-Json | Set-Content "$outputPath\environment.json"
$failures = @()
try {
    foreach ($scenario in @('enabled', 'disabled')) {
        $user = if ($scenario -eq 'enabled') { 'CyberSnapTestOn' } else { 'CyberSnapTestOff' }
        $password = [Guid]::NewGuid().ToString('N') + 'aA1!'
        New-LocalUser -Name $user -Password (ConvertTo-SecureString $password -AsPlainText -Force) -AccountNeverExpires | Out-Null
        Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $user
        $sid = (Get-LocalUser -Name $user).SID.Value
        & icacls.exe $testRoot /grant "*${sid}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not grant the test user access to its test files.' }
        [InstallerTestSession]::GrantDesktop($sid)
        $powershell = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
        $process = [InstallerTestSession]::Start($user, $password, $powershell,
            "-NoProfile -ExecutionPolicy Bypass -File `"$testRoot\Test-InstalledApp.ps1`" -Scenario $scenario", $testRoot)
        $password = $null
        if (-not $process.WaitForExit(420000)) { $process.Kill(); throw "Timed out: $scenario" }
        $resultFile = "$testRoot\$scenario\result.json"
        if (Test-Path -LiteralPath $resultFile) {
            Get-Content -LiteralPath $resultFile | Write-Output
            $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
            if (-not $result.Passed) { $failures += "$scenario test failed: $($result.Failures -join '; ')" }
        } else { $failures += "$scenario produced no result (exit $($process.ExitCode))." }
        if ($process.ExitCode -ne 0) { $failures += "$scenario worker exited with $($process.ExitCode)." }
    }
    if ($failures.Count) { throw ($failures -join "`n") }
}
finally {
    Copy-Item -Path "$testRoot\enabled", "$testRoot\disabled" -Destination $outputPath -Recurse -ErrorAction Continue
}
