# WebView2 lifetime probe (#90, throwaway)

Answers one question for the #91 enablement design: when two host processes attach to one
WebView2 user data folder with identical options, what happens to the shared browser process
when the host that CREATED it exits, cleanly or crashed?

Not shipped. Never referenced by the plugin. Built automatically with the UxTests project.

## Run

Through the repo's TestUI flow (the rows need no VMS):

```powershell
Invoke-psake -taskList TestUI -parameters @{ TestFilter = 'FullyQualifiedName~ProbeLifetime' }
```

Standalone host roles (the orchestrating tests do this; rarely needed by hand):

```powershell
spike\WebView2LifetimeProbe\bin\Debug\net10.0-windows\WebView2LifetimeProbe.exe --role creator --dir C:\somewhere\artifacts
spike\WebView2LifetimeProbe\bin\Debug\net10.0-windows\WebView2LifetimeProbe.exe --role joiner  --dir C:\somewhere\artifacts [--spawn-second-after 15000]
```

Each role journals `INIT_OK` / `TICK` / `PROCESS_FAILED` / `TICK_FAIL` / `SECOND_INIT_OK|FAIL`
to `status-<role>.log` in the artifact dir. The test rows read the journals plus a live
`msedgewebview2` WMI snapshot as independent witnesses.

## Answer (measured 2026-11-03, see #90 findings)

The shared browser process is tied to the attached hosts as a set, not to the creator:

- Creator exits cleanly, joiner still attached: browser process survives, joiner's page keeps running.
- Creator host hard-killed (host-only kill; Chromium children untouched): browser process STILL survives, joiner keeps ticking; no ProcessFailed.
- Browser process actually gone (row 6 tears it down via tree-kill of the creator host, since rows 2/3 prove WebView2 itself never ends the collection on creator exit; or when the last attached host leaves): attached views die with `ProcessFailed(BrowserProcessExited)`, and a FRESH `EnsureCoreWebView2Async` in any surviving process respawns the collection (row 6 asserts `SECOND_INIT_OK`, observed ~13 s).
