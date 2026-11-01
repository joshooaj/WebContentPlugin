using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VideoOS.Platform;
using VideoOS.Platform.Messaging;

namespace WebContent
{
    /// <summary>
    /// Requester side of the proxy-fetch contract (#53): sends WebContent.Proxy.FetchRequest over
    /// classic MIP messaging and hands back the reassembled outcome from the framed response
    /// stream. This is the client the ticket's "Management Client-driven harness" drives, and the
    /// seam the Smart Client view consumes in #54.
    ///
    /// The response receiver feeds the correlator; correlation identity (Guid) is local, so no
    /// other host's in-flight exchanges leak into this requester's results.
    /// </summary>
    internal sealed class ProxyFetchClient
    {
        /// <summary>
        /// Wired up during PluginDefinition.Init in the Administration environment; null until an
        /// environment provisions it.
        /// </summary>
        internal static ProxyFetchClient Active;

        private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

        private ServerId _serverId;
        private MessageCommunication _communication;
        private object _responseFilterRegistration;
        private object _leafResponseFilterRegistration;
        private object _echoFilterRegistration;
        private object _tunnelOpenResponseFilterRegistration;
        private object _tunnelDataFrameFilterRegistration;
        private object _tunnelCloseFrameFilterRegistration;
        private ProxyFetchCorrelation _correlation;
        private ProxyLeafCorrelation _leafCorrelation;
        private ProxyTunnelCorrelation _tunnels;
        private Timer _tunnelReaper;
        private readonly ProxyTunnelLimits _tunnelLimits = ProxyTunnelLimits.Default();
        private bool _trace;

        internal bool IsReady => _communication != null;

        /// <summary>
        /// Start MIP message communication for the master site and subscribe to proxy-fetch
        /// responses. Failures are logged, not thrown: an unready harness must not fail the host.
        /// </summary>
        internal void Setup(ServerId masterSite)
        {
            if (masterSite == null)
            {
                EnvironmentManager.Instance.Log(false, nameof(ProxyFetchClient), "Proxy fetch harness skipped: no master site is connected.");
                return;
            }

            try
            {
                // Messaging black holes (a request that transmits but never gets answered) are
                // invisible otherwise: the platform's own CommunicationClient traces every
                // transmit/receive when this flag is on. Opt in with WEBCONTENT_MIP_TRACE=1.
                if (Environment.GetEnvironmentVariable("WEBCONTENT_MIP_TRACE") == "1")
                {
                    _trace = true;
                    EnvironmentManager.Instance.TraceMessageCommunication = true;
                    EnvironmentManager.Instance.Log(false, nameof(ProxyFetchClient), "Messaging tracing enabled (WEBCONTENT_MIP_TRACE=1).");
                }

                MessageCommunicationManager.Start(masterSite);
                _serverId = masterSite;
                _communication = MessageCommunicationManager.Get(masterSite);
                _correlation = new ProxyFetchCorrelation();
                _leafCorrelation = new ProxyLeafCorrelation();
                _responseFilterRegistration = _communication.RegisterCommunicationFilter(
                    OnProxyFetchResponse,
                    new CommunicationIdFilter(ProxyMessageIds.FetchResponseId));

                // ADR-0003: the same client hosts the leaf-mint exchange the loopback listener's
                // TLS termination needs; its single-frame responses correlate separately.
                _leafResponseFilterRegistration = _communication.RegisterCommunicationFilter(
                    OnProxyLeafResponse,
                    new CommunicationIdFilter(ProxyMessageIds.LeafResponseId));

                // #57: websocket tunnels. The open ack and every target-pushed frame arrive on
                // these ids; the correlator owns the whole lifetime model and a sweep reclaims
                // idle/half-open tunnels (ticket criterion 4).
                _tunnels = new ProxyTunnelCorrelation();
                _tunnelOpenResponseFilterRegistration = _communication.RegisterCommunicationFilter(
                    OnTunnelOpenResponse,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelOpenResponseId));
                _tunnelDataFrameFilterRegistration = _communication.RegisterCommunicationFilter(
                    OnTunnelDataFromTarget,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelDataFromTargetId));
                _tunnelCloseFrameFilterRegistration = _communication.RegisterCommunicationFilter(
                    OnTunnelCloseFromTarget,
                    new CommunicationIdFilter(ProxyMessageIds.TunnelCloseFromTargetId));
                var sweepPeriod = TimeSpan.FromMilliseconds(Math.Max(1000, Math.Min(30_000, _tunnelLimits.IdleTimeoutMs / 2)));
                _tunnelReaper = new Timer(_ => SweepTunnels(), null, sweepPeriod, sweepPeriod);

                // Diagnostic (trace only): our own requests are filter-routed back to the sender's
                // endpoint queue. Inspecting that echo tests whether THIS process deserializes our
                // payload types at all: an intact echo isolates payload stripping to the response
                // leg, a null-Data echo isolates it to this process's message plumbing.
                if (_trace)
                {
                    _echoFilterRegistration = _communication.RegisterCommunicationFilter(
                        OnFetchRequestEcho,
                        new CommunicationIdFilter(ProxyMessageIds.FetchRequestId));
                }

                EnvironmentManager.Instance.Log(false, nameof(ProxyFetchClient), "Proxy fetch harness ready.");
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy fetch harness failed to start: {exception.Message}");
                _communication = null;
                _correlation = null;
            }
        }

        internal void Close()
        {
            var communication = _communication;
            if (communication != null)
            {
                foreach (var registration in new[] { _responseFilterRegistration, _leafResponseFilterRegistration, _echoFilterRegistration, _tunnelOpenResponseFilterRegistration, _tunnelDataFrameFilterRegistration, _tunnelCloseFrameFilterRegistration })
                {
                    if (registration == null)
                    {
                        continue;
                    }

                    try
                    {
                        communication.UnRegisterCommunicationFilter(registration);
                    }
                    catch (Exception exception)
                    {
                        EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy fetch filter detach failed: {exception.Message}");
                    }
                }
            }

            _responseFilterRegistration = null;
            _leafResponseFilterRegistration = null;
            _echoFilterRegistration = null;
            _tunnelOpenResponseFilterRegistration = null;
            _tunnelDataFrameFilterRegistration = null;
            _tunnelCloseFrameFilterRegistration = null;

            // #57: every live tunnel ends with an explicit close so the Event Server releases
            // its target sockets now, not at the ES-side idle sweep; then the sweep stops.
            var reaper = _tunnelReaper;
            _tunnelReaper = null;
            reaper?.Dispose();

            var tunnels = _tunnels;
            _tunnels = null;
            if (tunnels != null)
            {
                foreach (var tunnelId in tunnels.CloseAll("the Smart Client proxy client shut down"))
                {
                    TryTransmitTunnelClose(tunnelId, "the Smart Client closed");
                }
            }

            _communication = null;
            _correlation = null;
            _leafCorrelation = null;

            // Deliberately no MessageCommunicationManager.Stop: the messaging session is
            // process-wide and may be shared by other plugins (no MIP sample ever stops it).
            _serverId = null;
        }

        /// <summary>
        /// Send one GET proxy-fetch request (the Management Client harness path, #53). The
        /// browser-facing flows use the full <see cref="ProxyFetchQuery"/> overload below.
        /// </summary>
        internal Task<ProxyFetchResult> FetchAsync(string url, TimeSpan? deadline = null)
        {
            Uri target;
            if (!Uri.TryCreate(url, UriKind.Absolute, out target))
            {
                return Task.FromResult(Failure(ProxyFetchErrorCodes.BadRequest, "The proxy-fetch url must be an absolute http(s) uri."));
            }

            return FetchAsync(new ProxyFetchQuery { Uri = target, Method = "GET" }, deadline);
        }

        /// <summary>
        /// Send one proxy-fetch request and return the task completed with the reassembled
        /// outcome. The deadline bounds the whole exchange, not each frame. Since #55 the method,
        /// request headers, and body ride along — the JSON-string payload stays one string.
        /// </summary>
        internal Task<ProxyFetchResult> FetchAsync(ProxyFetchQuery query, TimeSpan? deadline = null)
        {
            var communication = _communication;
            if (communication == null)
            {
                // #58: this is a component/channel failure on the Smart Client side, not a target
                // failure - the distinct code renders the distinct proxy error page.
                return Task.FromResult(Failure(
                    ProxyFetchErrorCodes.ServerUnavailable,
                    "The Smart Client has no proxy messaging channel to the VMS Event Server (the Event Server may be stopped or restarted since this session connected)."));
            }

            Uri target = query == null ? null : query.Uri;
            if (!ProxyPolicy.IsHttpUri(target))
            {
                return Task.FromResult(Failure(ProxyFetchErrorCodes.BadRequest, "The proxy-fetch url must be an absolute http(s) uri."));
            }

            var correlationId = Guid.NewGuid().ToString("N");
            var completion = _correlation.Begin(correlationId, deadline ?? DefaultDeadline);

            try
            {
                // destinationEndPoint null: the Event Server filter-routes this to the proxy
                // fetcher plugin. The source argument stays null because the messaging layer
                // stamps every transmit with the sender's endpoint FQID (ExternalMessageSource-
                // EndPoint on the receiver), which is what the fetcher answers back to.
                // Data is a JSON string, never the payload class: the platform binds custom
                // payload types via Type.GetType and silently delivers null Data on hosts whose
                // type resolution cannot see WebContent.dll (Smart Client). See ProxyFetchWireCodec.
                communication.TransmitMessage(
                    new Message(
                        ProxyMessageIds.FetchRequestId,
                        ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload
                        {
                            CorrelationId = correlationId,
                            Url = target.AbsoluteUri,
                            Method = string.IsNullOrWhiteSpace(query.Method) ? null : query.Method.Trim().ToUpperInvariant(),
                            Headers = query.Headers != null && query.Headers.Count > 0 ? query.Headers : null,
                            BodyBase64 = query.Body != null && query.Body.Length > 0 ? Convert.ToBase64String(query.Body) : null,
                        })),
                    null,
                    null,
                    null);
            }
            catch (Exception exception)
            {
                // MIP messaging is down (a stopped/restarted Event Server disposes every client's
                // MessageCommunication): name the channel failure for the view (#58) instead of
                // letting the request time out as an anonymous 30-second wait.
                _correlation.Fail(
                    correlationId,
                    ProxyFetchErrorCodes.ServerUnavailable,
                    $"The proxy fetch request could not be sent over the VMS messaging channel (the Event Server proxy component is down, or an Event Server restart disposed this session's messaging channel): {exception.Message}");
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy fetch request not sent: {exception.Message}");
            }

            return completion;
        }

        /// <summary>
        /// ADR-0003: send one loopback-leaf mint request over the shared channel and return the
        /// task the single response frame completes. The leaf exchange is far less frequent than
        /// fetches (one per origin per browser session), so it shares the fetcher's messaging
        /// session and JSON-string discipline rather than owning a separate connection.
        /// </summary>
        internal Task<ProxyLeafResponsePayload> RequestLeafAsync(string host, int port, byte[] subjectPublicKeyInfo, TimeSpan? deadline = null)
        {
            var correlation = _leafCorrelation;
            var communication = _communication;
            if (communication == null || correlation == null)
            {
                return Task.FromResult(new ProxyLeafResponsePayload
                {
                    Success = false,
                    ErrorCode = ProxyFetchErrorCodes.Error,
                    ErrorMessage = "The proxy channel is not connected to the Event Server.",
                });
            }

            var correlationId = Guid.NewGuid().ToString("N");
            var completion = correlation.Begin(correlationId, deadline ?? DefaultDeadline);

            try
            {
                communication.TransmitMessage(
                    new Message(
                        ProxyMessageIds.LeafRequestId,
                        ProxyFetchWireCodec.ToWire(new ProxyLeafRequestPayload
                        {
                            CorrelationId = correlationId,
                            Host = host,
                            Port = port,
                            SpkiBase64 = Convert.ToBase64String(subjectPublicKeyInfo),
                        })),
                    null,
                    null,
                    null);
            }
            catch (Exception exception)
            {
                correlation.Abandon(correlationId);
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy leaf request not sent: {exception.Message}");
            }

            return completion;
        }

        /// <summary>
        /// Ticket #57: open one websocket tunnel for a decided upgrade request. The Event Server
        /// connects to the target, replays the handshake, and answers with its verbatim 101; the
        /// returned handle then pumps raw tunnel bytes in both directions through messaging.
        /// Never throws — every failure is an outcome the forwarder renders as 502.
        /// </summary>
        internal async Task<ProxyTunnelOpenOutcome> OpenTunnelAsync(ProxyForwardedRequest request)
        {
            var communication = _communication;
            var tunnels = _tunnels;
            if (communication == null || tunnels == null)
            {
                return TunnelFailure(ProxyFetchErrorCodes.Error, "The proxy channel is not connected to the Event Server.");
            }

            var target = request == null ? null : request.Uri;
            if (target == null)
            {
                return TunnelFailure(ProxyFetchErrorCodes.BadRequest, "The websocket tunnel request carried no target uri.");
            }

            if (tunnels.Count >= _tunnelLimits.ClientMaxTunnels)
            {
                return TunnelFailure(
                    ProxyFetchErrorCodes.Error,
                    $"This Smart Client already has {_tunnelLimits.ClientMaxTunnels} websocket tunnels open; close a proxied page and retry.");
            }

            var tunnelId = Guid.NewGuid().ToString("N");
            tunnels.TryRegister(tunnelId);

            try
            {
                TransmitTunnel(ProxyMessageIds.TunnelOpenRequestId, new ProxyTunnelPayload
                {
                    CorrelationId = tunnelId,
                    Url = target.AbsoluteUri,
                    Headers = ProxyWebSocketHandshake.ToTunnelWireHeaders(request),
                });
            }
            catch (Exception exception)
            {
                tunnels.EndLocal(tunnelId);
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy tunnel open not sent: {exception.Message}");
                return TunnelFailure(ProxyFetchErrorCodes.Error, "The tunnel open request could not be sent to the Event Server.");
            }

            var openTask = tunnels.AwaitOpenAsync(tunnelId);
            var winner = await Task.WhenAny(openTask, Task.Delay(_tunnelLimits.ClientOpenDeadlineMs));
            if (winner != openTask)
            {
                // Half-open reclaim (ticket criterion 4): the Event Server may still be dialing the
                // target, so release it too rather than leaving a socket to the idle sweep.
                if (tunnels.EndLocal(tunnelId))
                {
                    TryTransmitTunnelClose(tunnelId, "the Smart Client stopped waiting for the websocket handshake");
                }

                return TunnelFailure(
                    ProxyFetchErrorCodes.Timeout,
                    $"The Event Server did not answer the websocket tunnel open within {_tunnelLimits.ClientOpenDeadlineMs / 1000} seconds.");
            }

            var outcome = openTask.Result;
            if (outcome == null)
            {
                return TunnelFailure(ProxyFetchErrorCodes.Error, "The websocket tunnel closed before it opened.");
            }

            if (outcome.Success)
            {
                outcome.Handle = new MessagingTunnelHandle(this, tunnelId);
            }

            return outcome;
        }

        /// <summary>Relay browser bytes toward the target; unknown/closed tunnels drop quietly.</summary>
        private void SendTunnelData(string tunnelId, byte[] bytes)
        {
            var tunnels = _tunnels;
            if (tunnels == null || tunnels.IsClosed(tunnelId))
            {
                return;
            }

            tunnels.Touch(tunnelId);
            var frame = new ProxyTunnelPayload
            {
                CorrelationId = tunnelId,
                Kind = ProxyTunnelFrameKinds.Data,
                DataBase64 = Convert.ToBase64String(bytes),
            };

            try
            {
                TransmitTunnel(ProxyMessageIds.TunnelDataToTargetId, frame);
            }
            catch (Exception exception)
            {
                // Messaging is dying: end the tunnel so both pumps unwind instead of spin-failing.
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy tunnel data not sent ({tunnelId}): {exception.Message}");
                tunnels.OnTargetClose(tunnelId, "the proxy messaging channel failed");
            }
        }

        /// <summary>The browser side ended the tunnel: drop the session and tell the Event Server.</summary>
        private void CloseTunnel(string tunnelId, string reason)
        {
            var tunnels = _tunnels;
            if (tunnels == null)
            {
                return;
            }

            if (tunnels.EndLocal(tunnelId))
            {
                TryTransmitTunnelClose(tunnelId, reason);
            }
        }

        private void TryTransmitTunnelClose(string tunnelId, string reason)
        {
            try
            {
                TransmitTunnel(ProxyMessageIds.TunnelCloseToTargetId, new ProxyTunnelPayload
                {
                    CorrelationId = tunnelId,
                    Kind = ProxyTunnelFrameKinds.Close,
                    ErrorMessage = reason,
                });
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy tunnel close not sent ({tunnelId}): {exception.Message}");
            }
        }

        private static void TransmitTunnelAt(MessageCommunication communication, string messageId, ProxyTunnelPayload payload)
        {
            // destinationEndPoint null routes to the Event Server fetcher plugin, exactly like the
            // fetch requests; the messaging layer stamps the sender endpoint for the unicast answer.
            communication.TransmitMessage(new Message(messageId, ProxyFetchWireCodec.ToWire(payload)), null, null, null);
        }

        private void TransmitTunnel(string messageId, ProxyTunnelPayload payload)
        {
            var communication = _communication;
            if (communication == null)
            {
                return;
            }

            TransmitTunnelAt(communication, messageId, payload);
        }

        private void SweepTunnels()
        {
            var tunnels = _tunnels;
            if (tunnels == null)
            {
                return;
            }

            try
            {
                var reclaimed = tunnels.ReclaimIdle(TimeSpan.FromMilliseconds(_tunnelLimits.IdleTimeoutMs));
                foreach (var tunnelId in reclaimed)
                {
                    TryTransmitTunnelClose(tunnelId, "the Smart Client reclaimed an idle websocket tunnel");
                }
            }
            catch (Exception exception)
            {
                EnvironmentManager.Instance.Log(true, nameof(ProxyFetchClient), $"Proxy tunnel sweep faulted: {exception.Message}");
            }
        }

        private object OnTunnelOpenResponse(Message message, FQID destination, FQID source)
        {
            var payload = ProxyFetchWireCodec.TunnelFromWire(message?.Data);
            var tunnels = _tunnels;
            if (payload == null || tunnels == null)
            {
                if (_trace && message != null)
                {
                    EnvironmentManager.Instance.Log(false, nameof(ProxyFetchClient), "Proxy tunnel open response not recognized.");
                }

                return null;
            }

            // A success ack for a session the client already gave up on (open deadline fired, or
            // the sweep reclaimed it mid-handshake) must still release the Event Server's fresh
            // socket: a chatty target keeps the ES-side idle sweep from ever firing.
            if (payload.Success && tunnels.IsClosed(payload.CorrelationId))
            {
                TryTransmitTunnelClose(payload.CorrelationId, "the Smart Client stopped waiting before the tunnel opened");
                return null;
            }

            tunnels.CompleteOpen(payload.CorrelationId, new ProxyTunnelOpenOutcome
            {
                Success = payload.Success,
                StatusCode = payload.StatusCode,
                HandshakeBytes = DecodeTunnelBytes(payload.DataBase64),
                ErrorCode = payload.ErrorCode,
                ErrorMessage = payload.ErrorMessage,
            });
            return null;
        }

        private object OnTunnelDataFromTarget(Message message, FQID destination, FQID source)
        {
            var payload = ProxyFetchWireCodec.TunnelFromWire(message?.Data);
            var tunnels = _tunnels;
            if (payload == null || tunnels == null)
            {
                return null;
            }

            var bytes = DecodeTunnelBytes(payload.DataBase64);
            if (bytes != null)
            {
                tunnels.OnTargetData(payload.CorrelationId, bytes);
            }
            else if (!string.IsNullOrEmpty(payload.DataBase64))
            {
                tunnels.OnTargetClose(payload.CorrelationId, "the tunnel data frame was malformed");
            }

            return null;
        }

        private object OnTunnelCloseFromTarget(Message message, FQID destination, FQID source)
        {
            var payload = ProxyFetchWireCodec.TunnelFromWire(message?.Data);
            var tunnels = _tunnels;
            if (payload != null && tunnels != null)
            {
                tunnels.OnTargetClose(payload.CorrelationId, payload.ErrorMessage ?? "the target closed the websocket");
            }

            return null;
        }

        private static byte[] DecodeTunnelBytes(string base64)
        {
            if (string.IsNullOrEmpty(base64))
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// The browser-connection seam over the messaging channel: reads drain the correlator's
        /// ordered queue, writes relay base64-wrapped raw bytes, and Close ends the session once
        /// and notifies the Event Server. The 16KB pump chunks keep every message inside the
        /// classic messaging size budget.
        /// </summary>
        private sealed class MessagingTunnelHandle : ProxyTunnelHandle
        {
            private readonly ProxyFetchClient _client;
            private int _closeSent;

            internal MessagingTunnelHandle(ProxyFetchClient client, string tunnelId)
            {
                _client = client;
                TunnelId = tunnelId;
            }

            internal override bool IsClosed => _client._tunnels == null || _client._tunnels.IsClosed(TunnelId);

            internal override Task SendAsync(byte[] bytes)
            {
                _client.SendTunnelData(TunnelId, bytes);
                return Task.CompletedTask;
            }

            internal override Task<byte[]> NextDataAsync()
            {
                var tunnels = _client._tunnels;
                return tunnels == null ? Task.FromResult<byte[]>(null) : tunnels.NextDataAsync(TunnelId);
            }

            internal override void Close(string reason)
            {
                if (Interlocked.Exchange(ref _closeSent, 1) == 0)
                {
                    _client.CloseTunnel(TunnelId, reason);
                }
            }
        }

        private static ProxyTunnelOpenOutcome TunnelFailure(string errorCode, string message)
        {
            return new ProxyTunnelOpenOutcome
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = message,
            };
        }

        private object OnProxyLeafResponse(Message message, FQID destination, FQID source)
        {
            var response = ProxyFetchWireCodec.LeafResponseFromWire(message?.Data);
            if (response != null)
            {
                var correlation = _leafCorrelation;
                if (correlation != null && !correlation.Feed(response) && _trace)
                {
                    EnvironmentManager.Instance.Log(
                        false,
                        nameof(ProxyFetchClient),
                        $"Leaf response unmatched: correlationId='{response.CorrelationId}'.");
                }

                return null;
            }

            if (_trace && message != null)
            {
                var actual = message.Data == null ? "null Data" : message.Data.GetType().FullName;
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyFetchClient),
                    $"Leaf response not recognized: {actual} destination={destination} source={source}");
            }

            return null;
        }

        private object OnProxyFetchResponse(Message message, FQID destination, FQID source)
        {
            var frame = ProxyFetchWireCodec.FrameFromWire(message?.Data);
            if (frame != null)
            {
                var correlation = _correlation;
                if (correlation != null && !correlation.Feed(frame) && _trace)
                {
                    EnvironmentManager.Instance.Log(
                        false,
                        nameof(ProxyFetchClient),
                        $"Proxy fetch response frame unmatched: kind={frame.Kind} correlationId='{frame.CorrelationId}'.");
                }

                return null;
            }

            // A FetchResponse message we cannot decode is the classic silent black hole. With
            // tracing on, name what actually arrived (null Data = the platform failed to bind the
            // wire type; a non-string type = foreign or pre-codec-format sender).
            if (_trace && message != null)
            {
                var actual = message.Data == null ? "null Data" : message.Data.GetType().FullName;
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyFetchClient),
                    $"Proxy fetch response frame not recognized: {actual} destination={destination} source={source}");
            }

            return null;
        }

        private object OnFetchRequestEcho(Message message, FQID destination, FQID source)
        {
            if (_trace)
            {
                var request = ProxyFetchWireCodec.RequestFromWire(message?.Data);
                EnvironmentManager.Instance.Log(
                    false,
                    nameof(ProxyFetchClient),
                    "Proxy fetch request echo: " +
                    (message?.Data == null
                        ? "null Data"
                        : request != null
                            ? "payload intact, id=" + request.CorrelationId
                            : "undecodable content of type " + message.Data.GetType().FullName) +
                    $" destination={destination} source={source}");
            }

            return null;
        }

        private static ProxyFetchResult Failure(string errorCode, string message)
        {
            return new ProxyFetchResult
            {
                Success = false,
                ErrorCode = errorCode,
                ErrorMessage = message,
            };
        }
    }
}
