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
        private SemaphoreSlim _fetchSlots;
        private ProxyServerAllowlist _allowlist;

        public override Guid Id => PluginId;

        public override string Name => "Web Content proxy fetcher";

        public override List<EnvironmentType> TargetEnvironments { get; } = new List<EnvironmentType> { EnvironmentType.Service };

        public override void Init()
        {
            _fetchSlots = new SemaphoreSlim(MaxConcurrentFetches, MaxConcurrentFetches);

            try
            {
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

            _requestFilterRegistration = null;
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

            var payload = message?.Data as ProxyFetchRequestPayload;
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
                    await ServeFetchAsync(communication, payload, target, requester);
                }
                finally
                {
                    slots.Release();
                }
            });

            return null;
        }

        private async Task ServeFetchAsync(MessageCommunication communication, ProxyFetchRequestPayload payload, Uri target, FQID requesterFqid)
        {
            try
            {
                var limits = ProxyFetchLimits.Default();
                var result = await new ProxyHttpFetcher().FetchAsync(target, limits);

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
                    communication.TransmitMessage(new Message(ProxyMessageIds.FetchResponseId, frame), requesterFqid, null, null);
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
                    new ProxyFetchResponsePayload
                    {
                        CorrelationId = correlationId,
                        Kind = ProxyFetchFrameKinds.Failure,
                        Sequence = -1,
                        ErrorCode = errorCode,
                        ErrorMessage = message,
                    }),
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
