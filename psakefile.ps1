param(
    # Xunit test filter. Example: 'FullyQualifiedName~ProbeProxied'
    [Parameter()]
    [string]
    $TestFilter
)

Properties {
    $script:ProjectRoot = $PSScriptRoot
    $script:BuildOutput = Join-Path $script:ProjectRoot 'output/WebContent'
    $script:EnvironmentFile = Join-Path $script:ProjectRoot .env
    $script:ProjectPath = Join-Path $script:ProjectRoot 'src/WebContent/WebContent.csproj'
    $script:PluginName = 'CascadiaWebContentPlugin'
    $script:MIPPluginsPath = 'C:\Program Files\VideoOS\MIPPlugins'
    $script:RelatedServices = @(, 'MilestoneEventServerService')
    $script:RelatedProcesses = 'Client', 'VideoOS.Administration'
    $script:UITestFilter = $TestFilter
}

$scriptDirectory = Join-Path $PSScriptRoot 'scripts'

Include (Join-Path $scriptDirectory 'helpers.ps1')

Task Default -Depends Build
Task Deploy -Depends DeployLocally, DeployToVms

Task ToolRestore {
    Exec {
        & dotnet tool restore
    }
}

Task Build -Depends ToolRestore {
    Exec {
        & dotnet build --nologo $script:ProjectPath
    }
}

Task Lint -Depends ToolRestore {
    Exec {
        & dotnet format .\src\WebContent\WebContent.csproj --verify-no-changes
        & dotnet format .\src\WebContentTrustHelper\WebContentTrustHelper.csproj --verify-no-changes
        & dotnet format .\src\WebContentServiceCtl\WebContentServiceCtl.csproj --verify-no-changes
        & dotnet format .\src\WebContentProxyCa\WebContentProxyCa.csproj --verify-no-changes
        & dotnet format .\src\WebContent.Tests\WebContent.Tests.csproj --verify-no-changes
        & dotnet format .\tests\ux\WebContent.UxTests\WebContent.UxTests.csproj --verify-no-changes
        Invoke-ScriptAnalyzer -Settings .\PSScriptAnalyzerSettings.psd1 . -ErrorAction Stop
    }
}

Task DeployLocally {
    foreach ($process in $script:RelatedProcesses) {
        Stop-Process -Name $process -ErrorAction Ignore
    }
    $pluginPath = Join-Path $script:MIPPluginsPath $script:PluginName
    if (Test-Path -LiteralPath $pluginPath) {
        Remove-Item -LiteralPath $pluginPath -Recurse -Force -ErrorAction Stop
    }
    $null = New-Item -Path $pluginPath -ItemType Directory -Force
    Get-ChildItem -LiteralPath $script:BuildOutput | ForEach-Object {
        $_ | Copy-Item -Destination $pluginPath -Recurse
    }
}

Task DeployToVms {
    Import-Environment $script:EnvironmentFile
    Unlock-SecretStore -Password (Import-Clixml (Join-Path $script:ProjectRoot '.secretstore'))
    $cred = Get-Secret -Name $env:VMS_CREDENTIAL
    $deployParams = @{
        Session     = New-PSSession -ComputerName $env:VMS_ADDRESS -Credential $cred
        PluginName  = $script:PluginName
        Source      = $script:BuildOutput
        Destination = $script:MIPPluginsPath
        Services    = $script:RelatedServices
        Processes   = $script:RelatedProcesses
    }
    Deploy-MipPlugin @deployParams
}

Task Test -Depends Build {
    Exec {
        & dotnet test (Join-Path $script:ProjectRoot 'src/WebContent.Tests/WebContent.Tests.csproj')
    }
}

Task TestUI {
    Exec {
        $testArgs = @(
            'test',
            (Join-Path $script:ProjectRoot 'tests/ux/WebContent.UxTests/WebContent.UxTests.csproj')
        )
        if (![string]::IsNullOrEmpty($script:UITestFilter)) {
            $testArgs += '--filter', $script:UITestFilter
        }
        & dotnet @testArgs
    }
}