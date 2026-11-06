using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #53: the Event Server discards messages whose Data object sits outside the payload
    /// whitelist - only listed System types plus types from the plug-in's own namespace are
    /// allowed. These guards pin that every proxy-fetch payload field and every container
    /// element stays inside that whitelist, and that the proxy message ids are shaped so a
    /// receiver's default security level (which ignores ids ending in "Command") can accept them.
    /// </summary>
    public class ProxyFetchContractGuardTests
    {
        private static readonly Type[] WhitelistedValueTypePrimitives =
        {
            typeof(string), typeof(short), typeof(int), typeof(long),
            typeof(float), typeof(double), typeof(char), typeof(bool),
            typeof(DateTime), typeof(TimeSpan), typeof(Guid),
        };

        [Theory]
        [InlineData(typeof(ProxyFetchRequestPayload))]
        [InlineData(typeof(ProxyFetchResponsePayload))]
        [InlineData(typeof(ProxyFetchHeader))]
        [InlineData(typeof(ProxyLeafRequestPayload))]
        [InlineData(typeof(ProxyLeafResponsePayload))]
        [InlineData(typeof(ProxyTunnelPayload))]
        [InlineData(typeof(ProxyBlockReportPayload))]
        [InlineData(typeof(ProxySessionEventPayload))]
        [InlineData(typeof(ProxyStatusRequestPayload))]
        [InlineData(typeof(ProxyStatusResponsePayload))]
        [InlineData(typeof(ProxySessionStatusEntry))]
        [InlineData(typeof(ProxyBlockEntry))]
        [InlineData(typeof(ProxyCaUntrustedMachineEntry))]
        [InlineData(typeof(ProxyCaExportRequestPayload))]
        [InlineData(typeof(ProxyCaExportResponsePayload))]
        public void EveryPayloadFieldStaysInsideTheMessageWhitelist(Type payloadType)
        {
            foreach (var field in payloadType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.True(IsWhitelistSafe(field.FieldType, payloadType.Assembly),
                    $"{payloadType.Name}.{field.Name} has non-whitelisted type {field.FieldType.Name}.");
            }
        }

        [Theory]
        [InlineData(typeof(ProxyFetchRequestPayload))]
        [InlineData(typeof(ProxyFetchResponsePayload))]
        [InlineData(typeof(ProxyFetchHeader))]
        [InlineData(typeof(ProxyLeafRequestPayload))]
        [InlineData(typeof(ProxyLeafResponsePayload))]
        [InlineData(typeof(ProxyTunnelPayload))]
        [InlineData(typeof(ProxyBlockReportPayload))]
        [InlineData(typeof(ProxySessionEventPayload))]
        [InlineData(typeof(ProxyStatusRequestPayload))]
        [InlineData(typeof(ProxyStatusResponsePayload))]
        public void PayloadsLiveInThePluginNamespaceTheWhitelistGrants(Type payloadType)
        {
            // The whitelist escape for plug-in types is namespace-based; a payload living under a
            // different namespace would need the MessageCommunicationSettings.config hatch.
            Assert.Equal("WebContent", payloadType.Namespace);
        }

        [Fact]
        public void ProxyMessageIdsDoNotEndWithTheIgnoredCommandSuffix()
        {
            // Default SecurityLevel.Normal ignores incoming ids ending in "Command"; the proxy
            // ids must be receivable at Normal.
            Assert.EndsWith("Request", ProxyMessageIds.FetchRequestId, StringComparison.Ordinal);
            Assert.EndsWith("Response", ProxyMessageIds.FetchResponseId, StringComparison.Ordinal);
            Assert.EndsWith("Request", ProxyMessageIds.LeafRequestId, StringComparison.Ordinal);
            Assert.EndsWith("Response", ProxyMessageIds.LeafResponseId, StringComparison.Ordinal);

            // #69 diagnostics ids: one-way report/event plus the request/response pair - none may
            // carry the "Command" suffix a Normal-security receiver silently ignores.
            Assert.Equal("WebContent.Proxy.BlockReport", ProxyMessageIds.BlockReportId);
            Assert.Equal("WebContent.Proxy.SessionEvent", ProxyMessageIds.SessionEventId);
            Assert.EndsWith("Request", ProxyMessageIds.StatusRequestId, StringComparison.Ordinal);
            Assert.EndsWith("Response", ProxyMessageIds.StatusResponseId, StringComparison.Ordinal);

            // #75 certificate export pair: same shape rule, and the ids are declared on the
            // plugin definition so tooling sees them as plugin-owned traffic.
            Assert.EndsWith("Request", ProxyMessageIds.CaExportRequestId, StringComparison.Ordinal);
            Assert.EndsWith("Response", ProxyMessageIds.CaExportResponseId, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("CaExportRequestId")]
        [InlineData("CaExportResponseId")]
        public void TheCertificateExportIdsAreDeclaredOnThePluginDefinition(string fieldName)
        {
            var definition = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "WebContentPluginDefinition.cs"));
            Assert.Contains($"ProxyMessageIds.{fieldName},", definition);
        }

        private static bool IsWhitelistSafe(Type type, Assembly pluginAssembly)
        {
            if (WhitelistedValueTypePrimitives.Contains(type))
            {
                return true;
            }

            // Plugin-namespace types are the sanctioned payload escape hatch.
            if (type.Assembly == pluginAssembly && type.Namespace == "WebContent")
            {
                return true;
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(List<>) || definition == typeof(Collection<>) || definition == typeof(KeyValuePair<,>))
                {
                    return type.GetGenericArguments().All(argument => IsWhitelistSafe(argument, pluginAssembly));
                }
            }

            return false;
        }
    }
}
