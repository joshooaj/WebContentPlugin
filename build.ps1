param(
    [Parameter()]
    [string]
    $Version
)
# & dotnet wix build ./Installer/*.wxs -arch x64 -out output\installer\WebContent.msi
# & dotnet wix msi validate output\installer\WebContent.msi -sice ICE61

& dotnet tool restore

$null = New-Item $PSScriptRoot\output -ItemType Directory -Force
Get-ChildItem $PSScriptRoot\output | Remove-Item -Recurse -Force

# Download WebView2 runtime
$webview2Url = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/c638b5d5-4b2f-4845-baab-c9f4fd6c58ab/MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
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

& msbuild src/Setup/setup.wixproj /t:Rebuild /restore:True /p:Configuration=Release /p:Platform=x64 /p:BuildVersion=$Version

if (!$?) {
    throw "MSBuild exited with code $LASTEXITCODE"
}
Write-Host "Build completed successfully." -ForegroundColor Green

[pscustomobject]@{
    Version   = $Version
    Artifacts = Resolve-Path $PSScriptRoot\output\WebContent
    Installer = Resolve-Path $PSScriptRoot\output\Setup\en-US\
}