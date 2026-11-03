using System.IO;
using System.Linq;
using System.Xml.Linq;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #71: the Smart Client properties pane of a view item inside a managed workspace
    /// renders the security-relevant field (the home/target URL) disabled with a "Managed by
    /// Administrator" indicator, while every cosmetic/navigation control stays enabled and saves
    /// per instance. These source guards pin the XAML shape the live SC probe drives: the locked
    /// TextBox bindings and stable AutomationIds, the two admin notices wired to the managed
    /// flag, and the guard that no cosmetic control declares an IsEnabled binding - a cosmetic
    /// control that learned to disable itself would silently break "unlocked controls still
    /// save per instance".
    /// </summary>
    public class ManagedViewPropertiesPaneGuardTests
    {
        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static XDocument PropertiesPaneXaml()
        {
            var path = Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "Client", "WebContentPropertiesUserControl.xaml");
            return XDocument.Load(path);
        }

        private static string Name(XElement element) => element.Attribute(Xaml + "Name")?.Value;

        [Fact]
        public void HomeAddressFieldIsDrivenByTheEditableFlagAndCarriesTheProbeAutomationId()
        {
            var home = PropertiesPaneXaml()
                .Descendants(Presentation + "TextBox")
                .Single(e => Name(e) == "txtHomeAddress");

            Assert.Equal("{Binding IsHomeAddressEditable}", home.Attribute("IsEnabled")?.Value);
            Assert.Equal("{Binding HomeAddressToolTip}", home.Attribute("ToolTip")?.Value);
            // The two-way value binding must stay: standalone items keep the editable field.
            Assert.Contains("HomeAddress", home.Attribute("Text")?.Value ?? string.Empty);
        }

        [Fact]
        public void BothAdminNoticesRenderOnlyForManagedItems()
        {
            var xaml = PropertiesPaneXaml();
            foreach (var noticeId in new[] { "managedHomeNotice", "managedProxyNotice" })
            {
                var notice = xaml.Descendants(Presentation + "TextBlock").Single(e => Name(e) == noticeId);
                Assert.Equal("{Binding ManagedByAdministratorLabel, Mode=OneTime}", notice.Attribute("Text")?.Value);
                Assert.Equal("{Binding IsManagedWorkspace, Converter={StaticResource BoolToVisibility}}", notice.Attribute("Visibility")?.Value);
            }

            Assert.NotNull(xaml.Descendants(Presentation + "BooleanToVisibilityConverter")
                .Single(c => c.Attribute(Xaml + "Key")?.Value == "BoolToVisibility"));
        }

        [Fact]
        public void NoCosmeticControlDeclaresAnIsEnabledBinding()
        {
            // Only the locked home-address TextBox may condition its own enabled state. Every
            // checkbox, the zoom slider, and the zoom text box stay unconditionally enabled so
            // per-instance choices keep saving in a managed workspace.
            var xaml = PropertiesPaneXaml();

            foreach (var checkbox in xaml.Descendants(Presentation + "CheckBox"))
            {
                Assert.Null(checkbox.Attribute("IsEnabled"));
            }

            foreach (var other in xaml.Descendants(Presentation + "Slider")
                         .Concat(xaml.Descendants(Presentation + "Button"))
                         .Concat(xaml.Descendants(Presentation + "TextBox").Where(e => Name(e) != "txtHomeAddress")))
            {
                Assert.Null(other.Attribute("IsEnabled"));
            }
        }

        [Fact]
        public void EveryProbeTargetCarriesAStableAutomationId()
        {
            var names = PropertiesPaneXaml()
                .Descendants()
                .Select(Name)
                .Where(n => n != null)
                .ToHashSet();

            foreach (var required in new[]
            {
                "txtHomeAddress", "managedHomeNotice", "managedProxyNotice",
                "chkRememberLastAddress", "chkHideToolbar", "chkHideAddress",
                "chkClearBrowsingDataOnExit", "chkMuteAudio", "chkAllowAudioNotifications",
                "chkAllowBackgroundBrowser", "chkIsolateBrowserInstances", "chkEnableDevTools",
                "txtZoomLevel", "zoomSlider", "btnClearBrowsingData",
            })
            {
                Assert.Contains(required, names);
            }
        }
    }
}
