$dependencies = @(
    @{
        Name        = 'Microsoft Edge WebView2 Runtime'
        Url         = 'https://go.microsoft.com/fwlink/?linkid=2124701'
        Destination = 'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Microsoft\VisualStudio\BootstrapperPackages\WebViewRuntime\MicrosoftEdgeWebview2Setup.exe'
    },
    @{
        Name        = 'Microsoft .NET Framework 4.7.2'
        Url         = 'https://go.microsoft.com/fwlink/?linkid=863265'
        Destination = 'C:\Program Files (x86)\Microsoft SDKs\ClickOnce Bootstrapper\Packages\DotNetFX472\NDP472-KB4054530-x86-x64-AllOS-ENU.exe'
    }
)

function IsAdministrator {
    $Identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $Principal = New-Object System.Security.Principal.WindowsPrincipal($Identity)
    $Principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function IsUacEnabled {
    (Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Policies\System).EnableLua -ne 0
}

foreach ($dependency in $dependencies) {
    $fileInfo = [io.fileinfo]$dependency.Destination
    if (-not (Test-Path $fileInfo.DirectoryName)) {
        if (!(IsAdministrator)) {
            if (IsUacEnabled) {
                [string[]]$argList = @('-NoProfile', '-File', $MyInvocation.MyCommand.Path)
                $argList += $MyInvocation.BoundParameters.GetEnumerator() | ForEach-Object { "-$($_.Key)", "$($_.Value)" }
                $argList += $MyInvocation.UnboundArguments
                Start-Process PowerShell.exe -Verb Runas -WorkingDirectory $pwd -ArgumentList $argList
                return
            }
            else {
                throw "You must be administrator to run this script"
            }
        }
        Write-Host "Creating subdirectory $($fileInfo.Directory.Name)"
        $fileInfo.Directory.Create()
    }
    if (-not (Test-Path $dependency.Destination)) {
        if (!(IsAdministrator)) {
            if (IsUacEnabled) {
                [string[]]$argList = @('-NoProfile', '-File', $MyInvocation.MyCommand.Path)
                $argList += $MyInvocation.BoundParameters.GetEnumerator() | ForEach-Object { "-$($_.Key)", "$($_.Value)" }
                $argList += $MyInvocation.UnboundArguments
                Start-Process PowerShell.exe -Verb Runas -WorkingDirectory $pwd -ArgumentList $argList
                return
            }
            else {
                throw "You must be administrator to run this script"
            }
        }
        Write-Host "Downloading $($dependency.Name) to $($dependency.Destination)"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $dependency.Url -OutFile $dependency.Destination
    }
    else {
        Write-Host "$($dependency.Name) already available at $($dependency.Destination)"
    }
}
