using System.Globalization;

namespace AmberBatteryAI.Services;

/// <summary>
/// Decides when the hot water tank may be heated to the FULL target (80 C). FULL is only allowed when solar can pay
/// for the extra heating without putting the 18:00 battery target (60%) at risk. Otherwise the tank stays on MINIMAL (60 C).
/// </summary>
public static class HotWaterPlanner
{
    public const double FullTempC = 80;
    public const double MinimalTempC = 60;
    public const double FullWindowStartHour = 11;
    public const double FullWindowEndHour = 16;
    public const double MinimalHeatingEndHour = 16.5;
    public const double MinSocPercent = 25;

    /// <param name="Ok">True when the mode may be FULL.</param>
    /// <param name="Detail">Why, in words the model and the decision history can show.</param>
    /// <param name="ReserveKwh">
    /// Battery energy to keep for the heating, so exports do not use it. When FULL is allowed it is the energy to reach 80 C
    /// (zero when the grid pays for it). Otherwise it is the energy still needed to reach 60 C, until heating stops at 16:30.
    /// </param>
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
            return new FullAssessment(false, "tank temperature unavailable", 0);

        double minimalReserveKwh = hourOfDay < MinimalHeatingEndHour
            ? Math.Max(0, (MinimalTempC - tankC.Value) * kwhPerDegC)
            : 0;

        if (tankC >= FullTempC)
            return new FullAssessment(false, $"tank is already at {N(tankC.Value)} C", minimalReserveKwh);

        if (importPriceNow is < 0)
            return new FullAssessment(true, $"import price is negative ({P(importPriceNow.Value)} $/kWh), so heating from the grid is paid", 0);

        if (hourOfDay < FullWindowStartHour || hourOfDay >= FullWindowEndHour)
            return new FullAssessment(false, $"outside the {FullWindowStartHour:00}:00-{FullWindowEndHour:00}:00 solar heating window", minimalReserveKwh);
        if (socPercent is null)
            return new FullAssessment(false, "battery SOC unavailable", minimalReserveKwh);
        if (socPercent < MinSocPercent)
            return new FullAssessment(false, $"battery SOC is below {MinSocPercent:0}%", minimalReserveKwh);

        double storedKwh = socPercent.Value / 100 * capacityKwh;
        double targetKwh = capacityKwh * 0.60;
        double kwhToFull = (FullTempC - tankC.Value) * kwhPerDegC;
        double projectedKwh = storedKwh + netSolarKwhUntil18 - kwhToFull;

        if (projectedKwh < targetKwh)
            return new FullAssessment(false, $"18:00 target at risk: heating {N(tankC.Value)} C to {FullTempC:0} C needs about {N(kwhToFull)} kWh, leaving a projected {N(projectedKwh)} kWh at 18:00 against a {N(targetKwh)} kWh target", minimalReserveKwh);

        return new FullAssessment(
            true,
            $"heating {N(tankC.Value)} C to {FullTempC:0} C needs about {N(kwhToFull)} kWh and the projected battery at 18:00 is still {N(projectedKwh)} kWh (target {N(targetKwh)} kWh)",
            kwhToFull);
    }

    private static string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string P(double price) => price.ToString("0.###", CultureInfo.InvariantCulture);
}
