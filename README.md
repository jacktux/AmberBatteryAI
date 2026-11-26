# Amber Battery AI Optimizer

A Home Assistant add-on (C# / .NET 8) that sets Sigenstor battery export and import prices, stop SOCs and hot water mode from Amber prices and the Solcast forecast. Code planners compute the floors, export price, hot water and grid buying; a Gemini (OpenAI-compatible) model adds bounded "levers".

## Install
Add this repository to the Home Assistant add-on store, install **Amber Battery AI**, set your AI key and entity names on the Configuration tab, then start it. See `AmberBatteryAI/DOCS.md` for the options and `AmberBatteryAI/CHANGELOG.md` for history.

## Develop
- `dotnet test AmberBatteryAI.Tests` runs the tests (including config drift checks).
- Local runs read `AmberBatteryAI/appsettings.json` (set `HomeAssistant:Token` and `AiProvider:ApiKey`).
- `docs/SPEC.md` describes how to rebuild the app.