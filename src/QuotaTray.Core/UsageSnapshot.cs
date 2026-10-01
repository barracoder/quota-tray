namespace QuotaTray.Core;

/// <summary>Result of one poll of one provider.</summary>
public sealed record UsageSnapshot(
    string ProviderId,
    string ProviderName,
    DateTimeOffset FetchedAt,
    IReadOnlyList<QuotaWindow> Windows,
    string? Error = null)
{
    public bool IsError => Error is not null;

    /// <summary>The window with the least headroom, or null when there are none.</summary>
    public QuotaWindow? MostConstrained => Windows.Count == 0 ? null : Windows.MaxBy(w => w.UsedPercent);

    public static UsageSnapshot Failed(IUsageProvider provider, DateTimeOffset at, string error) =>
        new(provider.Id, provider.DisplayName, at, [], error);
}
