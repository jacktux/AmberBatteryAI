# AmberBatteryAI build specification

This is a prompt you can give a coding AI to rebuild the app from scratch. It reflects version 1.5.2. If it disagrees with the code or `AmberBatteryAI/CHANGELOG.md`, the code and changelog are correct.

---

Build a .NET 8 ASP.NET Core Razor Pages app with a BackgroundService, packaged as a Home Assistant add-on (Alpine Dockerfile with jq and tzdata, config.yaml, run.sh, ingress on port 8099, add-on data under /data/amber-battery-ai). It is an AI-supervised battery optimiser for a 40 kWh Sigenstor battery in NSW using Amber Electric prices and Solcast solar. Every 30 minutes (configurable) an OpenAI-compatible LLM (default gemini-3.5-flash-lite, free tier) picks thresholds. Code computes the numbers and overrides the model wherever it matters, because the model ignores arithmetic and copy instructions.

## How control works (do not change)

The add-on only writes Home Assistant helpers through the REST API: input_number.export_price, export_stop_soc, export_power_limit, import_price, import_stop_soc, import_power_limit, and input_select.hot_water_mode (Off/Minimal/Full). The user's own HA automations do the switching:

- Export starts when live feed-in >= export_price and SOC > export_stop_soc + input_number.export_soc_hysteresis. It stops when SOC <= stop SOC or price < export_price - export_price_hysteresis.
- Import is the mirror: it starts when general price <= import_price and SOC < import_stop_soc - input_number.import_soc_hysteresis.
- The model's "mode" (CHARGE/DISCHARGE/SELF CONSUME) is a label only. Nothing reads it.
- A hot water automation heats to 60 C for Minimal, 80 C for Full, and stops for Off.

## Inputs (all entity IDs configurable, with these defaults)

Read from HA /api/states: battery SOC (sensor.la_electrical_battery_soc), battery power (W, negative = charging), home power (sensor.la_sigenstor1_home_power), grid status (must equal "On grid", otherwise skip and record), hot water tank temperature (sensor.la_hot_water_tank_temperature_2), Amber general (import) and feed-in prices with their `forecast` attribute, weather (hourly forecast via weather.get_forecasts), Solcast forecast_today (`detailedForecast`) and forecast_remaining_today. Also read input_number.export_soc_hysteresis and input_number.import_soc_hysteresis (2 if unreadable). Amber forecast lists mix 5-minute and 30-minute slots and can repeat timestamps; compact them for the prompt and guard against that.

## Computed in code (sent to the model as "COMPUTED VALUES", then enforced after the reply)

- floor_kWh. The targets are 30% SOC at 06:00 next day and a 20% hard floor. Before 18:00 it is the energy needed at 18:00 for the overnight load plus the 30% morning target, less the net solar still to come (remaining Solcast x 0.9 x 0.6 minus max(avgLoad, 1.0 kW) x hoursTo18, minus the hot water heating reserve), never below 8 kWh. After 18:00 until 06:00 it is 30% + avgLoad x hoursTo06:00. Capped at capacity. avgLoad defaults to 0.75 kW (Optimizer:AverageLoadKw). Keep this in SocPlanner so it can be unit tested.
- surplus_kWh = stored - floor, minimum 0.
- suggested_export_price (ExportPricePlanner): the price of the forecast slot that completes selling the sellable energy (stored energy above the export stop SOC, not above the floor) when slots are used best-price-first at the export power limit (Optimizer:ExportPowerLimitKw, default 5 kW), minus $0.01. Forecast discount: none up to 4 h ahead, then a smooth ramp to 20% at 10 h and beyond. Skip zero-length (duplicate timestamp) slots, and cap each slot at 0.5 h.
- Look-ahead: compare the LIVE feed-in price with the best later forecast price (discounted for distance). Apply no efficiency or degradation penalty to waiting, because stored energy is discharged once either way. When a later window pays more, lift the suggested price above the current price so the energy is held, whatever the size of the surplus. Show the result (HOLD or SELL NOW) in best_feed_in_window and the prompt.
- export stop SOC = floor SOC + 2, then clamped and rounded. export_start_soc = stop SOC + export hysteresis (capped at 100). import_min_stop_soc = ceil(SOC + import hysteresis + 1), capped at 100.
- hot_water_full_ok (HotWaterPlanner): YES only if the tank is below 80 C and EITHER the import price is negative, OR all of these hold: local time is between 11:00 and 16:00, SOC >= 25%, and the projected battery at 18:00 after heating still meets the 18:00 requirement used by the floor. Energy needed = (80 - tank) x Optimizer:HotWaterKwhPerDegC (default 0.12). Otherwise NO with the reason. The reserve in the battery floor is the energy to reach 80 C when YES, otherwise the energy still needed to reach 60 C until 16:30, and zero if the tank temperature is unknown.

## Overrides after the model replies

- export_price = suggested price and export_stop_soc = floor + 2. Exception: a live feed-in price above $1.00 keeps the model's price, with stop SOC of at least 20. If the floor or suggested price can't be parsed, set export_price 9.99, which the clamp turns into the max, so export is disabled.
- Clamp everything to configurable limits: MinExportPrice 0.08, MaxExportPrice 5.00, export stop SOC 20 to 95, import price -1.00 to 0.25, import stop SOC 30 to 100, power limits 1000 to 10000 W. Round export price to 3 decimals. Keep import price at least $0.05 below the export price.
- If the model chose CHARGE, raise import_stop_soc to at least import_min_stop_soc.
- hot_water: force Full when hot_water_full_ok is YES, and force Minimal when the model says Full but the check is NO. Do not impose any 55 C or 60 C cap: Minimal stops at 60 C and Full at 80 C in the user's automation.
- Retry AI HTTP 429/500/502/503/504 after 5, 15 and 30 seconds. Other errors fail immediately and include the raw response text in the error.

## The prompt (editable on the dashboard, with a built-in default)

Placeholders: {{forecastHours}}, {{DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}}, {{soc}}, {{kwhBudgetSummary}}, {{batteryPower}}, {{homePower}}, {{gridStatus}}, {{generalSummary}}, {{feedInSummary}}, {{weatherSummary}}, {{solarSummary}}, {{hotWaterTemp}}, {{floorKwh}}, {{surplusKwh}}, {{bestWindow}}, {{suggestedExportPrice}}, {{hotWaterFull}}, {{exportStartSoc}}, {{importMinStopSoc}}. Always append fixed JSON output instructions with fields mode, export_price, export_stop_soc, export_power_limit, import_price, import_stop_soc, import_power_limit, hot_water, reasoning (180 words or fewer). Save a custom prompt in a JSON file in the data folder.

The prompt has these sections: OBJECTIVE (30% SOC at 06:00, sell surplus at the best price, 20% hard floor, efficiency 0.90, degradation $0.05/kWh, minimum margin $0.02); SAFETY; PRICE GUARD (never swap import and export prices); COMPUTED VALUES; PROFIT TESTS A (grid-charge: export x 0.90 - import - 0.07 > 0), B (export now only if current feed-in >= suggested_export_price and > $0.07), C (replacement check); RULES (limits before and after 18:00, prefer solar before 13:00, power limits, morning export, start-SOC rules); HOT WATER (Full only when hot_water_full_ok starts with YES; Off when SOC < 25%, 16:30-21:30, 2 h average import > $0.25 with tank >= 45 C, or the target is at risk; Minimal otherwise; no grid heating when 2 h average import > $0.20; only the thresholds 45/60/80 C); LIVE TELEMETRY; DECISION STEPS (6 steps); OUTPUT FIELD RULES (DISCHARGE only if surplus > 1 kWh and SOC > export_start_soc and feed-in >= suggested price; export_price copied unchanged from the computed value; import_stop_soc >= import_min_stop_soc when charging).

## Decision history (every run: applied, skipped when off-grid, no_decision, error)

- JSON Lines, one file per day (decisions-YYYY-MM-DD.jsonl) next to the saved prompt. Each line has the timestamp, outcome, model, inputs, computed values, raw AI response, applied values (including start SOCs), overrides list, and the full prompt. Delete files older than Optimizer:DecisionLogRetentionDays (default 30). Never let logging failures stop the control loop.
- Publish each run to Home Assistant: POST /api/states/sensor.battery_ai_decision (state = AI mode, or SKIPPED/NO DECISION/ERROR; attributes = applied settings, start SOCs, overrides, reasoning, hot water check; force_update true) and logbook.log with the reasoning. Escape template braces in the message.
- GET /api/decisions?days=&limit=&format=json|csv|jsonl (CSV with a UTF-8 BOM and one flat summary row per run).

## Dashboard (dark theme, one page, works behind HA ingress with relative URLs)

Next-run countdown, grid status, export and import threshold cards (with "Starts above X%" and "Starts below Y%"), AI reasoning, last prompt with a Copy button, telemetry snapshot, decision history card (last 10 runs plus CSV and JSONL download buttons), a custom prompt editor with a placeholder help list, and a Run Now button (POST /api/run-now). Add-on options in config.yaml for entities, limits, interval, model, endpoint, API key (password type), hot_water_kwh_per_deg_c (default 0.12), and the hysteresis entities. run.sh maps options to environment variables with jq.

## Build discipline

Put the pure logic in small static classes (SocPlanner, ExportPricePlanner, HotWaterPlanner) and unit test the boundaries: 4 h and 10 h haircut edges, 11:00 and 16:00 window edges, 24.9% vs 25% SOC, zero-length slots, an exact equality at the SOC target. Test the whole cycle against a mock HA and mock AI server (HTTP/1.1 keep-alive, chunked bodies) run on an isolated working directory so real data isn't touched. Keep a CHANGELOG.md and bump config.yaml's version on every release.

---

## Lessons worth keeping

- A leftover condition on a non-existent automation (`automation.sigenergy_dynamic_battery_charge_limit`) silently blocked all exports for about 18 hours. Check the automation traces after any change.
- A minimum export price clamp of 0.15 was overriding the computed price. Check clamps whenever a threshold looks stuck.
- The model got hot water wrong repeatedly, so the check lives in code.
- Home Assistant's default history keeps 10 days. Download the decision log and a history CSV regularly if you want to analyse longer periods.
