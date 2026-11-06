using System.IO;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #54 live-host diagnosis: the fetcher must answer each proxy-fetch request on the
    /// requester's endpoint. The MIP messaging layer stamps every received message with the
    /// sender's endpoint FQID in Message.ExternalMessageSourceEndPoint; the receive-callback
    /// source parameter only carries an explicitly passed identity, which ProxyFetchClient does
    /// not pass. Answering to a null destination instead filter-routes the frames to every client
    /// subscribed to the response message id - refusals vanish (clients time out) and fetched
    /// page bodies leak to unrelated hosts. These source guards pin the reply path; the live
    /// proof stays MC-10/ES-1/SC-1 in docs/verification-matrix.md.
    /// </summary>
    public class ProxyFetchReplyGuardTests
    {
        private static string ReadFetcher()
        {
            return File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "ProxyFetchBackgroundPlugin.cs"));
        }

        [Fact]
        public void RequesterResolutionFallsBackToThePlatformFilledSourceEndPoint()
        {
            var fetcher = ReadFetcher();

            Assert.Contains("source ?? message?.ExternalMessageSourceEndPoint", fetcher);
        }

        [Fact]
        public void EveryReplyTargetsTheResolvedRequesterEndpoint()
        {
            var fetcher = ReadFetcher();

            // The handler hands the resolved requester (never the raw callback source) to both
            // reply paths.
            Assert.Contains("TransmitFailure(payload.CorrelationId, requester,", fetcher);
            Assert.DoesNotContain("TransmitFailure(payload.CorrelationId, source,", fetcher);
            Assert.Contains("ServeFetchAsync(communication, payload, query, requester, tlsMode)", fetcher);
        }

        [Fact]
        public void ResponseTransmitsNeverFallBackToNullDestinationBroadcast()
        {
            var fetcher = ReadFetcher();

            // Success frames: unicast to the requester endpoint, and the frame loop is guarded so
            // a request without a requester endpoint is logged, not broadcast.
            Assert.Contains("if (requesterFqid == null)", fetcher);
            Assert.Contains("ProxyMessageIds.FetchResponseId, ProxyFetchWireCodec.ToWire(frame)), requesterFqid, null, null", fetcher);

            // Failure frames: unicast to the requester as well (message, requester, null, null).
            var collapsed = System.Text.RegularExpressions.Regex.Replace(fetcher, @"\s+", " ");
            Assert.Contains("ErrorMessage = message, })), requester, null, null);", collapsed);
        }

        [Fact]
        public void LeafMintAnswersAreUnicastToTheRequesterNeverBroadcast()
        {
            // ADR-0003: the minted certificate and the CA ride the same unicast discipline as
            // fetch frames - a null-destination leaf response would deliver signing material to
            // every host subscribed to the leaf response id.
            var fetcher = ReadFetcher();
            var collapsed = System.Text.RegularExpressions.Regex.Replace(fetcher, @"\s+", " ");

            Assert.Contains("ProxyFetchWireCodec.LeafRequestFromWire(message?.Data)", fetcher);
            Assert.Contains("new CommunicationIdFilter(ProxyMessageIds.LeafRequestId)", fetcher);
            Assert.Contains("if (requester == null)", fetcher);
            Assert.Contains("ProxyMessageIds.LeafResponseId, ProxyFetchWireCodec.ToWire(response)), requester, null, null);", collapsed);
        }

        [Fact]
        public void BothSidesCarryPayloadsAsJsonStringsNeverAsPluginTypes()
        {
            // The platform rehydrates Data via Type.GetType(DataClass) and silently delivers
            // null when the host cannot resolve the plugin type (Smart Client). Every transmit
            // therefore goes through the wire codec, and every receive decodes from it.
            var fetcher = ReadFetcher();
            var client = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "ProxyFetchClient.cs"));

            Assert.Contains("ProxyFetchWireCodec.RequestFromWire(message?.Data)", fetcher);
            Assert.Contains("ProxyFetchWireCodec.ToWire(new ProxyFetchResponsePayload", fetcher);
            Assert.DoesNotContain("message?.Data as ProxyFetchRequestPayload", fetcher);

            Assert.Contains("ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload", client);
            Assert.Contains("ProxyFetchWireCodec.FrameFromWire(message?.Data)", client);
            Assert.DoesNotContain("message?.Data as ProxyFetchResponsePayload", client);
        }

        [Fact]
        public void RequestSideAssemblesMethodHeadersAndBodyOntoTheWirePayload()
        {
            // The codec round-trip starts AFTER this assembly step, so nothing else pins that the
            // browser's verb, headers, and body actually ride the transmitted payload (#55).
            // Lose one assignment and the POST silently degrades to a body-less GET on the wire.
            var client = File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "ProxyFetchClient.cs"));

            var collapsed = System.Text.RegularExpressions.Regex.Replace(client, @"\s+", " ");
            Assert.Contains("Method = string.IsNullOrWhiteSpace(query.Method) ? null : query.Method.Trim().ToUpperInvariant(),", collapsed);
            Assert.Contains("Headers = query.Headers != null && query.Headers.Count > 0 ? query.Headers : null,", collapsed);
            Assert.Contains("BodyBase64 = query.Body != null && query.Body.Length > 0 ? Convert.ToBase64String(query.Body) : null,", collapsed);
        }

        [Fact]
        public void FetcherReChecksMethodBase64AndBodyCapOnDecodedPayloads()
        {
            // Defense-in-depth on the Event Server: the method table, the base64 shape, and the
            // request-body cap are re-checked against what the wire actually carried, never
            // trusted from the requester (#55).
            var fetcher = ReadFetcher();

            Assert.Contains("ProxyForwardPolicy.IsForwardedMethod(method)", fetcher);
            Assert.Contains("Convert.FromBase64String(payload.BodyBase64)", fetcher);
            Assert.Contains("body.Length > ProxyForwardConnection.MaxRequestBodyBytes", fetcher);
        }
    }
}
