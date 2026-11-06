#Requires -RunAsAdministrator
# Manual UI walkthrough of the #82 finish-page choice on the Smart Client machine.
# The interactive dialog set cannot be driven unattended (UIA-on-msiexec precedent),
# so this script stages the MSI, launches the wizard, and tells you what to click;
# it then journals MilestoneEventServerService state so the outcome is provable.
param(
    [Parameter(Mandatory = $true)][string] $MsiPath,
    [string] $TmpDir = 'C:\Users\joshooaj\AppData\Local\Temp\opencode'
)
$ErrorActionPreference = 'Stop'
$journal = Join-Path $TmpDir 'wces-ui-poll.txt'
$log = Join-Path $TmpDir 'wces-ui-install.log'
Remove-Item $journal, $log -ErrorAction SilentlyContinue
$job = Start-Job -ArgumentList $journal {
    param($j)
    1..2400 | ForEach-Object {
        (Get-Service MilestoneEventServerService -ErrorAction SilentlyContinue).Status.ToString() + ' @' + (Get-Date -Format 'HH:mm:ss') | Out-File -Append $j
        Start-Sleep -Milliseconds 250
    }
}
Write-Host 'Wizard starting. On the "Event Server options" page:'
Write-Host '  - the "Restart the Event Server now?" checkbox must be PRESENT and CHECKED by default,'
Write-Host '  - the warning text must mention restarting every Smart Client afterwards.'
Write-Host 'Tick or clear it as you choose, then click Install. The journal below proves the outcome.'
$p = Start-Process msiexec -ArgumentList '/i', "`"$MsiPath`"", "/l*v", "`"$log`"" -Wait -PassThru
Stop-Job $job; Remove-Job $job
Write-Host "msiexec exit: $($p.ExitCode)"
Write-Host "service now: $((Get-Service MilestoneEventServerService -ErrorAction SilentlyContinue).Status)"
Write-Host '--- journal (deduped):'
Get-Content $journal | ForEach-Object { $s = ($_ -split ' @')[0]; if ($s -ne $prev) { Write-Host $_; $prev = $s } }
Write-Host '--- dialog evidence in the log:'
Select-String $log -Pattern 'Doing action: (Stop|Restart)EventServerService|Skipping action: (Stop|Restart)EventServerService|EventServerOptionsDlg|PROPERTY CHANGE: Adding RESTART_EVENT_SERVER' |
    Select-Object -First 12 | ForEach-Object { Write-Host $_.Line }
