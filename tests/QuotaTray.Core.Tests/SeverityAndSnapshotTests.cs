using QuotaTray.Core;

namespace QuotaTray.Core.Tests;

public class SeverityAndSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly AppConfig Config = new() { WarnAtRemainingPercent = 25, CriticalAtRemainingPercent = 10 };

    private static UsageSnapshot Snapshot(string name, params QuotaWindow[] windows) => new(name, name, Now, windows);

    [Fact]
    public void RemainingPercent_is_clamped()
    {
        Assert.Equal(0, new QuotaWindow("w", 130).RemainingPercent);
        Assert.Equal(100, new QuotaWindow("w", -5).RemainingPercent);
        Assert.Equal(40, new QuotaWindow("w", 60).RemainingPercent);
    }

    [Fact]
    public void MostConstrained_picks_highest_used_across_providers()
    {
        var a = Snapshot("a", new QuotaWindow("session", 20), new QuotaWindow("weekly", 55));
        var b = Snapshot("b", new QuotaWindow("spend", 80));
        var err = UsageSnapshot.Failed(new FakeProvider("c"), Now, "boom");

        var result = SeverityRules.MostConstrained([a, err, b]);

        Assert.Equal("b", result?.Snapshot.ProviderId);
        Assert.Equal("spend", result?.Window.Label);
    }

    [Theory]
    [InlineData(50, Severity.Normal)]
    [InlineData(25, Severity.Warning)]
    [InlineData(10, Severity.Critical)]
    [InlineData(0, Severity.Critical)]
    public void Thresholds_are_inclusive(double remaining, Severity expected)
    {
        var window = new QuotaWindow("w", 100 - remaining);
        Assert.Equal(expected, SeverityRules.For(window, Config));
    }

    [Fact]
    public void Error_outranks_critical()
    {
        var critical = Snapshot("a", new QuotaWindow("w", 99));
        var err = UsageSnapshot.Failed(new FakeProvider("b"), Now, "boom");

        Assert.Equal(Severity.Error, SeverityRules.Worst([critical, err], Config));
        Assert.Equal(Severity.Critical, SeverityRules.Worst([critical], Config));
        Assert.Equal(Severity.Normal, SeverityRules.Worst([], Config));
    }

    [Fact]
    public void Humanize_until_formats_sensibly()
    {
        Assert.Equal("in 2h 05m", Humanize.Until(Now.AddHours(2).AddMinutes(5), Now));
        Assert.Equal("in 3d 4h", Humanize.Until(Now.AddDays(3).AddHours(4), Now));
        Assert.Equal("in 1m", Humanize.Until(Now.AddSeconds(10), Now));
        Assert.Equal("now", Humanize.Until(Now.AddMinutes(-1), Now));
        Assert.Equal("67%", Humanize.Percent(66.6));
    }

    private sealed class FakeProvider(string id) : IUsageProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
