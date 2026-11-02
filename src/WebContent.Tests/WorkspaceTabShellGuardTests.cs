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
    /// #81 took the Proxy tab live, and the same guards now pin the structured allowlist grid
    /// (column order + per-row AutomationIds, the collapsed-by-default wildcard banner, rows
    /// added/removed only by explicit buttons), that the proxy section left the Settings tab, and
    /// that the free-text allowlist box and its accept-untrusted token are gone from the admin UI.
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
            // the shell must not have dropped or renamed any of them. #81 moved the whole proxy
            // section off Settings onto the Proxy tab (pinned on its own below), so the proxy
            // ids are now asserted ABSENT here: the free-text txtProxyTargets box must not come
            // back alongside the grid, and Settings cannot grow proxy UI of its own.
            foreach (var required in new[]
            {
                "configError", "txtName", "txtHomeAddress",
                "chkHideToolbar", "chkHideAddress", "lblZoomLevel", "txtZoomLevel", "zoomSlider",
                "chkRememberLastAddress", "chkAllowBackgroundBrowser", "chkAllowAudioNotifications", "chkMuteAudio",
                "chkClearBrowsingDataOnExit", "chkIsolateBrowserInstances", "chkEnableDevTools",
            })
            {
                Assert.True(controlNames.Contains(required), $"Settings tab lost control '{required}'.");
            }

            foreach (var moved in new[]
            {
                "chkProxyEnabled", "lblProxyTargets", "txtProxyTargets", "lblProxyTargetsHint",
                "lblProxyTestUrl", "txtProxyTestUrl", "btnProxyTestFetch", "lblProxyTestHint", "lblProxyTestResult",
            })
            {
                Assert.True(!controlNames.Contains(moved), $"Settings tab carries proxy control '{moved}' - the proxy section lives on the Proxy tab (#81).");
            }
        }

        [Fact]
        public void ProxyTabCarriesTheStructuredAllowlistEditorGrid()
        {
            // #81: the Proxy tab is live - routing toggle, the structured grid, the wildcard
            // warning banner, add/remove affordances, and the fetch harness. The AutomationIds
            // below are the probe surface; the grid's per-row AutomationIds (proxyRow*) are the
            // ids the ux harness resolves cells by, scoped under each row.
            var proxy = ConfigPageTabs().Single(t => Name(t) == "tabProxy");
            var controlNames = proxy.Descendants()
                .Select(e => e.Attribute(Xaml + "Name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet();

            foreach (var required in new[]
            {
                "chkProxyEnabled", "lblProxyTargets", "proxyWildcardWarning", "txtProxyWildcardWarning",
                "dgProxyTargets", "btnProxyTargetAdd", "lblProxyTargetsHint",
                "lblProxyTestUrl", "txtProxyTestUrl", "btnProxyTestFetch", "lblProxyTestHint", "lblProxyTestResult",
            })
            {
                Assert.True(controlNames.Contains(required), $"Proxy tab lost probe-surface control '{required}'.");
            }

            var grid = proxy.Descendants().Single(e => e.Name.LocalName == "DataGrid");
            Assert.Equal("dgProxyTargets", grid.Attribute(Xaml + "Name")?.Value);

            // The grid edits rows through live template-column editors (no grid edit mode), and
            // every editing control carries its scoped AutomationId.
            var expectedColumns = new[]
            {
                ("Scheme", "proxyRowScheme"),
                ("Host", "proxyRowHost"),
                ("Port", "proxyRowPort"),
                ("Accept untrusted", "proxyRowUntrusted"),
                ("Problem", "proxyRowProblem"),
                ("Remove", "proxyRowRemove"),
            };
            var columns = grid.Descendants()
                .Where(e => e.Name.LocalName.EndsWith("Column", System.StringComparison.Ordinal))
                .ToList();
            Assert.Equal(expectedColumns.Length, columns.Count);
            for (var i = 0; i < expectedColumns.Length; i++)
            {
                Assert.Equal(expectedColumns[i].Item1, columns[i].Attribute("Header")?.Value);
                var cell = columns[i].Descendants()
                    .Single(e => e.Attribute("AutomationProperties.AutomationId")?.Value == expectedColumns[i].Item2);
                Assert.NotNull(cell);
            }

            // The warning banner starts collapsed; the page code keys its visibility solely off
            // a row being the full-host hatch.
            var warning = proxy.Descendants().Single(e => (e.Attribute(Xaml + "Name")?.Value) == "proxyWildcardWarning");
            Assert.Equal("Collapsed", warning.Attribute("Visibility")?.Value);

            // Add/remove: rows are added only by the explicit button, removed only by the
            // per-row button - never by grid row-editing affordances.
            Assert.Equal("False", grid.Attribute("CanUserAddRows")?.Value);
            Assert.Equal("False", grid.Attribute("CanUserDeleteRows")?.Value);
            var addButton = proxy.Descendants().Single(e => (e.Attribute(Xaml + "Name")?.Value) == "btnProxyTargetAdd");
            Assert.Equal("OnProxyTargetAdd", addButton.Attribute("Click")?.Value);
        }

        [Fact]
        public void ProxyGridCellBindingsPushToTheRowImmediately()
        {
            // #81, learned the hard way live: a DataGrid defers the source update of a cell-content
            // binding that does not ask for UpdateSourceTrigger=PropertyChanged. The editor then
            // shows the picked scheme / the ticked checkbox while the row silently keeps the old
            // value - the storage audit line and a reloaded pane both catch it, after the edit has
            // already looked successful. Every writeable binding inside the grid's templates must
            // say PropertyChanged; the read-only Problem cell (no Mode=TwoWay) is exempt.
            var proxy = ConfigPageTabs().Single(t => Name(t) == "tabProxy");
            var grid = proxy.Descendants().Single(e => e.Name.LocalName == "DataGrid");

            string Tighten(string value) => new(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

            var offenders = grid.Descendants()
                .SelectMany(e => e.Attributes())
                .Where(a => a.Value.Contains("{Binding", System.StringComparison.Ordinal))
                .Where(a => Tighten(a.Value).Contains("mode=twoway", System.StringComparison.OrdinalIgnoreCase))
                .Where(a => !Tighten(a.Value).Contains("updatesourcetrigger=propertychanged", System.StringComparison.OrdinalIgnoreCase))
                .Select(a => $"{a.Name.LocalName}=\"{a.Value}\"")
                .ToList();

            Assert.True(offenders.Count == 0,
                "grid cell bindings that defer their source update (add UpdateSourceTrigger=PropertyChanged): " +
                string.Join("; ", offenders));
        }

        [Fact]
        public void TheFreeTextAllowlistEditorIsGoneAndItsTokenIsNotUserFacing()
        {
            // #81 AC: "the accept-untrusted free-text token is no longer user-facing anywhere in
            // the MC UI (reader tolerance for stored legacy tokens remains)". Structurally pinned:
            // the free-text box and the Proxy-tab placeholder are absent from the page, and no
            // admin-facing string teaches a typed token any more - the flag is a grid column.
            // Reader tolerance stays pinned where it lives: ProxyPolicyTests pins the legacy
            // line grammar, ProxyAllowlistEditorTests pins legacy stored records rendering.
            var document = XDocument.Load(PagePath);
            Assert.DoesNotContain(document.Descendants(), e => Name(e) == "txtProxyTargets");
            Assert.DoesNotContain(document.Descendants(), e => Name(e) == "txtProxyTabPlaceholder");

            var strings = XDocument.Load(StringsPath);
            string Value(string name) => strings.Descendants("data")
                .Single(d => d.Attribute("name")?.Value == name)
                .Element("value")?.Value ?? string.Empty;

            Assert.DoesNotContain("accept-untrusted", Value("ProxyTargetsHint"), System.StringComparison.Ordinal);
            Assert.DoesNotContain("accept-untrusted", Value("InvalidProxyTargetMessage"), System.StringComparison.Ordinal);
            Assert.DoesNotContain("accept-untrusted", Value("ProxyWildcardWarning"), System.StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(Value("ProxyColumnAcceptUntrusted")));
        }

        [Fact]
        public void PlaceholderTabsCarryNoConfigWriteControls()
        {
            // #81 took the Proxy tab live, so Diagnostics (#84) is the only placeholder left: an
            // interactive input there could fire the dirty chain or land in the save. Status is
            // guarded on its own below.
            var writeCapable = new[]
            {
                "TextBox", "CheckBox", "Button", "Slider", "ComboBox", "ListBox",
                "RadioButton", "PasswordBox", "DataGrid", "TreeView", "Menu",
            };

            foreach (var placeholder in new[] { "tabDiagnostics" })
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
            Assert.Equal("False", grid.Attribute("CanUserResizeRows")?.Value);

            // Live finding (2026-11-02, MC-13 walk): a tri-state string on any WPF-boolean
            // DataGrid attribute - "CanUserResizeRows=Disabled" was the offender - throws the
            // BAML loader and takes the ENTIRE config page down (GenerateDetailUserControl
            // fails; not one control renders). Every WPF DataGrid CanUser* property is a bool,
            // so every one of them on every DataGrid of the page must carry exactly a bool.
            foreach (var dataGrid in ConfigPageTabs().SelectMany(t => t.Descendants()).Where(e => e.Name.LocalName == "DataGrid"))
            {
                var triStateOffenders = dataGrid.Attributes()
                    .Where(a => a.Name.LocalName.StartsWith("CanUser", System.StringComparison.Ordinal) &&
                                a.Value != "True" && a.Value != "False")
                    .Select(a => $"{a.Name.LocalName}={a.Value}")
                    .ToArray();
                Assert.True(triStateOffenders.Length == 0,
                    $"{Name(dataGrid)} carries non-boolean value(s) on WPF-boolean attributes: {string.Join(", ", triStateOffenders)} - the BAML loader refuses the whole page.");
            }

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

        private static string PagePath =>
            Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "Admin", "WorkspaceConfigPage.xaml");

        private static string StringsPath =>
            Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "Strings.resx");

        private static System.Collections.Generic.List<XElement> ConfigPageTabs()
        {
            var document = XDocument.Load(PagePath);
            return document.Descendants(Presentation + "TabItem").ToList();
        }

        private static string Name(XElement tab) => tab.Attribute(Xaml + "Name")?.Value ?? string.Empty;
    }
}
