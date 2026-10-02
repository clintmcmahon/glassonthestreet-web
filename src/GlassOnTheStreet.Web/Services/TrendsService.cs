using System.Text;
using GlassOnTheStreet.Web.Infrastructure;
using GlassOnTheStreet.Web.Models;
using Microsoft.Extensions.Caching.Memory;

namespace GlassOnTheStreet.Web.Services;

public class TrendsService(IncidentDataCache incidents, IMemoryCache cache) : ITrendsService
{
    // MPD's current records feed starts here; earlier dates are stray rows.
    public const int FirstYear = 2019;

    private const int TopAreaCount = 10;
    private const int RelatedAreaCount = 6;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private const string CacheKey = "trends-dataset";

    private readonly record struct Row(DateOnly Date, IncidentType Type, string? Neighborhood, int? Ward, TimeOfDay? TimeOfDay, int Count);

    private sealed record Dataset(Row[] Rows, IReadOnlyDictionary<string, string> NeighborhoodBySlug, DateOnly Through);

    public async Task<TrendsData> GetTrendsAsync(TrendFilter filter, CancellationToken cancellationToken = default)
    {
        var dataset = await LoadAsync(cancellationToken);
        return Compute(dataset.Rows, filter, dataset.Through);
    }

    public async Task<AreaPageData?> GetNeighborhoodAsync(string slug, CancellationToken cancellationToken = default)
    {
        var dataset = await LoadAsync(cancellationToken);
        if (!dataset.NeighborhoodBySlug.TryGetValue(slug.ToLowerInvariant(), out var name))
        {
            return null;
        }

        var all = dataset.Rows;
        var areaRows = all.Where(r => string.Equals(r.Neighborhood, name, StringComparison.Ordinal)).ToArray();
        var trends = Compute(all, new TrendFilter(null, name, null), dataset.Through);

        // The ward most of this neighborhood's reports fall in (boundaries
        // don't line up exactly, so a neighborhood can touch more than one).
        var ward = areaRows.Where(r => r.Ward is not null).GroupBy(r => r.Ward!.Value)
            .OrderByDescending(g => g.Sum(r => r.Count)).Select(g => (int?)g.Key).FirstOrDefault();

        var rankings = RankAreas(all.Where(r => r.Neighborhood is not null), r => r.Neighborhood!, NeighborhoodUrl, trends.Years, DatePredicate(trends.Through), null);
        var rank = rankings.FindIndex(a => a.Name == name) + 1;

        var sameWard = ward is null
            ? new List<AreaTrend>()
            : RankAreas(all.Where(r => r.Ward == ward && r.Neighborhood is not null), r => r.Neighborhood!, NeighborhoodUrl, trends.Years, DatePredicate(trends.Through), null);
        var related = (sameWard.Count > 1 ? sameWard : rankings)
            .Where(a => a.Name != name)
            .Take(RelatedAreaCount)
            .Select(a => new RelatedArea(a.Name, a.Url, a.Counts[^1]))
            .ToList();

        return BuildPage("neighborhood", name, AreaSlug.For(name), NeighborhoodUrl(name), areaRows, trends, rank, rankings.Count, ward, related);
    }

    public async Task<AreaPageData?> GetWardAsync(int ward, CancellationToken cancellationToken = default)
    {
        var dataset = await LoadAsync(cancellationToken);
        var all = dataset.Rows;
        var areaRows = all.Where(r => r.Ward == ward).ToArray();
        if (areaRows.Length == 0)
        {
            return null;
        }

        var trends = Compute(all, new TrendFilter(null, null, ward), dataset.Through);
        var rankings = RankAreas(all.Where(r => r.Ward is not null), r => $"Ward {r.Ward}", r => WardUrl(int.Parse(r.Split(' ')[1])), trends.Years, DatePredicate(trends.Through), null);
        var rank = rankings.FindIndex(a => a.Name == $"Ward {ward}") + 1;

        var related = RankAreas(areaRows.Where(r => r.Neighborhood is not null), r => r.Neighborhood!, NeighborhoodUrl, trends.Years, DatePredicate(trends.Through), RelatedAreaCount + 4)
            .Select(a => new RelatedArea(a.Name, a.Url, a.Counts[^1]))
            .ToList();

        return BuildPage("ward", $"Ward {ward}", $"ward-{ward}", WardUrl(ward), areaRows, trends, rank, rankings.Count, ward, related);
    }

    public async Task<AreaIndexData> GetAreaIndexAsync(CancellationToken cancellationToken = default)
    {
        var dataset = await LoadAsync(cancellationToken);
        var all = dataset.Rows;
        var trends = Compute(all, new TrendFilter(null, null, null), dataset.Through);
        var matches = DatePredicate(trends.Through);

        var neighborhoods = RankAreas(all.Where(r => r.Neighborhood is not null), r => r.Neighborhood!, NeighborhoodUrl, trends.Years, matches, null);
        var wards = RankAreas(all.Where(r => r.Ward is not null), r => $"Ward {r.Ward}", r => WardUrl(int.Parse(r.Split(' ')[1])), trends.Years, matches, null)
            .OrderBy(w => int.Parse(w.Name.Split(' ')[1]))
            .ToList();
        return new AreaIndexData(trends.Years, trends.Through, neighborhoods, wards);
    }

    private static AreaPageData BuildPage(
        string kind, string name, string slug, string url, Row[] areaRows, TrendsData trends,
        int rank, int rankedOf, int? ward, List<RelatedArea> related)
    {
        var matches = DatePredicate(trends.Through);
        var years = trends.Years;

        var categories = new[] { IncidentType.Unknown, IncidentType.VehicleStolen, IncidentType.PartsTheft, IncidentType.PropertyDamage }
            .Select(type =>
            {
                var counts = new int[years.Length];
                foreach (var row in areaRows.Where(r => r.Type == type && matches(r.Date)))
                {
                    var i = row.Date.Year - FirstYear;
                    if (i >= 0 && i < counts.Length)
                    {
                        counts[i] += row.Count;
                    }
                }

                return new CategoryTrend(CategoryLabel(type), counts);
            })
            .ToList();

        var currentYear = trends.CurrentYear;
        var timeOfDay = new[] { TimeOfDay.Overnight, TimeOfDay.Morning, TimeOfDay.Afternoon, TimeOfDay.Evening, TimeOfDay.NotSure }
            .Select(bucket => new BucketCount(
                bucket == TimeOfDay.NotSure ? "Not recorded" : bucket.ToString(),
                areaRows.Where(r => r.Date.Year == currentYear && matches(r.Date) && (r.TimeOfDay ?? TimeOfDay.NotSure) == bucket).Sum(r => r.Count)))
            .ToList();

        return new AreaPageData(kind, name, slug, url, trends, categories, timeOfDay, rank, rankedOf, areaRows.Sum(r => r.Count), ward, related);
    }

    public static string CategoryLabel(IncidentType type) => type switch
    {
        IncidentType.Unknown => "Theft from motor vehicle",
        IncidentType.VehicleStolen => "Motor vehicle theft",
        IncidentType.PartsTheft => "Vehicle parts theft",
        IncidentType.PropertyDamage => "Property damage / vandalism",
        _ => type.ToString()
    };

    private static string NeighborhoodUrl(string name) => $"/neighborhoods/{AreaSlug.For(name)}";

    private static string WardUrl(int ward) => $"/wards/{ward}";

    private static readonly string[] CarGroups = ["theft-from-vehicle", "vehicle-theft", "parts-theft", "vandalism"];

    private static IncidentType TypeOf(string groupKey) => groupKey switch
    {
        "theft-from-vehicle" => IncidentType.Unknown,
        "vehicle-theft" => IncidentType.VehicleStolen,
        "parts-theft" => IncidentType.PartsTheft,
        _ => IncidentType.PropertyDamage
    };

    // Same buckets the map's importer has always used.
    private static TimeOfDay BucketOf(byte hour) => hour switch
    {
        < 6 => TimeOfDay.Overnight,
        < 12 => TimeOfDay.Morning,
        < 17 => TimeOfDay.Afternoon,
        < 22 => TimeOfDay.Evening,
        _ => TimeOfDay.Overnight
    };

    // The car categories are four of the full MPD feed's offense groups, so this is the
    // same data (and the same offense counts) as the all-offense pages: the two can't
    // disagree. Filtered once and held for half an hour; the feed syncs daily.
    private async Task<Dataset> LoadAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out Dataset? cached) && cached is not null)
        {
            return cached;
        }

        var all = await incidents.GetAsync(cancellationToken);
        var carIndexes = CarGroups.Select(CrimeGroups.IndexOf).ToHashSet();
        var typeByIndex = CarGroups.ToDictionary(CrimeGroups.IndexOf, TypeOf);

        var rows = all.Rows
            .Where(r => carIndexes.Contains(r.Group))
            .Select(r => new Row(r.Date, typeByIndex[r.Group], r.Neighborhood, r.Ward > 0 ? r.Ward : null, BucketOf(r.Hour), r.Count))
            .ToArray();

        var dataset = new Dataset(rows, all.NeighborhoodBySlug, all.Through);
        cache.Set(CacheKey, dataset, CacheDuration);
        return dataset;
    }

    private static Func<DateOnly, bool> DatePredicate(DateOnly through) =>
        d => d.Month < through.Month || (d.Month == through.Month && d.Day <= through.Day);

    private static TrendsData Compute(Row[] all, TrendFilter filter, DateOnly through)
    {
        // "Through" is the last date the feed actually has, not today: MPD
        // posts with a lag, and comparing a full prior period against a
        // current one that's missing its last few days would understate it.
        var currentYear = through.Year;
        var years = Enumerable.Range(FirstYear, currentYear - FirstYear + 1).ToArray();
        var samePeriodOf = DatePredicate(through);

        var categoryRows = filter.Category is { } category ? all.Where(r => r.Type == category).ToArray() : all;
        var scoped = categoryRows.AsEnumerable();
        if (filter.Neighborhood is not null)
        {
            scoped = scoped.Where(r => string.Equals(r.Neighborhood, filter.Neighborhood, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.Ward is not null)
        {
            scoped = scoped.Where(r => r.Ward == filter.Ward);
        }

        var scopedRows = scoped.ToArray();

        var monthly = years.Select(_ => new int?[12]).ToArray();
        for (var i = 0; i < years.Length; i++)
        {
            var lastMonth = years[i] == currentYear ? through.Month : 12;
            for (var m = 0; m < lastMonth; m++)
            {
                monthly[i][m] = 0;
            }
        }

        var samePeriod = new int[years.Length];
        foreach (var row in scopedRows)
        {
            var yearIndex = row.Date.Year - FirstYear;
            if (yearIndex < 0 || yearIndex >= years.Length)
            {
                continue;
            }

            if (monthly[yearIndex][row.Date.Month - 1] is { } existing)
            {
                monthly[yearIndex][row.Date.Month - 1] = existing + row.Count;
            }

            if (samePeriodOf(row.Date))
            {
                samePeriod[yearIndex] += row.Count;
            }
        }

        // The area rankings answer "where", so they ignore the area filter
        // and respect only the category.
        var neighborhoods = RankAreas(categoryRows.Where(r => r.Neighborhood is not null), r => r.Neighborhood!, NeighborhoodUrl, years, samePeriodOf, TopAreaCount);
        var wards = RankAreas(categoryRows.Where(r => r.Ward is not null), r => $"Ward {r.Ward}", r => WardUrl(int.Parse(r.Split(' ')[1])), years, samePeriodOf, TopAreaCount);

        var neighborhoodOptions = all.Where(r => r.Neighborhood is not null)
            .Select(r => r.Neighborhood!).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var wardOptions = all.Where(r => r.Ward is not null).Select(r => r.Ward!.Value).Distinct().OrderBy(w => w).ToList();

        return new TrendsData(years, currentYear, through, monthly, samePeriod, neighborhoods, wards, neighborhoodOptions, wardOptions);
    }

    private static List<AreaTrend> RankAreas(
        IEnumerable<Row> rows, Func<Row, string> areaOf, Func<string, string> urlOf, int[] years,
        Func<DateOnly, bool> samePeriod, int? take)
    {
        var currentIndex = years.Length - 1;
        var ranked = rows
            .Where(r => samePeriod(r.Date))
            .GroupBy(areaOf)
            .Select(g =>
            {
                var counts = new int[years.Length];
                foreach (var row in g)
                {
                    var yearIndex = row.Date.Year - FirstYear;
                    if (yearIndex >= 0 && yearIndex < counts.Length)
                    {
                        counts[yearIndex] += row.Count;
                    }
                }

                var prior = currentIndex > 0 ? counts[currentIndex - 1] : 0;
                double? change = prior == 0 ? null : Math.Round((counts[currentIndex] - prior) / (double)prior * 100, 1);
                return new AreaTrend(g.Key, counts, change, urlOf(g.Key));
            })
            .OrderByDescending(a => a.Counts[currentIndex])
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

        return (take is { } n ? ranked.Take(n) : ranked).ToList();
    }

    /// <summary>Maps the dashboard's category query value to an IncidentType; null (all) for anything unknown.</summary>
    public static IncidentType? ParseCategory(string? value) => value?.ToLowerInvariant() switch
    {
        "theft-from-vehicle" => IncidentType.Unknown,
        "parts-theft" => IncidentType.PartsTheft,
        "vehicle-theft" => IncidentType.VehicleStolen,
        "property-damage" => IncidentType.PropertyDamage,
        _ => null
    };
}

/// <summary>URL slugs for neighborhood names: "Marcy Holmes" becomes "marcy-holmes".</summary>
public static class AreaSlug
{
    public static string For(string name)
    {
        var sb = new StringBuilder();
        var lastDash = true;
        foreach (var ch in name.ToLowerInvariant().Replace("&", " and "))
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }
}
