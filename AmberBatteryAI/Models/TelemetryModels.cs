using System.Text.Json.Serialization;

namespace AmberBatteryAI.Models;

/// <summary>
/// One reading of the live system. Any value is null when its entity is missing or unavailable.
/// Powers are in watts; grid_import_w and grid_export_w are never negative (see <c>TelemetryMath.SplitGrid</c>).
/// </summary>
public record TelemetrySample
{
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("soc_percent")] public double? SocPercent { get; init; }
    [JsonPropertyName("battery_power_w")] public double? BatteryPowerW { get; init; }
    [JsonPropertyName("home_power_w")] public double? HomePowerW { get; init; }
    [JsonPropertyName("ev_power_w")] public double? EvPowerW { get; init; }
    [JsonPropertyName("solar_power_w")] public double? SolarPowerW { get; init; }
    [JsonPropertyName("grid_power_w")] public double? GridPowerW { get; init; }
    [JsonPropertyName("grid_import_w")] public double? GridImportW { get; init; }
    [JsonPropertyName("grid_export_w")] public double? GridExportW { get; init; }
    [JsonPropertyName("import_price")] public double? ImportPrice { get; init; }
    [JsonPropertyName("feed_in_price")] public double? FeedInPrice { get; init; }
}

/// <summary>The helper values read back from Home Assistant after the decision was written.</summary>
public record ActiveSetpoints
{
    [JsonPropertyName("export_price")] public double? ExportPrice { get; init; }
    [JsonPropertyName("export_stop_soc")] public double? ExportStopSoc { get; init; }
    [JsonPropertyName("export_power_limit")] public double? ExportPowerLimit { get; init; }
    [JsonPropertyName("import_price")] public double? ImportPrice { get; init; }
    [JsonPropertyName("import_stop_soc")] public double? ImportStopSoc { get; init; }
    [JsonPropertyName("import_power_limit")] public double? ImportPowerLimit { get; init; }
    [JsonPropertyName("hot_water")] public string? HotWater { get; init; }
}

/// <summary>One point of the feed-in forecast as seen by a decision.</summary>
public record ForecastPoint
{
    [JsonPropertyName("time")] public DateTimeOffset Time { get; init; }
    [JsonPropertyName("lead_min")] public double LeadMinutes { get; init; }
    [JsonPropertyName("price")] public double Price { get; init; }
}

public static class TelemetryMath
{
    /// <summary>
    /// Splits a signed grid power reading into import and export watts.
    /// <paramref name="positiveIsImport"/> is true for sensors that read positive while buying from the grid.
    /// </summary>
    public static (double? Import, double? Export) SplitGrid(double? rawWatts, bool positiveIsImport)
    {
        if (rawWatts is not double raw || !double.IsFinite(raw)) return (null, null);
        double toGrid = positiveIsImport ? -raw : raw;
        return (Math.Max(0, -toGrid), Math.Max(0, toGrid));
    }
}
