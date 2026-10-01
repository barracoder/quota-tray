using System.Net.Http.Headers;
using System.Reflection;
using QuotaTray.Core;
using QuotaTray.Providers.Anthropic;
using QuotaTray.Providers.GitHub;

namespace QuotaTray.App;

/// <summary>Composition root: config + registry + HTTP + poller. Rebuilt on "Reload config".</summary>
public sealed class AppHost : IDisposable
{
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    private readonly HttpClient _http;
    private CancellationTokenSource _pollCts = new();

    private AppHost(ConfigStore store, ProviderRegistry registry, HttpClient http)
    {
        ConfigStore = store;
        Registry = registry;
        _http = http;
        Config = store.LoadOrCreate();
        Poller = BuildPoller(Config);
    }

    public ConfigStore ConfigStore { get; }
    public ProviderRegistry Registry { get; }
    public AppConfig Config { get; private set; }
    public UsagePoller Poller { get; private set; }

    /// <summary>Set when the last config load failed; the previous config stays in force.</summary>
    public string? ConfigError { get; private set; }

    public event Action? Rebuilt;

    public static AppHost Create()
    {
        var registry = new ProviderRegistry()
            .Register(new ClaudeSubscriptionProviderFactory())
            .Register(new AnthropicCostProviderFactory())
            .Register(new GitHubCopilotProviderFactory());

        var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("quota-tray", Version));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/barracoder/quota-tray)"));

        return new AppHost(new ConfigStore(ConfigStore.DefaultPath()), registry, http);
    }

    /// <summary>Starts (or restarts) the background poll loop.</summary>
    public void StartPolling()
    {
        _pollCts.Cancel();
        _pollCts.Dispose();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Poller.RunAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on reload/quit
            }
        }, token);
    }

    /// <summary>Re-reads config, rebuilds providers, restarts polling. Keeps the old config on parse failure.</summary>
    public void Reload()
    {
        try
        {
            Config = ConfigStore.Load();
            ConfigError = null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            ConfigError = ex.Message;
        }

        Poller = BuildPoller(Config);
        Rebuilt?.Invoke();
        StartPolling();
    }

    private UsagePoller BuildPoller(AppConfig config)
    {
        var context = new ProviderContext(_http, new SecretResolver(), TimeProvider.System);
        var providers = Registry.Build(config, context);
        var interval = TimeSpan.FromSeconds(Math.Max(10, config.PollIntervalSeconds));
        return new UsagePoller(providers, interval);
    }

    public void Dispose()
    {
        _pollCts.Cancel();
        _pollCts.Dispose();
        _http.Dispose();
    }
}
