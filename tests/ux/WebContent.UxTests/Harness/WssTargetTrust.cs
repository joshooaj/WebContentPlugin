using System;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #60 (SC-6 demo): make the Event Server trust the wss probe target's certificate.
/// The tunnel TLS leg keeps FULL chain validation on the Event Server (the accept-untrusted
/// override is a deliberate follow-on ticket), so the success row needs the probe's self-signed
/// certificate to sit in the Event Server machine's LocalMachine\Root store. Import follows the
/// same conventions as <see cref="EventServerControl"/>: jhadmin credentials from the repo-root
/// .env, PowerShell remoting, credentials through the child process's environment only. The
/// remote block is idempotent and self-cleaning: certificates previously imported under the
/// probe CN are removed before the fresh one is added, so repeat runs never stack roots.
/// Set UX_WSS_TRUST_IMPORT=0 when the target certificate is trusted by other means.
/// </summary>
public static class WssTargetTrust
{
    /// <summary>Subject CN marking every probe-minted certificate; the import purges stale
    /// siblings of this CN before adding the new one.</summary>
    public const string ProbeCertificateCommonName = "WebContent wss tunnel probe (test only)";

    /// <summary>False when the operator opted out (UX_WSS_TRUST_IMPORT=0) because the target
    /// certificate is already trusted on the Event Server by other means.</summary>
    public static bool ImportEnabled =>
        Environment.GetEnvironmentVariable("UX_WSS_TRUST_IMPORT") != "0";

    public static void ImportOnEventServer(UxConfig config, X509Certificate2 certificate, StringBuilder log)
    {
        // The same host sources as EventServerControl (UX_ES_HOST or VMS_ADDRESS), but a
        // tolerant parse: the lab .env VMS_ADDRESS is a bare hostname, so this only takes a
        // uri host when the value actually carries a scheme.
        var address = Environment.GetEnvironmentVariable("UX_ES_HOST") ?? config.VmsAddress;
        var host = address.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(address, UriKind.Absolute, out var addressUri)
            ? addressUri.Host
            : address;
        log.AppendLine($"importing the probe certificate (thumbprint {certificate.Thumbprint}) into LocalMachine\\Root on {host}...");

        // Public DER only: the trust store needs the certificate, never the private key.
        var publicDer = Convert.ToBase64String(certificate.Export(X509ContentType.Cert));

        var script = $@"
$ErrorActionPreference = 'Stop'
$password = ConvertTo-SecureString $env:WEBCONTENT_ES_PASSWORD -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential($env:WEBCONTENT_ES_USER, $password)
$session = New-PSSession -ComputerName '{host}' -Credential $credential
try {{
    Invoke-Command -Session $session -ArgumentList ([Convert]::FromBase64String($env:WEBCONTENT_WSS_CERT_DER)), '{ProbeCertificateCommonName}' -ScriptBlock {{
        param($certificateBytes, $commonName)
        $ErrorActionPreference = 'Stop'
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(,$certificateBytes)
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
        $store.Open('ReadWrite')
        try {{
            $stale = @($store.Certificates | Where-Object {{ $_.Subject -eq ('CN=' + $commonName) -and $_.Thumbprint -ne $certificate.Thumbprint }})
            foreach ($old in $stale) {{
                $store.Remove($old)
                Write-Output ('removed stale probe certificate ' + $old.Thumbprint)
            }}
            if (-not ($store.Certificates | Where-Object {{ $_.Thumbprint -eq $certificate.Thumbprint }})) {{
                $store.Add($certificate)
                Write-Output ('imported probe certificate ' + $certificate.Thumbprint)
            }} else {{
                Write-Output ('probe certificate ' + $certificate.Thumbprint + ' already present')
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
        startInfo.Environment["WEBCONTENT_WSS_CERT_DER"] = publicDer;

        using var process = Process.Start(startInfo);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        foreach (var line in stdout.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line)) log.AppendLine("  es-trust: " + line.Trim());
        }

        if (process.ExitCode != 0)
        {
            log.AppendLine($"  certificate import FAILED (exit {process.ExitCode}): {stderr.Trim()}");
            throw new InvalidOperationException($"Importing the wss probe certificate into LocalMachine\\Root on {host} failed: {stderr.Trim()}");
        }

        log.AppendLine($"  probe certificate trusted on {host}");
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
