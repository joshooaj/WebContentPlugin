using System;
using System.Collections.Generic;
using WebContent;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// The Management Client must not report "Saved configuration is broken" on a workspace the
    /// user has only just created. CreateItem persists a placeholder item whose WorkspaceConfig
    /// carries an empty home address, which DetectError classifies as MissingBrowserConfig; the
    /// placeholder name is what separates that never-saved state from a genuinely broken
    /// definition, so the admin page can stay quiet and let the user complete the workspace.
    /// </summary>
    public class NewWorkspacePlaceholderTests
    {
        private static Dictionary<string, string> PlaceholderProperties()
        {
            return new Dictionary<string, string>
            {
                { WorkspaceConfiguration.WorkspaceConfigKey, WorkspaceConfiguration.CreateDefault(string.Empty).Serialize() },
            };
        }

        [Fact]
        public void PlaceholderItemCarriesAnEmptyHomeAddressSoDetectionSeesMissingBrowserConfig()
        {
            Assert.Equal(WorkspaceConfigError.MissingBrowserConfig, WorkspaceConfiguration.DetectError(PlaceholderProperties()));
        }

        [Fact]
        public void PlaceholderNamedItemWithMissingConfigIsAnUnfinishedNewItem()
        {
            Assert.True(WorkspaceConfiguration.IsUnfinishedNewWorkspace(WorkspaceConfiguration.NewWorkspacePlaceholderName, PlaceholderProperties()));
        }

        [Theory]
        [InlineData("Demo Workspace")]
        [InlineData("demo workspace")]
        [InlineData(" Enter a name")]
        [InlineData("Enter a name ")]
        [InlineData("")]
        [InlineData(null)]
        public void RenamedOrSimilarNamedItemsWithMissingConfigAreBrokenNotNew(string name)
        {
            Assert.False(WorkspaceConfiguration.IsUnfinishedNewWorkspace(name, PlaceholderProperties()));
        }

        [Fact]
        public void CompletedWorkspaceEvenNamedLikeAPlaceholderIsNotUnfinished()
        {
            var properties = new Dictionary<string, string>
            {
                { WorkspaceConfiguration.WorkspaceConfigKey, WorkspaceConfiguration.CreateDefault("https://example.com/").Serialize() },
            };

            Assert.Equal(WorkspaceConfigError.None, WorkspaceConfiguration.DetectError(properties));
            Assert.False(WorkspaceConfiguration.IsUnfinishedNewWorkspace(WorkspaceConfiguration.NewWorkspacePlaceholderName, properties));
        }

        [Fact]
        public void LegacyShapePropertiesWithoutHomeAddressDecideTheSameWay()
        {
            var legacy = new Dictionary<string, string> { { "ZoomLevel", "100" } };

            Assert.True(WorkspaceConfiguration.IsUnfinishedNewWorkspace(WorkspaceConfiguration.NewWorkspacePlaceholderName, legacy));
            Assert.False(WorkspaceConfiguration.IsUnfinishedNewWorkspace("UX-PROBE-WS", legacy));
            Assert.True(WorkspaceConfiguration.IsUnfinishedNewWorkspace(WorkspaceConfiguration.NewWorkspacePlaceholderName, null));
        }
    }
}
