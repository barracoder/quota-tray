using System.Net;
using QuotaTray.Core;

namespace QuotaTray.Providers.Anthropic.Tests;

public class ClaudeSubscriptionProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    private static (ClaudeSubscriptionProvider Provider, FakeHttpMessageHandler Http) Build(
        FakeHttpMessageHandler http,
        ProviderConfig? config = null,
        Func<string, string?>? env = null,
        params IClaudeCredentialSource[] sources)
    {
        config ??= new ProviderConfig { Type = "claude-subscription", Name = "Claude" };
        var context = new ProviderContext(new HttpClient(http), new SecretResolver(env ?? (_ => null)), new FixedClock(Now));
        return (new ClaudeSubscriptionProvider(config, context, sources), http);
    }

    [Fact]
    public async Task Maps_limits_array_and_sends_oauth_headers()
    {
        var http = new FakeHttpMessageHandler().Enqueue(FakeHttpMessageHandler.Fixture("oauth-usage.json"));
        var (provider, _) = Build(http, sources: new StaticSource(new ClaudeOAuthCredentials("tok-123", Now.AddHours(1), "max")));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("tok-123", request.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta").Single());

        Assert.False(snapshot.IsError);
        Assert.Equal("Claude", snapshot.ProviderName);
        Assert.Collection(snapshot.Windows,
            w =>
            {
                Assert.Equal("Session (5h)", w.Label);
                Assert.Equal(2, w.UsedPercent);
                Assert.Equal(new DateTimeOffset(2026, 10, 1, 21, 10, 0, 308, TimeSpan.Zero).AddTicks(2780), w.ResetsAt);
            },
            w =>
            {
                Assert.Equal("Weekly (all models)", w.Label);
                Assert.Equal(94, w.RemainingPercent);
                Assert.Equal("currently binding", w.Detail);
            },
            w => Assert.Equal("Weekly (Fable)", w.Label));
        // extra_usage is disabled in this fixture, so it must not appear.
        Assert.DoesNotContain(snapshot.Windows, w => w.Label.Contains("Extra", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Falls_back_to_fixed_buckets_and_includes_enabled_extra_usage()
    {
        var http = new FakeHttpMessageHandler().Enqueue(FakeHttpMessageHandler.Fixture("oauth-usage-legacy.json"));
        var (provider, _) = Build(http, sources: new StaticSource(new ClaudeOAuthCredentials("tok", null, null)));

        var snapshot = await provider.FetchAsync(CancellationToken.None);

        Assert.Equal(["Session (5h)", "Weekly", "Weekly (Opus)", "Extra usage credits"], snapshot.Windows.Select(w => w.Label));
        Assert.Equal("model_limit", snapshot.Windows[2].Detail);
        Assert.Equal("12.50 of 50.00 USD", snapshot.Windows[3].Detail);
        Assert.Equal(90, snapshot.MostConstrained!.UsedPercent);
    }

    [Fact]
    public async Task Explicit_token_from_env_bypasses_credential_sources()
    {
        var http = new FakeHttpMessageHandler().Enqueue(FakeHttpMessageHandler.Fixture("oauth-usage.json"));
        var neverRead = new ThrowingSource();
        var (provider, _) = Build(http, env: n => n == "CLAUDE_CODE_OAUTH_TOKEN" ? "env-token" : null, sources: neverRead);

        await provider.FetchAsync(CancellationToken.None);

        Assert.Equal("env-token", http.Requests.Single().Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Expired_token_is_reported_without_calling_the_network()
    {
        var http = new FakeHttpMessageHandler();
        var (provider, _) = Build(http, sources: new StaticSource(new ClaudeOAuthCredentials("tok", Now.AddMinutes(-5), "max")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Missing_credentials_name_where_it_looked()
    {
        var (provider, _) = Build(new FakeHttpMessageHandler(), sources: new StaticSource(null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Contains("static-source", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_success_status_surfaces_code_and_body()
    {
        var http = new FakeHttpMessageHandler().Enqueue("""{"error":"revoked"}""", HttpStatusCode.Unauthorized);
        var (provider, _) = Build(http, sources: new StaticSource(new ClaudeOAuthCredentials("tok", null, null)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.FetchAsync(CancellationToken.None));

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("revoked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_claude_code_credential_blob()
    {
        var creds = ClaudeCredentialSources.Parse("""
            {"claudeAiOauth":{"accessToken":"sk-ant-oat01-xyz","refreshToken":"r","expiresAt":1790906377813,"scopes":["user:inference"],"subscriptionType":"max"}}
            """);

        Assert.NotNull(creds);
        Assert.Equal("sk-ant-oat01-xyz", creds.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790906377813), creds.ExpiresAt);
        Assert.Equal("max", creds.SubscriptionType);
        Assert.Null(ClaudeCredentialSources.Parse("""{"somethingElse":1}"""));
    }

    [Fact]
    public async Task Credentials_file_source_reads_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), "quota-tray-tests", Guid.NewGuid().ToString("N"), ".credentials.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"claudeAiOauth":{"accessToken":"file-token"}}""");
        try
        {
            var creds = await new CredentialsFileSource(path).ReadAsync(CancellationToken.None);
            Assert.Equal("file-token", creds?.AccessToken);
            Assert.Null(await new CredentialsFileSource(path + ".missing").ReadAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private sealed class StaticSource(ClaudeOAuthCredentials? creds) : IClaudeCredentialSource
    {
        public string Description => "static-source";
        public Task<ClaudeOAuthCredentials?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(creds);
    }

    private sealed class ThrowingSource : IClaudeCredentialSource
    {
        public string Description => "throwing";
        public Task<ClaudeOAuthCredentials?> ReadAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("should not be read");
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
