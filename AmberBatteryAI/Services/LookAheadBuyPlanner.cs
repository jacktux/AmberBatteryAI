using System.Globalization;

namespace AmberBatteryAI.Services;

public record LookAheadBuyInput(
    DateTime Now,
    double SocPercent,
    double CapacityKwh,
    double FloorKwh,
    double ConservePercent,
    double AvgLoadKw,
    double DaytimeLoadKw,
    double MorningLoadKw,
    double SolarConfidence,
    double MorningSolarFallbackHour,
    IReadOnlyList<SocPlanner.SolarSlot> SolarSlots,
    IReadOnlyList<PricePoint> BuyPrices,
    double? BuyNow,
    double CeilingPrice,
    double MaxImportPrice,
    double MaxImportStopSoc,
    double PowerKw,
    double Tolerance,
    double CarShortKwh);

/// <param name="Plan">What to do now.</param>
/// <param name="NextWindowStart">When the next chosen buying window starts after now (local time), so a run can be started then.</param>
/// <param name="NeedKwh">Energy the plan is trying to buy over today and tomorrow.</param>
public record LookAheadBuyResult(GridBuyPlan Plan, DateTime? NextWindowStart, double NeedKwh);

/// <summary>
/// Works out how much energy the battery must buy to get through tonight and through tomorrow's solar day into the
/// next night, then picks the cheapest 30-minute slots before it is needed (never above the ceiling price) and says
/// whether to buy now or wait for them.
/// </summary>
public static class LookAheadBuyPlanner
{
    public const double ChargeEfficiency = 0.9;
    private static readonly TimeSpan Slot = TimeSpan.FromMinutes(30);

    private static string P(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string K(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static double Up(double price) => Math.Ceiling(Math.Round(price * 100, 6)) / 100;

    private static DateTime Floor30(DateTime t) => new(t.Ticks - t.Ticks % Slot.Ticks, t.Kind);

    private record Bucket(DateTime Start, double Price);

    public static LookAheadBuyResult Plan(LookAheadBuyInput i)
    {
        LookAheadBuyResult No(string reason, double need = 0, DateTime? next = null) => new(GridBuyPlan.No(reason), next, need);

        if (i.CapacityKwh <= 0) return No("look-ahead buy: no battery size");
        double ceiling = Math.Min(Math.Max(i.CeilingPrice, 0), i.MaxImportPrice);
        if (ceiling <= 0) return No("look-ahead buy: no stretch price set, so there is no price to buy at");

        // The next solar takeover ends tonight's need; the solar day it starts is "day 2".
        DateTime sixAm = i.Now.Date.AddHours(6);
        if (sixAm <= i.Now) sixAm = sixAm.AddDays(1);
        DateTime takeover = SocPlanner.MorningSolarTakeover(sixAm, i.SolarSlots, i.AvgLoadKw, i.DaytimeLoadKw, i.SolarConfidence, i.MorningSolarFallbackHour, i.MorningLoadKw);
        DateTime day2 = takeover.Date;
        DateTime deadline2 = day2.AddHours(18);

        double stored = i.SocPercent / 100 * i.CapacityKwh;
        double shortfall1 = Math.Max(0, i.FloorKwh - stored);

        // Day 2: the energy at takeover is the morning target plus whatever the battery holds above the floor.
        double target = i.CapacityKwh * SocPlanner.ClampMorningTarget(i.ConservePercent) / 100;
        double atTakeover = target + Math.Max(0, stored - i.FloorKwh);
        var day2Slots = i.SolarSlots.Where(s => s.Start.Date == day2).ToList();
        double netSolar2 = day2Slots.Where(s => s.Start.Hour is >= 6 and < 18)
            .Sum(s => Math.Max(0, s.Kw * 0.9 * i.SolarConfidence - i.DaytimeLoadKw) * 0.5);
        double required18 = SocPlanner.HourlyFloorKwh(i.CapacityKwh, deadline2, i.SolarSlots, i.AvgLoadKw, i.DaytimeLoadKw, 0, i.SolarConfidence,
            i.ConservePercent, i.MorningSolarFallbackHour, i.MorningLoadKw)
            ?? SocPlanner.RequiredKwhAt18(i.CapacityKwh, i.AvgLoadKw, i.ConservePercent);
        double energy18 = Math.Min(i.CapacityKwh, atTakeover + netSolar2);
        double shortfall2 = day2Slots.Count == 0 ? 0 : Math.Max(0, required18 - energy18);

        double roomKwh = Math.Max(0, i.CapacityKwh * Math.Clamp(i.MaxImportStopSoc, 0, 100) / 100 - stored);
        double need1 = Math.Min(shortfall1 + Math.Max(0, i.CarShortKwh), roomKwh);
        double need2 = Math.Min(shortfall2, Math.Max(0, i.CapacityKwh - energy18));
        double need = need1 + need2;
        string needText = $"{K(shortfall1)} kWh for tonight" + (i.CarShortKwh > 0 ? $", {K(i.CarShortKwh)} kWh for the car" : "") +
            $", {K(shortfall2)} kWh for tomorrow night ({(day2Slots.Count == 0 ? "no solar forecast for tomorrow" : $"tomorrow's solar nets {K(netSolar2)} kWh")})";

        if (need <= 0.05)
            return No($"look-ahead buy: no shortfall ({needText})");

        // 30-minute price buckets from now to the later deadline.
        var buckets = i.BuyPrices
            .Select(p => (Start: Floor30(p.Time.LocalDateTime), p.Price))
            .Where(p => p.Start + Slot > i.Now && p.Start < deadline2)
            .GroupBy(p => p.Start)
            .Select(g => new Bucket(g.Key, g.Average(x => x.Price)))
            .OrderBy(b => b.Start)
            .ToList();
        if (buckets.Count == 0)
            return No($"look-ahead buy: need {K(need)} kWh ({needText}) but there is no buy price forecast yet", need);

        double perSlot = Math.Max(0.1, i.PowerKw) * 0.5 * ChargeEfficiency;
        var chosen = new Dictionary<DateTime, double>();

        double Allocate(double kwh, DateTime deadline)
        {
            foreach (var b in buckets.Where(b => b.Start < deadline && b.Price <= ceiling && !chosen.ContainsKey(b.Start)).OrderBy(b => b.Price).ThenBy(b => b.Start))
            {
                if (kwh <= 0.001) break;
                double take = Math.Min(kwh, perSlot);
                chosen[b.Start] = take;
                kwh -= take;
            }
            return kwh;
        }

        double left1 = Allocate(need1, takeover);
        double left2 = Allocate(need2, deadline2);
        double unmet = left1 + left2;
        string unmetText = unmet > 0.05
            ? $"; only the cheapest slots at or under ${P(ceiling)} cover {K(need - unmet)} of {K(need)} kWh, so raise the stretch price to buy the rest"
            : "";

        if (chosen.Count == 0)
        {
            // The slots that could serve the first need must come before the solar takeover; otherwise any slot before the later deadline.
            var relevant = need1 > 0 ? buckets.Where(b => b.Start < takeover).ToList() : buckets;
            string until = need1 > 0 ? $" before tomorrow's solar takeover ({takeover:HH:mm})" : "";
            string cheapestText = relevant.Count > 0 ? $"the cheapest slot{until} is ${P(relevant.Min(b => b.Price))}" : $"there is no buy price forecast{until}";
            return No($"look-ahead buy: need {K(need)} kWh ({needText}) but {cheapestText}, above the stretch price ${P(ceiling)}", need);
        }

        var ordered = chosen.OrderBy(c => c.Key).ToList();
        string windowText = string.Join(", ", ordered.Select(c => $"{c.Key:HH:mm}"));
        double minChosenPrice = ordered.Min(c => buckets.First(b => b.Start == c.Key).Price);
        DateTime? next = ordered.Select(c => c.Key).Where(s => s > i.Now).Cast<DateTime?>().FirstOrDefault();

        DateTime currentBucket = Floor30(i.Now);
        bool inWindow = chosen.ContainsKey(currentBucket);
        bool nearCheapest = i.BuyNow is double nowPrice && nowPrice <= ceiling && nowPrice <= minChosenPrice + Math.Max(0, i.Tolerance);

        if (!(inWindow || nearCheapest) || i.BuyNow is not double buy || buy > ceiling)
        {
            string when = next is DateTime n ? $"waiting for {n:HH:mm} (cheapest slots {windowText}, from ${P(minChosenPrice)})" : "waiting for a cheaper slot";
            return No($"look-ahead buy: need {K(need)} kWh ({needText}); {when}{unmetText}", need, next);
        }

        // Buying: cover the contiguous chosen run that starts now, or one slot's worth when buying just under the cheapest price.
        double buyKwh;
        double windowMaxPrice;
        if (inWindow)
        {
            buyKwh = 0; windowMaxPrice = 0;
            for (var t = currentBucket; chosen.ContainsKey(t); t += Slot)
            {
                buyKwh += chosen[t];
                windowMaxPrice = Math.Max(windowMaxPrice, buckets.First(b => b.Start == t).Price);
            }
        }
        else
        {
            buyKwh = Math.Min(need, perSlot);
            windowMaxPrice = minChosenPrice;
        }

        double maxStop = Math.Clamp(i.MaxImportStopSoc, 0, 100);
        if (i.SocPercent >= maxStop - GridBuyPlanner.MinRoomPoints)
            return No($"look-ahead buy: need {K(need)} kWh but the battery is already at its import limit", need, next);

        double targetSoc = Math.Min(maxStop, Math.Round(Math.Max(i.SocPercent + buyKwh / i.CapacityKwh * 100 + GridBuyPlanner.MarginPoints, i.SocPercent + 4), 0));
        double price = Math.Min(i.MaxImportPrice, Up(Math.Max(windowMaxPrice, buy)));
        return new LookAheadBuyResult(
            new GridBuyPlan(true, price, targetSoc, need, $"look-ahead buy: need {K(need)} kWh ({needText}); buying now at ${P(buy)} (chosen slots {windowText}) to {K(targetSoc)}%{unmetText}"),
            next, need);
    }
}
