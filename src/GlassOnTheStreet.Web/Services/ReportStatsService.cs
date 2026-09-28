using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace GlassOnTheStreet.Web.Services;

public class ReportStatsService(GlassOnTheStreetContext db) : IReportStatsService
{
    public async Task<ReportStats> GetStatsAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var rangeTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rangeFrom = from ?? rangeTo.AddDays(-30);
        var rangeDays = rangeTo.DayNumber - rangeFrom.DayNumber + 1;

        var priorTo = rangeFrom.AddDays(-1);
        var priorFrom = priorTo.AddDays(-(rangeDays - 1));

        var activeReports = db.Reports.Where(r => r.Status == ReportStatus.Active);

        var currentCount = await activeReports
            .CountAsync(r => r.ReportedDate >= rangeFrom && r.ReportedDate <= rangeTo, cancellationToken);
        var priorCount = await activeReports
            .CountAsync(r => r.ReportedDate >= priorFrom && r.ReportedDate <= priorTo, cancellationToken);

        double? percentChange = priorCount == 0
            ? null
            : Math.Round((currentCount - priorCount) / (double)priorCount * 100, 1);

        return new ReportStats(currentCount, priorCount, percentChange);
    }

    public async Task<ReportBreakdown> GetBreakdownAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        var query = db.Reports.Where(r => r.Status == ReportStatus.Active);

        if (from is not null)
        {
            query = query.Where(r => r.ReportedDate >= from);
        }

        if (to is not null)
        {
            query = query.Where(r => r.ReportedDate <= to);
        }

        // Projecting straight into the record's positional constructor
        // doesn't translate to SQL -- project to an anonymous type (which
        // does) and map to the record client-side after materializing.
        var topNeighborhoods = (await query
            .Where(r => r.Neighborhood != null)
            .GroupBy(r => r.Neighborhood)
            .Select(g => new { name = g.Key!, count = g.Count() })
            .OrderByDescending(g => g.count)
            .Take(5)
            .ToListAsync(cancellationToken))
            .Select(g => new NeighborhoodCount(g.name, g.count))
            .ToList();

        var timeOfDayCounts = (await query
            .Where(r => r.TimeOfDay != null)
            .GroupBy(r => r.TimeOfDay)
            .Select(g => new { bucket = g.Key!.Value, count = g.Count() })
            .ToListAsync(cancellationToken))
            .Select(g => new TimeOfDayCount(g.bucket.ToString(), g.count))
            .ToList();

        var topWards = (await query
            .Where(r => r.Ward != null)
            .GroupBy(r => r.Ward)
            .Select(g => new { ward = g.Key!.Value, count = g.Count() })
            .OrderByDescending(g => g.count)
            .Take(5)
            .ToListAsync(cancellationToken))
            .Select(g => new WardCount(g.ward, g.count))
            .ToList();

        return new ReportBreakdown(topNeighborhoods, timeOfDayCounts, topWards);
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

    public async Task<IReadOnlyList<MonthlyCount>> GetMonthlyTrendAsync(int months, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rangeStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));

        // Pulled as bare dates and bucketed in memory rather than a SQL
        // GroupBy on Year/Month -- keeps this independent of whether the
        // provider translates DateOnly.Year/.Month, and the row count for a
        // "last N months" window is small enough that this costs nothing.
        var dates = await db.Reports
            .Where(r => r.Status == ReportStatus.Active && r.ReportedDate >= rangeStart)
            .Select(r => r.ReportedDate)
            .ToListAsync(cancellationToken);

        var buckets = new List<(int Year, int Month, int Count)>();
        for (var i = 0; i < months; i++)
        {
            var month = rangeStart.AddMonths(i);
            buckets.Add((month.Year, month.Month, 0));
        }

        foreach (var date in dates)
        {
            var index = ((date.Year - rangeStart.Year) * 12) + date.Month - rangeStart.Month;
            if (index >= 0 && index < buckets.Count)
            {
                var b = buckets[index];
                buckets[index] = (b.Year, b.Month, b.Count + 1);
            }
        }

        return buckets
            .Select(b => new MonthlyCount(new DateOnly(b.Year, b.Month, 1).ToString("MMM yyyy"), b.Count))
            .ToList();
    }

    public async Task<IReadOnlyList<CategoryCount>> GetCategoryCountsAsync(CancellationToken cancellationToken = default)
    {
        var counts = (await db.Reports
            .Where(r => r.Status == ReportStatus.Active)
            .GroupBy(r => r.IncidentType)
            .Select(g => new { type = g.Key, count = g.Count() })
            .ToListAsync(cancellationToken))
            .Select(c => new CategoryCount(c.type.ToString(), c.count))
            .ToList();

        return counts;
    }

    public async Task<IReadOnlyList<YearlyCount>> GetYearlyCountsAsync(
        IncidentType incidentType, int startYear, CancellationToken cancellationToken = default)
    {
        var startDate = new DateOnly(startYear, 1, 1);
        var dates = await db.Reports
            .Where(r => r.Status == ReportStatus.Active
                && r.SourceType == SourceType.OfficialImport
                && r.IncidentType == incidentType
                && r.ReportedDate >= startDate)
            .Select(r => r.ReportedDate)
            .ToListAsync(cancellationToken);

        var currentYear = DateOnly.FromDateTime(DateTime.UtcNow).Year;
        var counts = new List<YearlyCount>();
        for (var year = startYear; year <= currentYear; year++)
        {
            counts.Add(new YearlyCount(year, dates.Count(d => d.Year == year)));
        }

        return counts;
    }
}
