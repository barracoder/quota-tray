using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaTray.Core;

namespace QuotaTray.Providers.Anthropic;

/// <summary>
/// Claude Pro/Max/Team plan limits (5-hour session, weekly, per-model weekly, extra-usage credits)
/// via the same endpoint Claude Code's <c>/usage</c> command uses.
/// </summary>
/// <remarks>
/// This endpoint is not part of the documented public API and may change without notice.
/// The token is read from Claude Code's own credential store unless the user supplies one.
/// </remarks>
public sealed class ClaudeSubscriptionProviderFactory : IUsageProviderFactory
{
    public string Type => "claude-subscription";

    public string Description =>
        "Claude Pro/Max plan limits (5-hour session, weekly, extra usage) using Claude Code's login.";

    public string? DefaultApiKeyEnvironmentVariable => "CLAUDE_CODE_OAUTH_TOKEN";

    public IUsageProvider Create(ProviderConfig config, ProviderContext context) =>
        new ClaudeSubscriptionProvider(config, context, ClaudeCredentialSources.Default());
}

public sealed class ClaudeSubscriptionProvider : IUsageProvider
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ProviderConfig _config;
    private readonly ProviderContext _context;
    private readonly IReadOnlyList<IClaudeCredentialSource> _credentialSources;
    private readonly Uri _usageUri;

    public ClaudeSubscriptionProvider(
        ProviderConfig config,
        ProviderContext context,
        IReadOnlyList<IClaudeCredentialSource> credentialSources)
    {
        _config = config;
        _context = context;
        _credentialSources = credentialSources;
        var baseUrl = config.GetString("baseUrl") ?? DefaultBaseUrl;
        _usageUri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "api/oauth/usage");
    }

    public string Id => _config.EffectiveName;
    public string DisplayName => _config.EffectiveName;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var credentials = await ResolveCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var now = _context.Clock.GetUtcNow();

        if (credentials.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            throw new InvalidOperationException(
                $"Claude Code token expired {Humanize.Until(now, expiresAt).Replace("in ", "", StringComparison.Ordinal)} ago. Run `claude` once to refresh it.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _usageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _context.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Usage endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 200)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OAuthUsageResponse>(Json, cancellationToken)
                          .ConfigureAwait(false)
                      ?? throw new InvalidDataException("Usage endpoint returned an empty body.");

        return new UsageSnapshot(Id, DisplayName, now, MapWindows(payload));
    }

    private async Task<ClaudeOAuthCredentials> ResolveCredentialsAsync(CancellationToken cancellationToken)
    {
        var secret = _context.Secrets.Resolve(_config, "CLAUDE_CODE_OAUTH_TOKEN");
        if (secret is not null)
        {
            return new ClaudeOAuthCredentials(secret.Value, null, null);
        }

        foreach (var source in _credentialSources)
        {
            var creds = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (creds is not null)
            {
                return creds;
            }
        }

        var looked = string.Join(", ", _credentialSources.Select(s => s.Description));
        throw new InvalidOperationException(
            $"No Claude Code login found (looked in: {looked}). Run `claude` and sign in, or set CLAUDE_CODE_OAUTH_TOKEN.");
    }

    /// <summary>
    /// Prefer the generic <c>limits[]</c> array (one entry per enforced limit, including per-model
    /// weekly caps); fall back to the fixed <c>five_hour</c>/<c>seven_day</c> fields for older payloads.
    /// </summary>
    internal static IReadOnlyList<QuotaWindow> MapWindows(OAuthUsageResponse payload)
    {
        var windows = new List<QuotaWindow>();

        if (payload.Limits is { Count: > 0 })
        {
            foreach (var limit in payload.Limits)
            {
                if (limit.Percent is null)
                {
                    continue;
                }

                windows.Add(new QuotaWindow(LabelFor(limit), limit.Percent.Value, limit.ResetsAt,
                    limit.IsActive == true ? "currently binding" : null));
            }
        }
        else
        {
            AddBucket(windows, "Session (5h)", payload.FiveHour);
            AddBucket(windows, "Weekly", payload.SevenDay);
            AddBucket(windows, "Weekly (Opus)", payload.SevenDayOpus);
            AddBucket(windows, "Weekly (Sonnet)", payload.SevenDaySonnet);
        }

        if (payload.ExtraUsage is { IsEnabled: true } extra && extra.Utilization is { } extraPct)
        {
            string? detail = null;
            if (extra.UsedCredits is { } used && extra.MonthlyLimit is { } limit)
            {
                var scale = Math.Pow(10, extra.DecimalPlaces ?? 2);
                detail = string.Create(CultureInfo.InvariantCulture,
                    $"{used / scale:0.00} of {limit / scale:0.00} {extra.Currency}");
            }

            windows.Add(new QuotaWindow("Extra usage credits", extraPct, null, detail));
        }

        return windows;
    }

    private static void AddBucket(List<QuotaWindow> windows, string label, UsageBucket? bucket)
    {
        if (bucket?.Utilization is { } pct)
        {
            windows.Add(new QuotaWindow(label, pct, bucket.ResetsAt, bucket.LockedReason));
        }
    }

    private static string LabelFor(LimitEntry limit)
    {
        var scopeName = limit.Scope?.Model?.DisplayName ?? limit.Scope?.Surface;
        return limit.Kind switch
        {
            "session" => "Session (5h)",
            "weekly_all" => "Weekly (all models)",
            "weekly_scoped" when scopeName is not null => $"Weekly ({scopeName})",
            "weekly_scoped" => "Weekly (scoped)",
            var other when scopeName is not null => $"{Prettify(other)} ({scopeName})",
            var other => Prettify(other),
        };
    }

    private static string Prettify(string? kind) =>
        string.IsNullOrEmpty(kind) ? "Limit" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind.Replace('_', ' '));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // --- wire shapes (snake_case on the wire) ---

    internal sealed class OAuthUsageResponse
    {
        public UsageBucket? FiveHour { get; set; }
        public UsageBucket? SevenDay { get; set; }
        public UsageBucket? SevenDayOpus { get; set; }
        public UsageBucket? SevenDaySonnet { get; set; }
        public List<LimitEntry>? Limits { get; set; }
        public ExtraUsage? ExtraUsage { get; set; }
    }

    internal sealed class UsageBucket
    {
        public double? Utilization { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public string? LockedReason { get; set; }
    }

    internal sealed class LimitEntry
    {
        public string? Kind { get; set; }
        public string? Group { get; set; }
        public double? Percent { get; set; }
        public string? Severity { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public LimitScope? Scope { get; set; }
        public bool? IsActive { get; set; }
    }

    internal sealed class LimitScope
    {
        public ScopeModel? Model { get; set; }
        public string? Surface { get; set; }
    }

    internal sealed class ScopeModel
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
    }

    internal sealed class ExtraUsage
    {
        public bool IsEnabled { get; set; }
        public double? MonthlyLimit { get; set; }
        public double? UsedCredits { get; set; }
        public double? Utilization { get; set; }
        public string? Currency { get; set; }
        public int? DecimalPlaces { get; set; }
    }
}
