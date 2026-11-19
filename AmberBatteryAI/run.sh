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
  export HomeAssistant__GridPowerEntity=$(jq -r '.grid_power_entity // "sensor.la_sigenstor1_grid_power"' "$OPTIONS_FILE")
  export HomeAssistant__SolarPowerEntity=$(jq -r '.solar_power_entity // "sensor.la_sigenstor1_solar_power"' "$OPTIONS_FILE")
  export HomeAssistant__GridPowerPositiveIsImport=$(jq -r 'if .grid_power_positive_is_import == null then true else .grid_power_positive_is_import end' "$OPTIONS_FILE")

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
  export Optimizer__MaxExportPowerW=$(jq -r '.max_export_power_watts // 15000' "$OPTIONS_FILE")
  export Telemetry__SampleIntervalMinutes=$(jq -r '.telemetry_sample_minutes // 5' "$OPTIONS_FILE")
  export Optimizer__HotWaterKwhPerDegC=$(jq -r '.hot_water_kwh_per_deg_c // 0.12' "$OPTIONS_FILE")
  export Optimizer__SolarConfidence=$(jq -r '.solar_confidence // 0.6' "$OPTIONS_FILE")
  export HomeAssistant__SolarForecastTomorrowEntity=$(jq -r '.solar_forecast_tomorrow_entity // "sensor.solcast_pv_forecast_forecast_tomorrow"' "$OPTIONS_FILE")
  export Optimizer__MorningSolarStartHour=$(jq -r '.morning_solar_start_hour // 7.5' "$OPTIONS_FILE")
  export HomeAssistant__EvPowerEntity=$(jq -r '.ev_power_entity // "sensor.wattpilot_charging_power"' "$OPTIONS_FILE")
  export Ev__SubtractFromLoad=$(jq -r 'if .ev_subtract_from_load == null then true else .ev_subtract_from_load end' "$OPTIONS_FILE")
  export Ev__Enabled=$(jq -r 'if .ev_control_enabled == null then false else .ev_control_enabled end' "$OPTIONS_FILE")
  export HomeAssistant__EvGrabThresholdEntity=$(jq -r '.ev_grab_threshold_entity // "input_number.ev_solar_grab_threshold"' "$OPTIONS_FILE")
  export Ev__HoldOffsetPercent=$(jq -r '.ev_hold_offset_percent // 10' "$OPTIONS_FILE")
  export Ev__ZeroFeedInThresholdPercent=$(jq -r '.ev_zero_feed_in_threshold_percent // 60' "$OPTIONS_FILE")
  export Ev__HoldFeedInPrice=$(jq -r '.ev_hold_feed_in_price // 0.15' "$OPTIONS_FILE")
  export HomeAssistant__EvSocEntity=$(jq -r '.ev_soc_entity // "sensor.garage_ev3_ev_battery_level"' "$OPTIONS_FILE")
  export HomeAssistant__EvChargeLimitEntity=$(jq -r '.ev_charge_limit_entity // "number.garage_ev3_ac_charging_limit"' "$OPTIONS_FILE")
  export HomeAssistant__EvPriorityEntity=$(jq -r '.ev_priority_entity // "input_select.ev_charge_priority"' "$OPTIONS_FILE")
  export Ev__BatteryKwh=$(jq -r '.ev_battery_kwh // 81' "$OPTIONS_FILE")
fi

export ASPNETCORE_URLS="http://0.0.0.0:8099"
exec dotnet /app/AmberBatteryAI.dll
