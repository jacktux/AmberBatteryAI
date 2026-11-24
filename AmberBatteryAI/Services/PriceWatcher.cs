using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

/// <summary>
/// Checks the live Amber prices every minute (no AI call) against the forecast the last optimizer run used, and
/// triggers an extra run when the feed-in price is higher, or the buy price lower, than forecast by the configured
/// percentage. Set Optimizer:PriceWatchEnabled to false to turn it off.
/// </summary>
public class PriceWatcher(
    TelemetryReader reader,
    OptimizerState state,
    IConfiguration config,
    ILogger<PriceWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(60);

    private List<ForecastPoint>? _feedInForecast;
    private List<ForecastPoint>? _buyForecast;
    private double? _baselineFeedIn;
    private double? _baselineBuy;
    private DateTimeOffset? _snapshotOfRun;
    private double? _previousCarWatts;
    private double? _previousBuy;
    private bool? _previousForce;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Optimizer:PriceWatchEnabled", true))
        {
            logger.LogInformation("Price watcher is off (Optimizer:PriceWatchEnabled = false).");
            return;
        }

        var settings = new PriceWatchRules.Settings(
            Math.Clamp(config.GetValue("Optimizer:PriceWatchShiftPercent", 10.0), 0, 500),
            Math.Max(0, config.GetValue("Optimizer:PriceWatchMinShift", 0.02)),
            config.GetValue("Optimizer:MinExportPrice", 0.08),
            config.GetValue("Optimizer:MaxImportPrice", 0.25),
            TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Optimizer:PriceWatchCooldownMinutes", 10), 1, 120)));

        string feedInEntity = config["HomeAssistant:FeedInPriceEntity"] ?? "sensor.amber_express_home_feed_in_price";
        string buyEntity = config["HomeAssistant:GeneralPriceEntity"] ?? "sensor.amber_express_home_general_price";
        string masterSwitch = config["HomeAssistant:MasterSwitchEntity"] ?? "input_boolean.amber_battery_ai_optimizer";

        logger.LogInformation(
            "Price watcher on: re-runs when feed-in is {Percent}% (min ${Min}) above, or the buy price below, its forecast; cooldown {Cooldown} min.",
            settings.ShiftPercent, settings.MinShiftDollars, settings.CooldownOrDefault.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(feedInEntity, buyEntity, masterSwitch, settings, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Price watch check failed.");
            }

            try { await Task.Delay(PollEvery, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task CheckAsync(string feedInEntity, string buyEntity, string masterSwitch, PriceWatchRules.Settings settings, CancellationToken ct)
    {
        var lastRun = state.LastRunUtc;
        if (lastRun is null || state.IsRunning) return;

        using var http = reader.CreateClient();
        var now = DateTimeOffset.Now;

        if (config.GetValue("Ev:HotWaterGuard", true) && await CheckCarAsync(http, lastRun.Value, ct))
            return;

        string forceEntity = config["HomeAssistant:ForceChargeEntity"] ?? "input_boolean.force_grid_charge";
        bool forceOn = await IsOnAsync(http, forceEntity, ct);
        var force = PriceWatchRules.ForceTurnedOn(_previousForce, forceOn, state.IsRunning);
        _previousForce = forceOn;
        if (force.Run && !await IsOffAsync(http, masterSwitch, ct))
        {
            logger.LogInformation("Run triggered: {Reason}.", force.Reason);
            state.TriggerImmediateRun(RunTrigger.Manual, force.Reason);
            return;
        }

        if (_snapshotOfRun != lastRun)
        {
            // The forecast the run just used: the prices it was planning against.
            _feedInForecast = await ReadForecastAsync(http, feedInEntity, now, ct);
            _buyForecast = await ReadForecastAsync(http, buyEntity, now, ct);
            _baselineFeedIn = await ReadPriceAsync(http, feedInEntity, ct);
            _baselineBuy = await ReadPriceAsync(http, buyEntity, ct);
            if (_feedInForecast is not null || _buyForecast is not null) _snapshotOfRun = lastRun;
            return;
        }

        double? liveFeedIn = await ReadPriceAsync(http, feedInEntity, ct);
        double? liveBuy = await ReadPriceAsync(http, buyEntity, ct);

        double cheapPrice = await ReadPriceAsync(http, config["HomeAssistant:CheapImportPriceEntity"] ?? "input_number.cheap_import_price", ct)
            ?? config.GetValue("Optimizer:CheapImportPrice", 0.0);
        var cheap = PriceWatchRules.CheapReached(_previousBuy, liveBuy, cheapPrice, DateTimeOffset.UtcNow - lastRun.Value, state.IsRunning);
        if (liveBuy is not null) _previousBuy = liveBuy;
        if (cheap.Run && !await IsOffAsync(http, masterSwitch, ct))
        {
            logger.LogInformation("Price watcher triggers a run: {Reason}.", cheap.Reason);
            state.TriggerImmediateRun(RunTrigger.Price, cheap.Reason);
            return;
        }

        var decision = PriceWatchRules.Evaluate(
            liveFeedIn, PriceWatchRules.ExpectedPrice(_feedInForecast, now),
            liveBuy, PriceWatchRules.ExpectedPrice(_buyForecast, now),
            DateTimeOffset.UtcNow - lastRun.Value, state.IsRunning, settings, _baselineFeedIn, _baselineBuy);

        if (!decision.Run) return;

        if (await IsOffAsync(http, masterSwitch, ct))
        {
            logger.LogDebug("Price watcher: {Reason}, but {Entity} is off.", decision.Reason, masterSwitch);
            return;
        }

        logger.LogInformation("Price watcher triggers a run: {Reason}.", decision.Reason);
        state.TriggerImmediateRun(RunTrigger.Price, decision.Reason);
    }

    /// <summary>Watches the car charger: a run starts when the car begins charging while hot water is on Full. Returns true when a run was triggered.</summary>
    private async Task<bool> CheckCarAsync(HttpClient http, DateTimeOffset lastRun, CancellationToken ct)
    {
        double? watts = await reader.ReadEvPowerWAsync(http, ct);
        double? previous = _previousCarWatts;
        if (watts is not null) _previousCarWatts = watts;

        var decision = PriceWatchRules.CarStarted(previous, watts, null, DateTimeOffset.UtcNow - lastRun, state.IsRunning);
        if (!decision.Run && decision.Reason != "hot water is not on Full") return false;

        string modeEntity = config["HomeAssistant:HotWaterModeEntity"] ?? "input_select.hot_water_mode";
        string? mode = null;
        try
        {
            var json = await http.GetFromJsonAsync<JsonElement>($"api/states/{modeEntity}", ct);
            mode = json.TryGetProperty("state", out var s) ? s.GetString() : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        decision = PriceWatchRules.CarStarted(previous, watts, mode, DateTimeOffset.UtcNow - lastRun, state.IsRunning);
        if (!decision.Run) return false;

        logger.LogInformation("Car watcher triggers a run: {Reason}.", decision.Reason);
        state.TriggerImmediateRun(RunTrigger.Car, decision.Reason);
        return true;
    }

    private static async Task<List<ForecastPoint>?> ReadForecastAsync(HttpClient http, string entityId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            return json.TryGetProperty("attributes", out var attrs) ? TelemetryReader.ParseForecast(attrs, now - PriceWatchRules.MaxPointAge, 48) : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<double?> ReadPriceAsync(HttpClient http, string entityId, CancellationToken ct)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            string? text = json.TryGetProperty("state", out var s) ? s.GetString() : null;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<bool> IsOnAsync(HttpClient http, string entityId, CancellationToken ct)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            return json.TryGetProperty("state", out var s) && string.Equals(s.GetString(), "on", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<bool> IsOffAsync(HttpClient http, string entityId, CancellationToken ct)
    {
        try
        {
            var json = await http.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            return json.TryGetProperty("state", out var s) && string.Equals(s.GetString(), "off", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
