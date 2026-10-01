namespace QuotaTray.Core;

public static class Humanize
{
    /// <summary>"in 2h 05m", "in 3d 4h", "now" — never negative.</summary>
    public static string Until(DateTimeOffset target, DateTimeOffset now)
    {
        var span = target - now;
        if (span <= TimeSpan.Zero)
        {
            return "now";
        }

        if (span.TotalDays >= 1)
        {
            return $"in {(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"in {(int)span.TotalHours}h {span.Minutes:00}m";
        }

        return $"in {Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    }

    public static string Percent(double value) => $"{Math.Round(value):0}%";
}
