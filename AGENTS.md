# WebContentPlugin

Smart Client MIP plugin (WebView2 browser view). Plugin project `src/WebContent` targets .NET Framework 4.7.2 in legacy csproj style; `src/WebContent.Tests` targets net10.0.

## Build

- Build the plugin with VS 2022 MSBuild; `dotnet build` on `src/WebContent/WebContent.csproj` cannot resolve the `MilestoneSystems.VideoOS.Platform` PackageReference and dies with ~110 `CS0246` errors.

  ```powershell
  msbuild src/WebContent/WebContent.csproj /t:Build /p:Configuration=Debug /p:Platform=x64
  # msbuild path when not on PATH: C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe
  ```

- `PostBuildEvent` `sbom-tool` runs with `-D true` (stale `_manifest` self-overwrites) and `-bc "$(MSBuildProjectDirectory)"` (quoted absolute component path — build events run with CWD = output dir, so a relative `-bc .\` would scan the drop and detect zero packages; `$(ProjectDir)` ends in a backslash that breaks quoting). A sbom-tool failure now fails the build with MSB3073. Judge success by exit code plus files in `output\WebContent\` (9 files + `runtimes` folder).
- Full installer: `.\build.ps1` restores dotnet tools, caches the WebView2 runtime into `.cache`, then `msbuild src/Setup/setup.wixproj /t:Rebuild` (needs msbuild on PATH; CI adds it via setup-msbuild).

## Test

- `dotnet test src/WebContent.Tests` works (xunit). The test project is not in `src/WebContent.sln`; point at the csproj.
- The test project links pure source files via `<Compile Include="..\WebContent\..." Link>` and never project-references the plugin csproj. Register every new testable class in both places: `Compile` item in `WebContent.csproj` and `Compile` link in `WebContent.Tests.csproj`. Land it in one only and the plugin or the tests silently miss the class.

## Deploy for live-host testing

1. Kill plugin hosts first, or the copy fails on locked DLLs: `Stop-Process -Name client,VideoOS.Administration` (escalate to `-Force` for survivors).
2. Copy `output\WebContent\*` to `C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin` — the `INSTALLFOLDER` from `src/Setup/setup.wxs`. The `VideoOS` root is deliberate: both `Milestone\MIPPlugins` and `VideoOS\MIPPlugins` are valid MIP roots (OEM compatibility).
3. Elevate the copy: `Start-Process -Verb runas -FilePath pwsh -ArgumentList '-NoProfile','-Command','<copy>'`. Leave off `-Wait` under a shell timeout; the UAC prompt is interactive, so launch and poll the destination.
4. Verify by file hash, not timestamp: `Copy-Item` preserves the source `LastWriteTime`, so a good copy can still look stale.
5. Logs: Smart Client plugin `EnvironmentManager.Log` output lands in `%ProgramData%\Milestone\XProtect Smart Client\MIPLog.txt`; Management Client logs to `Logs\MIP.log` under its own ProgramData folder. `MIPLog.txt` can be absent mid-session or when client logging is disabled, so missing logs are no proof the plugin never ran.

## Agent skills

### Issue tracker

Issues are tracked in GitHub (`joshooaj/WebContentPlugin`). See `docs/agents/issue-tracker.md`.

### Triage labels

Default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout. See `docs/agents/domain.md`.
