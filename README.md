# AmberBatteryAI (.NET 8 Worker Service)

Supervisory AI controller that adjusts Home Assistant `input_number` helpers for Sigenstor battery import/export arbitrage using Amber Express wholesale forecasts and GitHub Models.

## Quick Start
1. Edit `appsettings.json`:
   - Set `HomeAssistant:BaseUrl` and `HomeAssistant:Token` (Long-Lived Access Token from your HA Profile).
   - Set `GitHubModels:Token` (GitHub Personal Access Token with `models:read` permission).
   - Optionally add your Solcast or Forecast.Solar entity ID to `HomeAssistant:SolarForecastEntity`.
2. Run locally:
   ```bash
   dotnet run
   ```
3. Or build & run with Docker:
   ```bash
   docker build -t amber-battery-ai .
   docker run -d --name amber-battery-ai --restart unless-stopped amber-battery-ai
   ```

## Decision history
Every optimizer run (applied, skipped because the grid is off, no decision, or error) is recorded:
- **Home Assistant History / Activity:** the add-on sets `sensor.battery_ai_decision` (state = AI mode, attributes = applied settings, overrides and reasoning) and adds an Activity entry with the reasoning. The sensor is created through the REST API, so it is empty after a Home Assistant restart until the next run.
- **Add-on storage:** one JSON line per run in `decisions-YYYY-MM-DD.jsonl` next to the saved prompt, including the full prompt (all telemetry and forecasts), the raw AI response, the settings actually applied, and any overrides. Files older than `Optimizer:DecisionLogRetentionDays` (default 30) are deleted.
- **Dashboard:** the "AI Decision History" card lists recent runs and downloads the last 7 days as CSV (summary) or JSON Lines (everything). The same data is at `api/decisions?days=7&format=csv|jsonl|json`.