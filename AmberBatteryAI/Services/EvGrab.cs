using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmberBatteryAI.Services;

public record EvGrabInput(double BasePercent, double FloorPercent, bool Hold, double FeedInPrice, bool SolarLeft, double HoldOffsetPercent, double ZeroFeedInPercent);

public record EvGrabDecision(double Threshold, string Reason);

/// <summary>
/// Picks the SOC at which the car flow may start taking solar from the battery ("solar grab" threshold). The user's own
/// value is the base: it is only raised while energy is held for a better export price, and only lowered when feed-in
/// is zero or negative and solar is still to come. It never goes below the conserve target or the 20% hard floor.
/// </summary>
public static class EvThresholdPlanner
{
    public static EvGrabDecision Decide(EvGrabInput i)
    {
        double floor = Math.Clamp(i.FloorPercent, SocPlanner.HardFloorPercent, 100);
        double basePct = Math.Clamp(i.BasePercent, 0, 100);

        if (i.FeedInPrice <= 0 && i.SolarLeft)
            return new(Math.Max(floor, Math.Min(basePct, i.ZeroFeedInPercent)), "Feed-in is zero or negative and solar is still to come: the car may take solar earlier.");

        if (i.Hold)
            return new(Math.Min(100, Math.Max(basePct, floor) + Math.Max(0, i.HoldOffsetPercent)), "Holding energy for a better export window: the car waits for a higher SOC.");

        return new(basePct, "No change: your own threshold applies.");
    }
}

public record EvGrabState
{
    [JsonPropertyName("base")] public double Base { get; init; }
    [JsonPropertyName("applied")] public double Applied { get; init; }
}

public record EvGrabResult(double Base, double Applied, string Reason);

/// <summary>Writes the solar-grab threshold helper from the planner and keeps the user's value as the base. Never throws.</summary>
public class EvGrabService(PromptStorageService promptStorage, IConfiguration config, ILogger<EvGrabService> logger)
{
    private const string StateFile = "ev-grab-state.json";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _directory = Path.GetDirectoryName(promptStorage.GetStoragePath()) ?? Path.GetTempPath();
    private readonly object _lock = new();

    public bool Enabled => config.GetValue("Ev:Enabled", false);

    private string Entity => config["HomeAssistant:EvGrabThresholdEntity"] ?? "input_number.ev_solar_grab_threshold";

    public async Task<EvGrabResult?> ApplyAsync(HttpClient ha, bool hold, double feedInPrice, bool solarLeft, double floorPercent, CancellationToken ct)
    {
        if (!Enabled) return await RestoreAsync(ha, ct);

        try
        {
            if (await ReadAsync(ha, ct) is not double current) return null;
            var state = LoadState();
            double basePct = state is null || Math.Abs(current - state.Applied) > 0.01 ? current : state.Base;

            var decision = EvThresholdPlanner.Decide(new EvGrabInput(
                basePct, floorPercent, hold, feedInPrice, solarLeft,
                config.GetValue("Ev:HoldOffsetPercent", 10.0), config.GetValue("Ev:ZeroFeedInThresholdPercent", 60.0)));

            double desired = Math.Round(decision.Threshold, 1);
            if (Math.Abs(desired - current) > 0.01 && !await WriteAsync(ha, desired, ct)) return null;

            SaveState(new EvGrabState { Base = basePct, Applied = desired });
            return new EvGrabResult(basePct, desired, decision.Reason);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not update the EV solar-grab threshold.");
            return null;
        }
    }

    /// <summary>Puts the user's own value back (master switch off or feature turned off). Does nothing if the user changed it since.</summary>
    public async Task<EvGrabResult?> RestoreAsync(HttpClient ha, CancellationToken ct)
    {
        try
        {
            var state = LoadState();
            if (state is null) return null;

            if (await ReadAsync(ha, ct) is double current && Math.Abs(current - state.Applied) <= 0.01 && Math.Abs(state.Base - current) > 0.01
                && !await WriteAsync(ha, state.Base, ct))
                return null;

            File.Delete(Path.Combine(_directory, StateFile));
            return new EvGrabResult(state.Base, state.Base, "Restored your own threshold.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not restore the EV solar-grab threshold.");
            return null;
        }
    }

    private async Task<double?> ReadAsync(HttpClient ha, CancellationToken ct)
    {
        var json = await ha.GetFromJsonAsync<JsonElement>($"api/states/{Entity}", ct);
        return json.TryGetProperty("state", out var s) && double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
    }

    private async Task<bool> WriteAsync(HttpClient ha, double value, CancellationToken ct)
    {
        var response = await ha.PostAsJsonAsync("api/services/input_number/set_value", new { entity_id = Entity, value }, ct);
        if (!response.IsSuccessStatusCode) logger.LogWarning("Setting {Entity} to {Value} failed: {Status}.", Entity, value, response.StatusCode);
        return response.IsSuccessStatusCode;
    }

    private EvGrabState? LoadState()
    {
        lock (_lock)
        {
            try
            {
                string path = Path.Combine(_directory, StateFile);
                return File.Exists(path) ? JsonSerializer.Deserialize<EvGrabState>(File.ReadAllText(path, Utf8NoBom)) : null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read {File}.", StateFile);
                return null;
            }
        }
    }

    private void SaveState(EvGrabState state)
    {
        lock (_lock) File.WriteAllText(Path.Combine(_directory, StateFile), JsonSerializer.Serialize(state), Utf8NoBom);
    }
}
