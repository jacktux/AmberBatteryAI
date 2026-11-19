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


    public const double SellNowMarginDollars = 0.015;
    public const double SpikeDollars = 0.50;
    public const double HoldMarginDollars = 0.005;
    /// <summary>The later window must beat the live price by more than this to justify holding.</summary>
    public const double LiveHoldMarginDollars = 0.005;
    /// <summary>Above this live feed-in price the stop SOC drops to the hard floor.</summary>
    public const double ExtremeSpikeDollars = 1.00;
    public const double SmoothingHours = 0.5;
    public const double PlateauFraction = 0.90;

    /// <summary>
    /// Discount for a later slot in the look-ahead. None up to 2 hours, then 0-10% from 2 to 4 hours, then the
    /// longer-range <see cref="Haircut"/> (never smaller than the 10% already reached at 4 hours).
    /// </summary>
    public static double HorizonFactor(double hoursAhead) =>
        hoursAhead <= 2 ? 1.00
        : hoursAhead <= 4 ? 1.00 - 0.10 * (hoursAhead - 2) / 2
        : Math.Min(0.90, Haircut(hoursAhead));

    private static double SlotHours(IReadOnlyList<Slot> slots, int i)
    {
        double hours = i + 1 < slots.Count
            ? (slots[i + 1].Time - slots[i].Time).TotalHours
            : i > 0 ? (slots[i].Time - slots[i - 1].Time).TotalHours : MaxSlotHours;
        return Math.Clamp(hours, 0, MaxSlotHours);
    }

    /// <summary>
    /// Rolling 30-minute average of the prices that start at each slot, weighted by time. 5-minute slots are averaged,
    /// 30-minute slots stay as they are, so one 5-minute blip cannot stand out on its own.
    /// </summary>
    public static IReadOnlyList<Slot> Smooth(IReadOnlyList<Slot> slots)
    {
        var result = new List<Slot>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            var from = slots[i].Time;
            var to = from.AddHours(SmoothingHours);
            double weight = 0, sum = 0;
            for (int j = i; j < slots.Count; j++)
            {
                if (slots[j].Time >= to) break;
                var end = slots[j].Time.AddHours(SlotHours(slots, j));
                double overlap = ((end < to ? end : to) - slots[j].Time).TotalHours;
                if (overlap <= 0) continue;
                weight += overlap;
                sum += overlap * slots[j].Value;
            }

            result.Add(new Slot(from, weight > 0 ? sum / weight : slots[i].Value));
        }

        return result;
    }

    /// <param name="Hold">True when energy should be kept for a later window that pays better.</param>
    /// <param name="LaterPrice">Smoothed price of the best later window, before the horizon discount.</param>
    /// <param name="LaterDiscountedPrice">The same price after the horizon discount; the window is chosen on this.</param>
    /// <param name="NowPrice">30-minute average of the feed-in price from now.</param>
    /// <param name="WindowHours">Length of the best later window, where prices stay within 10% of its peak.</param>
    public record LookAhead(bool Hold, DateTimeOffset? LaterTime, double LaterPrice, double LaterDiscountedPrice, double NowPrice, double LaterCapacityKwh, double WindowHours, string Reason)
    {
        public string Text(Func<DateTimeOffset, string> fmt) => LaterTime is null
            ? "no later window to compare"
            : (Hold ? "HOLD" : "SELL NOW") +
              $" - now ${NowPrice:0.###}/kWh (30-min average) vs best later {fmt(LaterTime.Value)} ${LaterPrice:0.###}/kWh " +
              $"(${LaterDiscountedPrice:0.###} after the distance discount); {Reason}";
    }

    /// <summary>
    /// Compares the current feed-in price with the best later window. Both sides use 30-minute averages, so a short
    /// blip counts for little. Energy already stored is discharged once either way, so waiting carries no efficiency
    /// or degradation penalty. Returns SELL NOW when the live price is a spike or is within half a cent of (or above) the discounted later price, the current price beats the later
    /// window by <see cref="SellNowMarginDollars"/>, the window has started or is about to, or it cannot carry the surplus.
    /// </summary>
    public const string LiveMatchesLaterReason = "the live price is at least the later window price";

    public static LookAhead CompareWithLater(IReadOnlyList<Slot> slots, DateTimeOffset now, double exportPowerKw, double? livePrice = null, double surplusKwh = 0)
    {
        var series = new List<Slot>();
        if (livePrice is double live) series.Add(new Slot(now, live));
        series.AddRange(slots.Where(s => livePrice is null || s.Time > now));
        if (series.Count == 0)
            return new LookAhead(false, null, 0, 0, livePrice ?? 0, 0, 0, "no forecast");

        var smooth = Smooth(series);
        double nowPrice = smooth[0].Value;
        double rawNow = livePrice ?? series[0].Value;

        int best = -1;
        double bestDiscounted = double.MinValue;
        for (int i = 1; i < smooth.Count; i++)
        {
            double hours = (smooth[i].Time - now).TotalHours;
            if (hours < SmoothingHours - 1e-9) continue;
            double discounted = smooth[i].Value * HorizonFactor(hours);
            if (discounted > bestDiscounted) { bestDiscounted = discounted; best = i; }
        }

        if (best < 0)
            return new LookAhead(false, null, 0, 0, nowPrice, 0, 0, "no later window");

        double bestRaw = smooth[best].Value;
        double floor = bestRaw * PlateauFraction;
        int lo = best, hi = best;
        while (lo - 1 >= 1 && (smooth[lo - 1].Time - now).TotalHours >= SmoothingHours - 1e-9 && smooth[lo - 1].Value >= floor) lo--;
        while (hi + 1 < smooth.Count && smooth[hi + 1].Value >= floor) hi++;
        double windowHours = 0;
        for (int i = lo; i <= hi; i++) windowHours += SlotHours(smooth, i);
        windowHours = Math.Max(windowHours, MaxSlotHours);
        double capacity = windowHours * Math.Max(exportPowerKw, 0.1);

        string reason;
        if (rawNow >= SpikeDollars) reason = "live price is a spike";
        else if (rawNow > 0 && bestDiscounted <= rawNow + LiveHoldMarginDollars) reason = LiveMatchesLaterReason;
        else if (nowPrice >= bestRaw + SellNowMarginDollars) reason = "now beats the later window by more than the margin";
        else if (smooth[best].Time <= now.AddHours(SmoothingHours)) reason = "the best window has started or is about to";
        else if (surplusKwh > 0 && capacity < surplusKwh) reason = "the later window cannot carry the surplus";
        else reason = "the later window pays more";

        bool hold = reason == "the later window pays more";
        return new LookAhead(hold, smooth[best].Time, Math.Round(bestRaw, 3), Math.Round(bestDiscounted, 3), Math.Round(nowPrice, 3), capacity, windowHours, reason);
    }

    /// <summary>The feed-in price below which an export that is already running stops.</summary>
    public static double EffectiveSellFloor(double exportPrice, double exportPriceHysteresis) =>
        Math.Round(exportPrice - Math.Max(0, exportPriceHysteresis), 3);

    /// <summary>
    /// Applies the look-ahead to a threshold. On HOLD the price is the best later window's price, and at least the live
    /// price plus half a cent so a live blip cannot start the export, capped at the highest forecast feed-in price.
    /// On SELL NOW the threshold is unchanged.
    /// </summary>
    public static double ApplyLookAhead(double threshold, LookAhead look, double? livePrice, double forecastMaxPrice, double exportPriceHysteresis = 0)
    {
        if (!look.Hold)
        {
            // The 30-minute average can hide a live price that already matches the later window, so let it start now.
            if (look.Reason == LiveMatchesLaterReason && livePrice is double spot) return Math.Round(Math.Min(threshold, spot), 3);
            return threshold;
        }

        // The automation starts at feed-in >= price but only stops below price - hysteresis. Staying above live + hysteresis
        // therefore both prevents a start and ends an export already in progress.
        double live = livePrice ?? look.NowPrice;
        double floor = live + Math.Max(0, exportPriceHysteresis) + HoldMarginDollars;
        double price = Math.Max(Math.Max(threshold, look.LaterPrice), floor);
        return Math.Round(Math.Min(price, Math.Max(forecastMaxPrice, floor)), 3);
    }

    public const double MinExportWatts = 1000;
    public const double DefaultMaxExportWatts = 15000;
    /// <summary>Share of the stored surplus that reaches the grid (square root of the 0.90 round-trip efficiency).</summary>
    public const double DischargeEfficiency = 0.95;
    private const double RampSlotHours = 0.5;

    /// <param name="LimitWatts">Battery discharge limit for the coming run interval: planned export plus the house load.</param>
    /// <param name="CurrentKwh">Export energy allocated to the current half hour.</param>
    /// <param name="Text">Short explanation for the log.</param>
    public record ExportRamp(int LimitWatts, double CurrentKwh, string Text);

    /// <summary>
    /// Ranks the half-hour slots by forecast feed-in price (live price for the current slot, later slots after the horizon
    /// discount) and gives the deliverable surplus to the best slots first, up to the maximum discharge power. Slots below
    /// <paramref name="sellFloor"/> get nothing. The limit covers the whole run interval: the largest power wanted by any
    /// slot that starts before the next run, so a better slot that begins mid-interval is not starved. Ties go to the
    /// earlier slot, since a later price may not arrive. The limit includes the house load because it limits the battery
    /// discharge, of which the grid export is the part above the load.
    /// </summary>
    public static ExportRamp SuggestExportRamp(IReadOnlyList<Slot> slots, DateTimeOffset now, double? livePrice, double surplusKwh, double sellFloor,
        double maxWatts, double? homeLoadWatts, double coverHours = RampSlotHours, double? exportPrice = null)
    {
        double max = Math.Max(maxWatts, MinExportWatts);
        double load = homeLoadWatts is double h && double.IsFinite(h) ? Math.Max(0, h) : 0;
        int Clamp(double watts) => (int)Math.Clamp(Math.Ceiling(watts / 500) * 500, MinExportWatts, max);

        if (livePrice > ExtremeSpikeDollars) return new ExportRamp((int)max, 0, "live price is an extreme spike: full discharge power");

        var series = new List<Slot>();
        if (livePrice is double live) series.Add(new Slot(now, live));
        series.AddRange(slots.Where(s => livePrice is null || s.Time > now));
        if (series.Count == 0 || surplusKwh <= 0)
            return new ExportRamp((int)MinExportWatts, 0, "nothing to sell");

        var smooth = Smooth(series);
        long bucketTicks = TimeSpan.FromMinutes(30).Ticks;
        long Bucket(DateTimeOffset t) => t.ToLocalTime().DateTime.Ticks / bucketTicks;

        // One candidate per aligned half hour: the current one at the live price, later ones at their 30-minute average.
        var candidates = new List<(DateTimeOffset Time, double Price, double Ranked)>();
        candidates.Add((now, series[0].Value, series[0].Value));
        long currentBucket = Bucket(now);
        foreach (var s in smooth.Skip(1))
        {
            if (Bucket(s.Time) <= currentBucket || candidates.Any(c => Bucket(c.Time) == Bucket(s.Time))) continue;
            candidates.Add((s.Time, s.Value, s.Value * HorizonFactor((s.Time - now).TotalHours)));
        }

        double exportCap = Math.Max(0, max - load) / 1000 * RampSlotHours;
        double left = surplusKwh * DischargeEfficiency;
        var allocation = new Dictionary<int, double>();
        var order = candidates.Select((c, i) => (c, i)).Where(x => x.c.Price >= sellFloor)
            .OrderByDescending(x => x.c.Ranked).ThenBy(x => x.c.Time);
        foreach (var (c, i) in order)
        {
            if (left <= 0) break;
            double take = Math.Min(left, exportCap);
            allocation[i] = take;
            left -= take;
        }

        double worstWatts = 0, currentKwh = allocation.GetValueOrDefault(0);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i > 0 && (candidates[i].Time - now).TotalHours >= coverHours - 1e-9) break;
            if (allocation.TryGetValue(i, out double kwh)) worstWatts = Math.Max(worstWatts, kwh / RampSlotHours * 1000 + load);
        }

        // The automation starts selling the moment live feed-in reaches export_price and the limit is only rewritten
        // at the next run, so while the threshold is not yet met size the limit for the slot that price would trigger.
        bool arrival = false;
        if (exportPrice is double ep && livePrice is double lp && lp < ep && candidates.Count > 1)
        {
            double arrivalWatts = 0;
            for (int i = 1; i < candidates.Count; i++)
                if (candidates[i].Price >= ep && allocation.TryGetValue(i, out double kwh))
                    arrivalWatts = Math.Max(arrivalWatts, kwh / RampSlotHours * 1000 + load);
            if (arrivalWatts <= 0) arrivalWatts = Math.Min(surplusKwh * DischargeEfficiency, exportCap) / RampSlotHours * 1000 + load;
            if (arrivalWatts > worstWatts) { worstWatts = arrivalWatts; arrival = true; }
        }

        int limit = worstWatts > 0 ? Clamp(worstWatts) : (int)MinExportWatts;
        int used = allocation.Count;
        string text = currentKwh > 0
            ? $"current slot is one of the {used} best-priced slots and carries {currentKwh:0.##} kWh"
            : arrival ? "sized for the moment the live price reaches export_price"
            : worstWatts > 0 ? "export starts later in this run interval" : $"better slots come later ({used} slot(s) carry the surplus)";
        return new ExportRamp(limit, Math.Round(currentKwh, 2), text);
    }

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
