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

        return new ReportBreakdown(topNeighborhoods, timeOfDayCounts);
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
}
