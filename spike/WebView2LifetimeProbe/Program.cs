using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WebView2LifetimeProbe;

/// <summary>
/// #90 spike host (throwaway, no MIP/VMS): one role per process ('creator' or 'joiner').
/// Both roles construct IDENTICAL WebView2 environment options (same user data folder, same
/// additional browser argument), so by the documented WebView2 model the joiner must attach
/// to the creator's browser process rather than spawn its own. Everything interesting is
/// journaled to status-&lt;role&gt;.log so an orchestrating test (and a human) can watch:
///   INIT_OK / INIT_FAIL     CoreWebView2 initialization outcome
///   TICK n=...              successful in-page script round trips (proves the page is live)
///   TICK_FAIL ...           round trip broke (e.g. browser process vanished under us)
///   PROCESS_FAILED ...      WebView2 ProcessFailed events
///   SECOND_INIT_OK/FAIL     outcome of creating a SECOND WebView2 in-process with the same
///                           options after --spawn-second-after ms (the re-init question)
/// </summary>
internal static class Program
{
    [System.STAThread]
    private static int Main(string[] args)
    {
        string role = null;
        string dir = null;
        int? spawnSecondAfterMs = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "--role":
                    role = args[++i];
                    break;
                case "--dir":
                    dir = args[++i];
                    break;
                case "--spawn-second-after":
                    spawnSecondAfterMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
            }
        }

        if (role == null || dir == null)
        {
            Console.Error.WriteLine("usage: WebView2LifetimeProbe --role creator|joiner --dir <artifactDir> [--spawn-second-after <ms>]");
            return 2;
        }

        var app = new Application();
        var window = new ProbeWindow(role, dir, spawnSecondAfterMs);
        window.Show();
        return app.Run(window);
    }
}

internal sealed class ProbeWindow : Window
{
    private const string SharedBrowserArgument = "--disable-renderer-backgrounding";

    private readonly Journal _journal;
    private readonly int? _spawnSecondAfterMs;
    private WebView2 _webView;
    private DispatcherTimer _ticker;
    private int _lastTick = -1;

    public ProbeWindow(string role, string artifactDir, int? spawnSecondAfterMs)
    {
        Role = role;
        _journal = new Journal(Path.Combine(artifactDir, $"status-{role}.log"));
        _spawnSecondAfterMs = spawnSecondAfterMs;

        Title = $"WebView2LifetimeProbe-{role}";
        Width = 640;
        Height = 480;

        _journal.Write($"START role={role} pid={Environment.ProcessId}");

        var pagePath = Path.Combine(artifactDir, $"page-{role}.html");
        File.WriteAllText(pagePath, Page(role));

        _webView = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(artifactDir, "udf"),
                AdditionalBrowserArguments = SharedBrowserArgument,
            },
            Source = new Uri(pagePath),
        };
        Content = _webView;

        _webView.CoreWebView2InitializationCompleted += OnInitialized;
    }

    public string Role { get; }

    private void OnInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            _journal.Write($"INIT_FAIL role={Role} error={e.InitializationException?.Message ?? "(none)"}");
            return;
        }

        _journal.Write($"INIT_OK role={Role} pid={Environment.ProcessId}");
        _webView.CoreWebView2.ProcessFailed += (_, args) =>
            _journal.Write($"PROCESS_FAILED role={Role} kind={args.ProcessFailedKind} exit={args.ExitCode}");

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += async (_, _) =>
        {
            try
            {
                var script = await _webView.CoreWebView2.ExecuteScriptAsync("window.__tick||0");
                var tick = int.Parse(script.Trim('"'), CultureInfo.InvariantCulture);
                if (tick != _lastTick)
                {
                    _lastTick = tick;
                    _journal.Write($"TICK role={Role} n={tick}");
                }
            }
            catch (Exception ex)
            {
                _journal.Write($"TICK_FAIL role={Role} error={ex.Message}");
            }
        };
        _ticker.Start();

        if (_spawnSecondAfterMs.HasValue)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_spawnSecondAfterMs.Value) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await TryCreateSecondWebViewAsync();
            };
            timer.Start();
        }
    }

    private async System.Threading.Tasks.Task TryCreateSecondWebViewAsync()
    {
        try
        {
            // same options as the first: this asks whether a fresh init is possible in this
            // process after whatever the orchestrator did to the other host
            var second = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = _webView.CreationProperties.UserDataFolder,
                    AdditionalBrowserArguments = _webView.CreationProperties.AdditionalBrowserArguments,
                },
            };
            var grid = Content as System.Windows.Controls.Grid ?? WrapInGrid();
            grid.Children.Add(second);
            await second.EnsureCoreWebView2Async();
            _journal.Write($"SECOND_INIT_OK role={Role}");
        }
        catch (Exception ex)
        {
            _journal.Write($"SECOND_INIT_FAIL role={Role} error={ex.Message}");
        }
    }

    private System.Windows.Controls.Grid _grid;

    private System.Windows.Controls.Grid WrapInGrid()
    {
        _grid = new System.Windows.Controls.Grid();
        var existing = Content as UIElement;
        if (existing != null)
        {
            Content = null; // detach first: WPF refuses an element that still has a logical parent
            _grid.Children.Add(existing);
        }

        Content = _grid;
        return _grid;
    }

    private static string Page(string role) =>
        "<html><head><title>probe-" + role + "</title></head><body><h1>" + role +
        "</h1><div id=t></div><script>window.__tick=0;setInterval(function(){window.__tick++;" +
        "document.getElementById('t').textContent=window.__tick;},250);</script></body></html>";

    private sealed class Journal
    {
        private readonly string _path;

        public Journal(string path)
        {
            _path = path;
        }

        public void Write(string line)
        {
            var stamped = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line;
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(stamped);
        }
    }
}
