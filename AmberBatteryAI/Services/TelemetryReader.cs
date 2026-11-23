using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

/// <summary>
/// Reads the extra telemetry that is logged next to every decision and by the periodic sampler:
/// grid and solar power, the current Amber prices, the forecast and the setpoints that are active in Home Assistant.
/// Every read degrades to null when an entity is missing, so a wrong entity ID never stops the control loop.
/// </summary>
public class TelemetryReader(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<TelemetryReader> logger)
{
    public const string ExportPriceHelper = "input_number.export_price";
    public const string ExportStopSocHelper = "input_number.export_stop_soc";
    public const string ExportPowerLimitHelper = "input_number.export_power_limit";
    public const string ImportPriceHelper = "input_number.import_price";
    public const string ImportStopSocHelper = "input_number.import_stop_soc";
    public const string ImportPowerLimitHelper = "input_number.import_power_limit";

    public string FeedInEntity => config["HomeAssistant:FeedInPriceEntity"] ?? "sensor.amber_express_home_feed_in_price";

    public HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient();
        string baseUrl = config["HomeAssistant:BaseUrl"] ?? "http://supervisor/core";
        if (!baseUrl.EndsWith('/')) baseUrl += "/";

        string token = config["HomeAssistant:Token"] ?? throw new InvalidOperationException("HomeAssistant:Token is missing.");
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<TelemetrySample> ReadSampleAsync(HttpClient client, CancellationToken ct)
    {
        double? gridRaw = await ReadWattsAsync(client, config["HomeAssistant:GridPowerEntity"] ?? "sensor.la_sigenstor1_grid_power", ct);
        var (gridImport, gridExport) = TelemetryMath.SplitGrid(gridRaw, config.GetValue("HomeAssistant:GridPowerPositiveIsImport", true));

        return new TelemetrySample
        {
            Timestamp = DateTimeOffset.Now,
            SocPercent = await ReadNumberAsync(client, config["HomeAssistant:BatterySocEntity"] ?? "sensor.la_electrical_battery_soc", ct),
            BatteryPowerW = await ReadWattsAsync(client, config["HomeAssistant:BatteryPowerEntity"] ?? "sensor.la_electrical_battery_power", ct),
            HomePowerW = await ReadWattsAsync(client, config["HomeAssistant:HomePowerEntity"] ?? "sensor.la_sigenstor1_home_power", ct),
            EvPowerW = string.IsNullOrWhiteSpace(config["HomeAssistant:EvPowerEntity"] ?? "sensor.wattpilot_charging_power") ? null : await ReadWattsAsync(client, config["HomeAssistant:EvPowerEntity"] ?? "sensor.wattpilot_charging_power", ct),
            SolarPowerW = await ReadWattsAsync(client, config["HomeAssistant:SolarPowerEntity"] ?? "sensor.la_sigenstor1_solar_power", ct),
            GridPowerW = gridRaw,
            GridImportW = gridImport,
            GridExportW = gridExport,
            ImportPrice = await ReadNumberAsync(client, config["HomeAssistant:GeneralPriceEntity"] ?? "sensor.amber_express_home_general_price", ct),
            FeedInPrice = await ReadNumberAsync(client, FeedInEntity, ct)
        };
    }

    public async Task<ActiveSetpoints> ReadSetpointsAsync(HttpClient client, CancellationToken ct) => new()
    {
        ExportPrice = await ReadNumberAsync(client, ExportPriceHelper, ct),
        ExportStopSoc = await ReadNumberAsync(client, ExportStopSocHelper, ct),
        ExportPowerLimit = await ReadNumberAsync(client, ExportPowerLimitHelper, ct),
        ImportPrice = await ReadNumberAsync(client, ImportPriceHelper, ct),
        ImportStopSoc = await ReadNumberAsync(client, ImportStopSocHelper, ct),
        ImportPowerLimit = await ReadNumberAsync(client, ImportPowerLimitHelper, ct),
        HotWater = await ReadTextAsync(client, config["HomeAssistant:HotWaterModeEntity"] ?? "input_select.hot_water_mode", ct)
    };

    /// <summary>The car charger power in watts, or null when the entity is unset or unavailable.</summary>
    public async Task<double?> ReadEvPowerWAsync(HttpClient client, CancellationToken ct)
    {
        string? entity = config["HomeAssistant:EvPowerEntity"] ?? "sensor.wattpilot_charging_power";
        return string.IsNullOrWhiteSpace(entity) ? null : await ReadWattsAsync(client, entity, ct);
    }

    public async Task<List<ForecastPoint>?> ReadFeedInForecastAsync(HttpClient client, int hoursAhead, CancellationToken ct)
    {
        try
        {
            var json = await client.GetFromJsonAsync<JsonElement>($"api/states/{FeedInEntity}", ct);
            return json.TryGetProperty("attributes", out var attrs) ? ParseForecast(attrs, DateTimeOffset.Now, hoursAhead) : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the feed-in forecast for the decision log.");
            return null;
        }
    }

    /// <summary>The Amber "forecast" attribute as points at or after <paramref name="now"/>, with their lead time.</summary>
    public static List<ForecastPoint>? ParseForecast(JsonElement attributes, DateTimeOffset now, int hoursAhead)
    {
        if (!attributes.TryGetProperty("forecast", out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        var points = new List<ForecastPoint>();
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("time", out var timeProp) ||
                !DateTimeOffset.TryParse(timeProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
                !item.TryGetProperty("value", out var valueProp))
                continue;

            double price;
            if (valueProp.ValueKind == JsonValueKind.Number) price = valueProp.GetDouble();
            else if (valueProp.ValueKind != JsonValueKind.String ||
                     !double.TryParse(valueProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out price)) continue;

            double lead = (time - now).TotalMinutes;
            if (lead < 0 || lead > hoursAhead * 60) continue;
            points.Add(new ForecastPoint { Time = time, LeadMinutes = Math.Round(lead, 1), Price = price });
        }

        return points.OrderBy(p => p.Time).ToList();
    }

    private async Task<double?> ReadNumberAsync(HttpClient client, string? entityId, CancellationToken ct) =>
        (await ReadStateAsync(client, entityId, ct))?.Value;

    private async Task<string?> ReadTextAsync(HttpClient client, string? entityId, CancellationToken ct) =>
        (await ReadStateAsync(client, entityId, ct))?.Text;

    /// <summary>Power in watts, converting from kW when the entity says so.</summary>
    private async Task<double?> ReadWattsAsync(HttpClient client, string? entityId, CancellationToken ct)
    {
        var state = await ReadStateAsync(client, entityId, ct);
        if (state?.Value is not double value) return null;
        return string.Equals(state.Unit, "kW", StringComparison.OrdinalIgnoreCase) ? value * 1000 : value;
    }

    private record EntityState(string Text, double? Value, string? Unit);

    private async Task<EntityState?> ReadStateAsync(HttpClient client, string? entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return null;

        try
        {
            var json = await client.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            string text = json.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            if (text is "" or "unknown" or "unavailable") return null;

            string? unit = json.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("unit_of_measurement", out var u)
                ? u.GetString()
                : null;
            double? value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
                ? parsed
                : null;
            return new EntityState(text, value, unit);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not read {Entity}; logging it as null.", entityId);
            return null;
        }
    }
}
