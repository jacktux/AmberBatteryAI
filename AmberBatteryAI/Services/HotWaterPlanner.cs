using System.Globalization;

namespace AmberBatteryAI.Services;

/// <summary>
/// Decides when the hot water tank may be heated to the FULL target (80 C). FULL is only allowed when solar can pay
/// for the extra heating without putting the 18:00 battery target (60%) at risk. Otherwise the tank stays on MINIMAL (60 C).
/// </summary>
public static class HotWaterPlanner
{
    public const double FullTempC = 80;
    public const double FullWindowStartHour = 11;
    public const double FullWindowEndHour = 16;
    public const double MinSocPercent = 25;

    /// <param name="Ok">True when the mode may be FULL.</param>
    /// <param name="Detail">Why, in words the model and the decision history can show.</param>
    /// <param name="ReserveKwh">Battery energy to keep for the heating, so exports do not use it. Zero when the grid pays for it.</param>
    public record FullAssessment(bool Ok, string Detail, double ReserveKwh)
    {
        public string Text => (Ok ? "YES - " : "NO - ") + Detail;
    }

    public static FullAssessment AssessFull(
        double? tankC,
        double? socPercent,
        double capacityKwh,
        double netSolarKwhUntil18,
        double kwhPerDegC,
        double hourOfDay,
        double? importPriceNow)
    {
        if (tankC is null)
            return No("tank temperature unavailable");
        if (tankC >= FullTempC)
            return No($"tank is already at {N(tankC.Value)} C");

        if (importPriceNow is < 0)
            return new FullAssessment(true, $"import price is negative ({P(importPriceNow.Value)} $/kWh), so heating from the grid is paid", 0);

        if (hourOfDay < FullWindowStartHour || hourOfDay >= FullWindowEndHour)
            return No($"outside the {FullWindowStartHour:00}:00-{FullWindowEndHour:00}:00 solar heating window");
        if (socPercent is null)
            return No("battery SOC unavailable");
        if (socPercent < MinSocPercent)
            return No($"battery SOC is below {MinSocPercent:0}%");

        double storedKwh = socPercent.Value / 100 * capacityKwh;
        double targetKwh = capacityKwh * 0.60;
        double kwhToFull = (FullTempC - tankC.Value) * kwhPerDegC;
        double projectedKwh = storedKwh + netSolarKwhUntil18 - kwhToFull;

        if (projectedKwh < targetKwh)
            return No($"18:00 target at risk: heating {N(tankC.Value)} C to {FullTempC:0} C needs about {N(kwhToFull)} kWh, leaving a projected {N(projectedKwh)} kWh at 18:00 against a {N(targetKwh)} kWh target");

        return new FullAssessment(
            true,
            $"heating {N(tankC.Value)} C to {FullTempC:0} C needs about {N(kwhToFull)} kWh and the projected battery at 18:00 is still {N(projectedKwh)} kWh (target {N(targetKwh)} kWh)",
            kwhToFull);
    }

    private static FullAssessment No(string detail) => new(false, detail, 0);

    private static string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string P(double price) => price.ToString("0.###", CultureInfo.InvariantCulture);
}
