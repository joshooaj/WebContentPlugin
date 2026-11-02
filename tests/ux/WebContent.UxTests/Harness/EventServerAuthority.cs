using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #75 (MC-15): read the deployment proxy CA that lives on the Event Server machine, so a
/// live probe can compare what the Management Client exported against what the server actually
/// holds. Strictly a READ: the remote block opens LocalMachine\Personal ReadOnly|OpenExistingOnly
/// (the same posture the export responder uses) and returns public DER only - no store write, no
/// private key. Remoting follows the <see cref="EventServerControl"/> convention: jhadmin
/// credentials travel through the child process's environment, never the command line.
/// </summary>
public static class EventServerAuthority
{
    /// <summary>The fixed deployment authority CN (ProxyLoopbackTls.AuthorityCommonName).</summary>
    public const string AuthorityCommonName = "XProtect WebContent Plugin Loopback Proxy";

    /// <summary>
    /// The Event Server machine's LocalMachine\Personal copy of the deployment CA, or null when the
    /// machine has none yet (the posture whose export refusal the #75 AC demands).
    /// </summary>
    public static X509Certificate2 ReadDeploymentAuthority(UxConfig config, StringBuilder log)
    {
        var address = Environment.GetEnvironmentVariable("UX_ES_HOST") ?? config.VmsAddress;
        var host = address.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(address, UriKind.Absolute, out var addressUri)
            ? addressUri.Host
            : address;
        log.AppendLine($"reading LocalMachine\\Personal CN='{AuthorityCommonName}' on {host} (read-only)...");

        var script = $@"
$ErrorActionPreference = 'Stop'
$password = ConvertTo-SecureString $env:WEBCONTENT_ES_PASSWORD -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($env:WEBCONTENT_ES_USER, $password)
$session = New-PSSession -ComputerName '{host}' -Credential $credential
try {{
    Invoke-Command -Session $session -ArgumentList '{AuthorityCommonName}' -ScriptBlock {{
        param($commonName)
        $ErrorActionPreference = 'Stop'
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor [System.Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
        try {{
            $found = @($store.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindBySubjectName, $commonName, $false))
            Write-OUTPUT ('COUNT|' + $found.Count)
            foreach ($certificate in $found) {{
                Write-OUTPUT ('CERT|' + $certificate.Thumbprint + '|' + $certificate.NotAfter.ToString('o') + '|' + [Convert]::ToBase64String($certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)))
            }}
        }} finally {{
            $store.Close()
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

        using var process = Process.Start(startInfo);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            log.AppendLine($"  es-authority read FAILED (exit {process.ExitCode}): {stderr.Trim()}");
            throw new InvalidOperationException($"Reading the deployment authority on {host} failed: {stderr.Trim()}");
        }

        X509Certificate2 chosen = null;
        var chosenNotAfter = DateTime.MinValue;
        foreach (var line in stdout.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("COUNT|", StringComparison.Ordinal))
            {
                log.AppendLine($"  es-authority: {text}");
                continue;
            }

            if (!text.StartsWith("CERT|", StringComparison.Ordinal)) continue;
            var parts = text.Substring("CERT|".Length).Split('|', 3);
            if (parts.Length != 3) continue;
            var der = Convert.FromBase64String(parts[2]);
            var certificate = X509CertificateLoader.LoadCertificate(der);
            log.AppendLine($"  es-authority: thumbprint {parts[0]}, notAfter {parts[1]}, {der.Length} DER bytes, private key here={certificate.HasPrivateKey}");
            if (certificate.NotAfter > chosenNotAfter)
            {
                chosen = certificate;
                chosenNotAfter = certificate.NotAfter;
            }
        }

        return chosen;
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
