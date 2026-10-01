using System.Net;
using QuotaTray.Core;

namespace QuotaTray.Providers.GitHub.Tests;

public class GitHubCopilotProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private static (GitHubCopilotProvider Provider, FakeHttpMessageHandler Http) Build(
        FakeHttpMessageHandler http,
        ProviderConfig? config = null,
        Func<string, string?>? env = null,
        params IGitHubTokenSource[] sources)
    {
        config ??= new ProviderConfig { Type = "github-copilot", Name = "Copilot" };
        var context = new ProviderContext(new HttpClient(http), new SecretResolver(env ?? (_ => null)), new FixedClock(Now));
        return (new GitHubCopilotProvider(config, context, sources), http);
    }

    [Fact]
    public async Task Maps_metered_quotas_only_and_sends_copilot_client_headers()
    {
        var http = new FakeHttpMessageHandler().Enqueue(FakeHttpMessageHandler.Fixture("copilot-user.json"));
        var (provider, _) = Build(http, sources: new StaticSource("gho_test"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.github.com/copilot_internal/user", request.RequestUri!.ToString());
        Assert.Equal("token", request.Headers.Authorization!.Scheme);
        Assert.Equal("gho_test", request.Headers.Authorization.Parameter);
        Assert.StartsWith("vscode/", request.Headers.GetValues("Editor-Version").Single(), StringComparison.Ordinal);

        var window = Assert.Single(snapshot.Windows); // chat + completions are unlimited
        Assert.Equal("Premium requests", window.Label);
        Assert.Equal(0, window.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), window.ResetsAt);
        Assert.Equal("1500 of 1500 · individual plan", window.Detail);
    }

    [Fact]
    public async Task Handles_exhausted_quota_overage_missing_percent_and_unknown_ids()
    {
        var http = new FakeHttpMessageHandler().Enqueue(FakeHttpMessageHandler.Fixture("copilot-user-partial.json"));
        var (provider, _) = Build(http, sources: new StaticSource("t"));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(["Premium requests", "Completions"], snapshot.Windows.Select(w => w.Label)); // has_quota=false skipped
        Assert.Equal(100, snapshot.Windows[0].UsedPercent);
        Assert.Equal("0 of 300 · 12 over · business plan", snapshot.Windows[0].Detail);
        Assert.Equal(25, snapshot.Windows[1].UsedPercent); // no percent_remaining: derived from remaining/entitlement
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), snapshot.Windows[1].ResetsAt); // from quota_reset_date
    }

    [Fact]
    public async Task Token_precedence_is_config_then_GITHUB_TOKEN_then_sources()
    {
        var http = new FakeHttpMessageHandler()
            .Enqueue(FakeHttpMessageHandler.Fixture("copilot-user.json"))
            .Enqueue(FakeHttpMessageHandler.Fixture("copilot-user.json"))
            .Enqueue(FakeHttpMessageHandler.Fixture("copilot-user.json"));

        var (fromConfig, _) = Build(http, new ProviderConfig { Type = "github-copilot", ApiKey = "cfg" }, n => n == "GITHUB_TOKEN" ? "env" : null, new StaticSource("src"));
        await fromConfig.FetchAsync(CancellationToken.None);
        var (fromEnv, _) = Build(http, env: n => n == "GITHUB_TOKEN" ? "env" : null, sources: new StaticSource("src"));
        await fromEnv.FetchAsync(CancellationToken.None);
        var (fromSource, _) = Build(http, null, null, new StaticSource(null), new StaticSource("src"));
        await fromSource.FetchAsync(CancellationToken.None);

        Assert.Equal(["cfg", "env", "src"], http.Requests.Select(r => r.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task No_token_anywhere_is_actionable()
    {
        var (provider, http) = Build(new FakeHttpMessageHandler(), sources: new StaticSource(null));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Contains("gh auth login", ex.Message, StringComparison.Ordinal);
        Assert.Contains("static", ex.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "gh auth login")]
    [InlineData(HttpStatusCode.NotFound, "Copilot subscription")]
    [InlineData(HttpStatusCode.Forbidden, "Copilot subscription")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    public async Task Http_errors_carry_a_hint(HttpStatusCode status, string expected)
    {
        var http = new FakeHttpMessageHandler().Enqueue("""{"message":"nope"}""", status);
        var (provider, _) = Build(http, sources: new StaticSource("t"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_token_source_trims_and_treats_blank_as_missing()
    {
        Assert.Equal("abc", await new EnvironmentTokenSource("X", _ => " abc ").ReadAsync(CancellationToken.None));
        Assert.Null(await new EnvironmentTokenSource("X", _ => "  ").ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Gh_cli_source_returns_null_when_gh_is_missing()
    {
        Assert.Null(await new GhCliTokenSource("definitely-not-a-real-executable-" + Guid.NewGuid()).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void Factory_is_beta_and_defaults_to_GITHUB_TOKEN()
    {
        var factory = new GitHubCopilotProviderFactory();
        Assert.True(factory.IsBeta);
        Assert.Equal("github-copilot", factory.Type);
        Assert.Equal("GITHUB_TOKEN", factory.DefaultApiKeyEnvironmentVariable);
    }

    private sealed class StaticSource(string? token) : IGitHubTokenSource
    {
        public string Description => "static";
        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
