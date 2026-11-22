using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

public enum AiAuthority { Off, Shadow, On }

public static class AiAuthorityExtensions
{
    /// <summary>off | shadow | on; anything else (or nothing) is shadow, which changes no setting.</summary>
    public static AiAuthority Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "off" => AiAuthority.Off,
        "on" => AiAuthority.On,
        _ => AiAuthority.Shadow
    };
}

/// <summary>What the model asks for. Every field is optional and is checked against <see cref="LeverEnvelope"/> before use.</summary>
public record AiLevers
{
    [JsonPropertyName("strategy")] public string? Strategy { get; init; }
    [JsonPropertyName("forecast_trust")] public double? ForecastTrust { get; init; }
    [JsonPropertyName("reserve_extra_points")] public double? ReserveExtraPoints { get; init; }
    [JsonPropertyName("export_price")] public double? ExportPrice { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }

    /// <summary>Reads the model's "levers" object leniently: a missing, malformed or odd-typed value gives null or a null field, never an exception.</summary>
    public static AiLevers? TryParse(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return null;

        double? Number(string name)
        {
            if (!obj.TryGetProperty(name, out var p)) return null;
            double v;
            if (p.ValueKind == JsonValueKind.Number) v = p.GetDouble();
            else if (p.ValueKind != JsonValueKind.String || !double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
            return double.IsFinite(v) ? v : null;
        }

        string? Text(string name) => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        return new AiLevers
        {
            Strategy = Text("strategy"),
            ForecastTrust = Number("forecast_trust"),
            ReserveExtraPoints = Number("reserve_extra_points"),
            ExportPrice = Number("export_price"),
            Reason = Text("reason")
        };
    }
}

/// <summary>The safe range the system computed before the model answered. Everything the levers can do stays inside it.</summary>
public record LeverEnvelope(
    double? LivePrice,
    double BaseThreshold,
    double CodeThreshold,
    bool CodeHold,
    double LaterPrice,
    double LaterDiscountedPrice,
    double ForecastMaxPrice,
    double PriceHysteresis,
    int? HoldLimitWatts,
    int? SellNowLimitWatts);

public record LeverResult(bool Hold, double ExportPrice, double ReservePoints, int? PowerLimitWatts, bool Differs, List<string> Notes)
{
    public string Strategy => Hold ? "HOLD" : "SELL_NOW";
}

/// <summary>What was asked, what would apply, and what did, for the decision history.</summary>
public record AiLeverLog
{
    [JsonPropertyName("authority")] public string Authority { get; init; } = "";
    [JsonPropertyName("requested")] public AiLevers? Requested { get; init; }
    [JsonPropertyName("strategy")] public string? Strategy { get; init; }
    [JsonPropertyName("export_price")] public double? ExportPrice { get; init; }
    [JsonPropertyName("export_stop_soc")] public double? ExportStopSoc { get; init; }
    [JsonPropertyName("export_power_limit")] public double? ExportPowerLimit { get; init; }
    [JsonPropertyName("code_strategy")] public string? CodeStrategy { get; init; }
    [JsonPropertyName("code_export_price")] public double? CodeExportPrice { get; init; }
    [JsonPropertyName("code_export_stop_soc")] public double? CodeExportStopSoc { get; init; }
    [JsonPropertyName("differs")] public bool Differs { get; init; }
    [JsonPropertyName("applied")] public bool Applied { get; init; }
    [JsonPropertyName("notes")] public List<string> Notes { get; init; } = new();
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
}

/// <summary>
/// Turns the model's levers into export settings that stay inside the code-computed envelope. The model decides HOLD or
/// SELL_NOW, how far to trust later forecast prices, and how much extra reserve to keep; it can never go below the
/// floor, sell at a price below break-even, or ask for more than the forecast offers.
/// </summary>
public static class LeverPlanner
{
    public const double MinTrust = 0.7;
    public const double MaxReservePoints = 10;
    public const double PriceBandDollars = 0.02;
    public const double SellNowSlackDollars = 0.02;

    public static string? Normalize(string? strategy) => strategy?.Trim().Replace(' ', '_').Replace('-', '_').ToUpperInvariant() switch
    {
        "HOLD" => "HOLD",
        "SELL_NOW" or "SELLNOW" or "SELL" => "SELL_NOW",
        _ => null
    };

    /// <summary>The price that starts a sale now: the break-even price, or the live price when that is a little lower.</summary>
    public static double SellNowPrice(double baseThreshold, double? livePrice) =>
        Math.Round(livePrice is double live ? Math.Min(baseThreshold, live) : baseThreshold, 3);

    public static LeverResult Evaluate(LeverEnvelope env, AiLevers? levers, bool canSell)
    {
        var notes = new List<string>();
        var code = new LeverResult(env.CodeHold, env.CodeThreshold, 0, null, false, notes);

        if (levers is null)
        {
            notes.Add("no levers returned: look-ahead values kept");
            return code;
        }

        if (!canSell)
        {
            notes.Add("nothing above the floor to sell: look-ahead values kept");
            return code;
        }

        string? asked = Normalize(levers.Strategy);
        if (levers.Strategy is not null && asked is null)
            notes.Add($"strategy '{levers.Strategy}' not recognised: look-ahead result used");

        double trust = levers.ForecastTrust is double t ? Math.Clamp(t, MinTrust, 1.0) : 1.0;
        if (levers.ForecastTrust is double raw && Math.Abs(raw - trust) > 1e-9)
            notes.Add($"forecast_trust {raw:0.##} limited to {trust:0.##}");

        double reserve = levers.ReserveExtraPoints is double r ? Math.Clamp(r, 0, MaxReservePoints) : 0;
        if (levers.ReserveExtraPoints is double rawReserve && Math.Abs(rawReserve - reserve) > 1e-9)
            notes.Add($"reserve_extra_points {rawReserve:0.#} limited to {reserve:0.#}");

        bool hold = asked switch { "HOLD" => true, "SELL_NOW" => false, _ => env.CodeHold };

        // Without an explicit strategy a lower trust can show the later window is no better than selling now.
        double? live = env.LivePrice;
        if (asked is null && hold && live is double l0 && env.LaterDiscountedPrice * trust <= l0 + ExportPricePlanner.LiveHoldMarginDollars)
        {
            hold = false;
            notes.Add($"forecast_trust {trust:0.##} puts the later window (${env.LaterDiscountedPrice * trust:0.###}) no better than now (${l0:0.###}): SELL_NOW");
        }

        double price;
        int? limit;
        if (hold)
        {
            double floor = (live ?? env.CodeThreshold) + Math.Max(0, env.PriceHysteresis) + ExportPricePlanner.HoldMarginDollars;
            double ceiling = Math.Max(env.ForecastMaxPrice, floor);
            double target = Math.Max(env.BaseThreshold, env.LaterPrice * trust);
            if (levers.ExportPrice is double p)
            {
                double clamped = Math.Clamp(p, env.BaseThreshold - PriceBandDollars, ceiling);
                if (Math.Abs(clamped - p) > 0.0005) notes.Add($"export_price {p:0.###} limited to {clamped:0.###}");
                target = clamped;
            }

            price = Math.Round(Math.Min(Math.Max(target, floor), ceiling), 3);
            limit = env.HoldLimitWatts;
        }
        else
        {
            double breakEven = env.BaseThreshold;
            if (live is double l && l < breakEven - SellNowSlackDollars)
            {
                notes.Add($"SELL_NOW refused: live ${l:0.###} is more than ${SellNowSlackDollars:0.##} below break-even ${breakEven:0.###}; look-ahead values kept");
                return code;
            }

            double sellPrice = SellNowPrice(breakEven, live);
            if (levers.ExportPrice is double p)
            {
                double clamped = Math.Clamp(p, breakEven - SellNowSlackDollars, sellPrice);
                if (Math.Abs(clamped - p) > 0.0005) notes.Add($"export_price {p:0.###} limited to {clamped:0.###}");
                sellPrice = clamped;
            }

            price = Math.Round(sellPrice, 3);
            limit = env.SellNowLimitWatts;
        }

        bool sameStrategy = hold == env.CodeHold;
        bool differs = !sameStrategy || Math.Abs(price - env.CodeThreshold) > 0.0005 || reserve > 0;
        return new LeverResult(hold, price, reserve, sameStrategy ? null : limit, differs, notes);
    }
}

/// <summary>Short, code-computed facts that give the model memory: what happened recently and how reliable forecasts have been.</summary>
public static class AiContext
{
    public static string Build(
        DateTimeOffset now,
        IReadOnlyList<DecisionLogEntry> recent,
        IReadOnlyList<DailyPnl> daily,
        ForecastBiasReport? bias,
        IReadOnlyList<TelemetrySample> samples)
    {
        var lines = new List<string>();

        var runs = recent.Where(e => e.Outcome == DecisionOutcome.Applied && e.Applied is not null).TakeLast(6).ToList();
        if (runs.Count > 0)
        {
            lines.Add("- Last runs (oldest first):");
            foreach (var e in runs)
            {
                string strategy = e.AiLevers?.Strategy ?? (e.Computed?.LookAheadHold == true ? "HOLD" : "SELL_NOW");
                lines.Add($"  {e.Timestamp.ToLocalTime():ddd HH:mm} [{e.Trigger ?? "scheduled"}] SOC {e.Telemetry?.SocPercent:0.#}%, feed-in ${e.Telemetry?.FeedInPrice:0.###}, export from ${e.Applied!.ExportPrice:0.###} to {e.Applied.ExportStopSoc:0}%, {strategy}");
            }
        }

        var today = daily.FirstOrDefault(d => d.Date == DateOnly.FromDateTime(now.LocalDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var yesterday = daily.FirstOrDefault(d => d.Date == DateOnly.FromDateTime(now.LocalDateTime.AddDays(-1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (today is not null || yesterday is not null)
        {
            string Day(string name, DailyPnl? d) => d is null ? "" : $"{name}: exported {d.ExportKwh:0.#} kWh for ${d.ExportRevenue:0.00} (average ${d.AchievedSellPrice ?? 0:0.###}/kWh), imported {d.ImportKwh:0.#} kWh for ${d.ImportCost:0.00}";
            lines.Add("- Cash so far: " + string.Join("; ", new[] { Day("today", today), Day("yesterday", yesterday) }.Where(s => s.Length > 0)));
        }

        var buckets = bias?.Buckets.Where(b => b.Count >= 5 && b.MeanError is not null).ToList();
        if (buckets is { Count: > 0 })
            lines.Add("- Feed-in forecast accuracy over the last " + bias!.WindowDays + " days, actual minus forecast ($/kWh) by how far ahead: "
                + string.Join(", ", buckets.Select(b => $"{b.Bucket} {b.MeanError:+0.000;-0.000;0.000} (n={b.Count})")));

        var byHour = samples
            .Where(s => s.HomePowerW is double w && w >= 0 && !(s.EvPowerW is > 200))
            .GroupBy(s => s.Timestamp.ToLocalTime().Hour)
            .Where(g => g.Count() >= 6)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key:00}h {g.Average(s => s.HomePowerW!.Value) / 1000:0.0}")
            .ToList();
        if (byHour.Count > 0)
            lines.Add("- Measured house load by hour, kW, last days, car excluded, includes hot water heating: " + string.Join(", ", byHour));

        return lines.Count == 0 ? "" : "RECENT CONTEXT (calculated by the system from logged data)\n" + string.Join("\n", lines);
    }
}
