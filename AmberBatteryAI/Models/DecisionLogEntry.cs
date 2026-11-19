using System.Text.Json.Serialization;

namespace AmberBatteryAI.Models;

public static class DecisionOutcome
{
    public const string Applied = "applied";
    public const string Skipped = "skipped";
    public const string NoDecision = "no_decision";
    public const string Error = "error";
}

/// <summary>One optimizer run: what the AI was given, what it answered and what was actually applied.</summary>
public record DecisionLogEntry
{
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; init; }
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("inputs")] public DecisionInputs? Inputs { get; init; }
    [JsonPropertyName("computed")] public DecisionComputed? Computed { get; init; }
    [JsonPropertyName("ai_response")] public OptimizerDecision? AiResponse { get; init; }
    [JsonPropertyName("applied")] public DecisionApplied? Applied { get; init; }
    [JsonPropertyName("telemetry")] public TelemetrySample? Telemetry { get; init; }
    [JsonPropertyName("active_setpoints")] public ActiveSetpoints? ActiveSetpoints { get; init; }
    [JsonPropertyName("feed_in_forecast")] public List<ForecastPoint>? FeedInForecast { get; init; }
    [JsonPropertyName("overrides")] public List<string> Overrides { get; init; } = new();
    [JsonPropertyName("prompt")] public string? Prompt { get; init; }
}

public record DecisionInputs
{
    [JsonPropertyName("soc")] public string? Soc { get; init; }
    [JsonPropertyName("battery_power")] public string? BatteryPower { get; init; }
    [JsonPropertyName("home_power")] public string? HomePower { get; init; }
    [JsonPropertyName("hot_water_temp")] public string? HotWaterTemp { get; init; }
    [JsonPropertyName("grid_status")] public string? GridStatus { get; init; }
    [JsonPropertyName("remaining_solar_kwh")] public double? RemainingSolarKwh { get; init; }
    [JsonPropertyName("import_price_and_forecast")] public string? ImportPriceAndForecast { get; init; }
    [JsonPropertyName("feed_in_price_and_forecast")] public string? FeedInPriceAndForecast { get; init; }
    [JsonPropertyName("weather_outlook")] public string? WeatherOutlook { get; init; }
    [JsonPropertyName("solar_forecast")] public string? SolarForecast { get; init; }
}

public record DecisionComputed
{
    [JsonPropertyName("floor_kwh")] public string? FloorKwh { get; init; }
    [JsonPropertyName("surplus_kwh")] public string? SurplusKwh { get; init; }
    [JsonPropertyName("best_feed_in_window")] public string? BestFeedInWindow { get; init; }
    [JsonPropertyName("suggested_export_price")] public string? SuggestedExportPrice { get; init; }
    [JsonPropertyName("suggested_export_power_limit")] public string? SuggestedExportPowerLimit { get; init; }
    [JsonPropertyName("look_ahead_hold")] public bool? LookAheadHold { get; init; }
    [JsonPropertyName("hot_water_full_check")] public string? HotWaterFull { get; init; }
    [JsonPropertyName("export_start_soc")] public string? ExportStartSoc { get; init; }
    [JsonPropertyName("import_min_stop_soc")] public string? ImportMinStopSoc { get; init; }
    [JsonPropertyName("export_soc_hysteresis")] public double? ExportSocHysteresis { get; init; }
    [JsonPropertyName("import_soc_hysteresis")] public double? ImportSocHysteresis { get; init; }
    [JsonPropertyName("average_load_kw")] public double? AverageLoadKw { get; init; }
    [JsonPropertyName("ev_power_w")] public double? EvPowerW { get; init; }
    [JsonPropertyName("ev_grab_base")] public double? EvGrabBase { get; init; }
    [JsonPropertyName("ev_grab_applied")] public double? EvGrabApplied { get; init; }
    [JsonPropertyName("ev_grab_reason")] public string? EvGrabReason { get; init; }
}

/// <summary>The values written to Home Assistant after code overrides and limits were applied.</summary>
public record DecisionApplied
{
    [JsonPropertyName("export_price")] public double ExportPrice { get; init; }
    [JsonPropertyName("export_stop_soc")] public double ExportStopSoc { get; init; }
    [JsonPropertyName("export_start_soc")] public double? ExportStartSoc { get; init; }
    [JsonPropertyName("export_power_limit")] public double ExportPowerLimit { get; init; }
    [JsonPropertyName("import_price")] public double ImportPrice { get; init; }
    [JsonPropertyName("import_stop_soc")] public double ImportStopSoc { get; init; }
    [JsonPropertyName("import_start_soc")] public double? ImportStartSoc { get; init; }
    [JsonPropertyName("import_power_limit")] public double ImportPowerLimit { get; init; }
    [JsonPropertyName("hot_water")] public string? HotWater { get; init; }
}
