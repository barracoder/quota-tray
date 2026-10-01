using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using QuotaTray.Core;

namespace QuotaTray.App;

/// <summary>Owns the tray icon and its menu; translates snapshots into icon, tooltip and menu text.</summary>
/// <remarks>
/// Every native-backed object (menu, menu items, icons) is created once and kept for the life of
/// the controller. Avalonia's macOS backend releases wrapped AppKit objects from the finalizer
/// thread, and dropping menu items or icons on each refresh produced intermittent crashes inside
/// AppKit on the main thread. So: a fixed pool of status lines updated in place, and cached icons.
/// </remarks>
public sealed class TrayController : IDisposable
{
    /// <summary>Upper bound on provider + window lines shown in the menu.</summary>
    private const int StatusLinePoolSize = 32;

    private readonly Application _app;
    private readonly AppHost _host;
    private readonly Action _quit;
    private readonly TrayIcon _icon = new();
    private readonly NativeMenu _menu = new();
    private readonly NativeMenuItem[] _statusLines = new NativeMenuItem[StatusLinePoolSize];
    private readonly NativeMenuItem _configErrorLine = Disabled(string.Empty);
    private readonly NativeMenuItemSeparator _configErrorSeparator = new();
    private readonly NativeMenuItem _updatedLine = Disabled(string.Empty);
    private UsagePoller? _subscribedPoller;

    public TrayController(Application app, AppHost host, Action quit)
    {
        _app = app;
        _host = host;
        _quit = quit;
    }

    public void Start()
    {
        BuildMenuOnce();
        FillMenu([]);

        _icon.ToolTipText = "quota-tray — loading…";
        _icon.Icon = GaugeIconRenderer.Get(null, Severity.Normal);
        _icon.Menu = _menu;
        _icon.IsVisible = true;
        TrayIcon.SetIcons(_app, [_icon]);

        _host.Rebuilt += Subscribe;
        Subscribe();
        _host.StartPolling();
    }

    private void BuildMenuOnce()
    {
        _configErrorLine.IsVisible = false;
        _configErrorSeparator.IsVisible = false;
        _menu.Items.Add(_configErrorLine);
        _menu.Items.Add(_configErrorSeparator);

        for (var i = 0; i < _statusLines.Length; i++)
        {
            _statusLines[i] = Disabled(string.Empty);
            _statusLines[i].IsVisible = false;
            _menu.Items.Add(_statusLines[i]);
        }

        _updatedLine.IsVisible = false;
        _menu.Items.Add(_updatedLine);

        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Action("Refresh now", () => _ = _host.Poller.RefreshAsync(CancellationToken.None)));
        _menu.Items.Add(Action("Open config file", OpenConfig));
        _menu.Items.Add(Action("Reload config", _host.Reload));
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Disabled($"quota-tray {AppHost.Version}"));
        _menu.Items.Add(Action("Quit", _quit));
    }

    private void Subscribe()
    {
        if (_subscribedPoller is not null)
        {
            _subscribedPoller.Updated -= OnUpdated;
        }

        _subscribedPoller = _host.Poller;
        _subscribedPoller.Updated += OnUpdated;
    }

    private void OnUpdated(IReadOnlyList<UsageSnapshot> snapshots) =>
        Dispatcher.UIThread.Post(() => Apply(snapshots));

    private void Apply(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var config = _host.Config;
        var severity = SeverityRules.Worst(snapshots, config);
        var constrained = SeverityRules.MostConstrained(snapshots);
        var remaining = constrained?.Window.RemainingPercent;

        // An error alongside a healthy provider: keep the number, grey the ring.
        _icon.Icon = GaugeIconRenderer.Get(remaining, severity);
        _icon.ToolTipText = BuildTooltip(snapshots, constrained);
        FillMenu(snapshots);
    }

    private static string BuildTooltip(IReadOnlyList<UsageSnapshot> snapshots, (UsageSnapshot Snapshot, QuotaWindow Window)? constrained)
    {
        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        if (constrained is { } c)
        {
            sb.Append(c.Snapshot.ProviderName).Append(" · ").Append(c.Window.Label).Append(": ")
              .Append(Humanize.Percent(c.Window.RemainingPercent)).Append(" left");
            if (c.Window.ResetsAt is { } reset)
            {
                sb.Append(", resets ").Append(Humanize.Until(reset, now));
            }
        }
        else
        {
            sb.Append("quota-tray");
        }

        foreach (var s in snapshots.Where(s => s.IsError))
        {
            sb.Append('\n').Append(s.ProviderName).Append(": ").Append(s.Error);
        }

        return sb.ToString();
    }

    /// <summary>Lines the menu should show above the actions, in order.</summary>
    internal static IReadOnlyList<string> StatusLines(IReadOnlyList<UsageSnapshot> snapshots, bool anyProviderEnabled, DateTimeOffset now)
    {
        var lines = new List<string>();
        if (snapshots.Count == 0)
        {
            lines.Add(anyProviderEnabled ? "Loading…" : "No providers enabled — edit config");
            return lines;
        }

        foreach (var snapshot in snapshots)
        {
            lines.Add(snapshot.ProviderName);
            if (snapshot.IsError)
            {
                lines.Add("   ⚠ " + snapshot.Error);
            }
            else if (snapshot.Windows.Count == 0)
            {
                lines.Add("   (no limits reported)");
            }

            foreach (var w in snapshot.Windows)
            {
                var line = new StringBuilder("   ")
                    .Append(w.Label).Append(": ").Append(Humanize.Percent(w.RemainingPercent)).Append(" left");
                if (w.ResetsAt is { } reset)
                {
                    line.Append(" · resets ").Append(Humanize.Until(reset, now));
                }

                if (!string.IsNullOrEmpty(w.Detail))
                {
                    line.Append(" · ").Append(w.Detail);
                }

                lines.Add(line.ToString());
            }
        }

        return lines;
    }

    private void FillMenu(IReadOnlyList<UsageSnapshot> snapshots)
    {
        var hasConfigError = _host.ConfigError is not null;
        _configErrorLine.Header = hasConfigError ? "⚠ Config not reloaded: " + _host.ConfigError : string.Empty;
        _configErrorLine.IsVisible = hasConfigError;
        _configErrorSeparator.IsVisible = hasConfigError;

        var lines = StatusLines(snapshots, _host.Config.Providers.Any(p => p.Enabled), DateTimeOffset.UtcNow);
        for (var i = 0; i < _statusLines.Length; i++)
        {
            var show = i < lines.Count;
            if (show)
            {
                _statusLines[i].Header = i == _statusLines.Length - 1 && lines.Count > _statusLines.Length ? "…" : lines[i];
            }

            _statusLines[i].IsVisible = show;
        }

        if (snapshots.Count > 0)
        {
            var fetched = snapshots.Max(s => s.FetchedAt);
            _updatedLine.Header = $"Updated {fetched.ToLocalTime():HH:mm:ss}";
            _updatedLine.IsVisible = true;
        }
        else
        {
            _updatedLine.IsVisible = false;
        }
    }

    private void OpenConfig()
    {
        try
        {
            if (!File.Exists(_host.ConfigStore.Path))
            {
                _host.ConfigStore.Save(_host.Config);
            }

            Process.Start(new ProcessStartInfo(_host.ConfigStore.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _icon.ToolTipText = "Could not open config: " + ex.Message;
        }
    }

    private static NativeMenuItem Disabled(string text) => new(text) { IsEnabled = false };

    private static NativeMenuItem Action(string text, Action action)
    {
        var item = new NativeMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        if (_subscribedPoller is not null)
        {
            _subscribedPoller.Updated -= OnUpdated;
        }

        _host.Rebuilt -= Subscribe;
        _icon.Dispose();
    }
}
