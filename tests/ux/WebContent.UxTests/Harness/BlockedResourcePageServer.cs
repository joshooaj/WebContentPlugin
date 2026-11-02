using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Deterministic page pair for the #83 blocked-resources indicator probe (SC-7): a page that
/// loads FINE through the proxy while its subresources point at an origin the workspace does
/// NOT allowlist (two images plus a script), and a clean twin with no subresource references.
/// The blocked-origin port deliberately serves nothing - the loopback listener refuses those
/// requests client-side (allowlist 403) before the Event Server is ever asked, which is
/// exactly the half-broken-page posture the indicator exists to surface.
///
/// Raw TcpListener on IPAddress.Any (like <see cref="LoginTestServer"/>): HttpListener needs a
/// urlacl reservation to bind a LAN address unelevated, and the Event Server - not the local
/// browser - is the machine that must be able to fetch the page origin.
/// </summary>
public sealed class BlockedResourcePageServer : IDisposable
{
    private const string BlockedPage = """
        <!DOCTYPE html><html><head><title>UX Probe BLOCKEDPAGE</title>
        <style>body{{font-family:Segoe UI;background:#10203a;color:#fff;display:flex;
        align-items:center;justify-content:center;height:96vh}}
        .probe{{border:3px solid #37d0ff;padding:40px 60px;border-radius:12px}}</style></head>
        <body>
        <div class="probe" id="probe-marker" data-probe="blocked">UX-PROBE-BLOCKEDPAGE</div>
        <img src="{blocked}/one.png" alt="one">
        <img src="{blocked}/two.png" alt="two">
        <script src="{blocked}/blocked.js"></script>
        </body></html>
        """;

    private const string CleanPage = """
        <!DOCTYPE html><html><head><title>UX Probe CLEANPAGE</title>
        <style>body{{font-family:Segoe UI;background:#10203a;color:#fff;display:flex;
        align-items:center;justify-content:center;height:96vh}}
        .probe{{border:3px solid #37d0ff;padding:40px 60px;border-radius:12px}}</style></head>
        <body><div class="probe" id="probe-marker" data-probe="clean">UX-PROBE-CLEANPAGE</div></body></html>
        """;

    private readonly TcpListener _listener;

    /// <summary>The origin the page is served from - the allowlist entry the probe provisions.</summary>
    public string PageOrigin { get; }

    /// <summary>The origin every subresource points at - deliberately NOT allowlisted.</summary>
    public string BlockedOrigin { get; }

    public BlockedResourcePageServer(string bindHost, int pagePort, int blockedOriginPort)
    {
        PageOrigin = $"http://{bindHost}:{pagePort}";
        BlockedOrigin = $"http://{bindHost}:{blockedOriginPort}";

        _listener = new TcpListener(IPAddress.Any, pagePort);
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
            catch
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
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                // Read request headers; the body (if any) is irrelevant for these GETs.
                var reader = new StreamReader(stream, Encoding.ASCII);
                string requestLine = null;
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null) return;
                    if (line.Length == 0) break;
                    requestLine ??= line;
                }

                var path = requestLine != null && requestLine.Split(' ').Length > 1 ? requestLine.Split(' ')[1] : "/";
                var body = path.Contains("clean", System.StringComparison.OrdinalIgnoreCase)
                    ? string.Format(CleanPage)
                    : BlockedPage.Replace("{blocked}", BlockedOrigin);
                var bytes = Encoding.UTF8.GetBytes(body);

                var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            catch
            {
                // throwaway probe server: a broken client connection is not a test failure
            }
        }
    }
}
