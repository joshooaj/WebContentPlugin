param(
    [Parameter()]
    [string]
    $Version
)
# & dotnet wix build ./Installer/*.wxs -arch x64 -out output\installer\WebContent.msi
# & dotnet wix msi validate output\installer\WebContent.msi -sice ICE61

& dotnet tool restore

$null = New-Item $PSScriptRoot\output, .cache\ -ItemType Directory -Force
Get-ChildItem $PSScriptRoot\output | Remove-Item -Recurse -Force

# Download WebView2 runtime
$webview2Url = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/35871e19-d11b-432d-9d1e-7be0ff286540/MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$webviewDestination = [io.path]::Combine($PSScriptRoot, '.cache', 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe')
if (!(Test-Path $webviewDestination -PathType Leaf)) {
    $ProgressPreference = 'SilentlyContinue'
    Write-Host "Downloading WebView2 Runtime due to cache miss" -ForegroundColor Yellow
    Invoke-WebRequest -Uri $webview2Url -OutFile $webviewDestination
}

if (!$PSCmdlet.MyInvocation.BoundParameters.ContainsKey('Version')) {
    $Version = (dotnet nbgv get-version -f json | ConvertFrom-Json).Version
}

Write-Host "Building CascadiaWebContentPlugin.$Version.msi"
& dotnet build src/Setup/setup.wixproj -t:Rebuild -p:Configuration=Release -p:Platform=x64 -p:BuildVersion=$Version

if (!$?) {
    throw "MSBuild exited with code $LASTEXITCODE"
}

$sbomPath = Join-Path $PSScriptRoot 'output\WebContent\_manifest\spdx_2.2\manifest.spdx.json'
if (!(Test-Path $sbomPath -PathType Leaf)) {
    throw "SBOM was not generated at $sbomPath. The PostBuildEvent sbom-tool step did not run or failed."
}
$detectedPackages = ((Get-Content $sbomPath -Raw | ConvertFrom-Json).packages | Where-Object { $_.name -ne 'CascadiaWebContentPlugin' })
if (@($detectedPackages).Count -eq 0) {
    throw "SBOM at $sbomPath lists no referenced packages; sbom-tool component detection is broken (check the -bc component path in the PostBuildEvent)."
}
Write-Host "SBOM OK: $($detectedPackages.Count) referenced packages detected." -ForegroundColor Green

# #70: structural guard for the force-close-authoring. The upgrade only succeeds with
# clients running if the kill lands inside the script, before RemoveFiles/InstallFiles,
# and RemoveExistingProducts is late (afterInstallExecute). Pinned here against the
# intermediate wixpdb (zip) so the check needs no MSI COM API.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pdb = Get-ChildItem $PSScriptRoot\src\Setup\obj -Recurse -Filter "*$Version.wixpdb" | Sort-Object LastWriteTime | Select-Object -Last 1
if ($null -eq $pdb) {
    throw "No wixpdb found for version $Version under src\Setup\obj; cannot verify #70 installer sequencing."
}
$zip = [System.IO.Compression.ZipFile]::OpenRead($pdb.FullName)
try {
    $widEntry = $zip.Entries | Where-Object { $_.FullName -eq 'wix-wid.xml' }
    if ($null -eq $widEntry) {
        throw "wixpdb $pdb has no wix-wid.xml; cannot verify #70 installer sequencing."
    }
    $reader = New-Object System.IO.StreamReader($widEntry.Open())
    $widXml = $reader.ReadToEnd()
    $reader.Close()

    function Get-MsiTableRows([string]$xml, [string]$table) {
        $m = [regex]::Match($xml, '<table name="' + $table + '">[\s\S]*?</table>')
        if (!$m.Success) { return @() }
        $rows = @()
        foreach ($rowMatch in [regex]::Matches($m.Value, '<row[^>]*>([\s\S]*?)</row>')) {
            $fields = @()
            foreach ($field in [regex]::Matches($rowMatch.Groups[1].Value, '<field>([\s\S]*?)</field>')) {
                $fields += [System.Net.WebUtility]::HtmlDecode($field.Groups[1].Value)
            }
            $rows += ,$fields
        }
        return ,$rows
    }

    # InstallExecuteSequence rows: Action, Condition, Sequence (wid column order).
    $ies = Get-MsiTableRows $widXml 'InstallExecuteSequence'
    $seq = @{}
    foreach ($row in $ies) { if ($row.Count -ge 3 -and $row[2] -ne '') { $seq[$row[0]] = [int]$row[2] } }
    $close = $ies | Where-Object { $_.Count -ge 1 -and $_[0] -like 'Wix4CloseApplications_*' } | Select-Object -First 1
    if ($null -eq $close) {
        throw "Installer sequencing guard (#70): InstallExecuteSequence has no Wix4CloseApplications_* action - the util:CloseApplication authoring in setup.wxs is missing or not scheduled."
    }
    $closeSeq = [int]$close[2]
    foreach ($anchor in 'InstallInitialize', 'RemoveFiles', 'InstallFiles') {
        if (-not $seq.ContainsKey($anchor)) { throw "Installer sequencing guard (#70): $anchor missing from InstallExecuteSequence." }
    }
    if ($closeSeq -le $seq['InstallInitialize']) {
        throw "Installer sequencing guard (#70): Wix4CloseApplications ($closeSeq) must run after InstallInitialize ($($seq['InstallInitialize'])) so its deferred kill is inside the script."
    }
    if ($closeSeq -ge $seq['RemoveFiles'] -or $closeSeq -ge $seq['InstallFiles']) {
        throw "Installer sequencing guard (#70): Wix4CloseApplications ($closeSeq) must run before RemoveFiles ($($seq['RemoveFiles'])) and InstallFiles ($($seq['InstallFiles'])) so locked drop DLLs are killed first."
    }
    if (-not $seq.ContainsKey('RemoveExistingProducts')) {
        throw "Installer sequencing guard (#70): RemoveExistingProducts missing - MajorUpgrade Schedule=afterInstallExecute is required (#70)."
    }
    if ($seq['RemoveExistingProducts'] -lt $seq['InstallFiles']) {
        throw "Installer sequencing guard (#70): RemoveExistingProducts ($($seq['RemoveExistingProducts'])) must be scheduled after InstallFiles ($($seq['InstallFiles'])) - MajorUpgrade Schedule=afterInstallExecute was lost."
    }

    $closeApps = Get-MsiTableRows $widXml 'Wix4CloseApplication'
    $targets = $closeApps | Where-Object { $_.Count -ge 2 } | ForEach-Object { $_[1] }
    foreach ($target in 'Client.exe', 'VideoOS.Administration.exe', 'VideoOS.OAuth2TokenGenerator.exe') {
        if ($targets -notcontains $target) {
            throw "Installer sequencing guard (#70): Wix4CloseApplication table does not force-close $target (found: $($targets -join ', '))."
        }
    }
} finally {
    $zip.Dispose()
}
Write-Host "Installer sequencing OK: force-close before file removal, RemoveExistingProducts late (#70)." -ForegroundColor Green
Write-Host "Build completed successfully." -ForegroundColor Green

[pscustomobject]@{
    Version   = $Version
    Artifacts = Resolve-Path $PSScriptRoot\output\WebContent
    Installer = Resolve-Path $PSScriptRoot\output\Setup\en-US\
}