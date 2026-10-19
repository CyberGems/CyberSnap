param([ValidateSet('enabled', 'disabled')][string]$Scenario)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$testRoot = $PSScriptRoot
$output = Join-Path $testRoot $Scenario
New-Item -ItemType Directory -Path $output -Force | Out-Null
Start-Transcript -Path "$output\worker.log" | Out-Null
$failures = [Collections.Generic.List[string]]::new()
$checks = [Collections.Generic.List[string]]::new()
$processes = [Collections.Generic.List[object]]::new()
$installedExe = $null
$settingsDirectory = $null
function Assert-Test([bool]$Condition, [string]$Message) {
    if ($Condition) { $checks.Add($Message); Write-Output "PASS: $Message" }
    else { $failures.Add($Message); Write-Output "FAIL: $Message" }
}
function Get-StartupCommand {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try { if ($key) { $key.GetValue('CyberSnap') } } finally { if ($key) { $key.Dispose() } }
}
function Invoke-InstallerProcess([string]$Path, [string]$Arguments) {
    $manifest = [InstallerTestSession]::Manifest($Path)
    $manifest | Set-Content "$output\$([IO.Path]::GetFileName($Path)).manifest.xml"
    Assert-Test ($manifest -match 'requestedExecutionLevel\s+level="asInvoker"') "Executable manifest requests asInvoker: $([IO.Path]::GetFileName($Path))"
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru
    $elevated = [InstallerTestSession]::IsElevated($process)
    $processes.Add([ordered]@{ Path = $Path; PID = $process.Id; Elevated = $elevated })
    Assert-Test (-not $elevated) "Process uses a non-elevated token: $([IO.Path]::GetFileName($Path))"
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $consentSeen = $false
    while (-not $process.WaitForExit(200)) {
        if (Get-Process consent -ErrorAction SilentlyContinue) { $consentSeen = $true }
        if ([DateTime]::UtcNow -ge $deadline) { $process.Kill(); throw "Process timed out: $Path" }
    }
    Assert-Test (-not $consentSeen) "No consent.exe process appeared: $([IO.Path]::GetFileName($Path))"
    Assert-Test ($process.ExitCode -eq 0) "Process exits successfully: $([IO.Path]::GetFileName($Path)) ($($process.ExitCode))"
}
function Stop-TestApp {
    if ($installedExe) {
        Get-Process CyberSnap -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe } | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
}
function Save-StartupDiagnostics([Diagnostics.Process]$App) {
    Get-CimInstance Win32_Process -Filter "Name='CyberSnap.exe'" |
        Select-Object ProcessId, ParentProcessId, ExecutablePath, CommandLine |
        ConvertTo-Json | Set-Content "$output\app-processes.json"
    Get-ChildItem Env: | Where-Object { $_.Name -match '^(USERPROFILE|APPDATA|LOCALAPPDATA|TEMP|TMP|DOTNET.*)$' } |
        ConvertTo-Json | Set-Content "$output\profile-environment.json"
    $App.Modules | Select-Object ModuleName, FileName | ConvertTo-Json | Set-Content "$output\app-modules.json"
    $diagnostics = Start-Process "$testRoot\diagnostics\dotnet-stack.exe" -ArgumentList "report -p $($App.Id)" -PassThru -NoNewWindow `
        -RedirectStandardOutput "$output\managed-stacks.txt" -RedirectStandardError "$output\managed-stacks-errors.txt"
    if (-not $diagnostics.WaitForExit(30000)) { $diagnostics.Kill() }
}
try {
    Add-Type -Path "$testRoot\WindowsSession.cs"
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    Assert-Test (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole('Administrators')) 'Test account is not an administrator'
    Assert-Test (-not [InstallerTestSession]::IsElevated([Diagnostics.Process]::GetCurrentProcess())) 'Test worker is not elevated'
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    $roaming = [Environment]::GetFolderPath('ApplicationData')
    Assert-Test ($local -like "C:\Users\CyberSnapTest*\AppData\Local") 'LocalAppData belongs to the disposable account'
    $installDir = Join-Path $local 'Programs\CyberSnap'
    $installedExe = Join-Path $installDir 'CyberSnap.exe'
    $settingsDirectory = Join-Path $roaming 'CyberSnap'
    Assert-Test (-not (Test-Path -LiteralPath $settingsDirectory)) 'App profile is initially empty'
    # Only bypass onboarding and update notifications. Deliberately leave StartWithWindows unset.
    New-Item -ItemType Directory -Path $settingsDirectory | Out-Null
    $language = if ($Scenario -eq 'enabled') { 'en' } else { 'es' }
    @{ HasCompletedSetup = $true; HasSeenTrayPinTip = $true; AutoCheckForUpdates = $false; MuteSounds = $true; InterfaceLanguage = $language } |
        ConvertTo-Json | Set-Content "$settingsDirectory\settings.json" -Encoding UTF8
    $tasks = if ($Scenario -eq 'enabled') { 'startup,desktopicon,editordesktopicon' } else { '!startup,desktopicon,editordesktopicon' }
    $installerLanguage = if ($Scenario -eq 'enabled') { 'english' } else { 'spanish' }
    Invoke-InstallerProcess "$testRoot\Setup.exe" "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=$installerLanguage /TASKS=`"$tasks`" /LOG=`"$output\install.log`""
    Assert-Test (Test-Path -LiteralPath $installedExe) 'Executable is installed at the default per-user location'
    Assert-Test (Test-Path -LiteralPath "$installDir\ffmpeg.exe") 'FFmpeg is installed beside the application'
    $installLog = Get-Content "$output\install.log" -Raw
    Assert-Test ($installLog -match 'Administrative install mode: No') 'Inno confirms non-administrative installation'
    $startupCommand = Get-StartupCommand
    if ($Scenario -eq 'enabled') {
        Assert-Test ($startupCommand -eq "`"$installedExe`"") 'Startup command points to the installed executable with correct quoting'
    } else {
        Assert-Test ([string]::IsNullOrEmpty($startupCommand)) 'Unselected startup task leaves no Run entry, including after app launch'
    }
    $association = Get-ItemProperty 'HKCU:\Software\Classes\CyberSnap.Project\shell\open\command'
    Assert-Test ($association.'(default)' -eq "`"$installedExe`" `"%1`"") 'Project file association is per-user and correctly quoted'
    $programs = [Environment]::GetFolderPath('Programs')
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    foreach ($directory in @($programs, $desktop)) {
        foreach ($name in @('CyberSnap', 'CyberSnap Editor')) {
            Assert-Test (Test-Path -LiteralPath "$directory\$name.lnk") "Shortcut exists: $directory\$name.lnk"
        }
    }
    # Inno currently starts the app even in silent mode. End it before exercising Run.
    Stop-TestApp
    $launchPath = if ($Scenario -eq 'enabled' -and $startupCommand -match '^"([^"]+)"$') { $Matches[1] } else { $installedExe }
    $app = Start-Process -FilePath $launchPath -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $windows = @()
    do {
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        if ($app.HasExited) { throw "Installed application exited early ($($app.ExitCode))." }
        $windows = @([InstallerTestSession]::VisibleWindows($app.Id))
    } while ($windows -notcontains 'CyberSnap Quick Panel' -and [DateTime]::UtcNow -lt $deadline)
    $windows | ConvertTo-Json | Set-Content "$output\windows.json"
    if ($windows -notcontains 'CyberSnap Quick Panel') { Save-StartupDiagnostics $app }
    Assert-Test ($windows -contains 'CyberSnap Quick Panel') 'Installed app shows its Quick Panel after launching the startup command'
    Assert-Test (-not [InstallerTestSession]::IsElevated($app)) 'Installed app runs without elevation'
    Start-Sleep -Seconds 5
    Assert-Test (-not $app.HasExited) 'Installed app remains running after startup'
    $startupAfterLaunch = Get-StartupCommand
    Assert-Test (($Scenario -eq 'enabled' -and $startupAfterLaunch -eq "`"$installedExe`"") -or
        ($Scenario -eq 'disabled' -and [string]::IsNullOrEmpty($startupAfterLaunch))) 'App preserves the installer startup selection'
    $uninstallEntries = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' |
        Get-ItemProperty | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match '^CyberSnap(?: version .*)?$' })
    $uninstallEntries | Select-Object PSChildName, UninstallString | ConvertTo-Json | Set-Content "$output\uninstall-entries.json"
    Assert-Test ($uninstallEntries.Count -eq 1) 'Windows lists CyberSnap exactly once in installed applications'
    Stop-TestApp
    $settingsFile = "$settingsDirectory\settings.json"
    $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    Assert-Test ($settings.StartWithWindows -eq ($Scenario -eq 'enabled')) 'Installer startup choice is saved in application settings'
    $pendingKey = Get-ItemProperty 'HKCU:\Software\CyberGems\CyberSnap\Installer' -ErrorAction SilentlyContinue
    Assert-Test (-not ($pendingKey -and $pendingKey.PSObject.Properties['StartWithWindows'])) 'Installer preference is consumed after it is saved'
    # Simulate a later preference saved by Settings, then verify the normal app startup applies it.
    $settings.StartWithWindows = $Scenario -ne 'enabled'
    $settings | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $settingsFile -Encoding UTF8
    $app = Start-Process -FilePath $installedExe -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 500
        if ($app.HasExited) { throw 'Application exited while checking a changed startup preference.' }
        $windows = @([InstallerTestSession]::VisibleWindows($app.Id))
    } while ($windows -notcontains 'CyberSnap Quick Panel' -and [DateTime]::UtcNow -lt $deadline)
    Assert-Test ($windows -contains 'CyberSnap Quick Panel') 'App restarts after changing its startup preference'
    $changedCommand = Get-StartupCommand
    Assert-Test (($Scenario -eq 'disabled' -and $changedCommand -eq "`"$installedExe`"") -or
        ($Scenario -eq 'enabled' -and [string]::IsNullOrEmpty($changedCommand))) 'Later user preference overrides the previous installer choice'
}
catch { $failures.Add($_.ToString()); Write-Output $_.ToString() }
finally {
    try {
        Stop-TestApp
        if ($installedExe) {
            $uninstaller = Join-Path (Split-Path -Parent $installedExe) 'unins000.exe'
            if (Test-Path -LiteralPath $uninstaller) {
                Invoke-InstallerProcess $uninstaller "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$output\uninstall.log`""
                Start-Sleep -Seconds 3
                Assert-Test (-not (Test-Path -LiteralPath $installedExe)) 'Uninstaller removes the installed executable'
                Assert-Test (-not (Test-Path -LiteralPath (Split-Path -Parent $installedExe))) 'Uninstaller removes the installation directory'
                Assert-Test ([string]::IsNullOrEmpty((Get-StartupCommand))) 'Uninstaller removes the startup entry'
                Assert-Test (-not (Test-Path 'HKCU:\Software\Classes\CyberSnap.Project')) 'Uninstaller removes the project association'
                $remaining = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' |
                    Get-ItemProperty | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match '^CyberSnap(?: version .*)?$' })
                Assert-Test ($remaining.Count -eq 0) 'Uninstaller leaves no CyberSnap installed-app entry'
                foreach ($directory in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('DesktopDirectory'))) {
                    foreach ($name in @('CyberSnap', 'CyberSnap Editor')) {
                        Assert-Test (-not (Test-Path -LiteralPath "$directory\$name.lnk")) "Uninstaller removes shortcut: $directory\$name.lnk"
                    }
                }
            } else { $failures.Add('Inno uninstaller was not installed.') }
        }
    }
    catch { $failures.Add("Uninstall verification: $_") }
    if ($settingsDirectory -and (Test-Path -LiteralPath "$settingsDirectory\logs")) {
        Copy-Item -LiteralPath "$settingsDirectory\logs" -Destination "$output\app-logs" -Recurse
    }
    [ordered]@{ Scenario = $Scenario; Passed = ($failures.Count -eq 0); Checks = @($checks); Failures = @($failures); Processes = @($processes) } |
        ConvertTo-Json -Depth 6 | Set-Content "$output\result.json" -Encoding UTF8
    Stop-Transcript | Out-Null
}
if ($failures.Count) { exit 1 }
