using System.Globalization;

namespace GlassOnTheStreet.Web.Services;

public record MonthlyGroup(string Key, string Label, int Count, int PriorMonth, int LastYear, double? ChangeVsPriorMonth, double? ChangeVsLastYear);

public record MonthlyMover(string Name, string Url, int Count, int LastYear, int Change, double? Percent);

public record MonthSummary(int Year, int Month, string Label, int Total, double? ChangeVsPriorMonth, double? ChangeVsLastYear);

public record MonthlyReport(
    int Year,
    int Month,
    string Label,
    DateOnly Through,
    bool IsProvisional,
    int Total,
    int PriorMonthTotal,
    int LastYearTotal,
    IReadOnlyList<MonthlyGroup> Groups,
    IReadOnlyList<MonthlyMover> Increases,
    IReadOnlyList<MonthlyMover> Decreases,
    IReadOnlyList<string> TrendLabels,
    int[] Trend,
    IReadOnlyList<string> Narrative,
    (int Year, int Month)? Previous,
    (int Year, int Month)? Next);

/// <summary>
/// Month-by-month reports built from the MPD crime feed. Every sentence is
/// assembled from a template and the month's own numbers, so each claim is
/// checkable against the tables on the same page.
/// </summary>
public class MonthlyReportService(IncidentDataCache dataCache)
{
    private const int MinGroupCountForHeadline = 50;
    private const int MoversShown = 3;
    private const int MinMoverVolume = 20;

    public async Task<IReadOnlyList<MonthSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var totals = MonthlyCrimeTotals(dataset);
        var list = new List<MonthSummary>();
        foreach (var (year, month) in AvailableMonths(dataset))
        {
            var total = totals.GetValueOrDefault((year, month));
            var prior = Previous(year, month);
            var priorTotal = totals.GetValueOrDefault(prior);
            var lastYearTotal = totals.GetValueOrDefault((year - 1, month));
            list.Add(new MonthSummary(year, month, Label(year, month), total,
                Pct(total, priorTotal), Pct(total, lastYearTotal)));
        }

        return list.OrderByDescending(m => m.Year).ThenByDescending(m => m.Month).ToList();
    }

    /// <summary>Null when the month isn't finished in the feed yet, or has no data.</summary>
    public async Task<MonthlyReport?> GetAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var available = AvailableMonths(dataset);
        if (!available.Contains((year, month)))
        {
            return null;
        }

        var through = dataset.Through;
        var monthEnd = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1);
        var prior = Previous(year, month);
        var lastYear = (year - 1, month);
        var isCrime = CrimeGroups.All.Select(g => g.IsCrime).ToArray();

        var groupCounts = new Dictionary<((int, int), int), int>();
        var hoodNow = new Dictionary<string, int>();
        var hoodThen = new Dictionary<string, int>();
        var totals = new Dictionary<(int, int), int>();

        foreach (var row in dataset.Rows)
        {
            var key = (row.Date.Year, row.Date.Month);
            groupCounts[(key, (int)row.Group)] = groupCounts.GetValueOrDefault((key, (int)row.Group)) + row.Count;

            if (!isCrime[row.Group])
            {
                continue;
            }

            totals[key] = totals.GetValueOrDefault(key) + row.Count;
            if (row.Neighborhood is not null)
            {
                if (key == (year, month))
                {
                    hoodNow[row.Neighborhood] = hoodNow.GetValueOrDefault(row.Neighborhood) + row.Count;
                }
                else if (key == lastYear)
                {
                    hoodThen[row.Neighborhood] = hoodThen.GetValueOrDefault(row.Neighborhood) + row.Count;
                }
            }
        }

        var total = totals.GetValueOrDefault((year, month));
        var priorTotal = totals.GetValueOrDefault(prior);
        var lastYearTotal = totals.GetValueOrDefault(lastYear);

        var groups = new List<MonthlyGroup>();
        for (var i = 0; i < CrimeGroups.All.Count; i++)
        {
            if (!CrimeGroups.All[i].IsCrime)
            {
                continue;
            }

            var count = groupCounts.GetValueOrDefault(((year, month), i));
            var priorCount = groupCounts.GetValueOrDefault((prior, i));
            var lastYearCount = groupCounts.GetValueOrDefault((lastYear, i));
            groups.Add(new MonthlyGroup(CrimeGroups.All[i].Key, CrimeGroups.All[i].Label, count, priorCount, lastYearCount,
                Pct(count, priorCount), Pct(count, lastYearCount)));
        }

        var movers = hoodNow.Keys.Union(hoodThen.Keys)
            .Select(name =>
            {
                var now = hoodNow.GetValueOrDefault(name);
                var then = hoodThen.GetValueOrDefault(name);
                return new MonthlyMover(name, $"/neighborhoods/{AreaSlug.For(name)}", now, then, now - then, Pct(now, then));
            })
            .Where(m => Math.Max(m.Count, m.LastYear) >= MinMoverVolume)
            .ToList();
        var increases = movers.Where(m => m.Change > 0).OrderByDescending(m => m.Change).Take(MoversShown).ToList();
        var decreases = movers.Where(m => m.Change < 0).OrderBy(m => m.Change).Take(MoversShown).ToList();

        // Thirteen months ending here, so the same month a year earlier is on the chart.
        var trendMonths = Enumerable.Range(0, 13).Select(i => new DateOnly(year, month, 1).AddMonths(i - 12)).ToList();
        var trend = trendMonths.Select(d => totals.GetValueOrDefault((d.Year, d.Month))).ToArray();
        // Year on the first bar and on every January, so the two Augusts aren't ambiguous.
        var trendLabels = trendMonths.Select((d, i) => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(d.Month) + (d.Month == 1 || i == 0 ? $" {d.Year}" : "")).ToList();

        var label = Label(year, month);
        var narrative = BuildNarrative(label, year, month, total, priorTotal, lastYearTotal, prior, groups, increases, decreases);

        var all = AvailableMonths(dataset);
        var previous = all.Contains(prior) ? prior : ((int Year, int Month)?)null;
        var nextMonth = new DateOnly(year, month, 1).AddMonths(1);
        var next = all.Contains((nextMonth.Year, nextMonth.Month)) ? (nextMonth.Year, nextMonth.Month) : ((int Year, int Month)?)null;

        return new MonthlyReport(
            year, month, label, through, through < monthEnd.AddDays(7), total, priorTotal, lastYearTotal,
            groups, increases, decreases, trendLabels, trend, narrative, previous, next);
    }

    private static List<string> BuildNarrative(
        string label, int year, int month, int total, int priorTotal, int lastYearTotal, (int Year, int Month) prior,
        List<MonthlyGroup> groups, List<MonthlyMover> increases, List<MonthlyMover> decreases)
    {
        string Move(double? pct) => pct is null ? "" : $"{(pct >= 0 ? "up" : "down")} {Math.Abs(Math.Round(pct.Value)).ToString("0", CultureInfo.InvariantCulture)}%";
        var lines = new List<string>();

        var vsPrior = Pct(total, priorTotal);
        var vsYear = Pct(total, lastYearTotal);
        var headline = $"The Minneapolis Police Department recorded {total.ToString("N0", CultureInfo.InvariantCulture)} offenses in {label}";
        var parts = new List<string>();
        if (vsPrior is not null)
        {
            parts.Add($"{Move(vsPrior)} from {Label(prior.Year, prior.Month)} ({priorTotal.ToString("N0", CultureInfo.InvariantCulture)})");
        }

        if (vsYear is not null)
        {
            parts.Add($"{Move(vsYear)} from {Label(year - 1, month)} ({lastYearTotal.ToString("N0", CultureInfo.InvariantCulture)})");
        }

        lines.Add(headline + (parts.Count > 0 ? ", " + string.Join(" and ", parts) : "") + ".");

        var comparable = groups.Where(g => Math.Max(g.Count, g.LastYear) >= MinGroupCountForHeadline).ToList();
        var rise = comparable.Where(g => g.Count > g.LastYear).OrderByDescending(g => g.Count - g.LastYear).FirstOrDefault();
        var fall = comparable.Where(g => g.Count < g.LastYear).OrderBy(g => g.Count - g.LastYear).FirstOrDefault();
        if (rise is not null || fall is not null)
        {
            var bits = new List<string>();
            if (rise is not null)
            {
                bits.Add($"the largest increase from a year earlier was {rise.Label.ToLowerInvariant()} ({rise.Count.ToString("N0", CultureInfo.InvariantCulture)} against {rise.LastYear.ToString("N0", CultureInfo.InvariantCulture)}, {Move(rise.ChangeVsLastYear)})");
            }

            if (fall is not null)
            {
                bits.Add($"the largest decrease was {fall.Label.ToLowerInvariant()} ({fall.Count.ToString("N0", CultureInfo.InvariantCulture)} against {fall.LastYear.ToString("N0", CultureInfo.InvariantCulture)}, {Move(fall.ChangeVsLastYear)})");
            }

            var sentence = string.Join(", and ", bits);
            lines.Add(char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".");
        }

        var cars = groups.FirstOrDefault(g => g.Key == "theft-from-vehicle");
        var vehicles = groups.FirstOrDefault(g => g.Key == "vehicle-theft");
        if (cars is not null && vehicles is not null)
        {
            lines.Add(
                $"Theft from motor vehicle, the category that covers car break-ins, was {cars.Count.ToString("N0", CultureInfo.InvariantCulture)} " +
                $"({Move(cars.ChangeVsLastYear)} from a year earlier); motor vehicle theft was {vehicles.Count.ToString("N0", CultureInfo.InvariantCulture)} ({Move(vehicles.ChangeVsLastYear)}).");
        }

        var homicides = groups.FirstOrDefault(g => g.Key == "homicide");
        if (homicides is not null)
        {
            lines.Add($"MPD recorded {homicides.Count} homicide {(homicides.Count == 1 ? "offense" : "offenses")} in {label}, against {homicides.LastYear} in {Label(year - 1, month)}.");
        }

        if (increases.Count > 0 || decreases.Count > 0)
        {
            var bits = new List<string>();
            if (increases.Count > 0)
            {
                bits.Add("up the most: " + string.Join(", ", increases.Select(m => $"{m.Name} ({m.LastYear} to {m.Count})")));
            }

            if (decreases.Count > 0)
            {
                bits.Add("down the most: " + string.Join(", ", decreases.Select(m => $"{m.Name} ({m.LastYear} to {m.Count})")));
            }

            lines.Add($"By neighborhood, compared with {Label(year - 1, month)}, offenses were " + string.Join("; ", bits) + ".");
        }

        return lines;
    }

    private static Dictionary<(int, int), int> MonthlyCrimeTotals(IncidentDataset dataset)
    {
        var isCrime = CrimeGroups.All.Select(g => g.IsCrime).ToArray();
        var totals = new Dictionary<(int, int), int>();
        foreach (var row in dataset.Rows)
        {
            if (isCrime[row.Group])
            {
                var key = (row.Date.Year, row.Date.Month);
                totals[key] = totals.GetValueOrDefault(key) + row.Count;
            }
        }

        return totals;
    }

    /// <summary>Months that have ended within the data, from January of the first year.</summary>
    private static HashSet<(int Year, int Month)> AvailableMonths(IncidentDataset dataset)
    {
        var months = new HashSet<(int, int)>();
        for (var d = new DateOnly(IncidentDataCache.FirstYear, 1, 1); d.AddMonths(1).AddDays(-1) <= dataset.Through; d = d.AddMonths(1))
        {
            months.Add((d.Year, d.Month));
        }

        return months;
    }

    private static (int Year, int Month) Previous(int year, int month) =>
        month == 1 ? (year - 1, 12) : (year, month - 1);

    private static double? Pct(int now, int then) => then == 0 ? null : Math.Round((now - then) / (double)then * 100, 1);

    public static string Label(int year, int month) =>
        new DateOnly(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
}
