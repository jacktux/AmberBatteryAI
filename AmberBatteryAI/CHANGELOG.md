# Changelog

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
