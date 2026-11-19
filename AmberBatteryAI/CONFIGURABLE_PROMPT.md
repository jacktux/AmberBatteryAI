# Configurable AI Prompt Feature

The AI optimizer prompt is now fully configurable without requiring a redeploy. You can modify the prompt at runtime through the dashboard or via API.

## Dashboard UI

The dashboard at `http://localhost:8099/` includes a "Custom AI Prompt" section that allows you to:

1. **Edit Custom Prompt**: Opens a text editor
2. **Save Custom Prompt**: Persists the custom prompt in memory (survives app restart if using persistent storage)
3. **Load Default Prompt**: Shows hints for reverting to default

### Using the Dashboard

1. Navigate to the Battery AI dashboard
2. Scroll to the "Custom AI Prompt" section
3. Click "Edit Custom Prompt"
4. Paste or modify your custom prompt in the text area
5. Click "Save Custom Prompt"
6. The custom prompt will be used on the next optimization cycle

## API Endpoints

### Get Current Custom Prompt

```bash
curl http://localhost:8099/api/prompt
```

Response:
```json
{
  "customPrompt": "Your custom prompt text here or null if using default"
}
```

### Set Custom Prompt

```bash
curl -X POST http://localhost:8099/api/prompt \
  -H "Content-Type: application/json" \
  -d '{
    "customPrompt": "You are a quantitative wholesale electricity arbitrage trader..."
  }'
```

Response:
```json
{
  "message": "Prompt updated successfully",
  "customPrompt": "Your custom prompt text here"
}
```

### Clear Custom Prompt (Use Default)

```bash
curl -X POST http://localhost:8099/api/prompt \
  -H "Content-Type: application/json" \
  -d '{
    "customPrompt": null
  }'
```

## How It Works

1. **Startup**: The application loads with no custom prompt (uses default)
2. **Runtime**: You can set a custom prompt via dashboard or API
3. **Optimization Cycle**: Before each AI call:
   - Checks if a custom prompt is set
   - If custom prompt exists and is non-empty: uses it
   - If no custom prompt: generates the default prompt with live telemetry
4. **Persistence**: Custom prompt is stored in memory (app-scoped)
   - Survives page refreshes
   - Clears on app restart

## Default Prompt

If no custom prompt is set, the system generates a default prompt that includes:

- **AI Role**: Quantitative wholesale electricity arbitrage trader
- **Objective**: Maximize net financial profit while accounting for losses and degradation
- **Trading Rules**: 7 core rules for battery optimization
- **Safety Constraints**: Grid status, SOC limits, household load protection
- **Live Telemetry**: Current time, SOC, prices, weather, solar forecast

The default prompt is dynamically generated with live values (time, prices, forecasts) at each optimization cycle.

## Prompt Best Practices

### Include These Elements

1. **Clear Objective**: Define what the AI should optimize for
2. **Constraints**: SOC limits, grid status, safety rules
3. **Decision Process**: Step-by-step logic for the AI to follow
4. **Profitability Calculation**: How to determine if a trade is worth it
5. **Telemetry Interpretation**: How to read prices, solar, weather
6. **JSON Response Format**: Must match the required output schema

### Computed Values Placeholders

The system calculates these values and replaces the placeholders before each run. Put them in a block such as `COMPUTED VALUES` and tell the AI to trust them:

```
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
- conserve_soc_at_6am = {{conserveSocAt6am}}
```

`best_feed_in_window` ends with the look-ahead result, HOLD or SELL NOW. `export_price_hysteresis` is read from `input_number.export_price_hysteresis` (default 0.02): the export automation starts at feed-in >= export_price but only stops below export_price minus this, so `effective_sell_floor` (suggested price minus hysteresis) is the price selling really continues down to. Tests B and C should use it for energy already being sold. `suggested_export_power_limit` is in watts, includes the current house load (it is a discharge limit, grid export = limit - home load). It is a price-ranked ramp: the half-hours are ranked by forecast feed-in price and the surplus goes to the best ones first, up to the maximum discharge power (`max_export_power_watts`, default 15000), so it is high in a top-priced half-hour and drops to the minimum when better prices come later. The system enforces it, so the prompt should tell the AI to copy it into `export_power_limit` instead of using a fixed value.

### Required JSON Response Format

The AI must always return valid JSON with these exact properties:

```json
{
  "mode": "string (CHARGE, DISCHARGE, or SELF_CONSUME)",
  "export_price": number,
  "export_stop_soc": number,
  "export_power_limit": number,
  "import_price": number,
  "import_stop_soc": number,
  "import_power_limit": number,
  "reasoning": "string explaining the decision"
}
```

`import_stop_soc` is overridden by the system: unless `mode` is CHARGE and the import price now is at or below the import threshold, it is set to the current SOC and the battery never charges from the grid. Solar charging needs no `import_stop_soc`. Tell the AI to output `import_stop_soc` = current SOC for any non-CHARGE mode and to describe in its reasoning only what is actually applied (no "100% to capture solar").

The system appends a "[System] Applied: ..." line to the reasoning shown on the dashboard saying whether an export is actually happening (feed-in versus export_price, SOC versus export_start_soc). Tell the AI that discharging for house load is automatic in SELF CONSUME, and that during HOLD it must not say it is discharging or exporting now.

`conserve_soc_at_6am` is the lowest SOC the battery should reach before solar takes over next morning (from the first step after 06:00 where haircut solar covers the house load, found from the Solcast tomorrow forecast; 07:30 if that forecast is unavailable), read from `input_number.battery_conserve_at_6am` (default 30). Use `{{conserveSocAt6am}}` instead of writing a fixed percentage.

### Example Custom Prompt

```
You are an energy trading AI managing a home battery.

Your goal: Maximize profit by buying cheap electricity and selling expensive.

Rules:
1. Never pay more than $0.30/kWh to charge.
2. Never sell for less than $0.50/kWh unless household load requires it.
3. Keep at least 20% SOC as emergency reserve.
4. Optimize for the evening peak (16:30-21:30).

Live Data:
- Current time: <current_time>
- Battery SOC: <soc>%
- Current import price: <import_price>
- Forecast peak export price: <peak_export>

Decision: Based on above, choose mode (CHARGE/DISCHARGE/SELF_CONSUME) and set thresholds.
Return JSON: {"mode": "...", "export_price": ..., ...}
```

## Persistence

Custom prompts are now **persisted to disk** and automatically restored on application restart.

### Storage Location

- **Windows**: `%APPDATA%\AmberBatteryAI\prompt_storage.json`
- **Linux/macOS**: `~/.config/AmberBatteryAI/prompt_storage.json` (or `$XDG_CONFIG_HOME`)
- **Home Assistant Add-on**: `/root/.config/AmberBatteryAI/prompt_storage.json`

### How It Works

1. When the application starts, it automatically loads any saved custom prompt from disk
2. When you save a custom prompt via dashboard or API, it's immediately written to the file
3. On application restart, the saved prompt is restored automatically
4. To revert to the default prompt, clear the text and save (or delete the storage file)

### Storage File Format

The prompt is stored in a simple JSON file:

```json
{
  "customPrompt": "Your full custom prompt text here..."
}
```
  - Default interval: 30 minutes
     - Can trigger immediately with "Run Optimization Now" button
  - Large prompts may impact latency with AI provider
  - AI provider rate limits still apply
  - Custom prompt file requires appropriate file system permissions to read/write

## Troubleshooting

### Prompt Not Being Used
- Verify custom prompt is set via `/api/prompt` GET
- Check that previous cycle completed successfully
- Trigger "Run Optimization Now" to test immediately

### AI Provider Rejection
- Validate JSON response schema is correct
- Check prompt character encoding (UTF-8)
- Review AI provider error logs

### Prompt Lost on Restart
- **Fixed!** Prompts are now automatically persisted to disk
- Storage file location is shown above
- Check app logs if file fails to load/save due to permissions



## Tomorrow outlook

`{{solarSummary}}` now ends with a "Tomorrow outlook" text: Solcast energy and peak for tomorrow, the Amber feed-in/import means and best times for tomorrow, and whether tomorrow's solar (after the house load, haircut and `SolarConfidence`) can refill the battery from the `conserve_soc_at_6am` target. No new placeholder is needed; custom prompts receive it through `{{solarSummary}}`.
