using QuotaTray.Core;

namespace QuotaTray.Core.Tests;

public class RegistryAndPollerTests
{
    private static readonly ProviderContext Context = new(new HttpClient(), new SecretResolver(_ => null), TimeProvider.System);

    [Fact]
    public async Task Unknown_type_becomes_a_visible_error_not_a_silent_drop()
    {
        var registry = new ProviderRegistry().Register(new StubFactory("stub"));
        var config = new AppConfig
        {
            Providers =
            [
                new ProviderConfig { Type = "stub", Name = "Good" },
                new ProviderConfig { Type = "nope", Name = "Bad" },
                new ProviderConfig { Type = "stub", Name = "Off", Enabled = false },
            ],
        };

        var providers = registry.Build(config, Context);

        Assert.Equal(2, providers.Count);
        Assert.Equal("good", providers[0].Id);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => providers[1].FetchAsync(CancellationToken.None));
        Assert.Contains("Unknown provider type 'nope'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("stub", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_names_get_unique_ids()
    {
        var registry = new ProviderRegistry().Register(new StubFactory("stub"));
        var config = new AppConfig
        {
            Providers = [new ProviderConfig { Type = "stub" }, new ProviderConfig { Type = "stub" }],
        };

        var ids = registry.Build(config, Context).Select(p => p.Id).ToArray();

        Assert.Equal(["stub", "stub-2"], ids);
    }

    [Fact]
    public async Task Factory_construction_errors_become_error_providers()
    {
        var registry = new ProviderRegistry().Register(new StubFactory("stub", throwOnCreate: true));
        var providers = registry.Build(new AppConfig { Providers = [new ProviderConfig { Type = "stub" }] }, Context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => providers.Single().FetchAsync(CancellationToken.None));
        Assert.Equal("bad settings", ex.Message);
    }

    [Fact]
    public void Registering_the_same_type_twice_throws()
    {
        var registry = new ProviderRegistry().Register(new StubFactory("stub"));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new StubFactory("STUB")));
    }

    [Fact]
    public async Task Poller_isolates_failures_and_timeouts()
    {
        var ok = new DelegateProvider("ok", _ => Task.FromResult(new UsageSnapshot("ok", "ok", DateTimeOffset.UtcNow, [new QuotaWindow("w", 10)])));
        var boom = new DelegateProvider("boom", _ => throw new HttpRequestException("503"));
        var slow = new DelegateProvider("slow", async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return null!;
        });

        var poller = new UsagePoller([ok, boom, slow], TimeSpan.FromMinutes(1), perProviderTimeout: TimeSpan.FromMilliseconds(100));
        IReadOnlyList<UsageSnapshot>? raised = null;
        poller.Updated += s => raised = s;

        var results = await poller.RefreshAsync(CancellationToken.None);

        Assert.Same(results, raised);
        Assert.Same(results, poller.Latest);
        Assert.False(results[0].IsError);
        Assert.Equal("503", results[1].Error);
        Assert.Contains("Timed out", results[2].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Poller_rejects_non_positive_interval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsagePoller([], TimeSpan.Zero));
    }

    private sealed class StubFactory(string type, bool throwOnCreate = false) : IUsageProviderFactory
    {
        public string Type => type;
        public string Description => "stub";
        public string? DefaultApiKeyEnvironmentVariable => null;
        public IUsageProvider Create(ProviderConfig config, ProviderContext context) =>
            throwOnCreate ? throw new InvalidOperationException("bad settings") : new DelegateProvider("inner", _ => throw new NotSupportedException());
    }

    private sealed class DelegateProvider(string id, Func<CancellationToken, Task<UsageSnapshot>> fetch) : IUsageProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken) => fetch(cancellationToken);
    }
}
