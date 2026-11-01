using System;
using System.Collections.Generic;
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
        private SemaphoreSlim _fetchSlots;
        private ProxyServerAllowlist _allowlist;
        private ProxyLeafIssuer _leafIssuer;
        private ProxyTunnelServer _tunnels;

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
                // arbitrary targets by a crafted request.
                _allowlist = new ProxyServerAllowlist(
                    ResolveWorkspacePropertySnapshots,
                    message => EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), message));

                // ADR-0003: the same plugin answers leaf-mint requests for the loopback TLS
                // termination; the same allowlist union gates minting exactly like fetching.
                _leafIssuer = new ProxyLeafIssuer(
                    _allowlist,
                    ResolveDeploymentAuthority,
                    message => EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), message));
                _leafFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyLeafRequest,
                    new CommunicationIdFilter(ProxyMessageIds.LeafRequestId));

                // Ticket #57: the Event Server half of the websocket CONNECT tunnel. The client
                // has already allowlist-gated the upgrade; this side re-gates against its own
                // allowlist union, replays the handshake to the target over a raw socket, admits
                // only a 101, and pumps raw bytes both directions. The open runs on the thread
                // pool (connect + handshake is seconds long), so the receive thread stays free.
                _tunnels = new ProxyTunnelServer(
                    ProxyTunnelLimits.Default(),
                    message => EnvironmentManager.Instance.Log(false, nameof(ProxyFetchBackgroundPlugin), message));
                _tunnelOpenFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyTunnelOpen,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelOpenRequestId));
                _tunnelDataFrameFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyTunnelDataFrame,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelDataToTargetId));
                _tunnelCloseFrameFilterRegistration = _messageCommunication.RegisterCommunicationFilter(
                    OnProxyTunnelCloseFrame,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelCloseToTargetId));

                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Proxy fetcher ready (concurrency={MaxConcurrentFetches}, request id={ProxyMessageIds.FetchRequestId}).");
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
                foreach (var registration in new[] { _tunnelOpenFilterRegistration, _tunnelDataFrameFilterRegistration, _tunnelCloseFrameFilterRegistration })
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
                        EnvironmentManager.Instance.Log(true, nameof(ProxyFetchBackgroundPlugin), $"Proxy tunnel filter detach failed: {exception.Message}");
                    }
                }
            }

            _tunnelOpenFilterRegistration = null;
            _tunnelDataFrameFilterRegistration = null;
            _tunnelCloseFrameFilterRegistration = null;

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
                || target.Scheme != Uri.UriSchemeHttp
                || _allowlist == null
                || !_allowlist.IsAllowed(target, out allowReason))
            {
                EnvironmentManager.Instance.Log(
                    true,
                    nameof(ProxyFetchBackgroundPlugin),
                    $"Tunnel open {payload.CorrelationId} refused: {(allowReason ?? "the tunnel target was not an allowlisted http origin")}.");
                TransmitTunnelOpenAck(communication, requester, payload.CorrelationId, new ProxyTunnelOpenOutcome
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.NotAllowed,
                    ErrorMessage = allowReason ?? "The tunnel target is not an allowlisted http origin.",
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
