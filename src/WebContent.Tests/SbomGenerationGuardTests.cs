using System;
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
    /// silently shipped with only the root package. These guards pin the corrected invocation: an
    /// absolute component path via an MSBuild property and self-overwriting manifest directories.
    /// </summary>
    public class SbomGenerationGuardTests
    {
        private static readonly Regex SbomInvocation =
            new Regex(@"dotnet tool run sbom-tool generate[^\r\n]+", RegexOptions.Compiled);

        [Fact]
        public void PluginCsprojExistsAndDeclaresPostBuildSbomGeneration()
        {
            var invocation = GetSbomInvocation();
            Assert.NotNull(invocation);
        }

        [Fact]
        public void SbomComponentPathUsesAbsoluteMsBuildPropertyNotRelativePath()
        {
            var invocation = GetSbomInvocation();

            var componentPath = Regex.Match(invocation, @"-bc\s+(\S+)");
            Assert.True(componentPath.Success, "sbom-tool generate invocation has no -bc component path.");
            Assert.Equal(@"$(ProjectDir)", componentPath.Groups[1].Value);
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
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var csproj = Path.Combine(dir.FullName, "src", "WebContent", "WebContent.csproj");
                if (File.Exists(csproj))
                {
                    var content = File.ReadAllText(csproj);
                    var match = SbomInvocation.Match(content);
                    return match.Success ? match.Value : null;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate src/WebContent/WebContent.csproj from the test assembly directory; the guard must run from a repository checkout.");
        }
    }
}
