using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaTray.Core;

namespace QuotaTray.Providers.GitHub;

/// <summary>
/// GitHub Copilot premium-request allowance for the signed-in user, via the endpoint Copilot's
/// own editor clients call. Beta: the endpoint is internal and may change without notice.
/// </summary>
public sealed class GitHubCopilotProviderFactory : IUsageProviderFactory
{
    public string Type => "github-copilot";

    public string Description =>
        "GitHub Copilot premium requests remaining this month (and any other metered quota), using your gh login or a token.";

    public string? DefaultApiKeyEnvironmentVariable => "GITHUB_TOKEN";

    public bool IsBeta => true;

    public IUsageProvider Create(ProviderConfig config, ProviderContext context) =>
        new GitHubCopilotProvider(config, context, GitHubTokenSources.Default());
}

public sealed class GitHubCopilotProvider : IUsageProvider
{
    public const string DefaultBaseUrl = "https://api.github.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ProviderConfig _config;
    private readonly ProviderContext _context;
    private readonly IReadOnlyList<IGitHubTokenSource> _tokenSources;
    private readonly Uri _userUri;

    public GitHubCopilotProvider(ProviderConfig config, ProviderContext context, IReadOnlyList<IGitHubTokenSource> tokenSources)
    {
        _config = config;
        _context = context;
        _tokenSources = tokenSources;
        var baseUrl = config.GetString("baseUrl") ?? DefaultBaseUrl;
        _userUri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "copilot_internal/user");
    }

    public string Id => _config.EffectiveName;
    public string DisplayName => _config.EffectiveName;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var token = await ResolveTokenAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, _userUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // The endpoint is gated on looking like a Copilot client.
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.104.0");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.31.0");

        using var response = await _context.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => " Token rejected: run `gh auth login` or set GITHUB_TOKEN.",
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound => " Does this account have a Copilot subscription?",
                _ => string.Empty,
            };
            throw new HttpRequestException(
                $"Copilot endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 160)}.{hint}");
        }

        var payload = await response.Content.ReadFromJsonAsync<CopilotUser>(Json, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidDataException("Copilot endpoint returned an empty body.");

        return new UsageSnapshot(Id, DisplayName, _context.Clock.GetUtcNow(), MapWindows(payload));
    }

    private async Task<string> ResolveTokenAsync(CancellationToken cancellationToken)
    {
        var secret = _context.Secrets.Resolve(_config, "GITHUB_TOKEN");
        if (secret is not null)
        {
            return secret.Value;
        }

        foreach (var source in _tokenSources)
        {
            var token = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (token is not null)
            {
                return token;
            }
        }

        var looked = string.Join(", ", _tokenSources.Select(s => s.Description));
        throw new InvalidOperationException(
            $"No GitHub token found (looked in: GITHUB_TOKEN, {looked}). Run `gh auth login`, or set apiKey/apiKeyEnv in config.");
    }

    /// <summary>One window per metered quota; unlimited quotas are skipped.</summary>
    internal static IReadOnlyList<QuotaWindow> MapWindows(CopilotUser user)
    {
        var windows = new List<QuotaWindow>();
        if (user.QuotaSnapshots is null)
        {
            return windows;
        }

        var reset = user.QuotaResetDateUtc
                    ?? (DateTime.TryParseExact(user.QuotaResetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                        ? new DateTimeOffset(d, TimeSpan.Zero)
                        : null);

        foreach (var (id, q) in user.QuotaSnapshots.OrderBy(kv => Order(kv.Key)))
        {
            if (q.Unlimited || q.HasQuota == false)
            {
                continue;
            }

            var used = q.PercentRemaining is { } pr
                ? 100 - pr
                : q.Entitlement > 0 ? 100.0 * (q.Entitlement - q.Remaining) / q.Entitlement : 0;

            var detail = string.Create(CultureInfo.InvariantCulture, $"{q.Remaining:0} of {q.Entitlement:0}");
            if (q.OverageCount > 0)
            {
                detail += string.Create(CultureInfo.InvariantCulture, $" · {q.OverageCount:0} over");
            }

            if (windows.Count == 0 && !string.IsNullOrEmpty(user.CopilotPlan))
            {
                detail += $" · {user.CopilotPlan} plan";
            }

            windows.Add(new QuotaWindow(Label(id), used, reset, detail));
        }

        return windows;
    }

    private static int Order(string id) => id switch
    {
        "premium_interactions" => 0,
        "chat" => 1,
        "completions" => 2,
        _ => 3,
    };

    private static string Label(string id) => id switch
    {
        "premium_interactions" => "Premium requests",
        "chat" => "Chat",
        "completions" => "Completions",
        var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other.Replace('_', ' ')),
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // --- wire shapes (snake_case) ---

    internal sealed class CopilotUser
    {
        public string? Login { get; set; }
        public string? CopilotPlan { get; set; }
        public string? QuotaResetDate { get; set; }
        public DateTimeOffset? QuotaResetDateUtc { get; set; }
        public Dictionary<string, QuotaSnapshot>? QuotaSnapshots { get; set; }
    }

    internal sealed class QuotaSnapshot
    {
        public double Entitlement { get; set; }
        public double Remaining { get; set; }
        public double? PercentRemaining { get; set; }
        public bool Unlimited { get; set; }
        public bool? HasQuota { get; set; }
        public double OverageCount { get; set; }
        public bool OveragePermitted { get; set; }
    }
}
