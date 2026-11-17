using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

public record ForecastError
{
    [JsonPropertyName("decision_time")] public DateTimeOffset DecisionTime { get; init; }
    [JsonPropertyName("target_time")] public DateTimeOffset TargetTime { get; init; }
    [JsonPropertyName("lead_min")] public double LeadMinutes { get; init; }
    [JsonPropertyName("bucket")] public string Bucket { get; init; } = "";
    [JsonPropertyName("forecast")] public double Forecast { get; init; }
    [JsonPropertyName("actual")] public double Actual { get; init; }
    /// <summary>Actual minus forecast: positive means the forecast was too low.</summary>
    [JsonPropertyName("error")] public double Error { get; init; }
}

public record BucketBias
{
    [JsonPropertyName("bucket")] public string Bucket { get; init; } = "";
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("mean_error")] public double? MeanError { get; init; }
    [JsonPropertyName("mean_abs_error")] public double? MeanAbsError { get; init; }
}

public record ForecastBiasReport
{
    [JsonPropertyName("as_of")] public DateTimeOffset AsOf { get; init; }
    [JsonPropertyName("window_days")] public int WindowDays { get; init; }
    [JsonPropertyName("buckets")] public List<BucketBias> Buckets { get; init; } = new();
}

/// <summary>
/// Compares the feed-in forecast each decision saw with the price that actually arrived, grouped by how far ahead
/// the forecast point was. Recorded and shown only; nothing here feeds back into decisions.
/// </summary>
public static class ForecastErrorCalculator
{
    public static readonly string[] BucketLabels = { "0-1h", "1-2h", "2-4h", "4-8h", "8h+" };

    private static readonly double[] BucketEndsMinutes = { 60, 120, 240, 480 };

    public static string BucketFor(double leadMinutes)
    {
        for (int i = 0; i < BucketEndsMinutes.Length; i++)
            if (leadMinutes < BucketEndsMinutes[i]) return BucketLabels[i];
        return BucketLabels[^1];
    }

    /// <summary>
    /// One error per forecast point whose price slot has fully passed. The slot lasts until the next forecast point
    /// (5 to 60 minutes), and the actual price is the average of the samples taken inside it.
    /// </summary>
    public static List<ForecastError> Evaluate(IEnumerable<DecisionLogEntry> decisions, IEnumerable<TelemetrySample> samples)
    {
        var priced = samples.Where(s => s.FeedInPrice is not null).OrderBy(s => s.Timestamp).ToList();
        var errors = new List<ForecastError>();
        if (priced.Count == 0) return errors;

        var lastSample = priced[^1].Timestamp;
        var times = priced.Select(s => s.Timestamp).ToList();

        foreach (var decision in decisions)
        {
            var points = decision.FeedInForecast;
            if (points is null || points.Count == 0) continue;

            var ordered = points.OrderBy(p => p.Time).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var point = ordered[i];
                double slotMinutes = i + 1 < ordered.Count
                    ? (ordered[i + 1].Time - point.Time).TotalMinutes
                    : i > 0 ? (point.Time - ordered[i - 1].Time).TotalMinutes : 30;
                slotMinutes = Math.Clamp(slotMinutes, 5, 60);

                var end = point.Time.AddMinutes(slotMinutes);
                if (end > lastSample) continue;

                double sum = 0;
                int count = 0;
                for (int j = LowerBound(times, point.Time); j < priced.Count && priced[j].Timestamp < end; j++)
                {
                    sum += priced[j].FeedInPrice!.Value;
                    count++;
                }
                if (count == 0) continue;

                double actual = sum / count;
                double lead = (point.Time - decision.Timestamp).TotalMinutes;
                errors.Add(new ForecastError
                {
                    DecisionTime = decision.Timestamp,
                    TargetTime = point.Time,
                    LeadMinutes = Math.Round(lead, 1),
                    Bucket = BucketFor(lead),
                    Forecast = point.Price,
                    Actual = Math.Round(actual, 4),
                    Error = Math.Round(actual - point.Price, 4)
                });
            }
        }

        return errors;
    }

    /// <summary>Mean and mean absolute error per lead-time bucket over the errors whose target fell in the last <paramref name="days"/> days.</summary>
    public static ForecastBiasReport RollingBias(IEnumerable<ForecastError> errors, DateTimeOffset asOf, int days = 14)
    {
        var recent = errors.Where(e => e.TargetTime > asOf.AddDays(-days) && e.TargetTime <= asOf).ToList();

        var buckets = BucketLabels.Select(label =>
        {
            var inBucket = recent.Where(e => e.Bucket == label).ToList();
            return new BucketBias
            {
                Bucket = label,
                Count = inBucket.Count,
                MeanError = inBucket.Count > 0 ? Math.Round(inBucket.Average(e => e.Error), 4) : null,
                MeanAbsError = inBucket.Count > 0 ? Math.Round(inBucket.Average(e => Math.Abs(e.Error)), 4) : null
            };
        }).ToList();

        return new ForecastBiasReport { AsOf = asOf, WindowDays = days, Buckets = buckets };
    }

    public static string ToCsv(IEnumerable<ForecastError> errors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("decision_time,target_time,lead_min,bucket,forecast,actual,error");
        foreach (var e in errors)
        {
            sb.AppendLine(string.Join(',',
                e.DecisionTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                e.TargetTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                e.LeadMinutes.ToString("0.#", CultureInfo.InvariantCulture),
                e.Bucket,
                e.Forecast.ToString("0.####", CultureInfo.InvariantCulture),
                e.Actual.ToString("0.####", CultureInfo.InvariantCulture),
                e.Error.ToString("0.####", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    private static int LowerBound(List<DateTimeOffset> times, DateTimeOffset value)
    {
        int lo = 0, hi = times.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
