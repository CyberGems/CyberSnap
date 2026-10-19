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
Copy-Item -LiteralPath "$PSScriptRoot\..\installer-diagnostics" -Destination "$testRoot\diagnostics" -Recurse
Add-Type -Path "$testRoot\WindowsSession.cs"
$policyPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$originalPolicy = Get-ItemProperty $policyPath
if ($originalPolicy.EnableLUA -ne 1) { throw 'This VM needs UAC enabled at boot. Do not infer UAC behavior from a VM with EnableLUA=0.' }
# Enable real prompts in the disposable VM; EnableLUA is already active, so no reboot is needed.
Set-ItemProperty $policyPath ConsentPromptBehaviorAdmin 5
Set-ItemProperty $policyPath ConsentPromptBehaviorUser 1
Set-ItemProperty $policyPath PromptOnSecureDesktop 1
$policy = Get-ItemProperty $policyPath
$environmentReport = [ordered]@{
    OperatingSystem = (Get-CimInstance Win32_OperatingSystem).Caption
    RunnerImage = $env:ImageVersion
    SourceCommit = $env:GITHUB_SHA
    PayloadSourceCommit = if (Test-Path "$(Split-Path $installerPath)/payload-source-commit.txt") { (Get-Content "$(Split-Path $installerPath)/payload-source-commit.txt" -Raw).Trim() } else { $env:GITHUB_SHA }
    InstallerSHA256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    EnableLUA = $policy.EnableLUA
    ConsentPromptBehaviorAdmin = $policy.ConsentPromptBehaviorAdmin
    ConsentPromptBehaviorUser = $policy.ConsentPromptBehaviorUser
    PromptOnSecureDesktop = $policy.PromptOnSecureDesktop
    StartupTest = 'Launch the exact HKCU Run command in the standard-user profile, without a Windows logoff/logon.'
    UacTest = 'UAC enabled with secure-desktop credential prompts. Assert asInvoker manifests, non-elevated standard-user tokens, no consent.exe, and non-administrative install/uninstall.'
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
        $deadline = [DateTime]::UtcNow.AddMinutes(7)
        while (-not $process.WaitForExit(30000)) {
            if (Test-Path "$testRoot\$scenario\worker.log") { Get-Content "$testRoot\$scenario\worker.log" -Tail 8 }
            if ([DateTime]::UtcNow -ge $deadline) { $process.Kill(); throw "Timed out: $scenario" }
        }
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
