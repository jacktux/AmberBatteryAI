using System.Text.Json;
using AmberBatteryAI.Models;
using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class LeverPlannerTests
{
    // Live $0.101, break-even $0.108, later window $0.12 (discounted $0.115); the look-ahead says HOLD.
    private static LeverEnvelope Env(bool codeHold = true, double? live = 0.101) =>
        new(live, 0.108, codeHold ? 0.126 : 0.108, codeHold, 0.12, 0.115, 0.13, 0.02, 4500, 8000);

    private static AiLevers L(string? strategy = null, double? trust = null, double? reserve = null, double? price = null) =>
        new() { Strategy = strategy, ForecastTrust = trust, ReserveExtraPoints = reserve, ExportPrice = price };

    [Fact]
    public void NoLeversOrNothingToSellKeepsTheLookAheadValues()
    {
        var none = LeverPlanner.Evaluate(Env(), null, canSell: true);
        Assert.True(none.Hold);
        Assert.Equal(0.126, none.ExportPrice);
        Assert.False(none.Differs);

        var cannot = LeverPlanner.Evaluate(Env(), L("SELL_NOW"), canSell: false);
        Assert.True(cannot.Hold);
        Assert.Equal(0.126, cannot.ExportPrice);
        Assert.Null(cannot.PowerLimitWatts);
    }

    [Fact]
    public void SellNowStartsAtTheLivePriceAndUsesTheSellNowLimit()
    {
        var r = LeverPlanner.Evaluate(Env(live: 0.105), L("SELL_NOW"), canSell: true);
        Assert.False(r.Hold);
        Assert.Equal(0.105, r.ExportPrice);
        Assert.Equal(8000, r.PowerLimitWatts);
        Assert.True(r.Differs);
    }

    [Fact]
    public void SellNowIsRefusedWellBelowBreakEven()
    {
        var r = LeverPlanner.Evaluate(Env(live: 0.07), L("SELL_NOW"), canSell: true);
        Assert.True(r.Hold);
        Assert.Equal(0.126, r.ExportPrice);
        Assert.Contains(r.Notes, n => n.Contains("refused"));
    }

    [Fact]
    public void HoldNeverGoesBelowLivePlusHysteresisAndMargin()
    {
        var r = LeverPlanner.Evaluate(Env(), L("HOLD", price: 0.05), canSell: true);
        Assert.True(r.Hold);
        Assert.True(r.ExportPrice >= 0.101 + 0.02 + ExportPricePlanner.HoldMarginDollars - 1e-9);
    }

    [Fact]
    public void LowTrustTurnsAHoldIntoSellNowWhenTheLaterWindowIsNoBetter()
    {
        var r = LeverPlanner.Evaluate(Env(live: 0.105), L(trust: 0.85), canSell: true);
        Assert.False(r.Hold);
        Assert.Contains(r.Notes, n => n.Contains("forecast_trust"));

        var keep = LeverPlanner.Evaluate(Env(live: 0.08), L(trust: 0.85), canSell: true);
        Assert.True(keep.Hold);
    }

    [Fact]
    public void ValuesAreLimitedToTheirRanges()
    {
        var r = LeverPlanner.Evaluate(Env(), L("hold", trust: 0.1, reserve: 50, price: 5), canSell: true);
        Assert.Equal(LeverPlanner.MaxReservePoints, r.ReservePoints);
        Assert.True(r.ExportPrice <= 0.13 + 1e-9);
        Assert.Contains(r.Notes, n => n.Contains("limited"));

        Assert.Equal(0, LeverPlanner.Evaluate(Env(), L(reserve: -4), canSell: true).ReservePoints);
        Assert.Contains(LeverPlanner.Evaluate(Env(), L("maybe"), canSell: true).Notes, n => n.Contains("not recognised"));
    }

    [Theory]
    [InlineData("SELL_NOW", "SELL_NOW")]
    [InlineData("sell now", "SELL_NOW")]
    [InlineData("Hold", "HOLD")]
    [InlineData("later", null)]
    public void StrategyNamesAreNormalised(string text, string? expected) => Assert.Equal(expected, LeverPlanner.Normalize(text));

    [Fact]
    public void ReserveOnlyRaises()
    {
        var r = LeverPlanner.Evaluate(Env(), L(reserve: 3), canSell: true);
        Assert.Equal(3, r.ReservePoints);
        Assert.True(r.Differs);
    }

    [Theory]
    [InlineData("{\"strategy\":\"HOLD\",\"forecast_trust\":\"0.9\",\"reserve_extra_points\":2,\"reason\":\"x\"}", true)]
    [InlineData("[1,2]", false)]
    [InlineData("\"nonsense\"", false)]
    public void LeversAreParsedLeniently(string json, bool parsed)
    {
        using var doc = JsonDocument.Parse(json);
        var levers = AiLevers.TryParse(doc.RootElement.Clone());
        Assert.Equal(parsed, levers is not null);
        if (parsed) Assert.Equal(0.9, levers!.ForecastTrust);
    }

    [Fact]
    public void ADecisionWithoutLeversStillParses()
    {
        var d = JsonSerializer.Deserialize<OptimizerDecision>("{\"mode\":\"SELF CONSUME\",\"export_price\":0.1,\"export_stop_soc\":30,\"export_power_limit\":5000,\"import_price\":0,\"import_stop_soc\":30,\"import_power_limit\":0,\"hot_water\":\"OFF\",\"reasoning\":\"r\"}");
        Assert.Null(d!.Levers);
        Assert.Null(AiLevers.TryParse(d.Levers));
    }

    [Theory]
    [InlineData(null, AiAuthority.Shadow)]
    [InlineData("off", AiAuthority.Off)]
    [InlineData("ON", AiAuthority.On)]
    [InlineData("whatever", AiAuthority.Shadow)]
    public void AuthorityDefaultsToShadow(string? text, AiAuthority expected) =>
        Assert.Equal(expected, AiAuthorityExtensions.Parse(text));

    [Fact]
    public void ContextSummarisesRecentRunsAccuracyAndLoad()
    {
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.FromHours(11));
        var run = new DecisionLogEntry
        {
            Timestamp = now.AddMinutes(-30), Outcome = DecisionOutcome.Applied, Trigger = "price",
            Telemetry = new TelemetrySample { SocPercent = 61.5, FeedInPrice = 0.101 },
            Applied = new DecisionApplied { ExportPrice = 0.126, ExportStopSoc = 33 },
            Computed = new DecisionComputed { LookAheadHold = true }
        };
        var bias = new ForecastBiasReport
        {
            WindowDays = 14,
            Buckets = new() { new BucketBias { Bucket = "4-8h", Count = 40, MeanError = -0.0721 }, new BucketBias { Bucket = "8h+", Count = 2, MeanError = 0.1 } }
        };
        var samples = Enumerable.Range(0, 8).Select(i => new TelemetrySample { Timestamp = now.AddMinutes(i), HomePowerW = 900 }).ToList();

        string text = AiContext.Build(now, new[] { run }, Array.Empty<DailyPnl>(), bias, samples);

        Assert.Contains("RECENT CONTEXT", text);
        Assert.Contains("[price]", text);
        Assert.Contains("4-8h -0.072 (n=40)", text);
        Assert.DoesNotContain("8h+", text);
        Assert.Contains("12h 0.9", text);
        Assert.Equal("", AiContext.Build(now, Array.Empty<DecisionLogEntry>(), Array.Empty<DailyPnl>(), null, Array.Empty<TelemetrySample>()));
    }
}