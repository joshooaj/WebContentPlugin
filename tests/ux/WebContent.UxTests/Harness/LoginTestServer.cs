using System.Net;
using System.Net.Sockets;
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
/// </summary>
public sealed class LoginTestServer : IDisposable
{
    public const string Username = "acmeadmin";
    public const string Password = "camera1";

    private readonly TcpListener _listener;
    private readonly HashSet<string> _issuedSessions = new();
    private readonly List<string> _journal = new();

    /// <summary>The SID the last successful login issued (asserted against the browser's echo).</summary>
    public string LastIssuedSessionId { get; private set; }

    public int Port { get; }

    public IReadOnlyList<string> Journal { get { lock (_journal) return _journal.ToList(); } }

    public LoginTestServer(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public void Dispose() => _listener.Stop();

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
            using var stream = client.GetStream();
            try
            {
                await HandleAsync(stream);
            }
            catch (Exception exception)
            {
                lock (_journal)
                {
                    _journal.Add($"request aborted: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }

    private async Task HandleAsync(NetworkStream stream)
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

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
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

    private static async Task WriteAsync(NetworkStream stream, byte[] response)
    {
        await stream.WriteAsync(response, 0, response.Length);
        await stream.FlushAsync();
    }

    private static string Truncate(string value, int max) =>
        value == null ? string.Empty : (value.Length <= max ? value : value.Substring(0, max) + "...");

    private static string HtmlEscape(string value) =>
        (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
