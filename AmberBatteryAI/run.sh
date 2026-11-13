#!/usr/bin/env sh
set -e

OPTIONS_FILE="/data/options.json"

export HomeAssistant__BaseUrl="http://supervisor/core/"
export HomeAssistant__Token="${SUPERVISOR_TOKEN}"

if [ -f "$OPTIONS_FILE" ]; then
  export AiProvider__Endpoint=$(jq -r '.ai_provider_endpoint' "$OPTIONS_FILE")
  export AiProvider__ApiKey=$(jq -r '.ai_api_key' "$OPTIONS_FILE")
  export AiProvider__Model=$(jq -r '.ai_model' "$OPTIONS_FILE")

  export HomeAssistant__BatterySocEntity=$(jq -r '.battery_soc_entity' "$OPTIONS_FILE")
  export HomeAssistant__BatteryPowerEntity=$(jq -r '.battery_power_entity' "$OPTIONS_FILE")
  export HomeAssistant__HomePowerEntity=$(jq -r '.home_power_entity' "$OPTIONS_FILE")
  export HomeAssistant__GridStatusEntity=$(jq -r '.grid_status_entity // "sensor.la_sigenstor_1_grid_status"' "$OPTIONS_FILE")
  export HomeAssistant__BatteryCapacityKwh=$(jq -r '.battery_capacity_kwh' "$OPTIONS_FILE")
  export HomeAssistant__GeneralPriceEntity=$(jq -r '.general_price_entity' "$OPTIONS_FILE")
  export HomeAssistant__FeedInPriceEntity=$(jq -r '.feed_in_price_entity' "$OPTIONS_FILE")
  export HomeAssistant__WeatherEntity=$(jq -r '.weather_entity' "$OPTIONS_FILE")
  export HomeAssistant__SolarForecastEntity=$(jq -r '.solar_forecast_entity' "$OPTIONS_FILE")
  export HomeAssistant__SolarForecastRemainingEntity=$(jq -r '.solar_forecast_remaining_entity // "sensor.solcast_pv_forecast_forecast_remaining_today"' "$OPTIONS_FILE")

  export Optimizer__IntervalMinutes=$(jq -r '.interval_minutes' "$OPTIONS_FILE")
  export Optimizer__ForecastHours=$(jq -r '.forecast_hours' "$OPTIONS_FILE")
  export Optimizer__MinExportPrice=$(jq -r '.min_export_price' "$OPTIONS_FILE")
  export Optimizer__MaxExportPrice=$(jq -r '.max_export_price' "$OPTIONS_FILE")
  export Optimizer__MinExportStopSoc=$(jq -r '.min_export_stop_soc' "$OPTIONS_FILE")
  export Optimizer__MaxExportStopSoc=$(jq -r '.max_export_stop_soc' "$OPTIONS_FILE")
  export Optimizer__MinImportPrice=$(jq -r '.min_import_price' "$OPTIONS_FILE")
  export Optimizer__MaxImportPrice=$(jq -r '.max_import_price' "$OPTIONS_FILE")
  export Optimizer__MinImportStopSoc=$(jq -r '.min_import_stop_soc' "$OPTIONS_FILE")
  export Optimizer__MaxImportStopSoc=$(jq -r '.max_import_stop_soc' "$OPTIONS_FILE")
  export Optimizer__MaxPowerWatts=$(jq -r '.max_power_watts' "$OPTIONS_FILE")
fi

export ASPNETCORE_URLS="http://0.0.0.0:8099"
exec dotnet /app/AmberBatteryAI.dll
