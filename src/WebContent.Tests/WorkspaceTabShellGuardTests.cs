using System.IO;
using System.Linq;
using System.Xml.Linq;

using WebContent.Admin;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #74: the Management Client workspace config page is a four-tab WPF shell
    /// (Settings | Proxy | Diagnostics | Status). The policy tests pin the Status tab role gate
    /// (fail closed); the XAML guards pin the tab order/identity, that every existing settings
    /// control moved verbatim onto Settings, and that the placeholder tabs carry no input
    /// control that could write configuration ("no config writes from placeholder tabs").
    /// </summary>
    public class WorkspaceTabShellGuardTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        [Theory]
        [InlineData("Granted", true)]
        [InlineData("Denied", false)]
        [InlineData("LookupFailed", false)]
        public void StatusTabRendersOnlyOnExplicitGrant(string permissionName, bool expected)
        {
            var permission = (WorkspaceManagePermission)System.Enum.Parse(typeof(WorkspaceManagePermission), permissionName);
            Assert.Equal(expected, WorkspaceTabPolicy.ShowStatusTab(permission));
        }

        [Fact]
        public void PageDeclaresTheFourTabsInShellOrderWithStableAutomationIds()
        {
            var tabs = ConfigPageTabs();
            Assert.Equal(
                new[] { "tabSettings", "tabProxy", "tabDiagnostics", "tabStatus" },
                tabs.Select(Name).ToArray());
            Assert.Equal(
                new[] { "Settings", "Proxy", "Diagnostics", "Status" },
                tabs.Select(t => t.Attribute("Header")?.Value).ToArray());
        }

        [Fact]
        public void StatusTabStartsCollapsedSoTheGateFailsClosedBeforeAnyFill()
        {
            var status = ConfigPageTabs().Single(t => Name(t) == "tabStatus");
            Assert.Equal("Collapsed", status.Attribute("Visibility")?.Value);
        }

        [Fact]
        public void SettingsTabCarriesEveryExistingControlVerbatim()
        {
            var settings = ConfigPageTabs().Single(t => Name(t) == "tabSettings");
            var controlNames = settings.Descendants()
                .Select(e => e.Attribute(Xaml + "Name")?.Value)
                .Where(n => n != null)
                .ToHashSet();

            // Every control the item manager fills/saves and the ux probes reach by AutomationId:
            // the shell must not have dropped or renamed any of them.
            foreach (var required in new[]
            {
                "configError", "txtName", "txtHomeAddress",
                "chkHideToolbar", "chkHideAddress", "lblZoomLevel", "txtZoomLevel", "zoomSlider",
                "chkRememberLastAddress", "chkAllowBackgroundBrowser", "chkAllowAudioNotifications", "chkMuteAudio",
                "chkClearBrowsingDataOnExit", "chkIsolateBrowserInstances", "chkEnableDevTools",
                "chkProxyEnabled", "lblProxyTargets", "txtProxyTargets", "lblProxyTargetsHint",
                "lblProxyTestUrl", "txtProxyTestUrl", "btnProxyTestFetch", "lblProxyTestHint", "lblProxyTestResult",
            })
            {
                Assert.True(controlNames.Contains(required), $"Settings tab lost control '{required}'.");
            }
        }

        [Fact]
        public void PlaceholderTabsCarryNoConfigWriteControls()
        {
            // The AC "no config writes from placeholder tabs" pinned structurally: an interactive
            // input on Proxy/Diagnostics could fire the dirty chain or land in the save. Status is
            // no longer a placeholder (#76) and is guarded on its own below.
            var writeCapable = new[]
            {
                "TextBox", "CheckBox", "Button", "Slider", "ComboBox", "ListBox",
                "RadioButton", "PasswordBox", "DataGrid", "TreeView", "Menu",
            };

            foreach (var placeholder in new[] { "tabProxy", "tabDiagnostics" })
            {
                var tab = ConfigPageTabs().Single(t => Name(t) == placeholder);
                var offenders = tab.Descendants()
                    .Select(e => e.Name.LocalName)
                    .Where(n => writeCapable.Contains(n))
                    .ToArray();
                Assert.True(offenders.Length == 0, $"{placeholder} contains write-capable input(s): {string.Join(", ", offenders)}.");
                Assert.True(
                    tab.Descendants().Any(e => e.Attribute(Xaml + "Name")?.Value == placeholder.Replace("tab", "txt") + "TabPlaceholder"),
                    $"{placeholder} has no placeholder marker TextBlock for the ux probes to assert.");
            }
        }

        [Fact]
        public void StatusTabReportsOnlyAndCanNeverDirtyThePane()
        {
            // #76 replaced the Status placeholder with the live session table. It reads, it never
            // writes: the grid is read-only with no row editing, and the only button on the tab is
            // the explicit re-pull - so walking the tab cannot enable Save or land anything in
            // ValidateAndSaveUserControl. AutomationIds the ux probe selects by are pinned too.
            var status = ConfigPageTabs().Single(t => Name(t) == "tabStatus");

            var grid = status.Descendants().Single(e => e.Name.LocalName == "DataGrid");
            Assert.Equal("dgStatusSessions", grid.Attribute(Xaml + "Name")?.Value);
            Assert.Equal("True", grid.Attribute("IsReadOnly")?.Value);
            Assert.Equal("False", grid.Attribute("CanUserAddRows")?.Value);
            Assert.Equal("False", grid.Attribute("CanUserDeleteRows")?.Value);

            foreach (var button in status.Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                Assert.Equal("OnStatusRefresh", button.Attribute("Click")?.Value);
            }

            var settingsInput = new[] { "TextBox", "CheckBox", "Slider", "ComboBox", "ListBox", "RadioButton", "PasswordBox" };
            var offenders = status.Descendants()
                .Select(e => e.Name.LocalName)
                .Where(n => settingsInput.Contains(n))
                .ToArray();
            Assert.True(offenders.Length == 0, $"Status tab contains configuration input(s): {string.Join(", ", offenders)}.");

            var names = status.Descendants()
                .Select(e => e.Attribute(Xaml + "Name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            foreach (var required in new[] { "txtStatusSummary", "dgStatusSessions", "statusError", "txtStatusError", "btnStatusRefresh" })
            {
                Assert.True(names.Contains(required), $"Status tab lost the probe surface '{required}'.");
            }
        }

        private static System.Collections.Generic.List<XElement> ConfigPageTabs()
        {
            var path = Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "Admin", "WorkspaceConfigPage.xaml");
            var document = XDocument.Load(path);
            return document.Descendants(Presentation + "TabItem").ToList();
        }

        private static string Name(XElement tab) => tab.Attribute(Xaml + "Name")?.Value ?? string.Empty;
    }
}
