using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

public record DailyPnl
{
    [JsonPropertyName("date")] public string Date { get; init; } = "";
    [JsonPropertyName("export_kwh")] public double ExportKwh { get; init; }
    [JsonPropertyName("export_revenue")] public double ExportRevenue { get; init; }
    [JsonPropertyName("import_kwh")] public double ImportKwh { get; init; }
    [JsonPropertyName("import_cost")] public double ImportCost { get; init; }
    [JsonPropertyName("avoided_import_kwh")] public double AvoidedImportKwh { get; init; }
    [JsonPropertyName("avoided_import_value")] public double AvoidedImportValue { get; init; }
    [JsonPropertyName("net_cash")] public double NetCash { get; init; }
    [JsonPropertyName("net_incl_avoided")] public double NetInclAvoided { get; init; }
    [JsonPropertyName("achieved_sell_price")] public double? AchievedSellPrice { get; init; }
    [JsonPropertyName("best_sell_price")] public double? BestSellPrice { get; init; }
    [JsonPropertyName("best_sell_price_30min")] public double? BestSellPrice30Min { get; init; }
    [JsonPropertyName("covered_hours")] public double CoveredHours { get; init; }
    [JsonPropertyName("samples")] public int Samples { get; init; }
}

/// <summary>
/// Builds the daily profit and loss from logged telemetry samples. Each sample is taken to hold until the next one,
/// for at most <c>maxGap</c>, so gaps (add-on stopped, entity missing) are not filled with made-up energy.
/// A day is the local date the sample was recorded on.
/// </summary>
public static class PnlCalculator
{
    public static List<DailyPnl> Daily(IEnumerable<TelemetrySample> samples, DateTimeOffset asOf, TimeSpan maxGap)
    {
        var ordered = samples.OrderBy(s => s.Timestamp).ToList();
        var days = new SortedDictionary<DateOnly, Day>();

        for (int i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var until = i + 1 < ordered.Count ? ordered[i + 1].Timestamp : asOf;
            var span = until - s.Timestamp;
            if (span > maxGap) span = maxGap;

            var key = DateOnly.FromDateTime(s.Timestamp.DateTime);
            if (!days.TryGetValue(key, out var day)) days[key] = day = new Day();
            day.Add(s, span.TotalHours);
        }

        return days.Select(d => d.Value.ToReport(d.Key)).ToList();
    }

    private class Day
    {
        private double _exportKwh, _exportRevenue, _importKwh, _importCost, _avoidedKwh, _avoidedValue, _hours;
        private double? _bestPrice;
        private int _samples;
        private readonly Dictionary<int, (double Sum, int Count)> _halfHours = new();

        public void Add(TelemetrySample s, double hours)
        {
            _samples++;
            if (hours > 0) _hours += hours;

            if (s.FeedInPrice is double feedIn)
            {
                _bestPrice = _bestPrice is double best ? Math.Max(best, feedIn) : feedIn;
                int bucket = s.Timestamp.Hour * 2 + s.Timestamp.Minute / 30;
                var (sum, count) = _halfHours.GetValueOrDefault(bucket);
                _halfHours[bucket] = (sum + feedIn, count + 1);
            }

            if (hours <= 0) return;

            if (s.GridExportW is double exportW && s.FeedInPrice is double sellPrice)
            {
                double kwh = exportW / 1000 * hours;
                _exportKwh += kwh;
                _exportRevenue += kwh * sellPrice;
            }

            if (s.GridImportW is double importW && s.ImportPrice is double buyPrice)
            {
                double kwh = importW / 1000 * hours;
                _importKwh += kwh;
                _importCost += kwh * buyPrice;
            }

            // Load that solar or the battery served: the house load not covered by the grid.
            if (s.HomePowerW is double homeW && s.GridImportW is double gridW && s.ImportPrice is double price)
            {
                double kwh = Math.Max(0, homeW - gridW) / 1000 * hours;
                _avoidedKwh += kwh;
                _avoidedValue += kwh * price;
            }
        }

        public DailyPnl ToReport(DateOnly date) => new()
        {
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ExportKwh = Round(_exportKwh),
            ExportRevenue = Round(_exportRevenue),
            ImportKwh = Round(_importKwh),
            ImportCost = Round(_importCost),
            AvoidedImportKwh = Round(_avoidedKwh),
            AvoidedImportValue = Round(_avoidedValue),
            NetCash = Round(_exportRevenue - _importCost),
            NetInclAvoided = Round(_exportRevenue - _importCost + _avoidedValue),
            AchievedSellPrice = _exportKwh > 0.001 ? Round(_exportRevenue / _exportKwh) : null,
            BestSellPrice = _bestPrice is double b ? Round(b) : null,
            BestSellPrice30Min = _halfHours.Count > 0 ? Round(_halfHours.Values.Max(v => v.Sum / v.Count)) : null,
            CoveredHours = Math.Round(_hours, 2),
            Samples = _samples
        };

        private static double Round(double value) => Math.Round(value, 4);
    }

    public static string ToCsv(IEnumerable<DailyPnl> days)
    {
        var sb = new StringBuilder();
        sb.AppendLine("date,export_kwh,export_revenue,import_kwh,import_cost,avoided_import_kwh,avoided_import_value,net_cash,net_incl_avoided,achieved_sell_price,best_sell_price,best_sell_price_30min,covered_hours,samples");
        foreach (var d in days)
        {
            sb.AppendLine(string.Join(',', new[]
            {
                d.Date, N(d.ExportKwh), N(d.ExportRevenue), N(d.ImportKwh), N(d.ImportCost), N(d.AvoidedImportKwh), N(d.AvoidedImportValue),
                N(d.NetCash), N(d.NetInclAvoided), N(d.AchievedSellPrice), N(d.BestSellPrice), N(d.BestSellPrice30Min),
                N(d.CoveredHours), d.Samples.ToString(CultureInfo.InvariantCulture)
            }));
        }

        return sb.ToString();
    }

    private static string N(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
}
