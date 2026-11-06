using System;
using System.Collections.Generic;

using WebContent.ProxyCa;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #77: decision table for the MSI deployment-CA custom action's core
    /// (src/WebContentProxyCa, linked here exactly like the #82 ServiceCtlCore). The
    /// fakes stand in for the SCM reader, the plugin load-or-create gateway, and the
    /// private-key grant - the real implementations live in Program.cs (they touch the
    /// registry, the installed plugin assembly, and MachineKeys ACLs) and are exercised
    /// live on the Event Server machine (verification-matrix IN-3).
    /// </summary>
    public class ProxyCaProvisionCoreTests
    {
        private sealed class RecordingLog
        {
            public List<string> Lines { get; } = new List<string>();
            public Action<string> Act => Lines.Add;
        }

        private sealed class FakeAccounts : IServiceAccountReader
        {
            public FakeAccounts(string objectName)
            {
                ObjectName = objectName;
            }

            public string ObjectName { get; }
            public string SeenServiceName { get; private set; }

            public string ReadObjectName(string serviceName)
            {
                SeenServiceName = serviceName;
                return ObjectName;
            }
        }

        private sealed class FakeGateway : IAuthorityGateway
        {
            public Exception Throw { get; set; }
            public string SeenPluginPath { get; private set; }
            public int Calls { get; private set; }

            public void Provision(string pluginDllPath, Action<string> log)
            {
                Calls++;
                SeenPluginPath = pluginDllPath;
                if (Throw != null)
                {
                    throw Throw;
                }
            }
        }

        private sealed class FakeGrant : IPrivateKeyAccessGrant
        {
            public bool Result { get; set; } = true;
            public string SeenAccount { get; private set; }
            public int Calls { get; private set; }

            public bool EnsureServiceAccountCanReadKey(string account, Action<string> log)
            {
                Calls++;
                SeenAccount = account;
                return Result;
            }
        }

        // ---------------------------------------------------------------- parsing

        public static IEnumerable<object[]> BadArgumentVectors()
        {
            yield return new object[] { null };                                     // no args at all
            yield return new object[] { new string[0] };                            // empty vector
            yield return new object[] { new[] { "provision" } };                    // no service name
            yield return new object[] { new[] { "provision", "svc", "" } };         // blank override
            yield return new object[] { new[] { "provision", "svc", "a", "b" } };   // too many
            yield return new object[] { new[] { "start", "svc" } };                 // unknown verb
            yield return new object[] { new[] { "", "svc" } };                      // blank verb
            yield return new object[] { new[] { "provision", "" } };                // blank service
            yield return new object[] { new[] { "provision", "..\\Other" } };       // path chars cannot escape the registry subkey
            yield return new object[] { new[] { "provision", "a=b" } };             // name/value separator
        }

        [Theory]
        [MemberData(nameof(BadArgumentVectors))]
        public void RefusesBadArgumentVectorsBeforeTouchingAnything(string[] args)
        {
            var gateway = new FakeGateway();
            var grant = new FakeGrant();
            var exit = ProxyCaCore.Run(args, new FakeAccounts("NT AUTHORITY\\NetworkService"), gateway, grant, new RecordingLog().Act);
            Assert.Equal(ProxyCaCore.ExitUsage, exit);
            Assert.Equal(0, gateway.Calls);
            Assert.Equal(0, grant.Calls);
        }

        [Fact]
        public void ProvisionVerbIsCaseInsensitiveAndDefaultsThePluginPath()
        {
            string serviceName;
            string pluginDllPath;
            Assert.True(ProxyCaCore.TryParse(new[] { "Provision", "MilestoneEventServerService" }, out serviceName, out pluginDllPath));
            Assert.Equal("MilestoneEventServerService", serviceName);
            Assert.Equal(ProxyCaCore.DefaultPluginDllPath, pluginDllPath);
        }

        [Fact]
        public void OverridePluginPathFlowsThroughForManualRuns()
        {
            string serviceName;
            string pluginDllPath;
            Assert.True(ProxyCaCore.TryParse(new[] { "provision", "svc", @"D:\scratch\WebContent.dll" }, out serviceName, out pluginDllPath));
            Assert.Equal(@"D:\scratch\WebContent.dll", pluginDllPath);
        }

        // ---------------------------------------------------------------- account mapping

        [Theory]
        [InlineData(null, @"NT AUTHORITY\SYSTEM")]                    // no ObjectName value: the SCM default
        [InlineData("", @"NT AUTHORITY\SYSTEM")]
        [InlineData("LocalSystem", @"NT AUTHORITY\SYSTEM")]
        [InlineData("NT AUTHORITY\\NetworkService", @"NT AUTHORITY\NetworkService")]
        [InlineData("NetworkService", @"NT AUTHORITY\NetworkService")] // case-insensitive suffix normalization
        [InlineData("nt authority\\networkservice", @"NT AUTHORITY\NetworkService")]
        [InlineData("NT AUTHORITY\\LocalService", @"NT AUTHORITY\LocalService")]
        [InlineData(".\\farmsvc", ".\\farmsvc")]                      // anything else flows through verbatim
        public void ServiceAccountMappingCoversTheScmObjectNamespaces(string objectName, string expected)
        {
            Assert.Equal(expected, ProxyCaCore.MapServiceAccount(objectName));
        }

        // ---------------------------------------------------------------- the run itself

        [Fact]
        public void HappyPathFlowsServiceAccountIntoTheGrantAndExitsDone()
        {
            var accounts = new FakeAccounts("NT AUTHORITY\\NetworkService");
            var gateway = new FakeGateway();
            var grant = new FakeGrant();
            var log = new RecordingLog();

            var exit = ProxyCaCore.Run(new[] { "provision", "MilestoneEventServerService" }, accounts, gateway, grant, log.Act);

            Assert.Equal(ProxyCaCore.ExitDone, exit);
            Assert.Equal("MilestoneEventServerService", accounts.SeenServiceName);
            Assert.Equal(ProxyCaCore.DefaultPluginDllPath, gateway.SeenPluginPath);
            Assert.Equal(@"NT AUTHORITY\NetworkService", grant.SeenAccount);
            Assert.Contains(log.Lines, line => line.Contains("NT AUTHORITY\\NetworkService"));
            Assert.Contains(log.Lines, line => line.Contains(ProxyCaCore.AuthorityCommonName));
        }

        [Fact]
        public void GatewayFailureSkipsTheGrantAndFailsTheInstall()
        {
            var gateway = new FakeGateway { Throw = new InvalidOperationException("no CA and this account cannot create one") };
            var grant = new FakeGrant();
            var log = new RecordingLog();

            var exit = ProxyCaCore.Run(new[] { "provision", "svc" }, new FakeAccounts("LocalSystem"), gateway, grant, log.Act);

            Assert.Equal(ProxyCaCore.ExitProvisionFailed, exit);
            Assert.Equal(0, grant.Calls); // no grant against a CA that was never made
            Assert.Contains(log.Lines, line => line.Contains("FAILED") && line.Contains("cannot create one"));
        }

        [Fact]
        public void UnconfirmedKeyGrantFailsTheInstallEvenAfterProvisioning()
        {
            var gateway = new FakeGateway();
            var grant = new FakeGrant { Result = false };
            var log = new RecordingLog();

            var exit = ProxyCaCore.Run(new[] { "provision", "svc" }, new FakeAccounts("NT AUTHORITY\\NetworkService"), gateway, grant, log.Act);

            Assert.Equal(ProxyCaCore.ExitProvisionFailed, exit);
            Assert.Equal(1, gateway.Calls);
            Assert.Equal(1, grant.Calls);
            Assert.Contains(log.Lines, line => line.Contains("leaf minting would fail"));
        }

        [Fact]
        public void NullCollaboratorsThrowInsteadOfSilentlyPassing()
        {
            Assert.Throws<ArgumentNullException>(() => ProxyCaCore.Run(new[] { "provision", "svc" }, null, new FakeGateway(), new FakeGrant(), null));
            Assert.Throws<ArgumentNullException>(() => ProxyCaCore.Run(new[] { "provision", "svc" }, new FakeAccounts(null), null, new FakeGrant(), null));
            Assert.Throws<ArgumentNullException>(() => ProxyCaCore.Run(new[] { "provision", "svc" }, new FakeAccounts(null), new FakeGateway(), null, null));
        }

        [Fact]
        public void NullLogIsTolerated()
        {
            var exit = ProxyCaCore.Run(new[] { "provision", "svc" }, new FakeAccounts(null), new FakeGateway(), new FakeGrant(), null);
            Assert.Equal(ProxyCaCore.ExitDone, exit);
        }

        // ---------------------------------------------------------------- constants the MSI depends on

        [Fact]
        public void MsiCommandLineAndFixedDropMatchTheAuthoredWxs()
        {
            // setup.wxs authors ExeCommand="provision MilestoneEventServerService" with no
            // override: the fixed drop path and the plain service name must parse.
            string serviceName;
            string pluginDllPath;
            Assert.True(ProxyCaCore.TryParse(
                new[] { "provision", "MilestoneEventServerService" }, out serviceName, out pluginDllPath));
            Assert.Equal(@"C:\Program Files\VideoOS\MIPPlugins\CascadiaWebContentPlugin\WebContent.dll", pluginDllPath);
        }
    }
}
