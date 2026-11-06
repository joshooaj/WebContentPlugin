using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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
        public void UntrustedMachinesRoundTripAndAMissingMemberDecodesAsNull()
        {
            // #80 member: entries round trip, and the SKEW direction is pinned the same way #65
            // pinned the request wire - a frame that predates the member (no UntrustedMachines
            // key at all) still decodes, with the list null, so a mismatched pair degrades to
            // "no untrusted machines reported" instead of dropping the whole status response.
            var seen = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var wire = ProxyFetchWireCodec.ToWire(new ProxyStatusResponsePayload
            {
                CorrelationId = "c-1",
                Success = true,
                WorkspaceItemId = "aaaaaaaa-0000-0000-0000-000000000001",
                Sessions = new List<ProxySessionStatusEntry>(),
                Blocked = new List<ProxyBlockEntry>(),
                UntrustedMachines = new List<ProxyCaUntrustedMachineEntry>
                {
                    new ProxyCaUntrustedMachineEntry
                    {
                        Machine = "BEAST",
                        Count = 4,
                        LastSeenUtc = seen,
                    },
                },
            });

            var back = ProxyFetchWireCodec.StatusResponseFromWire(wire);
            Assert.NotNull(back);
            Assert.Single(back.UntrustedMachines);
            Assert.Equal("BEAST", back.UntrustedMachines[0].Machine);
            Assert.Equal(4, back.UntrustedMachines[0].Count);
            Assert.Equal(seen, back.UntrustedMachines[0].LastSeenUtc);

            // The serializer orders members alphabetically; the skew frame is what an older
            // Event Server would actually send: the member absent, the JSON around it intact.
            var withoutMember = Regex.Replace(
                wire,
                @",""UntrustedMachines"":\[.*?\]",
                string.Empty,
                RegexOptions.Singleline);
            Assert.DoesNotContain("UntrustedMachines", withoutMember);

            var old = ProxyFetchWireCodec.StatusResponseFromWire(withoutMember);
            Assert.True(old != null, "withoutMember=" + withoutMember);
            Assert.Null(old.UntrustedMachines);
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
        public void AFrameFromAnOlderBuildDecodesAsUnattributedRatherThanDying()
        {
            // Skew contract for the request wire, revised by #65: Smart Clients on workstations
            // can lag the Event Server, and a member added later (SessionId in #69,
            // WorkspaceItemId in #65) is OPTIONAL on read. An older client's frame decodes as an
            // unattributed fetch - null attribution, which downstream already treats as "no
            // session/no workspace", never a refusal - instead of every fetch from that client
            // dying on a strict read. Frames that are not fetch requests at all (no url, no
            // decodable shape) stay unusable nulls. The tunnel wire deliberately keeps the
            // strict inferred contract: a tunnel is negotiated inside one live browser session
            // on one matching pair of plugin builds, so no drift case exists to tolerate.
            var wire = ProxyFetchWireCodec.ToWire(new ProxyFetchRequestPayload
            {
                CorrelationId = "c-1",
                Url = "http://one.test:80/",
                Method = "GET",
            });
            var preDiagnostics = wire.Replace("\"SessionId\":null,", string.Empty);
            Assert.DoesNotContain("SessionId", preDiagnostics);

            var old = ProxyFetchWireCodec.RequestFromWire(preDiagnostics);
            Assert.NotNull(old);
            Assert.Equal("http://one.test:80/", old.Url);
            Assert.Null(old.SessionId);
            Assert.Null(old.WorkspaceItemId);

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
