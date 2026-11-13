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
