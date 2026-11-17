using System.Globalization;
using System.Net.Http.Json;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

/// <summary>
/// Publishes each optimizer run to Home Assistant: a sensor whose state is the AI mode (shows in History)
/// and a log entry carrying the reasoning (shows in the Activity panel).
/// </summary>
public static class DecisionPublisher
{
    private const int MaxTextLength = 2000;

    public static async Task PublishAsync(HttpClient client, string entityId, DecisionLogEntry entry, CancellationToken ct)
    {
        var statePayload = new
        {
            state = StateFor(entry),
            attributes = AttributesFor(entry),
            force_update = true // record every run, even when the mode is unchanged
        };
        var stateResponse = await client.PostAsJsonAsync($"api/states/{entityId}", statePayload, ct);
        stateResponse.EnsureSuccessStatusCode();

        var logPayload = new { name = "Battery AI", message = MessageFor(entry), entity_id = entityId };
        var logResponse = await client.PostAsJsonAsync("api/services/logbook/log", logPayload, ct);
        logResponse.EnsureSuccessStatusCode();
    }

    internal static string StateFor(DecisionLogEntry entry) => entry.Outcome switch
    {
        DecisionOutcome.Applied => Truncate(entry.AiResponse?.Mode, 60) ?? "UNKNOWN",
        DecisionOutcome.Skipped => "SKIPPED",
        DecisionOutcome.NoDecision => "NO DECISION",
        _ => "ERROR"
    };

    internal static Dictionary<string, object?> AttributesFor(DecisionLogEntry entry)
    {
        var all = new Dictionary<string, object?>
        {
            ["friendly_name"] = "Battery AI Decision",
            ["icon"] = "mdi:robot",
            ["outcome"] = entry.Outcome,
            ["run_time"] = entry.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            ["model"] = entry.Model,
            ["soc"] = entry.Inputs?.Soc,
            ["floor_kwh"] = entry.Computed?.FloorKwh,
            ["surplus_kwh"] = entry.Computed?.SurplusKwh,
            ["best_feed_in_window"] = entry.Computed?.BestFeedInWindow,
            ["suggested_export_price"] = entry.Computed?.SuggestedExportPrice,
            ["suggested_export_power_limit"] = entry.Computed?.SuggestedExportPowerLimit,
            ["look_ahead_hold"] = entry.Computed?.LookAheadHold,
            ["hot_water_full_check"] = entry.Computed?.HotWaterFull,
            ["export_soc_hysteresis"] = entry.Computed?.ExportSocHysteresis,
            ["import_soc_hysteresis"] = entry.Computed?.ImportSocHysteresis,
            ["ai_export_price"] = entry.AiResponse?.ExportPrice,
            ["ai_hot_water"] = entry.AiResponse?.HotWater,
            ["export_price"] = entry.Applied?.ExportPrice,
            ["export_stop_soc"] = entry.Applied?.ExportStopSoc,
            ["export_start_soc"] = entry.Applied?.ExportStartSoc,
            ["export_power_limit"] = entry.Applied?.ExportPowerLimit,
            ["import_price"] = entry.Applied?.ImportPrice,
            ["import_stop_soc"] = entry.Applied?.ImportStopSoc,
            ["import_start_soc"] = entry.Applied?.ImportStartSoc,
            ["import_power_limit"] = entry.Applied?.ImportPowerLimit,
            ["hot_water"] = entry.Applied?.HotWater,
            ["overrides"] = entry.Overrides.Count > 0 ? entry.Overrides : null,
            ["note"] = Truncate(entry.Note),
            ["reasoning"] = Truncate(entry.AiResponse?.Reasoning)
        };

        return all.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    internal static string MessageFor(DecisionLogEntry entry)
    {
        string text;
        if (entry.Outcome == DecisionOutcome.Applied && entry.Applied is { } a)
        {
            text = string.Create(CultureInfo.InvariantCulture,
                $"{entry.AiResponse?.Mode}: export >= ${a.ExportPrice:0.###} down to {a.ExportStopSoc:0}% SOC, import <= ${a.ImportPrice:0.###} up to {a.ImportStopSoc:0}% SOC, hot water {a.HotWater ?? "unchanged"}.");
            if (entry.Overrides.Count > 0)
                text += $" Overrides: {string.Join("; ", entry.Overrides)}.";
            text += $" Reasoning: {entry.AiResponse?.Reasoning}";
        }
        else
        {
            text = $"{StateFor(entry)}: {entry.Note}";
        }

        // logbook.log renders the message as a template, so keep template braces out of it
        return Truncate(text.Replace('{', '(').Replace('}', ')'), MaxTextLength + 500)!;
    }

    private static string? Truncate(string? text, int max = MaxTextLength) =>
        text is null || text.Length <= max ? text : text[..(max - 3)] + "...";
}
