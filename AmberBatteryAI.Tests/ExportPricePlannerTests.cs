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
    public void LiveAboveTheEveningPeakSellsNow()
    {
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: 0.134, surplusKwh: 5);

        Assert.False(look.Hold, look.Reason);
        Assert.Equal(ExportPricePlanner.LiveMatchesLaterReason, look.Reason);
        Assert.Equal(0.123, look.LaterPrice);

        double price = ExportPricePlanner.ApplyLookAhead(0.150, look, 0.134, 0.134);
        Assert.Equal(0.134, price);
        Assert.Equal(0.110, ExportPricePlanner.ApplyLookAhead(0.110, look, 0.134, 0.134));
    }

    [Theory]
    [InlineData(0.20, false)]
    [InlineData(0.119, false)]
    [InlineData(0.117, true)]
    [InlineData(0.08, true)]
    public void HoldsOnlyWhenTheLaterWindowBeatsTheLivePriceByTheMargin(double live, bool expectedHold)
    {
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: live, surplusKwh: 5);

        Assert.Equal(expectedHold, look.Hold);
        if (expectedHold)
        {
            double price = ExportPricePlanner.ApplyLookAhead(0.110, look, live, 0.134);
            Assert.True(price > live, $"HOLD price {price} must stay above the live price");
        }
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

        Assert.True(ExportPricePlanner.CompareWithLater(forecast, Now, 5.0, livePrice: 0.11, surplusKwh: 5).Hold);

        var look = ExportPricePlanner.CompareWithLater(forecast, Now, 5.0, livePrice: 0.11, surplusKwh: 20);
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
        var look = ExportPricePlanner.CompareWithLater(EveningForecast(), Now, 5.0, livePrice: 0.11, surplusKwh: 5);
        Assert.True(look.Hold, look.Reason);

        Assert.True(ExportPricePlanner.ApplyLookAhead(0.05, look, 0.11, 0.30) >= 0.115);
    }

    private static readonly DateTimeOffset Six = At(18, 0);

    // Half-hour slots from 18:30; the first value is the 18:30 price.
    private static List<ExportPricePlanner.Slot> Later(params double[] prices) =>
        prices.Select((p, i) => new ExportPricePlanner.Slot(At(18, 30).AddMinutes(30 * i), p)).ToList();

    private static ExportPricePlanner.ExportRamp Ramp(IReadOnlyList<ExportPricePlanner.Slot> slots, double live, double surplus, double load = 0, double floor = 0.10, DateTimeOffset? now = null, double max = 15000) =>
        ExportPricePlanner.SuggestExportRamp(slots, now ?? Six, live, surplus, floor, max, load);

    [Fact]
    public void FlatPricesSellNowAtFullPowerBecauseTiesGoToTheEarlierSlot()
    {
        var ramp = Ramp(Later(0.20, 0.20, 0.20), 0.20, 10);
        Assert.Equal(15000, ramp.LimitWatts);
        Assert.Equal(7.5, ramp.CurrentKwh, 2);
    }

    [Fact]
    public void BiggerPriceLaterGivesTheSurplusToThatSlotAndTheCurrentSlotIdles()
    {
        var ramp = Ramp(Later(0.20, 1.00, 0.20), 0.20, 5);
        Assert.Equal(0, ramp.CurrentKwh);
        Assert.Equal((int)ExportPricePlanner.MinExportWatts, ramp.LimitWatts);
    }

    [Fact]
    public void LowerPriceLaterTakesTheWholeSurplusNowAtTheRateItNeeds()
    {
        var ramp = Ramp(Later(0.20, 0.20), 0.50, 5);
        Assert.Equal(9500, ramp.LimitWatts); // 5 kWh x 0.95 over half an hour
        Assert.Equal(4.75, ramp.CurrentKwh, 2);
    }

    [Fact]
    public void SurplusBeyondOneSlotSpillsIntoTheNextBestSlot()
    {
        var ramp = Ramp(Later(0.40, 0.20), 0.50, 12);
        Assert.Equal(15000, ramp.LimitWatts);
        Assert.Equal(7.5, ramp.CurrentKwh, 2);
    }

    [Fact]
    public void TinySurplusUsesTheMinimumPracticalPower()
    {
        var ramp = Ramp(Later(0.20), 0.50, 0.2);
        Assert.Equal(1000, ramp.LimitWatts);
        Assert.Equal(0.19, ramp.CurrentKwh, 2);
    }

    [Fact]
    public void HouseLoadIsAddedBecauseTheLimitIsADischargeLimit()
    {
        Assert.Equal(11500, Ramp(Later(0.20), 0.50, 5, load: 2000).LimitWatts);
        // The load also takes room from the maximum: 12 kW of export fits beside 3 kW of load.
        Assert.Equal(15000, Ramp(Later(0.20), 0.50, 20, load: 3000).LimitWatts);
    }

    [Fact]
    public void SlotsBelowTheSellFloorGetNothing()
    {
        var ramp = Ramp(Later(0.30), 0.05, 5);
        Assert.Equal(0, ramp.CurrentKwh);
        Assert.Equal(9500, Ramp(Later(0.05), 0.20, 5).LimitWatts);
        Assert.Equal(1000, Ramp(Later(0.05), 0.05, 5).LimitWatts);
    }

    [Fact]
    public void ASlotStartingBeforeTheNextRunIsNotStarved()
    {
        // 18:12 now: the 18:30 slot starts inside this 30-minute run interval and has the best price.
        var ramp = Ramp(Later(1.00, 0.20), 0.20, 5, now: At(18, 12));
        Assert.Equal(0, ramp.CurrentKwh);
        Assert.Equal(9500, ramp.LimitWatts);
    }

    private static ExportPricePlanner.ExportRamp Hold(IReadOnlyList<ExportPricePlanner.Slot> slots, double live, double surplus, double exportPrice, double load = 0) =>
        ExportPricePlanner.SuggestExportRamp(slots, Six, live, surplus, exportPrice - 0.02, 15000, load, 0.5, exportPrice);

    [Fact]
    public void HoldWithALaterSpikeIsSizedForTheSpikeSlotNotTheMinimum()
    {
        var ramp = Hold(Later(0.20, 0.70, 0.20), 0.20, 5, 0.68);
        Assert.Equal(9500, ramp.LimitWatts);
        Assert.Equal(0, ramp.CurrentKwh);
    }

    [Fact]
    public void EarlyArrivalOfThePriceStillGetsFullPowerForABigSurplus() =>
        Assert.Equal(15000, Hold(Later(0.20, 0.70, 0.70), 0.20, 12, 0.68, load: 1000).LimitWatts);

    [Fact]
    public void ThresholdAboveEveryForecastSlotIsSizedAsOneSlotSale() =>
        Assert.Equal(9500, Hold(Later(0.20, 0.30), 0.20, 5, 0.68).LimitWatts);

    [Fact]
    public void HoldWithTinySurplusStaysAtTheMinimum() =>
        Assert.Equal(1000, Hold(Later(0.20, 0.70), 0.20, 0.2, 0.68).LimitWatts);

    [Fact]
    public void NoLaterSlotsMeansNoArrivalSizing() =>
        Assert.Equal(1000, Hold(new List<ExportPricePlanner.Slot>(), 0.20, 5, 0.68).LimitWatts);

    [Fact]
    public void ThresholdAlreadyMetKeepsTheRampBehaviour() =>
        Assert.Equal(Ramp(Later(0.20, 0.20), 0.50, 5).LimitWatts,
            ExportPricePlanner.SuggestExportRamp(Later(0.20, 0.20), Six, 0.50, 5, 0.10, 15000, 0, 0.5, 0.45).LimitWatts);

    [Fact]
    public void ExtremeSpikeUsesTheFullMaximum() =>
        Assert.Equal(15000, Ramp(Later(0.20), 1.50, 1, load: 3000).LimitWatts);

    [Fact]
    public void MaximumIsConfigurable() =>
        Assert.Equal(8000, Ramp(Later(0.20), 0.50, 20, max: 8000).LimitWatts);
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
}
