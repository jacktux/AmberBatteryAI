using System.Globalization;

namespace AmberBatteryAI.Services;

/// <summary>
/// SOC levels that follow from the export and import automations, which use a gap (hysteresis) between the SOC
/// where they start and the SOC where they stop, to avoid switching on and off around one value.
/// Export starts only above stop SOC + hysteresis. Import starts only below stop SOC - hysteresis.
/// </summary>
public static class SocPlanner
{
    public const double DefaultHysteresis = 2;

    /// <summary>Export begins only when SOC rises above this level.</summary>
    public static double ExportStartSoc(double exportStopSoc, double hysteresis) =>
        Math.Min(100, exportStopSoc + Math.Max(0, hysteresis));

    /// <summary>Import begins only when SOC falls below this level.</summary>
    public static double ImportStartSoc(double importStopSoc, double hysteresis) =>
        Math.Max(0, importStopSoc - Math.Max(0, hysteresis));

    /// <summary>
    /// The lowest import stop SOC at which the import automation can start from the current SOC.
    /// A target at or below SOC + hysteresis means charging never begins. Capped at 100.
    /// </summary>
    public static double ImportMinStopSoc(double currentSocPercent, double hysteresis) =>
        Math.Min(100, Math.Ceiling(currentSocPercent + Math.Max(0, hysteresis) + 1));

    /// <summary>Energy that exporting will actually sell: everything above the export stop level, not just above the floor.</summary>
    public static double SellableKwh(double storedKwh, double floorKwh, double capacityKwh)
    {
        double stopKwh = Math.Min(capacityKwh, floorKwh + capacityKwh * 0.02);
        return Math.Max(0, storedKwh - stopKwh);
    }

    public const double MorningTargetPercent = 30;
    public const double HardFloorPercent = 20;
    public const double OvernightHours = 12;

    /// <summary>Keeps the user's 06:00 target between the 20% hard floor and 100%.</summary>
    public static double ClampMorningTarget(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, HardFloorPercent, 100) : MorningTargetPercent;

    /// <summary>Energy the battery must hold at 18:00 to carry the house to 06:00 with the morning target still left.</summary>
    public static double RequiredKwhAt18(double capacityKwh, double avgLoadKw, double morningTargetPercent = MorningTargetPercent) =>
        Math.Min(capacityKwh, capacityKwh * morningTargetPercent / 100 + Math.Max(avgLoadKw, 0) * OvernightHours);

    /// <summary>
    /// Lowest stored energy allowed now so the battery is at the morning target (30%) at 06:00 next day.
    /// Before 18:00 the net solar still to come (after heating) can make up the shortfall at 18:00. Never below the 20% hard floor.
    /// </summary>
    public static double FloorKwh(double capacityKwh, double avgLoadKw, double netSolarKwhUntil18, double heatingReserveKwh, double hourOfDay, double morningTargetPercent = MorningTargetPercent)
    {
        double minKwh = capacityKwh * HardFloorPercent / 100;
        double floor;
        if (hourOfDay >= 6 && hourOfDay < 18)
            floor = RequiredKwhAt18(capacityKwh, avgLoadKw, morningTargetPercent) - Math.Max(netSolarKwhUntil18 - heatingReserveKwh, 0);
        else
        {
            double hoursTo0600 = hourOfDay >= 18 ? (24 - hourOfDay) + 6 : 6 - hourOfDay;
            floor = capacityKwh * morningTargetPercent / 100 + Math.Max(avgLoadKw, 0) * hoursTo0600;
        }

        return Math.Round(Math.Min(Math.Max(floor, minKwh), capacityKwh), 2);
    }

    public record SolarSlot(DateTime Start, double Kw);

    /// <summary>
    /// Lowest stored energy allowed now so the battery still reaches the morning target at 06:00, from a backward energy
    /// balance over 15-minute steps: each step's required energy is the next step's requirement minus that step's net
    /// energy (haircut solar minus house load), kept between the 20% hard floor and capacity. Unlike the lump-sum
    /// FloorKwh this respects when the solar arrives. Returns null when there is no solar forecast to use.
    /// </summary>
    public static double? HourlyFloorKwh(double capacityKwh, DateTime now, IReadOnlyList<SolarSlot> slots, double avgLoadKw, double daytimeLoadKw, double heatingReserveKwh, double solarConfidence, double morningTargetPercent = MorningTargetPercent)
    {
        if (slots.Count == 0)
            return null;

        double minKwh = capacityKwh * HardFloorPercent / 100;
        DateTime end = now.Date.AddHours(6);
        if (end <= now)
            end = end.AddDays(1);

        double load(DateTime t) => t.Hour >= 6 && t.Hour < 18 ? Math.Max(avgLoadKw, daytimeLoadKw) : Math.Max(avgLoadKw, 0);
        double solar(DateTime t) => slots.Where(x => x.Start <= t && t < x.Start.AddMinutes(30)).Select(x => x.Kw).DefaultIfEmpty(0).First();

        const double stepHours = 0.25;
        int steps = (int)Math.Ceiling((end - now).TotalHours / stepHours);
        double required = capacityKwh * ClampMorningTarget(morningTargetPercent) / 100;
        for (int i = steps - 1; i >= 0; i--)
        {
            DateTime start = now.AddHours(i * stepHours);
            double hours = Math.Min(stepHours, (end - start).TotalHours);
            DateTime mid = start.AddHours(hours / 2);
            double net = solar(mid) * 0.9 * solarConfidence - load(mid);
            required = Math.Clamp(required - net * hours, minKwh, capacityKwh);
        }

        return Math.Round(Math.Min(required + Math.Max(heatingReserveKwh, 0), capacityKwh), 2);
    }

    public static string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
