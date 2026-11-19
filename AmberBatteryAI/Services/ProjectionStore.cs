using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmberBatteryAI.Services;

/// <summary>
/// Keeps the latest day projection (day-projection.json) and a log of every run's projection
/// (projections-yyyy-MM-dd.jsonl, one line per optimizer run) next to the decision log, so the estimate can later be
/// compared with the realised daily P&amp;L. Retention follows the decision log.
/// </summary>
public class ProjectionStore
{
    private const string LatestFile = "day-projection.json";
    private const string LogPrefix = "projections-";
    private const string LogExtension = ".jsonl";
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions Compact = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly ILogger<ProjectionStore> _logger;
    private readonly object _lock = new();
    private DateOnly _lastPruned;

    public ProjectionStore(PromptStorageService promptStorage, IConfiguration config, ILogger<ProjectionStore> logger)
    {
        _logger = logger;
        _directory = Path.GetDirectoryName(promptStorage.GetStoragePath()) ?? Path.GetTempPath();
        _retentionDays = Math.Max(1, config.GetValue("Optimizer:DecisionLogRetentionDays", 30));
    }

    /// <summary>Saves the projection as the latest one and appends it to the day's log. Never throws.</summary>
    public void Save(DayProjection projection)
    {
        try
        {
            lock (_lock)
            {
                File.WriteAllText(Path.Combine(_directory, LatestFile), JsonSerializer.Serialize(projection, Indented), Utf8NoBom);
                File.AppendAllText(LogPath(DateOnly.FromDateTime(projection.GeneratedAt.DateTime)), JsonSerializer.Serialize(projection, Compact) + "\n", Utf8NoBom);
                Prune();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the day projection.");
        }
    }

    public DayProjection? ReadLatest()
    {
        try
        {
            string path = Path.Combine(_directory, LatestFile);
            lock (_lock)
                return File.Exists(path) ? JsonSerializer.Deserialize<DayProjection>(File.ReadAllText(path, Utf8NoBom)) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the day projection.");
            return null;
        }
    }

    /// <summary>Every logged projection for today and the previous days-1 days, oldest first.</summary>
    public List<DayProjection> ReadLog(int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var entries = new List<DayProjection>();
        lock (_lock)
        {
            for (var day = today.AddDays(-(Math.Clamp(days, 1, 365) - 1)); day <= today; day = day.AddDays(1))
            {
                string path = LogPath(day);
                if (!File.Exists(path)) continue;
                foreach (string line in File.ReadLines(path, Utf8NoBom))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<DayProjection>(line);
                        if (entry is not null) entries.Add(entry);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Skipping an unreadable line in {Path}.", path);
                    }
                }
            }
        }

        return entries;
    }

    private string LogPath(DateOnly day) =>
        Path.Combine(_directory, LogPrefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + LogExtension);

    private void Prune()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_lastPruned == today) return;
        _lastPruned = today;

        var cutoff = today.AddDays(-_retentionDays);
        foreach (string file in Directory.EnumerateFiles(_directory, LogPrefix + "*" + LogExtension))
        {
            string name = Path.GetFileNameWithoutExtension(file)[LogPrefix.Length..];
            if (!DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day >= cutoff)
                continue;

            try { File.Delete(file); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete old projection file {File}.", file); }
        }
    }
}

/// <summary>What the worker knows after applying a decision, used to project the rest of the day.</summary>
public record ProjectionContext
{
    public double CapacityKwh { get; init; }
    public double SocPercent { get; init; }
    public double FloorKwh { get; init; }
    public double AvgLoadKw { get; init; }
    public double DaytimeLoadKw { get; init; }
    public double HeatingReserveKwh { get; init; }
    public double ConservePercent { get; init; }
    public double SolarConfidence { get; init; }
    public double MorningSolarStartHour { get; init; }
    public IReadOnlyList<SocPlanner.SolarSlot> SolarSlots { get; init; } = Array.Empty<SocPlanner.SolarSlot>();
    public double ExportPrice { get; init; }
    public double ExportPowerKw { get; init; }
    public bool GridCharging { get; init; }
    public double ImportPrice { get; init; }
    public double ImportStopSoc { get; init; }
    public double ImportPowerKw { get; init; }
    public string GeneralEntity { get; init; } = "";
    public string FeedInEntity { get; init; } = "";
}

/// <summary>Builds the day projection after each optimizer run and stores it. Read-only: it never writes to Home Assistant.</summary>
public class DayProjectionService(TelemetryHistoryService telemetry, ProjectionStore store, IConfiguration config, ILogger<DayProjectionService> logger)
{
    public async Task UpdateAsync(HttpClient ha, ProjectionContext ctx, CancellationToken ct)
    {
        if (!config.GetValue("Projection:Enabled", true)) return;

        try
        {
            var now = DateTimeOffset.Now;
            double hoursToMidnight = (new DateTimeOffset(now.Date.AddDays(1), now.Offset) - now).TotalHours + 1;

            var importPrices = await ReadPricesAsync(ha, ctx.GeneralEntity, now, hoursToMidnight + 24, ct);
            var feedInPrices = await ReadPricesAsync(ha, ctx.FeedInEntity, now, hoursToMidnight + 24, ct);
            if (importPrices.Count == 0 || feedInPrices.Count == 0)
            {
                logger.LogInformation("Day projection skipped: no Amber price forecast available.");
                return;
            }

            int days = Math.Clamp(config.GetValue("Projection:LoadDays", LoadProfileBuilder.DefaultDays), 1, 30);
            var samples = telemetry.Read(days + 1);
            var load = LoadProfileBuilder.Build(samples, DateOnly.FromDateTime(now.DateTime), ctx.AvgLoadKw, days, subtractEv: config.GetValue("Ev:SubtractFromLoad", true));

            string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var pnl = telemetry.BuildDailyPnl(1).FirstOrDefault(d => d.Date == today);
            var realised = new RealisedTotals
            {
                Totals = pnl is null ? new ProjectionTotals() : ProjectionTotals.From(pnl.ExportKwh, pnl.ExportRevenue, pnl.ImportKwh, pnl.ImportCost, pnl.AvoidedImportKwh, pnl.AvoidedImportValue),
                CoveredHours = pnl?.CoveredHours ?? 0,
                Samples = pnl?.Samples ?? 0
            };

            double FloorAt(DateTime t) =>
                SocPlanner.HourlyFloorKwh(ctx.CapacityKwh, t, ctx.SolarSlots, ctx.AvgLoadKw, ctx.DaytimeLoadKw,
                    t.TimeOfDay < TimeSpan.FromHours(16.5) ? ctx.HeatingReserveKwh : 0, ctx.SolarConfidence, ctx.ConservePercent, ctx.MorningSolarStartHour) ?? ctx.FloorKwh;

            var bias = telemetry.ReadBias();
            var input = new ProjectionInput
            {
                Now = now,
                CapacityKwh = ctx.CapacityKwh,
                StartSocPercent = ctx.SocPercent,
                // The first step uses the floor the optimizer just computed; later steps use the floor as it will be then.
                FloorKwhAt = t => t <= now.DateTime.AddMinutes(30) ? ctx.FloorKwh : FloorAt(t),
                Load = load,
                Solar = ctx.SolarSlots,
                ImportPrices = importPrices,
                FeedInPrices = feedInPrices,
                ExportThreshold = ctx.ExportPrice,
                ExportPowerKw = ctx.ExportPowerKw,
                GridCharging = ctx.GridCharging,
                ImportThreshold = ctx.ImportPrice,
                ImportStopSocPercent = ctx.ImportStopSoc,
                ImportPowerKw = ctx.ImportPowerKw,
                SolarConfidence = ctx.SolarConfidence,
                Bias = bias
            };

            var notes = new List<string>
            {
                "Estimate only. Not applied to any decision.",
                "Battery energy above the floor is sold when feed-in reaches the applied export price, at the applied export power limit.",
                "Car charging is not projected; it is taken out of the historical house load.",
                "Solar is curtailed, not exported, while the feed-in price is zero or negative.",
                "Energy left in the battery at midnight is not valued; see end_soc_percent.",
                bias is null ? "No forecast bias report yet: the range uses Solcast low/high estimates only." : "Range: Solcast 10%/90% solar and feed-in forecast bias plus/minus its average error."
            };
            var midnight = new DateTimeOffset(now.Date.AddDays(1), now.Offset);
            var tomorrowInput = input with
            {
                Now = midnight,
                End = midnight.AddDays(1),
                FloorKwhAt = FloorAt,
                Bias = bias
            };
            notes.Add("Tomorrow starts from the SOC this projection leaves at midnight, uses tomorrow's Solcast and Amber forecasts and the same average house load, and assumes no planned export.");
            DateTimeOffset forecastUntil = feedInPrices.Max(p => p.Time);
            if (forecastUntil < new DateTimeOffset(now.Date.AddDays(1), now.Offset))
                notes.Add($"The Amber forecast ends at {forecastUntil:HH:mm}; the last known price is held after that.");

            store.Save(DayProjector.Build(input, realised, forecastUntil, load.DaysUsed, load.Fallback, notes, tomorrowInput));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not build the day projection.");
        }
    }

    private async Task<List<PricePoint>> ReadPricesAsync(HttpClient ha, string entityId, DateTimeOffset now, double hoursAhead, CancellationToken ct)
    {
        var points = new List<PricePoint>();
        try
        {
            var json = await ha.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            if (json.TryGetProperty("state", out var state) &&
                double.TryParse(state.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double current))
                points.Add(new PricePoint(now, current));

            if (json.TryGetProperty("attributes", out var attrs) && TelemetryReader.ParseForecast(attrs, now, (int)Math.Ceiling(hoursAhead)) is { } forecast)
                points.AddRange(forecast.Where(p => p.Time > now).Select(p => new PricePoint(p.Time, p.Price)));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the price forecast from {Entity} for the day projection.", entityId);
        }

        return points.OrderBy(p => p.Time).ToList();
    }
}
