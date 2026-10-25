using System;
using System.IO;

namespace WebContent.Tests
{
    /// <summary>
    /// Shared upward search from the test assembly directory to the repository checkout root,
    /// used by the source-hygiene guards that read plugin build files from disk.
    /// </summary>
    internal static class RepoLocator
    {
        internal static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "build.ps1")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "Could not locate the repository root (build.ps1 marker) from the test assembly directory; the guard must run from a repository checkout.");
        }
    }
}
