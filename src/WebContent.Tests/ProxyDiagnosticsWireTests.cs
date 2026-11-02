using System;
using System.Collections.Generic;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #69: the diagnostics payloads ride the same JSON-string-on-the-wire discipline as
    /// every other proxy message (#54). These guards pin the round trips and the strictness that
    /// comes with them: an unset session is an explicit null member, and a frame whose shape came
    /// from a different build is refused (null) rather than half-bound - so both plugin halves
    /// always ship from the same drop.
    /// </summary>
    public class ProxyDiagnosticsWireTests
    {
        [Fact]
        public void BlockReportRoundTripsEveryField()
        {
            var wire = ProxyFetchWireCodec.ToWire(new ProxyBlockReportPayload
            {
                WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001",
                WorkspaceFingerprint = "fp-9",
                Origin = "https://vendor.test:8443",
                Reason = ProxyBlockReasons.NotAllowed,
                Count = 3,
                ClientMachine = "BEAST",
            });

            var back = ProxyFetchWireCodec.BlockReportFromWire(wire);
            Assert.NotNull(back);
            Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", back.WorkspaceItemId);
            Assert.Equal("fp-9", back.WorkspaceFingerprint);
            Assert.Equal("https://vendor.test:8443", back.Origin);
            Assert.Equal(ProxyBlockReasons.NotAllowed, back.Reason);
            Assert.Equal(3, back.Count);
            Assert.Equal("BEAST", back.ClientMachine);
        }

        [Fact]
        public void SessionEventRoundTripsAndGarbageDecodesToNull()
        {
            var wire = ProxyFetchWireCodec.ToWire(new ProxySessionEventPayload
            {
                SessionId = "s-1",
                Kind = ProxySessionEventKinds.Open,
                WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001",
                ClientMachine = "BEAST",
            });

            var back = ProxyFetchWireCodec.SessionEventFromWire(wire);
            Assert.NotNull(back);
            Assert.Equal("s-1", back.SessionId);
            Assert.Equal(ProxySessionEventKinds.Open, back.Kind);
            Assert.Equal("BEAST", back.ClientMachine);

            Assert.Null(ProxyFetchWireCodec.SessionEventFromWire(42));
            Assert.Null(ProxyFetchWireCodec.SessionEventFromWire("not json at all"));
            Assert.Null(ProxyFetchWireCodec.BlockReportFromWire(null));
        }

        [Fact]
        public void StatusResponseRoundTripsSessionsAndBlockedList()
        {
            var opened = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var wire = ProxyFetchWireCodec.ToWire(new ProxyStatusResponsePayload
            {
                CorrelationId = "c-1",
                Success = true,
                WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001",
                Sessions = new System.Collections.Generic.List<ProxySessionStatusEntry>
                {
                    new ProxySessionStatusEntry
                    {
                        SessionId = "s-1",
                        UserName = "alice",
                        ClientMachine = "BEAST",
                        OpenedUtc = opened,
                        LastActivityUtc = opened.AddMinutes(2),
                        Requests = 7,
                        RequestsPerMinute = 3.5,
                        BytesSent = 120,
                        BytesReceived = 45000,
                    },
                },
                Blocked = new System.Collections.Generic.List<ProxyBlockEntry>
                {
                    new ProxyBlockEntry
                    {
                        Origin = "http://gate.test:80",
                        Reason = ProxyBlockReasons.TunnelRefused,
                        Count = 2,
                        LastSeenUtc = opened.AddMinutes(1),
                    },
                },
            });

            var back = ProxyFetchWireCodec.StatusResponseFromWire(wire);
            Assert.NotNull(back);
            Assert.True(back.Success);
            Assert.Single(back.Sessions);
            Assert.Equal("alice", back.Sessions[0].UserName);
            Assert.Equal(7, back.Sessions[0].Requests);
            Assert.Equal(3.5, back.Sessions[0].RequestsPerMinute);
            Assert.Equal(45000, back.Sessions[0].BytesReceived);
            Assert.Single(back.Blocked);
            Assert.Equal(ProxyBlockReasons.TunnelRefused, back.Blocked[0].Reason);
            Assert.Equal(opened.AddMinutes(1), back.Blocked[0].LastSeenUtc);
        }

        [Fact]
        public void StatusRequestRoundTrips()
        {
            var wire = ProxyFetchWireCodec.ToWire(new ProxyStatusRequestPayload
            {
                CorrelationId = "c-9",
                WorkspaceItemId = "bbbbbbbb-0000-0000-0000-000000000002",
            });

            var back = ProxyFetchWireCodec.StatusRequestFromWire(wire);
            Assert.NotNull(back);
            Assert.Equal("c-9", back.CorrelationId);
            Assert.Equal("bbbbbbbb-0000-0000-0000-000000000002", back.WorkspaceItemId);
        }

        [Fact]
        public void UnsetSessionIdTravelsAsAnExplicitNullMember()
        {
            // DataContractJsonSerializer treats every implicit-contract member as REQUIRED, so an
            // unset session rides as an explicit "SessionId":null and decodes back to null - the
            // shape both ends of a matching drop always see.
            var wire = ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload
            {
                CorrelationId = "c-1",
                Url = "http://one.test:80/",
                Method = "GET",
            });
            Assert.Contains("\"SessionId\":null", wire);

            var back = ProxyFetchWireCodec.RequestFromWire(wire);
            Assert.NotNull(back);
            Assert.Null(back.SessionId);
        }

        [Fact]
        public void AFrameFromAnOlderBuildIsRejectedRatherThanHalfBound()
        {
            // The wire is strict in both directions: a payload shape that predates #69 has no
            // SessionId member to write, and this build answers that frame with null (an unusable
            // frame), never a half-bound payload. That is why the Smart Client plugin and the
            // Event Server plugin ship from one drop (psake Deploy), and why an unattributed
            // session is expressed as null - not as a missing member.
            var wire = ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload
            {
                CorrelationId = "c-1",
                Url = "http://one.test:80/",
                Method = "GET",
            });
            var preDiagnostics = wire.Replace("\"SessionId\":null,", string.Empty);
            Assert.DoesNotContain("SessionId", preDiagnostics);
            Assert.Null(ProxyFetchWireCodec.RequestFromWire(preDiagnostics));

            var tunnelWire = ProxyFetchWireCodec.ToWire(new ProxyTunnelPayload
            {
                CorrelationId = "t-1",
                Url = "http://one.test:80/ws",
                Headers = new List<ProxyFetchHeader> { new ProxyFetchHeader { Name = "Sec-WebSocket-Key", Value = "k" } },
            });
            var preTunnel = tunnelWire.Replace("\"SessionId\":null,", string.Empty);
            Assert.DoesNotContain("SessionId", preTunnel);
            Assert.Null(ProxyFetchWireCodec.TunnelFromWire(preTunnel));

            var intact = ProxyFetchWireCodec.TunnelFromWire(tunnelWire);
            Assert.NotNull(intact);
            Assert.Equal("t-1", intact.CorrelationId);
            Assert.Null(intact.SessionId);
            Assert.Single(intact.Headers);
        }
    }
}
