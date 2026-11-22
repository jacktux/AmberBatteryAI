using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class SocPlannerTests
{
    [Fact]
    public void OvernightFloorUsesTheConfiguredMorningTarget()
    {
        // 23:00: 7 h to 06:00 at 1 kW. Target 50% of 40 kWh = 20 kWh, plus 7 kWh.
        Assert.Equal(27, SocPlanner.FloorKwh(40, 1, 0, 0, 23, 50), 2);
        Assert.Equal(19, SocPlanner.FloorKwh(40, 1, 0, 0, 23), 2); // default 30%: 12 + 7
    }

    [Fact]
    public void DaytimeFloorFollowsTheTargetAndStillHonoursTheHardFloor()
    {
        Assert.Equal(8, SocPlanner.FloorKwh(40, 1, 100, 0, 10, 20), 2);
        Assert.True(SocPlanner.FloorKwh(40, 1, 0, 0, 10, 60) > SocPlanner.FloorKwh(40, 1, 0, 0, 10, 30));
    }

    [Theory]
    [InlineData(5, 20)]
    [InlineData(45, 45)]
    [InlineData(150, 100)]
    [InlineData(double.NaN, 30)]
    public void MorningTargetIsClamped(double input, double expected) =>
        Assert.Equal(expected, SocPlanner.ClampMorningTarget(input));

    private static List<SocPlanner.SolarSlot> Sun(DateTime day, int fromHour, int toHour, double kw)
    {
        var slots = new List<SocPlanner.SolarSlot>();
        for (var t = day.Date.AddHours(fromHour); t < day.Date.AddHours(toHour); t = t.AddMinutes(30))
            slots.Add(new SocPlanner.SolarSlot(t, kw));
        return slots;
    }

    [Fact]
    public void HourlyFloorIsHardFloorWhenPlentyOfSolarIsStillToCome()
    {
        var now = new DateTime(2026, 11, 17, 10, 0, 0);
        double floor = SocPlanner.HourlyFloorKwh(40, now, Sun(now, 10, 17, 8), 0.75, 1.0, 0, 0.6, 30)!.Value;
        Assert.Equal(8, floor, 2);
    }

    [Fact]
    public void HourlyFloorRisesWhenTheSolarIsTooSmallOrTooLate()
    {
        var now = new DateTime(2026, 11, 17, 10, 0, 0);
        double cloudy = SocPlanner.HourlyFloorKwh(40, now, Sun(now, 10, 17, 1.5), 0.75, 1.0, 0, 0.6, 30)!.Value;
        double noSolar = SocPlanner.HourlyFloorKwh(40, now, Sun(now, 10, 11, 0.0), 0.75, 1.0, 0, 0.6, 30)!.Value;
        Assert.True(cloudy > 8);
        Assert.True(noSolar > cloudy);
    }

    [Fact]
    public void HourlyFloorAfterSolarMatchesTheOvernightFormula()
    {
        var now = new DateTime(2026, 11, 17, 20, 0, 0);
        double floor = SocPlanner.HourlyFloorKwh(40, now, Sun(now, 10, 17, 5), 0.75, 1.0, 0, 0.6, 30)!.Value;
        // No morning forecast: the floor runs to the 07:30 fallback, 1.5 h at the 1.0 kW daytime load past 06:00.
        Assert.Equal(SocPlanner.FloorKwh(40, 0.75, 0, 0, 20, 30) + 1.5, floor, 1);
    }

    [Fact]
    public void HourlyFloorNeedsAForecastAndAddsTheHeatingReserve()
    {
        var now = new DateTime(2026, 11, 17, 10, 0, 0);
        Assert.Null(SocPlanner.HourlyFloorKwh(40, now, new List<SocPlanner.SolarSlot>(), 0.75, 1.0, 0, 0.6, 30));
        double without = SocPlanner.HourlyFloorKwh(40, now, Sun(now, 10, 17, 1.5), 0.75, 1.0, 0, 0.6, 30)!.Value;
        double with = SocPlanner.HourlyFloorKwh(40, now, Sun(now, 10, 17, 1.5), 0.75, 1.0, 2, 0.6, 30)!.Value;
        Assert.Equal(without + 2, with, 2);
    }

    [Fact]
    public void MorningLoadRaisesTheFloorForTheHoursBeforeSolarTakesOver()
    {
        var now = new DateTime(2026, 11, 17, 20, 0, 0);
        var tomorrow = now.Date.AddDays(1);
        var slots = Sun(tomorrow, 7, 17, 5);
        double without = SocPlanner.HourlyFloorKwh(40, now, slots, 0.9, 1.0, 0, 0.6, 30, 7.5)!.Value;
        double with = SocPlanner.HourlyFloorKwh(40, now, slots, 0.9, 1.0, 0, 0.6, 30, 7.5, morningLoadKw: 1.5)!.Value;

        Assert.True(with > without + 0.4, $"{with} vs {without}");
        Assert.Equal(without, SocPlanner.HourlyFloorKwh(40, now, slots, 0.9, 1.0, 0, 0.6, 30, 7.5, morningLoadKw: 0)!.Value);
    }

    [Fact]
    public void ABigMorningLoadMakesSolarTakeOverLater()
    {
        var sixAm = new DateTime(2026, 11, 18, 6, 0, 0);
        var slots = Sun(sixAm, 6, 17, 2.0);
        var normal = SocPlanner.MorningSolarTakeover(sixAm, slots, 0.9, 1.0, 0.6);
        var heavy = SocPlanner.MorningSolarTakeover(sixAm, slots, 0.9, 1.0, 0.6, morningLoadKw: 1.8);

        Assert.True(heavy > normal, $"{heavy} vs {normal}");
    }
}
