using System.Net.Http.Headers;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

public class BatteryOptimizerWorker(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    OptimizerState state,
    DecisionHistoryService history,
    TelemetryReader telemetry,
    TelemetryHistoryService telemetryHistory,
    DayProjectionService projection,
    EvGrabService evGrab,
    ILogger<BatteryOptimizerWorker> logger) : BackgroundService
{
    private const string DefaultAiModel = "gemini-3.5-flash-lite";

    // The run in progress, kept so a failure part-way through can still be logged with its context.
    private DecisionLogEntry? _pending;

    private const string LeverInstructions = """
        LEVERS: in addition to the properties above, include a "levers" object. The system checks every lever against its own safe range, so you cannot lower the floor, sell below break-even, or exceed the forecast; use the levers for judgement the formulas cannot make.
        "levers": {
          "strategy": "HOLD" | "SELL_NOW",
          "forecast_trust": number between 0.7 and 1.0,
          "reserve_extra_points": number between 0 and 10,
          "export_price": number (optional),
          "reason": string (one sentence, at most 25 words)
        }
        - strategy: HOLD keeps the energy for the better later window; SELL_NOW sells at the current price. The default is the look-ahead result in best_feed_in_window. Choose the other one only for a reason found in RECENT CONTEXT or the forecasts (for example later prices have been arriving lower than forecast).
        - forecast_trust: 1.0 trusts later forecast prices as given; lower values discount them. Use less than 1.0 when the forecast accuracy in RECENT CONTEXT shows later prices arriving below forecast.
        - reserve_extra_points: SOC points kept above the computed export stop SOC. It can only raise the reserve. Use it when load or solar risk looks higher than the computed plan assumes.
        - export_price: the price at which selling starts, kept near suggested_export_price. Leave it out unless you have a reason.
        """;

    private string BuildAiContext()
    {
        try
        {
            return AiContext.Build(DateTimeOffset.Now, history.Read(2), telemetryHistory.BuildDailyPnl(2), telemetryHistory.ReadBias(), telemetryHistory.Read(7));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not build the recent context for the AI.");
            return "";
        }
    }

    private static AiLeverLog BuildLeverLog(AiAuthority authority, AiLevers? requested, LeverResult? result, bool applied, bool spike, LeverEnvelope? env,
        double codeStop, double appliedPrice, double appliedStop, double appliedLimit)
    {
        string authorityText = authority == AiAuthority.On ? "on" : "shadow";
        if (spike || result is null || env is null)
            return new AiLeverLog
            {
                Authority = authorityText,
                Requested = requested,
                Notes = new List<string> { spike ? "feed-in spike: levers not used" : "no look-ahead data: levers not used" },
                Summary = spike ? "Feed-in spike: levers not used." : "No look-ahead data: levers not used."
            };

        string codeStrategy = env.CodeHold ? "HOLD" : "SELL_NOW";
        double wouldStop = Math.Round(Math.Min(100.0, codeStop + result.ReservePoints), 0);
        string reason = string.IsNullOrWhiteSpace(requested?.Reason) ? "" : $" Reason: {requested!.Reason!.Trim()}";
        string? asked = LeverPlanner.Normalize(requested?.Strategy);
        bool kept = !result.Differs && asked is not null && asked != result.Strategy;
        string summary = kept
            ? $"AI asked for {asked} but the system kept {codeStrategy} (${F(env.CodeThreshold)}).{reason}"
            : applied
            ? (result.Differs ? $"AI levers applied: {result.Strategy}, export from ${F(result.ExportPrice)} to {F(wouldStop)}% (look-ahead: {codeStrategy} ${F(env.CodeThreshold)}).{reason}" : $"AI levers agree with the look-ahead ({codeStrategy}).{reason}")
            : (result.Differs ? $"Shadow: AI chose {result.Strategy}, export from ${F(result.ExportPrice)} to {F(wouldStop)}%; applied {codeStrategy}, ${F(appliedPrice)} to {F(appliedStop)}%.{reason}" : $"Shadow: AI agrees with the look-ahead ({codeStrategy}).{reason}");
        if (result.Notes.Count > 0) summary += " (" + string.Join("; ", result.Notes) + ")";

        return new AiLeverLog
        {
            Authority = authorityText,
            Requested = requested,
            Strategy = result.Strategy,
            ExportPrice = result.ExportPrice,
            ExportStopSoc = wouldStop,
            ExportPowerLimit = result.PowerLimitWatts ?? appliedLimit,
            CodeStrategy = codeStrategy,
            CodeExportPrice = env.CodeThreshold,
            CodeExportStopSoc = Math.Round(codeStop, 0),
            Differs = result.Differs,
            Applied = applied,
            Notes = result.Notes,
            Summary = summary
        };
    }

    private const string OutputInstructions = """
        OUTPUT:
        Respond with a single JSON object only. No markdown, no text outside the JSON.
        Use exactly these property names:
        {
          "mode": "CHARGE" | "DISCHARGE" | "SELF CONSUME",
          "export_price": number,
          "export_stop_soc": number,
          "export_power_limit": number,
          "import_price": number,
          "import_stop_soc": number,
          "import_power_limit": number,
          "hot_water": "OFF" | "MINIMAL" | "FULL",
          "reasoning": string
        }
        - export_price and import_price are in $/kWh.
        - export_stop_soc and import_stop_soc are percentages between 0 and 100.
        - export_power_limit and import_power_limit are in watts.
        - export_power_limit must be suggested_export_power_limit from COMPUTED VALUES, copied unchanged.
        - hot_water is the HOT WATER section result.
        - reasoning must cover every DECISION STEP in 180 words or fewer.
        """;

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

                if (!stoppingToken.IsCancellationRequested)
                {
                    var failed = (_pending ?? new DecisionLogEntry { Timestamp = DateTimeOffset.Now }) with
                    {
                        Outcome = DecisionOutcome.Error,
                        Note = ex.Message
                    };
                    await RecordDecisionAsync(failed, stoppingToken);
                }
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

    private async Task RecordDecisionAsync(DecisionLogEntry entry, CancellationToken ct)
    {
        history.Append(entry);

        try
        {
            string entityId = config["HomeAssistant:DecisionSensorEntity"] ?? "sensor.battery_ai_decision";
            using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            publishCts.CancelAfter(TimeSpan.FromSeconds(15));
            await DecisionPublisher.PublishAsync(CreateHaClient(), entityId, entry, publishCts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not publish the AI decision to Home Assistant history.");
        }
    }

    private static string AppendAppliedNote(string? reasoning, string? mode, double exportPrice, double exportStartSoc, double soc, string feedInState, bool hold)
    {
        string note;
        if (!double.TryParse(feedInState, NumberStyles.Float, CultureInfo.InvariantCulture, out double feedIn))
            note = $"Applied: {mode}. Export starts only when feed-in reaches ${F(exportPrice)}.";
        else if (feedIn >= exportPrice && soc > exportStartSoc)
            note = $"Applied: {mode}. Feed-in ${F(feedIn)} is at or above the export price ${F(exportPrice)}, so the export automation is exporting.";
        else if (feedIn < exportPrice)
            note = $"Applied: {mode}. Not exporting: feed-in ${F(feedIn)} is below the export price ${F(exportPrice)}{(hold ? " (look-ahead HOLD for a better later window)" : "")}. The battery only covers the house load.";
        else
            note = $"Applied: {mode}. Not exporting: SOC {F(soc)}% is not above the export start SOC {F(exportStartSoc)}%.";

        return string.IsNullOrWhiteSpace(reasoning) ? note : reasoning.TrimEnd() + "\n\n[System] " + note;
    }

    private async Task<bool> IsMasterSwitchOffAsync(HttpClient client, string entityId, CancellationToken ct)
    {
        try
        {
            return string.Equals((await GetEntityStateAsync(client, entityId, ct)).Trim(), "off", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read master switch {Entity}; treating it as on.", entityId);
            return false;
        }
    }

    private const double DefaultExportPriceHysteresis = 0.02;

    // "1.2 kW" or "850 W" as watts; null when unavailable.
    private static double? ParseWatts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(' ');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) return null;
        return parts.Length > 1 && parts[1].Equals("kW", StringComparison.OrdinalIgnoreCase) ? v * 1000 : v;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private HotWaterPlanner.FullAssessment AssessFullHotWater(
        string soc,
        string hotWaterTemp,
        string importPriceState,
        double capacityKwh,
        double remainingSolarKwh,
        double avgLoadKw,
        double conservePercent,
        HotWaterPlanner.CarDraw? car = null)
    {
        static double? ParseFinite(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

        double hour = DateTime.Now.TimeOfDay.TotalHours;
        double netSolarKwh = NetSolarKwhUntil18(remainingSolarKwh, avgLoadKw, Math.Max(0, 18 - hour), config.GetValue("Optimizer:SolarConfidence", DefaultSolarConfidence));
        double kwhPerDegC = config.GetValue("Optimizer:HotWaterKwhPerDegC", 0.12);

        return HotWaterPlanner.AssessFull(
            ParseFinite(hotWaterTemp.Split(' ')[0]),
            ParseFinite(soc),
            capacityKwh,
            netSolarKwh,
            SocPlanner.RequiredKwhAt18(capacityKwh, avgLoadKw, conservePercent),
            kwhPerDegC,
            hour,
            ParseFinite(importPriceState),
            car);
    }

    private async Task RunOptimizationCycleAsync(CancellationToken ct)
    {
        var (trigger, triggerReason) = state.ConsumeTrigger();
        var entry = new DecisionLogEntry { Timestamp = DateTimeOffset.Now, Model = config["AiProvider:Model"] ?? DefaultAiModel, Trigger = trigger, TriggerReason = triggerReason };
        _pending = entry;

        var haClient = CreateHaClient();

        // Master switch: when the helper is off nothing is read, decided or written. A missing helper counts as on.
        string masterSwitchEntity = config["HomeAssistant:MasterSwitchEntity"] ?? "input_boolean.amber_battery_ai_optimizer";
        if (await IsMasterSwitchOffAsync(haClient, masterSwitchEntity, ct))
        {
            logger.LogInformation("Skipping optimization: {Entity} is off.", masterSwitchEntity);
            await evGrab.RestoreAsync(haClient, ct);
            history.Append(entry with { Outcome = DecisionOutcome.Skipped, Note = $"{masterSwitchEntity} is off; optimizer idle, settings left unchanged." });
            return;
        }

        double conservePercent = SocPlanner.ClampMorningTarget(await ReadHelperNumberAsync(haClient, config["HomeAssistant:ConserveAt6amEntity"] ?? "input_number.battery_conserve_at_6am", SocPlanner.MorningTargetPercent, ct));
        int forecastHours = config.GetValue("Optimizer:ForecastHours", 12);

        string socEntity = config["HomeAssistant:BatterySocEntity"] ?? "sensor.la_electrical_battery_soc";
        string batteryPowerEntity = config["HomeAssistant:BatteryPowerEntity"] ?? "sensor.la_electrical_battery_power";
        string homePowerEntity = config["HomeAssistant:HomePowerEntity"] ?? "sensor.la_sigenstor1_home_power";
        string gridStatusEntity = config["HomeAssistant:GridStatusEntity"] ?? "sensor.la_sigenstor_1_grid_status";
        double batteryCapacityKwh = config.GetValue("HomeAssistant:BatteryCapacityKwh", 40.0);

        string generalEntity = config["HomeAssistant:GeneralPriceEntity"] ?? "sensor.amber_express_home_general_price";
        string feedInEntity = config["HomeAssistant:FeedInPriceEntity"] ?? "sensor.amber_express_home_feed_in_price";
        string weatherEntity = config["HomeAssistant:WeatherEntity"] ?? "weather.welby_hourly";
        string solarEntity = config["HomeAssistant:SolarForecastEntity"] ?? "sensor.solcast_pv_forecast_forecast_today";
        string remainingSolarEntity = config["HomeAssistant:SolarForecastRemainingEntity"] ?? "sensor.solcast_pv_forecast_forecast_remaining_today";
        string hotWaterTankEntity = config["HomeAssistant:HotWaterTankTempEntity"] ?? "sensor.la_hot_water_tank_temperature_2";
        string hotWaterModeEntity = config["HomeAssistant:HotWaterModeEntity"] ?? "input_select.hot_water_mode";

        var soc = await GetEntityStateAsync(haClient, socEntity, ct);
        var batteryPower = await GetEntityStateWithUnitAsync(haClient, batteryPowerEntity, ct);
        var homePower = await GetEntityStateWithUnitAsync(haClient, homePowerEntity, ct);
        var hotWaterTemp = await GetEntityStateWithUnitAsync(haClient, hotWaterTankEntity, ct);
        var gridStatus = await GetEntityStateAsync(haClient, gridStatusEntity, ct);
        state.SetGridStatus(gridStatus);
        var telemetrySample = await telemetry.ReadSampleAsync(haClient, ct);

        if (!string.Equals(gridStatus.Trim(), "On grid", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Skipping AI optimization because grid status is {GridStatus}.",
                gridStatus);

            await RecordDecisionAsync(entry with
            {
                Outcome = DecisionOutcome.Skipped,
                Note = $"Grid status is '{gridStatus}', not 'On grid'; settings left unchanged.",
                Telemetry = telemetrySample,
                Inputs = new DecisionInputs
                {
                    Soc = soc,
                    BatteryPower = batteryPower,
                    HomePower = homePower,
                    HotWaterTemp = hotWaterTemp,
                    GridStatus = gridStatus
                }
            }, ct);
            return;
        }

        var generalSummary = await GetCompactAmberForecastAsync(haClient, generalEntity, forecastHours, ct);
        var feedInSummary = await GetCompactAmberForecastAsync(haClient, feedInEntity, forecastHours, ct);
        var weatherSummary = await GetCompactWeatherAsync(haClient, weatherEntity, forecastHours, logger, ct);

        string solarSummary = await GetCompactSolcastForecastAsync(
            haClient,
            solarEntity,
            remainingSolarEntity,
            forecastHours,
            logger,
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

        double avgLoadKw = Math.Clamp(config.GetValue("Optimizer:AverageLoadKw", 0.9), 0.3, 3.0);
        double remainingSolarKwh = 0;
        try
        {
            string remainingSolarState = await GetEntityStateAsync(haClient, remainingSolarEntity, ct);
            double.TryParse(remainingSolarState, NumberStyles.Float, CultureInfo.InvariantCulture, out remainingSolarKwh);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read remaining solar for floor calculation; assuming 0 kWh.");
        }

        string currentImportState;
        try
        {
            currentImportState = await GetEntityStateAsync(haClient, generalEntity, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read current import price for the hot water check.");
            currentImportState = "unknown";
        }

        double solarConfidence = Math.Clamp(config.GetValue("Optimizer:SolarConfidence", DefaultSolarConfidence), 0.1, 1.0);
        var solarSlots = (await GetSolarSlotsAsync(haClient, solarEntity, logger, ct)).ToList();
        string solarTomorrowEntity = config["HomeAssistant:SolarForecastTomorrowEntity"] ?? "sensor.solcast_pv_forecast_forecast_tomorrow";
        // Tomorrow's forecast tells when solar takes over from the battery in the morning; without it a default hour is used.
        solarSlots.AddRange(await GetSolarSlotsAsync(haClient, solarTomorrowEntity, logger, ct, quiet: true));
        double morningSolarStartHour = Math.Clamp(config.GetValue("Optimizer:MorningSolarStartHour", SocPlanner.DefaultMorningSolarStartHour), 6, 10);
        double morningLoadKw = Math.Clamp(config.GetValue("Optimizer:MorningLoadKw", 1.5), 0, 5);

        var tomorrowFeedIn = await ReadPriceForecastAsync(haClient, feedInEntity, ct);
        var tomorrowImport = await ReadPriceForecastAsync(haClient, generalEntity, ct);
        var tomorrowOutlook = TomorrowOutlook.Build(DateTime.Now, solarSlots, tomorrowFeedIn, tomorrowImport, DaytimeLoadKw, batteryCapacityKwh, conservePercent, solarConfidence);
        solarSummary += " | " + tomorrowOutlook.Text;

        var car = await evGrab.ReadStatusAsync(haClient, ct);
        var hotWaterFull = AssessFullHotWater(soc, hotWaterTemp, currentImportState, batteryCapacityKwh, remainingSolarKwh, avgLoadKw, conservePercent,
            config.GetValue("Ev:HotWaterGuard", true) ? new HotWaterPlanner.CarDraw((telemetrySample.EvPowerW ?? 0) / 1000.0, car.NeedKwh, car.Plugged, car.Priority, config.GetValue("Ev:AssumedChargeKw", 3.7)) : null);

        double maxExportWatts = config.GetValue("Optimizer:MaxExportPowerW", ExportPricePlanner.DefaultMaxExportWatts);
        double exportPowerKw = maxExportWatts / 1000.0;

        // The export and import automations use a gap between their start and stop SOC; the add-on reads those gaps.
        double exportSocHysteresis = await ReadHelperNumberAsync(haClient, config["HomeAssistant:ExportSocHysteresisEntity"] ?? "input_number.export_soc_hysteresis", SocPlanner.DefaultHysteresis, ct);
        double importSocHysteresis = await ReadHelperNumberAsync(haClient, config["HomeAssistant:ImportSocHysteresisEntity"] ?? "input_number.import_soc_hysteresis", SocPlanner.DefaultHysteresis, ct);

        double exportPriceHysteresis = await ReadHelperNumberAsync(haClient, config["HomeAssistant:ExportPriceHysteresisEntity"] ?? "input_number.export_price_hysteresis", DefaultExportPriceHysteresis, ct);
        double importPriceHysteresis = await ReadHelperNumberAsync(haClient, config["HomeAssistant:ImportPriceHysteresisEntity"] ?? "input_number.import_price_hysteresis", 0, ct);
        // The cheap and stretch buy prices: the add-on options, or helpers of the same name when they exist (no restart needed to change them).
        double cheapImportPrice = await ReadHelperNumberAsync(haClient, config["HomeAssistant:CheapImportPriceEntity"] ?? "input_number.cheap_import_price", config.GetValue("Optimizer:CheapImportPrice", 0.0), ct);
        double stretchImportPrice = await ReadHelperNumberAsync(haClient, config["HomeAssistant:StretchImportPriceEntity"] ?? "input_number.stretch_import_price", config.GetValue("Optimizer:StretchImportPrice", 0.0), ct);
        double? homeLoadWatts = telemetrySample.HomePowerW ?? ParseWatts(homePower);
        if (telemetrySample.EvPowerW is > 200)
            homePower += $" (includes car charging {telemetrySample.EvPowerW.Value / 1000:0.0} kW)";
        homePower += car.PromptText();

        var (floorKwh, surplusKwh, bestWindow, suggestedExportPrice, suggestedExportPowerLimit, lookAheadHold, leverEnvelope) = await BuildComputedValuesAsync(haClient, soc, batteryCapacityKwh, remainingSolarKwh, avgLoadKw, hotWaterFull.ReserveKwh, exportPowerKw, feedInEntity, forecastHours, exportPriceHysteresis, homeLoadWatts, conservePercent, solarConfidence, solarSlots, morningSolarStartHour, morningLoadKw, maxExportWatts, Math.Max(0.5, config.GetValue("Optimizer:IntervalMinutes", 30) / 60.0), ct);
        string exportPriceHysteresisText = F(exportPriceHysteresis);
        string effectiveSellFloorText = double.TryParse(suggestedExportPrice, NumberStyles.Float, CultureInfo.InvariantCulture, out double sugForFloor)
            ? ExportPricePlanner.EffectiveSellFloor(sugForFloor, exportPriceHysteresis).ToString("0.000", CultureInfo.InvariantCulture)
            : "unknown";

        bool floorParsed = double.TryParse(floorKwh, NumberStyles.Float, CultureInfo.InvariantCulture, out double floorKwhValue) && batteryCapacityKwh > 0;
        double floorSoc = floorParsed
            ? Math.Max(20.0, floorKwhValue / batteryCapacityKwh * 100.0)
            : 20.0;

        // Same rounding and limits as the stop SOC that is applied below, so the model sees the real start level.
        double plannedExportStopSoc = Math.Round(Math.Clamp(Math.Min(100.0, floorSoc + 2), config.GetValue("Optimizer:MinExportStopSoc", 20.0), config.GetValue("Optimizer:MaxExportStopSoc", 95.0)), 0);
        string exportStartSocText = floorParsed
            ? SocPlanner.N(SocPlanner.ExportStartSoc(plannedExportStopSoc, exportSocHysteresis))
            : "unknown";
        double? importMinStopSoc = double.TryParse(soc, NumberStyles.Float, CultureInfo.InvariantCulture, out double socNow)
            ? SocPlanner.ImportMinStopSoc(socNow, importSocHysteresis)
            : null;
        string importMinStopSocText = importMinStopSoc is null ? "unknown" : SocPlanner.N(importMinStopSoc.Value);

        bool lookAheadBuy = config.GetValue("Optimizer:LookAheadBuyEnabled", false);
        double maxImportPriceOpt = config.GetValue("Optimizer:MaxImportPrice", 0.25);
        var gridBuyPlan = GridBuyPlan.No("not evaluated");
        if (floorParsed && importMinStopSoc is not null)
        {
            double hoursTo18 = Math.Max(0, 18 - DateTime.Now.TimeOfDay.TotalHours);
            gridBuyPlan = GridBuyPlanner.Plan(new GridBuyInput(
                double.TryParse(currentImportState, NumberStyles.Float, CultureInfo.InvariantCulture, out double buyNowForPlan) ? buyNowForPlan : null,
                socNow, batteryCapacityKwh, floorKwhValue,
                NetSolarKwhUntil18(remainingSolarKwh, avgLoadKw, hoursTo18, solarConfidence),
                conservePercent, cheapImportPrice, lookAheadBuy ? 0 : stretchImportPrice,
                config.GetValue("Optimizer:MaxImportPrice", 0.25), config.GetValue("Optimizer:MaxImportStopSoc", 100.0),
                car.NeedKwh, car.Priority));

            // Look-ahead: buy only the energy that tonight and tomorrow night need, in the cheapest slots (the cheap price still buys as before).
            if (lookAheadBuy)
            {
                double netSolarNow = NetSolarKwhUntil18(remainingSolarKwh, avgLoadKw, hoursTo18, solarConfidence);
                var look = LookAheadBuyPlanner.Plan(new LookAheadBuyInput(
                    DateTime.Now, socNow, batteryCapacityKwh, floorKwhValue, conservePercent, avgLoadKw, DaytimeLoadKw, morningLoadKw, solarConfidence, morningSolarStartHour,
                    solarSlots, tomorrowImport,
                    double.TryParse(currentImportState, NumberStyles.Float, CultureInfo.InvariantCulture, out double buyNowLook) ? buyNowLook : null,
                    stretchImportPrice, maxImportPriceOpt, config.GetValue("Optimizer:MaxImportStopSoc", 100.0),
                    config.GetValue("Optimizer:GridBuyPowerWatts", 5000.0) / 1000.0, config.GetValue("Optimizer:LookAheadBuyTolerance", 0.01),
                    GridBuyPlanner.CarShortfallKwh(car.NeedKwh, car.Priority, netSolarNow, socNow / 100 * batteryCapacityKwh, floorKwhValue)));
                if (!gridBuyPlan.Buy) gridBuyPlan = look.Plan;
                state.SetNextBuyWindow(look.NextWindowStart is DateTime ws ? new DateTimeOffset(ws, TimeZoneInfo.Local.GetUtcOffset(ws)) : null);
            }
            else state.SetNextBuyWindow(null);
        }
        // Force grid charge: a switch and a target SOC, both helpers. It beats the cheap and stretch rules and the floor.
        string forceEntity = config["HomeAssistant:ForceChargeEntity"] ?? "input_boolean.force_grid_charge";
        bool forceOn = false;
        try { forceOn = string.Equals((await GetEntityStateAsync(haClient, forceEntity, ct)).Trim(), "on", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogDebug(ex, "Could not read {Entity}; force grid charge counts as off.", forceEntity); }
        double forceTarget = await ReadHelperNumberAsync(haClient, config["HomeAssistant:ForceChargeTargetEntity"] ?? "input_number.force_charge_target_soc", config.GetValue("Optimizer:ForceChargeTargetSoc", 90.0), ct);
        bool forceBuying = false;
        if (forceOn)
        {
            var forcePlan = GridBuyPlanner.Force(socNow, forceTarget, config.GetValue("Optimizer:MaxImportStopSoc", 100.0),
                double.TryParse(currentImportState, NumberStyles.Float, CultureInfo.InvariantCulture, out double buyNowForForce) ? buyNowForForce : null,
                config.GetValue("Optimizer:MaxImportPrice", 0.25));
            if (forcePlan.Buy) { gridBuyPlan = forcePlan; forceBuying = true; }
            else
            {
                gridBuyPlan = gridBuyPlan.Buy ? gridBuyPlan : forcePlan;
                if (forcePlan.Reason.Contains("already at"))
                {
                    // The target is reached: turn the switch off so a later dip does not charge again unasked.
                    try { await haClient.PostAsJsonAsync("api/services/input_boolean/turn_off", new { entity_id = forceEntity }, ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Could not turn {Entity} off.", forceEntity); }
                }
            }
        }
        bool gridBuyEnabled = cheapImportPrice > 0 || stretchImportPrice > 0 || forceOn || lookAheadBuy;

        // max_import_price caps every purchase price; say so when it is lower than a price the user set.
        var cappedPrices = new List<string>();
        if (cheapImportPrice > maxImportPriceOpt) cappedPrices.Add($"cheap price ${F(cheapImportPrice)} is capped at max_import_price ${F(maxImportPriceOpt)}");
        if (stretchImportPrice > maxImportPriceOpt) cappedPrices.Add($"stretch price ${F(stretchImportPrice)} is capped at max_import_price ${F(maxImportPriceOpt)}");
        string capNote = cappedPrices.Count > 0 ? " (" + string.Join("; ", cappedPrices) + ")" : "";

        _pending = entry = entry with
        {
            Telemetry = telemetrySample,
            FeedInForecast = await telemetry.ReadFeedInForecastAsync(haClient, forecastHours, ct),
            Inputs = new DecisionInputs
            {
                Soc = soc,
                BatteryPower = batteryPower,
                HomePower = homePower,
                HotWaterTemp = hotWaterTemp,
                GridStatus = gridStatus,
                RemainingSolarKwh = remainingSolarKwh,
                ImportPriceAndForecast = generalSummary,
                FeedInPriceAndForecast = feedInSummary,
                WeatherOutlook = weatherSummary,
                SolarForecast = solarSummary
            },
            Computed = new DecisionComputed
            {
                FloorKwh = floorKwh,
                SurplusKwh = surplusKwh,
                BestFeedInWindow = bestWindow,
                SuggestedExportPrice = suggestedExportPrice,
                SuggestedExportPowerLimit = suggestedExportPowerLimit,
                LookAheadHold = lookAheadHold,
                HotWaterFull = hotWaterFull.Text,
                ExportStartSoc = exportStartSocText,
                ImportMinStopSoc = importMinStopSocText,
                ExportSocHysteresis = exportSocHysteresis,
                ImportSocHysteresis = importSocHysteresis,
                AverageLoadKw = avgLoadKw,
                EvPowerW = telemetrySample.EvPowerW,
                EvSoc = car.SocPercent,
                EvLimit = car.LimitPercent,
                EvPluggedIn = car.Plugged,
                EvNeedKwh = car.NeedKwh,
                EvPriority = car.PriorityText,
                GridBuy = gridBuyEnabled ? (gridBuyPlan.Buy ? "BUY - " : "no - ") + gridBuyPlan.Reason + capNote : null
            }
        };

        // Load custom prompt if configured, otherwise use default
        string? customPrompt = state.GetCustomPrompt();
        string prompt;
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            // Use custom prompt but interpolate live telemetry variables
            prompt = InterpolatePromptVariables(customPrompt, forecastHours, soc, kwhBudgetSummary, batteryPower, homePower, gridStatus, generalSummary, feedInSummary, weatherSummary, solarSummary, hotWaterTemp, floorKwh, surplusKwh, bestWindow, suggestedExportPrice, suggestedExportPowerLimit, hotWaterFull.Text, exportStartSocText, importMinStopSocText, exportPriceHysteresisText, effectiveSellFloorText, F(conservePercent));
        }
        else
        {
            prompt = BuildDefaultPrompt(forecastHours, soc, kwhBudgetSummary, batteryPower, homePower, gridStatus, generalSummary, feedInSummary, weatherSummary, solarSummary, hotWaterTemp, floorKwh, surplusKwh, bestWindow, suggestedExportPrice, suggestedExportPowerLimit, hotWaterFull.Text, exportStartSocText, importMinStopSocText, exportPriceHysteresisText, effectiveSellFloorText, F(conservePercent));
        }

        var authority = AiAuthorityExtensions.Parse(config["Optimizer:AiAuthority"]);
        if (authority == AiAuthority.Off)
        {
            prompt = $"{prompt.TrimEnd()}\n\n{OutputInstructions}";
        }
        else
        {
            string aiContext = BuildAiContext();
            prompt = $"{prompt.TrimEnd()}\n\n{(aiContext.Length > 0 ? aiContext + "\n\n" : "")}{OutputInstructions}\n\n{LeverInstructions}";
        }
        if (gridBuyEnabled)
            prompt += $"\n\nGRID BUYING (automatic; the system sets the import values itself when it buys): cheap price ${F(cheapImportPrice)}, stretch price ${F(stretchImportPrice)}. Now: {(gridBuyPlan.Buy ? "BUY - " : "no buying - ")}{gridBuyPlan.Reason}.";
        _pending = entry = entry with { Prompt = prompt };

        var decision = await GetAiDecisionAsync(prompt, ct);
        if (decision is null)
        {
            await RecordDecisionAsync(entry with
            {
                Outcome = DecisionOutcome.NoDecision,
                Note = "The AI returned an empty response; settings left unchanged."
            }, ct);
            return;
        }

        var aiDecision = decision;
        var aiLevers = AiLevers.TryParse(aiDecision.Levers);
        LeverResult? leverResult = null;
        bool leverApplied = false;
        bool spikeApplied = false;
        double codeStopSoc = 0;
        var overrides = new List<string>();
        _pending = entry = entry with { AiResponse = aiDecision };

        string currentFeedInState;
        try
        {
            currentFeedInState = await GetEntityStateAsync(haClient, feedInEntity, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read current feed-in price for spike check.");
            currentFeedInState = "unknown";
        }

        bool feedInSpike = double.TryParse(currentFeedInState, NumberStyles.Float, CultureInfo.InvariantCulture, out double currentFeedIn)
            && currentFeedIn > ExportPricePlanner.ExtremeSpikeDollars;

        if (feedInSpike)
        {
            // Sell into the spike down to the hard floor; the price stays at or below live so the export starts.
            double spikePrice = double.TryParse(suggestedExportPrice, NumberStyles.Float, CultureInfo.InvariantCulture, out double spikeSuggested)
                ? Math.Round(Math.Min(spikeSuggested, currentFeedIn), 3)
                : Math.Round(currentFeedIn, 3);
            decision = decision with { ExportPrice = spikePrice, ExportStopSoc = SocPlanner.HardFloorPercent + 2 };
            spikeApplied = true;
            overrides.Add($"Feed-in spike (${F(currentFeedIn)}/kWh): export_price {F(spikePrice)}, export_stop_soc {F(decision.ExportStopSoc)} (hard floor + 2); the floor is not applied.");
        }
        else if (floorParsed && double.TryParse(suggestedExportPrice, NumberStyles.Float, CultureInfo.InvariantCulture, out double suggestedPrice))
        {
            codeStopSoc = Math.Min(100.0, floorSoc + 2);
            decision = decision with
            {
                ExportPrice = suggestedPrice,
                ExportStopSoc = codeStopSoc
            };

            // The model's levers move the export price, strategy and reserve only inside the envelope computed before it answered.
            if (authority != AiAuthority.Off && leverEnvelope is not null)
            {
                double surplusNow = double.TryParse(surplusKwh, NumberStyles.Float, CultureInfo.InvariantCulture, out double sv) ? sv : 0;
                bool canSell = surplusNow > 0 && socNow > SocPlanner.ExportStartSoc(plannedExportStopSoc, exportSocHysteresis);
                leverResult = LeverPlanner.Evaluate(leverEnvelope, aiLevers, canSell);
                if (authority == AiAuthority.On)
                {
                    leverApplied = true;
                    decision = decision with { ExportPrice = leverResult.ExportPrice, ExportStopSoc = Math.Min(100.0, codeStopSoc + leverResult.ReservePoints) };
                    lookAheadHold = leverResult.Hold;
                    if (leverResult.Differs)
                        overrides.Add($"AI levers: {leverResult.Strategy}, export_price {F(leverResult.ExportPrice)}{(leverResult.ReservePoints > 0 ? $", +{F(leverResult.ReservePoints)} points reserve" : "")} (look-ahead said {(leverEnvelope.CodeHold ? "HOLD" : "SELL NOW")} at {F(leverEnvelope.CodeThreshold)})");
                }
            }

            if (Math.Abs(decision.ExportPrice - aiDecision.ExportPrice) > 0.0005)
                overrides.Add($"export_price {F(aiDecision.ExportPrice)} -> {F(decision.ExportPrice)} (computed)");
            if (Math.Abs(decision.ExportStopSoc - aiDecision.ExportStopSoc) > 0.5)
                overrides.Add($"export_stop_soc {F(aiDecision.ExportStopSoc)} -> {F(decision.ExportStopSoc)} (computed floor + 2)");
        }
        else
        {
            logger.LogWarning("Floor ('{Floor}') or suggested export price ('{Suggested}') unavailable; disabling export.", floorKwh, suggestedExportPrice);
            decision = decision with { ExportPrice = 9.99 };
            overrides.Add("export_price 9.99: export disabled because the floor or suggested export price was unavailable.");
        }

        // The export power limit is sized by the system: the surplus is given to the best-priced half hours first, up to the maximum discharge power.
        if (int.TryParse(suggestedExportPowerLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int suggestedLimit))
        {
            if (leverApplied && leverResult?.PowerLimitWatts is int leverLimit) suggestedLimit = leverLimit;
            if (Math.Abs(decision.ExportPowerLimit - suggestedLimit) > 0.5)
                overrides.Add($"export_power_limit {F(decision.ExportPowerLimit)} -> {suggestedLimit} (computed)");
            decision = decision with { ExportPowerLimit = suggestedLimit };
        }

        // While the look-ahead says HOLD the export price (set above) is above the live price, and the mode must not
        // be DISCHARGE, so the export automation cannot discharge early.
        if (lookAheadHold && string.Equals(decision.Mode?.Trim(), "DISCHARGE", StringComparison.OrdinalIgnoreCase))
        {
            decision = decision with { Mode = "SELF CONSUME" };
            overrides.Add("mode DISCHARGE -> SELF CONSUME (look-ahead says HOLD for a better later window)");
        }

        double minExpPrice = config.GetValue("Optimizer:MinExportPrice", 0.08);
        double maxExpPrice = config.GetValue("Optimizer:MaxExportPrice", 5.00);
        double minExpSoc = config.GetValue("Optimizer:MinExportStopSoc", 20.0);
        double maxExpSoc = config.GetValue("Optimizer:MaxExportStopSoc", 95.0);

        double minImpPrice = config.GetValue("Optimizer:MinImportPrice", -1.00);
        double maxImpPrice = config.GetValue("Optimizer:MaxImportPrice", 0.25);
        double minImpSoc = config.GetValue("Optimizer:MinImportStopSoc", 30.0);
        double maxImpSoc = config.GetValue("Optimizer:MaxImportStopSoc", 100.0);

        double maxWatts = config.GetValue("Optimizer:MaxPowerWatts", 10000.0);

        double safeExportPrice = Math.Round(Math.Clamp(decision.ExportPrice, minExpPrice, maxExpPrice), 3);
        double safeExportStopSoc = Math.Round(Math.Clamp(decision.ExportStopSoc, minExpSoc, maxExpSoc), 0);
        double safeExportLimit = Math.Round(Math.Clamp(decision.ExportPowerLimit, 1000, Math.Max(1000, maxExportWatts)), 0);

        double safeImportPrice = Math.Round(Math.Clamp(decision.ImportPrice, minImpPrice, maxImpPrice), 2);
        double safeImportStopSoc = Math.Round(Math.Clamp(decision.ImportStopSoc, minImpSoc, maxImpSoc), 0);
        double safeImportLimit = Math.Round(Math.Clamp(decision.ImportPowerLimit, 1000, maxWatts), 0);

        void NoteLimit(string name, double before, double after, double tolerance)
        {
            if (Math.Abs(before - after) > tolerance)
                overrides.Add($"{name} {F(before)} -> {F(after)} (limit)");
        }

        NoteLimit("export_price", decision.ExportPrice, safeExportPrice, 0.0005);
        NoteLimit("export_stop_soc", decision.ExportStopSoc, safeExportStopSoc, 0.5);
        NoteLimit("export_power_limit", decision.ExportPowerLimit, safeExportLimit, 0.5);
        NoteLimit("import_price", decision.ImportPrice, safeImportPrice, 0.005);
        NoteLimit("import_stop_soc", decision.ImportStopSoc, safeImportStopSoc, 0.5);
        NoteLimit("import_power_limit", decision.ImportPowerLimit, safeImportLimit, 0.5);

        // Grid buying at the user's cheap or stretch price: the system sets the import values, whatever the model said.
        if (gridBuyPlan.Buy && (!feedInSpike || forceBuying))
        {
            double buyPrice = Math.Min(gridBuyPlan.Price, maxImpPrice);
            bool buyOk = true;
            if (buyPrice > safeExportPrice - 0.05)
            {
                // Import must stay $0.05 below the export price; while buying there is nothing to export, so the export price moves up.
                double neededExport = Math.Round(buyPrice + 0.055, 3);
                if (neededExport <= maxExpPrice)
                {
                    overrides.Add($"export_price {F(safeExportPrice)} -> {F(neededExport)} (kept $0.05 above the grid buy price while buying)");
                    safeExportPrice = neededExport;
                }
                else
                {
                    buyOk = false;
                    overrides.Add($"grid buy cancelled: the buy price ${F(buyPrice)} cannot stay $0.05 below the highest allowed export price ${F(maxExpPrice)}");
                }
            }

            if (buyOk)
            {
                double buyLimit = Math.Round(Math.Clamp(config.GetValue("Optimizer:GridBuyPowerWatts", 5000.0), 1000, maxWatts), 0);
                decision = decision with { Mode = "CHARGE" };
                safeImportPrice = Math.Round(buyPrice, 2);
                safeImportStopSoc = Math.Round(Math.Clamp(gridBuyPlan.TargetSocPercent, minImpSoc, maxImpSoc), 0);
                safeImportLimit = Math.Max(safeImportLimit, buyLimit);
                overrides.Add($"grid buy: {gridBuyPlan.Reason} (import_price {F(safeImportPrice)}, import_stop_soc {F(safeImportStopSoc)}, import_power_limit {F(safeImportLimit)})");
            }
        }

        // CHARGE means grid charging. If the import price now is above the import threshold, nothing will charge,
        // so the mode is SELF CONSUME and the target is not raised (the model sometimes labels solar charging CHARGE).
        if (string.Equals(decision.Mode?.Trim(), "CHARGE", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(currentImportState, NumberStyles.Float, CultureInfo.InvariantCulture, out double importNow)
            && importNow > safeImportPrice)
        {
            decision = decision with { Mode = "SELF CONSUME" };
            overrides.Add($"mode CHARGE -> SELF CONSUME (import price now ${F(importNow)} is above the import threshold ${F(safeImportPrice)}, so no grid charging would start)");
            safeImportStopSoc = Math.Round(Math.Clamp(socNow, minImpSoc, maxImpSoc), 0);
        }

        // Without grid charging the stop SOC is just the current SOC: solar charging needs no import_stop_soc, and a
        // higher value could only start grid charging when the price happens to fall below the import threshold.
        if (!string.Equals(decision.Mode?.Trim(), "CHARGE", StringComparison.OrdinalIgnoreCase))
        {
            double idleStop = Math.Round(Math.Clamp(socNow, minImpSoc, maxImpSoc), 0);
            if (Math.Abs(idleStop - safeImportStopSoc) > 0.5)
                overrides.Add($"import_stop_soc {F(safeImportStopSoc)} -> {F(idleStop)} (mode is not CHARGE, so no grid charging)");
            safeImportStopSoc = idleStop;
        }

        // The import automation only starts when SOC is below the stop SOC minus its hysteresis, so a charge
        // whose target sits at or just above the current SOC would never begin.
        if (string.Equals(decision.Mode?.Trim(), "CHARGE", StringComparison.OrdinalIgnoreCase)
            && importMinStopSoc is double minStop && safeImportStopSoc < minStop)
        {
            double requestedStop = safeImportStopSoc;
            safeImportStopSoc = Math.Round(Math.Min(maxImpSoc, minStop), 0);
            overrides.Add($"import_stop_soc {F(requestedStop)} -> {F(safeImportStopSoc)} (charging starts only below the stop SOC minus {F(importSocHysteresis)}%)");
        }

        if (safeImportPrice >= safeExportPrice - 0.05)
        {
            double requestedImportPrice = safeImportPrice;
            safeImportPrice = Math.Round(safeExportPrice - 0.10, 2);
            overrides.Add($"import_price {F(requestedImportPrice)} -> {F(safeImportPrice)} (must stay $0.05 below export_price)");
        }

        string? hotWaterOption = NormalizeHotWaterOption(decision.HotWater);
        if (hotWaterFull.Ok && hotWaterOption != "Full")
        {
            logger.LogInformation("Hot water set to Full (80 C): {Detail}", hotWaterFull.Detail);
            overrides.Add($"hot_water {hotWaterOption ?? "unset"} -> Full ({hotWaterFull.Detail})");
            hotWaterOption = "Full";
        }
        else if (!hotWaterFull.Ok && hotWaterOption == "Full")
        {
            logger.LogInformation("Hot water Full overridden to Minimal: {Detail}", hotWaterFull.Detail);
            overrides.Add($"hot_water Full -> Minimal ({hotWaterFull.Detail})");
            hotWaterOption = "Minimal";
        }

        double exportStartSoc = SocPlanner.ExportStartSoc(safeExportStopSoc, exportSocHysteresis);
        double importStartSoc = SocPlanner.ImportStartSoc(safeImportStopSoc, importSocHysteresis);

        // The model's reasoning can describe a plan the system did not apply (for example "discharge now" while the
        // look-ahead is holding), so state what is actually in force. The logged ai_response keeps the original text.
        decision = decision with { Reasoning = AppendAppliedNote(decision.Reasoning, decision.Mode, safeExportPrice, exportStartSoc, socNow, currentFeedInState, lookAheadHold) };

        _pending = entry = entry with
        {
            Overrides = overrides,
            AiLevers = authority == AiAuthority.Off ? null : BuildLeverLog(authority, aiLevers, leverResult, leverApplied, spikeApplied, leverEnvelope, codeStopSoc, safeExportPrice, safeExportStopSoc, safeExportLimit),
            Applied = new DecisionApplied
            {
                ExportPrice = safeExportPrice,
                ExportStopSoc = safeExportStopSoc,
                ExportStartSoc = exportStartSoc,
                ExportPowerLimit = safeExportLimit,
                ImportPrice = safeImportPrice,
                ImportStopSoc = safeImportStopSoc,
                ImportStartSoc = importStartSoc,
                ImportPowerLimit = safeImportLimit,
                HotWater = hotWaterOption
            }
        };

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

        if (hotWaterOption is not null)
        {
            await SetInputSelectAsync(haClient, hotWaterModeEntity, hotWaterOption, ct);
        }
        else
        {
            logger.LogWarning("AI returned unrecognised hot_water value '{HotWater}'; hot water mode unchanged.", decision.HotWater);
        }

        // Read back what Home Assistant actually holds now (helpers can clamp or reject a written value).
        _pending = entry = entry with { ActiveSetpoints = await telemetry.ReadSetpointsAsync(haClient, ct) };

        // Car solar-grab threshold (optional): the user's value stays the base. Failures never affect the run.
        var evResult = await evGrab.ApplyAsync(
            haClient,
            lookAheadHold || (string.Equals(decision.Mode?.Trim(), "DISCHARGE", StringComparison.OrdinalIgnoreCase)
                && telemetrySample.FeedInPrice >= config.GetValue("Ev:HoldFeedInPrice", 0.15)),
            telemetrySample.FeedInPrice ?? 1,
            remainingSolarKwh > 1.0,
            Math.Max(SocPlanner.HardFloorPercent, conservePercent),
            car,
            ct,
            socNow,
            batteryCapacityKwh,
            Math.Max(0, NetSolarKwhUntil18(remainingSolarKwh, avgLoadKw, Math.Max(0, 18 - DateTime.Now.TimeOfDay.TotalHours), solarConfidence)),
            conservePercent);
        if (evResult is not null)
            _pending = entry = entry with
            {
                Computed = (entry.Computed ?? new DecisionComputed()) with { EvGrabBase = evResult.Base, EvGrabApplied = evResult.Applied, EvGrabReason = evResult.Reason }
            };

        // Display-only estimate of today's cash; it never changes a setting and a failure cannot affect the run.
        // Built before the run is reported as done so the page refreshes with the new estimate.
        if (floorParsed)
        {
            await projection.UpdateAsync(haClient, new ProjectionContext
            {
                CapacityKwh = batteryCapacityKwh,
                SocPercent = socNow,
                FloorKwh = floorKwhValue,
                AvgLoadKw = avgLoadKw,
                DaytimeLoadKw = DaytimeLoadKw,
                HeatingReserveKwh = hotWaterFull.ReserveKwh,
                ConservePercent = conservePercent,
                SolarConfidence = solarConfidence,
                MorningSolarStartHour = morningSolarStartHour,
                MorningLoadKw = morningLoadKw,
                SolarSlots = solarSlots,
                ExportPrice = safeExportPrice,
                ExportPowerKw = safeExportLimit / 1000.0,
                GridCharging = string.Equals(decision.Mode?.Trim(), "CHARGE", StringComparison.OrdinalIgnoreCase),
                ImportPrice = safeImportPrice,
                ImportStopSoc = safeImportStopSoc,
                ImportPowerKw = safeImportLimit / 1000.0,
                GeneralEntity = generalEntity,
                FeedInEntity = feedInEntity
            }, ct);
        }

        string telemetrySnapshot = $"SOC: {soc}% ({kwhBudgetSummary}) | Battery: {batteryPower} | Home: {homePower} | Solar: {solarSummary}";
        state.RecordSuccess(
            new AppliedDecision(
                decision.Mode ?? string.Empty,
                safeExportPrice,
                safeExportStopSoc,
                safeExportLimit,
                safeImportPrice,
                safeImportStopSoc,
                safeImportLimit,
                decision.Reasoning,
                exportStartSoc,
                importStartSoc,
                ExportPricePlanner.EffectiveSellFloor(safeExportPrice, exportPriceHysteresis),
                Math.Round(safeImportPrice + Math.Max(0, importPriceHysteresis), 3)),
            telemetrySnapshot,
            prompt);

        await RecordDecisionAsync(entry with { Outcome = DecisionOutcome.Applied }, ct);
    }

    private async Task<double> ReadHelperNumberAsync(HttpClient client, string entityId, double fallback, CancellationToken ct)
    {
        try
        {
            string value = await GetEntityStateAsync(client, entityId, ct);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) && parsed >= 0)
                return parsed;

            logger.LogWarning("Helper {Entity} is '{Value}'; using {Fallback}.", entityId, value, fallback);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read helper {Entity}; using {Fallback}.", entityId, fallback);
        }

        return fallback;
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
        if (!json.TryGetProperty("state", out var stateProp))
            return "unknown";
        return stateProp.GetString() ?? "unknown";
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

    private static async Task<List<PricePoint>> ReadPriceForecastAsync(HttpClient client, string entityId, CancellationToken ct)
    {
        try
        {
            var json = await client.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            if (json.TryGetProperty("attributes", out var attrs) && TelemetryReader.ParseForecast(attrs, DateTimeOffset.Now, 48) is { } forecast)
                return forecast.Select(p => new PricePoint(p.Time, p.Price)).ToList();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The outlook is optional context; the decision continues without it.
        }

        return new List<PricePoint>();
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

        var fineCutoff = now.AddMinutes(30);
        var halfHourBuckets = intervals
            .GroupBy(x =>
            {
                int bucketMinutes = x.LocalTime < fineCutoff ? 5 : 30;
                return new DateTime(x.LocalTime.Year, x.LocalTime.Month, x.LocalTime.Day, x.LocalTime.Hour, (x.LocalTime.Minute / bucketMinutes) * bucketMinutes, 0);
            })
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

    private static async Task<string> GetCompactWeatherAsync(HttpClient client, string entityId, int hoursAhead, ILogger logger, CancellationToken ct)
    {
        try
        {
            var stateJson = await client.GetFromJsonAsync<JsonElement>($"api/states/{entityId}", ct);
            if (!stateJson.TryGetProperty("state", out var stateProp))
                return "unavailable";
            string currentCondition = stateProp.GetString() ?? "unknown";
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
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to fetch weather forecast for {Entity}", entityId);
            return "unavailable";
        }
    }

    private static async Task<(string FloorKwh, string SurplusKwh, string BestWindow, string SuggestedExportPrice, string SuggestedExportPowerLimit, bool LookAheadHold, LeverEnvelope? Envelope)> BuildComputedValuesAsync(
        HttpClient client,
        string soc,
        double batteryCapacityKwh,
        double remainingSolarKwh,
        double avgLoadKw,
        double heatingReserveKwh,
        double exportPowerKw,
        string feedInEntity,
        int hoursAhead,
        double exportPriceHysteresis,
        double? homeLoadWatts,
        double conservePercent,
        double solarConfidence,
        IReadOnlyList<SocPlanner.SolarSlot> solarSlots,
        double morningSolarStartHour,
        double morningLoadKw,
        double maxExportWatts,
        double coverHours,
        CancellationToken ct)
    {
        if (!double.TryParse(soc, out double socPct))
            return ("unknown", "unknown", "unknown", "unknown", "unknown", false, null);

        double storedKwh = (socPct / 100.0) * batteryCapacityKwh;
        double floorKwh = CalculateFloorKwh(batteryCapacityKwh, remainingSolarKwh, avgLoadKw, heatingReserveKwh, DateTime.Now, conservePercent, solarConfidence, solarSlots, morningSolarStartHour, morningLoadKw);
        double surplusKwh = Math.Max(0, Math.Round(storedKwh - floorKwh, 2));
        double sellableKwh = SocPlanner.SellableKwh(storedKwh, floorKwh, batteryCapacityKwh);
        var (bestWindow, suggested, powerLimit, hold, envelope) = await GetBestFeedInWindowAsync(client, feedInEntity, hoursAhead, sellableKwh, surplusKwh, exportPowerKw, exportPriceHysteresis, homeLoadWatts, maxExportWatts, coverHours, ct);

        return (floorKwh.ToString("0.##"), surplusKwh.ToString("0.##"), bestWindow, suggested, powerLimit, hold, envelope);
    }

    // Conservative daytime assumptions: solar haircut for forecast error, and a daytime house load that
    // excludes the hot water element (measured 0.9-1.2 kW), which is reserved separately.
    private const double DefaultSolarConfidence = 0.6;
    private const double DaytimeLoadKw = 1.0;

    // Solar still to come before 18:00 minus the house load until then; negative when the load outweighs the solar.
    private static double NetSolarKwhUntil18(double remainingSolarKwh, double avgLoadKw, double hoursTo18, double solarConfidence) =>
        remainingSolarKwh * 0.9 * solarConfidence - Math.Max(avgLoadKw, DaytimeLoadKw) * hoursTo18;

    private static double CalculateFloorKwh(double capacityKwh, double remainingSolarKwh, double avgLoadKw, double heatingReserveKwh, DateTime localNow, double conservePercent, double solarConfidence, IReadOnlyList<SocPlanner.SolarSlot> solarSlots, double morningSolarStartHour, double morningLoadKw)
    {
        var hourly = SocPlanner.HourlyFloorKwh(capacityKwh, localNow, solarSlots, avgLoadKw, DaytimeLoadKw, heatingReserveKwh, solarConfidence, conservePercent, morningSolarStartHour, morningLoadKw);
        if (hourly.HasValue)
            return hourly.Value;

        double hour = localNow.TimeOfDay.TotalHours;
        double netSolarKwh = NetSolarKwhUntil18(remainingSolarKwh, avgLoadKw, Math.Max(0, 18 - hour), solarConfidence);
        return SocPlanner.FloorKwh(capacityKwh, avgLoadKw, netSolarKwh, heatingReserveKwh, hour, conservePercent);
    }

    private static async Task<(string Window, string Suggested, string PowerLimit, bool Hold, LeverEnvelope? Envelope)> GetBestFeedInWindowAsync(HttpClient client, string feedInEntity, int hoursAhead, double sellableKwh, double surplusKwh, double exportPowerKw, double exportPriceHysteresis, double? homeLoadWatts, double maxExportWatts, double coverHours, CancellationToken ct)
    {
        try
        {
            var json = await client.GetFromJsonAsync<JsonElement>($"api/states/{feedInEntity}", ct);
            if (!json.TryGetProperty("attributes", out var attrs) ||
                !attrs.TryGetProperty("forecast", out var forecastArray) ||
                forecastArray.ValueKind != JsonValueKind.Array)
            {
                return ("unknown", "unknown", "unknown", false, null);
            }

            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddHours(hoursAhead);
            var slots = new List<(DateTimeOffset Time, double Value)>();

            foreach (var item in forecastArray.EnumerateArray())
            {
                if (!item.TryGetProperty("time", out var timeProp) ||
                    !DateTimeOffset.TryParse(timeProp.GetString(), out var parsedTime))
                    continue;

                if (parsedTime < now)
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

                slots.Add((parsedTime, val));
            }

            if (slots.Count == 0)
                return ("unknown", "unknown", "unknown", false, null);

            var plannerSlots = slots.Select(s => new ExportPricePlanner.Slot(s.Time, s.Value)).ToList();
            double? suggested = ExportPricePlanner.SuggestThreshold(plannerSlots, now, sellableKwh, exportPowerKw);
            if (suggested is null)
                return ("unknown", "unknown", "unknown", false, null);

            double? livePrice = double.TryParse(json.TryGetProperty("state", out var st) ? st.GetString() : null, NumberStyles.Float, CultureInfo.InvariantCulture, out double lp) ? lp : null;
            var look = ExportPricePlanner.CompareWithLater(plannerSlots, now, exportPowerKw, livePrice, sellableKwh);
            double threshold = ExportPricePlanner.ApplyLookAhead(suggested.Value, look, livePrice, plannerSlots.Max(s => s.Value), exportPriceHysteresis);
            var ramp = ExportPricePlanner.SuggestExportRamp(plannerSlots, now, livePrice, surplusKwh, ExportPricePlanner.EffectiveSellFloor(threshold, exportPriceHysteresis), maxExportWatts, homeLoadWatts, coverHours, threshold);
            int powerLimit = ramp.LimitWatts;

            // Windows are 30-minute averages, so a single 5-minute blip is not reported as the best window.
            var bestSmoothed = ExportPricePlanner.Smooth(plannerSlots).MaxBy(s => s.Value)!;
            string window = $"{bestSmoothed.Time.ToLocalTime():HH:mm} @ ${Math.Round(bestSmoothed.Value, 3)}/kWh (30-min average) | look-ahead: {look.Text(t => t.ToLocalTime().ToString("HH:mm"))}";
            window += $" | export ramp: {ramp.LimitWatts} W ({ramp.Text})";
            // What the model's levers may choose between: the limit that goes with each strategy, sized the same way as the code's own.
            int LimitFor(double thr) => ExportPricePlanner.SuggestExportRamp(plannerSlots, now, livePrice, surplusKwh, ExportPricePlanner.EffectiveSellFloor(thr, exportPriceHysteresis), maxExportWatts, homeLoadWatts, coverHours, thr).LimitWatts;
            double forecastMax = plannerSlots.Max(s => s.Value);
            double holdThreshold = ExportPricePlanner.ApplyLookAhead(suggested.Value, look with { Hold = true }, livePrice, forecastMax, exportPriceHysteresis);
            var envelope = new LeverEnvelope(livePrice, suggested.Value, threshold, look.Hold, look.LaterPrice, look.LaterDiscountedPrice, forecastMax, exportPriceHysteresis,
                LimitFor(holdThreshold), LimitFor(LeverPlanner.SellNowPrice(suggested.Value, livePrice)));
            return (window, threshold.ToString("0.000"), powerLimit.ToString(), look.Hold, envelope);
        }
        catch
        {
            return ("unavailable", "unavailable", "unavailable", false, null);
        }
    }

    private static async Task SetInputNumberAsync(HttpClient client, string entityId, double value, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("Entity ID cannot be null or empty.", nameof(entityId));

        var payload = new { entity_id = entityId, value };
        var response = await client.PostAsJsonAsync("api/services/input_number/set_value", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task SetInputSelectAsync(HttpClient client, string entityId, string option, CancellationToken ct)
    {
        var payload = new { entity_id = entityId, option };
        var response = await client.PostAsJsonAsync("api/services/input_select/select_option", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string? NormalizeHotWaterOption(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "OFF" => "Off",
        "MINIMAL" => "Minimal",
        "FULL" => "Full",
        _ => null
    };

    private static async Task<IReadOnlyList<SocPlanner.SolarSlot>> GetSolarSlotsAsync(HttpClient client, string forecastEntityId, ILogger logger, CancellationToken ct, bool quiet = false)
    {
        var slots = new List<SocPlanner.SolarSlot>();
        try
        {
            var json = await client.GetFromJsonAsync<JsonElement>($"api/states/{forecastEntityId}", ct);
            if (json.TryGetProperty("attributes", out var attrs) &&
                attrs.TryGetProperty("detailedForecast", out var array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.TryGetProperty("period_start", out var ps) &&
                        DateTimeOffset.TryParse(ps.GetString(), out var start) &&
                        item.TryGetProperty("pv_estimate", out var pv) && pv.TryGetDouble(out var kw))
                    {
                        double? Optional(string name) => item.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? Math.Max(v, 0) : null;
                        slots.Add(new SocPlanner.SolarSlot(start.ToLocalTime().DateTime, Math.Max(kw, 0), Optional("pv_estimate10"), Optional("pv_estimate90")));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (quiet)
                logger.LogInformation("Could not read {Entity}; the morning solar start uses the default hour.", forecastEntityId);
            else
                logger.LogWarning(ex, "Could not read the hourly solar forecast; using the lump-sum floor.");
        }

        return slots;
    }

    private static async Task<string> GetCompactSolcastForecastAsync(
        HttpClient client,
        string forecastEntityId,
        string remainingEntityId,
        int hoursAhead,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var forecastJson = await client.GetFromJsonAsync<JsonElement>($"api/states/{forecastEntityId}", ct);
            var remainingJson = await client.GetFromJsonAsync<JsonElement>($"api/states/{remainingEntityId}", ct);
            if (!remainingJson.TryGetProperty("state", out var stateProp))
                return "Remaining today: unknown kWh";
            string remainingKwh = stateProp.GetString() ?? "unknown";

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
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to fetch Solcast forecast for {ForecastEntity} / {RemainingEntity}", forecastEntityId, remainingEntityId);
            return "unavailable";
        }
    }

    private string BuildDefaultPrompt(
        int forecastHours,
        string soc,
        string kwhBudgetSummary,
        string batteryPower,
        string homePower,
        string gridStatus,
        string generalSummary,
        string feedInSummary,
        string weatherSummary,
        string solarSummary,
        string hotWaterTemp,
        string floorKwh,
        string surplusKwh,
        string bestWindow,
        string suggestedExportPrice,
        string suggestedExportPowerLimit,
        string hotWaterFull,
        string exportStartSoc,
        string importMinStopSoc,
        string exportPriceHysteresis,
        string effectiveSellFloor,
        string conserveSoc)
    {
        return $$"""
                You are a quantitative wholesale electricity arbitrage trader managing a Sigenstor home battery in NSW, Australia.

                Your objective is to maximize net financial profit in AUD over the next {{forecastHours}} hours while:
                - Accounting for approximately 10% round-trip battery and inverter losses.
                - Accounting for a battery degradation cost of $0.05/kWh.
                - Maintaining enough energy for household consumption, specifically ensuring the battery is at ~{{conserveSoc}}% SOC at 06:00 the next morning (never below the 20% hard floor) to cover overnight and next-morning loads.
                - Avoiding unnecessary battery cycling.
                - Following all safety rules below.

                OPERATING MODE AND SAFETY:

                - The optimizer is only called when Grid Connection Status is exactly "On grid".
                - If Grid Connection Status is anything else, do not trade, import, export, or modify battery targets. The application should skip the AI call.
                - Treat numeric telemetry as valid only when it is available and plausible.
                - If required telemetry is unavailable, choose conservative SELF CONSUME thresholds.
                - Battery Power Flow is negative (-) when the battery is charging and positive (+) when the battery is discharging.
                - Current House Load is the total household demand in watts.
                - Never intentionally reduce SOC below the configured reserve.
                - Never force a battery cycle when the expected net profit is negligible.
                - Do not invent missing forecasts, prices, load values, or solar values.

                PROFITABILITY CALCULATION:

                A grid-charging arbitrage cycle must account for battery losses and degradation.

                For a positive grid import price, calculate:

                minimum_future_export_price =
                    (grid_import_price / 0.90) + 0.05 + 0.02

                Only charge from the grid when the expected future feed-in price is at least this calculated value.

                The additional $0.02/kWh is the minimum required trading margin.

                Examples:
                - If grid import is $0.30/kWh, future export should be at least approximately $0.403/kWh.
                - If grid import is $0.50/kWh, future export should be at least approximately $0.626/kWh.

                If the calculated margin is not positive and meaningful, default to SELF CONSUME.

                If the grid import price is negative, grid charging may be profitable, but only if:
                - The battery has available capacity.
                - Charging will not prevent a more profitable later export.
                - The SOC reserve and household-load requirements remain safe.

                TRADING RULES:

                1. MINIMUM PROFIT SPREAD

                Never cycle the battery for a small margin.

                To charge from the grid for later export:
                - Compare the current or planned import price with the best realistic future feed-in price.
                - Account for 10% round-trip losses.
                - Account for $0.05/kWh degradation.
                - Require at least $0.02/kWh additional profit after those costs.
                - Do not charge from the grid merely because a future price is higher.

                Maintain a deadband at all times:

                import_price must be at least $0.10/kWh below export_price.

                2. PRE-SOLAR MORNING ARBITRAGE

                If it is morning and the Solcast hourly forecast shows enough useful solar to recharge the battery to the energy required at 18:00 (see floor_kWh) before 18:00:

                - The battery may discharge during the morning peak window of approximately 06:30-09:30.
                - Preserve enough energy to serve the household until solar generation becomes useful.
                - Do not discharge below the safe reserve unless the current feed-in price is exceptionally high.
                - Use the expected solar generation after subtracting household consumption.
                - Do not dump energy if the solar forecast is insufficient to restore the battery to the energy required at 18:00 (enough for ~{{conserveSoc}}% at 06:00 next day).

                3. SOLAR FORECAST INTERPRETATION

                - "Remaining today" is the total Solcast generation remaining for the current day.
                - The hourly Solcast values show the expected timing of that generation.
                - Do not assume that all remaining solar will arrive before 18:00.
                - Subtract expected household consumption from available solar before estimating energy available for battery charging.
                - If the hourly forecast shows little or no useful solar before 18:00, prioritize reaching and keeping the energy needed to reach ~{{conserveSoc}}% SOC at 06:00 next day via profitable low-cost grid charging if needed.
                - Evaluate profitable grid charging only when the expected future export price justifies it or when charging at cheap midday rates avoids higher evening import costs.

                4. MIDDAY SOLAR AND NEGATIVE PRICING

                Between approximately 10:00 and 15:00:

                - Prefer free solar over grid charging.
                - Before 13:00, DO NOT set import_price above $0.00/kWh if remaining Solcast solar (minus house load) exceeds the kWh needed to reach 100% SOC. Only trigger positive grid charging after 13:00 if solar underperforms due to clouds/storms and SOC is falling behind track for the evening peak.
                - If Solcast shows abundant solar that will easily fill the battery above the evening requirement before 18:00, excess battery capacity above floor_kWh may be exported during good daytime feed-in prices.
                - If feed-in prices are negative or close to zero, avoid exporting battery energy unless required to prevent solar curtailment.
                - Only import from the grid when the import price is below $0.00/kWh, or after 13:00 when profitable charging is clearly supported by the forecast and solar is insufficient.
                - Do not fill the battery from the grid if sufficient solar is expected to charge it before 18:00.
                - Do not export solar at a loss unless reducing SOC is necessary to create capacity for forecast solar generation.

                5. LOOK-AHEAD EXPORT & 06:00 TARGET

                The requirement is that SOC never falls below ~{{conserveSoc}}% before solar takes over next morning, about 07:30-08:00 (hard floor 20%). floor_kWh is the system's calculation of the minimum energy to hold now, from forecast solar, expected load and overnight needs. Do not use a fixed SOC at 18:00.

                - Only export energy above floor_kWh. Keep the rest in SELF CONSUME unless an extreme price spike (>$1.00/kWh) occurs.
                - Look ahead: best_feed_in_window includes a look-ahead comparing the current feed-in price with the best later price (discounted for forecast distance; stored energy has the same loss whether sold now or later). If it says HOLD, do not sell now; keep the energy for the later window (suggested_export_price is already set above the current price). If it says SELL NOW, sell now.
                - Between 16:30 and 21:30: Identify the highest-value realistic 1-2 hour export window. Any battery capacity above floor_kWh (for example, if solar charged the battery to 85-100%) is excess and should be exported during the evening peak at a good feed-in price.
                - Set export_price just below the highest realistic feed-in price that is likely to occur.
                - Do not discharge too early at a low price when a higher price is forecast soon.
                - If an extreme feed-in price greater than $1.00/kWh is occurring now, allow aggressive export down to the configured minimum reserve (20%); the system sets the stop SOC to 22% in that case.
                - Do not export energy that will need to be repurchased later at a higher price.

                6. OVERNIGHT AND HOUSEHOLD-LOAD PROTECTION

                After 18:00, floor_kWh is the energy needed to carry the house through the night and still be at ~{{conserveSoc}}% SOC at 06:00.

                - Protect enough energy for expected overnight and morning household consumption until solar ramps up.
                - Any SOC above what is needed for overnight/morning load can be exported overnight if feed-in prices are attractive and exceed next-morning import replacement costs.
                - Do not export below {{conserveSoc}}% SOC overnight unless the feed-in price exceeds $0.80/kWh or an extreme price event justifies it.
                - If the battery is unlikely to receive useful solar the next morning, keep a higher overnight reserve.

                7. SOC AND POWER LIMITS

                - Respect the configured minimum and maximum SOC limits.
                - Do not request export or import power below the minimum practical operating power.
                - Set export_power_limit to suggested_export_power_limit (the system ranks the half-hours by feed-in price and ramps it up in the best-priced ones and down when better prices come later, up to the maximum discharge power). Use the highest safe import power limit only when the price signal clearly justifies it.
                - Avoid rapid or unnecessary cycling.
                - If the battery is already charging or discharging, consider whether changing direction would create unnecessary losses.
                - The export automation only starts when SOC is above export_start_soc, so choose DISCHARGE only when the current SOC is above it.
                - The import automation only starts charging when SOC is below import_stop_soc minus a margin. To charge, import_stop_soc must be at least import_min_stop_soc.
                - The system overrides import_stop_soc: unless mode is CHARGE and the import price now is at or below the import threshold, it sets import_stop_soc to the current SOC and never charges from the grid. Solar charging needs no import_stop_soc. For any non-CHARGE mode output import_stop_soc = current SOC, do not claim a higher value (such as 100% to capture solar), and describe in the reasoning only what is actually applied.

                8. HOT WATER

                The hot_water mode sets the tank's target temperature: FULL = 80 degrees C, MINIMAL = 60 degrees C, OFF = no heating.
                Set hot_water to exactly one of OFF, MINIMAL, or FULL using the Hot Water Tank Temperature in the telemetry below:
                - FULL only when hot_water_full_ok in COMPUTED VALUES starts with YES. The system has already checked the tank temperature, the 11:00-16:00 solar window, the battery SOC and that the 06:00 {{conserveSoc}}% target still holds after the extra heating. Never choose FULL when it says NO.
                - OFF when the battery SOC is below 25%, or it is 16:30-21:30, or the average import price over the next 2 hours is above $0.25/kWh and the tank is at or above 45 degrees C, or heating to 60 degrees C would put the 06:00 {{conserveSoc}}% target at risk.
                - MINIMAL in all other cases, including when the tank temperature is unknown.
                - Do not heat from grid energy when the 2 hour average import price is above $0.20/kWh. Solar-powered heating is allowed at any import price.

                DECISION PROCESS:

                Before selecting thresholds:

                1. Determine whether the battery should CHARGE, DISCHARGE, or SELF CONSUME.
                2. Check current SOC against floor_kWh ({{conserveSoc}}% at 06:00 target) and expected Solcast generation.
                3. Identify any excess capacity above floor_kWh and find the best realistic export window (daytime, evening peak, or overnight).
                4. Calculate whether any proposed cycle remains profitable after losses and degradation.
                5. Select conservative SELF CONSUME thresholds when the data is ambiguous.

                The reasoning must clearly explain:
                - Whether the battery should charge, discharge, or self consume.
                - How current SOC and Solcast compare to the {{conserveSoc}}% at 06:00 target and whether the look-ahead says HOLD or SELL NOW.
                - The expected profitable price spread and highest-value forecast period.
                - The estimated overnight/morning household-load requirement.
                - The reason for the selected export_stop_soc and charge_stop_soc limits.
                Discharging to cover house load happens automatically in SELF CONSUME and is not an export. Only export_price decides when energy is sold: it starts when the live feed-in price reaches export_price. While the look-ahead says HOLD, never say the battery is being discharged or exported "now"; say the export will begin when feed-in reaches export_price.

                LIVE TELEMETRY (Injected at runtime):
                - Current Local Time: {{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}}
                - Battery SOC and Energy Budget: {{soc}}% ({{kwhBudgetSummary}})
                - Current Battery Power Flow: {{batteryPower}}
                - Current House Load: {{homePower}}
                - Grid Connection Status: {{gridStatus}}
                - General Import Price and Forecast: {{generalSummary}}
                - Feed-in Export Price and Forecast: {{feedInSummary}}
                - Weather Outlook: {{weatherSummary}}
                - Solar Forecast (Solcast): {{solarSummary}}
                - Hot Water Tank Temperature: {{hotWaterTemp}}

                COMPUTED VALUES (calculated by the system; trust these, do not recompute them)
                - floor_kWh = {{floorKwh}}
                - surplus_kWh = {{surplusKwh}}
                - best_feed_in_window = {{bestWindow}}
                - suggested_export_price = {{suggestedExportPrice}}
                - suggested_export_power_limit = {{suggestedExportPowerLimit}}
                - hot_water_full_ok = {{hotWaterFull}}
                - export_start_soc = {{exportStartSoc}}
                - import_min_stop_soc = {{importMinStopSoc}}
                - export_price_hysteresis = {{exportPriceHysteresis}}
                - effective_sell_floor = {{effectiveSellFloor}}
                - conserve_soc_at_6am = {{conserveSoc}}
                An export that has started continues until the feed-in price falls below export_price - export_price_hysteresis (effective_sell_floor), so judge Tests B and C against effective_sell_floor for energy already being sold, and against suggested_export_price for starting a sale.

                The Solar Forecast line ends with a Tomorrow outlook (Solcast, Amber prices and whether tomorrow's solar can refill the battery from conserve_soc_at_6am). If it says solar will refill the battery, selling surplus tonight has little downside; if not, only sell tonight when the price clearly beats what buying back would cost tomorrow.

                Select thresholds that maximize realistic net profit while protecting household energy availability and avoiding unprofitable battery cycling.
                """;
    }

    private string InterpolatePromptVariables(
        string customPrompt,
        int forecastHours,
        string soc,
        string kwhBudgetSummary,
        string batteryPower,
        string homePower,
        string gridStatus,
        string generalSummary,
        string feedInSummary,
        string weatherSummary,
        string solarSummary,
        string hotWaterTemp,
        string floorKwh,
        string surplusKwh,
        string bestWindow,
        string suggestedExportPrice,
        string suggestedExportPowerLimit,
        string hotWaterFull,
        string exportStartSoc,
        string importMinStopSoc,
        string exportPriceHysteresis,
        string effectiveSellFloor,
        string conserveSoc)
    {
        // Create a dictionary
        var replacements = new Dictionary<string, string>
                    {
                        { "{{forecastHours}}", forecastHours.ToString() },
                        { "{{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}}", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz") },
                        { "{{soc}}", soc },
                        { "{{kwhBudgetSummary}}", kwhBudgetSummary },
                        { "{{batteryPower}}", batteryPower },
                        { "{{homePower}}", homePower },
                        { "{{gridStatus}}", gridStatus },
                        { "{{generalSummary}}", generalSummary },
                        { "{{feedInSummary}}", feedInSummary },
                        { "{{weatherSummary}}", weatherSummary },
                        { "{{solarSummary}}", solarSummary },
                        { "{{hotWaterTemp}}", hotWaterTemp },
                        { "{{floorKwh}}", floorKwh },
                        { "{{surplusKwh}}", surplusKwh },
                        { "{{bestWindow}}", bestWindow },
                        { "{{suggestedExportPrice}}", suggestedExportPrice },
                        { "{{suggestedExportPowerLimit}}", suggestedExportPowerLimit },
                        { "{{hotWaterFull}}", hotWaterFull },
                        { "{{exportStartSoc}}", exportStartSoc },
                        { "{{importMinStopSoc}}", importMinStopSoc },
                        { "{{exportPriceHysteresis}}", exportPriceHysteresis },
                        { "{{effectiveSellFloor}}", effectiveSellFloor },
                        { "{{conserveSocAt6am}}", conserveSoc }
                    };

        string result = customPrompt;
        foreach (var kvp in replacements)
        {
            result = result.Replace(kvp.Key, kvp.Value);
        }

        return result;
    }

    // Short waits so a busy free-tier model (HTTP 503) is retried without delaying the cycle for long.
    private static readonly TimeSpan[] AiRetryDelays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };

    private static bool IsTemporaryAiFailure(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    private async Task<OptimizerDecision?> GetAiDecisionAsync(string prompt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);

        string endpoint = config["AiProvider:Endpoint"] ?? "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
        string apiKey = config["AiProvider:ApiKey"] ?? throw new InvalidOperationException("AiProvider:ApiKey is missing.");
        string model = config["AiProvider:Model"] ?? DefaultAiModel;

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
                new { role = "user", content = prompt }
            },
            response_format = new
            {
                type = "json_object"
            }
        };

        HttpResponseMessage response;
        string rawBody;
        for (int attempt = 1; ; attempt++)
        {
            response = await client.PostAsJsonAsync(endpoint, requestBody, ct);
            rawBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode || attempt >= AiRetryDelays.Length + 1 || !IsTemporaryAiFailure(response.StatusCode))
                break;

            TimeSpan delay = AiRetryDelays[attempt - 1];
            logger.LogWarning("AI provider returned HTTP {Status}; retrying in {Seconds}s (attempt {Attempt} of {Max}).",
                (int)response.StatusCode, delay.TotalSeconds, attempt + 1, AiRetryDelays.Length + 1);
            await Task.Delay(delay, ct);
        }

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

        try
        {
            return JsonSerializer.Deserialize<OptimizerDecision>(content);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Could not parse the AI response as a decision: '{content}'", ex);
        }
    }
}
