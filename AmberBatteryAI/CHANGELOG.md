# Changelog

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
