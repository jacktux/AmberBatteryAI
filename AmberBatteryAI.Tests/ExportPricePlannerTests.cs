using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class ExportPricePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 17, 45, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int hour, int minute) => new(2026, 11, 17, hour, minute, 0, TimeSpan.Zero);

    // 5-minute slots to 18:25, then 30-minute slots with an evening peak around 19:30-20:30.
    private static List<ExportPricePlanner.Slot> EveningForecast(double nearPrice = 0.12)
    {
        var slots = new List<ExportPricePlanner.Slot>();
        for (var t = At(17, 50); t <= At(18, 25); t = t.AddMinutes(5))
            slots.Add(new ExportPricePlanner.Slot(t, nearPrice));

        slots.Add(new(At(18, 30), 0.100));
        slots.Add(new(At(19, 0), 0.115));
        slots.Add(new(At(19, 30), 0.123));
        slots.Add(new(At(20, 0), 0.123));
        slots.Add(new(At(20, 30), 0.120));
        slots.Add(new(At(21, 0), 0.100));
        return slots;
    }

    [Fact]
    public void FiveMinuteBlipNowStillHoldsForTheEveningPeak()
    {
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: 0.134, surplusKwh: 5);

        Assert.True(look.Hold, look.Reason);
        Assert.Equal(At(19, 30), look.LaterTime);
        Assert.Equal(0.123, look.LaterPrice);
        Assert.InRange(look.NowPrice, 0.120, 0.125);

        double price = ExportPricePlanner.ApplyLookAhead(0.110, look, 0.134, 0.134);
        Assert.True(price > 0.134, $"HOLD price {price} must stay above the live blip");
    }

    [Fact]
    public void LargerBlipNowDoesNotFlipHoldToSellNow()
    {
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: 0.20, surplusKwh: 5);

        Assert.True(look.Hold, look.Reason);
    }

    [Fact]
    public void SingleLaterFiveMinuteSlotCannotBeTheBestWindow()
    {
        var slots = new List<ExportPricePlanner.Slot>();
        for (var t = At(18, 30); t < At(20, 30); t = t.AddMinutes(5))
            slots.Add(new ExportPricePlanner.Slot(t, t == At(19, 40) ? 0.30 : 0.10));

        var look = ExportPricePlanner.CompareWithLater(slots, Now, 5.0, livePrice: 0.10);

        Assert.True(look.LaterPrice < 0.15, $"smoothed price {look.LaterPrice} should not follow the blip");
    }

    [Theory]
    [InlineData(1.0, 1.00)]
    [InlineData(2.0, 1.00)]
    [InlineData(3.0, 0.95)]
    [InlineData(4.0, 0.90)]
    [InlineData(5.0, 0.90)]
    [InlineData(10.0, 0.80)]
    [InlineData(14.0, 0.80)]
    public void HorizonFactorDiscountsFromTwoHours(double hours, double expected) =>
        Assert.Equal(expected, ExportPricePlanner.HorizonFactor(hours), 3);

    [Fact]
    public void SellNowWhenCurrentPriceBeatsLaterWindowByMargin()
    {
        var slots = new List<ExportPricePlanner.Slot>();
        for (var t = At(17, 50); t <= At(18, 25); t = t.AddMinutes(5))
            slots.Add(new ExportPricePlanner.Slot(t, 0.20));
        slots.Add(new(At(19, 30), 0.123));
        slots.Add(new(At(20, 0), 0.123));

        var look = ExportPricePlanner.CompareWithLater(slots, Now, 5.0, livePrice: 0.20, surplusKwh: 2);

        Assert.False(look.Hold);
    }

    [Fact]
    public void SellNowWhenBestWindowStartsWithinThirtyMinutes()
    {
        var slots = new List<ExportPricePlanner.Slot> { new(At(18, 15), 0.30), new(At(18, 45), 0.20) };

        var look = ExportPricePlanner.CompareWithLater(slots, Now, 5.0, livePrice: 0.10, surplusKwh: 1);

        Assert.False(look.Hold);
        Assert.Contains("started or is about to", look.Reason);
    }

    [Fact]
    public void SellNowOnSpike()
    {
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: 0.60, surplusKwh: 5);

        Assert.False(look.Hold);
        Assert.Contains("spike", look.Reason);
    }

    [Fact]
    public void SellNowWhenLaterWindowCannotCarryTheSurplus()
    {
        var forecast = EveningForecast();

        Assert.True(ExportPricePlanner.CompareWithLater(forecast, Now, 5.0, livePrice: 0.12, surplusKwh: 5).Hold);

        var look = ExportPricePlanner.CompareWithLater(forecast, Now, 5.0, livePrice: 0.12, surplusKwh: 20);
        Assert.False(look.Hold);
        Assert.Contains("cannot carry", look.Reason);
    }

    [Fact]
    public void SellNowKeepsTheBaseThreshold()
    {
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: 0.60, surplusKwh: 5);

        Assert.Equal(0.110, ExportPricePlanner.ApplyLookAhead(0.110, look, 0.60, 0.60));
    }

    [Fact]
    public void HoldPriceIsCappedAtTheHighestForecastPrice()
    {
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: 0.10, surplusKwh: 5);
        Assert.True(look.Hold, look.Reason);

        double price = ExportPricePlanner.ApplyLookAhead(0.90, look, 0.10, 0.123);

        Assert.Equal(0.123, price);
    }

    [Fact]
    public void HoldPriceIsAtLeastLivePricePlusHalfACent()
    {
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: 0.12, surplusKwh: 5);
        Assert.True(look.Hold, look.Reason);

        Assert.True(ExportPricePlanner.ApplyLookAhead(0.05, look, 0.12, 0.30) >= 0.125);
    }

    [Theory]
    [InlineData(1.20, 5, 2.0, 10000)]
    [InlineData(0.10, 5, 2.0, 2500)]
    [InlineData(0.10, 3.3, 2.0, 2000)]
    [InlineData(0.10, 5, 0.5, 5000)]
    [InlineData(0.10, 0.2, 2.0, 1000)]
    [InlineData(0.10, 5, 0, 5000)]
    public void ExportPowerLimitSpreadsSurplusOverTheWindow(double live, double surplus, double hours, int expected) =>
        Assert.Equal(expected, ExportPricePlanner.SuggestExportPowerLimit(live, surplus, hours));
}

public class ExportAutomationAlignmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 17, 45, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int h, int m) => new(2026, 11, 17, h, m, 0, TimeSpan.Zero);

    private static ExportPricePlanner.LookAhead Hold() =>
        new(true, At(19, 30), 0.123, 0.123, 0.125, 10, 1.5, "the later window pays more");

    [Fact]
    public void HoldPriceClearsLivePlusHysteresisSoARunningExportStops()
    {
        double price = ExportPricePlanner.ApplyLookAhead(0.05, Hold(), 0.134, 0.30, 0.02);

        Assert.Equal(0.159, price, 3);
        Assert.True(0.134 < price - 0.02);
    }

    [Fact]
    public void HoldPriceWithoutHysteresisKeepsTheOldBehaviour() =>
        Assert.Equal(0.139, ExportPricePlanner.ApplyLookAhead(0.05, Hold(), 0.134, 0.30), 3);

    [Fact]
    public void EffectiveSellFloorIsPriceMinusHysteresis() =>
        Assert.Equal(0.098, ExportPricePlanner.EffectiveSellFloor(0.118, 0.02), 3);

    [Theory]
    [InlineData(null, 5000)]   // no load: 5 kWh over 2 h = 2500 W
    [InlineData(0.0, 2500)]
    [InlineData(1200.0, 4000)] // 2500 + 1200 -> 3700 -> 4000
    [InlineData(4000.0, 5000)] // capped
    public void PowerLimitAddsHomeLoad(double? load, int expected)
    {
        int limit = ExportPricePlanner.SuggestExportPowerLimit(0.10, 5, 2.0, load);
        Assert.Equal(load is null ? 2500 : expected, limit);
    }

    [Fact]
    public void SpikeStillGivesTenKilowattsWhateverTheLoad() =>
        Assert.Equal(10000, ExportPricePlanner.SuggestExportPowerLimit(1.5, 5, 2.0, 3000));
}
