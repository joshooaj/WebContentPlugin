using System.Net;
using System.Text;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Serves deterministic probe pages so screenshots and DOM assertions never depend
/// on external websites. URLs: {Base}alpha and {Base}beta.
/// </summary>
public sealed class ProbePageServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private const string Page = """
        <!DOCTYPE html><html><head><title>UX Probe {0}</title>
        <style>body{{font-family:Segoe UI;background:#10203a;color:#fff;display:flex;
        align-items:center;justify-content:center;height:96vh}}
        .probe{{border:3px solid #37d0ff;padding:40px 60px;border-radius:12px}}</style></head>
        <body><div class="probe" id="probe-marker" data-probe="{0}">UX-PROBE-{0}</div></body></html>
        """;

    public string Base { get; }

    public ProbePageServer()
    {
        var port = GetFreePort();
        Base = $"http://127.0.0.1:{port}/probe/";
        _listener.Prefixes.Add(Base);
        _listener.Start();
        Task.Run(() =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    var name = ctx.Request.Url.AbsolutePath.Trim('/').Replace("probe/", "");
                    if (name != "alpha" && name != "beta") name = "alpha";
                    var bytes = Encoding.UTF8.GetBytes(string.Format(Page, name.ToUpperInvariant()));
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch
                {
                    // keep serving
                }
            }
        });
    }

    public void Dispose() => _listener.Close();

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
