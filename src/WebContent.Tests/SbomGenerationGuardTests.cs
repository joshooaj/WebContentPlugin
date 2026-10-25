using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #25: the PostBuildEvent sbom-tool invocation ran with the build drop as BOTH the
    /// drop path and the component path (build events execute with CWD set to the output folder,
    /// so "-bc .\" resolved to the drop itself). Component detection then found zero packages and
    /// CI emitted "There were no packages detected during the generation workflow" while the SBOM
    /// silently shipped with only the root package. These guards pin the corrected invocation: a
    /// quoted, absolute component path via an MSBuild property (unquoted breaks on paths with
    /// spaces; a property ending in a backslash breaks quoting) and self-overwriting manifest
    /// directories.
    /// </summary>
    public class SbomGenerationGuardTests
    {
        private static readonly Regex SbomInvocation =
            new Regex(@"dotnet tool run sbom-tool generate[^\r\n]+", RegexOptions.Compiled);

        [Fact]
        public void SbomComponentPathIsQuotedAbsoluteMsBuildProperty()
        {
            var invocation = GetSbomInvocation();

            var componentPath = Regex.Match(invocation, @"-bc\s+(\S+)");
            Assert.True(componentPath.Success, "sbom-tool generate invocation has no -bc component path.");
            Assert.Equal("\"$(MSBuildProjectDirectory)\"", componentPath.Groups[1].Value);
        }

        [Fact]
        public void SbomGenerationOverwritesStaleManifestDirectory()
        {
            var invocation = GetSbomInvocation();

            var deleteFlag = Regex.Match(invocation, @"-D\s+(\S+)");
            Assert.True(deleteFlag.Success, "sbom-tool generate invocation has no -D (DeleteManifestDirIfPresent) option.");
            Assert.Equal("true", deleteFlag.Groups[1].Value);
        }

        private static string GetSbomInvocation()
        {
            var csproj = Path.Combine(RepoLocator.FindRepoRoot(), "src", "WebContent", "WebContent.csproj");
            var match = SbomInvocation.Match(File.ReadAllText(csproj));
            Assert.True(match.Success, $"{csproj} declares no 'dotnet tool run sbom-tool generate' PostBuildEvent step.");
            return match.Value;
        }
    }
}
