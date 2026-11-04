# Ticket #89 row: MSI upgrade over a Copy-Item-strayed trust helper on the Event Server
# machine. Mirrors the IN-3 row-1 scenario WITH the fix: the helper now carries the build's
# nbgv stamp, so seeding it with an OLDER stamped build's helper (Copy-Item stray) and
# upgrading to a NEWER build must end with every drop file hash-identical to the local drop
# (the IN-3 original ended 12/13 because the unstamped 1.0.0.0 helper shadowed the upgrade).
# Sequence: pre-state -> seed stray helper from .cache\seed89 -> stage+run the new MSI
# (default options) -> post-state + full local-vs-remote drop hash table.
$ErrorActionPreference = 'Stop'
$root = 'C:\repos\WebContentPlugin'
. (Join-Path $root 'scripts\helpers.ps1')
Import-Environment (Join-Path $root '.env')
Unlock-SecretStore -Password (Import-Clixml (Join-Path $root '.secretstore'))
$cred = Get-Secret -Name $env:VMS_CREDENTIAL

$msi = Get-ChildItem (Join-Path $root 'output\Setup\en-US\*.msi') | Sort-Object LastWriteTime | Select-Object -Last 1
$localMsiHash = (Get-FileHash $msi.FullName -Algorithm SHA256).Hash
Write-Host "LOCAL MSI: $($msi.Name) SHA256=$localMsiHash"

$seedHelper = Join-Path $root '.cache\seed89\WebContentTrustHelper.exe'
$seedHash = (Get-FileHash $seedHelper -Algorithm SHA256).Hash
$seedVer = (Get-Item $seedHelper).VersionInfo
Write-Host ('SEED HELPER: {0}.{1}.{2}.{3} SHA256={4}' -f $seedVer.FileMajorPart, $seedVer.FileMinorPart, $seedVer.FileBuildPart, $seedVer.FilePrivatePart, $seedHash)

$s = New-PSSession -ComputerName $env:VMS_ADDRESS -Credential $cred
$s2 = New-PSSession -ComputerName $env:VMS_ADDRESS -Credential $cred

# --- pre-state (read-only) ---
$stateBlock = {
    $drop = 'C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin'
    $svc = Get-Service -Name MilestoneEventServerService
    $regs = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like '*Web Content*' } |
        ForEach-Object { [pscustomobject]@{ Name = $_.DisplayName; Version = $_.DisplayVersion; Code = $_.PSChildName } }
    $cas = @()
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor [System.Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
    try {
        $found = @($store.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindBySubjectName, 'XProtect WebContent Plugin Loopback Proxy', $false))
        foreach ($cert in $found) {
            $container = $null
            try { if ($cert.PrivateKey -and $cert.PrivateKey.CspKeyContainerInfo) { $container = $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName } } catch { $container = 'READ-REFUSED' }
            $cas += [pscustomobject]@{ Thumb = $cert.Thumbprint; NotAfter = $cert.NotAfter.ToString('o'); Container = $container }
        }
    } finally { $store.Close() }
    $cacl = 'n/a'
    if ($cas.Count -ge 1 -and $cas[0].Container -and $cas[0].Container -ne 'READ-REFUSED') {
        $keyFile = Get-ChildItem 'C:\ProgramData\Microsoft\Crypto\RSA\MachineKeys' -Filter $cas[0].Container -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($keyFile) {
            $cacl = (@(Get-Acl -LiteralPath $keyFile.FullName).Access | ForEach-Object { "$($_.IdentityReference) $($_.FileSystemRights) $($_.AccessControlType)" }) -join ' | '
        } else { $cacl = 'KEYFILE-NOT-FOUND' }
    }
    $files = @()
    if (Test-Path -LiteralPath $drop) {
        $trim = $drop.TrimEnd('\').Length + 1
        $files = @(Get-ChildItem -LiteralPath $drop -File -Recurse | ForEach-Object {
            $v = $_.VersionInfo
            [pscustomobject]@{
                File = $_.FullName.Substring($trim)
                Sha  = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                Ver  = "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart).$($v.FilePrivatePart)"
            }
        })
    }
    $mipTail = @()
    $mipLog = 'C:\ProgramData\Milestone\XProtect Event Server\logs\MIP.log'
    if (Test-Path -LiteralPath $mipLog) { $mipTail = @(Get-Content -LiteralPath $mipLog -Tail 400 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'Proxy fetcher ready|Loaded deployment proxy CA|Minted loopback proxy leaf' }) }
    [pscustomobject]@{
        Service = $svc.Status
        Regs    = $regs
        Cas     = $cas
        Dacl    = $cacl
        Files   = $files
        Mip     = $mipTail
    }
}
$pre = Invoke-Command -Session $s -ScriptBlock $stateBlock
Write-Host "PRE service=$($pre.Service)"
$pre.Regs | ForEach-Object { Write-Host "PRE reg: $($_.Version) [$($_.Code)]" }
$pre.Cas | ForEach-Object { Write-Host "PRE CA: $($_.Thumb) notAfter=$($_.NotAfter) container=$($_.Container)" }
Write-Host "PRE key DACL: $($pre.Dacl)"
Write-Host "PRE drop files=$($pre.Files.Count)"

# --- seed the Copy-Item stray: older stamped helper over the installed one ---
$remoteDrop = Invoke-Command -Session $s { 'C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin' }
Copy-Item -ToSession $s -Path $seedHelper -Destination (Join-Path $remoteDrop 'WebContentTrustHelper.exe') -Force
$seeded = Invoke-Command -Session $s {
    param($drop)
    $h = Join-Path $drop 'WebContentTrustHelper.exe'
    $v = (Get-Item -LiteralPath $h).VersionInfo
    [pscustomobject]@{ Ver = "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart).$($v.FilePrivatePart)"; Sha = (Get-FileHash -LiteralPath $h -Algorithm SHA256).Hash }
} -ArgumentList $remoteDrop
Write-Host "SEEDED helper on server: Ver=$($seeded.Ver) Sha=$($seeded.Sha)"
if ($seeded.Sha -ne $seedHash) { throw "seed copy hash mismatch on the server: $($seeded.Sha) != $seedHash" }

# --- stage the MSI ---
$remoteTempRoot = Invoke-Command -Session $s { [IO.Path]::GetTempPath() }
if ([string]::IsNullOrWhiteSpace($remoteTempRoot)) { throw 'remote GetTempPath returned nothing' }
$temp = Join-Path $remoteTempRoot 'wces89'
$null = Invoke-Command -Session $s -ArgumentList $temp { param($t) $null = New-Item -ItemType Directory -Path $t -Force; $t }
if ([string]::IsNullOrWhiteSpace($temp)) { throw 'remote temp folder creation failed' }
Copy-Item -ToSession $s -Path $msi.FullName -Destination $temp
$remoteMsi = Join-Path $temp $msi.Name
$remoteHash = Invoke-Command -Session $s -ArgumentList $remoteMsi { param($p) (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
if ($remoteHash -ne $localMsiHash) { throw "remote MSI hash $remoteHash != local $localMsiHash" }
Write-Host "staged + hash-verified: $remoteMsi"

# --- service journal from the second session ---
$journalFile = Join-Path $temp 'service-journal.txt'
$journal = Invoke-Command -Session $s2 -AsJob -ArgumentList $journalFile {
    param($f)
    $lines = @()
    $end = (Get-Date).AddMinutes(6)
    while ((Get-Date) -lt $end) {
        $lines += ('{0:HH:mm:ss.fff} {1}' -f (Get-Date), (Get-Service -Name MilestoneEventServerService -ErrorAction SilentlyContinue).Status)
        Start-Sleep -Milliseconds 500
    }
    Set-Content -LiteralPath $f -Value $lines
}

# --- the upgrade itself: defaults (RESTART_EVENT_SERVER=1, PROXY_SUPPORT=1) ---
$log = Join-Path $temp 'upgrade.log'
$exit = Invoke-Command -Session $s -ArgumentList @($remoteMsi, $log) {
    param($m, $l)
    $p = Start-Process msiexec.exe -ArgumentList "/i `"$m`" /qn /l*v `"$l`"" -Wait -PassThru
    $p.ExitCode
}
Write-Host "MSIEXEC EXIT=$exit"
if ($exit -notin 0, 3010, 1641) { throw "msiexec exit $exit - upgrade failed" }

Wait-Job -Job $journal -Timeout 420 | Out-Null
Receive-Job -Job $journal | Out-Null

# --- post-state ---
$post = Invoke-Command -Session $s -ScriptBlock $stateBlock
Write-Host "POST service=$($post.Service)"
$post.Regs | ForEach-Object { Write-Host "POST reg: $($_.Version) [$($_.Code)]" }
$post.Cas | ForEach-Object { Write-Host "POST CA: $($_.Thumb) notAfter=$($_.NotAfter) container=$($_.Container)" }
Write-Host "POST key DACL: $($post.Dacl)"
$post.Mip | ForEach-Object { Write-Host "MIP: $_" }

$journalText = Invoke-Command -Session $s -ArgumentList $journalFile { param($f) Get-Content -LiteralPath $f }
Write-Host '--- journal transitions ---'
$prev = ''
foreach ($line in $journalText) {
    $st = ($line -split ' ')[-1]
    if ($st -ne $prev) { Write-Host $line; $prev = $st }
}

Write-Host '--- MSI log action evidence ---'
Invoke-Command -Session $s -ArgumentList $log {
    param($l)
    Get-Content -LiteralPath $l | Where-Object {
        $_ -match 'CustomActionSchedule|Skipping action: (StopEventServerService|RestartEventServerService|CreateProxyAuthority)|Doing action: (StopEventServerService|RestartEventServerService|CreateProxyAuthority)|MainEngineThread is returning|Wix4CloseApplications|Shutting down application:|ExitCode'
    } | Select-Object -First 60
}

# --- the #89 verdict: local drop vs remote drop, every file, hash + version ---
Write-Host '--- local drop (output\WebContent) ---'
$localDrop = Join-Path $root 'output\WebContent'
$trim = $localDrop.TrimEnd('\').Length + 1
$localFiles = @(Get-ChildItem -LiteralPath $localDrop -File -Recurse | ForEach-Object {
    $v = $_.VersionInfo
    [pscustomobject]@{
        File = $_.FullName.Substring($trim)
        Sha  = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        Ver  = "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart).$($v.FilePrivatePart)"
    }
})
Write-Host '--- local-vs-remote file comparison ---'
$remoteByFile = @{}
foreach ($f in $post.Files) { $remoteByFile[$f.File] = $f }
$mismatch = 0
foreach ($lf in $localFiles) {
    $rf = $remoteByFile[$lf.File]
    if ($null -eq $rf) { Write-Host "MISSING-REMOTE  $($lf.File)"; $mismatch++; continue }
    $tag = if ($rf.Sha -eq $lf.Sha) { 'IDENTICAL' } else { $mismatch++; 'DIFFERS' }
    Write-Host ('{0,-9} {1} local={2} {3} remote={4} {5}' -f $tag, $lf.File, $lf.Ver, $lf.Sha.Substring(0, 8), $rf.Ver, $rf.Sha.Substring(0, 8))
}
foreach ($rf in $post.Files) {
    if (-not ($localFiles.File -contains $rf.File)) { Write-Host "EXTRA-REMOTE    $($rf.File) $($rf.Ver) $($rf.Sha.Substring(0,8))"; $mismatch++ }
}
Write-Host "COMPARISON: local=$($localFiles.Count) remote=$($post.Files.Count) mismatches=$mismatch"

# --- cleanup ---
Invoke-Command -Session $s -ArgumentList $temp { param($t) Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction SilentlyContinue }
Remove-PSSession $s, $s2
Write-Host 'DONE'
