namespace QuotaTray.Core;

public enum Severity
{
    Normal,
    Warning,
    Critical,
    Error,
}

public static class SeverityRules
{
    public static Severity For(QuotaWindow window, AppConfig config)
    {
        if (window.RemainingPercent <= config.CriticalAtRemainingPercent)
        {
            return Severity.Critical;
        }

        return window.RemainingPercent <= config.WarnAtRemainingPercent ? Severity.Warning : Severity.Normal;
    }

    public static Severity For(UsageSnapshot snapshot, AppConfig config)
    {
        if (snapshot.IsError)
        {
            return Severity.Error;
        }

        return snapshot.Windows.Count == 0
            ? Severity.Normal
            : snapshot.Windows.Max(w => For(w, config));
    }

    /// <summary>
    /// Worst severity across all snapshots. Errors outrank quota states because a silent
    /// failure is worse than a loud "you are at 90%".
    /// </summary>
    public static Severity Worst(IEnumerable<UsageSnapshot> snapshots, AppConfig config)
    {
        var worst = Severity.Normal;
        foreach (var s in snapshots)
        {
            var sev = For(s, config);
            if (sev > worst)
            {
                worst = sev;
            }
        }

        return worst;
    }

    /// <summary>The single window with the least headroom across all non-error snapshots.</summary>
    public static (UsageSnapshot Snapshot, QuotaWindow Window)? MostConstrained(IEnumerable<UsageSnapshot> snapshots)
    {
        (UsageSnapshot, QuotaWindow)? best = null;
        foreach (var s in snapshots.Where(s => !s.IsError))
        {
            var w = s.MostConstrained;
            if (w is not null && (best is null || w.UsedPercent > best.Value.Item2.UsedPercent))
            {
                best = (s, w);
            }
        }

        return best;
    }
}
