# Manual UX test runner. Requires an interactive desktop session, the XProtect
# desktop clients installed, and repo-root .env credentials.
#
# When tests succeed, README screenshots produced by the run are automatically
# promoted (copied) into docs/assets/images/ under their final names. Pass
# -NoPromote to skip that and leave the artifacts for inspection instead.
param(
  [Parameter()]
  [ValidateSet('ux', 'ux-recon', 'ux-screenshots')]
  [string]
  $Category = 'ux',

  [Parameter()]
  [string]
  $Filter = '',

  [Parameter()]
  [string]
  $Configuration = 'Debug',

  [Parameter()]
  [switch]
  $NoPromote
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSCommandPath

Push-Location $repo
try {
  $results = Join-Path $repo 'artifacts/ux/test-results'
  New-Item -ItemType Directory -Path $results -Force | Out-Null

  $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=0'

  $dotnetArgs = @(
    'test',
    'tests/ux/WebContent.UxTests/WebContent.UxTests.csproj',
    '-c', $Configuration,
    '--logger', 'console;verbosity=normal',
    '--results-directory', $results
  )

  if ($Filter) {
    $dotnetArgs += @('--filter', $Filter)
  } else {
    $dotnetArgs += @('--filter', "Category=$Category")
  }

  $runStart = Get-Date
  & dotnet @dotnetArgs
  $exit = $LASTEXITCODE

  if ($exit -eq 0 -and -not $NoPromote) {
    # Promote only files written by THIS run (artifact dirs touched after $runStart),
    # newest wins per name, so running one test at a time still promotes correctly.
    $map = [ordered]@{
      'readme-sc-01-view.png'          = 'screenshot1.png'
      'readme-sc-02-properties.png'    = 'properties.png'
      'readme-mc-01-workspace.png'     = 'mc-workspace.png'
      'readme-mc-02-role-security.png' = 'mc-role-security.png'
    }
    $imagesDir = Join-Path $repo 'docs/assets/images'
    $runDirs = @(Get-ChildItem (Join-Path $repo 'artifacts/ux') -Directory |
      Where-Object { $_.Name -ne 'test-results' -and $_.LastWriteTime -ge $runStart })

    foreach ($src in $map.Keys) {
      $found = $runDirs |
        ForEach-Object { Get-ChildItem $_.FullName -Filter $src -File -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
      if ($found) {
        Copy-Item $found.FullName (Join-Path $imagesDir $map[$src]) -Force
        Write-Host "promoted: $($found.Name) -> docs/assets/images/$($map[$src])"
      } else {
        Write-Warning "no '$src' produced by this run; docs/assets/images/$($map[$src]) left untouched"
      }
    }
  }

  $latest = Get-ChildItem (Join-Path $repo 'artifacts/ux') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'test-results' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

  if ($latest) {
    Write-Host "UX artifacts: $($latest.FullName)"
    Get-ChildItem $latest.FullName |
      Sort-Object LastWriteTime |
      Select-Object Name, Length, LastWriteTime |
      Out-Host
  }

  exit $exit
} finally {
  Pop-Location
}
