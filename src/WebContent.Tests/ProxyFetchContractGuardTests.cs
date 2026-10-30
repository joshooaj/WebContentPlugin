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
