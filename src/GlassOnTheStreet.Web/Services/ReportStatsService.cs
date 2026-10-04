using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

/// <summary>
/// Headline figures. Every count here is MPD data (the car-related offense groups, from the same
/// dataset as the crime pages). Resident reports are unverified and can describe an incident MPD
/// also has (a resident who also called the police), and the two can't be matched, since MPD
/// records carry no shared ID and sit at block midpoints. So they are never added together:
/// residents' reports are only ever counted by the *Resident* methods, as a separate figure.
/// </summary>
public class ReportStatsService(GlassOnTheStreetContext db, IncidentDataCache incidents, MapDataService mapData) : IReportStatsService
{
    // Last 30 days of data (counted back from the last date MPD has published) unless a range is given.
    private async Task<MapSummary> CarSummaryAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var dataset = await incidents.GetAsync(cancellationToken);
        var end = to ?? dataset.Through;
        var start = from ?? end.AddDays(-29);
        var (clampedStart, clampedEnd) = MapDataService.ResolveRange(null, start, end, dataset.Through);
        return await mapData.GetSummaryAsync(new MapFilter(MapDataService.CarGroup, clampedStart, clampedEnd), cancellationToken);
    }

    public async Task<ReportStats> GetStatsAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var summary = await CarSummaryAsync(from, to, cancellationToken);
        return new ReportStats(summary.Total, summary.PriorTotal, summary.ChangeVsPrior);
    }

    public async Task<ReportBreakdown> GetBreakdownAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var summary = await CarSummaryAsync(from, to, cancellationToken);
        return new ReportBreakdown(
            summary.Neighborhoods.Take(5).Select(n => new NeighborhoodCount(n.Name, n.Count)).ToList(),
            summary.TimeOfDay.Select(t => new TimeOfDayCount(t.Name, t.Count)).ToList(),
            summary.Wards.Take(5).Select(w => new WardCount(int.Parse(w.Name.Replace("Ward ", "")), w.Count)).ToList());
    }

    public async Task<int> GetResidentReportCountAsync(DateOnly from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var query = db.Reports.Where(r => r.Status == ReportStatus.Active && r.SourceType == SourceType.UserReport && r.ReportedDate >= from);
        if (to is { } end)
        {
            query = query.Where(r => r.ReportedDate <= end);
        }

        return await query.CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CategoryCount>> GetResidentCategoryCountsAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var query = db.Reports.Where(r => r.Status == ReportStatus.Active && r.SourceType == SourceType.UserReport);
        if (from is { } start)
        {
            query = query.Where(r => r.ReportedDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(r => r.ReportedDate <= end);
        }

        return (await query
                .GroupBy(r => r.IncidentType)
                .Select(g => new { type = g.Key, count = g.Count() })
                .ToListAsync(cancellationToken))
            .Select(c => new CategoryCount(c.type.ToString(), c.count))
            .ToList();
    }

    public async Task<PoliceReportingGap> GetPoliceReportingGapAsync(CancellationToken cancellationToken = default)
    {
        var answered = db.Reports.Where(r =>
            r.Status == ReportStatus.Active &&
            r.SourceType == SourceType.UserReport &&
            r.PoliceReported != null);

        var respondedCount = await answered.CountAsync(cancellationToken);
        if (respondedCount == 0)
        {
            return new PoliceReportingGap(0, null);
        }

        var notReportedCount = await answered.CountAsync(r => r.PoliceReported == false, cancellationToken);
        var percentUnreported = Math.Round(notReportedCount / (double)respondedCount * 100, 1);

        return new PoliceReportingGap(respondedCount, percentUnreported);
    }

    private static readonly string[] CarGroupKeys = ["theft-from-vehicle", "vehicle-theft", "parts-theft", "vandalism"];

    public async Task<IReadOnlyList<MonthlyCount>> GetMonthlyTrendAsync(int months, CancellationToken cancellationToken = default)
    {
        // Complete months only, ending with the last month the data has finished. A month that
        // started yesterday would plot as a near-zero bar and read as a collapse.
        var dataset = await incidents.GetAsync(cancellationToken);
        var through = dataset.Through;
        var lastMonth = through.AddDays(1).Day == 1
            ? new DateOnly(through.Year, through.Month, 1)
            : new DateOnly(through.Year, through.Month, 1).AddMonths(-1);
        var firstMonth = lastMonth.AddMonths(-(months - 1));
        var endExclusive = lastMonth.AddMonths(1);

        var carGroups = CarGroupKeys.Select(CrimeGroups.IndexOf).ToHashSet();
        var totals = new int[months];
        foreach (var row in dataset.Rows)
        {
            if (row.Date < firstMonth || row.Date >= endExclusive || !carGroups.Contains(row.Group))
            {
                continue;
            }

            totals[((row.Date.Year - firstMonth.Year) * 12) + row.Date.Month - firstMonth.Month] += row.Count;
        }

        return Enumerable.Range(0, months)
            .Select(i => new MonthlyCount(firstMonth.AddMonths(i).ToString("MMM yyyy"), totals[i]))
            .ToList();
    }

    // The four car-related MPD groups under the names the homepage list has always used.
    public async Task<IReadOnlyList<CategoryCount>> GetCategoryCountsAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var dataset = await incidents.GetAsync(cancellationToken);
        var start = from ?? new DateOnly(IncidentDataCache.FirstYear, 1, 1);
        var end = to ?? dataset.Through;

        var typeByGroup = new Dictionary<int, string>
        {
            [CrimeGroups.IndexOf("theft-from-vehicle")] = nameof(IncidentType.Unknown),
            [CrimeGroups.IndexOf("vehicle-theft")] = nameof(IncidentType.VehicleStolen),
            [CrimeGroups.IndexOf("parts-theft")] = nameof(IncidentType.PartsTheft),
            [CrimeGroups.IndexOf("vandalism")] = nameof(IncidentType.PropertyDamage)
        };
        var counts = typeByGroup.Keys.ToDictionary(k => k, _ => 0);

        foreach (var row in dataset.Rows)
        {
            if (row.Date >= start && row.Date <= end && counts.ContainsKey(row.Group))
            {
                counts[row.Group] += row.Count;
            }
        }

        return counts.Where(c => c.Value > 0).Select(c => new CategoryCount(typeByGroup[c.Key], c.Value)).ToList();
    }

    public async Task<IReadOnlyList<YearlyCount>> GetYearlyCountsAsync(
        IncidentType incidentType, int startYear, CancellationToken cancellationToken = default)
    {
        // The car categories come from the same full MPD feed as the crime pages (offense
        // counts), so the homepage charts agree with them.
        var groupKey = incidentType switch
        {
            IncidentType.Unknown => "theft-from-vehicle",
            IncidentType.VehicleStolen => "vehicle-theft",
            IncidentType.PartsTheft => "parts-theft",
            IncidentType.PropertyDamage => "vandalism",
            _ => null
        };
        var dataset = await incidents.GetAsync(cancellationToken);
        var groupIndex = groupKey is null ? -1 : CrimeGroups.IndexOf(groupKey);
        var startDate = new DateOnly(startYear, 1, 1);
        var dates = dataset.Rows
            .Where(r => r.Group == groupIndex && r.Date >= startDate)
            .Select(r => (r.Date, r.Count))
            .ToList();

        // Measured through the feed's last date rather than today's: MPD
        // posts with a lag, and a current year missing its last few days
        // would understate the share and bias the projection low.
        var today = dataset.Through;

        var currentYear = today.Year;
        var counts = new List<YearlyCount>();
        var priorYtdTotal = 0;
        var priorFullTotal = 0;
        for (var year = startYear; year <= currentYear; year++)
        {
            var yearCount = dates.Where(d => d.Date.Year == year).Sum(d => d.Count);
            if (year < currentYear)
            {
                // Same calendar cutoff in each earlier year (Feb 29 falls
                // back to Feb 28), to measure how much of a typical year's
                // total has already happened by today's date.
                var cutoff = new DateOnly(year, today.Month, Math.Min(today.Day, DateTime.DaysInMonth(year, today.Month)));
                priorYtdTotal += dates.Where(d => d.Date.Year == year && d.Date <= cutoff).Sum(d => d.Count);
                priorFullTotal += yearCount;
                counts.Add(new YearlyCount(year, yearCount));
            }
            else
            {
                counts.Add(new YearlyCount(
                    year, yearCount,
                    ProjectYearEnd(yearCount, priorYtdTotal, priorFullTotal),
                    today));
            }
        }

        return counts;
    }

    /// <summary>
    /// Projects a full-year total from a year-to-date count, using the share
    /// of the year that was already done by this date in earlier years
    /// (pooled across those years, so one odd year doesn't dominate).
    /// Null when there's no prior data to base a share on.
    /// </summary>
    public static int? ProjectYearEnd(int yearToDate, int priorYearsToDate, int priorYearsFullTotal)
    {
        if (priorYearsFullTotal <= 0 || priorYearsToDate <= 0)
        {
            return null;
        }

        var share = priorYearsToDate / (double)priorYearsFullTotal;
        return (int)Math.Round(yearToDate / share);
    }
}
