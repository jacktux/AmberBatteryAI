using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

/// <summary>
/// Decides when a live Amber price has moved far enough in the useful direction, against the forecast the last run used,
/// to be worth a new optimizer run. Only feed-in rising and the buy price falling count: those are the moves that can
/// start an export or an import. A price that is still where the last run saw it does not count again. The opposite moves are already handled by the export and import automations.
/// </summary>
public static class PriceWatchRules
{
    public const double SpikeDollars = 1.00;
    public static readonly TimeSpan SpikeCooldown = TimeSpan.FromMinutes(1);

    /// <summary>A forecast point older than this no longer describes the current price.</summary>
    public static readonly TimeSpan MaxPointAge = TimeSpan.FromMinutes(35);

    public record Settings(
        double ShiftPercent = 10,
        double MinShiftDollars = 0.02,
        double MinExportPrice = 0.08,
        double MaxImportPrice = 0.25,
        TimeSpan? Cooldown = null)
    {
        public TimeSpan CooldownOrDefault => Cooldown ?? TimeSpan.FromMinutes(10);
    }

    public record Decision(bool Run, string Reason);

    public const double CarOffWatts = 500;
    public const double CarOnWatts = 1000;
    public static readonly TimeSpan CarCooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// True when the car has just started charging (from under 500 W to over 1000 W) while hot water is on Full, so the
    /// Full check can be redone with the car in it. A previous reading of null (first look) never triggers.
    /// </summary>
    public static Decision CarStarted(double? previousWatts, double? nowWatts, string? hotWaterMode, TimeSpan sinceLastRun, bool runInProgress)
    {
        if (runInProgress) return new Decision(false, "a run is in progress");
        if (previousWatts is not double prev || nowWatts is not double now) return new Decision(false, "no car reading");
        if (!(prev < CarOffWatts && now > CarOnWatts)) return new Decision(false, "the car did not just start charging");
        if (!string.Equals(hotWaterMode?.Trim(), "Full", StringComparison.OrdinalIgnoreCase)) return new Decision(false, "hot water is not on Full");
        if (sinceLastRun < CarCooldown) return new Decision(false, "a run just finished");
        return new Decision(true, $"the car started charging at {now / 1000:0.0} kW while hot water was on Full");
    }

    /// <summary>True when the buy price has just fallen to the cheap price (an edge, so it fires once per dip).</summary>
    public static Decision CheapReached(double? previousBuy, double? buyNow, double cheapPrice, TimeSpan sinceLastRun, bool runInProgress)
    {
        if (runInProgress) return new Decision(false, "a run is in progress");
        if (cheapPrice <= 0 || previousBuy is not double prev || buyNow is not double now) return new Decision(false, "no cheap price or reading");
        if (!(prev > cheapPrice && now <= cheapPrice)) return new Decision(false, "the buy price did not just reach the cheap price");
        if (sinceLastRun < CarCooldown) return new Decision(false, "a run just finished");
        return new Decision(true, $"the buy price fell to ${now:0.###}, at or under the cheap price ${cheapPrice:0.###}");
    }

    /// <summary>The forecast price for the slot that contains <paramref name="now"/>, or null when the forecast is stale or empty.</summary>
    public static double? ExpectedPrice(IReadOnlyList<ForecastPoint>? forecast, DateTimeOffset now)
    {
        ForecastPoint? current = null;
        foreach (var point in forecast ?? [])
            if (point.Time <= now && (current is null || point.Time > current.Time)) current = point;

        return current is not null && now - current.Time <= MaxPointAge ? current.Price : null;
    }

    /// <summary>The move needed to count: the percentage of the forecast price, but never less than the minimum in dollars.</summary>
    public static double RequiredShift(double expected, Settings s) =>
        Math.Max(Math.Abs(expected) * Math.Max(s.ShiftPercent, 0) / 100.0, s.MinShiftDollars);

    public static Decision Evaluate(
        double? liveFeedIn, double? expectedFeedIn,
        double? liveBuy, double? expectedBuy,
        TimeSpan sinceLastRun, bool runInProgress, Settings s,
        double? baselineFeedIn = null, double? baselineBuy = null)
    {
        if (runInProgress) return new Decision(false, "a run is in progress");

        string? reason = null;
        bool spike = false;

        if (liveFeedIn is double feed && expectedFeedIn is double expFeed && feed >= s.MinExportPrice)
        {
            double move = feed - expFeed;
            double need = RequiredShift(expFeed, s);
            // A gap the last run already saw (the price has not moved since) is not news.
            bool moved = baselineFeedIn is not double bf || feed - bf + 1e-9 >= s.MinShiftDollars;
            if (move + 1e-9 >= need && moved)
            {
                reason = $"feed-in ${feed:0.###} is ${move:0.###} above its forecast ${expFeed:0.###} (needs ${need:0.###})";
                spike = feed > SpikeDollars;
            }
        }

        if (reason is null && liveBuy is double buy && expectedBuy is double expBuy && buy <= s.MaxImportPrice)
        {
            double move = expBuy - buy;
            double need = RequiredShift(expBuy, s);
            bool moved = baselineBuy is not double bb || bb - buy + 1e-9 >= s.MinShiftDollars;
            if (move + 1e-9 >= need && moved)
                reason = $"buy price ${buy:0.###} is ${move:0.###} below its forecast ${expBuy:0.###} (needs ${need:0.###})";
        }

        if (reason is null) return new Decision(false, "no price beat its forecast by enough");

        var cooldown = spike ? SpikeCooldown : s.CooldownOrDefault;
        if (sinceLastRun < cooldown)
            return new Decision(false, $"{reason}, but the last run was {sinceLastRun.TotalMinutes:0.#} min ago (cooldown {cooldown.TotalMinutes:0.#} min)");

        return new Decision(true, reason);
    }
}
