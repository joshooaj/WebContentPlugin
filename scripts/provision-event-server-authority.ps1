<#
.SYNOPSIS
  One-time elevated setup of the WebContent proxy certificate authority on the
  XProtect Event Server machine (ADR-0003). Run this ONCE per deployment, from
  an elevated Windows PowerShell 5.1 session on the Event Server machine.

.DESCRIPTION
  The proxy fetcher mints short-lived loopback leaf certificates from a
  deployment CA whose certificate and private key live in the LocalMachine
  Personal store, under the fixed machine key container "WebContent-Proxy-CA".
  The Event Server service runs as a restricted built-in account
  (NT AUTHORITY\NETWORK SERVICE on current Milestone installs) that cannot
  create the CA itself, so an administrator creates it here once. The script
  then grants the service account read access to the CA private key so leaf
  minting can proceed at runtime.

  After this script, copy the exported CA certificate to every client machine
  and import it into LocalMachine\Root (see README), because Smart Client
  installs are not elevated during plugin load.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File provision-event-server-authority.ps1
#>

param([string] $PluginDll = 'C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin\WebContent.dll')

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PluginDll)) { throw "WebContent.dll not found at $PluginDll - deploy the plugin first." }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this script from an elevated (Administrator) Windows PowerShell 5.1 session.' }
if ($PSVersionTable.PSVersion.Major -ge 7) { throw 'Run this script in Windows PowerShell 5.1 (powershell.exe); PowerShell 7 cannot create the CAPI machine key.' }

# LoadFrom (not Add-Type) so the MIP SDK types the plugin assembly also
# references are never enumerated; only the crypto types are used here.
$asm = [Reflection.Assembly]::LoadFrom($PluginDll)

$csharp = 'using System;' + [char]10 + 'public static class ProvisionLog {' + [char]10 +
  '  public static readonly Action<string> Act = m => Console.WriteLine(("' + 'LOG:' + ' " + m));' + [char]10 + '}'
Add-Type -TypeDefinition $csharp
$log = [ProvisionLog]::Act

$authType = $asm.GetType('WebContent.Authority')
$load = $authType.GetMethod('LoadOrCreateDeploymentAuthority', [Reflection.BindingFlags]'Public,NonPublic,Static')
$createdBefore = Get-Date
$authority = $load.Invoke($null, [object[]] @($log))

$cert = $authType.GetProperty('Certificate', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($authority)
Write-Output ('CA subject:    ' + $cert.Subject)
Write-Output ('CA thumbprint: ' + $cert.Thumbprint)
if (-not $cert.HasPrivateKey) { throw 'Loaded CA has no private key - the machine key container is not readable by this account.' }

# Export the CA certificate (public part) for the client-side LocalMachine\Root import.
$cerPath = Join-Path $env:PUBLIC 'WebContent-Proxy-Authority.cer'
[IO.File]::WriteAllBytes($cerPath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
Write-Output ('CA exported:   ' + $cerPath)

# The CAPI machine key file lands in the MachineKeys folder at creation time.
# Its file name is the lowercase MD5 hex of the container name (try both the
# ANSI and UTF-16 spellings); fall back to a creation-time window. Then grant
# the Event Server service account Read so the plugin can mint leaves at runtime.
$svc = 'NT AUTHORITY\NETWORK SERVICE'
$keyDir = Join-Path $env:ProgramData 'Microsoft\Crypto\RSA\MachineKeys'
$container = 'WebContent-Proxy-CA'
$candidates = @()
foreach ($encoding in @([Text.Encoding]::ASCII, [Text.Encoding]::Unicode)) {
  $hash = [System.BitConverter]::ToString([System.Security.Cryptography.MD5]::Create().ComputeHash($encoding.GetBytes($container))).Replace('-','').ToLower()
  $candidates += @(Get-ChildItem $keyDir -File -Filter $hash -EA SilentlyContinue)
}
if ($candidates.Count -eq 0) {
  $candidates = @(Get-ChildItem $keyDir -File | Where-Object { $_.CreationTime -ge $createdBefore.AddSeconds(-120) })
}
if ($candidates.Count -eq 0) {
  Write-Output 'NOTE: no machine key file located. If minting later fails with Access is denied, grant Read on the WebContent-Proxy-CA key file to the service account.'
}
foreach ($k in $candidates) {
  $acl = Get-Acl $k.FullName
  $rule = New-Object Security.AccessControl.FileSystemAccessRule($svc, 'Read', 'Allow')
  $acl.AddAccessRule($rule)
  Set-Acl $k.FullName $acl
  Write-Output ('key ACL:       ' + $k.Name + ' -> Read for ' + $svc)
}

Write-Output 'Done. Import the exported .cer into LocalMachine\Root on every Smart Client machine (elevated Import-Certificate), then restart the Smart Client.'
