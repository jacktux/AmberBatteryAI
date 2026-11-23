using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmberBatteryAI.Services;

public enum EvPriority { NiceToHave, MustCharge, WhenYouCan }

/// <summary>What the car needs: its SOC, the wallbox charge limit and the priority helper. Any value may be unknown.</summary>
public record EvStatus(double? SocPercent, double? LimitPercent, EvPriority Priority, string PriorityText, double BatteryKwh, bool? Plugged = null)
{
    /// <summary>Energy still needed to reach the charge limit; null when SOC or limit is unknown.</summary>
    /// <summary>Plugged is false only when the plug sensor says the car is not connected: then it needs nothing.</summary>
    public double? NeedKwh => Plugged == false ? 0 : SocPercent is double s && LimitPercent is double l ? Math.Round(Math.Max(0, l - s) / 100.0 * BatteryKwh, 1) : null;
    public bool AtLimit => NeedKwh is <= 0;

    public static EvPriority ParsePriority(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "must charge" => EvPriority.MustCharge,
        "charge when you can" => EvPriority.WhenYouCan,
        _ => EvPriority.NiceToHave
    };

    /// <summary>One line for the decision prompt; empty when nothing is known about the car.</summary>
    public string PromptText()
    {
        if (Plugged == false) return " (car not plugged in: no car need counted)";
        if (SocPercent is null && LimitPercent is null) return "";
        string need = NeedKwh is double n ? (n <= 0 ? "the car is at its charge limit" : $"the car still needs about {n:0.#} kWh") : "the car's need is unknown";
        string soc = SocPercent is double s ? $"SOC {s:0}%" : "SOC unknown";
        string limit = LimitPercent is double l ? $", limit {l:0}%" : "";
        return $" (car {soc}{limit}: {need}; priority: {PriorityText})";
    }
}

public record EvGrabInput(double BasePercent, double FloorPercent, bool Hold, double FeedInPrice, bool SolarLeft, double HoldOffsetPercent, double ZeroFeedInPercent,
    EvPriority Priority = EvPriority.NiceToHave, bool CarAtLimit = false,
    double? SocPercent = null, double CapacityKwh = 0, double? CarNeedKwh = null, double? SolarSurplusKwh = null, double ConservePercent = 0, double PlentyPoints = 20);

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

        if (i.CarAtLimit)
            return new(basePct, "The car is at its charge limit: your own threshold applies.");

        if (i.Priority == EvPriority.MustCharge)
            return new(floor, "Must Charge: the car may take energy from the battery down to the floor.");

        if (i.Priority == EvPriority.WhenYouCan && !(i.FeedInPrice <= 0 && i.SolarLeft))
        {
            double battery = Math.Min(100, Math.Max(basePct, floor) + Math.Max(0, i.HoldOffsetPercent));
            double plenty = Math.Max(floor, i.ConservePercent + Math.Max(0, i.PlentyPoints));
            if (i.SocPercent is double soc && i.ConservePercent > 0 && soc > i.ConservePercent + Math.Max(0, i.PlentyPoints) && plenty < battery)
                return new(plenty, $"Charge when you can: the battery has plenty of charge ({soc:0}% is above {plenty:0}%), so the car may take energy from it.");
            return new(battery, "Charge when you can: the car only takes solar surplus, the battery comes first.");
        }

        if (i.FeedInPrice <= 0 && i.SolarLeft)
            return new(Math.Max(floor, Math.Min(basePct, i.ZeroFeedInPercent)), "Feed-in is zero or negative and solar is still to come: the car may take solar earlier.");

        // Nice to Have: lower the threshold just enough that the solar left today plus the battery above it finishes the car.
        if (i.Priority == EvPriority.NiceToHave && i.SocPercent is double socNow && i.CapacityKwh > 0 && i.CarNeedKwh is double need && need > 0 && i.SolarSurplusKwh is double surplus)
        {
            double shortfall = need - Math.Max(0, surplus);
            if (shortfall > 0)
            {
                double stored = socNow / 100 * i.CapacityKwh;
                double wanted = (stored - shortfall) / i.CapacityKwh * 100;
                double threshold = Math.Min(basePct, Math.Max(floor, wanted));
                string reason = wanted < floor
                    ? $"Nice to Have: the car needs {need:0.#} kWh and today's spare solar is {Math.Max(0, surplus):0.#} kWh; even the battery above the floor cannot cover the gap, so the car may take energy down to the floor."
                    : $"Nice to Have: the car needs {need:0.#} kWh and today's spare solar is {Math.Max(0, surplus):0.#} kWh, so the car may take the {shortfall:0.#} kWh gap from the battery above {threshold:0}%.";
                return new(threshold, reason);
            }
        }

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

    /// <summary>Reads the car's SOC, charge limit and priority helper. Empty or missing entities give unknown values; never throws.</summary>
    public async Task<EvStatus> ReadStatusAsync(HttpClient ha, CancellationToken ct)
    {
        async Task<string?> Text(string key, string fallback)
        {
            string? entity = config[key] ?? fallback;
            if (string.IsNullOrWhiteSpace(entity)) return null;
            try
            {
                var json = await ha.GetFromJsonAsync<JsonElement>($"api/states/{entity}", ct);
                return json.TryGetProperty("state", out var s) ? s.GetString() : null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Could not read {Entity}.", entity);
                return null;
            }
        }

        double? Number(string? text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

        var soc = Number(await Text("HomeAssistant:EvSocEntity", "sensor.garage_ev3_ev_battery_level"));
        var limit = Number(await Text("HomeAssistant:EvChargeLimitEntity", "number.garage_ev3_ac_charging_limit"));
        string? priorityText = await Text("HomeAssistant:EvPriorityEntity", "input_select.ev_charge_priority");
        bool known = priorityText is not null && !priorityText.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !priorityText.Equals("unavailable", StringComparison.OrdinalIgnoreCase);
        bool? plugged = null;
        string? plug = await Text("HomeAssistant:EvPluggedInEntity", "binary_sensor.garage_ev3_ev_battery_plug");
        if (plug is not null)
        {
            if (plug.Equals("on", StringComparison.OrdinalIgnoreCase)) plugged = true;
            else if (plug.Equals("off", StringComparison.OrdinalIgnoreCase)) plugged = false;
            else logger.LogInformation("Car plug sensor is {State}: counting the car's need as before.", plug);
        }
        return new EvStatus(soc, limit, known ? EvStatus.ParsePriority(priorityText) : EvPriority.NiceToHave, known ? priorityText! : "Nice to Have (helper not set)", config.GetValue("Ev:BatteryKwh", 81.0), plugged);
    }

    private string Entity => config["HomeAssistant:EvGrabThresholdEntity"] ?? "input_number.ev_solar_grab_threshold";

    public async Task<EvGrabResult?> ApplyAsync(HttpClient ha, bool hold, double feedInPrice, bool solarLeft, double floorPercent, EvStatus? car, CancellationToken ct,
        double? socPercent = null, double capacityKwh = 0, double? solarSurplusKwh = null, double conservePercent = 0)
    {
        if (!Enabled) return await RestoreAsync(ha, ct);

        try
        {
            if (await ReadAsync(ha, ct) is not double current) return null;
            var state = LoadState();
            double basePct = state is null || Math.Abs(current - state.Applied) > 0.01 ? current : state.Base;

            var decision = EvThresholdPlanner.Decide(new EvGrabInput(
                basePct, floorPercent, hold, feedInPrice, solarLeft,
                config.GetValue("Ev:HoldOffsetPercent", 10.0), config.GetValue("Ev:ZeroFeedInThresholdPercent", 60.0),
                car?.Priority ?? EvPriority.NiceToHave, car?.AtLimit ?? false,
                socPercent, capacityKwh, car?.NeedKwh, solarSurplusKwh, conservePercent, config.GetValue("Ev:PlentyPoints", 20.0)));

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
