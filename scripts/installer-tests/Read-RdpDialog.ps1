param([int]$RdpProcessId, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'RDP diagnostics require the disposable test VM.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $RdpProcessId)
$windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $condition)
$messages = foreach ($window in $windows) {
    $controls = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    foreach ($control in $controls) {
        $current = $control.Current
        # Only diagnostic labels and buttons in this RDP client's windows, never edit values.
        if (-not $current.IsPassword -and $current.ControlType -in @([Windows.Automation.ControlType]::Text, [Windows.Automation.ControlType]::Button)) {
            $current.Name
        }
    }
}
@($messages) | ConvertTo-Json | Set-Content -LiteralPath $OutputPath
