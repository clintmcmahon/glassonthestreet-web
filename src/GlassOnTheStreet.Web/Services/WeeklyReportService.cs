using System.Globalization;

namespace GlassOnTheStreet.Web.Services;

public record WeekSummary(DateOnly Start, string Label, int Total, double? ChangeVsBaseline, double? ChangeVsLastYear);

/// <param name="Baseline">Mean weekly count over the four weeks before this one.</param>
/// <param name="IsSpike">Above the prior eight weeks' mean by more than two standard deviations, with enough volume to mean something.</param>
public record WeeklyGroup(string Key, string Label, int Count, double Baseline, int LastYear, double? ChangeVsBaseline, double? ChangeVsLastYear, bool IsSpike);

public record WeeklyWard(int Ward, int Count, double Baseline, int LastYear, double? ChangeVsBaseline);

public record WeeklyBlock(string Address, string Neighborhood, int Count, string Types);

public record WeeklyBucket(string Label, int Count);

public record WeeklyReport(
    DateOnly Start,
    DateOnly End,
    string Label,
    DateOnly Through,
    bool IsProvisional,
    int Total,
    double Baseline,
    int LastYearTotal,
    IReadOnlyList<WeeklyGroup> Groups,
    IReadOnlyList<WeeklyGroup> Spikes,
    IReadOnlyList<WeeklyWard> Wards,
    IReadOnlyList<WeeklyBlock> RepeatBlocks,
    IReadOnlyList<WeeklyBucket> ByDay,
    IReadOnlyList<WeeklyBucket> ByTimeOfDay,
    IReadOnlyList<string> TrendLabels,
    int[] Trend,
    IReadOnlyList<string> Narrative,
    DateOnly? Previous,
    DateOnly? Next);

/// <summary>
/// Monday-to-Sunday bulletins built from the MPD crime feed, the public counterpart of a weekly
/// crime-analysis briefing. A week is only published once the whole week sits inside the feed's
/// complete window. Every sentence comes from a template and the week's own numbers.
/// </summary>
public class WeeklyReportService(IncidentDataCache dataCache)
{
    public const int BaselineWeeks = 4;
    public const int SpikeWeeks = 8;
    public const int MinSpikeCount = 10;
    public const double SpikeSigmas = 2.0;
    public const int RepeatBlockThreshold = 3;

    private static readonly string[] CarGroups = ["theft-from-vehicle", "vehicle-theft", "parts-theft"];
    private static readonly string[] TimeLabels = ["Overnight (12-6 a.m.)", "Morning (6 a.m.-noon)", "Afternoon (noon-6 p.m.)", "Evening (6 p.m.-midnight)"];

    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static string Label(DateOnly start)
    {
        var end = start.AddDays(6);
        var inv = CultureInfo.InvariantCulture;
        return start.Month == end.Month
            ? $"{start.ToString("MMMM d", inv)}-{end.Day}, {end.Year}"
            : start.Year == end.Year
                ? $"{start.ToString("MMMM d", inv)} to {end.ToString("MMMM d, yyyy", inv)}"
                : $"{start.ToString("MMMM d, yyyy", inv)} to {end.ToString("MMMM d, yyyy", inv)}";
    }

    public async Task<IReadOnlyList<WeekSummary>> ListAsync(int limit = 104, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var isCrime = CrimeGroups.All.Select(g => g.IsCrime).ToArray();
        var totals = new Dictionary<DateOnly, int>();
        foreach (var row in dataset.Rows)
        {
            if (isCrime[row.Group])
            {
                var monday = MondayOf(row.Date);
                totals[monday] = totals.GetValueOrDefault(monday) + row.Count;
            }
        }

        var list = new List<WeekSummary>();
        foreach (var start in CompleteWeeks(dataset).OrderByDescending(d => d).Take(limit))
        {
            var total = totals.GetValueOrDefault(start);
            var baseline = Enumerable.Range(1, BaselineWeeks).Average(i => totals.GetValueOrDefault(start.AddDays(-7 * i)));
            list.Add(new WeekSummary(start, Label(start), total, Pct(total, baseline), Pct(total, totals.GetValueOrDefault(start.AddDays(-364)))));
        }

        return list;
    }

    /// <summary>Null when the date isn't a Monday, the week isn't finished inside the complete window, or it lacks a full year of history to compare against.</summary>
    public async Task<WeeklyReport?> GetAsync(DateOnly start, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var weeks = CompleteWeeks(dataset);
        if (start.DayOfWeek != DayOfWeek.Monday || !weeks.Contains(start))
        {
            return null;
        }

        var end = start.AddDays(6);
        var lastYearStart = start.AddDays(-364);
        var historyFrom = start.AddDays(-7 * SpikeWeeks);
        var isCrime = CrimeGroups.All.Select(g => g.IsCrime).ToArray();
        var groupCount = CrimeGroups.All.Count;

        // Counts per week and group over the lookback; the report week is the last index.
        var weekly = new int[SpikeWeeks + 1, groupCount];
        var lastYearGroup = new int[groupCount];
        var wardNow = new Dictionary<int, int>();
        var wardBase = new Dictionary<int, int>();
        var wardLastYear = new Dictionary<int, int>();
        var byDay = new int[7];
        var byTime = new int[4];
        var blocks = new Dictionary<(int Address, int Group), (int Count, string? Hood)>();
        var trend = new int[13];

        foreach (var row in dataset.Rows)
        {
            if (!isCrime[row.Group] || row.Date > end)
            {
                continue;
            }

            var weeksBack = (start.DayNumber - MondayOf(row.Date).DayNumber) / 7;
            if (weeksBack < 13)
            {
                trend[12 - weeksBack] += row.Count;
            }

            if (row.Date >= lastYearStart && row.Date <= lastYearStart.AddDays(6))
            {
                lastYearGroup[row.Group] += row.Count;
                wardLastYear[row.Ward] = wardLastYear.GetValueOrDefault(row.Ward) + row.Count;
            }

            if (row.Date < historyFrom)
            {
                continue;
            }

            weekly[SpikeWeeks - weeksBack, row.Group] += row.Count;

            if (weeksBack >= 1 && weeksBack <= BaselineWeeks)
            {
                wardBase[row.Ward] = wardBase.GetValueOrDefault(row.Ward) + row.Count;
            }

            if (weeksBack != 0)
            {
                continue;
            }

            wardNow[row.Ward] = wardNow.GetValueOrDefault(row.Ward) + row.Count;
            byDay[((int)row.Date.DayOfWeek + 6) % 7] += row.Count;
            byTime[Math.Min(3, row.Hour / 6)] += row.Count;

            if (row.AddressIndex > 0 && Array.IndexOf(CarGroups, CrimeGroups.All[row.Group].Key) >= 0)
            {
                var key = (row.AddressIndex, (int)row.Group);
                var existing = blocks.GetValueOrDefault(key);
                blocks[key] = (existing.Count + row.Count, row.Neighborhood ?? existing.Hood);
            }
        }

        var groups = new List<WeeklyGroup>();
        for (var g = 0; g < groupCount; g++)
        {
            if (!isCrime[g])
            {
                continue;
            }

            var count = weekly[SpikeWeeks, g];
            var baseline = Enumerable.Range(SpikeWeeks - BaselineWeeks, BaselineWeeks).Average(i => (double)weekly[i, g]);
            var history = Enumerable.Range(0, SpikeWeeks).Select(i => (double)weekly[i, g]).ToArray();
            var mean = history.Average();
            var sd = Math.Sqrt(history.Sum(v => (v - mean) * (v - mean)) / (history.Length - 1));
            var spike = count >= MinSpikeCount && count > mean + SpikeSigmas * sd;
            groups.Add(new WeeklyGroup(CrimeGroups.All[g].Key, CrimeGroups.All[g].Label, count, baseline, lastYearGroup[g],
                Pct(count, baseline), Pct(count, lastYearGroup[g]), spike));
        }

        var total = groups.Sum(g => g.Count);
        var totalBaseline = groups.Sum(g => g.Baseline);
        var lastYearTotal = groups.Sum(g => g.LastYear);
        var spikes = groups.Where(g => g.IsSpike).OrderByDescending(g => g.Count - g.Baseline).ToList();

        var wards = wardNow.Keys.Union(wardBase.Keys).Where(w => w > 0)
            .Select(w =>
            {
                var now = wardNow.GetValueOrDefault(w);
                var baseline = wardBase.GetValueOrDefault(w) / (double)BaselineWeeks;
                return new WeeklyWard(w, now, baseline, wardLastYear.GetValueOrDefault(w), Pct(now, baseline));
            })
            .OrderBy(w => w.Ward)
            .ToList();

        var repeatBlocks = blocks
            .GroupBy(b => b.Key.Address)
            .Where(g => g.Sum(b => b.Value.Count) >= RepeatBlockThreshold)
            .Select(g => new WeeklyBlock(
                dataset.AddressAt(g.Key),
                g.Select(b => b.Value.Hood).FirstOrDefault(h => h is not null) ?? "",
                g.Sum(b => b.Value.Count),
                string.Join(", ", g.OrderByDescending(b => b.Value.Count).Select(b => CrimeGroups.All[b.Key.Group].Label.ToLowerInvariant()))))
            .OrderByDescending(b => b.Count).ThenBy(b => b.Address, StringComparer.Ordinal)
            .Take(8).ToList();

        var trendLabels = Enumerable.Range(0, 13)
            .Select(i => start.AddDays(7 * (i - 12)).ToString("MMM d", CultureInfo.InvariantCulture)).ToList();

        var dayLabels = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        var dayBuckets = dayLabels.Select((l, i) => new WeeklyBucket(l, byDay[i])).ToList();
        var timeBuckets = TimeLabels.Select((l, i) => new WeeklyBucket(l, byTime[i])).ToList();

        var report = new WeeklyReport(
            start, end, Label(start), dataset.Through, dataset.Through < end.AddDays(7), total, totalBaseline, lastYearTotal,
            groups, spikes, wards, repeatBlocks, dayBuckets, timeBuckets, trendLabels, trend, [],
            weeks.Contains(start.AddDays(-7)) ? start.AddDays(-7) : null,
            weeks.Contains(start.AddDays(7)) ? start.AddDays(7) : null);
        return report with { Narrative = BuildNarrative(report) };
    }

    private static List<string> BuildNarrative(WeeklyReport r)
    {
        var inv = CultureInfo.InvariantCulture;
        string N(int v) => v.ToString("N0", inv);
        string Move(double? pct) => pct is null ? "" : Math.Round(pct.Value) == 0 ? "flat" : $"{(pct >= 0 ? "up" : "down")} {Math.Abs(Math.Round(pct.Value)).ToString("0", inv)}%";
        var lines = new List<string>();

        var parts = new List<string>();
        if (Pct(r.Total, r.Baseline) is { } vsBase)
        {
            parts.Add($"{Move(vsBase)} from the {BaselineWeeks}-week average of {N((int)Math.Round(r.Baseline))}");
        }

        if (Pct(r.Total, r.LastYearTotal) is { } vsYear)
        {
            parts.Add($"{Move(vsYear)} from the same week a year earlier ({N(r.LastYearTotal)})");
        }

        lines.Add($"The Minneapolis Police Department recorded {N(r.Total)} offenses for the week of {r.Label}" +
                  (parts.Count > 0 ? ", " + string.Join(" and ", parts) : "") + ".");

        if (r.Spikes.Count > 0)
        {
            lines.Add("Above their recent range (more than two standard deviations over the prior eight weeks): " +
                      string.Join("; ", r.Spikes.Take(3).Select(s => $"{s.Label.ToLowerInvariant()}, {N(s.Count)} against a {BaselineWeeks}-week average of {s.Baseline.ToString("0.#", inv)}")) + ".");
        }
        else
        {
            lines.Add("No offense type ran above its recent range this week.");
        }

        if (r.Total > 0)
        {
            var busiestDay = r.ByDay.MaxBy(b => b.Count)!;
            var busiestTime = r.ByTimeOfDay.MaxBy(b => b.Count)!;
            lines.Add($"{busiestDay.Label} had the most offenses ({N(busiestDay.Count)}). The busiest part of the day was {busiestTime.Label.ToLowerInvariant()} ({N(busiestTime.Count)}).");
        }

        var cars = r.Groups.FirstOrDefault(g => g.Key == "theft-from-vehicle");
        var vehicles = r.Groups.FirstOrDefault(g => g.Key == "vehicle-theft");
        if (cars is not null && vehicles is not null)
        {
            lines.Add($"Theft from motor vehicle, the category that covers car break-ins, was {N(cars.Count)} ({Move(cars.ChangeVsBaseline)} from the {BaselineWeeks}-week average); motor vehicle theft was {N(vehicles.Count)} ({Move(vehicles.ChangeVsBaseline)}).");
        }

        if (r.RepeatBlocks.Count > 0)
        {
            lines.Add($"{r.RepeatBlocks.Count} {(r.RepeatBlocks.Count == 1 ? "block" : "blocks")} had {RepeatBlockThreshold} or more car-related offenses this week, listed below.");
        }

        return lines;
    }

    /// <summary>
    /// Mondays of every week wholly inside the feed's complete window, starting once a full year of
    /// history exists for the same-week-last-year comparison.
    /// </summary>
    private static HashSet<DateOnly> CompleteWeeks(IncidentDataset dataset)
    {
        var weeks = new HashSet<DateOnly>();
        var first = MondayOf(new DateOnly(IncidentDataCache.FirstYear, 1, 1)).AddDays(7 + 364);
        for (var d = first; d.AddDays(6) <= dataset.Through; d = d.AddDays(7))
        {
            weeks.Add(d);
        }

        return weeks;
    }

    private static double? Pct(double now, double then) => then == 0 ? null : Math.Round((now - then) / then * 100, 1);
}
