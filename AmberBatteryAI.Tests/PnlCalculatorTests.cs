using AmberBatteryAI.Models;
using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class PnlCalculatorTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(10);
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(15);

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, Offset);

    private static TelemetrySample Sample(DateTimeOffset time, double? export = 0, double? import = 0, double? home = 0,
        double? feedIn = 0.10, double? importPrice = 0.30) => new()
    {
        Timestamp = time, GridExportW = export, GridImportW = import, HomePowerW = home, FeedInPrice = feedIn, ImportPrice = importPrice
    };

    [Fact]
    public void ExportKwhAndRevenueUseTheSamplePriceAndDuration()
    {
        var samples = new[] { Sample(At(17, 12, 0), export: 4000, feedIn: 0.20), Sample(At(17, 12, 10), export: 0) };

        var day = Assert.Single(PnlCalculator.Daily(samples, At(17, 12, 10), MaxGap));

        Assert.Equal(4.0 / 6, day.ExportKwh, 3);
        Assert.Equal(4.0 / 6 * 0.20, day.ExportRevenue, 3);
        Assert.Equal(0.20, day.AchievedSellPrice);
    }

    [Fact]
    public void ImportCostAndAvoidedImportAreValuedAtTheImportPrice()
    {
        var samples = new[] { Sample(At(17, 19, 0), import: 1000, home: 3000, importPrice: 0.30) };

        var day = Assert.Single(PnlCalculator.Daily(samples, At(17, 19, 12), MaxGap));

        Assert.Equal(0.2, day.ImportKwh, 3);
        Assert.Equal(0.06, day.ImportCost, 3);
        Assert.Equal(0.4, day.AvoidedImportKwh, 3);
        Assert.Equal(0.12, day.AvoidedImportValue, 3);
        Assert.Equal(-0.06, day.NetCash, 3);
        Assert.Equal(0.06, day.NetInclAvoided, 3);
    }

    [Fact]
    public void LongGapsAreCappedInsteadOfFilledIn()
    {
        var samples = new[] { Sample(At(17, 9, 0), export: 4000), Sample(At(17, 15, 0), export: 0) };

        var day = Assert.Single(PnlCalculator.Daily(samples, At(17, 15, 0), MaxGap));

        Assert.Equal(1.0, day.ExportKwh, 3);
    }

    [Fact]
    public void BestPricesComeFromThePeakAndTheBestThirtyMinuteAverage()
    {
        var samples = new[]
        {
            Sample(At(17, 18, 0), feedIn: 0.10),
            Sample(At(17, 18, 5), feedIn: 0.40),
            Sample(At(17, 18, 10), feedIn: 0.10),
            Sample(At(17, 19, 0), feedIn: 0.15)
        };

        var day = Assert.Single(PnlCalculator.Daily(samples, At(17, 19, 5), MaxGap));

        Assert.Equal(0.40, day.BestSellPrice);
        Assert.Equal(0.20, day.BestSellPrice30Min);
    }

    [Fact]
    public void MissingValuesAreSkippedAndSamplesAreGroupedByDay()
    {
        var samples = new[]
        {
            Sample(At(17, 23, 55), export: 6000, feedIn: null),
            Sample(At(18, 0, 5), export: 6000, feedIn: 0.10)
        };

        var days = PnlCalculator.Daily(samples, At(18, 0, 10), MaxGap);

        Assert.Equal(new[] { "2026-11-17", "2026-11-18" }, days.Select(d => d.Date));
        Assert.Equal(0, days[0].ExportKwh);
        Assert.Null(days[0].BestSellPrice);
        Assert.Equal(0.5, days[1].ExportKwh, 3);
    }

    [Theory]
    [InlineData(2000.0, true, 2000.0, 0.0)]
    [InlineData(-3000.0, true, 0.0, 3000.0)]
    [InlineData(2000.0, false, 0.0, 2000.0)]
    [InlineData(-3000.0, false, 3000.0, 0.0)]
    public void GridPowerIsSplitByTheConfiguredSign(double raw, bool positiveIsImport, double import, double export)
    {
        var (i, e) = TelemetryMath.SplitGrid(raw, positiveIsImport);
        Assert.Equal(import, i);
        Assert.Equal(export, e);
    }

    [Fact]
    public void MissingGridPowerGivesNoImportOrExport() =>
        Assert.Equal((null, null), TelemetryMath.SplitGrid(null, true));
}
