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
}
