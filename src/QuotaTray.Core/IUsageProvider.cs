namespace QuotaTray.Core;

/// <summary>
/// A configured instance of a usage source. One provider yields one
/// <see cref="UsageSnapshot"/> per poll, containing zero or more quota windows.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Stable identifier for this instance (unique within one config).</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the tray menu.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Fetch the current usage. Implementations should throw on failure; the poller
    /// converts exceptions into error snapshots so one broken provider never hides the others.
    /// </summary>
    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds <see cref="IUsageProvider"/> instances from a <see cref="ProviderConfig"/> whose
/// <see cref="ProviderConfig.Type"/> matches <see cref="Type"/>.
/// </summary>
public interface IUsageProviderFactory
{
    /// <summary>The value users put in <c>"type"</c> in config, e.g. <c>claude-subscription</c>.</summary>
    string Type { get; }

    /// <summary>One line describing what the provider measures. Shown in docs and diagnostics.</summary>
    string Description { get; }

    /// <summary>
    /// Environment variable consulted when neither <c>apiKey</c> nor <c>apiKeyEnv</c> is set.
    /// Null when the provider needs no secret.
    /// </summary>
    string? DefaultApiKeyEnvironmentVariable { get; }

    IUsageProvider Create(ProviderConfig config, ProviderContext context);
}

/// <summary>Shared services handed to every provider.</summary>
public sealed record ProviderContext(HttpClient HttpClient, ISecretResolver Secrets, TimeProvider Clock);
