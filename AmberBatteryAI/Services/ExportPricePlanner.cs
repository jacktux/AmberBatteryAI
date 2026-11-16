namespace AmberBatteryAI.Services;

/// <summary>
/// Chooses the feed-in price at which the battery should start selling its surplus.
/// The surplus is sold in the best-priced forecast slots that together can carry it at the export power limit,
/// so a large surplus gets a lower threshold than a small one and is not left unsold.
/// </summary>
public static class ExportPricePlanner
{
    public const double MarginDollars = 0.01;
    public const double MaxSlotHours = 0.5;

    public record Slot(DateTimeOffset Time, double Value);

    /// <summary>
    /// Forecast prices get less reliable the further ahead they are. No discount up to 4 hours, then a smooth ramp
    /// to 20% at 10 hours and beyond, so two neighbouring slots never differ by a sudden step.
    /// </summary>
    public static double Haircut(double hoursAhead) =>
        hoursAhead <= 4 ? 1.00
        : hoursAhead >= 10 ? 0.80
        : 1.00 - 0.20 * (hoursAhead - 4) / 6;

    public const double RoundTripEfficiency = 0.90;
    public const double DegradationDollarsPerKwh = 0.02;

    /// <param name="Hold">True when energy should be kept for a later window that pays better after losses.</param>
    public record LookAhead(bool Hold, DateTimeOffset? LaterTime, double LaterPrice, double LaterNetPrice, double NowPrice, double LaterCapacityKwh)
    {
        public string Text(Func<DateTimeOffset, string> fmt) => LaterTime is null
            ? "no later window to compare"
            : (Hold ? "HOLD" : "SELL NOW") +
              $" - now ${NowPrice:0.###}/kWh vs best later {fmt(LaterTime.Value)} ${LaterPrice:0.###}/kWh " +
              $"(${LaterNetPrice:0.###} net of {RoundTripEfficiency:0%} efficiency and ${DegradationDollarsPerKwh:0.00} degradation)";
    }

    /// <summary>Compares the current feed-in price with the best later (haircut) price, net of efficiency and degradation.</summary>
    public static LookAhead CompareWithLater(IReadOnlyList<Slot> slots, DateTimeOffset now, double exportPowerKw)
    {
        if (slots.Count < 2) return new LookAhead(false, null, 0, 0, slots.Count == 1 ? slots[0].Value : 0, 0);

        double nowPrice = slots[0].Value;
        Slot? best = null;
        double bestNet = double.MinValue;
        double capacity = 0;
        for (int i = 1; i < slots.Count; i++)
        {
            double price = slots[i].Value * Haircut((slots[i].Time - now).TotalHours);
            double net = price * RoundTripEfficiency - DegradationDollarsPerKwh;
            if (net > nowPrice) capacity += MaxSlotHours * Math.Max(exportPowerKw, 0.1);
            if (net > bestNet) { bestNet = net; best = new Slot(slots[i].Time, price); }
        }

        return new LookAhead(bestNet > nowPrice, best!.Time, best.Value, Math.Round(bestNet, 3), nowPrice, capacity);
    }

    /// <summary>
    /// Applies the look-ahead to a threshold: when a later window pays better net of losses and can carry the surplus,
    /// the threshold is lifted above the current price so nothing is sold now.
    /// </summary>
    public static double ApplyLookAhead(double threshold, LookAhead look, double surplusKwh) =>
        look.Hold && look.LaterCapacityKwh >= surplusKwh
            ? Math.Max(threshold, Math.Round(look.NowPrice + MarginDollars, 3))
            : threshold;

    /// <returns>The suggested export price, or null when there are no usable slots.</returns>
    public static double? SuggestThreshold(IReadOnlyList<Slot> slots, DateTimeOffset now, double surplusKwh, double exportPowerKw)
    {
        var priced = new List<(double Price, double CapacityKwh)>();
        for (int i = 0; i < slots.Count; i++)
        {
            double hours = i + 1 < slots.Count
                ? Math.Min((slots[i + 1].Time - slots[i].Time).TotalHours, MaxSlotHours)
                : MaxSlotHours;
            if (hours <= 0) continue;

            double price = slots[i].Value * Haircut((slots[i].Time - now).TotalHours);
            priced.Add((price, hours * Math.Max(exportPowerKw, 0.1)));
        }

        if (priced.Count == 0) return null;

        var best = priced.OrderByDescending(p => p.Price).ToList();
        double marginal = best[0].Price;

        if (surplusKwh > 0)
        {
            double carried = 0;
            foreach (var slot in best)
            {
                marginal = slot.Price;
                carried += slot.CapacityKwh;
                if (carried >= surplusKwh) break;
            }
        }

        return Math.Round(marginal - MarginDollars, 3);
    }
}
