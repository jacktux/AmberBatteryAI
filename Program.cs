using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddHostedService<BatteryOptimizerWorker>();

var host = builder.Build();
host.Run();

public class BatteryOptimizerWorker(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<BatteryOptimizerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int intervalMinutes = config.GetValue("Optimizer:IntervalMinutes", 30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Starting AI battery optimization cycle...");
                await RunOptimizationCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during optimization cycle.");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task RunOptimizationCycleAsync(CancellationToken ct)
    {
        var haClient = CreateHaClient();
        int forecastHours = config.GetValue("Optimizer:ForecastHours", 12);

        string socEntity = config["HomeAssistant:BatterySocEntity"] ?? "sensor.la_electrical_battery_soc";
        string generalEntity = config["HomeAssistant:GeneralPriceEntity"] ?? "sensor.amber_express_home_general_price";
        string feedInEntity = config["HomeAssistant:FeedInPriceEntity"] ?? "sensor.amber_express_home_feed_in_price";
        string weatherEntity = config["HomeAssistant:WeatherEntity"] ?? "weather.forecast_home";
        string? solarEntity = config["HomeAssistant:SolarForecastEntity"];

        // 1. Gather current state & 12-hour trimmed forecasts
        var soc = await GetEntityStateAsync(haClient, socEntity, ct);
        var generalSummary = await GetCompactAmberForecastAsync(haClient, generalEntity, forecastHours, ct);
        var feedInSummary = await GetCompactAmberForecastAsync(haClient, feedInEntity, forecastHours, ct);
        var weatherSummary = await GetCompactWeatherAsync(haClient, weatherEntity, ct);

        string solarSummary = "Not configured";
        if (!string.IsNullOrWhiteSpace(solarEntity))
        {
            solarSummary = await GetEntityStateAsync(haClient, solarEntity, ct);
        }

        // 2. Build prompt with trimmed telemetry
        string prompt = $$"""
            You are an automated home battery arbitrage controller in NSW, Australia.
            Analyze the current battery SOC, Amber wholesale electricity prices (next {{forecastHours}} hours), and weather/solar outlook.
            Determine the optimal Home Assistant helper thresholds for the next 30-60 minutes.

            Strategy Rules:
            - Heavy solar generation forecast soon + high SOC: lower export_stop_soc and export_price to sell stored energy before solar curtailment or negative midday feed-in.
            - Cloudy/rainy weather forecast or high evening peak prices ahead: preserve battery (raise export_stop_soc and export_price) and lower import_price only if cheap/negative grid power is available.
            - Always keep import_price strictly below export_price to prevent simultaneous charge/discharge cycling.
            - Keep power_limit between 2000W and 10000W depending on urgency and spike duration.

            Live Telemetry:
            - Current Local Time: {{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}}
            - Battery SOC: {{soc}}%
            - General Import Price & Forecast: {{generalSummary}}
            - Feed-in Export Price & Forecast: {{feedInSummary}}
            - Weather Outlook: {{weatherSummary}}
            - Solar Forecast Sensor: {{solarSummary}}
            """;

        // 3. Query OpenAI-compatible AI Endpoint with Structured Outputs
        var decision = await GetAiDecisionAsync(prompt, ct);
        if (decision is null) return;

        // 4. Hard safety clamping
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

        // Enforce at least $0.10 deadband between import and export prices
        if (safeImportPrice >= safeExportPrice - 0.05)
        {
            safeImportPrice = Math.Round(safeExportPrice - 0.10, 2);
        }

        logger.LogInformation(
            "AI Decision -> Export: >=${ExpPrice} down to {ExpSoc}% @ {ExpW}W | Import: <=${ImpPrice} up to {ImpSoc}% @ {ImpW}W | Reason: {Reason}",
            safeExportPrice, safeExportStopSoc, safeExportLimit,
            safeImportPrice, safeImportStopSoc, safeImportLimit,
            decision.Reasoning);

        // 5. Push updated thresholds to Home Assistant helpers
        await SetInputNumberAsync(haClient, "input_number.export_price", safeExportPrice, ct);
        await SetInputNumberAsync(haClient, "input_number.export_stop_soc", safeExportStopSoc, ct);
        await SetInputNumberAsync(haClient, "input_number.export_power_limit", safeExportLimit, ct);

        await SetInputNumberAsync(haClient, "input_number.import_price", safeImportPrice, ct);
        await SetInputNumberAsync(haClient, "input_number.import_stop_soc", safeImportStopSoc, ct);
        await SetInputNumberAsync(haClient, "input_number.import_power_limit", safeImportLimit, ct);
    }

    private HttpClient CreateHaClient()
    {
        var client = httpClientFactory.CreateClient();
        string baseUrl = config["HomeAssistant:BaseUrl"] ?? "http://homeassistant.local:8123";
        string token = config["HomeAssistant:Token"] ?? throw new InvalidOperationException("HomeAssistant:Token is missing.");

        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<string> GetEntityStateAsync(HttpClient client, string entityId, CancellationToken ct)
    {
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/states/{entityId}", ct);
        return json.GetProperty("state").GetString() ?? "unknown";
    }

    private static async Task<string> GetCompactAmberForecastAsync(HttpClient client, string entityId, int hoursAhead, CancellationToken ct)
    {
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/states/{entityId}", ct);
        string currentState = json.GetProperty("state").GetString() ?? "unknown";

        if (!json.TryGetProperty("attributes", out var attrs))
            return $"Current: {currentState}";

        JsonElement forecastArray = default;
        bool hasForecast = attrs.TryGetProperty("forecasts", out forecastArray) ||
                           attrs.TryGetProperty("forecast", out forecastArray);

        if (!hasForecast || forecastArray.ValueKind != JsonValueKind.Array)
            return $"Current: {currentState}";

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(hoursAhead);
        var compactEntries = new List<string>();

        foreach (var item in forecastArray.EnumerateArray())
        {
            string? timeStr = item.TryGetProperty("start_time", out var st) ? st.GetString() :
                              item.TryGetProperty("nem_date", out var nd) ? nd.GetString() : null;

            if (DateTimeOffset.TryParse(timeStr, out var startTime))
            {
                if (startTime < now.AddMinutes(-30))
                    continue;
                if (startTime > cutoff)
                    break;
            }

            double perKwh = item.TryGetProperty("per_kwh", out var pk) && pk.TryGetDouble(out var d) ? Math.Round(d, 3) : 0;
            string spike = item.TryGetProperty("spike_status", out var sp) ? sp.GetString() ?? "none" : "none";

            string localLabel = DateTimeOffset.TryParse(timeStr, out var parsed)
                ? parsed.ToLocalTime().ToString("HH:mm")
                : timeStr ?? "?";

            compactEntries.Add(spike != "none" ? $"{localLabel}=${perKwh}({spike})" : $"{localLabel}=${perKwh}");
        }

        if (compactEntries.Count > 48)
        {
            compactEntries = compactEntries.Where((_, idx) => idx % 3 == 0).ToList();
        }

        return $"Current=${currentState} | Next {hoursAhead}h: [{string.Join(", ", compactEntries)}]";
    }

    private static async Task<string> GetCompactWeatherAsync(HttpClient client, string entityId, CancellationToken ct)
    {
        try
        {
            var json = await client.GetFromJsonAsync<JsonElement>($"/api/states/{entityId}", ct);
            string condition = json.GetProperty("state").GetString() ?? "unknown";
            double temp = json.TryGetProperty("attributes", out var attrs) &&
                          attrs.TryGetProperty("temperature", out var t) && t.TryGetDouble(out var tv)
                ? tv : double.NaN;

            return double.IsNaN(temp) ? condition : $"{condition}, {temp}C";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static async Task SetInputNumberAsync(HttpClient client, string entityId, double value, CancellationToken ct)
    {
        var payload = new { entity_id = entityId, value };
        var response = await client.PostAsJsonAsync("/api/services/input_number/set_value", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<OptimizerDecision?> GetAiDecisionAsync(string prompt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        string endpoint = config["AiProvider:Endpoint"] ?? "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
        string apiKey = config["AiProvider:ApiKey"] ?? throw new InvalidOperationException("AiProvider:ApiKey is missing.");
        string model = config["AiProvider:Model"] ?? "gemini-2.5-flash";

        // Only attach Bearer header if an API key is provided (allows local Ollama without a key)
        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "none")
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var requestBody = new
        {
            model,
            //reasoning_effort = "none",
            messages = new[]
            {
                new { role = "system", content = "You output strictly valid JSON matching the requested schema." },
                new { role = "user", content = prompt }
            },
            response_format = new
            {
                type = "json_object"
                //type = "json_schema",
                //json_schema = new
                //{
                //    name = "battery_thresholds",
                //    strict = true,
                //    schema = new
                //    {
                //        type = "object",
                //        properties = new
                //        {
                //            export_price = new { type = "number" },
                //            export_stop_soc = new { type = "number" },
                //            export_power_limit = new { type = "number" },
                //            import_price = new { type = "number" },
                //            import_stop_soc = new { type = "number" },
                //            import_power_limit = new { type = "number" },
                //            reasoning = new { type = "string" }
                //        },
                //        required = new[]
                //        {
                //            "export_price", "export_stop_soc", "export_power_limit",
                //            "import_price", "import_stop_soc", "import_power_limit", "reasoning"
                //        },
                //        additionalProperties = false
                //    }
                //}
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

public record OptimizerDecision(
    [property: JsonPropertyName("export_price")] double ExportPrice,
    [property: JsonPropertyName("export_stop_soc")] double ExportStopSoc,
    [property: JsonPropertyName("export_power_limit")] double ExportPowerLimit,
    [property: JsonPropertyName("import_price")] double ImportPrice,
    [property: JsonPropertyName("import_stop_soc")] double ImportStopSoc,
    [property: JsonPropertyName("import_power_limit")] double ImportPowerLimit,
    [property: JsonPropertyName("reasoning")] string Reasoning
);
