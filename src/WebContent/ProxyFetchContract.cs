using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebContent
{
    /// <summary>
    /// Ticket #53: classic MIP message ids for the proxy-fetch request/response pair. The MIP
    /// messaging contract (MIP message communication, routed by the Event Server) requires one id
    /// for the request and another for the response, transmitted as two operations. These ids are
    /// also registered via PluginDefinition.PluginDefinedMessageIds.
    ///
    /// Payload whitelist: the Event Server discards messages whose Data object is outside the
    /// allowed type set; since #54 the wire Data is always a plain JSON STRING (see
    /// ProxyFetchWireCodec) - custom payload classes are never bound by host processes. The
    /// platform rehydrates Data via Type.GetType(DataClass), which silently yields null on hosts
    /// (Smart Client) whose type resolution cannot see WebContent.dll.
    /// </summary>
    internal static class ProxyMessageIds
    {
        internal const string FetchRequestId = "WebContent.Proxy.FetchRequest";
        internal const string FetchResponseId = "WebContent.Proxy.FetchResponse";

        /// <summary>ADR-0003: client asks the Event Server to mint a loopback leaf for one
        /// allowlisted origin, carrying the client-generated SubjectPublicKeyInfo.</summary>
        internal const string LeafRequestId = "WebContent.Proxy.LeafRequest";
        internal const string LeafResponseId = "WebContent.Proxy.LeafResponse";

        /// <summary>
        /// Ticket #57: the websocket tunnel's directional message pairs. Open is a request/
        /// response handshake exchange (the Event Server reports whether the target accepted the
        /// upgrade); data and close each get one id PER DIRECTION, so tunnel bytes flow as their
        /// own messages — never through the #53 Response/Chunk fetch framing, and one client's
        /// frames can never be swallowed by another client's filter.
        /// </summary>
        internal const string TunnelOpenRequestId = "WebContent.Proxy.TunnelOpenRequest";
        internal const string TunnelOpenResponseId = "WebContent.Proxy.TunnelOpenResponse";
        internal const string TunnelDataToTargetId = "WebContent.Proxy.TunnelDataToTarget";
        internal const string TunnelDataFromTargetId = "WebContent.Proxy.TunnelDataFromTarget";
        internal const string TunnelCloseToTargetId = "WebContent.Proxy.TunnelCloseToTarget";
        internal const string TunnelCloseFromTargetId = "WebContent.Proxy.TunnelCloseFromTarget";
    }

    /// <summary>
    /// Frame kinds within the response stream. The requester treats these as the terminal states
    /// of a proxy-fetch exchange: Response + Chunk* on success, a lone Failure otherwise.
    /// </summary>
    internal static class ProxyFetchFrameKinds
    {
        /// <summary>First frame: status line, response headers, body length, and chunk count.</summary>
        internal const string Response = "Response";

        /// <summary>One base64 body slice, sent in sequence after the metadata frame.</summary>
        internal const string Chunk = "Chunk";

        /// <summary>Terminal failure frame: no chunks follow.</summary>
        internal const string Failure = "Failure";
    }

    /// <summary>
    /// Machine-readable failure codes carried in Failure frames and in unsuccessful results.
    /// </summary>
    internal static class ProxyFetchErrorCodes
    {
        /// <summary>The request payload was missing, the url blank, or not an http(s) uri.</summary>
        internal const string BadRequest = "BadRequest";

        /// <summary>DNS, connect, or TLS failure: the target could not be reached.</summary>
        internal const string Unreachable = "Unreachable";

        /// <summary>Connect or read timeout per the fetcher's enforced limits.</summary>
        internal const string Timeout = "Timeout";

        /// <summary>The response body exceeded the configured maximum response size.</summary>
        internal const string TooLarge = "TooLarge";

        /// <summary>Any other transport-level failure.</summary>
        internal const string Error = "Error";

        /// <summary>
        /// Ticket #54 server-side hardening: the fetched origin is not in the proxy allowlist of
        /// any proxied workspace, so the Event Server refuses to reach it.
        /// </summary>
        internal const string NotAllowed = "NotAllowed";

        /// <summary>
        /// Ticket #56: the https target's certificate failed chain validation and the target has
        /// no per-target "accept untrusted certificate" override, so the fetch is refused.
        /// </summary>
        internal const string TlsValidation = "TlsValidation";

        /// <summary>
        /// Ticket #56 (ADR-0003): the Event Server could not load or create the deployment CA,
        /// so no loopback leaf certificate can be minted.
        /// </summary>
        internal const string NoAuthority = "NoAuthority";

        /// <summary>
        /// Ticket #57: the tunnel's target answered the websocket handshake with something other
        /// than 101 (a plain 200, a vendor 401) — the upgrade is refused, no tunnel exists.
        /// </summary>
        internal const string HandshakeRefused = "HandshakeRefused";
    }

    /// <summary>
    /// How the Event Server fetcher treats TLS certificate validation on the https leg to the
    /// target (#56). The caller decides per request from the target's allowlist entry —
    /// AcceptUntrusted for exactly the origin whose entry carries the admin override,
    /// ValidateNormally for everything else. Never a process-wide or browser-wide bypass.
    /// </summary>
    internal enum ProxyFetchTlsMode
    {
        /// <summary>Full chain validation; an untrusted certificate fails with TlsValidation.</summary>
        ValidateNormally,

        /// <summary>Validation errors accepted for this one already-allowlisted target.</summary>
        AcceptUntrusted,
    }

    /// <summary>
    /// One response header from the fetched target, as the fetcher's HTTP layer captured it.
    /// This type NEVER travels on the wire as an object: it is a member of
    /// <see cref="ProxyFetchResponsePayload"/>, and the whole payload crosses messaging only as a
    /// JSON string via <see cref="ProxyFetchWireCodec"/> (#54 - hosts like Smart Client cannot
    /// bind plugin-namespace types through the platform's Type.GetType rehydration).
    /// </summary>
    [Serializable]
    public class ProxyFetchHeader
    {
        public string Name;
        public string Value;
    }

    /// <summary>
    /// Request payload: one correlation id per request so the requester can match the response
    /// frames that come back over the shared message channel. Since #55 it also carries the HTTP
    /// method, the browser's request headers, and a base64 request body — the proxy is a pure
    /// passthrough for authentication (Cookie/Authorization cross untouched; the plugin never
    /// injects or stores target credentials). A null Method means GET; null Headers/BodyBase64
    /// mean none. Still ONE JSON string on the wire via <see cref="ProxyFetchWireCodec"/>.
    /// </summary>
    [Serializable]
    public class ProxyFetchRequestPayload
    {
        public string CorrelationId;
        public string Url;

        /// <summary>Uppercase HTTP method (POST/PUT/DELETE/OPTIONS/...); null or blank is GET.</summary>
        public string Method;

        /// <summary>Request headers to replay at the target, hop-by-hop headers already stripped
        /// by the requester side; null when the request carried none worth relaying.</summary>
        public List<ProxyFetchHeader> Headers;

        /// <summary>Request body as base64 (login forms, XHR payloads); null when there is no body.</summary>
        public string BodyBase64;
    }

    /// <summary>
    /// Response payload for every frame kind (Response / Chunk / Failure). Fields irrelevant to a
    /// kind stay null/default. Serialized to a JSON string by <see cref="ProxyFetchWireCodec"/>
    /// before transmit; the platform must never be asked to bind this type itself.
    /// </summary>
    [Serializable]
    public class ProxyFetchResponsePayload
    {
        public string CorrelationId;
        public string Kind;

        /// <summary>0-based chunk order; -1 on metadata and failure frames.</summary>
        public int Sequence;

        /// <summary>True on the final chunk frame so a consumer can notice the stream ended.</summary>
        public bool IsLastChunk;

        // Metadata-frame fields.
        public int StatusCode;

        /// <summary>Human-readable status text (the status code name; the wire reason phrase is
        /// not exposed by HttpWebResponse). Display metadata only.</summary>
        public string StatusText;
        public List<ProxyFetchHeader> Headers;

        /// <summary>Actual fetched body size in bytes (metadata frame).</summary>
        public long BodyLength;

        /// <summary>How many chunk frames follow the metadata frame (metadata frame).</summary>
        public int ChunkCount;

        // Chunk-frame fields.
        public string ContentBase64;

        // Failure-frame fields.
        public string ErrorCode;
        public string ErrorMessage;
    }

    /// <summary>
    /// One browser request the loopback forwarding proxy decided to relay, as it travels the
    /// client-side seam: listener -> BrowserManager fetch seam -> ProxyFetchClient -> messaging.
    /// Not a wire payload; <see cref="ProxyFetchRequestPayload"/> is its serialized form. Since
    /// #55 the method, browser request headers, and request body ride along, so a vendor login
    /// POST reaches the Event Server fetcher with its credentials and form body intact.
    /// </summary>
    internal sealed class ProxyFetchQuery
    {
        internal Uri Uri;

        /// <summary>Uppercase HTTP method; null/blank means GET.</summary>
        internal string Method;

        /// <summary>Browser request headers to replay at the target, hop-by-hop ones stripped.</summary>
        internal List<ProxyFetchHeader> Headers = new List<ProxyFetchHeader>();

        /// <summary>Request body bytes; null when the request carried no body.</summary>
        internal byte[] Body;
    }

    /// <summary>
    /// The fetcher's own outcome model, shared by the server-side fetcher, the framing seam, and
    /// the requester-side reassembly. Not a message payload: the framer serializes it into frames,
    /// the correlator deserializes frames back.
    /// </summary>
    internal class ProxyFetchResult
    {
        internal bool Success;

        /// <summary>Target HTTP status code; 0 when the transport never produced a response.</summary>
        internal int StatusCode;

        /// <summary>Human-readable status text (status code name), carried through the meta frame.</summary>
        internal string StatusText;
        internal List<ProxyFetchHeader> Headers = new List<ProxyFetchHeader>();
        internal long BodyLength;

        /// <summary>The fetched bytes; null while not yet assembled, empty-array when body was empty.</summary>
        internal byte[] Body;

        // Failure fields.
        internal string ErrorCode;
        internal string ErrorMessage;
    }

    /// <summary>
    /// Ticket #56 (ADR-0003): one loopback-leaf mint request. The client generates the leaf key
    /// pair and sends only the public half (DER SubjectPublicKeyInfo, base64) — no private key
    /// ever crosses the wire. Host/Port identify the CONNECT origin; the Event Server mints only
    /// for origins some proxied workspace allowlists (the allowlist IS the minting policy).
    /// One JSON string on the wire like every other proxy payload (#54).
    /// </summary>
    [Serializable]
    public class ProxyLeafRequestPayload
    {
        public string CorrelationId;

        /// <summary>CONNECT target host as the browser resolved it (IdnHost form).</summary>
        public string Host;

        /// <summary>CONNECT target port; the allowlist gate matches scheme+host+port exactly.</summary>
        public int Port;

        public string SpkiBase64;
    }

    /// <summary>
    /// Ticket #56 (ADR-0003): the leaf exchange's single response frame. Success carries the minted
    /// leaf DER (no private key — the client attaches its own) plus the deployment CA certificate
    /// and its thumbprint, so the client can verify its machine trust roots hold the CA and show
    /// actionable guidance when it does not.
    /// </summary>
    [Serializable]
    public class ProxyLeafResponsePayload
    {
        public string CorrelationId;
        public bool Success;

        public string LeafDerBase64;
        public string AuthorityDerBase64;
        public string AuthorityThumbprint;

        // Failure fields.
        public string ErrorCode;
        public string ErrorMessage;
    }

    /// <summary>
    /// Bounds the fetcher enforces per request (ticket #53 acceptance: connect timeout, read
    /// timeout, maximum response size).
    /// </summary>
    internal class ProxyFetchLimits
    {
        internal const int DefaultConnectTimeoutMs = 5000;
        internal const int DefaultReadTimeoutMs = 15000;
        internal const int DefaultMaxResponseBytes = 5 * 1024 * 1024;
        internal const int DefaultChunkSizeBytes = 64 * 1024;

        internal int ConnectTimeoutMs = DefaultConnectTimeoutMs;
        internal int ReadTimeoutMs = DefaultReadTimeoutMs;
        internal int MaxResponseBytes = DefaultMaxResponseBytes;
        internal int ChunkSizeBytes = DefaultChunkSizeBytes;

        internal static ProxyFetchLimits Default()
        {
            return new ProxyFetchLimits();
        }
    }

    /// <summary>
    /// Frame kinds inside the tunnel messages the Event Server pushes toward the client: raw
    /// tunnel bytes versus the end-of-tunnel notice. The client's receiving side picks the
    /// directional message id from this kind.
    /// </summary>
    internal static class ProxyTunnelFrameKinds
    {
        internal const string Data = "Data";
        internal const string Close = "Close";
    }

    /// <summary>
    /// Ticket #57: the single payload shape for every websocket-tunnel message (open request,
    /// open response, data frame in either direction, close in either direction). Which fields
    /// matter is decided by the message id the payload rides on, so one JSON string covers the
    /// whole protocol like every other proxy payload (#54). The namespace/whitelist guards in
    /// ProxyFetchContractGuardTests pin the field types.
    /// </summary>
    [Serializable]
    public class ProxyTunnelPayload
    {
        /// <summary>The tunnel id; one Guid for the whole life of one tunnel.</summary>
        public string CorrelationId;

        /// <summary>Server-pushed frames: Data or Close (see <see cref="ProxyTunnelFrameKinds"/>).</summary>
        public string Kind;

        /// <summary>Open request: the ws:// target url (http-scheme origin + path).</summary>
        public string Url;

        /// <summary>
        /// Open request: the browser's handshake headers minus per-hop/proxy decoration, casing
        /// and values intact — the target computes Sec-WebSocket-Accept from the relayed
        /// Sec-WebSocket-Key, so the key must not be touched.
        /// </summary>
        public List<ProxyFetchHeader> Headers;

        /// <summary>
        /// Data frame: raw tunnel bytes base64-encoded at the JSON edge only. Open response:
        /// the target's raw handshake answer (101 head plus any already-buffered frame bytes),
        /// relayed verbatim so the browser sees exactly the target's Sec-WebSocket-Accept.
        /// </summary>
        public string DataBase64;

        /// <summary>Open response: whether the tunnel is up. Close frames leave it default.</summary>
        public bool Success;

        /// <summary>Open response: the target's handshake status line code (101, or whatever else).</summary>
        public int StatusCode;

        // Failure/close fields.
        public string ErrorCode;
        public string ErrorMessage;
    }

    /// <summary>
    /// Ticket #57: bounds the websocket tunnel enforces on both sides — the Event Server's
    /// connect/handshake budget for the target leg, the idle timeout that reclaims stalled or
    /// half-open tunnels, the per-message raw byte cap, and the concurrency caps that keep the
    /// Event Server from accumulating sockets.
    /// </summary>
    internal sealed class ProxyTunnelLimits
    {
        internal const int DefaultConnectTimeoutMs = 5000;
        internal const int DefaultHandshakeTimeoutMs = 10000;
        internal const int DefaultIdleTimeoutMs = 5 * 60 * 1000;
        internal const int DefaultHandshakeMaxBytes = 64 * 1024;
        internal const int DefaultFrameMaxBytes = 16 * 1024;
        internal const int DefaultEsMaxTunnels = 8;
        internal const int DefaultClientMaxTunnels = 16;
        internal const int DefaultClientOpenDeadlineMs = 30000;

        internal int ConnectTimeoutMs = DefaultConnectTimeoutMs;
        internal int HandshakeTimeoutMs = DefaultHandshakeTimeoutMs;
        internal int IdleTimeoutMs = DefaultIdleTimeoutMs;
        internal int HandshakeMaxBytes = DefaultHandshakeMaxBytes;
        internal int FrameMaxBytes = DefaultFrameMaxBytes;
        internal int EsMaxTunnels = DefaultEsMaxTunnels;
        internal int ClientMaxTunnels = DefaultClientMaxTunnels;
        internal int ClientOpenDeadlineMs = DefaultClientOpenDeadlineMs;

        internal static ProxyTunnelLimits Default()
        {
            return new ProxyTunnelLimits();
        }
    }

    /// <summary>
    /// Ticket #57: the client-side seam a tunnelled browser connection pumps against. The
    /// production implementation rides MIP messaging (ProxyFetchClient); tests substitute an
    /// in-memory tunnel. Byte-oriented, not frame-oriented: everything after the 101 is opaque
    /// RFC 6455 framing to the proxy, exactly like a CONNECT tunnel carries opaque TLS.
    /// </summary>
    internal abstract class ProxyTunnelHandle
    {
        internal string TunnelId;

        /// <summary>True once either side closed the tunnel (no further data will arrive).</summary>
        internal abstract bool IsClosed { get; }

        /// <summary>Relay browser bytes toward the target; no-op once closed.</summary>
        internal abstract Task SendAsync(byte[] bytes);

        /// <summary>
        /// Await the next target→browser bytes; null once the tunnel is closed (either side).
        /// </summary>
        internal abstract Task<byte[]> NextDataAsync();

        /// <summary>Close the tunnel and tell the Event Server; idempotent.</summary>
        internal abstract void Close(string reason);
    }

    /// <summary>
    /// Ticket #57: outcome of a tunnel-open attempt on the client side. Success carries the
    /// handle to pump plus the target's raw handshake answer to write to the browser socket
    /// before the first pump byte; failure carries the refusal to render as 502.
    /// </summary>
    internal sealed class ProxyTunnelOpenOutcome
    {
        internal bool Success;
        internal ProxyTunnelHandle Handle;

        /// <summary>The target's 101 answer (head + buffered bytes), relayed verbatim.</summary>
        internal byte[] HandshakeBytes;

        internal int StatusCode;
        internal string ErrorCode;
        internal string ErrorMessage;
    }
}
