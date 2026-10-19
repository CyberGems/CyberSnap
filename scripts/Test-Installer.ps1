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
# RDP stays inside this disposable VM. Do not enable a network-facing firewall rule.
Get-NetFirewallRule -Name 'RemoteDesktop-*' -ErrorAction SilentlyContinue | Disable-NetFirewallRule
New-NetFirewallRule -Name CyberSnapTestLoopbackRdp -DisplayName CyberSnapTestLoopbackRdp -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort 3389 -RemoteAddress 127.0.0.1,127.0.0.2 | Out-Null
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' fDenyTSConnections 0
Start-Service TermService
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
    StartupTest = 'Create a real Windows session over loopback RDP. Observe the HKCU Run application automatically started by Windows, and verify no automatic launch with startup disabled.'
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
        Add-LocalGroupMember -SID 'S-1-5-32-555' -Member $user
        $sid = (Get-LocalUser -Name $user).SID.Value
        & icacls.exe $testRoot /grant "*${sid}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not grant the test user access to its test files.' }
        [InstallerTestSession]::GrantDesktop($sid)
        $powershell = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
        $process = [InstallerTestSession]::Start($user, $password, $powershell,
            "-NoProfile -ExecutionPolicy Bypass -File `"$testRoot\Test-InstalledApp.ps1`" -Scenario $scenario", $testRoot)
        $deadline = [DateTime]::UtcNow.AddMinutes(7)
        while (-not $process.WaitForExit(30000)) {
            if (Test-Path "$testRoot\$scenario\worker.log") { Get-Content "$testRoot\$scenario\worker.log" -Tail 8 }
            if ([DateTime]::UtcNow -ge $deadline) { $process.Kill(); throw "Timed out: $scenario" }
        }
        if ($process.ExitCode -ne 0) { throw "Installation worker failed: $scenario ($($process.ExitCode))" }
        if (-not (Test-Path "$testRoot\$scenario\installed-state.json")) { throw 'The installer worker did not prepare the Windows logon test.' }
        $taskName = "CyberSnapInstallerVerification-$user"
        $taskUser = "$env:COMPUTERNAME\$user"
        $action = New-ScheduledTaskAction -Execute $powershell -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$testRoot\Test-InstalledApp.ps1`" -Scenario $scenario -Phase Logon"
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
        $principal = New-ScheduledTaskPrincipal -UserId $taskUser -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal | Out-Null
        & cmdkey.exe /generic:TERMSRV/127.0.0.2 "/user:$env:COMPUTERNAME\$user" "/pass:$password" | Out-Null
        if ($LASTEXITCODE) { throw 'Could not prepare credentials for the loopback test session.' }
        $password = $null
        $rdpPath = "$testRoot\$scenario.rdp"
        @(
            'full address:s:127.0.0.2', "username:s:$env:COMPUTERNAME\$user",
            'authentication level:i:0', 'enablecredsspsupport:i:1', 'prompt for credentials:i:0',
            'desktopwidth:i:1280', 'desktopheight:i:900', 'redirectclipboard:i:0', 'redirectprinters:i:0', 'audiomode:i:2'
        ) | Set-Content -LiteralPath $rdpPath
        $rdp = Start-Process "$env:WINDIR\System32\mstsc.exe" -ArgumentList "`"$rdpPath`"" -PassThru
        $resultFile = "$testRoot\$scenario\result.json"
        $deadline = [DateTime]::UtcNow.AddMinutes(7)
        while (-not (Test-Path -LiteralPath $resultFile)) {
            Start-Sleep -Seconds 15
            & quser.exe | Set-Content "$testRoot\$scenario\sessions.txt"
            $rdpMessages = @([InstallerTestSession]::VisibleWindows($rdp.Id)) + @([InstallerTestSession]::DialogMessages($rdp.Id))
            $rdpMessages |
                Tee-Object -FilePath "$testRoot\$scenario\rdp-windows.txt"
            Get-ScheduledTaskInfo -TaskName $taskName | ConvertTo-Json | Set-Content "$testRoot\$scenario\observer-task.json"
            if ($rdpMessages -match 'An authentication error|Your credentials did not work|cannot connect to another console session|Remote Desktop can.t connect') {
                throw "The loopback RDP connection failed: $($rdpMessages -join ' ')"
            }
            if (Test-Path "$testRoot\$scenario\worker.log") { Get-Content "$testRoot\$scenario\worker.log" -Tail 8 }
            if ([DateTime]::UtcNow -ge $deadline) {
                & quser.exe | Set-Content "$testRoot\$scenario\sessions.txt"
                [InstallerTestSession]::VisibleWindows($rdp.Id) | Set-Content "$testRoot\$scenario\rdp-windows.txt"
                throw "Timed out waiting for the real logon test: $scenario"
            }
        }
        if (Test-Path -LiteralPath $resultFile) {
            Get-Content -LiteralPath $resultFile | Write-Output
            $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
            if (-not $result.Passed) { $failures += "$scenario test failed: $($result.Failures -join '; ')" }
            & quser.exe | Set-Content "$testRoot\$scenario\sessions.txt"
            if ($result.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId) {
                & logoff.exe $result.SessionId
            }
        } else { $failures += "$scenario produced no result (exit $($process.ExitCode))." }
        if (-not $rdp.HasExited) { $rdp.Kill() }
        & cmdkey.exe /delete:TERMSRV/127.0.0.2 | Out-Null
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    if ($failures.Count) { throw ($failures -join "`n") }
}
finally {
    Copy-Item -Path "$testRoot\enabled", "$testRoot\disabled" -Destination $outputPath -Recurse -ErrorAction Continue
}
