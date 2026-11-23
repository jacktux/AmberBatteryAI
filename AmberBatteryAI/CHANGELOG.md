# Changelog

## 1.17.0

### Added
- Hot water Full now gives way to a charging car. The Full check subtracts the energy the car will take until 18:00 (its current power, up to what it still needs; a plugged-in Must Charge car that has not started counts at `ev_assumed_charge_kw`, default 3.7 kW) from the projected battery. With plenty of solar left the check still allows Full; with little, Full becomes Minimal (60 C), which stops the element at once when the tank is already hotter. The detail names the car ("... leaving a projected 2.4 kWh at 18:00, with the car taking about 22 kWh (charging at 3.7 kW) against the 20.8 kWh needed"). Option `ev_hot_water_guard` (default on) turns it off. This was the 23 Nov case: the car started at 3.7 kW needing 47 kWh while hot water stayed on Full.
- Car watcher: the price watcher also reads the car charger power each minute. When it rises from under 500 W to over 1 kW while hot water is on Full, an extra run starts within a minute (trigger `car`, shown in blue on the Decisions page with the reason). It needs the price watcher on (`price_watch_enabled`) and the car power entity set. Your own Home Assistant car-plug automation can stay; the two overlap safely.
- `input_number.ev_solar_grab_threshold` now follows the priority more closely (your own value is still the base and is restored when the car is done or the feature is off):
  - Must Charge: unchanged (the floor, so the car gets all the solar and may draw the battery down).
  - Charge when you can: still the battery first. New: when SOC is above the conserve target plus `ev_plenty_points` (default 20), the threshold drops to that level (never below the floor, never above the old value), so the car can also take energy from a well-charged battery.
  - Nice to Have: the threshold is lowered just enough to finish by sunset. Shortfall = the car's remaining need minus today's spare solar (remaining Solcast x 0.9 x solar_confidence minus house load). With no shortfall your value is kept (and a look-ahead HOLD still raises it, as before). The threshold is never above your value; when even the battery above the floor cannot cover the gap it goes to the floor and the reason says so.
- New options `ev_hot_water_guard`, `ev_assumed_charge_kw`, `ev_plenty_points`.

### Notes
- The car's need uses `ev_battery_kwh` and the car SOC and charge limit sensors, so those must be right for the Nice to Have rule.
- The reason for each threshold is in the decision notes (`ev_grab_reason`).
## 1.16.0

### Added
- `average_load_kw` add-on option (`Optimizer:AverageLoadKw`, 0.3-3 kW). The house load assumed overnight and as the base for the day. The default is now 0.9 kW (was a fixed 0.75 kW in code), matching the measured 0.8-0.9 kW overnight. This raises `floor_kWh` and the overnight stop SOC slightly, about 0.2 kWh per 0.1 kW per night.
- `morning_load_kw` add-on option (`Optimizer:MorningLoadKw`, 0-5 kW, 0 turns it off). The house load assumed from 06:00 until solar takes over. The default is 1.5 kW (measured: about 1.1 kW on average with spikes to 3-5 kW from showers, cooking and the hot water element). The hourly floor balance uses it for those hours, and the solar-takeover time now waits until solar covers it, so the floor rises by roughly 0.5-1 kWh. This is aimed at mornings like 22 Nov, when the battery fell to 15.8% against a 25% target.
- Both options also apply to the day projection on the dashboard.

### Notes
- To keep the old behaviour set `average_load_kw` to 0.75 and `morning_load_kw` to 0.
- The AI levers (1.15.0) can only add reserve on top of this floor, so a realistic floor matters more than the AI's extra points.
## 1.15.0

### Added
- The AI can now make judgement calls inside limits the system computes. Besides its usual answer it returns a `levers` object: `strategy` (HOLD or SELL_NOW), `forecast_trust` (0.7-1.0, discounts later forecast prices), `reserve_extra_points` (0-10 SOC points kept above the computed stop SOC), an optional `export_price`, and a one-sentence `reason`. A pure `LeverPlanner` checks every lever against the look-ahead envelope: a HOLD price never drops below live + hysteresis + margin, SELL_NOW is refused when the live price is more than $0.02 below break-even, reserve can only go up, trust is limited to 0.7-1.0, and nothing is applied when there is nothing above the floor to sell. The floor, the 20% hard floor, the spike rules, the master switch, the import safety rules and the Full hot water check stay in code.
- New option `ai_authority` (`Optimizer:AiAuthority`): `off` (previous behaviour, no levers asked for), `shadow` (the default: the AI is asked for levers and what it would have done is logged, but the code's values are applied), `on` (the levers are applied, with the matching export power limit for the chosen strategy). The Decisions page notes and a new `ai_levers` CSV column show "Shadow: AI chose SELL_NOW, export from $0.092 ...; applied HOLD, $0.131 ..." or "AI levers applied: ...". The JSONL log has an `ai_levers` object with the request, the values that would apply, the look-ahead values and every limit that was applied.
- When `ai_authority` is not `off` the prompt gets a RECENT CONTEXT block calculated by the system: the last six runs, today's and yesterday's cash, the feed-in forecast accuracy by lead time (actual minus forecast), and the measured house load by hour. The lever instructions and this block are added by the add-on after your prompt, so a saved custom prompt needs no change.

### Notes
- Suggested use: leave `ai_authority` on `shadow` for a week, then compare the logged AI choices with what was applied and the actual prices before switching to `on`.
- The AI still chooses hot water Off or Minimal; Full is decided by the system as before.

## 1.14.0

### Added
- Every decision now records why the run happened (`trigger`: scheduled, price or manual, and `trigger_reason`), in the JSONL log, `/api/decisions` and two new trailing CSV columns. The Decisions page colours the time green for the normal 30-minute run, red when the price watcher started it, and amber for a manual run (dashboard button or a call to `/api/run-now`). Price runs start their notes with "Triggered by price move: ..." and the reason (for example "feed-in $0.15 is $0.05 above its forecast $0.1"). Older entries stay uncoloured.
- `POST /api/run-now` accepts an optional `?reason=text` that shows in the notes of that manual run, so a Home Assistant automation can say why it called.

## 1.13.0 (2026-11-20)

### Added
- The Performance page's by-hour chart has a Today / Tomorrow toggle (default Tomorrow, remembered in the browser). Today shows the rest of the day with the same solar, house load, SOC and price detail, using the same projection. Hours already past are not shown; use the Daily Profit & Loss table for actuals. New `today_by_hour` list in `/api/projection`. Display only.

## 1.12.5 (2026-11-19)

### Fixed
- The import card now reads `input_number.import_price_hysteresis` (entity key `HomeAssistant:ImportPriceHysteresisEntity`; 0 if unreadable) and shows "Stops above $(import price + hysteresis) | Starts at/below $P". Display only; new `import_stop_price` field in the status JSON.

## 1.12.4 (2026-11-19)

### Changed
- Import card price row now reads "Stops above $P | Starts at/below $P" (the app has no import price hysteresis, so both are the import price). Display only.

## 1.12.3 (2026-11-19)

### Changed
- Threshold cards use two rows: SOC text on the first, price text on the second, with the Max power value larger and bold at the right, centred across both rows (wraps below on narrow widths). Display only.

## 1.12.2 (2026-11-19)

### Added
- Dashboard cards show the price thresholds beside the SOC ones (display only). Export: "Stops below $X | Starts at/above $Y | Stop at N% SOC | Starts above M% | Max W", where X is the export price minus `export_price_hysteresis` (effective_sell_floor) and Y is the export price written to Home Assistant. Import: "Starts at/below $P | Charge to N% SOC | ...", the import price written; the app has no import price hysteresis, so there is no import stop price. New `export_stop_price` field in the status JSON. No trading change.

## 1.12.1 (2026-11-19)

### Added
- Car plug sensor: new option `ev_plugged_in_entity` (default `binary_sensor.garage_ev3_ev_battery_plug`, empty = off). When it reads `off` the car's need counts as 0, the prompt's home-power text says "car not plugged in" and the EV threshold control uses your own value. `on`, unavailable or unknown keep the old behaviour (unknown is logged). New `ev_plugged_in` field in the decision log. Prompt wording is unchanged.

## 1.12.0

### Added
- Price watcher: every minute the add-on compares the live Amber prices with the forecast the last run used (no AI call). When the feed-in price is higher than its forecast for the same time, or the buy price lower, by at least `price_watch_shift_percent` (default 10%) and `price_watch_min_shift` (default $0.02/kWh), it starts an extra optimizer run, the same as `/api/run-now`. Only the useful direction counts (feed-in up, buy price down); the export and import automations already stop on their own when prices move the other way. Feed-in must be at least `min_export_price` and the buy price at most `max_import_price`.
- A cooldown (`price_watch_cooldown_minutes`, default 10) since the last run limits the extra AI calls to at most 6 an hour; a feed-in price above $1.00 only waits 1 minute. Nothing is triggered while a run is in progress or when the master switch is off. The reason is logged ("Price watcher triggers a run: ...").
- Options: `price_watch_enabled` (default true), `price_watch_shift_percent`, `price_watch_min_shift`, `price_watch_cooldown_minutes`.

### Notes
- This replaces a Home Assistant automation that calls `/api/run-now` on price moves. An automation for Amber spikes can stay; the two can overlap safely.
- The forecast snapshot (and the live prices at that moment) is read in the first minute after each run finishes. A price that is still where the last run saw it does not trigger again; it has to move at least `price_watch_min_shift` further in the useful direction.

## 1.11.1 (2026-11-19)

### Fixed
- While the look-ahead said HOLD (export_price above the live feed-in) the export limit dropped to the 1000 W minimum, so a price that arrived early or unforecast started selling at 1000 W until the next run. The limit is now sized for the moment the live price reaches export_price: the ramp allocation of the best slot(s) at or above export_price (or, when none, the surplus sold in one half-hour), plus house load, clamped to 1000 W..maximum. Tiny surpluses and no-window cases still give 1000 W; behaviour when the threshold is already met is unchanged.

## 1.11.0 (2026-11-19)

### Changed
- Price-ranked discharge ramp. `suggested_export_power_limit` is no longer capped at 5 kW (10 kW in a spike). The remaining half-hours are ranked by forecast feed-in price (live price for the current slot, 30-minute smoothing and horizon discount for later ones; slots below `effective_sell_floor` get nothing) and the surplus above `floor_kWh` (x0.95 discharge efficiency) is given to the best slots first at up to the maximum discharge power. The limit is the power the current slot (or any slot starting before the next run) needs, plus house load, rounded up to 500 W and clamped to 1000 W..maximum. It ramps up in top-priced slots and drops to 1000 W when better slots come later.
- New option `max_export_power_watts` (`Optimizer:MaxExportPowerW`, default 15000, 1000-30000) replaces the fixed caps and the old `Optimizer:ExportPowerLimitKw`. The suggested export price and look-ahead capacity use it too, so large surpluses can give a higher price than before.
- Prompt wording only (no new placeholder): rule 7 now says the limit ramps by price up to the maximum discharge power.

### Notes
- A live feed-in above $1.00 still gives the maximum power and the 22% stop SOC. The import limit still uses `max_power_watts`.

## 1.10.0 (2026-11-19)

### Added
- Car need and priority. The add-on reads the car's SOC (`ev_soc_entity`, default `sensor.garage_ev3_ev_battery_level`), the wallbox charge limit (`ev_charge_limit_entity`, default `number.garage_ev3_ac_charging_limit`) and an `input_select.ev_charge_priority` helper (Must Charge / Nice to Have / Charge when you can; `ev_priority_entity`). The energy the car still needs uses `ev_battery_kwh` (default 81). The prompt's home-power line shows the car's SOC, limit, need and priority, and the decision log records `ev_soc`, `ev_limit`, `ev_need_kwh`, `ev_priority`.
- With `ev_control_enabled` on, the priority steers the solar-grab threshold: Must Charge lowers it to the floor (the higher of 20% and the conserve target); Charge when you can raises it by the hold offset so the car only takes solar surplus (except at zero feed-in with solar to come); Nice to Have is unchanged; a car at its limit always keeps your own value.

### Notes
- A missing helper counts as Nice to Have, so nothing changes until you create it. The prompt change is a short text addition; no placeholder is added.

## 1.9.0 (2026-11-19)

### Added
- Tomorrow's forecasts now inform today's decision. The Solar Forecast line in the prompt ends with a "Tomorrow outlook": Solcast energy and peak for tomorrow, Amber feed-in and import means and best times for tomorrow, and a computed verdict on whether tomorrow's solar (after the house load, the usual haircut and `solar_confidence`) can refill the battery from the `conserve_soc_at_6am` target. It arrives through the existing `{{solarSummary}}` placeholder, so custom prompts need no edits; the default prompt has one new guidance paragraph (sell surplus tonight freely if solar will refill, otherwise only when the price clearly beats buying back).
- Tomorrow by hour on the Performance page and in `GET /api/projection` (`tomorrow`): hourly solar, house load, expected import/export (with low/high range), battery SOC and prices, starting from the SOC today's plan leaves at midnight. It also compares that with the same day if nothing were sold today (SOC at the start, peak, time full, solar spilled, imports) and gives a plain-language verdict on whether selling today costs anything tomorrow. Display only; no export is assumed for tomorrow.

### Notes
- The projection reads about 48 h of Amber forecast; if it does not reach tomorrow the last known price is held and the page says so.
- Trading behaviour is unchanged apart from the extra prompt context.

## 1.8.0 (2026-11-19)

### Added
- Car charging awareness. The add-on reads the wallbox power (`ev_power_entity`, default `sensor.wattpilot_charging_power`; empty to turn off) and records it in the telemetry samples and decision log (`ev_power_w`). While the car draws more than 200 W the prompt's house-load line says "(includes car charging X kW)" (no new placeholder, so custom prompts need no edits). Car power is taken out of the 7-day house-load profile used by the day projection, so a charging session does not inflate it (`ev_subtract_from_load`, default on; turn it off if your home-power sensor does not include the wallbox). The projection does not forecast car charging.
- Optional control of the Node-RED car flow's solar-grab threshold (`ev_control_enabled`, default off). The flow starts taking energy from the battery at `input_number.ev_solar_grab_threshold`; with this on, your value stays the base and the add-on only changes it while it matters: it adds `ev_hold_offset_percent` (default 10, up to 100%) while the look-ahead says HOLD or a discharge is planned with feed-in at or above `ev_hold_feed_in_price` (default $0.15), and lowers it to `ev_zero_feed_in_threshold_percent` (default 60, never above your value) when feed-in is zero or negative and solar is still to come. It never goes below the conserve target or 20%. If you edit the helper yourself the new value becomes the base. Turning the option off or the master switch off puts your value back. The change is shown on the Decisions page and logged (`ev_grab_*`).

### Notes
- Nothing else about trading changes. The flow's own export boost (maximum amps while exporting) still ignores prices.
- No prompt change; saved custom prompts need no edits.

## 1.7.1 (2026-11-19)

### Added
- Today's projection on the Performance page: an estimate of today's cash (AUD) made of what has been realised so far (daily P&L) plus a simulation of the rest of the day, rebuilt on every optimizer run that applies a decision. It shows export revenue, import cost, avoided import and net cash for the realised part, the remainder and the day total, a likely low/high range, the SOC now and at midnight, and the assumptions used. It is clearly labelled as an estimate and is display only: it changes no setting and is not sent to the model.
- The simulation steps through the remaining half-hour slots to midnight from the current SOC (40 kWh, about 10% round-trip loss, 20% hard floor and the floor_kWh target). Solar serves the house first, then the battery, then export; a shortfall comes from the battery, then the grid. The planned export (surplus above floor_kWh at or above the applied export price, within the export power limit) and CHARGE-mode grid charging are included. Solar is treated as curtailed when feed-in is zero or negative.
- Inputs: the Amber import and feed-in forecasts, Solcast (P50 for the base case, P10/P90 for the range) and the average house load of the last 7 complete days by half-hour, built from the telemetry samples. Days with under 12 hours of samples are ignored; with fewer than 7 days the available days are averaged, and with none the average load setting is used. The days used are shown. The range also applies the feed-in forecast bias from `forecast-bias.json` when it exists.
- Hourly bar chart on the Performance page (inline SVG, no external library): projected import and export kWh for each remaining hour of today, with a lighter extension to the high end of the low/base/high scenarios (Solcast P10/P90 and feed-in bias) as the likelihood range. Also in the projection JSON as `hourly` (`import_kwh`, `import_low_kwh`, `import_high_kwh`, and the same for export). Day-to-day variation in house load is not part of the range.
- A day selector on the Performance projection panel (today plus the 7 days before): today shows the projection, a past day shows the actual export/import kWh, cash and avoided import for the day and an hourly bar chart of actual import and export, from the logged telemetry (limited by sample retention; partial days are flagged). New `GET /api/pnl/hourly?date=yyyy-MM-dd`.
- `GET /api/projection` (latest projection, 204 until the first run) and `GET /api/projection/log?days=` (every run's projection). Each run is appended to `projections-YYYY-MM-DD.jsonl` next to the decision log (same retention) and the latest is kept in `day-projection.json`, so projections can be compared with actuals later.
- Options `Projection:Enabled` (default true) and `Projection:LoadDays` (default 7).

### Notes
- No prompt or trading behaviour change; saved custom prompts need no edits.
- Energy left in the battery at midnight and battery degradation are not valued. Samples from before the sampler started make the realised part partial.

## 1.6.6

### Changed
- The look-ahead now compares the live feed-in price (not the 30-minute average) with the best later window. It only says HOLD when the discounted later price beats the live price by more than half a cent. Before, a live price above the later forecast could still be held because the 30-minute average included the dip after a short spike, and the HOLD threshold (live + hysteresis + margin) moved up with every live rise, so the export never started. When the live price already matches or beats the later window the result is SELL NOW and the export price is capped at the live price so the export starts. Short spikes can now start an export; the export automation's price hysteresis still stops it when the price falls away.
- Feed-in above $1.00/kWh now sells down to the hard floor: `export_price` is the computed price capped at the live price and `export_stop_soc` is 22% (hard floor + 2), with the 10000 W limit as before. Before, the model's own price and stop SOC were used, and the prompt only allowed aggressive export above $2.00/kWh. The default prompt now says $1.00. Custom prompts that mention $2.00 should be changed to $1.00 (wording only; the system sets the values).
- Between $0.50 and $1.00 the look-ahead already says SELL NOW (unchanged).
## 1.6.5

### Changed
- The morning target is now the lowest SOC the battery reaches, not the SOC at exactly 06:00. The floor calculation used to stop at 06:00, but the battery keeps supplying the house until solar covers the load (about 1.1 kWh, roughly 3 points of SOC, on a typical morning). With a 25% target the battery dipped to 22%. The hourly energy balance now runs to the first 15-minute step after 06:00 where haircut solar (x 0.9 x `solar_confidence`) meets the daytime load, so a 25% target stays near 25%. Expect `floor_kWh` and the stop SOC about 1 kWh (2-3 points) higher overnight, so about the same amount less is exported on an average night.
- The solar-takeover time comes from tomorrow's Solcast forecast (`sensor.solcast_pv_forecast_forecast_tomorrow`, option `solar_forecast_tomorrow_entity`). If that forecast is unavailable the default `morning_solar_start_hour` (7.5, i.e. 07:30, range 6-10) is used. If solar never covers the load in the first four hours after 06:00 (a dark forecast), the balance runs to 10:00, which raises the floor.
- The prompt and `CONFIGURABLE_PROMPT.md` describe the target as the lowest SOC before solar takes over. Custom prompts that say "at 06:00" still work; the numbers come from the code.

### Notes
- The hot water Full check still measures the 18:00 requirement without the pre-solar dip, so it can be up to about 1 kWh more generous than the floor. Heating energy is still reserved in the floor, so exports are not affected.
- To keep the old behaviour, set `morning_solar_start_hour` to 6 and `solar_forecast_tomorrow_entity` to an entity that does not exist.

## Unreleased

### Added
- Master switch `input_boolean.amber_battery_ai_optimizer` (entity configurable via `HomeAssistant:MasterSwitchEntity`). When it is off each cycle does nothing: no AI call and no helper or hot water writes; a "skipped" line is kept in the decision log (not published to HA). A missing helper counts as on.
- The morning target now comes from `input_number.battery_conserve_at_6am` (percent, clamped 20-100, default 30 if missing; entity configurable via `HomeAssistant:ConserveAt6amEntity`) instead of the fixed 30%. It drives floor_kWh, the 18:00 requirement and the hot water check, and appears in the prompt as `{{conserveSoc}}` / the new computed value `conserve_soc_at_6am` (`{{conserveSocAt6am}}` in custom prompts).
- Custom prompts: add `- conserve_soc_at_6am = {{conserveSocAt6am}}` under COMPUTED VALUES and replace any fixed "30% at 06:00" wording with `{{conserveSocAt6am}}%`.

### Changed
- floor_kWh (and so export_stop_soc) now comes from an hourly energy balance from now to 06:00 using the Solcast hourly forecast (solar x 0.9 x confidence minus house load per 15-minute step, kept between the 20% hard floor and capacity, ending at the conserve target) instead of one lump sum of solar until 18:00, so solar that arrives late or is too small raises the stop SOC. Falls back to the old calculation if the hourly forecast is unavailable. The solar haircut is now configurable via the `solar_confidence` add-on option (`Optimizer:SolarConfidence`, 0.1-1, default 0.6; a saved options set needs it added manually). No prompt changes needed.
- The reasoning shown on the dashboard now ends with a "[System] Applied: ..." line stating whether an export is really happening (live feed-in versus the export price, SOC versus the export start SOC), so "discharging now" cannot contradict a held export. The prompt tells the model that house-load discharge is automatic in SELF CONSUME and that during HOLD it must not claim to export now. The logged ai_response is unchanged.

## 1.6.0

### Changed
- The web page is split into four pages grouped by function (Dashboard, Decisions, Performance, Prompt) with a shared layout and navigation. Content and API endpoints are unchanged.
- The prompt (section 7 and CONFIGURABLE_PROMPT.md) now tells the model that the system sets import_stop_soc to the current SOC unless the mode is CHARGE with the import price at or below the threshold, so it should not claim a high import_stop_soc to capture solar. The override layer now enforces this for every non-CHARGE mode and logs it.

## 1.5.4

### Added
- More data per decision in the decision log (JSONL, plus flat CSV columns): `telemetry` (solar power, grid import/export power, the current 5-minute import and feed-in prices, SOC, battery and home power), `active_setpoints` (the export/import helpers and the hot water mode read back from Home Assistant after the write, so the log shows what is really active, not only what was requested) and `feed_in_forecast` (the feed-in forecast points with their lead time). Any value is null when its entity is missing.
- Telemetry sampler: every 5 minutes (`telemetry_sample_minutes`, 0 turns it off) the add-on records the same telemetry to `samples-YYYY-MM-DD.jsonl`. It only reads; nothing is written to Home Assistant. Samples are kept for `DecisionLogRetentionDays`.
- Daily P&L (`daily-pnl.csv` and `daily-pnl.json` next to the decision log, `GET /api/pnl?days=&format=json|csv`, and a table on the web page): per day, kWh and $ exported and imported, avoided import (house load served by solar or the battery, valued at the import price), net cash, the average price achieved on exports, the best feed-in price of the day and the best 30-minute average. It is built from the samples (each held until the next, at most three intervals) rather than from Home Assistant history, so it needs no recorder setup and works the same on every install. It is only as accurate as the sampling interval.
- Feed-in forecast error: each decision's forecast points are compared with the price that arrived (average of the samples inside the slot) and recorded as actual minus forecast, in lead-time buckets 0-1h, 1-2h, 2-4h, 4-8h and 8h+. `forecast-errors.csv` holds the records and `forecast-bias.json` the rolling 14-day mean error and mean absolute error per bucket (also `GET /api/forecast-bias` and a table on the web page). This is recorded and shown only; it is not used in decisions.
- New options: `grid_power_entity` (default `sensor.la_sigenstor1_grid_power`), `solar_power_entity` (default `sensor.la_sigenstor1_solar_power`), `grid_power_positive_is_import` (default true; set false if the grid sensor reads positive while exporting) and `telemetry_sample_minutes` (default 5). Check the two power entity IDs against your Home Assistant, as the defaults are guesses.

### Changed
- The add-on reads `input_number.export_price_hysteresis` (default 0.02, entity configurable via `HomeAssistant:ExportPriceHysteresisEntity`) and gives the prompt `{{exportPriceHysteresis}}` and `{{effectiveSellFloor}}` (suggested price minus hysteresis). The export automation only stops below export_price - hysteresis, so selling continues down to that floor.
- On HOLD the export price is now at least live price + hysteresis + $0.005, so it prevents a start and also ends an export already running (raising the price by less would not stop it).
- `suggested_export_power_limit` now adds the current house load (the limit is a battery discharge limit that also feeds the house; grid export = limit - home load). Falls back to the old calculation when the load is unavailable; still 1000-5000 W, or 10000 W above $1.00.
- Custom prompts: add `- export_price_hysteresis = {{exportPriceHysteresis}}` and `- effective_sell_floor = {{effectiveSellFloor}}` under COMPUTED VALUES, and have Tests B and C use effective_sell_floor for energy already being sold.
- Look-ahead no longer flips to SELL NOW on short price blips. Feed-in is smoothed with a rolling 30-minute average (5-minute slots averaged, 30-minute slots as-is), so one 5-minute slot cannot win alone or trigger a sale.
- Later prices are discounted by distance: none up to 2 h, rising linearly to a 10% haircut at 4 h, and the existing haircut beyond that. The raw price is kept alongside the discounted one.
- SELL NOW only when the current price is at least the best later price + $0.015, the best later window starts within 30 minutes, the live price is $0.50 or more, or the later window cannot carry the surplus. Otherwise HOLD.
- On HOLD the suggested export price is the highest of the base suggestion, the best later price and live + $0.005, capped at the forecast maximum.
- New computed value `suggested_export_power_limit`: 10000 W if feed-in is above $1.00, otherwise the surplus spread over the best window, rounded up to 500 W and clamped to 1000-5000 W. The prompt and the override layer now use it instead of a fixed 5000 W. Add it to any saved custom prompt (see CONFIGURABLE_PROMPT.md).
- On HOLD a DISCHARGE decision is forced to SELF CONSUME so the Sigenstor automation cannot export early.
- Added the `AmberBatteryAI.Tests` xunit project covering the above.

## 1.5.2

### Changed
- The fixed "60% at 18:00" requirement is replaced by "30% SOC at 06:00 next day" (20% hard floor kept). `floor_kWh` is now the energy needed at 18:00 for the overnight load plus the 30% morning target, less the net solar still to come (after hot water). After 18:00 it is 30% plus the load until 06:00, as before.
- The `Full` hot water check uses the same requirement at 18:00 instead of 60%.
- Look-ahead export: the current feed-in price is compared with the best later forecast price (discounted for distance). No efficiency or degradation penalty is applied to waiting, because stored energy is discharged once either way. The comparison uses the live feed-in price, not the next forecast slot. When a later window pays more, the suggested export price is set above the current price so the energy is held, whatever the size of the surplus. The result (HOLD or SELL NOW) is shown in `best_feed_in_window` and the prompt.
- A CHARGE decision is changed to SELF CONSUME when the import price now is above the import threshold, since no grid charging would start; the import stop SOC is then not raised. Before, the model could label solar charging as CHARGE and the dashboard showed a charge that never happened.

## 1.5.1

### Changed
- The add-on reads `input_number.export_soc_hysteresis` and `input_number.import_soc_hysteresis` (entities configurable with `HomeAssistant:ExportSocHysteresisEntity` and `HomeAssistant:ImportSocHysteresisEntity`; 2% is used if one cannot be read) and uses them in its decisions.
- The model is told the SOC above which exporting actually starts (`export_start_soc`) and the lowest import stop SOC at which charging can start (`import_min_stop_soc`), and its DISCHARGE and CHARGE choices must respect them.
- When the model chooses CHARGE, the import stop SOC is raised to at least the current SOC plus the import hysteresis plus 1. Before, a target at or just above the current SOC meant the import automation never started.
- The export price now plans around the energy that exporting will actually sell (everything above the export stop SOC) instead of everything above the floor, which included about 0.8 kWh that is never sold.
- The dashboard shows when each automation starts, and the start SOC is saved in the decision history.

## 1.5.0

### Changed
- The suggested export price is now set so the whole surplus is sold in the best-priced forecast slots that can carry it at the export power limit (`Optimizer:ExportPowerLimitKw`, default 5 kW). A larger surplus gets a lower threshold, so it is not left unsold, and a small one still waits for the peak. Before, the price was set from a single best window whatever the surplus.
- Forecast prices are discounted by distance: none up to 4 hours, then a smooth ramp to 20% at 10 hours and beyond. A day of data showed far-ahead overnight prices coming in 10-25% below the forecast. The ramp avoids sudden steps between neighbouring slots.
- The daytime house load assumed in the battery floor drops from 1.5 kW to 1.0 kW. The measured load without the hot water element is 0.9-1.2 kW, and the element is now reserved separately, so the old value counted it twice and held back exports.
- The default `hot_water_kwh_per_deg_c` drops from 0.2 to 0.12, from measured heating (0.095-0.13 kWh per degree). If you saved a value in the add-on options, change it there; the new default does not replace a saved value.

### Added
- Temporary AI failures (HTTP 429, 500, 502, 503, 504) are retried up to 3 times, after 5, 15 and 30 seconds, before the cycle is recorded as an error. Three of 50 runs overnight failed with 503 "high demand".

## 1.4.1

### Fixed
- The battery floor now reserves the energy still needed to heat the hot water tank to 60 C (`Minimal`) whenever `Full` is not allowed, and the energy to reach 80 C when it is. Before this, nothing was reserved outside the 11:00-16:00 window or when `Full` was refused, so the battery could be exported just before the hot water started. The reserve stops at 16:30, when heating stops, and uses the live tank temperature and `hot_water_kwh_per_deg_c`.

## 1.4.0

### Added
- Hot water heats to 80 C (`Full`) on days with enough solar. The system allows `Full` only between 11:00 and 16:00, with the battery at 25% or more, and when the 18:00 battery target (60%) still holds after deducting the energy needed to heat the tank to 80 C. Otherwise the tank stays on `Minimal` (60 C). `Full` is also allowed whenever the import price is negative.
- `{{hotWaterFull}}` placeholder and a `hot_water_full_ok` line in the computed values (YES or NO, with the reason), also saved in the decision history.
- `hot_water_kwh_per_deg_c` add-on option (default 0.2): kWh needed to warm the tank by 1 C.

### Changed
- The code enforces the `Full` decision: the model's `Full` becomes `Minimal` when the check says NO, and `Full` is set when it says YES.
- While `Full` is allowed, the energy for heating is reserved in the battery floor, so exports cannot use it.
- The old rules that forced `Off` at 60 C or more and capped `Full` at 55 C are removed (`Minimal` now stops at 60 C and `Full` at 80 C).

## 1.3.2

### Added
- Hot water control: reads the tank temperature sensor and writes the hot water mode (`Off`, `Minimal`, `Full`) to an `input_select` entity.
- `{{hotWaterTemp}}` placeholder, plus help text for all computed placeholders, in the prompt editor.
- Copy Prompt button on the dashboard to copy the latest prompt to the clipboard.
- Computed values sent to the model: `floorKwh`, `surplusKwh`, `bestWindow` and `suggestedExportPrice`.

### Changed
- Export override now uses the suggested export price and sets the export stop SoC to the floor plus 2%.
- Feed-in spikes above $1.00 keep the model's values, with the export stop SoC kept at 20% or higher.
- Battery floor is time-aware: it is based on remaining solar before 18:00 and on the expected overnight load after 18:00.

### Fixed
- Duplicate 10:00 timestamp in the Amber price lists.
- Export price falls back to 9.99 (clamped to the maximum export price) when the floor or suggested price cannot be parsed.

## 1.3.1

- Previous release.
