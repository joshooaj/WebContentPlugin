using WebContent.Admin;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// WorkspaceItemManager.ValidateAndSaveUserControl refuses to persist an empty name: the
    /// name box decides the tree node and the only way to address the workspace. Everything the
    /// user could type into that box that still names a workspace must pass, and everything that
    /// does not must be rejected — including the whitespace traps the shell happily accepts.
    /// </summary>
    public class WorkspaceNameValidationTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(" \t\n ")]
        public void EmptyOrWhitespaceNamesAreRejected(string name)
        {
            Assert.True(WorkspaceNameValidation.IsEmpty(name));
        }

        [Theory]
        [InlineData("Demo Workspace")]
        [InlineData("UX-PROBE-WS")]
        [InlineData("Enter a name")]
        [InlineData("  padded  ")]
        public void AnythingThatNamesAWorkspacePasses(string name)
        {
            Assert.False(WorkspaceNameValidation.IsEmpty(name));
        }
    }
}
