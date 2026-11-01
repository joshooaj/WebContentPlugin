using System;
using System.Diagnostics;
using System.Text;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #58 (SC-5 demo): stop/start the XProtect Event Server service on the VMS machine over
/// PowerShell remoting, so a live Smart Client can be shown degrading to the proxy error page
/// while the server-side proxy component is down, and recovering afterwards. Follows the deploy
/// conventions in AGENTS.md: jhadmin credentials from the repo-root .env, the service's stop is
/// slow so the state is POLLED to Stopped/Running before the caller continues, and Start is
/// always safe to call (the probe's finally must be able to restore the VMS even on failure).
/// Credentials travel through the child process's environment, never the command line or a
/// script file.
/// </summary>
public static class EventServerControl
{
    public const string ServiceName = "MilestoneEventServerService";

    /// <summary>The outage row only runs when the operator explicitly opts in.</summary>
    public static bool OutageRowEnabled =>
        Environment.GetEnvironmentVariable("WEBCONTENT_ES_STOP") == "1";

    public static void Stop(UxConfig config, StringBuilder log, int waitSeconds = 300) =>
        Run(config, "Stop", log, waitSeconds);

    public static void Start(UxConfig config, StringBuilder log, int waitSeconds = 300) =>
        Run(config, "Start", log, waitSeconds);

    private static void Run(UxConfig config, string action, StringBuilder log, int waitSeconds)
    {
        var host = Environment.GetEnvironmentVariable("UX_ES_HOST") ?? config.VmsAddress;
        log.AppendLine($"remote {action} of {ServiceName} on {host}...");

        var script = $@"
$ErrorActionPreference = 'Stop'
$password = ConvertTo-SecureString $env:WEBCONTENT_ES_PASSWORD -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($env:WEBCONTENT_ES_USER, $password)
$session = New-PSSession -ComputerName '{host}' -Credential $credential
try {{
    Invoke-Command -Session $session -ArgumentList $env:WEBCONTENT_ES_ACTION, $env:WEBCONTENT_ES_WAIT_SECONDS, '{ServiceName}' -ScriptBlock {{
        param($action, $waitSeconds, $serviceName)
        $ErrorActionPreference = 'Stop'
        $deadline = (Get-Date).AddSeconds($waitSeconds)
        $service = Get-Service -Name $serviceName
        if ($action -eq 'Stop') {{
            if ($service.Status -ne 'Stopped') {{
                Write-Output ('stopping ' + $serviceName + ' (was ' + $service.Status + ')')
                Stop-Service -Name $serviceName -Force
                $target = 'Stopped'
            }} else {{ $target = 'Stopped' }}
        }} else {{
            if ($service.Status -ne 'Running') {{
                Write-Output ('starting ' + $serviceName + ' (was ' + $service.Status + ')')
                Start-Service -Name $serviceName
                $target = 'Running'
            }} else {{ $target = 'Running' }}
        }}
        while ((Get-Service -Name $serviceName).Status -ne $target -and (Get-Date) -lt $deadline) {{
            Start-Sleep -Seconds 2
        }}
        $final = (Get-Service -Name $serviceName).Status
        Write-Output ($serviceName + ' final status: ' + $final)
        if ($final -ne $target) {{
            throw ('service ' + $serviceName + ' did not reach ' + $target + ' within ' + $waitSeconds + 's (now ' + $final + ')')
        }}
    }}
}} finally {{
    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
}}
";

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShell(),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["WEBCONTENT_ES_USER"] = config.VmsUsername;
        startInfo.Environment["WEBCONTENT_ES_PASSWORD"] = config.VmsPassword;
        startInfo.Environment["WEBCONTENT_ES_ACTION"] = action;
        startInfo.Environment["WEBCONTENT_ES_WAIT_SECONDS"] = waitSeconds.ToString();

        using var process = Process.Start(startInfo);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        foreach (var line in stdout.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line)) log.AppendLine("  es: " + line.Trim());
        }

        if (process.ExitCode != 0)
        {
            log.AppendLine($"  es {action} FAILED (exit {process.ExitCode}): {stderr.Trim()}");
            throw new InvalidOperationException($"Remote {action} of {ServiceName} on {host} failed: {stderr.Trim()}");
        }

        log.AppendLine($"  {ServiceName} {action.ToLowerInvariant()} confirmed");
    }

    private static string ResolvePowerShell()
    {
        // pwsh (PowerShell 7) is the repo's shell; Windows PowerShell 5.1 is the fallback.
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh)) return pwsh;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}
