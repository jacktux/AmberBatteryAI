using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

public class BatteryOptimizerWorker(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    OptimizerState state,
    ILogger<BatteryOptimizerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int intervalMinutes = config.GetValue("Optimizer:IntervalMinutes", 30);

        while (!stoppingToken.IsCancellationRequested)
        {
            state.SetRunning(true);
            try
            {
                logger.LogInformation("Starting AI battery optimization cycle...");
                await RunOptimizationCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during optimization cycle.");
                state.RecordError(ex.Message);
            }
            finally
            {
                state.SetRunning(false);
            }

            var nextRun = DateTimeOffset.UtcNow.AddMinutes(intervalMinutes);
            state.SetNextRun(nextRun);

            try
            {
                var delayToken = state.GetDelayToken(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), delayToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Immediate optimization run triggered from dashboard.");
            }
        }
    }

    private async Task RunOptimizationCycleAsync(CancellationToken ct)
    {
        var haClient = CreateHaClient();
        int forecastHours = config.GetValue("Optimizer:ForecastHours", 12);

        string socEntity = config["HomeAssistant:BatterySocEntity"] ?? "sensor.la_electrical_battery_soc";
        string batteryPowerEntity = config["HomeAssistant:BatteryPowerEntity"] ?? "sensor.la_electrical_battery_power";
        string homePowerEntity = config["HomeAssistant:HomePowerEntity"] ?? "sensor.la_sigenstor1_home_power";
        string gridStatusEntity = config["HomeAssistant:GridStatusEntity"] ?? "sensor.la_sigenstor_1_grid_status";
        double batteryCapacityKwh = config.GetValue("HomeAssistant:BatteryCapacityKwh", 16.0);

        string generalEntity = config["HomeAssistant:GeneralPriceEntity"] ?? "sensor.amber_express_home_general_price";
        string feedInEntity = config["HomeAssistant:FeedInPriceEntity"] ?? "sensor.amber_express_home_feed_in_price";
        string weatherEntity = config["HomeAssistant:WeatherEntity"] ?? "weather.welby_hourly";
        string solarEntity = config["HomeAssistant:SolarForecastEntity"] ?? "sensor.solcast_pv_forecast_forecast_today";
        string remainingSolarEntity = config["HomeAssistant:SolarForecastRemainingEntity"] ?? "sensor.solcast_pv_forecast_forecast_remaining_today";

        var soc = await GetEntityStateAsync(haClient, socEntity, ct);
        var batteryPower = await GetEntityStateWithUnitAsync(haClient, batteryPowerEntity, ct);
        var homePower = await GetEntityStateWithUnitAsync(haClient, homePowerEntity, ct);
        var gridStatus = await GetEntityStateAsync(haClient, gridStatusEntity, ct);
        state.SetGridStatus(gridStatus);

        if (!string.Equals(gridStatus.Trim(), "On grid", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Skipping AI optimization because grid status is {GridStatus}.",
                gridStatus);
            return;
        }

        var generalSummary = await GetCompactAmberForecastAsync(haClient, generalEntity, forecastHours, ct);
        var feedInSummary = await GetCompactAmberForecastAsync(haClient, feedInEntity, forecastHours, ct);
        var weatherSummary = await GetCompactWeatherAsync(haClient, weatherEntity, forecastHours, ct);

        string solarSummary = await GetCompactSolcastForecastAsync(
            haClient,
            solarEntity,
            remainingSolarEntity,
            forecastHours,
            ct);

        string kwhBudgetSummary = "Unknown";
        if (double.TryParse(soc, out double socPct))
        {
            double storedKwh = Math.Round((socPct / 100.0) * batteryCapacityKwh, 2);
            double neededToFullKwh = Math.Round(batteryCapacityKwh - storedKwh, 2);
            double reserve20Kwh = Math.Round(batteryCapacityKwh * 0.20, 2);
            double usableAboveReserveKwh = Math.Max(0, Math.Round(storedKwh - reserve20Kwh, 2));

            kwhBudgetSummary = $"{storedKwh} kWh stored of {batteryCapacityKwh} kWh total ({usableAboveReserveKwh} kWh usable above 20% floor; {neededToFullKwh} kWh needed to reach 100%)";
        }

        string prompt = $$"""
            You are a quantitative wholesale electricity arbitrage trader managing a Sigenstor home battery in NSW, Australia.
            Your sole objective is to MAXIMIZE NET FINANCIAL PROFIT ($ AUD) over the next {{forecastHours}} hours while accounting for ~10% round-trip inverter/battery efficiency losses and $0.05/kWh battery degradation cost.

            Trading & Arbitrage Rules (Strictly Follow):
            1. MINIMUM PROFIT SPREAD: Never cycle the battery for tiny margins. To profitably buy from the grid and sell later, the expected future export price MUST exceed (current_import_price * 1.12) + $0.06/kWh.
            2. PRE-SOLAR DUMPING (MORNING ARBITRAGE): If it is morning and Solcast shows enough upcoming solar kWh (after covering current house load) to easily recharge the battery to 100% before 15:00, sell existing battery charge during the morning peak (06:30-09:30) down to 20-30% SOC so you don't waste free solar to curtailment or negative midday feed-in.
            3. MIDDAY SOLAR & NEGATIVE PRICING: Between 10:00 and 15:00, if feed-in prices are negative or near $0, rely on free solar first. Only trigger grid import (import_price) if general grid prices drop below $0.00 (get paid to charge) OR if Solcast shows insufficient solar to reach 95-100% SOC before the evening peak (16:30-21:00) AND current general price is significantly cheaper than evening peak prices.
            4. EVENING PEAK CAPTURE (16:30 - 21:30): Hold battery capacity for the highest-priced 1-2 hour window in the forecast. Set export_price just below the top forecasted spike prices (e.g., if peak forecast is $0.85-$1.50, set export_price to $0.75) so you capture the spike without dumping early at $0.25. If an extreme spike (>$2.00/kWh) is occurring NOW, drop export_stop_soc to 20% and set export_power_limit to 10000W immediately.
            5. OVERNIGHT / SHOULDER PROTECTION: Account for current house load to estimate overnight kWh consumption. Never export below 30% SOC unless feed-in exceeds $0.80/kWh, ensuring you cover overnight household load instead of buying back expensive peak/shoulder power.
            6. DEADBAND SAFETY: Keep import_price at least $0.10/kWh below export_price at all times.

            Live Telemetry:
            - Current Local Time: {{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}}
            - Battery SOC & Energy Budget: {{soc}}% ({{kwhBudgetSummary}})
            - Current Battery Power Flow: {{batteryPower}}
            - Current House Load: {{homePower}}
            - Grid Connection Status: {{gridStatus}}
            - General Import Price & Forecast: {{generalSummary}}
            - Feed-in Export Price & Forecast: {{feedInSummary}}
            - Weather Outlook: {{weatherSummary}}
            - Solar Forecast (Solcast): {{solarSummary}}
            """;

        var decision = await GetAiDecisionAsync(prompt, ct);
        if (decision is null) return;

        double minExpPrice = config.GetValue("Optimizer:MinExportPrice", 0.15);
        double maxExpPrice = config.GetValue("Optimizer:MaxExportPrice", 5.00);
        double minExpSoc = config.GetValue("Optimizer:MinExportStopSoc", 20.0);
        double maxExpSoc = config.GetValue("Optimizer:MaxExportStopSoc", 95.0);

        double minImpPrice = config.GetValue("Optimizer:MinImportPrice", -1.00);
        double maxImpPrice = config.GetValue("Optimizer:MaxImportPrice", 0.25);
        double minImpSoc = config.GetValue("Optimizer:MinImportStopSoc", 30.0);
        double maxImpSoc = config.GetValue("Optimizer:MaxImportStopSoc", 100.0);

        double maxWatts = config.GetValue("Optimizer:MaxPowerWatts", 10000.0);

        double safeExportPrice = Math.Round(Math.Clamp(decision.ExportPrice, minExpPrice, maxExpPrice), 2);
        double safeExportStopSoc = Math.Round(Math.Clamp(decision.ExportStopSoc, minExpSoc, maxExpSoc), 0);
        double safeExportLimit = Math.Round(Math.Clamp(decision.ExportPowerLimit, 1000, maxWatts), 0);

        double safeImportPrice = Math.Round(Math.Clamp(decision.ImportPrice, minImpPrice, maxImpPrice), 2);
        double safeImportStopSoc = Math.Round(Math.Clamp(decision.ImportStopSoc, minImpSoc, maxImpSoc), 0);
        double safeImportLimit = Math.Round(Math.Clamp(decision.ImportPowerLimit, 1000, maxWatts), 0);

        if (safeImportPrice >= safeExportPrice - 0.05)
        {
            safeImportPrice = Math.Round(safeExportPrice - 0.10, 2);
        }

        logger.LogInformation(
            "AI Decision -> Export: >=${ExpPrice} down to {ExpSoc}% @ {ExpW}W | Import: <=${ImpPrice} up to {ImpSoc}% @ {ImpW}W | Reason: {Reason}",
            safeExportPrice, safeExportStopSoc, safeExportLimit,
            safeImportPrice, safeImportStopSoc, safeImportLimit,
            decision.Reasoning);

        await SetInputNumberAsync(haClient, "input_number.export_price", safeExportPrice, ct);
        await SetInputNumberAsync(haClient, "input_number.export_stop_soc", safeExportStopSoc, ct);
        await SetInputNumberAsync(haClient, "input_number.export_power_limit", safeExportLimit, ct);

        await SetInputNumberAsync(haClient, "input_number.import_price", safeImportPrice, ct);
        await SetInputNumberAsync(haClient, "input_number.import_stop_soc", safeImportStopSoc, ct);
        await SetInputNumberAsync(haClient, "input_number.import_power_limit", safeImportLimit, ct);

        string telemetrySnapshot = $"SOC: {soc}% ({kwhBudgetSummary}) | Battery: {batteryPower} | Home: {homePower} | Solar: {solarSummary}";
        state.RecordSuccess(
            new AppliedDecision(
                safeExportPrice,
                safeExportStopSoc,
                safeExportLimit,
                safeImportPrice,
                safeImportStopSoc,
                safeImportLimit,
                decision.Reasoning),
            telemetrySnapshot,
            prompt);
    }

    private HttpClient CreateHaClient()
    {
        var client = httpClientFactory.CreateClient();
        string baseUrl = config["HomeAssistant:BaseUrl"] ?? "http://supervisor/core";
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        string token = config["HomeAssistant:Token"] ?? throw new InvalidOperationException("HomeAssistant:Token is missing.");

        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }


    private static async Task<string> GetEntityStateAsync(HttpClient client, string entityId, CancellationToken ct)
    {
        var json = await client.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
        return json.GetProperty("state").GetString() ?? "unknown";
    }

    private static async Task<string> GetEntityStateWithUnitAsync(HttpClient client, string entityId, CancellationToken ct)
    {
        try
        {
            var json = await client.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            string state = json.GetProperty("state").GetString() ?? "unknown";
            string unit = json.TryGetProperty("attributes", out var attrs) &&
                          attrs.TryGetProperty("unit_of_measurement", out var u)
                ? u.GetString() ?? ""
                : "";

            return string.IsNullOrEmpty(unit) ? state : $"{state} {unit}";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static async Task<string> GetCompactAmberForecastAsync(HttpClient client, string entityId, int hoursAhead, CancellationToken ct)
    {
        var json = await client.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
        string currentState = json.GetProperty("state").GetString() ?? "unknown";

        if (!json.TryGetProperty("attributes", out var attrs))
            return $"Current=${currentState}";

        string descriptor = attrs.TryGetProperty("descriptor", out var descProp)
            ? descProp.GetString() ?? ""
            : "";

        if (!attrs.TryGetProperty("forecast", out var forecastArray) ||
            forecastArray.ValueKind != JsonValueKind.Array ||
            forecastArray.GetArrayLength() == 0)
        {
            return string.IsNullOrEmpty(descriptor)
                ? $"Current=${currentState}"
                : $"Current=${currentState} ({descriptor})";
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(hoursAhead);
        var intervals = new List<(DateTimeOffset LocalTime, double Value)>();

        foreach (var item in forecastArray.EnumerateArray())
        {
            if (!item.TryGetProperty("time", out var timeProp) ||
                !DateTimeOffset.TryParse(timeProp.GetString(), out var parsedTime))
                continue;

            if (parsedTime < now.AddMinutes(-5))
                continue;
            if (parsedTime > cutoff)
                break;

            double val = 0;
            if (item.TryGetProperty("value", out var valProp))
            {
                if (valProp.ValueKind == JsonValueKind.Number)
                    val = valProp.GetDouble();
                else if (valProp.ValueKind == JsonValueKind.String)
                    double.TryParse(valProp.GetString(), out val);
            }

            intervals.Add((parsedTime.ToLocalTime(), val));
        }

        if (intervals.Count == 0)
            return $"Current=${currentState} ({descriptor})";

        var halfHourBuckets = intervals
            .GroupBy(x => new DateTime(x.LocalTime.Year, x.LocalTime.Month, x.LocalTime.Day, x.LocalTime.Hour, (x.LocalTime.Minute / 30) * 30, 0))
            .Select(g =>
            {
                double avg = Math.Round(g.Average(x => x.Value), 3);
                double max = Math.Round(g.Max(x => x.Value), 3);
                string label = g.Key.ToString("HH:mm");

                return (max - avg) >= 0.05
                    ? $"{label}=avg${avg}/max${max}"
                    : $"{label}=${avg}";
            });

        string currentHeader = string.IsNullOrEmpty(descriptor)
            ? $"Current=${currentState}"
            : $"Current=${currentState} ({descriptor})";

        return $"{currentHeader} | Next {hoursAhead}h: [{string.Join(", ", halfHourBuckets)}]";
    }

    private static async Task<string> GetCompactWeatherAsync(HttpClient client, string entityId, int hoursAhead, CancellationToken ct)
    {
        try
        {
            var stateJson = await client.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            string currentCondition = stateJson.GetProperty("state").GetString() ?? "unknown";
            double currentTemp = stateJson.TryGetProperty("attributes", out var attrs) &&
                                 attrs.TryGetProperty("temperature", out var t) && t.TryGetDouble(out var tv)
                ? tv : double.NaN;

            string currentHeader = double.IsNaN(currentTemp)
                ? $"Now={currentCondition}"
                : $"Now={currentCondition} ({currentTemp}C)";

            var payload = new
            {
                entity_id = entityId,
                type = "hourly"
            };

            var response = await client.PostAsJsonAsync("api/services/weather/get_forecasts?return_response=true", payload, ct);
            if (!response.IsSuccessStatusCode)
                return currentHeader;

            var forecastRoot = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

            if (!forecastRoot.TryGetProperty("service_response", out var serviceResp) ||
                !serviceResp.TryGetProperty(entityId, out var entityResp) ||
                !entityResp.TryGetProperty("forecast", out var forecastArray) ||
                forecastArray.ValueKind != JsonValueKind.Array)
            {
                return currentHeader;
            }

            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddHours(hoursAhead);
            var hourlyEntries = new List<string>();

            foreach (var item in forecastArray.EnumerateArray())
            {
                if (!item.TryGetProperty("datetime", out var dtProp) ||
                    !DateTimeOffset.TryParse(dtProp.GetString(), out var parsedTime))
                    continue;

                if (parsedTime < now.AddHours(-1))
                    continue;
                if (parsedTime > cutoff)
                    break;

                string cond = item.TryGetProperty("condition", out var cProp) ? cProp.GetString() ?? "?" : "?";
                double temp = item.TryGetProperty("temperature", out var tProp) && tProp.TryGetDouble(out var tempVal)
                    ? Math.Round(tempVal, 1) : double.NaN;

                int? rainProb = item.TryGetProperty("precipitation_probability", out var rpProp) && rpProp.TryGetInt32(out var rpVal)
                    ? rpVal : null;

                int? cloudCover = item.TryGetProperty("cloud_coverage", out var ccProp) && ccProp.TryGetInt32(out var ccVal)
                    ? ccVal : null;

                string localHour = parsedTime.ToLocalTime().ToString("HH:mm");
                var details = new List<string> { cond };
                if (!double.IsNaN(temp)) details.Add($"{temp}C");
                if (rainProb.HasValue && rainProb.Value > 0) details.Add($"rain:{rainProb.Value}%");
                if (cloudCover.HasValue) details.Add($"cloud:{cloudCover.Value}%");

                hourlyEntries.Add($"{localHour}={string.Join("/", details)}");
            }

            return hourlyEntries.Count > 0
                ? $"{currentHeader} | Next {hoursAhead}h: [{string.Join(", ", hourlyEntries)}]"
                : currentHeader;
        }
        catch
        {
            return "unavailable";
        }
    }

    private static async Task SetInputNumberAsync(HttpClient client, string entityId, double value, CancellationToken ct)
    {
        var payload = new { entity_id = entityId, value };
        var response = await client.PostAsJsonAsync("api/services/input_number/set_value", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> GetCompactSolcastForecastAsync(
        HttpClient client,
        string forecastEntityId,
        string remainingEntityId,
        int hoursAhead,
        CancellationToken ct)
    {
        try
        {
            var forecastJson = await client.GetFromJsonAsync<JsonElement>($"api/states/{forecastEntityId}", ct);
            var remainingJson = await client.GetFromJsonAsync<JsonElement>($"api/states/{remainingEntityId}", ct);
            string remainingKwh = remainingJson.GetProperty("state").GetString() ?? "unknown";

            if (!forecastJson.TryGetProperty("attributes", out var attrs) ||
                !attrs.TryGetProperty("detailedForecast", out var forecastArray) ||
                forecastArray.ValueKind != JsonValueKind.Array)
            {
                return $"Remaining today: {remainingKwh} kWh";
            }

            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddHours(hoursAhead);
            var compactEntries = new List<string>();

            foreach (var item in forecastArray.EnumerateArray())
            {
                string? timeStr = item.TryGetProperty("period_start", out var ps) ? ps.GetString() : null;

                if (DateTimeOffset.TryParse(timeStr, out var periodStart))
                {
                    if (periodStart < now.AddMinutes(-30))
                        continue;
                    if (periodStart > cutoff)
                        break;
                }

                double pvEstimate = item.TryGetProperty("pv_estimate", out var pv) && pv.TryGetDouble(out var d)
                    ? Math.Round(d, 2)
                    : 0;

                if (pvEstimate <= 0.01)
                    continue;

                string localLabel = DateTimeOffset.TryParse(timeStr, out var parsed)
                    ? parsed.ToLocalTime().ToString("HH:mm")
                    : timeStr ?? "?";

                compactEntries.Add($"{localLabel}={pvEstimate}kW");
            }

            return compactEntries.Count > 0
                ? $"Remaining today={remainingKwh}kWh | Next {hoursAhead}h solar: [{string.Join(", ", compactEntries)}]"
                : $"Remaining today={remainingKwh}kWh | Next {hoursAhead}h solar: 0kW (night/no generation)";
        }
        catch
        {
            return "unavailable";
        }
    }

    private async Task<OptimizerDecision?> GetAiDecisionAsync(string prompt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);

        string endpoint = config["AiProvider:Endpoint"] ?? "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
        string apiKey = config["AiProvider:ApiKey"] ?? throw new InvalidOperationException("AiProvider:ApiKey is missing.");
        string model = config["AiProvider:Model"] ?? "gemini-3.5-flash-lite";

        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "none")
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var requestBody = new
        {
            model,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = """
                        You output strictly valid JSON with no markdown fences and exact property names:
                        {"export_price": number, "export_stop_soc": number, "export_power_limit": number, "import_price": number, "import_stop_soc": number, "import_power_limit": number, "reasoning": string}
                        """
                },
                new { role = "user", content = prompt }
            },
            response_format = new
            {
                type = "json_object"
            }
        };

        var response = await client.PostAsJsonAsync(endpoint, requestBody, ct);
        string rawBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"AI Provider returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {rawBody}");
        }

        JsonElement result;
        try
        {
            result = JsonSerializer.Deserialize<JsonElement>(rawBody);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Expected JSON from {endpoint}, but received non-JSON body: '{rawBody}'", ex);
        }

        if (!result.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Unexpected response structure from AI provider: {rawBody}");
        }

        string content = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()!;

        return JsonSerializer.Deserialize<OptimizerDecision>(content);
    }
}
