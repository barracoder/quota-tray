using System.Net;
using System.Text.Json;
using QuotaTray.Core;

namespace QuotaTray.Providers.Anthropic.Tests;

public class AnthropicCostProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);

    private static ProviderConfig Config(double? budget = 200, string? apiKey = "sk-ant-admin-test")
    {
        var settings = new Dictionary<string, JsonElement>();
        if (budget is { } b)
        {
            settings["monthlyBudgetUsd"] = JsonSerializer.SerializeToElement(b);
        }

        return new ProviderConfig { Type = "anthropic-cost", Name = "Console", ApiKey = apiKey, Settings = settings };
    }

    private static AnthropicCostProvider Build(FakeHttpMessageHandler http, ProviderConfig config) =>
        new(config, new ProviderContext(new HttpClient(http), new SecretResolver(_ => null), new FixedClock(Now)));

    [Fact]
    public async Task Sums_cents_across_pages_and_reports_against_budget()
    {
        var http = new FakeHttpMessageHandler()
            .Enqueue("""
                {"data":[{"starting_at":"2026-10-01T00:00:00Z","ending_at":"2026-10-02T00:00:00Z","results":[{"currency":"USD","amount":"1234.5"},{"currency":"USD","amount":"100"}]}],
                 "has_more":true,"next_page":"page_2"}
                """)
            .Enqueue("""
                {"data":[{"results":[{"currency":"USD","amount":"3665.5"}]}],"has_more":false,"next_page":null}
                """);

        var snapshot = await Build(http, Config()).FetchAsync(CancellationToken.None);

        Assert.Equal(2, http.Requests.Count);
        var first = http.Requests[0];
        Assert.StartsWith("https://api.anthropic.com/v1/organizations/cost_report?", first.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("starting_at=2026-10-01T00:00:00Z", first.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("ending_at=2026-10-15T09:30:00Z", first.RequestUri.Query, StringComparison.Ordinal);
        Assert.Equal("sk-ant-admin-test", first.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", first.Headers.GetValues("anthropic-version").Single());
        Assert.Contains("page=page_2", http.Requests[1].RequestUri!.Query, StringComparison.Ordinal);

        var window = Assert.Single(snapshot.Windows);
        Assert.Equal("Month-to-date spend", window.Label);
        Assert.Equal(25, window.UsedPercent, precision: 6); // $50.00 of $200
        Assert.Equal("$50.00 of $200.00 this month", window.Detail);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), window.ResetsAt);
    }

    [Fact]
    public void Missing_or_invalid_budget_fails_at_construction()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new FakeHttpMessageHandler(), Config(budget: null)));
        Assert.Contains("monthlyBudgetUsd", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Build(new FakeHttpMessageHandler(), Config(budget: 0)));
    }

    [Fact]
    public async Task Missing_key_is_a_clear_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build(new FakeHttpMessageHandler(), Config(apiKey: null)).FetchAsync(CancellationToken.None));
        Assert.Contains("ANTHROPIC_ADMIN_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_failure_surfaces_status()
    {
        var http = new FakeHttpMessageHandler().Enqueue("""{"error":{"type":"permission_error"}}""", HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Build(http, Config()).FetchAsync(CancellationToken.None));
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unparseable_amounts_are_skipped_not_fatal()
    {
        var report = new AnthropicCostProvider.CostReport
        {
            Data = [new AnthropicCostProvider.CostBucket { Results = [new() { Amount = "abc" }, new() { Amount = "12.25" }, new() { Amount = null }] }],
        };

        Assert.Equal(12.25m, AnthropicCostProvider.SumCents(report));
    }
}
