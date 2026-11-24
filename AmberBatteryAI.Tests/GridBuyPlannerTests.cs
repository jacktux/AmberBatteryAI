using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class GridBuyPlannerTests
{
    // 40 kWh battery, floor 14 kWh, conserve 25%, 10 kWh of net solar still to come.
    private static GridBuyInput In(double? buy = 0.06, double soc = 40, double cheap = 0.08, double stretch = 0.18, double solar = 10,
        double? carNeed = null, EvPriority pri = EvPriority.NiceToHave, double floor = 14) =>
        new(buy, soc, 40, floor, solar, 25, cheap, stretch, 0.25, 100, carNeed, pri);

    [Fact]
    public void OffByDefault() => Assert.False(GridBuyPlanner.Plan(In(cheap: 0, stretch: 0)).Buy);

    [Fact]
    public void CheapPriceBuysWhenThereIsRoomAndLeavesRoomForSolar()
    {
        var plan = GridBuyPlanner.Plan(In());
        Assert.True(plan.Buy, plan.Reason);
        Assert.Equal(0.08, plan.Price);
        // 10 kWh of solar = 25 points, so the target is 75%.
        Assert.Equal(75, plan.TargetSocPercent);
        Assert.Contains("cheap price", plan.Reason);
    }

    [Fact]
    public void CheapPriceDoesNotBuyWithoutRoomOrAboveThePrice()
    {
        Assert.False(GridBuyPlanner.Plan(In(soc: 74.5)).Buy);
        Assert.False(GridBuyPlanner.Plan(In(buy: 0.09, stretch: 0)).Buy);
        Assert.False(GridBuyPlanner.Plan(In(buy: null)).Buy);
    }

    [Fact]
    public void StretchPriceNeedsAShortfall()
    {
        Assert.False(GridBuyPlanner.Plan(In(buy: 0.15, soc: 60)).Buy);

        // 20% of 40 kWh = 8 kWh stored, floor 14: 6 kWh short.
        var plan = GridBuyPlanner.Plan(In(buy: 0.15, soc: 20));
        Assert.True(plan.Buy, plan.Reason);
        Assert.Equal(0.18, plan.Price);
        Assert.Equal(6, plan.ShortfallKwh, 1);
        Assert.Equal(37, plan.TargetSocPercent);
        Assert.Contains("poor solar", plan.Reason);
    }

    [Fact]
    public void StretchPriceStopsAtItsLimit() =>
        Assert.False(GridBuyPlanner.Plan(In(buy: 0.19, soc: 20)).Buy);

    [Fact]
    public void ACarThatSolarAndTheBatteryCannotCoverCountsAsAShortfall()
    {
        // stored 24 kWh, floor 14: 10 kWh spare; solar 10; car needs 40 -> 20 kWh short.
        var plan = GridBuyPlanner.Plan(In(buy: 0.15, soc: 60, carNeed: 40, solar: 10));
        Assert.True(plan.Buy, plan.Reason);
        Assert.Equal(20, plan.ShortfallKwh, 1);
        Assert.Contains("car", plan.Reason);

        Assert.False(GridBuyPlanner.Plan(In(buy: 0.15, soc: 60, carNeed: 15, solar: 10)).Buy);
        Assert.False(GridBuyPlanner.Plan(In(buy: 0.15, soc: 60, carNeed: 40, pri: EvPriority.WhenYouCan)).Buy);
    }

    [Fact]
    public void ThePriceIsRoundedUpSoAPriceAtTheLimitReallyStarts()
    {
        var plan = GridBuyPlanner.Plan(In(buy: 0.0641, cheap: 0.065));
        Assert.True(plan.Buy);
        Assert.Equal(0.07, plan.Price);
    }

    [Fact]
    public void PricesAreCappedAtTheMaximumImportPrice()
    {
        var plan = GridBuyPlanner.Plan(In(buy: 0.24, soc: 20, stretch: 0.60));
        Assert.True(plan.Buy);
        Assert.Equal(0.25, plan.Price);
        Assert.False(GridBuyPlanner.Plan(In(buy: 0.26, soc: 20, stretch: 0.60)).Buy);
    }

    [Fact]
    public void CheapAndAShortfallTogetherChargeToCoverTheShortfall()
    {
        var plan = GridBuyPlanner.Plan(In(buy: 0.05, soc: 20, solar: 40));
        Assert.True(plan.Buy);
        Assert.True(plan.TargetSocPercent >= 37, $"target {plan.TargetSocPercent}");
    }
}

public class CheapReachedTests
{
    [Theory]
    [InlineData(0.12, 0.07, true)]
    [InlineData(0.07, 0.06, false)]
    [InlineData(0.12, 0.09, false)]
    [InlineData(null, 0.07, false)]
    public void FiresOnlyWhenThePriceFallsToTheCheapPrice(double? before, double now, bool expected) =>
        Assert.Equal(expected, PriceWatchRules.CheapReached(before, now, 0.08, TimeSpan.FromMinutes(5), false).Run);

    [Fact]
    public void NeedsACheapPriceAndNoRunInProgress()
    {
        Assert.False(PriceWatchRules.CheapReached(0.12, 0.07, 0, TimeSpan.FromMinutes(5), false).Run);
        Assert.False(PriceWatchRules.CheapReached(0.12, 0.07, 0.08, TimeSpan.FromMinutes(5), true).Run);
        Assert.False(PriceWatchRules.CheapReached(0.12, 0.07, 0.08, TimeSpan.FromSeconds(20), false).Run);
    }
}

public class ForceChargeTests
{
    [Fact]
    public void ForceChargesToTheTargetAtTheMaximumPrice()
    {
        var plan = GridBuyPlanner.Force(40, 90, 100, 0.22, 0.25);
        Assert.True(plan.Buy, plan.Reason);
        Assert.Equal(0.25, plan.Price);
        Assert.Equal(90, plan.TargetSocPercent);
    }

    [Fact]
    public void ForceIsCappedByTheMaximumImportStopSoc() =>
        Assert.Equal(95, GridBuyPlanner.Force(40, 100, 95, 0.1, 0.25).TargetSocPercent);

    [Fact]
    public void ForceDoesNothingAtTheTargetOrAboveTheMaximumPrice()
    {
        var done = GridBuyPlanner.Force(89.5, 90, 100, 0.1, 0.25);
        Assert.False(done.Buy);
        Assert.Contains("already at", done.Reason);

        var dear = GridBuyPlanner.Force(40, 90, 100, 0.40, 0.25);
        Assert.False(dear.Buy);
        Assert.Contains("maximum import price", dear.Reason);

        Assert.False(GridBuyPlanner.Force(40, 90, 100, null, 0.25).Buy);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(null, true, false)]
    [InlineData(false, false, false)]
    public void TheWatcherFiresOnlyWhenTheSwitchTurnsOn(bool? was, bool now, bool expected) =>
        Assert.Equal(expected, PriceWatchRules.ForceTurnedOn(was, now, false).Run);
}