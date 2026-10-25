using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace WebContent.Tests
{
    /// <summary>
    /// Ticket #16 (guarding #13): the plugin deliberately does NOT participate in the MIP site
    /// license scheme (removed in 02ff12c, MIT relicense via PR #23). This source-hygiene scan
    /// pins that decision: the deleted license classes must stay deleted and no plugin source
    /// may reference the VideoOS license APIs again, so a merge or revert cannot silently
    /// resurrect license gating in an MIT-licensed plugin.
    /// </summary>
    public class PluginLicenseHygieneTests
    {
        private static readonly string[] ForbiddenLicenseReferences =
        {
            "VideoOS.Platform.License",
            "LicenseManager",
            "ReservedLicenseManager",
            "PluginLicenseType",
            "SiteLicenseHandler",
            "WebContentLicenseManager",
        };

        private static readonly string[] DeletedLicenseFiles =
        {
            "Admin/SiteLicenseHandler.cs",
            "Admin/WebContentLicenseManager.cs",
        };

        [Fact]
        public void DeletedLicenseClassesDoNotReappear()
        {
            var pluginRoot = FindPluginSourceRoot();
            var offenders = DeletedLicenseFiles
                .Select(f => Path.Combine(pluginRoot, f.Replace('/', Path.DirectorySeparatorChar)))
                .Where(File.Exists)
                .ToList();

            Assert.Empty(offenders);
        }

        [Fact]
        public void NoPluginSourceReferencesTheRemovedLicenseApiSurface()
        {
            var pluginRoot = FindPluginSourceRoot();
            var violations = new List<string>();

            foreach (var file in Directory.EnumerateFiles(pluginRoot, "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (var forbidden in ForbiddenLicenseReferences)
                    {
                        if (lines[i].Contains(forbidden, StringComparison.Ordinal))
                        {
                            violations.Add($"{Path.GetRelativePath(pluginRoot, file)}:{i + 1} references '{forbidden}': {lines[i].Trim()}");
                        }
                    }
                }
            }

            Assert.Empty(violations);
        }

        private static string FindPluginSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "WebContent");
                if (File.Exists(Path.Combine(candidate, "WebContentPluginDefinition.cs")))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate src/WebContent from the test assembly directory; the hygiene guard must run from a repository checkout.");
        }
    }
}
