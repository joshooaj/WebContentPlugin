param(
    [Parameter()]
    [string]
    $Version,

    [Parameter()]
    [switch]
    $Force
)
# & dotnet wix build ./Installer/*.wxs -arch x64 -out output\installer\WebContent.msi
# & dotnet wix msi validate output\installer\WebContent.msi -sice ICE61

# OR


$null = New-Item $PSScriptRoot\output -ItemType Directory -Force

# Download WebView2 runtime
$webview2Url = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/c638b5d5-4b2f-4845-baab-c9f4fd6c58ab/MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
if (!(Test-Path $PSScriptRoot\output\MicrosoftEdgeWebView2RuntimeInstallerX64.exe)) {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $webview2Url -OutFile $PSScriptRoot\output\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
}

if (!$PSCmdlet.MyInvocation.BoundParameters.ContainsKey('Version')) {
    $Version = (dotnet nbgv get-version -f json | ConvertFrom-Json).SimpleVersion
}

$verb = if ($Force) { '/Rebuild' } else { '/Build' }
& msbuild Setup/package.wixproj /t:Rebuild /restore:True /p:Configuration=Release /p:Platform=x64 /p:BuildVersion=$Version