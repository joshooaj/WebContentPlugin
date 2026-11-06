using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #67 (SC-8): a minimal plain-HTTP forward proxy whose ONLY job is to be the OS
/// (WinINET) proxy the WebView2 system-proxy resolver discovers. It answers every
/// absolute-form http request with a synthetic marker page and journals each request head,
/// so "the marker page rendered" IS the proof the browser routed through the OS-configured
/// proxy: the requested host sits on the .invalid TLD (RFC 2606), which can never resolve,
/// so a direct route physically cannot serve it. CONNECT is journaled and refused (403) -
/// nothing is ever tunnelled - and origin-form junk gets a plain 400. The harness never
/// makes an upstream connection of its own.
///
/// One request per connection (Connection: close): the probe only needs the journal, and a
/// short-lived connection keeps the parser honest about where a request head ends.
/// </summary>
public sealed class WinInetProxyHarness : IDisposable
{
    /// <summary>Title of the synthetic page the harness serves; the probe asserts it.</summary>
    public const string PageTitle = "WebContent OS-proxy harness";

    /// <summary>Token only this harness puts in a body; its presence proves who served the page.</summary>
    public const string BodyMarker = "WININET-OS-PROXY-ROUTE-CONFIRMED";

    /// <summary>One journaled request head, exactly as the proxy socket saw it.</summary>
    public sealed record JournalEntry(DateTime SeenAtUtc, string Method, string RequestTarget, string UserAgent);

    private readonly TcpListener _listener;
    private readonly List<JournalEntry> _journal = new();
    private readonly object _journalGate = new();
    private volatile bool _stopping;

    public int Port { get; }

    public WinInetProxyHarness()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Journal entries whose request target contains the given fragment.</summary>
    public List<JournalEntry> RequestsFor(string targetFragment)
    {
        lock (_journalGate)
        {
            return _journal
                .Where(e => e.RequestTarget.IndexOf(targetFragment ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }
    }

    public List<JournalEntry> Journal()
    {
        lock (_journalGate)
        {
            return _journal.ToList();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _listener.Stop(); } catch { }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                return; // listener stopped (Dispose)
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var head = await ReadRequestHeadAsync(stream).ConfigureAwait(false);
                if (head == null) return;

                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var requestLine = (lines[0] ?? string.Empty).Split(new[] { ' ' }, 3);
                if (requestLine.Length < 2)
                {
                    await WriteRawAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    return;
                }

                var method = requestLine[0].ToUpperInvariant();
                var target = requestLine[1];
                var userAgent = lines.Skip(1)
                    .FirstOrDefault(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))
                    ?.Substring("User-Agent:".Length).Trim() ?? string.Empty;

                lock (_journalGate)
                {
                    _journal.Add(new JournalEntry(DateTime.UtcNow, method, target, userAgent));
                }

                if (method == "CONNECT")
                {
                    // Journaled and refused: the probe only exercises plain http, and a harness
                    // that tunnelled would blur what "served by the OS proxy" means.
                    await WriteRawAsync(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    return;
                }

                if ((method == "GET" || method == "HEAD") &&
                    target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    var body = BuildPage(target);
                    var response = "HTTP/1.1 200 OK\r\n" +
                                   "Content-Type: text/html; charset=utf-8\r\n" +
                                   $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                                   "Cache-Control: no-store\r\n" +
                                   "Connection: close\r\n\r\n";
                    await WriteRawAsync(stream, response).ConfigureAwait(false);
                    if (method == "GET")
                        await WriteRawAsync(stream, body).ConfigureAwait(false);
                    return;
                }

                await WriteRawAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
            }
            catch
            {
                // A dead client mid-exchange is data, not an error: the journal already holds
                // whatever head arrived.
            }
        }
    }

    private static async Task<string> ReadRequestHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var collected = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && collected.Length < 65536)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0) break;
            collected.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (collected.ToString().Contains("\r\n\r\n")) break;
        }

        var head = collected.ToString();
        var end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return end < 0 ? (head.Length == 0 ? null : head) : head.Substring(0, end);
    }

    private static string BuildPage(string requestedTarget) =>
        "<!DOCTYPE html><html><head><title>" + PageTitle + "</title></head><body>" +
        $"<h1>{BodyMarker}</h1>" +
        $"<p id=\"wdc-target\">Served by the OS-configured (WinINET) proxy harness for: {WebUtility.HtmlEncode(requestedTarget)}</p>" +
        $"<p id=\"wdc-time\">{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}</p>" +
        "</body></html>";

    private static async Task WriteRawAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
