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

    public static string N(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
