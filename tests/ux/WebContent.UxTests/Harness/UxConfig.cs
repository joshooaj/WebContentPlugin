using System.Text.RegularExpressions;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Configuration for the UX harness. Credentials come from the repo-root .env
/// (VMS_ADDRESS/VMS_USERNAME/VMS_PASSWORD); paths get Milestone-default fallbacks
/// and can be overridden with UX_* environment variables or a tests/ux/uxconfig.json.
/// </summary>
public sealed class UxConfig
{
    public string VmsAddress { get; private init; }
    public string VmsUsername { get; private init; }
    public string VmsPassword { get; private init; }

    public string SmartClientExe { get; private init; }
    public string AdminClientExe { get; private init; }
    public string WebView2DataRoot { get; private init; }
    public string ArtifactsRoot { get; private init; }
    public string RepoRoot { get; private init; }

    /// <summary>Name of the Smart Client workspace tab the plugin provisions on this server.</summary>
    public string WorkspaceName { get; private init; }

    /// <summary>Name of the throwaway workspace the Management Client scenario creates/edits.</summary>
    public string ProbeWorkspaceName { get; private init; }

    /// <summary>URL fragment expected when the configured workspace restores its home page.</summary>
    public string HomeAddressFragment { get; private init; }

    /// <summary>Name of the Smart Client view used for README screenshots.</summary>
    public string ViewName { get; private init; }

    /// <summary>Name of the workspace shown in the Management Client README screenshots.</summary>
    public string McWorkspaceName { get; private init; }

    /// <summary>Name of the plugin's top-level node in the Management Client tree.</summary>
    public string PluginNodeName { get; private init; }

    /// <summary>Role whose security tab is shown in the Management Client README screenshot.</summary>
    public string RoleName { get; private init; }

    /// <summary>
    /// Whether the configured workspace is expected to hide its address bar. The Smart Client
    /// address-bar tests use this to assert the right contract (bar absent vs bar present).
    /// </summary>
    public bool HideAddress { get; private init; }

    public static UxConfig Load()
    {
        var repoRoot = FindRepoRoot();
        var envFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var envPath = Path.Combine(repoRoot, ".env");
        if (File.Exists(envPath))
        {
            foreach (var line in File.ReadAllLines(envPath))
            {
                var m = Regex.Match(line.Trim(), "^(?<k>[A-Za-z_][A-Za-z0-9_]*)=(?<v>.*)$");
                if (m.Success)
                {
                    envFile[m.Groups["k"].Value] = m.Groups["v"].Value.Trim();
                }
            }
        }

        var localConfig = Path.Combine(repoRoot, "tests", "ux", "uxconfig.json");
        if (File.Exists(localConfig))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(localConfig));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    envFile[prop.Name] = prop.Value.GetString();
                }
                else if (prop.Value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    envFile[prop.Name] = prop.Value.GetBoolean().ToString();
                }
            }
        }

        string Pick(string varName, params string[] envKeys)
        {
            var fromVar = Environment.GetEnvironmentVariable(varName);
            if (!string.IsNullOrWhiteSpace(fromVar)) return fromVar;
            foreach (var k in envKeys)
            {
                if (envFile.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        var config = new UxConfig
        {
            RepoRoot = repoRoot,
            VmsAddress = Pick("UX_VMS_ADDRESS", "VMS_ADDRESS") ?? throw new InvalidOperationException("VMS_ADDRESS missing (set it in the repo-root .env or UX_VMS_ADDRESS)."),
            VmsUsername = Pick("UX_VMS_USERNAME", "VMS_USERNAME") ?? throw new InvalidOperationException("VMS_USERNAME missing (set it in the repo-root .env or UX_VMS_USERNAME)."),
            VmsPassword = Pick("UX_VMS_PASSWORD", "VMS_PASSWORD") ?? throw new InvalidOperationException("VMS_PASSWORD missing (set it in the repo-root .env or UX_VMS_PASSWORD)."),
            SmartClientExe = Pick("UX_SMARTCLIENT_EXE") ?? @"C:\Program Files\Milestone\XProtect Smart Client\Client.exe",
            AdminClientExe = Pick("UX_ADMINCLIENT_EXE") ?? @"C:\Program Files\Milestone\XProtect Management Client\VideoOS.Administration.exe",
            WebView2DataRoot = Pick("UX_WEBVIEW2_DATA_ROOT") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartClient.WebContentData"),
            ArtifactsRoot = Pick("UX_ARTIFACTS_ROOT") ?? Path.Combine(repoRoot, "artifacts", "ux"),
            WorkspaceName = Pick("UX_WORKSPACE_NAME", "workspaceName") ?? "Web Content",
            ProbeWorkspaceName = Pick("UX_PROBE_WORKSPACE_NAME", "probeWorkspaceName") ?? "UX-PROBE-WS",
            HomeAddressFragment = Pick("UX_HOME_ADDRESS_FRAGMENT", "homeAddressFragment") ?? "github",
            ViewName = Pick("UX_VIEW_NAME", "viewName") ?? "Demo View",
            McWorkspaceName = Pick("UX_MC_WORKSPACE_NAME", "mcWorkspaceName") ?? "Demo Workspace",
            PluginNodeName = Pick("UX_PLUGIN_NODE_NAME", "pluginNodeName") ?? "Cascadia Technology",
            RoleName = Pick("UX_ROLE_NAME", "roleName") ?? "Operators",
            HideAddress = bool.TryParse(Pick("UX_HIDE_ADDRESS", "hideAddress"), out var hideAddress) && hideAddress,
        };

        if (!File.Exists(config.SmartClientExe)) throw new FileNotFoundException($"Smart Client exe not found: {config.SmartClientExe}");
        if (!File.Exists(config.AdminClientExe)) throw new FileNotFoundException($"Management Client exe not found: {config.AdminClientExe}");
        return config;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".env")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src", "WebContent")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the WebContentPlugin repo root (needs .env + src/WebContent).");
    }
}
