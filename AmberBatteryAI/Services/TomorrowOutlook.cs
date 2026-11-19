using System.Globalization;

namespace AmberBatteryAI.Services;

/// <summary>
/// Plain-text outlook on tomorrow for the decision prompt: Solcast energy, Amber prices and whether tomorrow's solar can
/// refill the battery from the conserve target, so the model can judge if selling tonight costs anything tomorrow.
/// </summary>
public static class TomorrowOutlook
{
    private const double SolarHaircut = 0.9;

    public record Result(double SolarKwh, double NetSolarKwh, double PeakKw, double RoomKwh, bool Refills, string Text);

    public static Result Build(
        DateTime today,
        IReadOnlyList<SocPlanner.SolarSlot> solar,
        IReadOnlyList<PricePoint> feedIn,
        IReadOnlyList<PricePoint> import,
        double daytimeLoadKw,
        double capacityKwh,
        double conservePercent,
        double solarConfidence)
    {
        var tomorrow = today.Date.AddDays(1);
        var slots = solar.Where(s => s.Start.Date == tomorrow).ToList();
        var inv = CultureInfo.InvariantCulture;

        double solarKwh = slots.Sum(s => s.Kw * 0.5);
        double peakKw = slots.Select(s => s.Kw).DefaultIfEmpty(0).Max();
        if (slots.Count == 0)
            return new Result(0, 0, 0, 0, false, "Tomorrow outlook: no Solcast forecast for tomorrow is available; assume nothing about tomorrow's solar refill.");

        // Net of the house load during daylight (06:00-18:00 average load), with the usual haircut and confidence.
        double netSolar = slots.Where(s => s.Start.Hour is >= 6 and < 18).Sum(s => Math.Max(0, s.Kw * SolarHaircut * solarConfidence - daytimeLoadKw) * 0.5);
        double room = Math.Max(0, capacityKwh * (1 - conservePercent / 100.0));
        bool refills = netSolar >= room;

        var parts = new List<string>
        {
            $"Tomorrow outlook: Solcast {solarKwh.ToString("0.#", inv)} kWh (peak {peakKw.ToString("0.#", inv)} kW), about {netSolar.ToString("0.#", inv)} kWh left after the house (with haircut and confidence).",
            $"Battery room from the {conservePercent.ToString("0.#", inv)}% conserve target is {room.ToString("0.#", inv)} kWh, so tomorrow's solar {(refills ? "can" : "cannot")} fully refill it."
        };

        string Prices(string name, IReadOnlyList<PricePoint> points)
        {
            var day = points.Where(p => p.Time.Date == tomorrow).ToList();
            if (day.Count == 0) return $"{name}: no Amber forecast for tomorrow yet";
            var best = name == "feed-in" ? day.MaxBy(p => p.Price)! : day.MinBy(p => p.Price)!;
            return $"{name} mean {day.Average(p => p.Price).ToString("0.000", inv)} $/kWh, {(name == "feed-in" ? "best" : "cheapest")} {best.Price.ToString("0.000", inv)} at {best.Time:HH:mm}";
        }

        parts.Add($"Amber tomorrow: {Prices("feed-in", feedIn)}; {Prices("import", import)}.");
        parts.Add(refills
            ? "Selling surplus tonight has little downside for tomorrow because solar will refill the battery."
            : "Tomorrow's solar will not refill the battery on its own, so energy sold tonight may have to be bought back; weigh that against tonight's price.");
        return new Result(Math.Round(solarKwh, 1), Math.Round(netSolar, 1), peakKw, Math.Round(room, 1), refills, string.Join(" ", parts));
    }
}
