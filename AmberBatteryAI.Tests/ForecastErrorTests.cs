using System.Text.Json;
using AmberBatteryAI.Models;
using AmberBatteryAI.Services;
using Xunit;

namespace AmberBatteryAI.Tests;

public class ForecastErrorTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(10);

    private static DateTimeOffset At(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, Offset);

    private static DecisionLogEntry Decision(DateTimeOffset time, params (int Hour, int Minute, double Price)[] points) => new()
    {
        Timestamp = time,
        FeedInForecast = points.Select(p =>
        {
            var target = At(time.Day, p.Hour, p.Minute);
            return new ForecastPoint { Time = target, LeadMinutes = (target - time).TotalMinutes, Price = p.Price };
        }).ToList()
    };

    private static TelemetrySample Price(DateTimeOffset time, double? price) => new() { Timestamp = time, FeedInPrice = price };

    [Theory]
    [InlineData(0, "0-1h")]
    [InlineData(59.9, "0-1h")]
    [InlineData(60, "1-2h")]
    [InlineData(120, "2-4h")]
    [InlineData(239, "2-4h")]
    [InlineData(240, "4-8h")]
    [InlineData(480, "8h+")]
    [InlineData(900, "8h+")]
    public void LeadTimeFallsIntoTheRightBucket(double minutes, string bucket) =>
        Assert.Equal(bucket, ForecastErrorCalculator.BucketFor(minutes));

    [Fact]
    public void ErrorIsActualMinusForecastAndUsesTheSlotAverage()
    {
        var decision = Decision(At(17, 10, 0), (10, 30, 0.10), (11, 0, 0.12));
        var samples = new[]
        {
            Price(At(17, 10, 30), 0.14), Price(At(17, 10, 40), 0.18), Price(At(17, 10, 50), 0.16),
            Price(At(17, 11, 0), 0.20), Price(At(17, 11, 30), 0.20)
        };

        var errors = ForecastErrorCalculator.Evaluate(new[] { decision }, samples);

        var first = errors.First(e => e.TargetTime == At(17, 10, 30));
        Assert.Equal(0.16, first.Actual, 4);
        Assert.Equal(0.06, first.Error, 4);
        Assert.Equal("0-1h", first.Bucket);
        Assert.Equal(30, first.LeadMinutes);
    }

    [Fact]
    public void SlotsThatHaveNotFinishedOrHaveNoSamplesAreLeftOut()
    {
        var decision = Decision(At(17, 10, 0), (10, 30, 0.10), (11, 0, 0.10), (13, 0, 0.10));
        var samples = new[] { Price(At(17, 10, 30), 0.12), Price(At(17, 11, 20), null), Price(At(17, 11, 40), 0.12) };

        var errors = ForecastErrorCalculator.Evaluate(new[] { decision }, samples);

        Assert.Equal(new[] { At(17, 10, 30) }, errors.Select(e => e.TargetTime));
    }

    [Fact]
    public void BiasIsTheMeanErrorPerBucketOverTheRollingWindowOnly()
    {
        var asOf = At(17, 12, 0);
        ForecastError Err(DateTimeOffset target, double lead, double error) => new()
        {
            TargetTime = target, LeadMinutes = lead, Bucket = ForecastErrorCalculator.BucketFor(lead), Error = error
        };

        var errors = new[]
        {
            Err(At(17, 9, 0), 30, 0.02),
            Err(At(17, 9, 5), 45, 0.04),
            Err(At(16, 9, 0), 300, -0.10),
            Err(At(1, 9, 0), 30, 0.50)
        };

        var report = ForecastErrorCalculator.RollingBias(errors, asOf, 14);

        var near = report.Buckets.Single(b => b.Bucket == "0-1h");
        Assert.Equal(2, near.Count);
        Assert.Equal(0.03, near.MeanError!.Value, 4);
        Assert.Equal(-0.10, report.Buckets.Single(b => b.Bucket == "4-8h").MeanError!.Value, 4);
        Assert.Null(report.Buckets.Single(b => b.Bucket == "8h+").MeanError);
        Assert.Equal(ForecastErrorCalculator.BucketLabels, report.Buckets.Select(b => b.Bucket));
    }

    [Fact]
    public void ForecastIsParsedWithLeadTimesAndSkipsPastAndFarPoints()
    {
        var now = At(17, 10, 0);
        using var doc = JsonDocument.Parse("""
            { "forecast": [
                { "time": "2026-11-17T09:55:00+10:00", "value": 0.30 },
                { "time": "2026-11-17T10:30:00+10:00", "value": 0.12 },
                { "time": "2026-11-17T11:00:00+10:00", "value": "0.15" },
                { "time": "2026-11-17T22:30:00+10:00", "value": 0.40 }
            ] }
            """);

        var points = TelemetryReader.ParseForecast(doc.RootElement, now, 6)!;

        Assert.Equal(new[] { 30.0, 60.0 }, points.Select(p => p.LeadMinutes));
        Assert.Equal(new[] { 0.12, 0.15 }, points.Select(p => p.Price));
    }
}
