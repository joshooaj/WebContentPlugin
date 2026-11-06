<#
.SYNOPSIS
  DEV/TEST shortcut (#68): deploy (or update) the Web Content plugin drop on the machine
  running the XProtect Event Server, so the server-side proxy component (fetcher, leaf
  issuer, tunnel server) is loaded there. Ticket #59. The SUPPORTED deployment path on
  that machine is the release MSI run THERE (README + verification-matrix IN-3); a
  file-copy deploy leaves untracked strays that shadow a later MSI upgrade at equal
  file version, and it never sets up proxy support (the CA setup is separate either way).

.DESCRIPTION
  The proxied-workspace feature is both-machine: Smart Client needs the plugin
  for the loopback forwarding listener, and the XProtect EVENT SERVER (not the
  Management Server) needs the same drop to host the proxy fetcher. The plugin
  definition declares the "Service" load environment, and the Event Server only
  loads MIP plugins at service start, so a deployment is: stop the service,
  copy the drop into a valid MIP root, verify, start the service.

  This script performs exactly the repo's documented deployment procedure:
  1. Pre-check the LOCAL source drop: WebContent.dll present and plugin.def
     declaring a load environment that includes "Service". A drop whose def
     lacks "Service" would copy cleanly and then silently do nothing on the
     Event Server - it is refused up front.
  2. Open a WinRM session (explicit credentials: plain negotiate/Kerberos does
     not authenticate on the lab host, an explicit -Credential does).
  3. Stop MilestoneEventServerService and WAIT for the Stopped state - the
     service stop is slow, and copying while it still holds WebContent.dll
     fails on the locked file.
  4. Copy the drop into the MIP root (default: the setup.wxs INSTALLFOLDER,
     C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin; the other
     valid root is Milestone\MIPPlugins for OEM installs). The copy retries
     briefly because the service releases WebContent.dll a moment after it
     reports Stopped (observed live during the #60 rollout).
  5. Verify every copied file by SHA-256 HASH against the source - never by
     timestamp (Copy-Item preserves LastWriteTime, so a good copy looks stale
     and a partial copy looks fresh).
  6. Start the service and wait for Running.

  Consequences every rollout must account for (printed at the end too):
  - Restarting the Event Server disposes every connected client's
    MessageCommunication. Any Smart Client that was connected BEFORE this run
    must be restarted afterwards, or every proxy fetch dies with
    "Call to TransmitMessage after call to Dispose?".
  - The one-time certificate-authority setup for https targets is
    provision-event-server-authority.ps1 (README), run after this deployment.

  The release MSI run ON the Event Server machine is the SUPPORTED path (#68): it does
  the same placement and - since #82 - drives the service stop/wait/restart itself for
  existing installs. This script is the remote, repeatable form kept only as a dev/test
  shortcut for lab machines.

.PARAMETER ComputerName
  The machine running the XProtect Event Server.

.PARAMETER Credential
  Explicit credentials for the remote WinRM session (for example the jhadmin
  account of the VMS host). Omit only when the current identity is already
  trusted on that machine.

.PARAMETER SourceDrop
  Local directory holding the plugin build drop (the msbuild output, or the
  contents of the MSI's INSTALLFOLDER). Defaults to output\WebContent under
  the repository root.

.PARAMETER InstallFolder
  Target folder on the Event Server machine. Must be a valid MIP root; the
  default matches the installer's INSTALLFOLDER.

.PARAMETER ServiceName
  The Event Server service name. Default: MilestoneEventServerService.

.EXAMPLE
  .\scripts\deploy-event-server-plugin.ps1 -ComputerName vms.joshooaj.com -Credential (Get-Credential)

  Remote deploy from a dev machine; supply the VMS admin credentials when
  prompted.

.EXAMPLE
  .\scripts\deploy-event-server-plugin.ps1 -ComputerName localhost

  Local deploy on the Event Server machine itself (elevated session).

.NOTES
  The copy retries for about 50 seconds because the service process releases
  WebContent.dll a moment after the service reports Stopped. If the copy still
  fails, the script throws with the service left Stopped and the install folder
  possibly half-copied; re-running the script is the documented recovery.
  Non-interactive runs need -Confirm:$False: PowerShell 7.6.9 throws a
  NullReferenceException when a High-impact ShouldProcess confirmation has no
  console to answer it.

.OUTPUTS
  Status lines; throws on any pre-check, copy, or hash-verification failure.
#>

[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ComputerName,

    [System.Management.Automation.PSCredential] $Credential,

    [string] $SourceDrop = (Join-Path $PSScriptRoot '..\output\WebContent'),

    [string] $InstallFolder = 'C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin',

    [string] $ServiceName = 'MilestoneEventServerService'
)

$ErrorActionPreference = 'Stop'

# --- Phase 1: local pre-checks, before anything is touched remotely. ----------

$SourceDrop = (Resolve-Path $SourceDrop).Path
foreach ($required in @('WebContent.dll', 'plugin.def')) {
    if (-not (Test-Path -Path (Join-Path $SourceDrop $required))) {
        throw "Source drop '$SourceDrop' is missing $required - build the plugin first (msbuild src/WebContent/WebContent.csproj)."
    }
}

# The def is what makes the Event Server load the drop at all. An old drop
# without the Service environment copies fine and answers nothing: refuse it.
$pluginDef = Get-Content -Path (Join-Path $SourceDrop 'plugin.def') -Raw
if ($pluginDef -notmatch 'env="[^"]*Service[^"]*"') {
    throw 'plugin.def in the source drop does not declare the Service load environment; this drop would never load inside the Event Server.'
}

$sourceFiles = Get-ChildItem -Path $SourceDrop -File -Recurse
if ($sourceFiles.Count -eq 0) {
    throw "Source drop '$SourceDrop' contains no files."
}
$sourceHashes = @{}
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($SourceDrop.Length).TrimStart('\', '/')
    $sourceHashes[$relative] = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
}
Write-Output ("Source drop: {0} ({1} file(s), SHA-256 computed)" -f $SourceDrop, $sourceFiles.Count)

$sessionParameters = @{
    ComputerName = $ComputerName
}
if ($PSBoundParameters.ContainsKey('Credential') -and $null -ne $Credential) {
    $sessionParameters['Credential'] = $Credential
}

# --- Phase 2: the remote change, under ShouldProcess. -------------------------

$whatIfTarget = "$ComputerName`: service '$ServiceName' + '$InstallFolder'"
if (-not $PSCmdlet.ShouldProcess($whatIfTarget, 'stop service, copy plugin drop, verify hashes, start service')) {
    return
}

$session = New-PSSession @sessionParameters
try {
    # Remote pre-check: the service must exist there; a wrong host is caught
    # before any stop/copy.
    $serviceInfo = Invoke-Command -Session $session -ScriptBlock {
        $service = Get-Service -Name $Using:ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            return $null
        }
        [PSCustomObject]@{
            Name   = $service.Name
            Status = $service.Status
        }
    }
    if ($null -eq $serviceInfo) {
        throw "Service '$ServiceName' not found on $ComputerName - is this the Event Server machine?"
    }
    Write-Output ("Service on {0}: {1} ({2})" -f $ComputerName, $serviceInfo.Name, $serviceInfo.Status)

    # Stop and WAIT for Stopped: the Event Server stop is slow, and the copy
    # fails on the locked WebContent.dll while it is still running.
    Invoke-Command -Session $session -ScriptBlock {
        Stop-Service -Name $Using:ServiceName -ErrorAction Stop
    }
    $stopped = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        # ToString() remotely: WinRM can deserialize the ServiceControllerStatus
        # enum as its underlying Int32, and a numeric 1 never equals 'Stopped'.
        $state = Invoke-Command -Session $session -ScriptBlock {
            (Get-Service -Name $Using:ServiceName).Status.ToString()
        }
        if ($state -eq 'Stopped') {
            $stopped = $true
            break
        }
        Start-Sleep -Seconds 3
    }
    if (-not $stopped) {
        throw "Service '$ServiceName' on $ComputerName did not reach Stopped within 120 s; the drop was NOT copied - start the service again and investigate."
    }
    Write-Output 'Service stopped.'

    Invoke-Command -Session $session -ScriptBlock {
        if (-not (Test-Path -Path $Using:InstallFolder)) {
            New-Item -Path $Using:InstallFolder -ItemType Directory -Force | Out-Null
        }
    }

    # The service state flips to Stopped slightly BEFORE the last thread lets go of
    # WebContent.dll (observed live, #60 rollout): a single copy attempt fails on the
    # lock even though the stop completed. Retry briefly instead of failing the run.
    $maxCopyAttempts = 10
    $copied = $false
    $copyError = $null
    $copyAttempts = 0
    for ($attempt = 1; $attempt -le $maxCopyAttempts; $attempt++) {
        $copyAttempts = $attempt
        try {
            Copy-Item -Path (Join-Path $SourceDrop '*') -Destination $InstallFolder -ToSession $session -Recurse -Force -ErrorAction Stop
            $copied = $true
            break
        } catch {
            $copyError = $_
            if ($attempt -lt $maxCopyAttempts) {
                Write-Warning "Copy attempt $attempt failed (the service process may still be releasing the dll): $($_.Exception.Message)"
                Start-Sleep -Seconds 5
            }
        }
    }
    if (-not $copied) {
        throw "Drop copy to $ComputerName failed after $copyAttempts attempts - a process other than the service still holds the files: $($copyError.Exception.Message)`n$ServiceName is left Stopped and $InstallFolder may hold a half-copied drop. Re-running this script is the recovery: it stops, copies, verifies, and starts again."
    }
    Write-Output "Drop copied to $ComputerName`:$InstallFolder (attempt $copyAttempts)."

    # --- Phase 3: hash verification, per file, against the local source. ------
    # Hashes, not timestamps: Copy-Item carries the source LastWriteTime over,
    # so a timestamp can never tell a good copy from a stale one.
    $remoteHashes = Invoke-Command -Session $session -ScriptBlock {
        $result = @{}
        foreach ($file in (Get-ChildItem -Path $Using:InstallFolder -File -Recurse)) {
            $relative = $file.FullName.Substring($Using:InstallFolder.Length).TrimStart('\', '/')
            $result[$relative] = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
        }
        $result
    }

    $mismatches = @()
    foreach ($relative in $sourceHashes.Keys) {
        if (-not $remoteHashes.ContainsKey($relative) -or $remoteHashes[$relative] -ne $sourceHashes[$relative]) {
            $mismatches += $relative
        }
    }
    foreach ($relative in $remoteHashes.Keys) {
        if (-not $sourceHashes.ContainsKey($relative)) {
            $mismatches += "$relative (extra on server)"
        }
    }
    if ($mismatches.Count -gt 0) {
        throw ("Hash verification FAILED on {0}: {1}" -f $ComputerName, ($mismatches -join '; '))
    }
    Write-Output ("Hash verification passed: {0} file(s) identical on {1}." -f $sourceHashes.Count, $ComputerName)

    # Start and wait for Running so the caller sees a loaded, live service.
    Invoke-Command -Session $session -ScriptBlock {
        Start-Service -Name $Using:ServiceName -ErrorAction Stop
    }
    $running = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $state = Invoke-Command -Session $session -ScriptBlock {
            (Get-Service -Name $Using:ServiceName).Status.ToString()
        }
        if ($state -eq 'Running') {
            $running = $true
            break
        }
        Start-Sleep -Seconds 3
    }
    if (-not $running) {
        throw "Service '$ServiceName' on $ComputerName did not reach Running within 60 s; check its logs."
    }
    Write-Output 'Service running; the Event Server loaded the plugin drop at startup.'
} finally {
    Remove-PSSession -Session $session
}

Write-Output ''
Write-Output 'Post-deployment checks:'
Write-Output '  1. Event Server MIP log (%ProgramData%\Milestone\XProtect Event Server\logs\MIP.log, rolled'
Write-Output '     archives under logs\MIPLogs\, or the tray icon "Show MIP logs") must contain:'
Write-Output '       "Proxy fetcher ready (concurrency=..., request id=WebContent.Proxy.FetchRequest)"'
Write-Output '     A failure instead logs "Proxy fetcher failed to start: ...". Use the Management Client'
Write-Output '     workspace page "Test proxy fetch" row as the end-to-end check.'
Write-Output '  2. Every Smart Client that was connected BEFORE this restart must be restarted:'
Write-Output '     the Event Server restart disposed their messaging channels (TransmitMessage would die'
Write-Output '     with "Call to TransmitMessage after call to Dispose?").'
Write-Output '  3. For https targets run scripts/provision-event-server-authority.ps1 once on this machine,'
Write-Output '     then import the exported CA into LocalMachine\Root on every Smart Client machine (README).'
