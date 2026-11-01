#requires -Version 7.0
<#
.SYNOPSIS
    One-command release gate for the WebContent plugin (ticket #16).

.DESCRIPTION
    Runs the checks that must pass before a release: the xunit regression suite (which
    includes the source-hygiene guards). With -IncludePluginBuild it additionally runs the
    dotnet plugin build (the csproj is SDK-style net472, so plain `dotnet build` suffices;
    no full-MSBuild dependency). Exit code 0 means every gate passed; non-zero means at
    least one failed.
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
    Write-Host "`n== Gate 2: plugin build (dotnet Debug) ==" -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'src/WebContent/WebContent.csproj') -c Debug -v minimal
    # GenerateSbom target is self-overwriting (-D true); exit code is authoritative.
    $gates.Add([pscustomobject]@{ Name = 'Plugin build'; ExitCode = $LASTEXITCODE })
}

Write-Host "`n== Gate results ==" -ForegroundColor Cyan
$failed = $false
foreach ($gate in $gates) {
    if ($gate.ExitCode -eq 0) {
        Write-Host ("PASS  {0} (exit 0)" -f $gate.Name) -ForegroundColor Green
    } else {
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
