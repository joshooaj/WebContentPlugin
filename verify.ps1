#requires -Version 7.0
<#
.SYNOPSIS
    One-command release gate for the WebContent plugin (ticket #16).

.DESCRIPTION
    Runs the checks that must pass before a release: the xunit regression suite (which
    includes the source-hygiene guards). With -IncludePluginBuild it additionally runs the
    VS2022 MSBuild plugin build, since dotnet cannot build the plugin csproj. Exit code 0
    means every gate passed; non-zero means at least one failed.
    See docs/verification-matrix.md for the full verification contract.
#>
param(
    [switch]$IncludePluginBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$gates = [System.Collections.Generic.List[object]]::new()

Write-Host "== Gate 1: regression suite (src/WebContent.Tests) ==" -ForegroundColor Cyan
dotnet test (Join-Path $repoRoot 'src/WebContent.Tests') --nologo -v minimal
$gates.Add([pscustomobject]@{ Name = 'Regression suite'; ExitCode = $LASTEXITCODE })

if ($IncludePluginBuild) {
    Write-Host "`n== Gate 2: plugin build (MSBuild x64 Debug) ==" -ForegroundColor Cyan
    $msbuild = (Get-Command msbuild -ErrorAction SilentlyContinue)?.Source
    if (-not $msbuild) {
        $msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
    }
    if (-not (Test-Path -LiteralPath $msbuild)) {
        Write-Host 'MSBuild not found (PATH or VS2022 Community default). Install VS2022 or add msbuild to PATH.' -ForegroundColor Red
        $gates.Add([pscustomobject]@{ Name = 'Plugin build'; ExitCode = 1 })
    }
    else {
        & $msbuild (Join-Path $repoRoot 'src/WebContent/WebContent.csproj') /t:Build /p:Configuration=Debug /p:Platform=x64 /v:minimal /nologo
        # sbom-tool "_manifest already exists" post-build noise is harmless; exit code is authoritative.
        $gates.Add([pscustomobject]@{ Name = 'Plugin build'; ExitCode = $LASTEXITCODE })
    }
}

Write-Host "`n== Gate results ==" -ForegroundColor Cyan
$failed = $false
foreach ($gate in $gates) {
    if ($gate.ExitCode -eq 0) {
        Write-Host ("PASS  {0} (exit 0)" -f $gate.Name) -ForegroundColor Green
    }
    else {
        Write-Host ("FAIL  {0} (exit {1})" -f $gate.Name, $gate.ExitCode) -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) {
    Write-Host 'Release gate: FAIL' -ForegroundColor Red
    exit 1
}

Write-Host 'Release gate: PASS' -ForegroundColor Green
exit 0
