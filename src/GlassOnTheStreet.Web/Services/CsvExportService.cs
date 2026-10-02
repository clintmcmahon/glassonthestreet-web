using System.Globalization;
using System.Text;

namespace GlassOnTheStreet.Web.Services;

/// <summary>Aggregated downloads of the MPD crime data; incident-level rows stay with the city's own feed.</summary>
public class CsvExportService(IncidentDataCache dataCache, IPopulationService population)
{
    public static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string Line(params object?[] cells) =>
        string.Join(",", cells.Select(c => Escape(Convert.ToString(c, CultureInfo.InvariantCulture) ?? ""))) + "\n";

    /// <summary>Citywide offenses by month and group.</summary>
    public async Task<byte[]> MonthlyAsync(CancellationToken cancellationToken)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var counts = new Dictionary<(int Year, int Month, int Group), int>();
        foreach (var row in dataset.Rows)
        {
            var key = (row.Date.Year, row.Date.Month, (int)row.Group);
            counts[key] = counts.GetValueOrDefault(key) + row.Count;
        }

        var sb = new StringBuilder(Line("year", "month", "group_key", "group", "is_crime", "offenses", "month_complete"));
        foreach (var ((year, month, group), count) in counts.OrderBy(k => k.Key.Year).ThenBy(k => k.Key.Month).ThenBy(k => k.Key.Group))
        {
            var g = CrimeGroups.All[group];
            var complete = new DateOnly(year, month, 1).AddMonths(1).AddDays(-1) <= dataset.Through;
            sb.Append(Line(year, month, g.Key, g.Label, g.IsCrime ? "true" : "false", count, complete ? "true" : "false"));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Offenses by calendar year, area and group, for neighborhoods or wards.</summary>
    public async Task<byte[]> ByAreaAsync(bool neighborhoods, CancellationToken cancellationToken)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var counts = new Dictionary<(string Area, int Year, int Group), int>();
        foreach (var row in dataset.Rows)
        {
            var area = neighborhoods ? row.Neighborhood : row.Ward > 0 ? row.Ward.ToString(CultureInfo.InvariantCulture) : null;
            if (area is null)
            {
                continue;
            }

            var key = (area, row.Date.Year, (int)row.Group);
            counts[key] = counts.GetValueOrDefault(key) + row.Count;
        }

        var sb = new StringBuilder(Line(neighborhoods ? "neighborhood" : "ward", "year", "group_key", "group", "is_crime", "offenses", "year_complete", "residents_2020"));
        foreach (var ((area, year, group), count) in counts
                     .OrderBy(k => neighborhoods ? 0 : int.Parse(k.Key.Area, CultureInfo.InvariantCulture))
                     .ThenBy(k => k.Key.Area, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Key.Year).ThenBy(k => k.Key.Group))
        {
            var g = CrimeGroups.All[group];
            var residents = neighborhoods ? population.ForNeighborhood(area) : population.ForWard(int.Parse(area, CultureInfo.InvariantCulture));
            sb.Append(Line(area, year, g.Key, g.Label, g.IsCrime ? "true" : "false", count, year < dataset.Through.Year ? "true" : "false", residents));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public async Task<byte[]> PopulationAsync(CancellationToken cancellationToken)
    {
        var dataset = await dataCache.GetAsync(cancellationToken);
        var sb = new StringBuilder(Line("area_type", "area", "residents_2020"));
        sb.Append(Line("city", "Minneapolis", population.CityTotal));
        foreach (var ward in dataset.Wards)
        {
            sb.Append(Line("ward", ward, population.ForWard(ward)));
        }

        foreach (var name in dataset.NeighborhoodNames)
        {
            sb.Append(Line("neighborhood", name, population.ForNeighborhood(name)));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
