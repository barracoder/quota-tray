using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaTray.Core;

namespace QuotaTray.Providers.Anthropic;

/// <summary>
/// Month-to-date spend on the Claude Developer Platform (Console) against a budget you set,
/// via the Admin API cost report. Needs an Admin API key (<c>sk-ant-admin…</c>).
/// </summary>
public sealed class AnthropicCostProviderFactory : IUsageProviderFactory
{
    public string Type => "anthropic-cost";

    public string Description =>
        "Console API spend this calendar month vs. a monthlyBudgetUsd you set (Admin API key required).";

    public string? DefaultApiKeyEnvironmentVariable => "ANTHROPIC_ADMIN_KEY";

    public IUsageProvider Create(ProviderConfig config, ProviderContext context) =>
        new AnthropicCostProvider(config, context);
}

public sealed class AnthropicCostProvider : IUsageProvider
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string BudgetSetting = "monthlyBudgetUsd";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ProviderConfig _config;
    private readonly ProviderContext _context;
    private readonly Uri _baseUri;
    private readonly double _budgetUsd;

    public AnthropicCostProvider(ProviderConfig config, ProviderContext context)
    {
        _config = config;
        _context = context;
        _baseUri = new Uri((config.GetString("baseUrl") ?? DefaultBaseUrl).TrimEnd('/') + "/");
        _budgetUsd = config.GetDouble(BudgetSetting)
                     ?? throw new InvalidOperationException(
                         $"'{BudgetSetting}' is required for the '{config.Type}' provider (e.g. 200).");
        if (_budgetUsd <= 0)
        {
            throw new InvalidOperationException($"'{BudgetSetting}' must be greater than zero.");
        }
    }

    public string Id => _config.EffectiveName;
    public string DisplayName => _config.EffectiveName;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var secret = _context.Secrets.Resolve(_config, "ANTHROPIC_ADMIN_KEY")
                     ?? throw new InvalidOperationException(
                         "No Admin API key. Set apiKey/apiKeyEnv in config or export ANTHROPIC_ADMIN_KEY.");

        var now = _context.Clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var nextMonth = monthStart.AddMonths(1);

        var spentCents = 0m;
        string? page = null;
        do
        {
            var query = $"v1/organizations/cost_report?starting_at={Iso(monthStart)}&ending_at={Iso(now)}&bucket_width=1d";
            if (page is not null)
            {
                query += $"&page={Uri.EscapeDataString(page)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, query));
            request.Headers.TryAddWithoutValidation("x-api-key", secret.Value);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

            using var response = await _context.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Cost report returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 200)}");
            }

            var report = await response.Content.ReadFromJsonAsync<CostReport>(Json, cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidDataException("Cost report returned an empty body.");

            spentCents += SumCents(report);
            page = report.HasMore == true ? report.NextPage : null;
        }
        while (page is not null);

        var spentUsd = (double)spentCents / 100d;
        var usedPercent = spentUsd / _budgetUsd * 100d;
        var detail = string.Create(CultureInfo.InvariantCulture, $"${spentUsd:0.00} of ${_budgetUsd:0.00} this month");

        return new UsageSnapshot(Id, DisplayName, now,
            [new QuotaWindow("Month-to-date spend", usedPercent, nextMonth, detail)]);
    }

    /// <summary>Amounts arrive as decimal strings in cents.</summary>
    internal static decimal SumCents(CostReport report)
    {
        var total = 0m;
        foreach (var bucket in report.Data ?? [])
        {
            foreach (var result in bucket.Results ?? [])
            {
                if (decimal.TryParse(result.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var cents))
                {
                    total += cents;
                }
            }
        }

        return total;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    internal sealed class CostReport
    {
        public List<CostBucket>? Data { get; set; }
        public bool? HasMore { get; set; }
        public string? NextPage { get; set; }
    }

    internal sealed class CostBucket
    {
        public DateTimeOffset? StartingAt { get; set; }
        public DateTimeOffset? EndingAt { get; set; }
        public List<CostResult>? Results { get; set; }
    }

    internal sealed class CostResult
    {
        public string? Currency { get; set; }
        public string? Amount { get; set; }
        public string? WorkspaceId { get; set; }
        public string? Description { get; set; }
    }
}
