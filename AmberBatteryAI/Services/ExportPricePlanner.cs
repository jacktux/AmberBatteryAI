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
