# Changelog

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
