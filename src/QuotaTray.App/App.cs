using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace QuotaTray.App;

public sealed class App : Application
{
    private AppHost? _host;
    private TrayController? _tray;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _host = AppHost.Create();
            _tray = new TrayController(this, _host, Quit);
            _tray.Start();
            desktop.Exit += (_, _) => Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Quit()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void Dispose()
    {
        _tray?.Dispose();
        _host?.Dispose();
    }
}
