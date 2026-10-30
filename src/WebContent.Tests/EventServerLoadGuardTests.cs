using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #53: the same assembly loads into the XProtect Event Server to host the proxy-fetch
    /// background plugin. Loading is governed by plugin.def's <load env>; the fetcher activates
    /// only for the Service environment; Smart Client view/WebView2 code paths stay in their own
    /// case so the Event Server never touches them. These source guards pin the load contract;
    /// the live proof is ES-1 in docs/verification-matrix.md.
    /// </summary>
    public class EventServerLoadGuardTests
    {
        [Fact]
        public void PluginDefDeclaresTheServiceEnvironmentForEventServerLoading()
        {
            var pluginDef = File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "plugin.def"));

            Assert.Matches(@"<load\s+env\s*=\s*\u0022[^\u0022]*Service[^\u0022]*\u0022", pluginDef);
        }

        [Fact]
        public void DefinitionActivatesTheProxyBackgroundPluginForTheServiceEnvironment()
        {
            var definition = ReadDefinition();

            var serviceCase = Regex.Match(
                definition,
                @"case EnvironmentType\.Service:\s*\r?\n(.*?)(?=case |default:|break;\s*\r?\n\s*default)",
                RegexOptions.Singleline);
            Assert.True(serviceCase.Success, "WebContentPluginDefinition.Init has no Service case.");
            Assert.Matches("ProxyFetchBackgroundPlugin", serviceCase.Groups[1].Value);
        }

        [Fact]
        public void DefinitionOverridesBackgroundPluginsAndRegistersTheProxyMessageIds()
        {
            var definition = ReadDefinition();

            Assert.Matches(@"override\s+List<BackgroundPlugin>\s+BackgroundPlugins", definition);
            Assert.Matches(@"override\s+List<string>\s+PluginDefinedMessageIds", definition);
            Assert.Matches("FetchRequestId", definition);
            Assert.Matches("FetchResponseId", definition);
        }

        [Fact]
        public void SmartClientViewCodeStaysGuardedToTheSmartClientCase()
        {
            var definition = ReadDefinition();

            // ViewItemPlugins population may only happen inside the SmartClient case; the Event
            // Server path must never reach the WebView2 view stack.
            var smartClientCase = Regex.Match(
                definition,
                @"case EnvironmentType\.SmartClient:(.*?)(?=case EnvironmentType\.Standalone)",
                RegexOptions.Singleline);
            Assert.True(smartClientCase.Success);
            Assert.Matches(@"ViewItemPlugins\.Add\(new WebContentViewItemPlugin\(\)\)", smartClientCase.Groups[1].Value);

            var outsideSmartClientCase = definition.Replace(smartClientCase.Value, string.Empty);
            Assert.DoesNotMatch(@"ViewItemPlugins\.Add\(new WebContentViewItemPlugin\(\)\)", outsideSmartClientCase);
        }

        private static string ReadDefinition()
        {
            return File.ReadAllText(Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "WebContentPluginDefinition.cs"));
        }
    }
}
