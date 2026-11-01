using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
/// </summary>
public sealed class WebSocketEchoServer : IDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly List<string> _journal = new();
    private volatile bool _stopping;

    public WebSocketEchoServer(int port)
    {
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
                var head = await ReadHeadAsync(stream);
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
                    await EchoWebSocketAsync(stream, head, path);
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
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                await stream.WriteAsync(bytes);
            }
            catch (Exception exception)
            {
                Note($"connection fault: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private async Task EchoWebSocketAsync(NetworkStream stream, string head, string path)
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

    private static async Task<(byte opcode, byte[] payload)?> ReadFrameAsync(NetworkStream stream)
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

    private static async Task WriteFrameAsync(NetworkStream stream, byte opcode, byte[] payload)
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

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
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

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
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

    private static string BuildProbePage() =>
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
