using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Ticket #57 acceptance target: a vendor-style page that opens a WEBSOCKET, served from this
/// machine on one port. Plain HTTP GETs serve the probe page; a GET carrying the RFC 6455
/// upgrade headers is answered with a real 101 handshake and the connection then echoes every
/// client frame back verbatim. The probe page shows "WS-OK:&lt;token&gt;" only when its token
/// survived the full browser -> loopback proxy -> MIP messaging -> Event Server raw-socket
/// round trip, and the journal records the handshake and every echoed frame for server-side
/// evidence. No external websocket library: the harness speaks the raw RFC 6455 frames itself,
/// exactly like the tunnel it is testing.
///
/// #60 adds a TLS mode: every accepted connection first runs a server-side SslStream under an
/// in-process self-signed certificate (journal: TLS-CONNECT/TLS-HANDSHAKE/TLS-FAULT), so the
/// same echo target can sit behind an https origin and exercise the Event Server's wss
/// handshake leg. probePageHtml replaces the served page (the wss probe dials a different
/// origin from the page it loads on).
/// </summary>
public sealed class WebSocketEchoServer : IDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly List<string> _journal = new();
    private readonly X509Certificate2 _tlsCertificate;
    private readonly string _probePageHtml;
    private volatile bool _stopping;

    /// <summary>The in-process TLS certificate when useTls was requested (null otherwise). The
    /// wss probe exports this and has the Event Server trust it for the validated-target row.</summary>
    public X509Certificate2 TlsCertificate => _tlsCertificate;

    public WebSocketEchoServer(int port) : this(port, useTls: false, tlsHost: null, probePageHtml: null)
    {
    }

    /// <param name="useTls">Accept connections under an in-process self-signed certificate
    /// (server-auth EKU, SAN for tlsHost + loopback), which is exactly what the #60 wss rows
    /// need: untrusted for the fail-closed row, imported into the Event Server's trust store
    /// for the validated row.</param>
    /// <param name="tlsHost">Host the https origin names; becomes a certificate SAN so the
    /// failure on the fail-closed row is chain trust, never name mismatch.</param>
    /// <param name="probePageHtml">Replaces the default ws page for GETs (the wss probe's page
    /// lives on a plain http origin and dials the https origin by absolute url).</param>
    public WebSocketEchoServer(int port, bool useTls, string tlsHost = null, string probePageHtml = null)
    {
        if (useTls)
        {
            _tlsCertificate = CreateSelfSignedCertificate(tlsHost);
        }
        _probePageHtml = probePageHtml;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public IReadOnlyList<string> Journal
    {
        get
        {
            lock (_journal)
            {
                return _journal.ToList();
            }
        }
    }

    private void Note(string entry)
    {
        lock (_journal)
        {
            _journal.Add(entry);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping)
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
            var stream = client.GetStream();
            try
            {
                Stream requestStream = stream;
                if (_tlsCertificate != null)
                {
                    Note("TLS-CONNECT peer");
                    var tls = new SslStream(stream, false);
                    try
                    {
                        await tls.AuthenticateAsServerAsync(_tlsCertificate, false, SslProtocols.Tls12, false);
                        Note("TLS-HANDSHAKE ok");
                    }
                    catch (Exception tlsFault)
                    {
                        // A client that refuses the chain mid-TLS is a clean close, not a
                        // request: the journal entry is the evidence the leg was dialed.
                        Note($"TLS-FAULT {tlsFault.GetType().Name}: {tlsFault.Message}");
                        tls.Dispose();
                        return;
                    }
                    requestStream = tls;
                }

                var head = await ReadHeadAsync(requestStream);
                if (head == null)
                {
                    return;
                }

                var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var requestLine = lines.Length > 0 ? lines[0] : "";
                var parts = requestLine.Split(' ');
                var path = parts.Length > 1 ? parts[1] : "";
                var upgraded = head.Contains("upgrade: websocket", StringComparison.OrdinalIgnoreCase) ||
                    head.Contains("Upgrade: websocket", StringComparison.Ordinal);

                if (upgraded && path.StartsWith("/echo", StringComparison.Ordinal))
                {
                    await EchoWebSocketAsync(requestStream, head, path);
                    return;
                }

                Note($"GET {path}");
                var body = BuildProbePage();
                var bytes = Encoding.UTF8.GetBytes(body);
                var response =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {bytes.Length}\r\n" +
                    "Connection: close\r\n" +
                    "\r\n";
                await requestStream.WriteAsync(Encoding.UTF8.GetBytes(response));
                await requestStream.WriteAsync(bytes);
            }
            catch (Exception exception)
            {
                Note($"connection fault: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private async Task EchoWebSocketAsync(Stream stream, string head, string path)
    {
        string key = null;
        foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                key = line.Substring(line.IndexOf(':') + 1).Trim();
            }
        }

        if (string.IsNullOrEmpty(key))
        {
            Note($"WS-REFUSED {path}: no Sec-WebSocket-Key");
            var refusal = Encoding.UTF8.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n");
            await stream.WriteAsync(refusal);
            return;
        }

        using var sha1 = SHA1.Create();
        var accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WsGuid)));
        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(handshake));
        Note($"WS-HANDSHAKE {path}");

        while (true)
        {
            var frame = await ReadFrameAsync(stream);
            if (frame == null)
            {
                Note("WS-CLOSE peer");
                return;
            }

            var (opcode, payload) = frame.Value;
            if (opcode == 8)
            {
                Note("WS-CLOSE client");
                return;
            }

            Note($"WS-ECHO {payload.Length} bytes");
            await WriteFrameAsync(stream, opcode, payload);
        }
    }

    private static async Task<(byte opcode, byte[] payload)?> ReadFrameAsync(Stream stream)
    {
        var first = await ReadExactlyAsync(stream, 2);
        if (first == null)
        {
            return null;
        }

        var opcode = (byte)(first[0] & 0x0F);
        var masked = (first[1] & 0x80) != 0;
        long length = (byte)(first[1] & 0x7F);
        if (length == 126)
        {
            var extra = await ReadExactlyAsync(stream, 2);
            if (extra == null)
            {
                return null;
            }

            length = (extra[0] << 8) | extra[1];
        }
        else if (length == 127)
        {
            var extra = await ReadExactlyAsync(stream, 8);
            if (extra == null)
            {
                return null;
            }

            length = 0;
            foreach (var b in extra)
            {
                length = (length << 8) | b;
            }
        }

        byte[] mask = null;
        if (masked)
        {
            mask = await ReadExactlyAsync(stream, 4);
            if (mask == null)
            {
                return null;
            }
        }

        var payload = await ReadExactlyAsync(stream, (int)length);
        if (payload == null)
        {
            return null;
        }

        if (mask != null)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i % 4];
            }
        }

        return (opcode, payload);
    }

    private static async Task WriteFrameAsync(Stream stream, byte opcode, byte[] payload)
    {
        var head = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length < 126)
        {
            head.Add((byte)payload.Length);
        }
        else if (payload.Length <= 0xFFFF)
        {
            head.Add(126);
            head.Add((byte)(payload.Length >> 8));
            head.Add((byte)payload.Length);
        }
        else
        {
            head.Add(127);
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                head.Add((byte)(((long)payload.Length >> shift) & 0xFF));
            }
        }

        await stream.WriteAsync(head.ToArray());
        await stream.WriteAsync(payload);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer, offset, count - offset);
            if (read <= 0)
            {
                return null;
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<string> ReadHeadAsync(Stream stream)
    {
        var buffer = new byte[8192];
        var received = 0;
        while (received < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, received, buffer.Length - received);
            if (read <= 0)
            {
                return null;
            }

            received += read;
            var text = Encoding.UTF8.GetString(buffer, 0, received);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return text.Substring(0, end);
            }
        }

        return null;
    }

    private string BuildProbePage() => _probePageHtml ?? DefaultWsProbePage;

    private static string DefaultWsProbePage =>
        "<!DOCTYPE html><html><head><title>ws-echo-probe</title></head><body>" +
        "<h1>websocket probe</h1>" +
        "<div id=\"ws-status\">WS-PENDING</div>" +
        "<script>" +
        "var token = 'WS-TOKEN-' + Math.random().toString(36).slice(2);" +
        "try {" +
        "  var socket = new WebSocket('ws://' + location.host + '/echo');" +
        "  socket.onopen = function () { socket.send(token); };" +
        "  socket.onmessage = function (event) {" +
        "    document.getElementById('ws-status').textContent = event.data === token ? 'WS-OK:' + event.data : 'WS-MISMATCH:' + event.data;" +
        "    socket.close();" +
        "  };" +
        "  socket.onerror = function () { document.getElementById('ws-status').textContent = 'WS-FAIL:error'; };" +
        "  socket.onclose = function (event) {" +
        "    var el = document.getElementById('ws-status');" +
        "    if (el.textContent === 'WS-PENDING') { el.textContent = 'WS-FAIL:closed-' + event.code; }" +
        "  };" +
        "} catch (e) { document.getElementById('ws-status').textContent = 'WS-FAIL:' + e.message; }" +
        "</script></body></html>";

    /// <summary>
    /// RSA self-signed certificate valid for the https origin the probe names (SAN: tlsHost,
    /// loopback, machine name). KeyUsage/EKU mirror a real camera portal's server certificate,
    /// so the #60 fail-closed row fails on chain trust, never on name mismatch. The PFX
    /// roundtrip lands the key in a persisted form Schannel accepts server-side.
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCertificate(string tlsHost)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={WssTargetTrust.ProbeCertificateCommonName}",
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
        if (!string.IsNullOrWhiteSpace(tlsHost))
        {
            if (IPAddress.TryParse(tlsHost, out var address))
            {
                san.AddIpAddress(address);
            }
            else
            {
                san.AddDnsName(tlsHost);
            }
        }
        san.AddDnsName(Environment.MachineName.ToLowerInvariant());
        request.CertificateExtensions.Add(san.Build());

        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        var password = Guid.NewGuid().ToString("N");
        var portable = created.Export(X509ContentType.Pkcs12, password);
        return X509CertificateLoader.LoadPkcs12(portable, password);
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            _listener.Stop();
        }
        catch
        {
        }
    }
}
