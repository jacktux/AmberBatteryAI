using System.Globalization;

namespace AmberBatteryAI.Services;

/// <param name="Buy">True when the grid should charge the battery now.</param>
/// <param name="Price">The import_price to set (rounded up to the next cent so a price at or under the limit really starts).</param>
/// <param name="TargetSocPercent">The import_stop_soc to set.</param>
/// <param name="ShortfallKwh">Energy the battery is short of (floor and car) that the purchase is for.</param>
public record GridBuyPlan(bool Buy, double Price, double TargetSocPercent, double ShortfallKwh, string Reason)
{
    public static GridBuyPlan No(string reason) => new(false, 0, 0, 0, reason);
}

public record GridBuyInput(
    double? BuyNow,
    double SocPercent,
    double CapacityKwh,
    double FloorKwh,
    double NetSolarKwhUntil18,
    double ConservePercent,
    double CheapPrice,
    double StretchPrice,
    double MaxImportPrice,
    double MaxImportStopSoc,
    double? CarNeedKwh,
    EvPriority CarPriority,
    double SolarConfidenceFactor = 1.0);

/// <summary>
/// When to buy from the grid. Two prices the user sets (0 turns each off): the cheap price buys whenever the battery has
/// room, keeping room for the free solar still to come; the stretch price buys only while there is a shortfall, the
/// stored energy being below the floor, or a car that needs energy the spare solar and the battery above the floor
/// cannot supply. A car on "Charge when you can" never counts as a need.
/// </summary>
public static class GridBuyPlanner
{
    public const double MinRoomPoints = 1;
    public const double MarginPoints = 2;

    private static double Up(double price) => Math.Ceiling(Math.Round(price * 100, 6)) / 100;

    private static string P(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string K(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// The user's "force grid charge" switch: charge to the target SOC at any buy price up to the maximum import price,
    /// whatever the floor, solar or car say. <see cref="GridBuyPlan.Buy"/> is false with a reason when there is nothing to do.
    /// </summary>
    public static GridBuyPlan Force(double socPercent, double targetSocPercent, double maxImportStopSoc, double? buyNow, double maxImportPrice)
    {
        double target = Math.Round(Math.Clamp(Math.Min(targetSocPercent, Math.Clamp(maxImportStopSoc, 0, 100)), 0, 100), 0);
        if (socPercent >= target - MinRoomPoints)
            return GridBuyPlan.No($"force charge: the battery ({K(socPercent)}%) is already at the {K(target)}% target");
        if (buyNow is not double buy)
            return GridBuyPlan.No("force charge: no buy price");
        if (buy > maxImportPrice)
            return GridBuyPlan.No($"force charge: buy price ${P(buy)} is above the maximum import price ${P(maxImportPrice)}, so nothing can charge");
        return new GridBuyPlan(true, Up(maxImportPrice), target, 0, $"force charge on: charging to {K(target)}% at up to ${P(maxImportPrice)} (buy ${P(buy)})");
    }

    public static GridBuyPlan Plan(GridBuyInput i)
    {
        double maxPrice = i.MaxImportPrice;
        double cheap = Math.Min(Math.Max(i.CheapPrice, 0), maxPrice);
        double stretch = Math.Min(Math.Max(Math.Max(i.StretchPrice, 0), cheap), maxPrice);
        if (cheap <= 0 && stretch <= 0) return GridBuyPlan.No("grid buying is off (no cheap or stretch price set)");
        if (i.BuyNow is not double buy) return GridBuyPlan.No("no buy price");
        if (i.CapacityKwh <= 0) return GridBuyPlan.No("no battery size");

        double stored = i.SocPercent / 100 * i.CapacityKwh;
        double floorShort = Math.Max(0, i.FloorKwh - stored);

        double carShort = 0;
        if (i.CarNeedKwh is double need && need > 0 && i.CarPriority != EvPriority.WhenYouCan)
            carShort = Math.Max(0, need - Math.Max(0, i.NetSolarKwhUntil18) - Math.Max(0, stored - i.FloorKwh));

        double shortfall = floorShort + carShort;
        double maxStop = Math.Clamp(i.MaxImportStopSoc, 0, 100);
        double targetFor(double kwh) => Math.Min(maxStop, Math.Round(i.SocPercent + kwh / i.CapacityKwh * 100 + MarginPoints, 0));

        if (cheap > 0 && buy <= cheap)
        {
            // Leave room for the free solar still to come, so cheap grid energy does not displace it.
            double solarPercent = Math.Max(0, i.NetSolarKwhUntil18) * i.SolarConfidenceFactor / i.CapacityKwh * 100;
            double target = Math.Min(maxStop, Math.Max(100 - solarPercent, i.ConservePercent + 10));
            if (shortfall > 0) target = Math.Max(target, targetFor(shortfall));
            target = Math.Round(Math.Min(target, maxStop), 0);

            if (i.SocPercent >= target - MinRoomPoints)
                return GridBuyPlan.No($"buy price ${P(buy)} is at or under the cheap price ${P(cheap)}, but the battery ({K(i.SocPercent)}%) has no room under {K(target)}% once the solar still to come is counted");

            string why = shortfall > 0 ? $", and the battery is short by {K(shortfall)} kWh" : "";
            return new GridBuyPlan(true, Up(Math.Max(cheap, buy)), target, shortfall,
                $"cheap price: buy ${P(buy)} is at or under ${P(cheap)}{why}; charging to {K(target)}%");
        }

        if (stretch > cheap && buy <= stretch && shortfall > 0)
        {
            double target = Math.Max(targetFor(shortfall), Math.Round(i.SocPercent + 1, 0));
            if (i.SocPercent >= maxStop - MinRoomPoints)
                return GridBuyPlan.No("short of energy but the battery is already at its import limit");
            string cause = floorShort > 0 && carShort > 0 ? $"battery {K(floorShort)} kWh under the floor and the car {K(carShort)} kWh short"
                : floorShort > 0 ? $"battery {K(floorShort)} kWh under the floor (poor solar)"
                : $"car {K(carShort)} kWh short of what spare solar and the battery can supply";
            return new GridBuyPlan(true, Up(stretch), target, shortfall,
                $"stretch price: buy ${P(buy)} is at or under ${P(stretch)} with {cause}; charging to {K(target)}%");
        }

        if (shortfall > 0 && stretch > 0)
            return GridBuyPlan.No($"short by {K(shortfall)} kWh but buy price ${P(buy)} is above the stretch price ${P(stretch)}");
        return GridBuyPlan.No(cheap > 0 ? $"buy price ${P(buy)} is above the cheap price ${P(cheap)}" : "no shortfall");
    }
}
