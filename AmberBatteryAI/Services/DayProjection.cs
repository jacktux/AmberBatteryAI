using System.Text.Json.Serialization;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

/// <summary>Cash and energy for a period of the day. Revenue and cost are in AUD.</summary>
public record ProjectionTotals
{
    [JsonPropertyName("export_kwh")] public double ExportKwh { get; init; }
    [JsonPropertyName("export_revenue")] public double ExportRevenue { get; init; }
    [JsonPropertyName("import_kwh")] public double ImportKwh { get; init; }
    [JsonPropertyName("import_cost")] public double ImportCost { get; init; }
    [JsonPropertyName("avoided_import_kwh")] public double AvoidedImportKwh { get; init; }
    [JsonPropertyName("avoided_import_value")] public double AvoidedImportValue { get; init; }
    /// <summary>Export revenue minus import cost: the cash on the Amber bill.</summary>
    [JsonPropertyName("net_cash")] public double NetCash { get; init; }
    /// <summary>Net cash plus the avoided import value.</summary>
    [JsonPropertyName("net_incl_avoided")] public double NetInclAvoided { get; init; }

    public static ProjectionTotals From(double exportKwh, double exportRevenue, double importKwh, double importCost, double avoidedKwh, double avoidedValue) => new()
    {
        ExportKwh = R(exportKwh),
        ExportRevenue = R(exportRevenue),
        ImportKwh = R(importKwh),
        ImportCost = R(importCost),
        AvoidedImportKwh = R(avoidedKwh),
        AvoidedImportValue = R(avoidedValue),
        NetCash = R(exportRevenue - importCost),
        NetInclAvoided = R(exportRevenue - importCost + avoidedValue)
    };

    public static ProjectionTotals Sum(ProjectionTotals a, ProjectionTotals b) =>
        From(a.ExportKwh + b.ExportKwh, a.ExportRevenue + b.ExportRevenue, a.ImportKwh + b.ImportKwh,
            a.ImportCost + b.ImportCost, a.AvoidedImportKwh + b.AvoidedImportKwh, a.AvoidedImportValue + b.AvoidedImportValue);

    private static double R(double v) => Math.Round(v, 4);
}

public record RealisedTotals
{
    [JsonPropertyName("totals")] public ProjectionTotals Totals { get; init; } = new();
    [JsonPropertyName("covered_hours")] public double CoveredHours { get; init; }
    [JsonPropertyName("samples")] public int Samples { get; init; }
}

public record ProjectionRange
{
    [JsonPropertyName("low_net_cash")] public double LowNetCash { get; init; }
    [JsonPropertyName("high_net_cash")] public double HighNetCash { get; init; }
    [JsonPropertyName("low_net_incl_avoided")] public double LowNetInclAvoided { get; init; }
    [JsonPropertyName("high_net_incl_avoided")] public double HighNetInclAvoided { get; init; }
}

public record ProjectionAssumptions
{
    [JsonPropertyName("load_days_used")] public int LoadDaysUsed { get; init; }
    [JsonPropertyName("load_source")] public string LoadSource { get; init; } = "";
    [JsonPropertyName("load_kwh_remaining")] public double LoadKwhRemaining { get; init; }
    [JsonPropertyName("solar_kwh_remaining")] public double SolarKwhRemaining { get; init; }
    [JsonPropertyName("price_forecast_until")] public DateTimeOffset? PriceForecastUntil { get; init; }
    [JsonPropertyName("export_threshold")] public double? ExportThreshold { get; init; }
    [JsonPropertyName("export_power_kw")] public double ExportPowerKw { get; init; }
    [JsonPropertyName("grid_charging")] public bool GridCharging { get; init; }
    [JsonPropertyName("round_trip_efficiency")] public double RoundTripEfficiency { get; init; }
    [JsonPropertyName("bias_window_days")] public int? BiasWindowDays { get; init; }
    [JsonPropertyName("notes")] public List<string> Notes { get; init; } = new();
}

/// <summary>An estimate of today's cash: what has been realised so far plus a simulation of the rest of the day.</summary>
public record DayProjection
{
    [JsonPropertyName("generated_at")] public DateTimeOffset GeneratedAt { get; init; }
    [JsonPropertyName("date")] public string Date { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "Estimate only: not a guarantee and not applied to any decision.";
    [JsonPropertyName("start_soc_percent")] public double StartSocPercent { get; init; }
    [JsonPropertyName("end_soc_percent")] public double EndSocPercent { get; init; }
    [JsonPropertyName("realised")] public RealisedTotals Realised { get; init; } = new();
    [JsonPropertyName("remainder")] public ProjectionTotals Remainder { get; init; } = new();
    [JsonPropertyName("day_total")] public ProjectionTotals DayTotal { get; init; } = new();
    [JsonPropertyName("hourly")] public List<HourlyProjection> Hourly { get; init; } = new();
    /// <summary>Rest of today with solar, load, SOC and prices for the chart; hours already past are not included.</summary>
    [JsonPropertyName("today_by_hour")] public List<TomorrowHour> TodayByHour { get; init; } = new();
    [JsonPropertyName("range")] public ProjectionRange Range { get; init; } = new();
    [JsonPropertyName("tomorrow")] public TomorrowProjection? Tomorrow { get; init; }
    [JsonPropertyName("assumptions")] public ProjectionAssumptions Assumptions { get; init; } = new();
}

/// <summary>The house load by half-hour of the day (48 values, kW), averaged over recent days.</summary>
public record LoadProfile(double[] Kw, int DaysUsed, bool Fallback)
{
    public double KwAt(DateTime time) => Kw[time.Hour * 2 + time.Minute / 30];
}

public static class LoadProfileBuilder
{
    public const int DefaultDays = 7;
    public const int MinSlotsPerDay = 24;

    /// <summary>
    /// Average house load per half-hour over the <paramref name="days"/> complete days before <paramref name="today"/>.
    /// A day counts only if samples cover at least <paramref name="minSlotsPerDay"/> of its 48 half-hours. A slot with no
    /// data on any day, and the whole profile when no day qualifies, uses <paramref name="fallbackKw"/>.
    /// </summary>
    public static LoadProfile Build(IEnumerable<TelemetrySample> samples, DateOnly today, double fallbackKw, int days = DefaultDays, int minSlotsPerDay = MinSlotsPerDay, bool subtractEv = true)
    {
        var first = today.AddDays(-Math.Max(1, days));
        var perDay = new Dictionary<DateOnly, (double Sum, int Count)[]>();

        foreach (var s in samples)
        {
            if (s.HomePowerW is not double homeWatts || !double.IsFinite(homeWatts) || homeWatts < 0) continue;
            // Car charging is not house load: a session would otherwise inflate the average for that time of day.
            double watts = subtractEv && s.EvPowerW is double ev && double.IsFinite(ev) && ev > 0 ? Math.Max(0, homeWatts - ev) : homeWatts;
            var day = DateOnly.FromDateTime(s.Timestamp.DateTime);
            if (day < first || day >= today) continue;

            if (!perDay.TryGetValue(day, out var slots)) perDay[day] = slots = new (double, int)[48];
            int slot = s.Timestamp.Hour * 2 + s.Timestamp.Minute / 30;
            slots[slot] = (slots[slot].Sum + watts, slots[slot].Count + 1);
        }

        var qualifying = perDay.Values.Where(slots => slots.Count(x => x.Count > 0) >= minSlotsPerDay).ToList();
        var profile = new double[48];
        for (int slot = 0; slot < 48; slot++)
        {
            var dayMeans = qualifying.Where(d => d[slot].Count > 0).Select(d => d[slot].Sum / d[slot].Count / 1000).ToList();
            profile[slot] = dayMeans.Count > 0 ? dayMeans.Average() : Math.Max(fallbackKw, 0);
        }

        return new LoadProfile(profile, qualifying.Count, qualifying.Count == 0);
    }
}

public record PricePoint(DateTimeOffset Time, double Price);

public record ProjectionInput
{
    public DateTimeOffset Now { get; init; }
    public double CapacityKwh { get; init; }
    public double StartSocPercent { get; init; }
    /// <summary>Lowest energy to hold at a given time of day (the same floor the optimizer uses), in kWh.</summary>
    public Func<DateTime, double> FloorKwhAt { get; init; } = _ => 0;
    public LoadProfile Load { get; init; } = new(new double[48], 0, true);
    public IReadOnlyList<SocPlanner.SolarSlot> Solar { get; init; } = Array.Empty<SocPlanner.SolarSlot>();
    public IReadOnlyList<PricePoint> ImportPrices { get; init; } = Array.Empty<PricePoint>();
    public IReadOnlyList<PricePoint> FeedInPrices { get; init; } = Array.Empty<PricePoint>();
    /// <summary>Battery energy above the floor is sold when the feed-in price reaches this. Null means no planned export.</summary>
    public double? ExportThreshold { get; init; }
    public double ExportPowerKw { get; init; } = 5;
    /// <summary>True when the applied mode is CHARGE: the battery charges from the grid when the import price is at or below the threshold.</summary>
    public bool GridCharging { get; init; }
    public double ImportThreshold { get; init; }
    public double ImportStopSocPercent { get; init; }
    public double ImportPowerKw { get; init; } = 5;
    public double RoundTripEfficiency { get; init; } = 0.90;
    /// <summary>Solar multiplier for the low case when Solcast gives no 10th percentile estimate.</summary>
    public double SolarConfidence { get; init; } = 0.6;
    public ForecastBiasReport? Bias { get; init; }
    /// <summary>Where the simulation stops; the next midnight when null.</summary>
    public DateTimeOffset? End { get; init; }
}

public enum ProjectionScenario { Base, Low, High }

public record SimulationResult(ProjectionTotals Totals, double EndSocPercent, double LoadKwh, double SolarKwh, double[] HourImportKwh, double[] HourExportKwh)
{
    public double PeakSocPercent { get; init; }
    public DateTimeOffset? FullAt { get; init; }
    /// <summary>Solar left over after the house and a full battery (exported or curtailed).</summary>
    public double SpilledSolarKwh { get; init; }
    public double[] HourSolarKwh { get; init; } = new double[24];
    public double[] HourLoadKwh { get; init; } = new double[24];
    public double[] HourSocPercent { get; init; } = new double[24];
    public double[] HourImportPrice { get; init; } = new double[24];
    public double[] HourFeedInPrice { get; init; } = new double[24];
}

/// <summary>One clock hour of tomorrow: solar, house load, grid flow, battery SOC at the end of the hour and the forecast prices.</summary>
public record TomorrowHour
{
    [JsonPropertyName("hour")] public int Hour { get; init; }
    [JsonPropertyName("solar_kwh")] public double SolarKwh { get; init; }
    [JsonPropertyName("load_kwh")] public double LoadKwh { get; init; }
    [JsonPropertyName("import_kwh")] public double ImportKwh { get; init; }
    [JsonPropertyName("import_low_kwh")] public double ImportLowKwh { get; init; }
    [JsonPropertyName("import_high_kwh")] public double ImportHighKwh { get; init; }
    [JsonPropertyName("export_kwh")] public double ExportKwh { get; init; }
    [JsonPropertyName("export_low_kwh")] public double ExportLowKwh { get; init; }
    [JsonPropertyName("export_high_kwh")] public double ExportHighKwh { get; init; }
    [JsonPropertyName("soc_percent")] public double SocPercent { get; init; }
    [JsonPropertyName("import_price")] public double ImportPrice { get; init; }
    [JsonPropertyName("feed_in_price")] public double FeedInPrice { get; init; }
}

/// <summary>How tomorrow's solar charge looks when starting from the SOC left at midnight.</summary>
public record TomorrowCase
{
    [JsonPropertyName("start_soc_percent")] public double StartSocPercent { get; init; }
    [JsonPropertyName("peak_soc_percent")] public double PeakSocPercent { get; init; }
    [JsonPropertyName("full_at")] public DateTimeOffset? FullAt { get; init; }
    [JsonPropertyName("spilled_solar_kwh")] public double SpilledSolarKwh { get; init; }
    [JsonPropertyName("import_kwh")] public double ImportKwh { get; init; }
}

public record TomorrowProjection
{
    [JsonPropertyName("date")] public string Date { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "Estimate only: no planned export is assumed for tomorrow.";
    [JsonPropertyName("solar_kwh")] public double SolarKwh { get; init; }
    [JsonPropertyName("load_kwh")] public double LoadKwh { get; init; }
    [JsonPropertyName("price_forecast_covers_tomorrow")] public bool PriceForecastCoversTomorrow { get; init; }
    [JsonPropertyName("with_todays_plan")] public TomorrowCase WithTodaysPlan { get; init; } = new();
    [JsonPropertyName("if_nothing_sold_today")] public TomorrowCase IfNothingSoldToday { get; init; } = new();
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("hourly")] public List<TomorrowHour> Hourly { get; init; } = new();
}

/// <summary>Projected grid flow for one clock hour: expected (base case) with the low/high of the solar and price scenarios.</summary>
public record HourlyProjection
{
    [JsonPropertyName("hour")] public int Hour { get; init; }
    [JsonPropertyName("import_kwh")] public double ImportKwh { get; init; }
    [JsonPropertyName("import_low_kwh")] public double ImportLowKwh { get; init; }
    [JsonPropertyName("import_high_kwh")] public double ImportHighKwh { get; init; }
    [JsonPropertyName("export_kwh")] public double ExportKwh { get; init; }
    [JsonPropertyName("export_low_kwh")] public double ExportLowKwh { get; init; }
    [JsonPropertyName("export_high_kwh")] public double ExportHighKwh { get; init; }
}

/// <summary>
/// Display-only estimate of the rest of today. It steps through the remaining half-hours, simulating the battery:
/// solar serves the house first, then charges the battery, then is exported; any shortfall comes from the battery,
/// then the grid. Battery energy above the floor is sold when the feed-in price reaches the applied export price.
/// It changes no setting and nothing in it feeds back into a decision.
/// </summary>
public static class DayProjector
{
    public const double ExportStopMarginPercent = 2;

    /// <summary>The average price of the points inside [from, to); otherwise the latest point before it, then the next one.</summary>
    public static double? PriceFor(IReadOnlyList<PricePoint> points, DateTimeOffset from, DateTimeOffset to)
    {
        if (points.Count == 0) return null;
        var inside = points.Where(p => p.Time >= from && p.Time < to).Select(p => p.Price).ToList();
        if (inside.Count > 0) return inside.Average();

        var before = points.Where(p => p.Time <= from).OrderByDescending(p => p.Time).FirstOrDefault();
        return before?.Price ?? points.OrderBy(p => p.Time).First().Price;
    }

    public static List<(DateTimeOffset Start, double Hours)> Steps(DateTimeOffset now, DateTimeOffset? until = null)
    {
        var end = until ?? new DateTimeOffset(now.Date.AddDays(1), now.Offset);
        var steps = new List<(DateTimeOffset, double)>();
        for (var t = now; t < end;)
        {
            var next = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset).AddMinutes(t.Minute < 30 ? 30 : 60);
            if (next > end) next = end;
            double hours = (next - t).TotalHours;
            if (hours > 0.001) steps.Add((t, hours));
            t = next;
        }

        return steps;
    }

    private static double SolarKw(IReadOnlyList<SocPlanner.SolarSlot> slots, DateTime at, ProjectionScenario scenario, double confidence)
    {
        var slot = slots.FirstOrDefault(x => x.Start <= at && at < x.Start.AddMinutes(30));
        if (slot is null) return 0;
        return scenario switch
        {
            ProjectionScenario.Low => slot.Kw10 ?? slot.Kw * confidence,
            ProjectionScenario.High => slot.Kw90 ?? slot.Kw,
            _ => slot.Kw
        };
    }

    private static double FeedInPrice(ProjectionInput input, double basePrice, double leadMinutes, ProjectionScenario scenario)
    {
        if (scenario == ProjectionScenario.Base || input.Bias is null) return basePrice;

        var bucket = input.Bias.Buckets.FirstOrDefault(b => b.Bucket == ForecastErrorCalculator.BucketFor(leadMinutes));
        if (bucket?.MeanError is not double bias || bucket.MeanAbsError is not double spread) return basePrice;

        // The mean absolute error is never smaller than the absolute mean error, so low <= forecast <= high.
        return scenario == ProjectionScenario.Low ? basePrice + bias - spread : basePrice + bias + spread;
    }

    public static SimulationResult Simulate(ProjectionInput input, ProjectionScenario scenario)
    {
        double capacity = input.CapacityKwh;
        double hardFloor = capacity * SocPlanner.HardFloorPercent / 100;
        double eta = Math.Sqrt(Math.Clamp(input.RoundTripEfficiency, 0.01, 1));
        double energy = Math.Clamp(input.StartSocPercent / 100 * capacity, 0, capacity);

        double exportKwh = 0, exportRevenue = 0, importKwh = 0, importCost = 0, avoidedKwh = 0, avoidedValue = 0, loadTotal = 0, solarTotal = 0;
        var hourImport = new double[24];
        var hourExport = new double[24];
        var hourSolar = new double[24];
        var hourLoad = new double[24];
        var hourSoc = new double[24];
        var hourImportPrice = new double[24];
        var hourFeedInPrice = new double[24];
        var hourSeen = new double[24];
        double spilled = 0, peak = energy / capacity * 100;
        DateTimeOffset? fullAt = null;
        double nowImport = PriceFor(input.ImportPrices, input.Now, input.Now.AddMinutes(1)) ?? 0;
        double nowFeedIn = PriceFor(input.FeedInPrices, input.Now, input.Now.AddMinutes(1)) ?? 0;

        foreach (var (start, hours) in Steps(input.Now, input.End))
        {
            var end = start.AddHours(hours);
            var mid = start.AddHours(hours / 2).DateTime;
            double importPrice = PriceFor(input.ImportPrices, start, end) ?? nowImport;
            double rawFeedIn = PriceFor(input.FeedInPrices, start, end) ?? nowFeedIn;
            double feedIn = FeedInPrice(input, rawFeedIn, (start - input.Now).TotalMinutes, scenario);

            double solar = SolarKw(input.Solar, mid, scenario, input.SolarConfidence) * hours;
            double load = input.Load.KwAt(mid) * hours;
            solarTotal += solar;
            loadTotal += load;

            double direct = Math.Min(solar, load);
            double surplus = solar - direct;
            double shortfall = load - direct;
            double batteryOut = 0;
            double importBefore = importKwh, exportBefore = exportKwh;
            avoidedKwh += direct;
            avoidedValue += direct * importPrice;

            if (shortfall > 0)
            {
                double delivered = Math.Min(shortfall, Math.Max(0, energy - hardFloor) * eta);
                energy -= delivered / eta;
                batteryOut += delivered;
                avoidedKwh += delivered;
                avoidedValue += delivered * importPrice;
                shortfall -= delivered;
                importKwh += shortfall;
                importCost += shortfall * importPrice;
            }

            if (surplus > 0)
            {
                double stored = Math.Min(surplus, Math.Max(0, capacity - energy) / eta);
                energy += stored * eta;
                surplus -= stored;
                spilled += surplus;

                // A zero or negative feed-in price means the solar is curtailed (the PV limit automation) rather than sold at a loss.
                if (surplus > 0 && feedIn > 0)
                {
                    exportKwh += surplus;
                    exportRevenue += surplus * feedIn;
                }
            }

            if (input.GridCharging && importPrice <= input.ImportThreshold)
            {
                double targetEnergy = Math.Min(capacity, input.ImportStopSocPercent / 100 * capacity);
                double fromGrid = Math.Min(input.ImportPowerKw * hours, Math.Max(0, targetEnergy - energy) / eta);
                if (fromGrid > 0)
                {
                    energy += fromGrid * eta;
                    importKwh += fromGrid;
                    importCost += fromGrid * importPrice;
                }
            }

            if (input.ExportThreshold is double threshold && feedIn >= threshold)
            {
                double stopEnergy = Math.Min(capacity, input.FloorKwhAt(mid) + capacity * ExportStopMarginPercent / 100);
                double sellable = Math.Max(0, energy - stopEnergy) * eta;
                double room = Math.Max(0, input.ExportPowerKw * hours - batteryOut);
                double sold = Math.Min(sellable, room);
                if (sold > 0)
                {
                    energy -= sold / eta;
                    exportKwh += sold;
                    exportRevenue += sold * feedIn;
                }
            }

            hourSolar[start.Hour] += solar;
            hourLoad[start.Hour] += load;
            hourSoc[start.Hour] = energy / capacity * 100;
            hourImportPrice[start.Hour] += importPrice * hours;
            hourFeedInPrice[start.Hour] += feedIn * hours;
            hourSeen[start.Hour] += hours;
            peak = Math.Max(peak, energy / capacity * 100);
            if (fullAt is null && energy >= capacity * 0.99) fullAt = start.AddHours(hours);

            hourImport[start.Hour] += importKwh - importBefore;
            hourExport[start.Hour] += exportKwh - exportBefore;
        }

        return new SimulationResult(
            ProjectionTotals.From(exportKwh, exportRevenue, importKwh, importCost, avoidedKwh, avoidedValue),
            Math.Round(energy / capacity * 100, 1),
            Math.Round(loadTotal, 2),
            Math.Round(solarTotal, 2),
            hourImport,
            hourExport)
        {
            PeakSocPercent = Math.Round(peak, 1),
            FullAt = fullAt,
            SpilledSolarKwh = Math.Round(spilled, 2),
            HourSolarKwh = hourSolar,
            HourLoadKwh = hourLoad,
            HourSocPercent = hourSoc,
            HourImportPrice = hourImportPrice.Select((v, h) => hourSeen[h] > 0 ? v / hourSeen[h] : 0).ToArray(),
            HourFeedInPrice = hourFeedInPrice.Select((v, h) => hourSeen[h] > 0 ? v / hourSeen[h] : 0).ToArray()
        };
    }

    private static List<HourlyProjection> Hourly(SimulationResult low, SimulationResult baseCase, SimulationResult high)
    {
        double R(double v) => Math.Round(v, 2);
        var list = new List<HourlyProjection>();
        for (int h = 0; h < 24; h++)
        {
            double[] imports = { low.HourImportKwh[h], baseCase.HourImportKwh[h], high.HourImportKwh[h] };
            double[] exports = { low.HourExportKwh[h], baseCase.HourExportKwh[h], high.HourExportKwh[h] };
            if (imports.Max() <= 0 && exports.Max() <= 0) continue;
            list.Add(new HourlyProjection
            {
                Hour = h,
                ImportKwh = R(imports[1]), ImportLowKwh = R(imports.Min()), ImportHighKwh = R(imports.Max()),
                ExportKwh = R(exports[1]), ExportLowKwh = R(exports.Min()), ExportHighKwh = R(exports.Max())
            });
        }
        return list;
    }

    /// <summary>
    /// Solar, house load, grid flow with its low/high range, end-of-hour SOC and prices per clock hour. With skipEmpty only
    /// the hours the simulation actually covered are returned (today: the rest of the day; hours already past are not projected).
    /// </summary>
    public static List<TomorrowHour> ByHour(SimulationResult low, SimulationResult plan, SimulationResult high, bool skipEmpty)
    {
        double R(double v) => Math.Round(v, 2);
        var hours = new List<TomorrowHour>();
        for (int h = 0; h < 24; h++)
        {
            if (skipEmpty && plan.HourSolarKwh[h] <= 0 && plan.HourLoadKwh[h] <= 0 && plan.HourImportPrice[h] == 0) continue;
            double[] imports = { low.HourImportKwh[h], plan.HourImportKwh[h], high.HourImportKwh[h] };
            double[] exports = { low.HourExportKwh[h], plan.HourExportKwh[h], high.HourExportKwh[h] };
            hours.Add(new TomorrowHour
            {
                Hour = h, SolarKwh = R(plan.HourSolarKwh[h]), LoadKwh = R(plan.HourLoadKwh[h]),
                ImportKwh = R(imports[1]), ImportLowKwh = R(imports.Min()), ImportHighKwh = R(imports.Max()),
                ExportKwh = R(exports[1]), ExportLowKwh = R(exports.Min()), ExportHighKwh = R(exports.Max()),
                SocPercent = Math.Round(plan.HourSocPercent[h], 1), ImportPrice = Math.Round(plan.HourImportPrice[h], 3), FeedInPrice = Math.Round(plan.HourFeedInPrice[h], 3)
            });
        }
        return hours;
    }

    /// <summary>
    /// Tomorrow by hour, starting from the SOC today's plan leaves at midnight, next to the same day if nothing were sold today.
    /// No planned export is assumed for tomorrow, so the battery charge from tomorrow's solar is shown on its own.
    /// </summary>
    private static TomorrowProjection BuildTomorrow(ProjectionInput today, SimulationResult todayBase, ProjectionInput template)
    {
        var unsold = Simulate(today with { ExportThreshold = null }, ProjectionScenario.Base);

        SimulationResult Run(double startSoc, ProjectionScenario scenario) => Simulate(template with { StartSocPercent = startSoc, ExportThreshold = null, GridCharging = false }, scenario);
        var plan = Run(todayBase.EndSocPercent, ProjectionScenario.Base);
        var noSell = Run(unsold.EndSocPercent, ProjectionScenario.Base);
        var low = Run(todayBase.EndSocPercent, ProjectionScenario.Low);
        var high = Run(todayBase.EndSocPercent, ProjectionScenario.High);
        double R(double v) => Math.Round(v, 2);

        var hours = ByHour(low, plan, high, false);

        var withPlan = Case(todayBase.EndSocPercent, plan);
        var withoutSelling = Case(unsold.EndSocPercent, noSell);
        bool covers = template.FeedInPrices.Any(p => p.Time >= template.Now.AddHours(12)) && template.ImportPrices.Any(p => p.Time >= template.Now.AddHours(12));

        return new TomorrowProjection
        {
            Date = template.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            SolarKwh = plan.SolarKwh,
            LoadKwh = plan.LoadKwh,
            PriceForecastCoversTomorrow = covers,
            WithTodaysPlan = withPlan,
            IfNothingSoldToday = withoutSelling,
            Verdict = Verdict(withPlan, withoutSelling, todayBase.Totals.ExportKwh),
            Hourly = hours
        };

        static TomorrowCase Case(double startSoc, SimulationResult r) => new()
        {
            StartSocPercent = startSoc, PeakSocPercent = r.PeakSocPercent, FullAt = r.FullAt, SpilledSolarKwh = r.SpilledSolarKwh, ImportKwh = r.Totals.ImportKwh
        };
    }

    private static string Verdict(TomorrowCase plan, TomorrowCase noSell, double soldTodayKwh)
    {
        string fullBy(TomorrowCase c) => c.FullAt is { } t ? $"full by about {t:HH:mm}" : $"peaking at {c.PeakSocPercent:0}%";
        if (soldTodayKwh < 0.1)
            return $"No battery export is planned today. Tomorrow starts at {plan.StartSocPercent:0}% and is {fullBy(plan)}.";
        if (plan.PeakSocPercent >= 99)
            return $"Selling about {soldTodayKwh:0.#} kWh today leaves {plan.StartSocPercent:0}% at midnight; tomorrow's solar still refills the battery ({fullBy(plan)}), so the sale costs nothing tomorrow. Holding it instead would only spill about {noSell.SpilledSolarKwh - plan.SpilledSolarKwh:0.#} kWh more solar.";
        return $"Selling about {soldTodayKwh:0.#} kWh today leaves {plan.StartSocPercent:0}% at midnight and tomorrow's solar only reaches {plan.PeakSocPercent:0}% (about {noSell.PeakSocPercent:0}% if nothing were sold), with {plan.ImportKwh:0.#} kWh imported against {noSell.ImportKwh:0.#} kWh. Part of what is sold today has to be bought back or missed tomorrow.";
    }

    public static DayProjection Build(ProjectionInput input, RealisedTotals realised, DateTimeOffset? priceForecastUntil, int loadDaysUsed, bool loadFallback, IEnumerable<string>? notes = null, ProjectionInput? tomorrow = null)
    {
        var low = Simulate(input, ProjectionScenario.Low);
        var baseCase = Simulate(input, ProjectionScenario.Base);
        var high = Simulate(input, ProjectionScenario.High);

        var totals = new[] { low, baseCase, high }.Select(r => ProjectionTotals.Sum(realised.Totals, r.Totals)).ToList();
        var dayTotal = ProjectionTotals.Sum(realised.Totals, baseCase.Totals);

        return new DayProjection
        {
            GeneratedAt = input.Now,
            Date = input.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            StartSocPercent = Math.Round(input.StartSocPercent, 1),
            EndSocPercent = baseCase.EndSocPercent,
            Realised = realised,
            Remainder = baseCase.Totals,
            DayTotal = dayTotal,
            Hourly = Hourly(low, baseCase, high),
            TodayByHour = ByHour(low, baseCase, high, true),
            Tomorrow = tomorrow is null ? null : BuildTomorrow(input, baseCase, tomorrow),
            Range = new ProjectionRange
            {
                LowNetCash = totals.Min(t => t.NetCash),
                HighNetCash = totals.Max(t => t.NetCash),
                LowNetInclAvoided = totals.Min(t => t.NetInclAvoided),
                HighNetInclAvoided = totals.Max(t => t.NetInclAvoided)
            },
            Assumptions = new ProjectionAssumptions
            {
                LoadDaysUsed = loadDaysUsed,
                LoadSource = loadFallback ? "fallback: no complete day of history, using the average load setting" : $"average of {loadDaysUsed} day(s) by half-hour",
                LoadKwhRemaining = baseCase.LoadKwh,
                SolarKwhRemaining = baseCase.SolarKwh,
                PriceForecastUntil = priceForecastUntil,
                ExportThreshold = input.ExportThreshold,
                ExportPowerKw = input.ExportPowerKw,
                GridCharging = input.GridCharging,
                RoundTripEfficiency = input.RoundTripEfficiency,
                BiasWindowDays = input.Bias?.WindowDays,
                Notes = notes?.ToList() ?? new()
            }
        };
    }
}
