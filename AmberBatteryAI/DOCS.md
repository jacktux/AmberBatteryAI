# Amber Battery AI Optimizer

Every option has a name and description on the add-on Configuration tab (`translations/en.yaml`). Highlights:

- **Entities**: battery, price, solar, grid, hot water, master switch and decision sensor entity IDs. Optional ones may be left at their defaults.
- **Export**: `min/max_export_price`, `min/max_export_stop_soc`, `max_export_power_watts`.
- **Import / grid buying**: `max_import_price` caps every price the add-on will pay (cheap, stretch, look-ahead, force). `cheap_import_price` and `stretch_import_price` (0 = off) choose when to buy; `look_ahead_buy_enabled` buys only the needed energy in the cheapest slots. `max_power_watts` is the import power limit and `grid_buy_power_watts` the buy rate.
- **Price watcher**: `price_watch_*` start an early run when live prices move away from the forecast.
- **AI**: `ai_authority` is off, shadow (log only) or on.
- **Car**: `ev_*` options let the add-on steer the Node-RED solar-grab threshold from the car priority helper.

The prompt is editable on the dashboard; see `CONFIGURABLE_PROMPT.md` for placeholders.