using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class LookAheadBuyPlannerTests
{
    // 2026-11-26 10:00 local; battery 40 kWh; floor 16 kWh; conserve 30%.
    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0);

    private static List<SocPlanner.SolarSlot> Solar(double tomorrowKw, DateTime at)
    {
        var slots = new List<SocPlanner.SolarSlot>();
        for (var t = at.Date.AddHours(6); t < at.Date.AddHours(18); t = t.AddMinutes(30)) slots.Add(new(t, 6));
        for (var t = at.Date.AddDays(1).AddHours(6); t < at.Date.AddDays(1).AddHours(18); t = t.AddMinutes(30)) slots.Add(new(t, tomorrowKw));
        return slots;
    }

    // Half-hour buy prices from now to tomorrow 20:00: `basePrice`, with a cheap window tomorrow 11:00-13:00.
    private static List<PricePoint> Prices(double basePrice = 0.30, double cheap = 0.07, double? todayCheap = null)
    {
        var list = new List<PricePoint>();
        var off = TimeZoneInfo.Local.GetUtcOffset(Now);
        for (var t = Now; t < Now.Date.AddDays(1).AddHours(20); t = t.AddMinutes(30))
        {
            double p = basePrice;
            if (t >= Now.Date.AddDays(1).AddHours(11) && t < Now.Date.AddDays(1).AddHours(13)) p = cheap;
            if (todayCheap is double tc && t >= Now.Date.AddHours(12) && t < Now.Date.AddHours(14)) p = tc;
            list.Add(new PricePoint(new DateTimeOffset(t, off), p));
        }
        return list;
    }

    private static LookAheadBuyInput In(double soc = 50, double floor = 16, double tomorrowKw = 2, DateTime? now = null, double? buyNow = 0.30,
        double ceiling = 0.18, List<PricePoint>? prices = null, double carShort = 0) =>
        new(now ?? Now, soc, 40, floor, 30, 0.9, 1.0, 1.5, 0.6, 7.5, Solar(tomorrowKw, now ?? Now), prices ?? Prices(), buyNow, ceiling, 0.25, 100, 5, 0.01, carShort);

    [Fact]
    public void GoodSolarTomorrowMeansNoBuying()
    {
        var r = LookAheadBuyPlanner.Plan(In(tomorrowKw: 10));
        Assert.False(r.Plan.Buy);
        Assert.Contains("no shortfall", r.Plan.Reason);
        Assert.Equal(0, r.NeedKwh);
    }

    [Fact]
    public void PoorSolarTomorrowWaitsForTheCheapWindow()
    {
        // stored 20, floor 16: tonight is covered; tomorrow nets almost nothing, so the battery is short for tomorrow night.
        var r = LookAheadBuyPlanner.Plan(In());
        Assert.False(r.Plan.Buy);
        Assert.True(r.NeedKwh > 5, $"need {r.NeedKwh}");
        Assert.Equal(Now.Date.AddDays(1).AddHours(11), r.NextWindowStart);
        Assert.Contains("waiting for 11:00", r.Plan.Reason);
    }

    [Fact]
    public void BuysInsideTheChosenWindowAndTargetsTheEnergyNeeded()
    {
        var now = Now.Date.AddDays(1).AddHours(11).AddMinutes(10);
        var r = LookAheadBuyPlanner.Plan(In(soc: 50, now: now, buyNow: 0.07, prices: Prices().Where(p => p.Time.LocalDateTime + TimeSpan.FromMinutes(30) > now).ToList()));
        Assert.True(r.Plan.Buy, r.Plan.Reason);
        Assert.Equal(0.07, r.Plan.Price);
        Assert.True(r.Plan.TargetSocPercent > 52 && r.Plan.TargetSocPercent < 100, $"target {r.Plan.TargetSocPercent}");
    }

    [Fact]
    public void PoorTodayBuysTonightsShortfallInTheCheapestSlotBeforeTakeover()
    {
        // stored 8 kWh (20%) against a floor of 16 kWh; a cheap slot this afternoon beats tomorrow's.
        var r = LookAheadBuyPlanner.Plan(In(soc: 20, tomorrowKw: 10, prices: Prices(todayCheap: 0.05)));
        Assert.False(r.Plan.Buy);
        Assert.Equal(Now.Date.AddHours(12), r.NextWindowStart);
        Assert.True(r.NeedKwh >= 8 - 0.01, $"need {r.NeedKwh}");
    }

    [Fact]
    public void NeverBuysAboveTheStretchPriceAndSaysSo()
    {
        var r = LookAheadBuyPlanner.Plan(In(prices: Prices(cheap: 0.30)));
        Assert.False(r.Plan.Buy);
        Assert.Null(r.NextWindowStart);
        Assert.Contains("above the stretch price", r.Plan.Reason);

        Assert.Contains("no stretch price", LookAheadBuyPlanner.Plan(In(ceiling: 0)).Plan.Reason);
    }

    [Fact]
    public void PartlyCoveredNeedExplainsWhatIsMissing()
    {
        // Only two cheap slots (4.5 kWh at 5 kW) against a need of more than that.
        var prices = Prices().Select(p => p.Time.LocalDateTime >= Now.Date.AddDays(1).AddHours(12) && p.Time.LocalDateTime < Now.Date.AddDays(1).AddHours(13) ? p with { Price = 0.30 } : p).ToList();
        var r = LookAheadBuyPlanner.Plan(In(soc: 30, floor: 16, prices: prices));
        Assert.Contains("raise the stretch price", r.Plan.Reason);
    }

    [Fact]
    public void BuysNowWhenTheCurrentPriceIsJustUnderTheCheapestChosenSlot()
    {
        var r = LookAheadBuyPlanner.Plan(In(buyNow: 0.075));
        Assert.True(r.Plan.Buy, r.Plan.Reason);
        Assert.False(LookAheadBuyPlanner.Plan(In(buyNow: 0.09)).Plan.Buy);
    }

    [Fact]
    public void ACarShortfallAddsToTheNeed()
    {
        var without = LookAheadBuyPlanner.Plan(In(tomorrowKw: 10));
        var with = LookAheadBuyPlanner.Plan(In(tomorrowKw: 10, carShort: 6));
        Assert.Equal(0, without.NeedKwh);
        Assert.Equal(6, with.NeedKwh, 1);
    }

    [Fact]
    public void CarShortfallFollowsThePriority()
    {
        Assert.Equal(20, GridBuyPlanner.CarShortfallKwh(40, EvPriority.NiceToHave, 10, 14, 4), 1);
        Assert.Equal(0, GridBuyPlanner.CarShortfallKwh(40, EvPriority.WhenYouCan, 10, 14, 4));
        Assert.Equal(0, GridBuyPlanner.CarShortfallKwh(null, EvPriority.MustCharge, 10, 14, 4));
    }

    [Fact]
    public void ItNeverAsksForMoreThanTheBatteryCanHold()
    {
        var r = LookAheadBuyPlanner.Plan(In(soc: 98, floor: 16, tomorrowKw: 0));
        Assert.True(r.NeedKwh <= 40 * 0.02 + 0.01, $"need {r.NeedKwh}");
    }
}