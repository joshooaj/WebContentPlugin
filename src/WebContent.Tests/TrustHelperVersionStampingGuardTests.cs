using System.IO;

using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #89: the trust helper exe must carry the build's Nerdbank.GitVersioning stamp,
    /// or MSI's file-copy rules (never replace an existing file whose version equals or outranks
    /// the payload) let an unstamped 1.0.0.0 Copy-Item stray shadow every upgrade forever.
    /// These source pins hold the stamping authoring on the helper csproj and keep its nbgv
    /// package in lockstep with the plugin project's. The live proof is the #89 upgrade row in
    /// docs/verification-matrix.md.
    /// </summary>
    public class TrustHelperVersionStampingGuardTests
    {
        private static string ReadHelperCsproj()
        {
            return File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContentTrustHelper", "WebContentTrustHelper.csproj"));
        }

        private static string ReadPluginCsproj()
        {
            return File.ReadAllText(Path.Combine(
                RepoLocator.FindRepoRoot(), "src", "WebContent", "WebContent.csproj"));
        }

        private static string Collapse(string source)
        {
            return System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ");
        }

        private static string NbgvPackageVersion(string csproj)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                csproj,
                "PackageReference Include=\"Nerdbank.GitVersioning\">\\s*<Version>([^<]+)</Version>");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        [Fact]
        public void TheTrustHelperProjectCarriesTheNbgvPackageReference()
        {
            var collapsed = Collapse(ReadHelperCsproj());

            // Same authoring the plugin project already carries; root version.json is the
            // single version source, so no second version file may appear beside the csproj.
            Assert.Contains(
                "<PackageReference Include=\"Nerdbank.GitVersioning\"> <Version>3.10.94</Version> <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets> <PrivateAssets>all</PrivateAssets> </PackageReference>",
                collapsed);
            Assert.False(
                File.Exists(Path.Combine(
                    RepoLocator.FindRepoRoot(), "src", "WebContentTrustHelper", "version.json")),
                "the helper must not carry a second version file - root version.json is the single version source");
        }

        [Fact]
        public void ThePluginProjectKeepsItsOwnNbgvReference()
        {
            // The lockstep check below is only meaningful while the plugin still stamps.
            Assert.Equal("3.10.94", NbgvPackageVersion(ReadPluginCsproj()));
        }

        [Fact]
        public void HelperAndPluginStampWithTheSameNbgvPackageVersion()
        {
            var helper = NbgvPackageVersion(ReadHelperCsproj());
            var plugin = NbgvPackageVersion(ReadPluginCsproj());

            Assert.NotNull(helper);
            Assert.NotNull(plugin);
            Assert.Equal(plugin, helper);
        }
    }
}
