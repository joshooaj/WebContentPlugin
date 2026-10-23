using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace WebContent.Client
{
    /// <summary>
    /// Deterministic fingerprint of an admin-defined workspace definition (display name plus the
    /// raw source property dictionary). It changes whenever the admin edits any part of the
    /// definition, which lets the Smart Client detect that a persisted view item is stale and
    /// re-provision it from the current definition instead of keeping old settings.
    /// </summary>
    internal static class WorkspaceDefinitionFingerprint
    {
        public static string Compute(string workspaceName, IDictionary<string, string> sourceProperties)
        {
            var pairs = new List<string>();
            if (sourceProperties != null)
            {
                foreach (var entry in sourceProperties)
                {
                    pairs.Add($"{entry.Key}={entry.Value ?? string.Empty}");
                }
            }

            pairs.Sort(System.StringComparer.Ordinal);
            var payload = string.Join("&", pairs) + "|name=" + (workspaceName ?? string.Empty);

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
