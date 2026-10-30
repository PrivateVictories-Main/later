# A fresh runner session can sit on Windows' first-run privacy page, which holds the foreground and the keyboard, so the UI tests'
# keys never reach the app. This presses its Accept button until the page is gone. Prints only how many pages it dismissed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$byId = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), 'OobeSettingsAcceptButton'
$dismissed = 0
for ($i = 0; $i -lt 10; $i++) {
    $button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId)
    if ($null -eq $button) { Start-Sleep -Seconds 2; $button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId) }
    if ($null -eq $button) { break }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $dismissed++
    Start-Sleep -Seconds 3
}
"first-run pages dismissed: $dismissed"
