using GlassOnTheStreet.Web.Data;
using GlassOnTheStreet.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GlassOnTheStreet.Web.Services;

/// <summary>One MPD offense row, trimmed to what the statistics need.</summary>
/// <param name="Group">Index into <see cref="CrimeGroups.All"/>.</param>
public readonly record struct IncidentRow(
    int Id, DateOnly Date, byte Hour, byte Group, short Count, string? Neighborhood, byte Ward, float Lat, float Lng);

public sealed record IncidentDataset(
    IncidentRow[] Rows,
    DateOnly Through,
    IReadOnlyDictionary<string, string> NeighborhoodBySlug,
    IReadOnlyList<string> NeighborhoodNames,
    IReadOnlyList<int> Wards);

/// <summary>
/// The whole MPD crime feed (about 400k small rows) held in memory so every
/// filter, ranking and nearby lookup is computed without going back to SQL.
/// The feed syncs daily, so a half hour of staleness is invisible.
/// </summary>
public class IncidentDataCache(GlassOnTheStreetContext db, IMemoryCache cache)
{
    public const int FirstYear = 2019;

    private const string CacheKey = "incident-dataset";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public virtual async Task<IncidentDataset> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out IncidentDataset? cached) && cached is not null)
        {
            return cached;
        }

        var loaded = await db.MpdIncidents
            .Where(i => i.OccurredDate >= new DateOnly(FirstYear, 1, 1))
            .Select(i => new { i.Id, i.OccurredDate, i.OccurredHour, i.GroupKey, i.CrimeCount, i.Neighborhood, i.Ward, i.Lat, i.Lng })
            .ToListAsync(cancellationToken);

        var rows = new IncidentRow[loaded.Count];
        for (var i = 0; i < loaded.Count; i++)
        {
            var r = loaded[i];
            rows[i] = new IncidentRow(
                r.Id, r.OccurredDate, r.OccurredHour, (byte)CrimeGroups.IndexOf(r.GroupKey), r.CrimeCount,
                r.Neighborhood is null ? null : string.Intern(r.Neighborhood), r.Ward ?? 0,
                r.Lat is null ? float.NaN : (float)r.Lat.Value, r.Lng is null ? float.NaN : (float)r.Lng.Value);
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, CentralTime.Zone));
        var through = rows.Length == 0 ? today : rows.Max(r => r.Date);
        if (through > today)
        {
            through = today;
        }

        var names = rows.Where(r => r.Neighborhood is not null).Select(r => r.Neighborhood!).Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var bySlug = new Dictionary<string, string>();
        foreach (var name in names)
        {
            bySlug.TryAdd(AreaSlug.For(name), name);
        }

        var wards = rows.Where(r => r.Ward > 0).Select(r => (int)r.Ward).Distinct().OrderBy(w => w).ToList();

        var dataset = new IncidentDataset(rows, through, bySlug, names, wards);
        cache.Set(CacheKey, dataset, CacheDuration);
        return dataset;
    }
}
