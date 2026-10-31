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
            Assert.Contains("ServeFetchAsync(communication, payload, target, requester)", fetcher);
        }

        [Fact]
        public void ResponseTransmitsNeverFallBackToNullDestinationBroadcast()
        {
            var fetcher = ReadFetcher();

            // Success frames: unicast to the requester endpoint, and the frame loop is guarded so
            // a request without a requester endpoint is logged, not broadcast.
            Assert.Contains("if (requesterFqid == null)", fetcher);
            Assert.Contains("ProxyMessageIds.FetchResponseId, frame), requesterFqid, null, null", fetcher);

            // Failure frames: unicast to the requester as well (message, requester, null, null).
            var collapsed = System.Text.RegularExpressions.Regex.Replace(fetcher, @"\s+", " ");
            Assert.Contains("ErrorMessage = message, }), requester, null, null);", collapsed);
        }
    }
}
