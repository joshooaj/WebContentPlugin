using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Throwaway vendor-style login site for the #55 proxy login-flow probe (SC-2). A raw
/// TcpListener rather than HttpListener: HttpListener needs a urlacl reservation to bind a
/// LAN address unelevated, and the raw socket gives the probe exact control over multi-value
/// Set-Cookie framing — the same control the proxy's own listener relies on.
///
/// It plays the camera-portal role the ticket names: a login form (POST), a session cookie
/// issued with a 302, a dashboard that only answers when the cookie comes back, and an XHR
/// endpoint the dashboard polls in the background. Every request is journaled so probe
/// artifacts show what actually crossed the proxy.
///
/// #56 adds a TLS mode: the same site behind a self-signed certificate minted in-process, so
/// the Event Server's target TLS validation (and the per-target accept-untrusted override)
/// has a genuinely untrusted chain to decide on.
/// </summary>
public sealed class LoginTestServer : IDisposable
{
    public const string Username = "acmeadmin";
    public const string Password = "camera1";

    private readonly TcpListener _listener;
    private readonly X509Certificate2 _tlsCertificate;
    private readonly HashSet<string> _issuedSessions = new();
    private readonly List<string> _journal = new();

    /// <summary>The SID the last successful login issued (asserted against the browser's echo).</summary>
    public string LastIssuedSessionId { get; private set; }

    public int Port { get; }

    public IReadOnlyList<string> Journal { get { lock (_journal) return _journal.ToList(); } }

    public LoginTestServer(int port) : this(port, useTls: false)
    {
    }

    /// <param name="useTls">Serve over HTTPS with an in-process self-signed certificate —
    /// untrusted by design, which is exactly what the #56 fail-closed / override probes need.
    /// The certificate carries the bind host as SAN so the failure is chain-trust, not name mismatch.</param>
    public LoginTestServer(int port, bool useTls)
    {
        Port = port;
        if (useTls)
        {
            _tlsCertificate = CreateSelfSignedCertificate();
        }

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _listener.Stop();
        _tlsCertificate?.Dispose();
    }

    /// <summary>
    /// RSA self-signed certificate valid for the addresses the probe binds (the VMS-network
    /// address the allowlist names, plus loopback for local poking). KeyUsage/EKU mirror a real
    /// camera portal's server certificate; only the chain is untrusted.
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ACME IPCStore (self-signed test)",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.Parse("172.16.128.210"));
        san.AddDnsName(Environment.MachineName.ToLowerInvariant());
        request.CertificateExtensions.Add(san.Build());

        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        // PFX roundtrip lands the key in a persisted form Schannel accepts server-side.
        var password = Guid.NewGuid().ToString("N");
        var portable = created.Export(X509ContentType.Pkcs12, password);
        return X509CertificateLoader.LoadPkcs12(portable, password);
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            Stream stream = client.GetStream();
            try
            {
                if (_tlsCertificate != null)
                {
                    var tls = new System.Net.Security.SslStream(stream, false);
                    stream = tls;
                    try
                    {
                        await tls.AuthenticateAsServerAsync(
                            _tlsCertificate, false, SslProtocols.Tls12, false);
                    }
                    catch (Exception handshake)
                    {
                        // A failed client handshake is itself probe data (e.g. the fetcher
                        // refusing the chain mid-TLS is a clean close, not a request).
                        lock (_journal)
                        {
                            _journal.Add($"TLS handshake failed: {handshake.GetType().Name}: {handshake.Message}");
                        }

                        return;
                    }
                }

                await HandleAsync(stream);
            }
            catch (Exception exception)
            {
                lock (_journal)
                {
                    _journal.Add($"request aborted: {exception.GetType().Name}: {exception.Message}");
                }
            }
            finally
            {
                stream.Dispose();
            }
        }
    }

    private async Task HandleAsync(Stream stream)
    {
        var head = await ReadHeadAsync(stream);
        if (head == null)
        {
            return;
        }

        var lines = head.Replace("\n", "\r\n").Split(new[] { "\r\n" }, StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3)
        {
            return;
        }

        var method = requestLine[0].ToUpperInvariant();
        var path = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var line = 1; line < lines.Length && lines[line].Length > 0; line++)
        {
            var colon = lines[line].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[line].Substring(0, colon).Trim()] = lines[line].Substring(colon + 1).Trim();
            }
        }

        string body = null;
        if (headers.TryGetValue("Content-Length", out var lengthText) &&
            int.TryParse(lengthText, out var length) && length > 0)
        {
            var buffer = new byte[length];
            var readTotal = 0;
            while (readTotal < length)
            {
                var read = await stream.ReadAsync(buffer, readTotal, length - readTotal);
                if (read <= 0)
                {
                    break;
                }

                readTotal += read;
            }

            body = Encoding.UTF8.GetString(buffer, 0, readTotal);
        }

        headers.TryGetValue("Cookie", out var cookieHeader);
        lock (_journal)
        {
            _journal.Add($"{method} {path} body={body?.Length ?? 0}B cookie='{Truncate(cookieHeader, 120)}'");
        }

        switch (method, path)
        {
            case ("GET", "/"):
                await WriteAsync(stream, Page(200, "ACME IPCStore - Login", LoginForm, null));
                return;

            case ("POST", "/login"):
                var form = body ?? string.Empty;
                if (form.Contains($"username={Username}") && form.Contains($"password={Password}"))
                {
                    var session = Guid.NewGuid().ToString("N");
                    lock (_issuedSessions)
                    {
                        _issuedSessions.Add(session);
                    }

                    LastIssuedSessionId = session;
                    await WriteAsync(stream, Page(
                        302,
                        "Found",
                        "<html><body>redirecting...</body></html>",
                        $"SID={session}; Path=/; HttpOnly",
                        "/dashboard"));
                    return;
                }

                await WriteAsync(stream, Page(401, "ACME IPCStore - Login failed",
                    "<h2>ACME IPCStore: bad credentials</h2>" + LoginForm, null));
                return;

            case ("GET", "/dashboard"):
                if (!HasValidSession(cookieHeader))
                {
                    await WriteAsync(stream, Page(403, "ACME IPCStore - Not signed in",
                        "<div id=\"signed-out\">NOT SIGNED IN</div>", null));
                    return;
                }

                await WriteAsync(stream, Page(200, "ACME Portal - Dashboard",
                    """
                    <h2>ACME Camera Portal</h2>
                    <div id="signed-in">SIGNED IN</div>
                    <div id="cookie-echo">COOKIE-HEADER [[{0}]]</div>
                    <div id="xhr-status">XHR-PENDING</div>
                    <script>fetch('/api/status').then(function(r){return r.text();})
                      .then(function(t){document.getElementById('xhr-status').textContent=t;});</script>
                    """.Replace("{0}", HtmlEscape(cookieHeader)), null));
                return;

            case ("GET", "/api/status"):
                if (!HasValidSession(cookieHeader))
                {
                    await WriteAsync(stream, Plain(403, "XHR-DENIED"));
                    return;
                }

                await WriteAsync(stream, Plain(200, $"XHR-OK status=online cookie='{Truncate(cookieHeader, 60)}'"));
                return;

            default:
                await WriteAsync(stream, Plain(404, "not found"));
                return;
        }
    }

    private bool HasValidSession(string cookieHeader)
    {
        if (string.IsNullOrEmpty(cookieHeader))
        {
            return false;
        }

        foreach (var part in cookieHeader.Split(';'))
        {
            var pair = part.Trim();
            if (!pair.StartsWith("SID=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = pair.Substring(4);
            lock (_issuedSessions)
            {
                if (_issuedSessions.Contains(value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private const string LoginForm = """
        <form method="post" action="/login">
        <label>user <input name="username" id="username" type="text"></label>
        <label>pass <input name="password" id="password" type="password"></label>
        <button type="submit" id="signin">Sign in</button>
        </form>
        """;

    private static byte[] Page(
        int status,
        string title,
        string bodyHtml,
        string setCookie,
        string redirectLocation = null,
        string contentType = "text/html; charset=utf-8")
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!DOCTYPE html><html><head><title>{title}</title></head><body>{bodyHtml}</body></html>");
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 302 ? "Found" : "Status")}\r\n");
        head.Append($"Content-Type: {contentType}\r\n");
        if (setCookie != null)
        {
            head.Append($"Set-Cookie: {setCookie}\r\n");
        }

        if (redirectLocation != null)
        {
            head.Append($"Location: {redirectLocation}\r\n");
        }

        head.Append($"Content-Length: {body.Length}\r\n");
        head.Append("Connection: close\r\n\r\n");
        return Encoding.UTF8.GetBytes(head.ToString()).Concat(body).ToArray();
    }

    /// <summary>
    /// Raw text/plain response (no HTML document shell): an XHR endpoint's body IS its payload,
    /// so the dashboard can assert the exact text the fetcher carried back.
    /// </summary>
    private static byte[] Plain(int status, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 403 ? "Forbidden" : "Not Found")}\r\n");
        head.Append("Content-Type: text/plain; charset=utf-8\r\n");
        head.Append($"Content-Length: {body.Length}\r\n");
        head.Append("Connection: close\r\n\r\n");
        return Encoding.UTF8.GetBytes(head.ToString()).Concat(body).ToArray();
    }

    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        var received = new MemoryStream();
        var buffer = new byte[4096];
        while (received.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return null;
            }

            received.Write(buffer, 0, read);
            var bytes = received.GetBuffer();
            for (var i = 0; i + 3 < received.Length; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                {
                    return Encoding.UTF8.GetString(bytes, 0, i);
                }
            }
        }

        return null;
    }

    private static async Task WriteAsync(Stream stream, byte[] response)
    {
        await stream.WriteAsync(response, 0, response.Length);
        await stream.FlushAsync();
    }

    private static string Truncate(string value, int max) =>
        value == null ? string.Empty : (value.Length <= max ? value : value.Substring(0, max) + "...");

    private static string HtmlEscape(string value) =>
        (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
