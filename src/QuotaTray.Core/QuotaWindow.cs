namespace QuotaTray.Core;

/// <summary>
/// One rolling or calendar window of a quota: "5-hour session", "weekly", "month-to-date spend".
/// Percentages are 0–100.
/// </summary>
public sealed record QuotaWindow(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt = null,
    string? Detail = null)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
