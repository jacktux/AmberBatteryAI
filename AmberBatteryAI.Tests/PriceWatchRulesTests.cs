using AmberBatteryAI.Models;
using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class PriceWatchRulesTests
{
    private static readonly PriceWatchRules.Settings Default = new();
    [Fact]
    public void AGapTheLastRunAlreadySawDoesNotTriggerAgain()
    {
        Assert.False(PriceWatchRules.Evaluate(0.15, 0.10, null, null, Long, false, Default, baselineFeedIn: 0.15).Run);
        Assert.True(PriceWatchRules.Evaluate(0.15, 0.10, null, null, Long, false, Default, baselineFeedIn: 0.12).Run);
        Assert.False(PriceWatchRules.Evaluate(null, null, 0.20, 0.30, Long, false, Default, baselineBuy: 0.20).Run);
        Assert.True(PriceWatchRules.Evaluate(null, null, 0.20, 0.30, Long, false, Default, baselineBuy: 0.28).Run);
    }

    private static readonly TimeSpan Long = TimeSpan.FromMinutes(30);

    private static PriceWatchRules.Decision Feed(double live, double expected, TimeSpan? since = null, bool running = false) =>
        PriceWatchRules.Evaluate(live, expected, null, null, since ?? Long, running, Default);

    private static PriceWatchRules.Decision Buy(double live, double expected, TimeSpan? since = null) =>
        PriceWatchRules.Evaluate(null, null, live, expected, since ?? Long, false, Default);

    [Theory]
    [InlineData(0.131, 0.100, true)]
    [InlineData(0.120, 0.100, true)]
    [InlineData(0.119, 0.100, false)]
    [InlineData(0.250, 0.220, true)]
    [InlineData(0.241, 0.220, false)]
    [InlineData(0.100, 0.100, false)]
    [InlineData(0.080, 0.100, false)]
    public void FeedInRunsOnlyWhenTenPercentAboveForecast(double live, double expected, bool run) =>
        Assert.Equal(run, Feed(live, expected).Run);

    [Theory]
    [InlineData(0.180, 0.200, true)]
    [InlineData(0.181, 0.200, false)]
    [InlineData(0.175, 0.200, true)]
    [InlineData(0.250, 0.200, false)]
    public void BuyRunsOnlyWhenTenPercentBelowForecast(double live, double expected, bool run) =>
        Assert.Equal(run, Buy(live, expected).Run);

    [Fact]
    public void MinimumDollarShiftAppliesToSmallPrices()
    {
        Assert.False(Feed(0.099, 0.090).Run);
        Assert.True(Feed(0.110, 0.090).Run);
        Assert.False(Buy(0.040, 0.050).Run);
    }

    [Fact]
    public void NegativeForecastUsesTheAbsoluteShift()
    {
        Assert.True(PriceWatchRules.Evaluate(null, null, -0.10, -0.05, Long, false, Default).Run);
        Assert.False(PriceWatchRules.Evaluate(null, null, -0.06, -0.05, Long, false, Default).Run);
    }

    [Fact]
    public void PriceGatesStopRunsOutsideTheUsefulRange()
    {
        Assert.False(Feed(0.07, 0.01).Run);
        Assert.False(Buy(0.30, 0.40).Run);
    }

    [Fact]
    public void CooldownBlocksUnlessSpike()
    {
        Assert.False(Feed(0.20, 0.10, TimeSpan.FromMinutes(5)).Run);
        Assert.True(Feed(0.20, 0.10, TimeSpan.FromMinutes(10)).Run);
        Assert.True(Feed(2.00, 0.10, TimeSpan.FromMinutes(2)).Run);
        Assert.False(Feed(2.00, 0.10, TimeSpan.FromSeconds(30)).Run);
    }

    [Fact]
    public void NoRunWhileRunningOrWithoutData()
    {
        Assert.False(Feed(0.20, 0.10, running: true).Run);
        Assert.False(PriceWatchRules.Evaluate(0.20, null, null, null, Long, false, Default).Run);
        Assert.False(PriceWatchRules.Evaluate(null, 0.10, null, null, Long, false, Default).Run);
    }

    private static ForecastPoint P(int hour, int minute, double price) =>
        new() { Time = new DateTimeOffset(2026, 11, 19, hour, minute, 0, TimeSpan.FromHours(11)), Price = price };

    [Fact]
    public void ExpectedPriceIsTheSlotContainingNow()
    {
        var forecast = new[] { P(17, 0, 0.10), P(17, 5, 0.11), P(17, 10, 0.12), P(17, 30, 0.13) };
        var now = new DateTimeOffset(2026, 11, 19, 17, 12, 0, TimeSpan.FromHours(11));

        Assert.Equal(0.12, PriceWatchRules.ExpectedPrice(forecast, now));
        Assert.Null(PriceWatchRules.ExpectedPrice(forecast, now.AddHours(2)));
        Assert.Null(PriceWatchRules.ExpectedPrice(null, now));
        Assert.Null(PriceWatchRules.ExpectedPrice(forecast, now.AddHours(-1)));
    }
}
