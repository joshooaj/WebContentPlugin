using System.IO;
using System.Linq;
using System.Xml.Linq;

using WebContent.Admin;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #74: the Management Client workspace config page is a four-tab WPF shell
    /// (Settings | Proxy | Diagnostics | Status). The policy tests pin the Status and Diagnostics
    /// tab role gates (fail closed); the XAML guards pin the tab order/identity and that every
    /// existing settings control moved verbatim onto Settings. No placeholder tab is left: #81
    /// took the Proxy tab live and #84 took Diagnostics live, and the "reads only" guards now
    /// pin both live read tabs - the Status session table and the Diagnostics blocked-origin
    /// table - down to the per-row AutomationIds and the buttons' exact handlers. The same
    /// guards pin the structured allowlist grid (column order + per-row AutomationIds, the
    /// collapsed-by-default wildcard banner, rows added/removed only by explicit buttons), that
    /// the proxy section left the Settings tab, and that the free-text allowlist box and its
    /// accept-untrusted token are gone from the admin UI.
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

        [Theory]
        [InlineData("Granted", true)]
        [InlineData("Denied", false)]
        [InlineData("LookupFailed", false)]
        public void DiagnosticsTabRendersOnlyOnExplicitGrant(string permissionName, bool expected)
        {
            // #84: the Diagnostics tab rides the identical Manage gate - one affordance on it
            // writes the allowlist, so it cannot be visible to users who may not save.
            var permission = (WorkspaceManagePermission)System.Enum.Parse(typeof(WorkspaceManagePermission), permissionName);
            Assert.Equal(expected, WorkspaceTabPolicy.ShowDiagnosticsTab(permission));
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
                "lblProxyResponseCap", "txtProxyResponseCapMB", "lblProxyResponseCapHint",
                "lblProxyTestUrl", "txtProxyTestUrl", "btnProxyTestFetch", "lblProxyTestHint", "lblProxyTestResult",
            })
            {
                Assert.True(controlNames.Contains(required), $"Proxy tab lost probe-surface control '{required}'.");
            }

            // #85: the response-cap field is a plain whole-MB TextBox (no grid, no picker); the
            // probe drives it through this AutomationId and its TextChanged handler marks dirty.
            var capBox = proxy.Descendants().Single(e => (e.Attribute(Xaml + "Name")?.Value) == "txtProxyResponseCapMB");
            Assert.Equal("proxyResponseCapMB", capBox.Attribute("AutomationProperties.AutomationId")?.Value);
            Assert.Equal("OnProxyResponseCapTextChanged", capBox.Attribute("TextChanged")?.Value);

            // #86: the Proxy tab now carries THREE grids (allowlist + the two header-policy
            // grids); probes resolve each by its AutomationId, so every grid must carry one.
            var grid = proxy.Descendants().Single(e => (e.Attribute(Xaml + "Name")?.Value) == "dgProxyTargets");

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
        public void ProxyTabCarriesTheHeaderPolicyGridsForBothLegs()
        {
            // #86: two name/value/strip grids (request leg + response leg), each addressed by a
            // stable AutomationId so a live probe can scope its cell reads to one leg, each with
            // the same live-editor column shape the allowlist grid established.
            var proxy = ConfigPageTabs().Single(t => Name(t) == "tabProxy");
            var expectedColumns = new[]
            {
                ("Header", "proxyHeaderRowName"),
                ("Value", "proxyHeaderRowValue"),
                ("Strip", "proxyHeaderRowStrip"),
                ("Problem", "proxyHeaderRowProblem"),
                ("Remove", "proxyHeaderRowRemove"),
            };

            foreach (var (gridName, automationId, addButtonName, addHandler) in new[]
            {
                ("dgProxyRequestHeaders", "proxyRequestHeadersGrid", "btnProxyHeaderAddRequest", "OnProxyHeaderAddRequest"),
                ("dgProxyResponseHeaders", "proxyResponseHeadersGrid", "btnProxyHeaderAddResponse", "OnProxyHeaderAddResponse"),
            })
            {
                var grid = proxy.Descendants().Single(e => (e.Attribute(Xaml + "Name")?.Value) == gridName);
                Assert.Equal(automationId, grid.Attribute("AutomationProperties.AutomationId")?.Value);
                Assert.Equal("False", grid.Attribute("CanUserAddRows")?.Value);
                Assert.Equal("False", grid.Attribute("CanUserDeleteRows")?.Value);

                var columns = grid.Descendants()
                    .Where(e => e.Name.LocalName.EndsWith("Column", System.StringComparison.Ordinal))
                    .ToList();
                Assert.Equal(expectedColumns.Length, columns.Count);
                for (var i = 0; i < expectedColumns.Length; i++)
                {
                    Assert.Equal(expectedColumns[i].Item1, columns[i].Attribute("Header")?.Value);
                    Assert.NotNull(columns[i].Descendants()
                        .Single(e => e.Attribute("AutomationProperties.AutomationId")?.Value == expectedColumns[i].Item2));
                }

                var addButton = proxy.Descendants().Single(e => (e.Attribute(Xaml + "Name")?.Value) == addButtonName);
                Assert.Equal(addHandler, addButton.Attribute("Click")?.Value);
            }

            // Section scaffolding the admin (and a screenshot) needs: the labels and the hint that
            // documents the FIXED hop-by-hop set and the no-Via/no-X-Forwarded-For posture.
            foreach (var required in new[] { "lblProxyHeaders", "lblProxyHeadersHint", "lblProxyRequestHeaders", "lblProxyResponseHeaders" })
            {
                Assert.NotNull(proxy.Descendants().SingleOrDefault(e => (e.Attribute(Xaml + "Name")?.Value) == required));
            }
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

            // #86: every Proxy-tab grid (allowlist + both header grids) carries the same live-
            // editor obligation - guard them all, not just the first.
            static string Tighten(string value) => new(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

            foreach (var grid in proxy.Descendants().Where(e => e.Name.LocalName == "DataGrid"))
            {
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
        public void DiagnosticsTabStartsCollapsedSoTheGateFailsClosedBeforeAnyFill()
        {
            var diagnostics = ConfigPageTabs().Single(t => Name(t) == "tabDiagnostics");
            Assert.Equal("Collapsed", diagnostics.Attribute("Visibility")?.Value);
        }

        [Fact]
        public void DiagnosticsTabReportsThenPromotesAndCanNeverWriteAnythingElse()
        {
            // #84: the Diagnostics tab is live - the blocked-origin table, its summary line, the
            // failure banner, and one promotion affordance per row. The table reads; the ONLY
            // write path is the per-row "Add to allowlist" button, which edits the Proxy tab's
            // grid through the page code (verified in the page/pure layers) - walking the tab
            // with any other input is impossible, so Save only arms through that button.
            var diagnostics = ConfigPageTabs().Single(t => Name(t) == "tabDiagnostics");

            var grid = diagnostics.Descendants().Single(e => e.Name.LocalName == "DataGrid");
            Assert.Equal("dgDiagnosticsBlocked", grid.Attribute(Xaml + "Name")?.Value);
            Assert.Equal("True", grid.Attribute("IsReadOnly")?.Value);
            Assert.Equal("False", grid.Attribute("CanUserAddRows")?.Value);
            Assert.Equal("False", grid.Attribute("CanUserDeleteRows")?.Value);
            Assert.Equal("False", grid.Attribute("CanUserResizeRows")?.Value);

            // Every button on the tab is one of the two pinned handlers; nothing else can fire.
            foreach (var button in diagnostics.Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                var handler = button.Attribute("Click")?.Value;
                Assert.True(handler == "OnDiagnosticsRefresh" || handler == "OnDiagnosticsPromote",
                    $"{Name(button)} carries unexpected handler '{handler}'.");
            }

            // The promote button: hidden (not merely disabled) for rows the promotion cannot
            // apply to, and addressed by the ux probes through its scoped AutomationId.
            var promote = diagnostics.Descendants()
                .Single(e => e.Attribute("AutomationProperties.AutomationId")?.Value == "diagnosticsRowPromote");
            Assert.Equal("OnDiagnosticsPromote", promote.Attribute("Click")?.Value);
            Assert.Equal("Add to allowlist", promote.Attribute("Content")?.Value);
            var promoteVisibility = promote.Attribute("Visibility")?.Value ?? string.Empty;
            Assert.Contains("CanPromote", promoteVisibility, System.StringComparison.Ordinal);
            Assert.Contains("Converter", promoteVisibility, System.StringComparison.Ordinal);

            // No free input anywhere on the tab.
            var settingsInput = new[] { "TextBox", "CheckBox", "Slider", "ComboBox", "ListBox", "RadioButton", "PasswordBox" };
            var offenders = diagnostics.Descendants()
                .Select(e => e.Name.LocalName)
                .Where(n => settingsInput.Contains(n))
                .ToArray();
            Assert.True(offenders.Length == 0, $"Diagnostics tab contains configuration input(s): {string.Join(", ", offenders)}.");

            var names = diagnostics.Descendants()
                .Select(e => e.Attribute(Xaml + "Name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
            foreach (var required in new[] { "txtDiagnosticsSummary", "dgDiagnosticsBlocked", "diagnosticsError", "txtDiagnosticsError", "btnDiagnosticsRefresh" })
            {
                Assert.True(names.Contains(required), $"Diagnostics tab lost the probe surface '{required}'.");
            }

            // #84 AC, pinned as prose: the tab says out loud that the list is in the Event
            // Server's memory and resets when the service restarts.
            var cadence = diagnostics.Descendants()
                .Single(e => (e.Attribute(Xaml + "Name")?.Value) == "txtDiagnosticsCadence")
                .Attribute("Text")?.Value ?? string.Empty;
            Assert.Contains("memory", cadence, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("restart", cadence, System.StringComparison.OrdinalIgnoreCase);
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
