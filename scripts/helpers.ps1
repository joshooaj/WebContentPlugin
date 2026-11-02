function Import-Environment {
    [CmdletBinding()]
    param (
        [Parameter(Mandatory)]
        [string]
        $Environment
    )

    $Environment = (Resolve-Path $Environment -ErrorAction Stop).Path
    foreach ($line in Get-Content -LiteralPath $Environment) {
        if ($line -match '^\s*#') {
            continue
        }

        if ($line -match '^(?<k>[a-z][a-z0-9_-]*)=(?<v>.*)$') {
            $null = New-Item -Path "Env:\$($Matches['k'])" -Value $Matches['v'] -Force
        } else {
            Write-Warning "Skipping invalid line: $line"
            continue
        }
    }
}

function Deploy-MipPlugin {
    [CmdletBinding()]
    param (
        [Parameter(Mandatory)]
        [System.Management.Automation.Runspaces.PSSession]
        $Session,

        # Name of plugin which also determines folder name in destination
        [Parameter(Mandatory)]
        [string]
        $PluginName,

        # Specifies the path to the folder containing the "plugin.def" file.
        [Parameter(Mandatory)]
        [string]
        $Source,

        [Parameter()]
        [string]
        $Destination = 'C:\Program Files\VideoOS\MIPPlugins\',

        # Services to stop and restart, if any, during plugin deployment.
        [Parameter()]
        [string[]]
        $Services,

        # Processes to stop before deploying plugin.
        [Parameter()]
        [string[]]
        $Processes
    )

    if ($null -eq $Session) {
        $Session = New-VmsSession
    }

    $Source = (Resolve-Path -Path $Source).Path
    $pluginDefPath = Join-Path $Source 'plugin.def'
    if (-not (Test-Path -Path $pluginDefPath)) {
        Write-Error "File not found: $pluginDefPath"
        return
    }

    $tempFolder = Invoke-Command -Session $Session -ScriptBlock {
        $tempPath = [System.IO.Path]::GetTempPath()
        $tempFolder = Join-Path $tempPath (New-Guid)
        (New-Item -ItemType Directory -Path $tempFolder).FullName
    }

    Get-ChildItem -Path $Source | ForEach-Object {
        $_ | Copy-Item -ToSession $Session -Destination $tempFolder -Recurse
    }

    $settings = @{
        TempFolder  = $tempFolder
        Destination = Join-Path $Destination $PluginName
        Services    = $Services
        Processes   = $Processes
    }
    Invoke-Command -Session $Session -ArgumentList $settings -ScriptBlock {
        param([hashtable]$settings)

        foreach ($process in $settings.Processes) {
            if ($null -eq (Get-Process -Name $process -ErrorAction SilentlyContinue)) {
                continue
            }
            Stop-Process -Name $process -Force -ErrorAction Stop
        }

        $stoppedServices = @()
        foreach ($service in $settings.Services) {
            $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
            if ($null -ne $svc -and $svc.Status -ne 'Stopped') {
                $svc | Stop-Service -WarningAction SilentlyContinue -ErrorAction Stop
                $stoppedServices += $svc
            }
        }

        if (Test-Path -LiteralPath $settings.Destination -PathType Any) {
            $timeout = (Get-Date).AddMinutes(1)
            do {
                try {
                    Remove-Item -LiteralPath $settings.Destination -Recurse -Force -ErrorAction Stop
                    break
                } catch {
                    if ((Get-Date) -ge $timeout) {
                        throw
                    }
                    Start-Sleep -Seconds 1
                }
            } while ((Get-Date) -lt $timeout)
        }
        $null = New-Item -Path $settings.Destination -ItemType Directory -Force
        Get-ChildItem -LiteralPath $settings.TempFolder | ForEach-Object {
            $_ | Copy-Item -Destination $settings.Destination -Recurse -Force
        }

        # Only start services we actually stopped
        foreach ($svc in $stoppedServices) {
            $svc | Start-Service
        }
    }
}