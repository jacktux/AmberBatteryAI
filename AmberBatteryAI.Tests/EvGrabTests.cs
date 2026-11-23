using AmberBatteryAI.Models;
using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class EvGrabTests
{
    private static EvGrabInput Input(double basePct = 90, bool hold = false, double feedIn = 0.10, bool solar = true, double floor = 30) =>
        new(basePct, floor, hold, feedIn, solar, 10, 60);

    [Fact]
    public void KeepsTheUsersValueByDefault() => Assert.Equal(90, EvThresholdPlanner.Decide(Input()).Threshold);

    [Fact]
    public void HoldRaisesTheThresholdAndCapsAtOneHundred()
    {
        Assert.Equal(95, EvThresholdPlanner.Decide(Input(basePct: 85, hold: true)).Threshold);
        Assert.Equal(100, EvThresholdPlanner.Decide(Input(basePct: 95, hold: true)).Threshold);
    }

    [Fact]
    public void ZeroFeedInLowersTheThresholdButNeverRaisesIt()
    {
        Assert.Equal(60, EvThresholdPlanner.Decide(Input(feedIn: 0)).Threshold);
        Assert.Equal(40, EvThresholdPlanner.Decide(Input(basePct: 40, feedIn: -0.02)).Threshold);
        Assert.Equal(90, EvThresholdPlanner.Decide(Input(feedIn: 0, solar: false)).Threshold);
    }

    [Fact]
    public void NeverFallsBelowTheConserveTarget()
    {
        Assert.Equal(75, EvThresholdPlanner.Decide(Input(feedIn: 0, floor: 75)).Threshold);
    }

    [Fact]
    public void CarChargingIsTakenOutOfTheLoadProfile()
    {
        var today = new DateOnly(2026, 11, 18);
        var samples = Enumerable.Range(0, 48).Select(slot => new TelemetrySample
        {
            Timestamp = new DateTimeOffset(2026, 11, 17, slot / 2, slot % 2 * 30, 0, TimeSpan.FromHours(10)),
            HomePowerW = 5000,
            EvPowerW = 4000
        }).ToList();

        Assert.All(LoadProfileBuilder.Build(samples, today, 0.75).Kw, kw => Assert.Equal(1.0, kw, 3));
        Assert.All(LoadProfileBuilder.Build(samples, today, 0.75, subtractEv: false).Kw, kw => Assert.Equal(5.0, kw, 3));
    }

    [Fact]
    public void MustChargeLowersToTheFloorAndWhenYouCanRaisesAboveBase()
    {
        Assert.Equal(30, EvThresholdPlanner.Decide(Input() with { Priority = EvPriority.MustCharge, Hold = true }).Threshold);
        Assert.Equal(100, EvThresholdPlanner.Decide(Input(basePct: 95) with { Priority = EvPriority.WhenYouCan }).Threshold);
        Assert.Equal(60, EvThresholdPlanner.Decide(Input(feedIn: 0) with { Priority = EvPriority.WhenYouCan }).Threshold);
    }

    [Fact]
    public void CarAtItsLimitKeepsTheUsersValueWhateverThePriority() =>
        Assert.Equal(90, EvThresholdPlanner.Decide(Input() with { Priority = EvPriority.MustCharge, CarAtLimit = true }).Threshold);

    [Fact]
    public void CarNeedComesFromSocLimitAndBatterySize()
    {
        var car = new EvStatus(40, 80, EvPriority.MustCharge, "Must Charge", 80);
        Assert.Equal(32, car.NeedKwh);
        Assert.Contains("needs about 32 kWh", car.PromptText());
        Assert.True(new EvStatus(80, 80, EvPriority.NiceToHave, "x", 80).AtLimit);
        Assert.Null(new EvStatus(null, 80, EvPriority.NiceToHave, "x", 80).NeedKwh);
        Assert.Equal("", new EvStatus(null, null, EvPriority.NiceToHave, "x", 80).PromptText());
        Assert.Equal(EvPriority.WhenYouCan, EvStatus.ParsePriority("Charge when you can"));
    }

    [Fact]
    public void PluggedCarCountsItsNeed()
    {
        var car = new EvStatus(40, 80, EvPriority.MustCharge, "Must Charge", 80, true);
        Assert.Equal(32, car.NeedKwh);
        Assert.False(car.AtLimit);
    }

    [Fact]
    public void UnpluggedCarNeedsNothingAndSaysSo()
    {
        var car = new EvStatus(40, 80, EvPriority.MustCharge, "Must Charge", 80, false);
        Assert.Equal(0, car.NeedKwh);
        Assert.True(car.AtLimit);
        Assert.Contains("not plugged in", car.PromptText());
    }

    [Theory]
    [InlineData(null)]
    public void UnknownOrDisabledPlugKeepsTheOldNeed(bool? plugged)
    {
        var car = new EvStatus(40, 80, EvPriority.NiceToHave, "x", 80, plugged);
        Assert.Equal(32, car.NeedKwh);
        Assert.DoesNotContain("not plugged in", car.PromptText());
    }

    [Fact]
    public void OffMeansTheCarCountsForNothing()
    {
        Assert.Equal(EvPriority.Off, EvStatus.ParsePriority("Off"));
        Assert.Equal(EvPriority.Off, EvStatus.ParsePriority(" off "));

        var car = new EvStatus(24, 80, EvPriority.Off, "Off", 81, true);
        Assert.Equal(0, car.NeedKwh);
        Assert.Contains("Off", car.PromptText());

        var d = EvThresholdPlanner.Decide(Input() with { Priority = EvPriority.Off, Hold = true, CarAtLimit = true });
        Assert.Equal(90, d.Threshold);
        Assert.Contains("Off", d.Reason);

        Assert.Equal(0, HotWaterPlanner.CarKwhUntil18(new HotWaterPlanner.CarDraw(3.7, 47, true, EvPriority.Off), 12));
        Assert.False(GridBuyPlanner.Plan(new GridBuyInput(0.15, 60, 40, 14, 10, 25, 0.08, 0.18, 0.25, 100, 40, EvPriority.Off)).Buy);
    }
}
