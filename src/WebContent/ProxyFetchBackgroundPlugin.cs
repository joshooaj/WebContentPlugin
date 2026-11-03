using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using VideoOS.Platform;
using VideoOS.Platform.Background;
using VideoOS.Platform.Messaging;

namespace WebContent
{
    /// <summary>
    /// Ticket #53: the server-side half of the proxy-fetch contract. The same WebContent.dll
    /// loads into the XProtect Event Server (plugin.def load env "Service"), where this
    /// BackgroundPlugin answers WebContent.Proxy.FetchRequest messages: it performs the HTTP(S)
    /// GET from the server network under the ProxyHttpFetcher budgets (connect timeout, read
    /// timeout, maximum response size) and streams the outcome back to the requester as framed,
    /// plugin-namespaced response messages (metadata frame with status line and content headers,
    /// then base64 body chunks).
    ///
    /// Failure classes (unreachable target, size cap exceeded, timeout) are logged to the Event
    /// Server MIP log with actionable context; successful fetches are silent because proxied page
    /// views make that stream high-frequency.
    /// </summary>
    internal sealed class ProxyFetchBackgroundPlugin : BackgroundPlugin
    {
        private static readonly Guid PluginId = new Guid("ced898c8-25f6-406e-b4fa-aecae669fbb0");

        private const int MaxConcurrentFetches = 3;
        private static readonly TimeSpan FetchSlotWait = TimeSpan.FromSeconds(10);

        private ServerId _serverId;
        private MessageCommunication _messageCommunication;
        private object _requestFilterRegistration;
        private object _leafFilterRegistration;
        private object _tunnelOpenFilterRegistration;
        private object _tunnelDataFrameFilterRegistration;
        private object _tunnelCloseFrameFilterRegistration;
        private object _blockReportFilterRegistration;
        private object _sessionEventFilterRegistration;
        private object _statusRequestFilterRegistration;
        private object _caExportRequestFilterRegistration;
        private object _onlineTableFilterRegistration;
        private object _newEndPointFilterRegistration;
        private object _newEventIndicationRegistration;
        private SemaphoreSlim _fetchSlots;
        private ProxyServerAllowlist _allowlist;
        private ProxyLeafIssuer _leafIssuer;
        private ProxyCaExportResponder _caExportResponder;
        private ProxyTunnelServer _tunnels;

        /// <summary>
        /// #65: per-workspace rule-trigger rows (URL/method/status matchers) matched against
        /// completed proxy fetches; a match raises the fixed "WebContent proxy request" platform
        /// event so administrators can build Event Server rules on it.
        /// </summary>
        private ProxyTriggerMatcher _triggerMatcher;
        private int _selfTestProbeDone;

        /// <summary>
        /// Ticket #69: the diagnostics substrate - per-workspace blocked-origin rings and live
        /// session tables, in memory only (an Event Server restart resets it by design). Fed by
        /// client block reports and session events plus the fetch/tunnel paths' own accounting;
        /// read by the unicast workspace status snapshots. Every path swallows faults:
        /// diagnostics must never take proxy traffic down with it.
        /// </summary>
        private ProxyDiagnosticsAggregator _diagnostics;
        private Timer _sessionSweeper;
        private static readonly TimeSpan SessionIdleBackstop = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan SessionSweepPeriod = TimeSpan.FromSeconds(30);

        /// <summary>
        /// #69: messaging endpoint FQID id → XProtect login name, mirrored from the platform's own
        /// endpoint identity table (WhoAreOnlineResponse + NewEndPointIndication). Session users
        /// come from here - the messaging sender identity the platform stamped - never from a
        /// client-sent claim.
        /// </summary>
        private readonly object _identitySync = new object();
        private readonly Dictionary<string, string> _endpointUsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// ADR-0003: the deployment CA is loaded/created lazily on the first leaf request and
        /// then reused - its private key lives in this machine's LocalMachine\Personal store and
        /// outlives the service process.
        /// </summary>
        private readonly object _authorityGate = new object();
        private Authority _deploymentAuthority;

        public override Guid Id => PluginId;

        public override string Name => "Web Content proxy fetcher";

        public override List<EnvironmentType> TargetEnvironments { get; } = new List<EnvironmentType> { EnvironmentType.Service };

        public override void Init()
        {
            _fetchSlots = new SemaphoreSlim(MaxConcurrentFetches, MaxConcurrentFetches);

            try
            {
                // Same opt-in messaging trace as the client side: with WEBCONTENT_MIP_TRACE=1 on
                // the Event Server machine, the platform logs every message this process's
                // messaging client transmits and receives.
                if (Environment.GetEnvironmentVariable("WEBCONTENT_MIP_TRACE") == "1")
                {
                    EnvironmentManager.Instance.TraceMessageCommunication = true;
                }

                _serverId = EnvironmentManager.Instance.MasterSite.ServerId;

                // Per the MIP messaging docs the Event Server plug-in may initialize the message
                // communication client directly in Init().
                MessageCommunicationManager.Start(_serverId);
                _messageCommunication = MessageCommunicationManager.Get(_serverId);
                _requestFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyFetchRequest,
                    new CommunicationIdFilter(ProxyMessageIds.FetchRequestId));

                // Ticket #54 server-side hardening: the fetcher only reaches origins some proxied
                // workspace actually allowlists, so the server network cannot be driven at
                // arbitrary targets by a crafted request. #73: the same plugin supplies the VMS's
                // own host names so the shared matcher floors every wildcard entry here too.
                _allowlist = new ProxyServerAllowlist(
                    ResolveWorkspacePropertySnapshots,
                    message => EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), message),
                    ResolveVmsFloorHosts);

                // ADR-0003: the same plugin answers leaf-mint requests for the loopback TLS
                // termination; the same allowlist union gates minting exactly like fetching.
                _leafIssuer = new ProxyLeafIssuer(
                    _allowlist,
                    ResolveDeploymentAuthority,
                    message => EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), message));
                _leafFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyLeafRequest,
                    new CommunicationIdFilter(ProxyMessageIds.LeafRequestId));

                // Ticket #57/#60/#61: the Event Server half of the websocket CONNECT tunnel. The client
                // has already allowlist-gated the upgrade; this side re-gates against its own
                // allowlist union, replays the handshake to the target (raw socket for http-scheme
                // targets, TLS leg for https-scheme wss targets), admits only a 101, and
                // pumps raw bytes both directions. The open runs on the thread pool (connect +
                // handshake is seconds long), so the receive thread stays free. #61: the TLS leg
                // honours the same per-target accept-untrusted override as the fetch path — the
                // resolver hands the tunnel its own SslStream's callback, never a global bypass.
                _tunnels = new ProxyTunnelServer(
                    ProxyTunnelLimits.Default(),
                    message => EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), message),
                    ResolveTunnelTlsValidation,
                    (sessionId, clientBytes, targetBytes) => RecordTunnelTrafficSafe(sessionId, clientBytes, targetBytes));
                _tunnelOpenFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyTunnelOpen,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelOpenRequestId));
                _tunnelDataFrameFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyTunnelDataFrame,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelDataToTargetId));
                _tunnelCloseFrameFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyTunnelCloseFrame,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelCloseToTargetId));

                // Ticket #69: the diagnostics substrate. One-way block reports and session events
                // arrive fire-and-forget; the status query is answered unicast like every other
                // proxy response. The sweep timer backstops Smart Clients that die without a
                // SessionClose.
                _diagnostics = new ProxyDiagnosticsAggregator();
                _blockReportFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyBlockReport,
                    new CommunicationIdFilter(ProxyMessageIds.BlockReportId));
                _sessionEventFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxySessionEvent,
                    new CommunicationIdFilter(ProxyMessageIds.SessionEventId));
                _statusRequestFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyStatusRequest,
                    new CommunicationIdFilter(ProxyMessageIds.StatusRequestId));

                // Ticket #75: the Management Client's pull of the deployment CA's PUBLIC
                // certificate for client-machine distribution. Read-only against
                // LocalMachine\Personal and provenance-independent (MSI action, legacy script,
                // or lazy first-use mint all land the CA in the same store); the private key
                // never participates, so answering needs no store write access.
                _caExportResponder = new ProxyCaExportResponder(
                    ProxyCaExportResponder.FindDeploymentAuthorityCertificates,
                    message => EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), message));
                _caExportRequestFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyCaExportRequest,
                    new CommunicationIdFilter(ProxyMessageIds.CaExportRequestId));

                // #69 user attribution: mirror the platform's endpoint identity table so a session's
                // XProtect login name comes from the messaging sender identity the platform itself
                // stamped. The initial WhoAreOnlineRequest fills the table for already-connected
                // clients; NewEndPointIndication covers logins from here on.
                _onlineTableFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnWhoAreOnlineResponse,
                    new CommunicationIdFilter(MessageCommunication.WhoAreOnlineResponse));
                _newEndPointFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnNewEndPointIndication,
                    new CommunicationIdFilter(MessageCommunication.NewEndPointIndication));
                try
                {
                    _messageCommunication.TransmitMessage(new Message(MessageCommunication.WhoAreOnlineRequest), null, null, null);
                }
                catch (Exception identityException)
                {
                    EnvironmentManager.Instance.Log(
                        true,
                        nameof(ProxyFetchBackgroundPlugin),
                        $"Proxy diagnostics online-user query not sent (session user names will fill in as endpoints change): {identityException.Message}");
                }

                _sessionSweeper = new Timer(_ => SweepSessionsSafely(), null, SessionSweepPeriod, SessionSweepPeriod);

                // #65: rule triggers. Per-workspace URL/method/status rows (the "proxyTriggers"
                // section of the workspace's WebContent item settings) are matched against
                // completed proxy fetches; each match raises the fixed "WebContent proxy request"
                // platform event as a system-defined Server.NewEventCommand message (the raise
                // route available to a component-hosted plugin - see the EventTracer sample's
                // matching NewEventIndication receiver). The receiver logs every WebContent event
                // as it comes back on the bus, which is the end-to-end proof the raise path is
                // live in this process.
                _triggerMatcher = new ProxyTriggerMatcher(
                    ResolveWorkspaceTriggerSnapshots,
                    message => EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), message));
                _triggerMatcher.Prime();
                _newEventIndicationRegistration = EnvironmentManager.Instance.RegisterReceiver(
                    OnPlatformNewEvent,
                    new MessageIdFilter(MessageId.Server.NewEventIndication));
                RaiseTriggerSelfTestProbeIfRequested();

                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy fetcher ready (concurrency={MaxConcurrentFetches}, request id={ProxyMessageIds.FetchRequestId}, diagnostics={ProxyMessageIds.BlockReportId}/{ProxyMessageIds.SessionEventId}/{ProxyMessageIds.StatusRequestId}, export={ProxyMessageIds.CaExportRequestId}, triggers={ProxyTriggerPolicy.EventType}).");
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy fetcher failed to start: {exception.Message}");
            }
        }

        public override void Close()
        {
            if (_messageCommunication != null && _requestFilterRegistration != null)
            {
                try
                {
                    _messageCommunication.UnRegisterCommunicationFilter(_requestFilterRegistration);
                }
                catch (Exception exception)
                {
                    EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy fetch filter detach failed: {exception.Message}");
                }
            }

            if (_messageCommunication != null && _leafFilterRegistration != null)
            {
                try
                {
                    _messageCommunication.UnRegisterCommunicationFilter(_leafFilterRegistration);
                }
                catch (Exception exception)
                {
                    EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy leaf filter detach failed: {exception.Message}");
                }
            }

            _requestFilterRegistration = null;
            _leafFilterRegistration = null;
            _leafIssuer = null;

            // Tear tunnels down while messaging is still alive: each close rides a message
            // notice back to its requester, and the sockets must not outlive the plugin.
            var tunnels = _tunnels;
            _tunnels = null;
            tunnels?.Dispose();

            if (_messageCommunication != null)
            {
                foreach (var registration in new[] { _tunnelOpenFilterRegistration, _tunnelDataFrameFilterRegistration, _tunnelCloseFrameFilterRegistration, _blockReportFilterRegistration, _sessionEventFilterRegistration, _statusRequestFilterRegistration, _caExportRequestFilterRegistration, _onlineTableFilterRegistration, _newEndPointFilterRegistration, _newEventIndicationRegistration })
                {
                    if (registration == null)
                    {
                        continue;
                    }

                    try
                    {
                        _messageCommunication.UnRegisterCommunicationFilter(registration);
                    }
                    catch (Exception exception)
                    {
                        EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy filter detach failed: {exception.Message}");
                    }
                }
            }

            _tunnelOpenFilterRegistration = null;
            _tunnelDataFrameFilterRegistration = null;
            _tunnelCloseFrameFilterRegistration = null;
            _blockReportFilterRegistration = null;
            _sessionEventFilterRegistration = null;
            _statusRequestFilterRegistration = null;
            _caExportRequestFilterRegistration = null;
            _caExportResponder = null;
            _onlineTableFilterRegistration = null;
            _newEndPointFilterRegistration = null;
            _newEventIndicationRegistration = null;
            _triggerMatcher = null;

            // #69: stop sweeping and drop the in-memory diagnostics tables (by design they never
            // outlive the service process).
            var sweeper = _sessionSweeper;
            _sessionSweeper = null;
            sweeper?.Dispose();
            _diagnostics = null;
            lock (_identitySync)
            {
                _endpointUsers.Clear();
            }

            _messageCommunication = null;

            // Deliberately no MessageCommunicationManager.Stop: the messaging session is
            // process-wide and may be shared by other plugins (no MIP sample ever stops it).
            _serverId = null;

            var slots = _fetchSlots;
            _fetchSlots = null;
            slots?.Dispose();

            EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), "Proxy fetcher stopped.");
        }

        /// <summary>
        /// Message-receiver thread: keep it non-blocking. The fetch runs on the thread pool and
        /// the response stream is pushed back to the requester's endpoint FQID.
        ///
        /// The requester endpoint comes from Message.ExternalMessageSourceEndPoint, which the MIP
        /// messaging layer fills on every received message with the sender's endpoint FQID (the
        /// callback's source parameter only carries an explicitly passed sender identity, which
        /// ProxyFetchClient does not pass). Replying to that endpoint unicasts the response frames
        /// to the requesting client; replying with a null destination would filter-route them to
        /// every client subscribed to the response message id - leaking page bodies into unrelated
        /// hosts and letting one client's correlation ids swallow another's frames.
        /// </summary>
        private object OnProxyFetchRequest(Message message, FQID destination, FQID source)
        {
            var requester = ResolveRequester(source, message);

            var payload = ProxyFetchWireCodec.RequestFromWire(message?.Data);
            if (payload == null || string.IsNullOrEmpty(payload.CorrelationId) || string.IsNullOrEmpty(payload.Url))
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), "Proxy fetch request carried no usable payload; refused.");
                return null;
            }

            Uri target;
            if (!Uri.TryCreate(payload.Url, UriKind.Absolute, out target) || !ProxyPolicy.IsHttpUri(target))
            {
                LogRefused(payload, "the url was not an absolute http(s) uri");
                TransmitFailure(payload.CorrelationId, requester, ProxyFetchErrorCodes.BadRequest, "The proxy-fetch url must be an absolute http(s) uri.");
                return null;
            }

            // #55: method and body decode against the same verb table the client forwarder uses,
            // so a crafted or version-skewed payload can never drive the VMS server with an
            // arbitrary verb. A null method is a pre-#55 GET sender.
            var method = string.IsNullOrWhiteSpace(payload.Method) ? "GET" : payload.Method.Trim().ToUpperInvariant();
            if (!ProxyForwardPolicy.IsForwardedMethod(method))
            {
                LogRefused(payload, $"'{method}' is outside the relayed method set");
                TransmitFailure(payload.CorrelationId, requester, ProxyFetchErrorCodes.BadRequest, $"The proxy does not relay '{method}' requests.");
                return null;
            }

            byte[] body = null;
            if (!string.IsNullOrEmpty(payload.BodyBase64))
            {
                try
                {
                    body = Convert.FromBase64String(payload.BodyBase64);
                }
                catch (FormatException)
                {
                    LogRefused(payload, "the request body was not valid base64");
                    TransmitFailure(payload.CorrelationId, requester, ProxyFetchErrorCodes.BadRequest, "The proxy-fetch request body was not valid base64.");
                    return null;
                }

                // The client forwarder caps bodies before relaying; the fetcher re-caps what the
                // wire actually carried, so no sender — old build or crafted payload — can make
                // the VMS server replay an unbounded body at a target.
                if (body.Length > ProxyForwardConnection.MaxRequestBodyBytes)
                {
                    LogRefused(payload, $"the request body ({body.Length} bytes) is past the {ProxyForwardConnection.MaxRequestBodyBytes} byte request cap");
                    TransmitFailure(payload.CorrelationId, requester, ProxyFetchErrorCodes.BadRequest, "The proxy-fetch request body exceeds the proxy's request cap.");
                    return null;
                }
            }

            var query = new ProxyFetchQuery
            {
                Uri = target,
                Method = method,
                Headers = payload.Headers,
                Body = body,
            };

            string allowReason = null;
            if (_allowlist == null || !_allowlist.IsAllowed(target, out allowReason))
            {
                LogRefused(payload, allowReason);

                // #69: the server-side gate refusal is a per-origin block - recorded where it is
                // decided (no client report needed for a decision the Event Server made itself).
                var gateDiagnostics = _diagnostics;
                if (gateDiagnostics != null && !string.IsNullOrEmpty(payload.SessionId))
                {
                    try
                    {
                        var gateNow = DateTime.UtcNow;
                        gateDiagnostics.RecordFetchTraffic(payload.SessionId, body == null ? 0 : body.Length, 0, gateNow);
                        gateDiagnostics.RecordBlockForSession(payload.SessionId, ProxyPolicy.CanonicalOrigin(target), ProxyBlockReasons.NotAllowed, gateNow);
                    }
                    catch (Exception diagnosticsException)
                    {
                        EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy gate refusal not recorded: {diagnosticsException.Message}");
                    }
                }

                TransmitFailure(payload.CorrelationId, requester, ProxyFetchErrorCodes.NotAllowed, allowReason);
                return null;
            }

            var communication = _messageCommunication;
            if (communication == null)
            {
                LogRefused(payload, "the fetcher is not connected to the Event Server");
                TransmitFailure(payload.CorrelationId, requester, ProxyFetchErrorCodes.Error, "The proxy fetcher is not ready.");
                return null;
            }

            // #56: the certificate mode comes from this target's own allowlist entry — the union
            // snapshot resolved it to exactly this canonical origin. Every other target, proxied
            // or not, keeps full TLS chain validation.
            var tlsMode = target.Scheme == Uri.UriSchemeHttps && _allowlist.AcceptsUntrustedCertificate(target)
                ? ProxyFetchTlsMode.AcceptUntrusted
                : ProxyFetchTlsMode.ValidateNormally;

            var slots = _fetchSlots;
            if (slots == null)
            {
                return null;
            }

            _ = Task.Run(async () =>
            {
                if (!await slots.WaitAsync(FetchSlotWait))
                {
                    LogRefused(payload, "the fetcher queue was saturated");
                    TransmitFailure(payload.CorrelationId, requester, ProxyFetchErrorCodes.Error, "The proxy fetcher is saturated; try again later.");
                    return;
                }

                try
                {
                    await ServeFetchAsync(communication, payload, query, requester, tlsMode);
                }
                finally
                {
                    slots.Release();
                }
            });

            return null;
        }

        private async Task ServeFetchAsync(MessageCommunication communication, ProxyFetchRequestPayload payload, ProxyFetchQuery query, FQID requesterFqid, ProxyFetchTlsMode tlsMode)
        {
            var target = query.Uri;
            try
            {
                var limits = ProxyFetchLimits.Default();
                var result = await new ProxyHttpFetcher().FetchAsync(query, limits, tlsMode);

                if (!result.Success)
                {
                    // Ticket acceptance: failure cases produce clear log entries in the Event
                    // Server log. Successes stay quiet: proxied page views make fetches frequent.
                    EnvironmentManager.Instance.Log(
                        true,
                        nameof(ProxyFetchBackgroundPlugin),
                        $"Proxy fetch failed (code={result.ErrorCode}, target={ProxyPolicy.CanonicalOrigin(target)}): {result.ErrorMessage}");
                }

                // #69: every served request counts toward its session's live status; per-origin
                // failure codes also land in the workspace's blocked ring (component faults like
                // Error map to nothing and are not per-origin blocks).
                var diagnostics = _diagnostics;
                if (diagnostics != null && !string.IsNullOrEmpty(payload.SessionId))
                {
                    try
                    {
                        var now = DateTime.UtcNow;
                        diagnostics.RecordFetchTraffic(payload.SessionId, query.Body == null ? 0 : query.Body.Length, result.Success ? result.BodyLength : 0, now);
                        string reason;
                        if (!result.Success && ProxyBlockReasons.TryMapErrorCode(result.ErrorCode, out reason))
                        {
                            diagnostics.RecordBlockForSession(payload.SessionId, ProxyPolicy.CanonicalOrigin(target), reason, now);
                        }
                    }
                    catch (Exception diagnosticsException)
                    {
                        EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy fetch traffic not recorded: {diagnosticsException.Message}");
                    }
                }

                // #65: rule triggers run on the fetch path AFTER the response is known - the row's
                // status filter matches the actual response status. A transport failure (status 0)
                // never matches; the response frames are already on their way and an event fault
                // must not touch them.
                RaiseTriggerEventsSafely(payload, query, result.StatusCode);

                if (requesterFqid == null)
                {
                    // Nowhere to answer: broadcasting fetched page bodies to every messaging
                    // client is a leak, not a fallback. The requester's own deadline closes its
                    // exchange.
                    EnvironmentManager.Instance.Log(
                        true,
                        nameof(ProxyFetchBackgroundPlugin),
                        $"Proxy fetch response for {payload.CorrelationId} not sent: the request carried no requester endpoint.");
                    return;
                }

                foreach (var frame in ProxyFetchFramer.Frame(payload.CorrelationId, result, limits.ChunkSizeBytes))
                {
                    communication.TransmitMessage(new Message(ProxyMessageIds.FetchResponseId, ProxyFetchWireCodec.ToWire(frame)), requesterFqid, null, null);
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy fetch aborted (target={(target == null ? "unknown" : ProxyPolicy.CanonicalOrigin(target))}): {exception.Message}");

                // #69: an aborted fetch still counted as one request for its session; the generic
                // Error fault is not a per-origin block, so no ring entry.
                var aborted = _diagnostics;
                if (aborted != null && !string.IsNullOrEmpty(payload.SessionId))
                {
                    try
                    {
                        aborted.RecordFetchTraffic(payload.SessionId, query.Body == null ? 0 : query.Body.Length, 0, DateTime.UtcNow);
                    }
                    catch (Exception diagnosticsException)
                    {
                        EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy fetch traffic not recorded: {diagnosticsException.Message}");
                    }
                }

                try
                {
                    TransmitFailure(payload.CorrelationId, requesterFqid, ProxyFetchErrorCodes.Error, exception.Message);
                }
                catch (Exception transmitException)
                {
                    EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy failure frame not sent: {transmitException.Message}");
                }
            }
        }

        /// <summary>
        /// ADR-0003: answer one loopback-leaf mint request. Issuance is local CPU plus a store
        /// lookup - fast enough for the messaging receive thread, unlike the fetch path. The
        /// response is unicasted to the requester endpoint exactly like fetch frames: a null
        /// destination would broadcast a certificate to every subscribed host.
        /// </summary>
        private object OnProxyLeafRequest(Message message, FQID destination, FQID source)
        {
            var payload = ProxyFetchWireCodec.LeafRequestFromWire(message?.Data);
            if (payload == null || string.IsNullOrEmpty(payload.CorrelationId))
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), "Leaf request carried no usable payload; refused.");
                return null;
            }

            var requester = source ?? message?.ExternalMessageSourceEndPoint;
            if (requester == null)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Leaf request {payload.CorrelationId} carried no requester endpoint; its response is logged rather than broadcast.");
                return null;
            }

            var communication = _messageCommunication;
            if (communication == null)
            {
                return null;
            }

            ProxyLeafResponsePayload response;
            try
            {
                response = _leafIssuer.Issue(payload);
            }
            catch (Exception exception)
            {
                // Issue is written not to throw; anything escaping still answers as a failure
                // frame rather than leaving the requester to time out.
                response = new ProxyLeafResponsePayload
                {
                    CorrelationId = payload.CorrelationId,
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = exception.Message,
                };
            }

            try
            {
                communication.TransmitMessage(
                    new Message(ProxyMessageIds.LeafResponseId, ProxyFetchWireCodec.ToWire(response)),
                    requester,
                    null,
                    null);
            }
            catch (Exception transmitException)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Leaf response {response.CorrelationId} not sent: {transmitException.Message}");
            }

            return null;
        }

        /// <summary>
        /// ADR-0003: the long-lived deployment CA for this Event Server machine, loaded from (or
        /// created in) LocalMachine\Personal on first use. The private key never leaves this
        /// machine's machine-level key storage; a service that cannot write that store answers
        /// NoAuthority and recovers once an operator provisions the CA (the creation is retried
        /// per request until it succeeds).
        /// </summary>
        private Authority ResolveDeploymentAuthority()
        {
            lock (_authorityGate)
            {
                if (_deploymentAuthority == null)
                {
                    _deploymentAuthority = Authority.LoadOrCreateDeploymentAuthority(
                        message => EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), message));
                }

                return _deploymentAuthority;
            }
        }

        /// <summary>
        /// Ticket #57: answer one tunnel-open. Re-gated against this process's own allowlist
        /// union (the client's decision table is the first gate, not the only one), then the open
        /// runs on the thread pool: connect + handshake replay is seconds long and must never
        /// block the messaging receive thread. Frames for this tunnel unicast to the requester
        /// endpoint - a null destination would broadcast one device's websocket frames to every
        /// subscribed host.
        /// </summary>
        private object OnProxyTunnelOpen(Message message, FQID destination, FQID source)
        {
            var requester = ResolveRequester(source, message);

            var payload = ProxyFetchWireCodec.TunnelFromWire(message?.Data);
            if (payload == null || string.IsNullOrEmpty(payload.CorrelationId))
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), "Tunnel open carried no usable payload; refused.");
                return null;
            }

            var communication = _messageCommunication;
            var tunnels = _tunnels;
            if (communication == null || tunnels == null)
            {
                TransmitTunnelOpenAck(communication, requester, payload.CorrelationId, new ProxyTunnelOpenOutcome
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = "The proxy tunnel server is not ready.",
                });
                return null;
            }

            Uri target;
            string allowReason = null;
            if (!Uri.TryCreate(payload.Url, UriKind.Absolute, out target)
                || !ProxyPolicy.IsHttpUri(target)
                || _allowlist == null
                || !_allowlist.IsAllowed(target, out allowReason))
            {
                // #60: https-scheme tunnel targets (wss upgrades) are re-gated against this
                // process's own allowlist exactly like http ones - the entry's scheme decides
                // whether the dialer adds a validated TLS leg, never whether the target is asked.
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Tunnel open {payload.CorrelationId} refused: {(allowReason ?? "the tunnel target was not an absolute http(s) origin")}.");
                RecordTunnelOpenAttempt(payload.SessionId, target, ProxyFetchErrorCodes.NotAllowed);
                TransmitTunnelOpenAck(communication, requester, payload.CorrelationId, new ProxyTunnelOpenOutcome
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.NotAllowed,
                    ErrorMessage = allowReason ?? "The tunnel target is not an absolute allowlisted http(s) origin.",
                });
                return null;
            }

            if (requester == null)
            {
                // Nowhere to unicast the frame stream: a broadcast tunnel leaks every frame to
                // every subscriber, so the tunnel must not open at all.
                return null;
            }

            _ = Task.Run(async () =>
            {
                Func<ProxyTunnelPayload, Task> toClient = frame =>
                {
                    var id = frame.Kind == ProxyTunnelFrameKinds.Close
                        ? ProxyMessageIds.TunnelCloseFromTargetId
                        : ProxyMessageIds.TunnelDataFromTargetId;
                    try
                    {
                        communication.TransmitMessage(new Message(id, ProxyFetchWireCodec.ToWire(frame)), requester, null, null);
                    }
                    catch (Exception exception)
                    {
                        EnvironmentManager.Instance.Log(
                            true,
                            nameof(ProxyFetchBackgroundPlugin),
                            $"Tunnel {frame.CorrelationId} frame ({id}) not sent: {exception.Message}");
                    }

                    return Task.CompletedTask;
                };

                ProxyTunnelOpenOutcome outcome;
                try
                {
                    outcome = await tunnels.OpenAsync(payload, toClient);
                }
                catch (Exception exception)
                {
                    // OpenAsync is written not to throw; anything escaping still answers as a
                    // failure rather than leaving the browser waiting for a 101 that never comes.
                    outcome = new ProxyTunnelOpenOutcome
                    {
                        Success = false,
                        ErrorCode = ProxyFetchErrorCodes.Error,
                        ErrorMessage = exception.Message,
                    };
                }

                // #69: the open attempt counts as one request for the session; a target-side
                // failure (unreachable, TLS refusal, non-101 answer) is a per-origin block.
                RecordTunnelOpenAttempt(payload.SessionId, target, outcome.Success ? null : outcome.ErrorCode);

                TransmitTunnelOpenAck(communication, requester, payload.CorrelationId, outcome);
            });

            return null;
        }

        private object OnProxyTunnelDataFrame(Message message, FQID destination, FQID source)
        {
            var tunnels = _tunnels;
            if (tunnels != null)
            {
                tunnels.OnClientData(ProxyFetchWireCodec.TunnelFromWire(message?.Data));
            }

            return null;
        }

        private object OnProxyTunnelCloseFrame(Message message, FQID destination, FQID source)
        {
            var tunnels = _tunnels;
            if (tunnels != null)
            {
                tunnels.OnClientClose(ProxyFetchWireCodec.TunnelFromWire(message?.Data));
            }

            return null;
        }

        /// <summary>
        /// #61: the per-target certificate posture for the wss tunnel's TLS leg. The allowlist's
        /// accept-untrusted decision for exactly this canonical origin (the same scoped #56
        /// decision the fetch path applies at line "OnProxyFetchRequest") becomes the validation
        /// callback on THIS tunnel's own SslStream; everything else — no flag, an http-scheme
        /// target, a closed gate, or version skew hiding the flag — resolves to null, SslStream's
        /// default full chain validation. Never a process-wide bypass, never the browser-side
        /// loopback termination.
        /// </summary>
        private RemoteCertificateValidationCallback ResolveTunnelTlsValidation(Uri target)
        {
            var allowlist = _allowlist;
            return allowlist == null ? null : allowlist.TunnelTlsValidationCallback(target);
        }

        /// <summary>
        /// #69: one client-decided proxy block. The workspace must be a configured workspace item on
        /// this server (a stale view item or a crafted payload has nowhere to be filed), and the
        /// reason must be one of the six wire categories. Dedup and ring eviction live in the
        /// aggregator; successes stay quiet - a refusal storm must not spam the MIP log.
        /// </summary>
        private object OnProxyBlockReport(Message message, FQID destination, FQID source)
        {
            var report = ProxyFetchWireCodec.BlockReportFromWire(message?.Data);
            if (report == null || string.IsNullOrEmpty(report.Origin) || string.IsNullOrEmpty(report.Reason))
            {
                return null;
            }

            var diagnostics = _diagnostics;
            if (diagnostics == null || !IsKnownBlockReason(report.Reason) || !IsKnownWorkspaceItemId(report.WorkspaceItemId))
            {
                return null;
            }

            try
            {
                if (diagnostics.RecordBlock(report.WorkspaceItemId, report.Origin, report.Reason, report.Count, DateTime.UtcNow))
                {
                    // One line per newly blocked origin (workspace+origin+reason); the dedup
                    // refreshes that follow stay quiet, so a refusal storm cannot bury the log.
                    EnvironmentManager.Instance.Log(
                        false,
                        nameof(ProxyFetchBackgroundPlugin),
                        $"Proxy block recorded: workspace='{report.WorkspaceItemId}' origin='{report.Origin}' reason={report.Reason} client='{report.ClientMachine}'");
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy block report not recorded: {exception.Message}");
            }

            return null;
        }

        /// <summary>
        /// #69: one session-lifecycle event. Open registers the session under its (validated)
        /// workspace with the user resolved from the platform's endpoint identity table - never
        /// from a client-sent claim; Heartbeat keeps it off the idle backstop; Close retires it.
        /// </summary>
        private object OnProxySessionEvent(Message message, FQID destination, FQID source)
        {
            var payload = ProxyFetchWireCodec.SessionEventFromWire(message?.Data);
            if (payload == null || string.IsNullOrEmpty(payload.SessionId) || string.IsNullOrEmpty(payload.Kind))
            {
                return null;
            }

            var diagnostics = _diagnostics;
            if (diagnostics == null)
            {
                return null;
            }

            try
            {
                var now = DateTime.UtcNow;
                switch (payload.Kind)
                {
                    case ProxySessionEventKinds.Open:
                        if (!IsKnownWorkspaceItemId(payload.WorkspaceItemId))
                        {
                            EnvironmentManager.Instance.Log(
                                true,
                                nameof(ProxyFetchBackgroundPlugin),
                                $"Proxy session open for unknown workspace '{payload.WorkspaceItemId}' dropped.");
                            return null;
                        }

                        var openUser = ResolveEndpointUser(message);
                        diagnostics.OpenSession(payload.SessionId, payload.WorkspaceItemId, openUser, payload.ClientMachine, now);
                        EnvironmentManager.Instance.Log(
                            false,
                            nameof(ProxyFetchBackgroundPlugin),
                            $"Proxy diagnostics session open: workspace='{payload.WorkspaceItemId}' user='{openUser ?? "(unknown)"}' machine='{payload.ClientMachine}'");
                        break;

                    case ProxySessionEventKinds.Heartbeat:
                        diagnostics.HeartbeatSession(payload.SessionId, now);
                        break;

                    case ProxySessionEventKinds.Close:
                        diagnostics.CloseSession(payload.SessionId, now);
                        break;
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy session event ({payload.Kind}) not recorded: {exception.Message}");
            }

            return null;
        }

        /// <summary>
        /// #69: the pull of one workspace's live proxy state. Answered unicast to the requester
        /// endpoint like every other proxy response (a broadcast snapshot would leak one customer's
        /// browsing pattern to every subscriber). An unknown workspace answers as a failure, not an
        /// empty snapshot, so the MC page can tell "gone" from "quiet".
        /// </summary>
        private object OnProxyStatusRequest(Message message, FQID destination, FQID source)
        {
            var payload = ProxyFetchWireCodec.StatusRequestFromWire(message?.Data);
            if (payload == null || string.IsNullOrEmpty(payload.CorrelationId))
            {
                return null;
            }

            var requester = ResolveRequester(source, message);
            ProxyStatusResponsePayload response;
            var diagnostics = _diagnostics;
            if (diagnostics == null)
            {
                response = new ProxyStatusResponsePayload
                {
                    CorrelationId = payload.CorrelationId,
                    WorkspaceItemId = payload.WorkspaceItemId,
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = "The proxy fetcher is not ready.",
                };
            }
            else if (!IsKnownWorkspaceItemId(payload.WorkspaceItemId))
            {
                response = new ProxyStatusResponsePayload
                {
                    CorrelationId = payload.CorrelationId,
                    WorkspaceItemId = payload.WorkspaceItemId,
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.BadRequest,
                    ErrorMessage = "The workspace item id is not a configured workspace on this server.",
                };
            }
            else
            {
                response = diagnostics.Snapshot(payload.WorkspaceItemId, DateTime.UtcNow);
                response.CorrelationId = payload.CorrelationId;
            }

            var communication = _messageCommunication;
            if (communication == null || requester == null)
            {
                return null;
            }

            try
            {
                communication.TransmitMessage(
                    new Message(ProxyMessageIds.StatusResponseId, ProxyFetchWireCodec.ToWire(response)),
                    requester,
                    null,
                    null);
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy status response {payload.CorrelationId} not sent: {exception.Message}");
            }

            return null;
        }

        /// <summary>
        /// Ticket #75: answer one Management Client pull of the deployment CA's public
        /// certificate. The lookup is a store read - fast enough for the messaging receive
        /// thread, like the leaf mint. The answer is unicasted to the requester endpoint: a
        /// null destination would deliver the certificate to every host subscribed to the
        /// export response id. Export never creates the CA and never reads a private key - an
        /// absent CA is a refusal the admin can act on, not an empty success.
        /// </summary>
        private object OnProxyCaExportRequest(Message message, FQID destination, FQID source)
        {
            var payload = ProxyFetchWireCodec.CaExportRequestFromWire(message?.Data);
            if (payload == null || string.IsNullOrEmpty(payload.CorrelationId))
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), "Proxy certificate export request carried no usable payload; refused.");
                return null;
            }

            var requester = ResolveRequester(source, message);
            if (requester == null)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy certificate export request {payload.CorrelationId} carried no requester endpoint; its response is logged rather than broadcast.");
                return null;
            }

            var communication = _messageCommunication;
            if (communication == null)
            {
                return null;
            }

            ProxyCaExportResponsePayload response;
            try
            {
                // Respond is written not to throw; anything escaping still answers as a failure
                // frame rather than leaving the Management Client to time out.
                response = _caExportResponder.Respond(payload);
            }
            catch (Exception exception)
            {
                response = new ProxyCaExportResponsePayload
                {
                    CorrelationId = payload.CorrelationId,
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = exception.Message,
                };
            }

            try
            {
                communication.TransmitMessage(
                    new Message(ProxyMessageIds.CaExportResponseId, ProxyFetchWireCodec.ToWire(response)),
                    requester,
                    null,
                    null);
            }
            catch (Exception transmitException)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy certificate export response {response.CorrelationId} not sent: {transmitException.Message}");
            }

            return null;
        }

        /// <summary>
        /// #69: traffic from the tunnel server's per-chunk seam. Fire-and-swallow: accounting must
        /// never break a byte pump.
        /// </summary>
        private void RecordTunnelTrafficSafe(string sessionId, long clientBytes, long targetBytes)
        {
            var diagnostics = _diagnostics;
            if (diagnostics == null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            try
            {
                diagnostics.RecordTunnelTraffic(sessionId, clientBytes, targetBytes, DateTime.UtcNow);
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy tunnel traffic not recorded: {exception.Message}");
            }
        }

        /// <summary>
        /// #69: count one tunnel-open attempt toward its session (+1 request) and file a block when
        /// the failure is a per-origin category (the gate refusal, an unreachable target, a refused
        /// handshake). Successes and component faults (Error) count but never block.
        /// </summary>
        private void RecordTunnelOpenAttempt(string sessionId, Uri target, string errorCode)
        {
            var diagnostics = _diagnostics;
            if (diagnostics == null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }

            try
            {
                var now = DateTime.UtcNow;
                diagnostics.RecordFetchTraffic(sessionId, 0, 0, now);
                string reason;
                if (target != null && !string.IsNullOrEmpty(errorCode) && ProxyBlockReasons.TryMapErrorCode(errorCode, out reason))
                {
                    diagnostics.RecordBlockForSession(sessionId, ProxyPolicy.CanonicalOrigin(target), reason, now);
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy tunnel open not recorded: {exception.Message}");
            }
        }

        private void SweepSessionsSafely()
        {
            try
            {
                var diagnostics = _diagnostics;
                if (diagnostics == null)
                {
                    return;
                }

                var reclaimed = diagnostics.SweepIdleSessions(SessionIdleBackstop, DateTime.UtcNow);
                if (reclaimed.Count > 0)
                {
                    EnvironmentManager.Instance.Log(
                        false,
                        nameof(ProxyFetchBackgroundPlugin),
                        $"Proxy diagnostics: {reclaimed.Count} session(s) idle past {SessionIdleBackstop.TotalMinutes:0} minutes reclaimed (a Smart Client died without a session close).");
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy diagnostics sweep faulted: {exception.Message}");
            }
        }

        /// <summary>
        /// #65: raise the platform event for one rule-trigger match on the event bus
        /// (Server.NewEventCommand, the system-defined raise the Event Server processes through its
        /// matcher chain). Fire-and-swallow: a trigger event must never alter or break the fetch
        /// response that is already on its way back to the browser. The fetch payload's workspace
        /// attribution wins; an older Smart Client that stamps no workspace id falls back to the
        /// session's workspace in the diagnostics session map; an unattributable request is fail-
        /// closed (no event), mirroring how #69 treats unattributed traffic.
        /// </summary>
        private void RaiseTriggerEventsSafely(ProxyFetchRequestPayload payload, ProxyFetchQuery query, int responseStatus)
        {
            var matcher = _triggerMatcher;
            if (matcher == null || query == null || responseStatus <= 0)
            {
                return;
            }

            try
            {
                var workspaceItemId = payload.WorkspaceItemId;
                if (string.IsNullOrEmpty(workspaceItemId))
                {
                    string mapped;
                    workspaceItemId = _diagnostics != null && _diagnostics.TryGetSessionWorkspace(payload.SessionId, out mapped)
                        ? mapped
                        : null;
                }

                ProxyTriggerMatcher.TriggerMatch match;
                if (!matcher.TryMatch(workspaceItemId, query.Uri, query.Method, responseStatus, out match))
                {
                    return;
                }

                var workspaceName = match.WorkspaceName ?? match.WorkspaceItemId;
                var row = match.RowDescription ?? "row";
                var analyticsEvent = new VideoOS.Platform.Data.AnalyticsEvent
                {
                    EventHeader = new VideoOS.Platform.Data.EventHeader
                    {
                        ID = Guid.NewGuid(),
                        Timestamp = DateTime.Now,
                        Message = ProxyTriggerPolicy.EventType,
                        // The admin's Analytics Event definition matches on Message; the fixed
                        // property text (workspace | method | url | status) rides in CustomTag for
                        // the rule's condition values and action text.
                        CustomTag = $"{workspaceName} | {query.Method} {query.Uri.AbsoluteUri} | HTTP {responseStatus} | row {row}",
                        Source = new VideoOS.Platform.Data.EventSource
                        {
                            FQID = WorkspaceItemFqid(match.WorkspaceItemId),
                            Name = workspaceName,
                        },
                    },
                    Description = $"{workspaceName} | {query.Method} {query.Uri.AbsoluteUri} | HTTP {responseStatus}",
                };
                RaiseAnalyticsEvent(analyticsEvent);
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy trigger evaluation faulted: {exception.Message}");
            }
        }

        /// <summary>
        /// #65: workspace item id (the FQID ObjectId string) → the workspace's FQID for the event
        /// Source. Live-proven on the Event Server: its event processor validates the source and
        /// answers a sourceless event with ArgumentException "Invalid event", so a match whose
        /// workspace item vanished mid-flight produces a raise the Event Server rejects (logged
        /// by <see cref="RaiseAnalyticsEvent"/>, never a fault in the fetch path).
        /// </summary>
        private static FQID WorkspaceItemFqid(string workspaceItemId)
        {
            if (string.IsNullOrEmpty(workspaceItemId))
            {
                return null;
            }

            try
            {
                List<Item> workspaces;
                Exception error;
                if (!Admin.WorkspaceItemManager.TryGetWorkspaces(out workspaces, out error))
                {
                    return null;
                }

                foreach (var workspace in workspaces)
                {
                    if (workspace != null && workspace.FQID != null &&
                        string.Equals(workspace.FQID.ObjectId.ToString(), workspaceItemId, StringComparison.OrdinalIgnoreCase))
                    {
                        return workspace.FQID;
                    }
                }
            }
            catch (Exception)
            {
                // Attribution is best-effort.
            }

            return null;
        }

        private static bool SelfTestFlagFilePresent()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(
                    new System.Uri(typeof(ProxyFetchBackgroundPlugin).Assembly.CodeBase).LocalPath);
                return !string.IsNullOrEmpty(directory) &&
                       System.IO.File.Exists(System.IO.Path.Combine(directory, "trigger-selftest.flag"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// #65: the single raise route for this plugin's analytics events. The documented way for
        /// a plugin to generate an analytics event is <c>EnvironmentManager.Instance.SendMessage</c>
        /// with a <see cref="MessageId.Server.NewEventCommand"/> message whose Data is a TYPED
        /// <c>VideoOS.Platform.Data.AnalyticsEvent</c>: unlike the plugin-namespace payloads that
        /// forced the #54 JSON-string discipline, AnalyticsEvent is a platform type every host
        /// binds through its own type resolution. (Raw MessageCommunication.TransmitMessage of a
        /// system-defined message id is refused by the transmit layer outright.) Replies, if any,
        /// are logged: an empty reply set is the Event Server core consuming the event normally.
        /// </summary>
        private void RaiseAnalyticsEvent(VideoOS.Platform.Data.AnalyticsEvent analyticsEvent)
        {
            try
            {
                var results = EnvironmentManager.Instance.SendMessage(
                    new Message(MessageId.Server.NewEventCommand)
                    {
                        Data = analyticsEvent,
                        RelatedFQID = analyticsEvent.EventHeader.Source != null
                            ? analyticsEvent.EventHeader.Source.FQID
                            : null,
                    });

                string replies = null;
                if (results != null && results.Count > 0)
                {
                    var first = results[0];
                    replies = $" replies={results.Count} first={first}";
                }

                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Analytics event raised: {analyticsEvent.EventHeader.ID} \"{analyticsEvent.EventHeader.Message}\"{replies}");
            }
            catch (Exception raiseException)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Analytics event not raised ({analyticsEvent.EventHeader.CustomTag ?? analyticsEvent.EventHeader.Message}): {raiseException.GetType().Name}: {raiseException.Message}");
            }
        }

        /// <summary>
        /// #65 live-gate self-test: one probe event rides the same raise path the trigger matches
        /// use, and the NewEventIndication receiver logs its return; the pair of log lines proves
        /// the plugin's event registration (raise + observe) is alive inside the running service.
        /// Requested either by WEBCONTENT_TRIGGER_SELFTEST=1 in the service environment or by a
        /// trigger-selftest.flag file next to the plugin dll (the deployed folder's write ACL is a
        /// deliberate operator grant, so a lab run can request the probe without touching machine
        /// state). One-shot per process; never fires without one of the two switches.
        /// </summary>
        private void RaiseTriggerSelfTestProbeIfRequested()
        {
            if (Environment.GetEnvironmentVariable("WEBCONTENT_TRIGGER_SELFTEST") != "1" && !SelfTestFlagFilePresent())
            {
                return;
            }

            if (Interlocked.Exchange(ref _selfTestProbeDone, 1) != 0)
            {
                return;
            }

            try
            {
                // The probe carries the SAME source shape as a real trigger event (an existing
                // VMS item as the event source): the Event Server's event processor validates the
                // source and rejects sourceless events with ArgumentException "Invalid event" -
                // proven live. The first workspace item in the system is as faithful as any.
                FQID sourceFqid = null;
                string sourceName = null;
                List<Item> workspaces;
                Exception lookupError;
                if (Admin.WorkspaceItemManager.TryGetWorkspaces(out workspaces, out lookupError) &&
                    workspaces != null && workspaces.Count > 0)
                {
                    sourceFqid = workspaces[0].FQID;
                    sourceName = workspaces[0].Name;
                }

                var probe = new VideoOS.Platform.Data.AnalyticsEvent
                {
                    EventHeader = new VideoOS.Platform.Data.EventHeader
                    {
                        ID = Guid.NewGuid(),
                        Timestamp = DateTime.Now,
                        Message = ProxyTriggerPolicy.EventType,
                        CustomTag = "selftest",
                        Source = new VideoOS.Platform.Data.EventSource
                        {
                            FQID = sourceFqid,
                            Name = sourceName ?? "WebContent self-test",
                        },
                    },
                    Description = "WebContent trigger self-test probe",
                };
                RaiseAnalyticsEvent(probe);
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Trigger self-test probe raised: {probe.EventHeader.ID} \"{ProxyTriggerPolicy.EventType}\" source={sourceName ?? "(none)"}");
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Trigger self-test probe failed: {exception.Message}");
            }
        }

        /// <summary>
        /// #65: the platform event bus echo for this process. Only WebContent's own event type is
        /// logged (the bus carries every VMS event; logging them all would bury the MIP log); the
        /// self-test probe id logs with it as the raise-path round trip marker.
        /// </summary>
        private object OnPlatformNewEvent(Message message, FQID sourceFqid, FQID destinationFqid)
        {
            try
            {
                if (message == null)
                {
                    return null;
                }

                // Typed-first: the raise path sends the platform's own AnalyticsEvent object, and
                // every host binds that type through its own resolution (the #54 silent-null
                // hazard was plugin-namespace types only). The string branch is the belt for a
                // host that hands back the serialized form.
                var evt = message.Data as VideoOS.Platform.Data.AnalyticsEvent;
                if (evt == null)
                {
                    var json = message.Data as string;
                    if (!string.IsNullOrEmpty(json))
                    {
                        evt = ProxyTriggerEventCodec.FromWire(json);
                    }
                }

                if (evt == null || evt.EventHeader == null)
                {
                    return null;
                }

                if (string.Equals(evt.EventHeader.Message, ProxyTriggerPolicy.EventType, StringComparison.OrdinalIgnoreCase))
                {
                    EnvironmentManager.Instance.Log(
                        false,
                        nameof(ProxyFetchBackgroundPlugin),
                        $"Proxy trigger event seen on the bus: {evt.EventHeader.ID} \"{evt.EventHeader.Message}\" ({evt.EventHeader.CustomTag ?? "no tag"})");
                }
            }
            catch (Exception)
            {
                // Observation only; a fault here must not touch messaging.
            }

            return null;
        }

        /// <summary>
        /// #65: per-workspace trigger rows for the matcher, read from the WebContent item settings
        /// the Management Client page stores (the proxyTriggers section). Invalid rows are refused
        /// by ProxyTriggerPolicy with a specific reason logged; one bad row never drops the
        /// workspace's other rows.
        /// </summary>
        private static System.Collections.Generic.IEnumerable<ProxyWorkspaceTriggerSource> ResolveWorkspaceTriggerSnapshots()
        {
            List<Item> workspaces;
            Exception error;
            if (!Admin.WorkspaceItemManager.TryGetWorkspaces(out workspaces, out error))
            {
                throw error ?? new InvalidOperationException("Workspace configuration lookup failed.");
            }

            var sources = new List<ProxyWorkspaceTriggerSource>();
            foreach (var workspace in workspaces)
            {
                if (workspace != null && workspace.FQID != null && workspace.Properties != null)
                {
                    sources.Add(new ProxyWorkspaceTriggerSource
                    {
                        ItemId = workspace.FQID.ObjectId.ToString(),
                        Name = workspace.Name,
                        Properties = workspace.Properties,
                    });
                }
            }

            return sources;
        }

        /// <summary>
        /// #69: the XProtect login name of the endpoint that sent this message, from the platform's
        /// own endpoint identity table. Null for endpoints the table does not name yet.
        /// </summary>
        private object OnWhoAreOnlineResponse(Message message, FQID destination, FQID source)
        {
            FeedEndpointIdentities(message == null ? null : message.Data);
            return null;
        }

        private object OnNewEndPointIndication(Message message, FQID destination, FQID source)
        {
            FeedEndpointIdentities(message == null ? null : message.Data);
            return null;
        }

        private void FeedEndpointIdentities(object data)
        {
            try
            {
                if (data == null)
                {
                    return;
                }

                var single = data as EndPointIdentityData;
                if (single != null)
                {
                    UpsertEndpointUser(single);
                    return;
                }

                var collection = data as System.Collections.IEnumerable;
                if (collection == null)
                {
                    return;
                }

                foreach (var item in collection)
                {
                    var identity = item as EndPointIdentityData;
                    if (identity != null)
                    {
                        UpsertEndpointUser(identity);
                    }
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy endpoint identity table update failed: {exception.Message}");
            }
        }

        private void UpsertEndpointUser(EndPointIdentityData identity)
        {
            var fqid = identity.EndPointFQID;
            if (fqid == null || string.IsNullOrEmpty(identity.IdentityName))
            {
                return;
            }

            lock (_identitySync)
            {
                _endpointUsers[fqid.ObjectId.ToString()] = identity.IdentityName;
            }
        }

        private string ResolveEndpointUser(Message message)
        {
            var endpoint = message == null ? null : message.ExternalMessageSourceEndPoint;
            if (endpoint == null)
            {
                return null;
            }

            lock (_identitySync)
            {
                string name;
                return _endpointUsers.TryGetValue(endpoint.ObjectId.ToString(), out name) ? name : null;
            }
        }

        private static bool IsKnownBlockReason(string reason)
        {
            return reason == ProxyBlockReasons.NotAllowed
                || reason == ProxyBlockReasons.TooLarge
                || reason == ProxyBlockReasons.CertUntrusted
                || reason == ProxyBlockReasons.Unreachable
                || reason == ProxyBlockReasons.Timeout
                || reason == ProxyBlockReasons.TunnelRefused;
        }

        /// <summary>
        /// #69: is this a workspace item this server actually owns? The same Configuration lookup
        /// the allowlist union uses; a failed lookup fails closed (the report or query is dropped) -
        /// unlike the allowlist there is nothing last-known-good to keep serving.
        /// </summary>
        private bool IsKnownWorkspaceItemId(string workspaceItemId)
        {
            Guid id;
            if (string.IsNullOrEmpty(workspaceItemId) || !Guid.TryParse(workspaceItemId, out id) || id == Guid.Empty)
            {
                return false;
            }

            try
            {
                List<Item> workspaces;
                Exception error;
                if (!Admin.WorkspaceItemManager.TryGetWorkspaces(out workspaces, out error))
                {
                    return false;
                }

                foreach (var workspace in workspaces)
                {
                    if (workspace != null && workspace.FQID != null && workspace.FQID.ObjectId.ToString() == workspaceItemId)
                    {
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy diagnostics workspace lookup failed: {exception.Message}");
            }

            return false;
        }

        private static void TransmitTunnelOpenAck(MessageCommunication communication, FQID requester, string tunnelId, ProxyTunnelOpenOutcome outcome)
        {
            if (communication == null || requester == null)
            {
                return;
            }

            try
            {
                communication.TransmitMessage(
                    new Message(
                        ProxyMessageIds.TunnelOpenResponseId,
                        ProxyFetchWireCodec.ToWire(new ProxyTunnelPayload
                        {
                            CorrelationId = tunnelId,
                            Success = outcome.Success,
                            StatusCode = outcome.StatusCode,
                            DataBase64 = outcome.HandshakeBytes == null || outcome.HandshakeBytes.Length == 0
                                ? null
                                : Convert.ToBase64String(outcome.HandshakeBytes),
                            ErrorCode = outcome.ErrorCode,
                            ErrorMessage = outcome.ErrorMessage,
                        })),
                    requester,
                    null,
                    null);
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Tunnel open ack {tunnelId} not sent: {exception.Message}");
            }
        }

        /// <summary>
        /// The endpoint to answer a request on. The explicit source FQID (rarely sent) wins; the
        /// platform-filled ExternalMessageSourceEndPoint is the normal answer path. When neither
        /// exists there is no unicast route, and responses must not fall back to a broadcast.
        /// </summary>
        private static FQID ResolveRequester(FQID source, Message message)
        {
            var requester = source ?? message?.ExternalMessageSourceEndPoint;
            if (requester == null)
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    "Proxy fetch request carried no requester endpoint; any response for it will be logged rather than sent.");
            }

            return requester;
        }

        /// <summary>
        /// Snapshot of every workspace item's property dictionary for the allowlist union. The
        /// same Configuration lookup the Smart Client workspace provisioning uses; if it fails,
        /// the allowlist keeps its last-known-good gate (and stays closed before its first success).
        /// </summary>
        private static System.Collections.Generic.IEnumerable<Dictionary<string, string>> ResolveWorkspacePropertySnapshots()
        {
            if (!Admin.WorkspaceItemManager.TryGetWorkspaces(out var workspaces, out var error))
            {
                throw error ?? new InvalidOperationException("Workspace configuration lookup failed.");
            }

            var snapshots = new List<Dictionary<string, string>>();
            foreach (var workspace in workspaces)
            {
                if (workspace != null && workspace.Properties != null)
                {
                    snapshots.Add(workspace.Properties);
                }
            }

            return snapshots;
        }

        /// <summary>
        /// #73: the VMS's own host names for the wildcard SSRF floor - the Management Server /
        /// API Gateway host the platform knows from <c>EnvironmentManager</c> (the very server
        /// this fetcher runs beside / on). Wildcard allowlist entries never match a host listed
        /// here; explicit exact entries still may.
        /// </summary>
        private static System.Collections.Generic.IEnumerable<string> ResolveVmsFloorHosts()
        {
            var host = EnvironmentManager.Instance?.MasterSite?.ServerId?.Uri?.Host;
            return string.IsNullOrWhiteSpace(host) ? new string[0] : new[] { host };
        }

        private void TransmitFailure(string correlationId, FQID requester, string errorCode, string message)
        {
            var communication = _messageCommunication;
            // A null requester is never broadcast: the condition is already logged once by
            // ResolveRequester, and the client-side deadline closes the exchange.
            if (communication == null || requester == null)
            {
                return;
            }

            communication.TransmitMessage(
                new Message(
                    ProxyMessageIds.FetchResponseId,
                    ProxyFetchWireCodec.ToWire(new ProxyFetchResponsePayload
                    {
                        CorrelationId = correlationId,
                        Kind = ProxyFetchFrameKinds.Failure,
                        Sequence = -1,
                        ErrorCode = errorCode,
                        ErrorMessage = message,
                    })),
                requester,
                null,
                null);
        }

        private static void LogRefused(ProxyFetchRequestPayload payload, string reason)
        {
            EnvironmentManager.Instance.Log(
                true,
                nameof(ProxyFetchBackgroundPlugin),
                $"Proxy fetch request {(payload == null ? "" : payload.CorrelationId)} refused: {reason}.");
        }
    }
}
