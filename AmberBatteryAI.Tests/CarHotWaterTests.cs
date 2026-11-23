using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class CarHotWaterTests
{
    // 12:45, tank 76.5 C, SOC 31%, 40 kWh battery, 6 kWh net solar to 18:00, 21 kWh needed at 18:00.
    private static HotWaterPlanner.FullAssessment Check(HotWaterPlanner.CarDraw? car, double netSolar = 6, double soc = 60) =>
        HotWaterPlanner.AssessFull(76.5, soc, 40, netSolar, 21, 0.12, 12.75, 0.20, car);

    [Fact]
    public void WithoutACarTheCheckIsUnchanged() => Assert.True(Check(null, netSolar: 6).Ok);

    [Fact]
    public void AChargingCarWithPoorSolarWithholdsFull()
    {
        var r = Check(new HotWaterPlanner.CarDraw(3.7, 47, true, EvPriority.NiceToHave), netSolar: 6);
        Assert.False(r.Ok);
        Assert.Contains("car", r.Detail);
        Assert.Contains("3.7 kW", r.Detail);
    }

    [Fact]
    public void AChargingCarWithPlentyOfSolarKeepsFull()
    {
        var r = Check(new HotWaterPlanner.CarDraw(3.7, 47, true, EvPriority.NiceToHave), netSolar: 40);
        Assert.True(r.Ok, r.Detail);
        Assert.Contains("car taking", r.Detail);
    }

    [Fact]
    public void AMustChargeCarIsCountedBeforeItStartsDrawing()
    {
        Assert.False(Check(new HotWaterPlanner.CarDraw(0, 30, true, EvPriority.MustCharge)).Ok);
        Assert.True(Check(new HotWaterPlanner.CarDraw(0, 30, true, EvPriority.NiceToHave)).Ok);
    }

    [Fact]
    public void AnUnpluggedOrFullCarCountsForNothing()
    {
        Assert.Equal(0, HotWaterPlanner.CarKwhUntil18(new HotWaterPlanner.CarDraw(3.7, 47, false, EvPriority.MustCharge), 12));
        Assert.Equal(0, HotWaterPlanner.CarKwhUntil18(new HotWaterPlanner.CarDraw(3.7, 0, true, EvPriority.MustCharge), 12));
        Assert.Equal(7.4, HotWaterPlanner.CarKwhUntil18(new HotWaterPlanner.CarDraw(3.7, 47, true, EvPriority.NiceToHave), 16), 2);
        Assert.Equal(5, HotWaterPlanner.CarKwhUntil18(new HotWaterPlanner.CarDraw(3.7, 5, true, EvPriority.NiceToHave), 10), 2);
    }

    [Theory]
    [InlineData(100.0, 3700.0, "Full", true)]
    [InlineData(100.0, 3700.0, "Minimal", false)]
    [InlineData(1200.0, 3700.0, "Full", false)]
    [InlineData(100.0, 700.0, "Full", false)]
    [InlineData(null, 3700.0, "Full", false)]
    public void TheWatcherFiresOnlyWhenTheCarStartsWhileOnFull(double? before, double? now, string mode, bool expected) =>
        Assert.Equal(expected, PriceWatchRules.CarStarted(before, now, mode, TimeSpan.FromMinutes(5), false).Run);

    [Fact]
    public void TheWatcherWaitsAfterARunAndWhileRunning()
    {
        Assert.False(PriceWatchRules.CarStarted(0, 3700, "Full", TimeSpan.FromSeconds(30), false).Run);
        Assert.False(PriceWatchRules.CarStarted(0, 3700, "Full", TimeSpan.FromMinutes(5), true).Run);
    }
}

public class EvPriorityThresholdTests
{
    // Base 90, floor 30 (conserve 30), 80 kWh car, 40 kWh battery.
    private static EvGrabInput In(EvPriority p, double soc, double need = 20, double surplus = 5, double basePct = 90, bool hold = false) =>
        new(basePct, 30, hold, 0.10, true, 10, 60, p, false, soc, 40, need, surplus, 30, 20);

    [Fact]
    public void NiceToHaveIsLoweredJustEnoughToFinishBySunset()
    {
        // shortfall 15 kWh; battery 28 kWh (70%) -> keep 13 kWh -> 32.5%
        var d = EvThresholdPlanner.Decide(In(EvPriority.NiceToHave, 70));
        Assert.Equal(32.5, d.Threshold, 1);
        Assert.Contains("15", d.Reason);
    }

    [Fact]
    public void NiceToHaveKeepsTheUsersValueWhenSolarCoversTheCar()
    {
        Assert.Equal(90, EvThresholdPlanner.Decide(In(EvPriority.NiceToHave, 70, need: 10, surplus: 12)).Threshold);
        Assert.Equal(95, EvThresholdPlanner.Decide(In(EvPriority.NiceToHave, 70, need: 10, surplus: 12, basePct: 85, hold: true)).Threshold);
    }

    [Fact]
    public void NiceToHaveNeverGoesBelowTheFloorOrAboveTheBase()
    {
        var floorBound = EvThresholdPlanner.Decide(In(EvPriority.NiceToHave, 40, need: 50, surplus: 0));
        Assert.Equal(30, floorBound.Threshold);
        Assert.Contains("floor", floorBound.Reason);
        Assert.Equal(50, EvThresholdPlanner.Decide(In(EvPriority.NiceToHave, 95, need: 2, surplus: 0, basePct: 50)).Threshold);
    }

    [Fact]
    public void WhenYouCanKeepsTheBatteryFirstUntilItHasPlentyOfCharge()
    {
        Assert.Equal(100, EvThresholdPlanner.Decide(In(EvPriority.WhenYouCan, 45, basePct: 95)).Threshold);
        var plenty = EvThresholdPlanner.Decide(In(EvPriority.WhenYouCan, 70, basePct: 95));
        Assert.Equal(50, plenty.Threshold);
        Assert.Contains("plenty", plenty.Reason);
    }

    [Fact]
    public void MustChargeStillGoesToTheFloor() =>
        Assert.Equal(30, EvThresholdPlanner.Decide(In(EvPriority.MustCharge, 70)).Threshold);
}