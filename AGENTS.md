# WebContentPlugin

Smart Client MIP plugin (WebView2 browser view). Plugin project `src/WebContent` targets .NET Framework 4.7.2 (SDK-style csproj); `src/WebContent.Tests` targets net10.0.

## Build

- Task runner is `psakefile.ps1` (psake module — `Install-Module psake`). Tasks: `ToolRestore` (`dotnet tool restore`), `Build` (depends on ToolRestore), `Lint` (`dotnet format --verify-no-changes`), `Test` (depends on Build), `TestUI`, `DeployLocally`, `DeployToVms`; `Deploy` = DeployLocally + DeployToVms.

  ```powershell
  Invoke-psake -taskList Build, Deploy          # build + deploy to local machine and the VMS server
  Invoke-psake -taskList Build, DeployLocally   # local-only (when the Event Server plugin is unaffected)
  Invoke-psake -taskList Build, DeployToVms     # VMS server only
  Invoke-psake -taskList Lint                   # dotnet format --verify-no-changes
  Invoke-psake -taskList Test                   # unit suite (builds first)
  Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~ProbeProxied' }  # filtered UI probes
  ```

  All native commands run inside psake `Exec`, so a non-zero exit code fails the task — check `$psake.build_success` (or the exit code), not just the console. `Lint` is green since the `chore: run dotnet format` commit; keep it green (run `dotnet format` and land reformatting as its own commit).
- The plugin csproj is SDK-style (`<Project Sdk="Microsoft.NET.Sdk">`, `TargetFramework` net472) — raw `dotnet build` also works directly; full MSBuild (VS 2022) also still works but nothing in this repo needs it anymore. The psake `Build` task builds with the default (Debug) config; pass `-c Release` manually for release drops.

  ```powershell
  dotnet build src/WebContent/WebContent.csproj -c Debug   # or -c Release
  ```

- x64: the project itself is `Platform=Any CPU` with `<PlatformTarget>x64</PlatformTarget>` set unconditionally; `src/WebContent.sln` maps its x64 solution platform onto the project's Any CPU config (same remap as UxTests). Never set per-`Platform` config conditions in that csproj — `dotnet build` defaults to Any CPU.
- The drop lives at `output\WebContent\` (`AppendTargetFrameworkToOutputPath=false` - the Setup harvest expects that exact path, no `net472` subfolder). Judge success by exit code plus files there (10 files + `runtimes` folder - the #72 helper added `WebContentTrustHelper.exe/.config/.pdb`; the WiX `Files` harvest picks them up automatically).
- Post-build steps are real MSBuild targets, not cmd `PostBuildEvent`s: `CleanHostAssemblies` deletes host-owned `VideoOS.*` (and satellite folders) from the drop — `$(OutDir)` is relative in SDK-style while wildcard items come back absolute, both are anchored via a `DropDirAbsolute` property, keep that or `RemoveDir` wipes the whole drop. `RestoreDotNetTools` + `GenerateSbom` run sbom-tool; a failure fails the build. `SbomGenerationGuardTests` regex-pin the sbom invocation (quoted absolute `-bc "$(MSBuildProjectDirectory)"`, `-D true`, one line) — update the guards together with the target. The `-b` drop argument must be quoted without a trailing backslash (a trailing `\` before the closing quote corrupts the rest of the command line).
- Full installer: `.\build.ps1` restores dotnet tools, caches the WebView2 runtime into `.cache`, then `dotnet build src/Setup/setup.wixproj -t:Rebuild` (WiX v7 wixproj builds under the dotnet CLI; no msbuild on PATH needed — the setup-msbuild CI step was removed with this).
- WiX version lockstep (ticket #31): the `wix` CLI version in `.config/dotnet-tools.json`, the `WixToolset.Sdk/<version>` attribute on `src/Setup/setup.wixproj`, and every `WixToolset.*` extension `PackageReference` in that project must all be bumped together to the same version — moving only one breaks the installer build. WiX major bumps also need the `<AcceptEula>` value refreshed (v7: `wix7`), and the v7 CLI refuses every command until that EULA is accepted (`dotnet build` passes it from the project property; ad hoc CLI usage needs `-acceptEula wix7`). Heat was removed in v7; the drop harvest in `src/Setup/files.wxs` uses the built-in `Files` element instead. Dependabot PRs that bump only some WiX surfaces must never be merged alone — close them and do a coordinated bump.

## Test

- `Invoke-psake -taskList Test` runs the unit suite (builds first); `dotnet test src/WebContent.Tests` works too (xunit). The test project is not in `src/WebContent.sln`; point at the csproj.
- `Invoke-psake -taskList TestUI` runs the whole `tests/ux/WebContent.UxTests` project, live probes included; pass `-parameters @{ TestFilter = 'FullyQualifiedName~...' }` to select rows. Do not run the UI suite casually: probes drive real desktop apps against the live VMS and some rows mutate live state (see `docs/verification-matrix.md`).
- The test project links pure source files via `<Compile Include="..\WebContent\..." Link>` and never project-references the plugin csproj. Register every new testable class in both places: `Compile` item in `WebContent.csproj` and `Compile` link in `WebContent.Tests.csproj`. Land it in one only and the plugin or the tests silently miss the class.

## Deploy for live-host testing

Use the psake deploy tasks (see Build); they encode the procedure below.

1. Local machine: `Invoke-psake -taskList Build, DeployLocally` — kills plugin hosts (`Client`, `VideoOS.Administration`), wipes and recreates `C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin` (the `INSTALLFOLDER` from `src/Setup/setup.wxs`), copies the drop. No elevation needed: the current user has full control on that folder (deliberate ACL grant on this machine — `C:\Program Files\VideoOS\MIPPlugins` is the MIP root; both `Milestone\MIPPlugins` and `VideoOS\MIPPlugins` are valid roots, OEM compatibility). If a copy ever fails on access denied, re-check the ACL before reaching for `Start-Process -Verb runas`; if hosts survive the Stop-Process, escalate to `-Force`.
2. Verify by file hash, not timestamp: `Copy-Item` preserves the source `LastWriteTime`, so a good copy can still look stale.
3. The Event Server machine (the proxy fetcher's home): `Invoke-psake -taskList Build, DeployToVms` — needs `.env` (`VMS_ADDRESS`, `VMS_CREDENTIAL`, …) and `.secretstore` (Clixml-protected password unlocking the SecretStore that holds the credential named `$env:VMS_CREDENTIAL`); both are gitignored. `Deploy-MipPlugin` in `scripts/helpers.ps1` copies the drop to a remote temp folder, kills the listed processes, stops `MilestoneEventServerService`, retries the destination wipe for up to a minute (the service stop is slow — the retry window absorbs the still-locked dll), copies into place, and restarts only the services it stopped. WinRM works with those credentials (bare negotiate/Kerberos does not). An ES restart disposes every running client's `MessageCommunication` — desktop clients must be restarted afterwards or every `TransmitMessage` dies with `Call to TransmitMessage after call to Dispose?`. `scripts/deploy-event-server-plugin.ps1` predates the psake flow and is superseded by `DeployToVms`.
4. Logs (verified on BEAST): Smart Client plugin `EnvironmentManager.Log` output lands in `%ProgramData%\Milestone\XProtect Smart Client\MIPLog.txt` (top level, plus rolling `MIPLogs\MIPLog.N.txt` — there is no Smart Client `Logs\` folder there); Management Client logs to `Logs\MIP.log` under its own ProgramData folder; the Event Server writes its MIP log to `%ProgramData%\Milestone\XProtect Event Server\logs\MIP.log` (rolled archives under `logs\MIPLogs\`; Event Server tray icon → "Show MIP logs"). Caveat: at least one Smart Client install has been observed loading the plugin view into a separate `VideoOS.OAuth2TokenGenerator.exe` process rather than `Client.exe` — if a session's plugin entries are missing from `MIPLog.txt` but the view visibly runs, hunt for the actual hosting process before concluding the plugin never loaded. `MIP.log`/`MIPLog.txt` can be absent mid-session or when client logging is disabled, so missing logs are no proof the plugin never ran.

## Agent skills

### Issue tracker

Issues are tracked in GitHub (`joshooaj/WebContentPlugin`). See `docs/agents/issue-tracker.md`.

### Triage labels

Default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout. See `docs/agents/domain.md`.
