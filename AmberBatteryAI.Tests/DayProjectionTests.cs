using AmberBatteryAI.Models;
using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class DayProjectionTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(10);
    private static readonly DateTimeOffset Noon = new(2026, 11, 18, 12, 0, 0, Offset);

    private static LoadProfile FlatLoad(double kw) => new(Enumerable.Repeat(kw, 48).ToArray(), 7, false);

    private static List<PricePoint> Flat(double price) => new() { new PricePoint(Noon, price) };

    private static List<SocPlanner.SolarSlot> Sun(int fromHour, int toHour, double kw, double? kw10 = null, double? kw90 = null)
    {
        var slots = new List<SocPlanner.SolarSlot>();
        for (var t = Noon.Date.AddHours(fromHour); t < Noon.Date.AddHours(toHour); t = t.AddMinutes(30))
            slots.Add(new SocPlanner.SolarSlot(t, kw, kw10, kw90));
        return slots;
    }

    private static ProjectionInput Input(double soc = 50, double load = 1, double importPrice = 0.30, double feedIn = 0.10) => new()
    {
        Now = Noon,
        CapacityKwh = 40,
        StartSocPercent = soc,
        FloorKwhAt = _ => 8,
        Load = FlatLoad(load),
        ImportPrices = Flat(importPrice),
        FeedInPrices = Flat(feedIn),
        ExportThreshold = null,
        RoundTripEfficiency = 1.0
    };

    [Fact]
    public void StepsEndAtMidnightOnHalfHourBoundaries()
    {
        var steps = DayProjector.Steps(Noon.AddMinutes(10));
        Assert.Equal(20, steps[0].Hours * 60, 3);
        Assert.Equal(12 - 10.0 / 60, steps.Sum(s => s.Hours), 3);
    }

    [Fact]
    public void LoadProfileAveragesQualifyingDaysAndFallsBack()
    {
        var today = new DateOnly(2026, 11, 18);
        var samples = new List<TelemetrySample>();
        for (int day = 1; day <= 2; day++)
            for (int slot = 0; slot < 48; slot++)
                samples.Add(new TelemetrySample { Timestamp = new DateTimeOffset(2026, 11, 18 - day, slot / 2, slot % 2 * 30, 0, Offset), HomePowerW = day * 1000 });
        // A day with too little coverage and today's samples are ignored.
        samples.Add(new TelemetrySample { Timestamp = new DateTimeOffset(2026, 11, 14, 3, 0, 0, Offset), HomePowerW = 9000 });
        samples.Add(new TelemetrySample { Timestamp = new DateTimeOffset(2026, 11, 18, 3, 0, 0, Offset), HomePowerW = 9000 });

        var profile = LoadProfileBuilder.Build(samples, today, 0.75);
        Assert.Equal(2, profile.DaysUsed);
        Assert.False(profile.Fallback);
        Assert.All(profile.Kw, kw => Assert.Equal(1.5, kw, 3));

        var none = LoadProfileBuilder.Build(Array.Empty<TelemetrySample>(), today, 0.75);
        Assert.True(none.Fallback);
        Assert.Equal(0, none.DaysUsed);
        Assert.All(none.Kw, kw => Assert.Equal(0.75, kw, 3));
    }

    [Fact]
    public void PriceUsesTheSlotAverageThenTheLatestEarlierPoint()
    {
        var points = new List<PricePoint> { new(Noon, 0.10), new(Noon.AddMinutes(10), 0.20), new(Noon.AddMinutes(40), 0.50) };
        Assert.Equal(0.15, DayProjector.PriceFor(points, Noon, Noon.AddMinutes(30))!.Value, 4);
        Assert.Equal(0.20, DayProjector.PriceFor(points, Noon.AddMinutes(30), Noon.AddMinutes(35))!.Value, 4);
        Assert.Null(DayProjector.PriceFor(new List<PricePoint>(), Noon, Noon.AddMinutes(30)));
    }

    [Fact]
    public void ShortfallComesFromTheBatteryThenTheGrid()
    {
        // 12 h at 1 kW = 12 kWh; battery at 30% holds 12 kWh, 4 kWh of it above the 20% hard floor.
        var result = DayProjector.Simulate(Input(soc: 30), ProjectionScenario.Base).Totals;
        Assert.Equal(4, result.AvoidedImportKwh, 2);
        Assert.Equal(8, result.ImportKwh, 2);
        Assert.Equal(8 * 0.30, result.ImportCost, 3);
        Assert.Equal(-2.4, result.NetCash, 3);
        Assert.Equal(-2.4 + 4 * 0.30, result.NetInclAvoided, 3);
    }

    [Fact]
    public void SolarSurplusChargesTheBatteryThenExports()
    {
        // 4 kW of sun against 1 kW load for 2 h: 6 kWh surplus; the battery has 2 kWh of room, so 4 kWh is exported.
        var input = Input(soc: 95, load: 1, feedIn: 0.10) with { Solar = Sun(12, 14, 4) };
        var result = DayProjector.Simulate(input, ProjectionScenario.Base);
        Assert.Equal(4, result.Totals.ExportKwh, 2);
        Assert.Equal(0.4, result.Totals.ExportRevenue, 3);
    }

    [Fact]
    public void SolarIsCurtailedWhenTheFeedInPriceIsNotPositive()
    {
        var input = Input(soc: 100, load: 0, feedIn: -0.05) with { Solar = Sun(12, 14, 4) };
        var totals = DayProjector.Simulate(input, ProjectionScenario.Base).Totals;
        Assert.Equal(0, totals.ExportKwh);
        Assert.Equal(0, totals.ExportRevenue);
    }

    [Fact]
    public void PlannedExportSellsOnlyAboveTheFloorAndWithinThePowerLimit()
    {
        // Battery 90% = 36 kWh, floor 8 kWh + 2% margin (0.8) = 8.8 kWh stop. No load, no solar.
        var input = Input(soc: 90, load: 0, feedIn: 0.50) with { ExportThreshold = 0.40, ExportPowerKw = 5 };
        var totals = DayProjector.Simulate(input, ProjectionScenario.Base);
        Assert.Equal(36 - 8.8, totals.Totals.ExportKwh, 2);
        Assert.Equal((36 - 8.8) * 0.50, totals.Totals.ExportRevenue, 2);
        Assert.Equal(22, totals.EndSocPercent, 1);

        // Capped by 2 kW over the 12 hours when the limit is low.
        var slow = Input(soc: 90, load: 0, feedIn: 0.50) with { ExportThreshold = 0.40, ExportPowerKw = 1 };
        Assert.Equal(12, DayProjector.Simulate(slow, ProjectionScenario.Base).Totals.ExportKwh, 2);
    }

    [Fact]
    public void NoExportWhenThePriceStaysBelowTheThresholdOrExportIsOff()
    {
        var below = Input(soc: 90, load: 0, feedIn: 0.30) with { ExportThreshold = 0.40 };
        Assert.Equal(0, DayProjector.Simulate(below, ProjectionScenario.Base).Totals.ExportKwh);
        Assert.Equal(0, DayProjector.Simulate(Input(soc: 90, load: 0, feedIn: 0.90), ProjectionScenario.Base).Totals.ExportKwh);
    }

    [Fact]
    public void LossesReduceWhatTheBatteryDelivers()
    {
        var lossy = Input(soc: 30) with { RoundTripEfficiency = 0.90 };
        var perfect = Input(soc: 30);
        Assert.True(DayProjector.Simulate(lossy, ProjectionScenario.Base).Totals.ImportKwh > DayProjector.Simulate(perfect, ProjectionScenario.Base).Totals.ImportKwh);
    }

    [Fact]
    public void GridChargingOnlyHappensInChargeModeAtOrBelowTheImportThreshold()
    {
        var cheap = new List<PricePoint> { new(Noon, -0.10), new(Noon.AddHours(1), 0.30) };
        var input = Input(soc: 20, load: 0) with { ImportPrices = cheap, GridCharging = true, ImportThreshold = 0, ImportStopSocPercent = 40, ImportPowerKw = 5 };
        var totals = DayProjector.Simulate(input, ProjectionScenario.Base).Totals;
        Assert.Equal(5, totals.ImportKwh, 2); // one cheap hour at 5 kW
        Assert.True(totals.ImportCost < 0);

        var off = DayProjector.Simulate(input with { GridCharging = false }, ProjectionScenario.Base).Totals;
        Assert.Equal(0, off.ImportKwh);
    }

    [Fact]
    public void RangeBracketsTheBaseCaseUsingSolarPercentilesAndForecastBias()
    {
        var bias = new ForecastBiasReport
        {
            WindowDays = 14,
            Buckets = ForecastErrorCalculator.BucketLabels
                .Select(b => new BucketBias { Bucket = b, Count = 10, MeanError = 0.02, MeanAbsError = 0.04 }).ToList()
        };
        var input = Input(soc: 60, load: 0.5, feedIn: 0.20) with { Solar = Sun(12, 16, 3, 1, 5), Bias = bias, ExportThreshold = 0.15, ExportPowerKw = 5 };
        var realised = new RealisedTotals { Totals = ProjectionTotals.From(1, 0.2, 2, 0.6, 3, 0.9) };

        var report = DayProjector.Build(input, realised, Noon.AddHours(12), 7, false);

        Assert.True(report.Range.LowNetCash <= report.DayTotal.NetCash);
        Assert.True(report.Range.HighNetCash >= report.DayTotal.NetCash);
        Assert.True(report.Range.HighNetCash > report.Range.LowNetCash);
        Assert.Equal(realised.Totals.NetCash + report.Remainder.NetCash, report.DayTotal.NetCash, 3);
        Assert.Equal(7, report.Assumptions.LoadDaysUsed);
        Assert.Equal(14, report.Assumptions.BiasWindowDays);
    }

    [Fact]
    public void WithoutBiasLowAndHighUseOnlyTheSolarEstimates()
    {
        var input = Input(soc: 60, load: 0.5, feedIn: 0.20) with { Solar = Sun(12, 16, 3, 1, 5) };
        var report = DayProjector.Build(input, new RealisedTotals(), null, 0, true);
        Assert.True(report.Range.HighNetInclAvoided >= report.DayTotal.NetInclAvoided);
        Assert.True(report.Range.LowNetInclAvoided <= report.DayTotal.NetInclAvoided);
        Assert.Contains("fallback", report.Assumptions.LoadSource);
    }
}
