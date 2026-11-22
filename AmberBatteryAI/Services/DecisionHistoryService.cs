using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

/// <summary>
/// Keeps a persistent record of every optimizer run as JSON Lines, one file per day.
/// The files sit next to the saved prompt, so they survive add-on restarts and updates.
/// </summary>
public class DecisionHistoryService
{
    private const string FilePrefix = "decisions-";
    private const string FileExtension = ".jsonl";
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly string[] CsvColumns =
    {
        "timestamp", "outcome", "mode", "soc_percent", "battery_power_w", "home_power_w", "hot_water_temp_c",
        "floor_kwh", "surplus_kwh", "best_feed_in_window", "suggested_export_price", "suggested_export_power_limit", "hot_water_full_check",
        "ai_export_price", "ai_export_stop_soc",
        "export_price", "export_stop_soc", "export_start_soc", "export_power_limit",
        "import_price", "import_stop_soc", "import_start_soc", "import_power_limit",
        "ai_hot_water", "hot_water",
        "solar_power_w", "grid_import_w", "grid_export_w", "import_price_now", "feed_in_price_now",
        "active_export_price", "active_export_stop_soc", "active_export_power_limit",
        "active_import_price", "active_import_stop_soc", "active_import_power_limit", "active_hot_water",
        "overrides", "note", "reasoning", "trigger", "trigger_reason", "ai_levers"
    };

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly ILogger<DecisionHistoryService> _logger;
    private readonly object _lock = new();
    private DateOnly _lastPruned;

    public DecisionHistoryService(
        PromptStorageService promptStorage,
        IConfiguration config,
        ILogger<DecisionHistoryService> logger)
    {
        _logger = logger;
        _directory = Path.GetDirectoryName(promptStorage.GetStoragePath()) ?? Path.GetTempPath();
        _retentionDays = Math.Max(1, config.GetValue("Optimizer:DecisionLogRetentionDays", 30));
        _logger.LogInformation("Decision history folder: {Directory} (keeping {Days} days)", _directory, _retentionDays);
    }

    /// <summary>Appends one entry. Never throws: a logging problem must not stop the control loop.</summary>
    public void Append(DecisionLogEntry entry)
    {
        try
        {
            string line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
            lock (_lock)
            {
                File.AppendAllText(PathFor(DateOnly.FromDateTime(entry.Timestamp.DateTime)), line, Utf8NoBom);
                PruneOldFiles();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the AI decision to the history files.");
        }
    }

    /// <summary>Returns entries for today and the previous days-1 days, oldest first.</summary>
    public List<DecisionLogEntry> Read(int days, int? limit = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = today.AddDays(-(Math.Clamp(days, 1, 365) - 1));
        var entries = new List<DecisionLogEntry>();

        lock (_lock)
        {
            for (var day = first; day <= today; day = day.AddDays(1))
            {
                string path = PathFor(day);
                if (!File.Exists(path)) continue;

                try
                {
                    foreach (string line in File.ReadLines(path, Utf8NoBom))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        try
                        {
                            var entry = JsonSerializer.Deserialize<DecisionLogEntry>(line, JsonOptions);
                            if (entry is not null) entries.Add(entry);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Skipping an unreadable line in {Path}.", path);
                        }
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not read {Path}.", path);
                }
            }
        }

        return limit is > 0 && entries.Count > limit.Value
            ? entries.GetRange(entries.Count - limit.Value, limit.Value)
            : entries;
    }

    public static string ToJsonLines(IEnumerable<DecisionLogEntry> entries) =>
        string.Concat(entries.Select(e => JsonSerializer.Serialize(e, JsonOptions) + "\n"));

    public static string ToCsv(IEnumerable<DecisionLogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', CsvColumns));

        foreach (var e in entries)
        {
            string?[] fields =
            {
                e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                e.Outcome,
                e.AiResponse?.Mode,
                Num(ParseLeadingNumber(e.Inputs?.Soc)),
                Num(ParseWatts(e.Inputs?.BatteryPower)),
                Num(ParseWatts(e.Inputs?.HomePower)),
                Num(ParseLeadingNumber(e.Inputs?.HotWaterTemp)),
                e.Computed?.FloorKwh,
                e.Computed?.SurplusKwh,
                e.Computed?.BestFeedInWindow,
                e.Computed?.SuggestedExportPrice,
                e.Computed?.SuggestedExportPowerLimit,
                e.Computed?.HotWaterFull,
                Num(e.AiResponse?.ExportPrice),
                Num(e.AiResponse?.ExportStopSoc),
                Num(e.Applied?.ExportPrice),
                Num(e.Applied?.ExportStopSoc),
                Num(e.Applied?.ExportStartSoc),
                Num(e.Applied?.ExportPowerLimit),
                Num(e.Applied?.ImportPrice),
                Num(e.Applied?.ImportStopSoc),
                Num(e.Applied?.ImportStartSoc),
                Num(e.Applied?.ImportPowerLimit),
                e.AiResponse?.HotWater,
                e.Applied?.HotWater,
                Num(e.Telemetry?.SolarPowerW),
                Num(e.Telemetry?.GridImportW),
                Num(e.Telemetry?.GridExportW),
                Num(e.Telemetry?.ImportPrice),
                Num(e.Telemetry?.FeedInPrice),
                Num(e.ActiveSetpoints?.ExportPrice),
                Num(e.ActiveSetpoints?.ExportStopSoc),
                Num(e.ActiveSetpoints?.ExportPowerLimit),
                Num(e.ActiveSetpoints?.ImportPrice),
                Num(e.ActiveSetpoints?.ImportStopSoc),
                Num(e.ActiveSetpoints?.ImportPowerLimit),
                e.ActiveSetpoints?.HotWater,
                string.Join("; ", e.Overrides),
                e.Note,
                e.AiResponse?.Reasoning,
            e.Trigger,
            e.TriggerReason,
            e.AiLevers?.Summary
            };

            sb.AppendLine(string.Join(',', fields.Select(CsvEscape)));
        }

        return sb.ToString();
    }

    private string PathFor(DateOnly day) =>
        Path.Combine(_directory, FilePrefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension);

    private void PruneOldFiles()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_lastPruned == today) return;
        _lastPruned = today;

        var cutoff = today.AddDays(-_retentionDays);
        foreach (string file in Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
        {
            string name = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (!DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day >= cutoff)
                continue;

            try
            {
                File.Delete(file);
                _logger.LogInformation("Deleted old decision history file {File}.", file);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete old decision history file {File}.", file);
            }
        }
    }

    private static double? ParseLeadingNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string token = text.Trim().Split(' ')[0];
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    private static double? ParseWatts(string? text)
    {
        double? value = ParseLeadingNumber(text);
        return value is not null && text!.Trim().EndsWith("kW", StringComparison.OrdinalIgnoreCase) ? value * 1000 : value;
    }

    private static string? Num(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture);

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
