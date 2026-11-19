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
}
