using Microsoft.Playwright;

namespace WebContent.UxTests.Harness;

/// <summary>
/// Discovers the CDP endpoints the plugin's WebView2 browser processes expose
/// (DevToolsActivePort written into each user data folder when the OS picks the port)
/// and connects Playwright to them.
/// </summary>
public sealed class CdpConnector : IAsyncDisposable
{
    private readonly List<IBrowser> _browsers = new();
    private IPlaywright _playwright;

    public static void CleanStalePortFiles(string dataRoot)
    {
        if (!Directory.Exists(dataRoot)) return;
        foreach (var file in Directory.EnumerateFiles(dataRoot, "DevToolsActivePort", SearchOption.AllDirectories))
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static List<int> ReadPorts(string dataRoot)
    {
        var ports = new List<int>();
        if (!Directory.Exists(dataRoot)) return ports;
        foreach (var file in Directory.EnumerateFiles(dataRoot, "DevToolsActivePort", SearchOption.AllDirectories))
        {
            try
            {
                var first = File.ReadLines(file).FirstOrDefault();
                if (int.TryParse(first?.Trim(), out var port) && port > 0) ports.Add(port);
            }
            catch { }
        }
        return ports.Distinct().ToList();
    }

    public async Task<IBrowser> ConnectFirstAvailableAsync(string dataRoot, int timeoutSeconds = 60)
    {
        _playwright ??= await Playwright.CreateAsync();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        Exception lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var port in ReadPorts(dataRoot))
            {
                try
                {
                    var browser = await _playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
                    _browsers.Add(browser);
                    return browser;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"No WebView2 CDP endpoint found under {dataRoot}. Last error: {lastError?.Message}");
    }

    public static async Task<IPage> WaitForPageAsync(IBrowser browser, string urlFragment, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var context in browser.Contexts)
            {
                var page = context.Pages.FirstOrDefault(p => !string.IsNullOrEmpty(p.Url) &&
                    p.Url.Contains(urlFragment ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                if (page != null) return page;
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"No CDP page matching '{urlFragment}'. Seen: " +
            string.Join(", ", browser.Contexts.SelectMany(c => c.Pages).Select(p => p.Url)));
    }

    public static async Task<IPage> WaitForAnyPageAsync(IBrowser browser, IEnumerable<string> urlFragments, int timeoutSeconds = 90)
    {
        var fragments = (urlFragments ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var context in browser.Contexts)
            {
                var page = context.Pages.FirstOrDefault(p => !string.IsNullOrEmpty(p.Url) &&
                    fragments.Any(f => p.Url.Contains(f, StringComparison.OrdinalIgnoreCase)));
                if (page != null) return page;
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"No CDP page matching any of: {string.Join(", ", fragments)}. Seen: " +
            string.Join(", ", browser.Contexts.SelectMany(c => c.Pages).Select(p => p.Url)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var browser in _browsers)
        {
            try { await browser.CloseAsync(); } catch { }
        }
        _playwright?.Dispose();
    }
}
