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

    # #82: the Event Server service flow. The stop action must sit inside the script
    # (after InstallInitialize, after the #70 client-kill scan) and before both file
    # actions - a still-running Event Server locks the drop DLLs. The restart action
    # (finish-page choice) must land after InstallFiles so the service comes back on
    # the new files, and before InstallFinalize so it stays a deferred SYSTEM action.
    function Get-CustomActionRow([string]$xml, [string]$action) {
        $rows = Get-MsiTableRows $xml 'CustomAction'
        $rows | Where-Object { $_.Count -ge 1 -and $_[0] -eq $action } | Select-Object -First 1
    }
    foreach ($action in 'StopEventServerService', 'RestartEventServerService') {
        $row = Get-CustomActionRow $widXml $action
        if ($null -eq $row) {
            throw "Installer service-flow guard (#82): CustomAction table has no $action row."
        }
        # 1026 = exe streamed from the Binary table, deferred, return check, non-impersonating.
        if ($row[1] -ne '1026') {
            throw "Installer service-flow guard (#82): $action must be a deferred (SYSTEM) exe custom action (type 1026), found type $($row[1])."
        }
        if ($row[2] -ne 'WebContentServiceCtl.exe') {
            throw "Installer service-flow guard (#82): $action must run the WebContentServiceCtl.exe binary stream, found $($row[2])."
        }
        if (-not $seq.ContainsKey($action)) {
            throw "Installer service-flow guard (#82): $action missing from InstallExecuteSequence."
        }
    }
    $stopCondition = ($ies | Where-Object { $_.Count -ge 1 -and $_[0] -eq 'StopEventServerService' })[0][1]
    foreach ($needle in 'NOT REMOVE', 'EVENTSERVERSERVICEPRESENT', 'WIX_UPGRADE_DETECTED', 'WEBCONTENTPLUGINPRESENT') {
        if ($stopCondition -notlike "*$needle*") {
            throw "Installer service-flow guard (#82): StopEventServerService condition '$stopCondition' lost '$needle' - the stop must fire only on install/upgrade, only when the Event Server service exists, and only when plugin files are already installed."
        }
    }
    if ($seq['StopEventServerService'] -le $seq['InstallInitialize'] -or $seq['StopEventServerService'] -le $closeSeq) {
        throw "Installer service-flow guard (#82): StopEventServerService ($($seq['StopEventServerService'])) must run after InstallInitialize ($($seq['InstallInitialize'])) and the client-kill scan ($closeSeq)."
    }
    if ($seq['StopEventServerService'] -ge $seq['RemoveFiles'] -or $seq['StopEventServerService'] -ge $seq['InstallFiles']) {
        throw "Installer service-flow guard (#82): StopEventServerService ($($seq['StopEventServerService'])) must run before RemoveFiles ($($seq['RemoveFiles'])) and InstallFiles ($($seq['InstallFiles'])) - the Event Server still holds the drop dlls until it is Stopped."
    }
    if ($seq['RestartEventServerService'] -lt $seq['InstallFiles'] -or $seq['RestartEventServerService'] -ge $seq['InstallFinalize']) {
        throw "Installer service-flow guard (#82): RestartEventServerService ($($seq['RestartEventServerService'])) must run after InstallFiles ($($seq['InstallFiles'])) and before InstallFinalize ($($seq['InstallFinalize']))."
    }
    $restartCondition = ($ies | Where-Object { $_.Count -ge 1 -and $_[0] -eq 'RestartEventServerService' })[0][1]
    if ($restartCondition -notlike '*RESTART_EVENT_SERVER*') {
        throw "Installer service-flow guard (#82): RestartEventServerService condition '$restartCondition' lost the RESTART_EVENT_SERVER choice."
    }

    # #82: the UI half - the welcome page must branch to the choice page (and to the old
    # one-click end) on the ES-detected property, the choice page carries the checkbox,
    # and its Install button ends the dialog loop.
    $controlEvents = Get-MsiTableRows $widXml 'ControlEvent'
    $branch = $controlEvents | Where-Object {
        $_.Count -ge 4 -and $_[0] -eq 'WebContentWelcomeEulaDlg' -and $_[1] -eq 'Install' -and $_[2] -eq 'NewDialog' -and $_[3] -eq 'EventServerOptionsDlg'
    } | Select-Object -First 1
    if ($null -eq $branch) {
        throw "Installer service-flow guard (#82): no WebContentWelcomeEulaDlg/Install -> EventServerOptionsDlg branch - setupui.wxs lost the Event Server page hop."
    }
    if ($branch[4] -notlike '*EVENTSERVERSERVICEPRESENT*') {
        throw "Installer service-flow guard (#82): the Event Server page hop is not gated on EVENTSERVERSERVICEPRESENT - the page must never appear on machines without the Event Server."
    }
    $endNoEs = $controlEvents | Where-Object {
        $_.Count -ge 4 -and $_[0] -eq 'WebContentWelcomeEulaDlg' -and $_[1] -eq 'Install' -and $_[2] -eq 'EndDialog' -and $_[3] -eq 'Return' -and $_.Count -ge 5 -and $_[4] -like '*NOT EVENTSERVERSERVICEPRESENT*'
    } | Select-Object -First 1
    if ($null -eq $endNoEs) {
        throw "Installer service-flow guard (#82): the no-Event-Server branch lost the direct EndDialog Return (the old WixUI_Minimal one-click behaviour)."
    }
    $installChoice = $controlEvents | Where-Object {
        $_.Count -ge 4 -and $_[0] -eq 'EventServerOptionsDlg' -and $_[1] -eq 'Install' -and $_[2] -eq 'EndDialog' -and $_[3] -eq 'Return'
    } | Select-Object -First 1
    if ($null -eq $installChoice) {
        throw "Installer service-flow guard (#82): EventServerOptionsDlg/Install no longer ends the dialog loop."
    }
    $checkboxes = Get-MsiTableRows $widXml 'Control'
    $restartCheckbox = $checkboxes | Where-Object { $_.Count -ge 9 -and $_[0] -eq 'EventServerOptionsDlg' -and $_[8] -eq 'RESTART_EVENT_SERVER' } | Select-Object -First 1
    if ($null -eq $restartCheckbox) {
        throw "Installer service-flow guard (#82): EventServerOptionsDlg carries no CheckBox bound to RESTART_EVENT_SERVER."
    }
} finally {
    $zip.Dispose()
}
Write-Host "Installer sequencing OK: force-close before file removal, RemoveExistingProducts late (#70); Event Server stop/restart flow gated, inside the script, choice-carrying (#82)." -ForegroundColor Green
Write-Host "Build completed successfully." -ForegroundColor Green

[pscustomobject]@{
    Version   = $Version
    Artifacts = Resolve-Path $PSScriptRoot\output\WebContent
    Installer = Resolve-Path $PSScriptRoot\output\Setup\en-US\
}