$projectRoot = if ([string]::IsNullOrEmpty($PSScriptRoot)) { $PWD } else { $PSScriptRoot }
$nbgv = dotnet nbgv get-version -f json | ConvertFrom-Json
$vdprojPath = (Resolve-Path "$projectRoot\Setup\Setup.vdproj" -ErrorAction Stop).Path
$vdproj = Get-Content $vdprojPath -Raw
$vdproj = $vdproj -replace '(?<="ProductCode"\s+=\s+"8:{)(.+?)(?=}")', (New-Guid).ToString().ToUpper()
$vdproj = $vdproj -replace '(?<="ProductVersion"\s+=\s+"8:)(.+?)(?=")', $nbgv.SimpleVersion
$vdproj | Set-Content -Path $vdprojPath -Force
